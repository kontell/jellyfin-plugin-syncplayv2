#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// Class SyncPlayManager.
    /// </summary>
    public class SyncPlayManagerV2 : ISyncPlayManagerV2, IDisposable
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<SyncPlayManagerV2> _logger;

        /// <summary>
        /// The logger factory.
        /// </summary>
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// The user manager.
        /// </summary>
        private readonly IUserManager _userManager;

        /// <summary>
        /// The session manager.
        /// </summary>
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// The library manager.
        /// </summary>
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// The map between users and counter of active sessions.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, int> _activeUsers =
            new ConcurrentDictionary<Guid, int>();

        /// <summary>
        /// The map between sessions and groups.
        /// </summary>
        private readonly ConcurrentDictionary<string, Group> _sessionToGroupMap =
            new ConcurrentDictionary<string, Group>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The groups.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, Group> _groups =
            new ConcurrentDictionary<Guid, Group>();

        /// <summary>
        /// Lock used for accessing multiple groups at once.
        /// </summary>
        /// <remarks>
        /// This lock has priority on locks made on <see cref="Group"/>.
        /// </remarks>
        private readonly Lock _groupsLock = new();

        /// <summary>
        /// The maximum time a single member can keep its group in the waiting state
        /// before the group stops waiting on it. The member is waited on again as soon
        /// as it reports again.
        /// </summary>
        private static readonly TimeSpan GroupWaitTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a buffering report is held back while the group keeps playing,
        /// giving the member a chance to recover before the whole group is paused.
        /// </summary>
        private static readonly TimeSpan BufferingGracePeriod = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long a member whose session ended is kept in its group. The group does
        /// not wait on disconnected members; a member that reconnects within the window
        /// resumes with a state snapshot, one that does not is removed.
        /// </summary>
        private static readonly TimeSpan DisconnectedGracePeriod = TimeSpan.FromSeconds(90);

        /// <summary>
        /// How often position beacons are broadcast to protocol version 2 members
        /// while a group is playing.
        /// </summary>
        private static readonly TimeSpan PositionBeaconInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The buffering requests currently held back, by session identifier.
        /// </summary>
        private readonly Dictionary<string, DeferredBuffering> _deferredBuffering =
            new Dictionary<string, DeferredBuffering>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Lock used for accessing the deferred buffering requests.
        /// </summary>
        /// <remarks>
        /// Never taken while holding a lock on a <see cref="Group"/>'s members, except
        /// right after acquiring the group lock itself (group lock has priority).
        /// </remarks>
        private readonly Lock _deferredBufferingLock = new();

        /// <summary>
        /// The timer driving the group sweep (group-wait timeouts and deferred buffering).
        /// </summary>
        private readonly Timer _sweepTimer;

        /// <summary>
        /// Whether a sweep is currently running, to avoid overlapping timer callbacks.
        /// </summary>
        private int _sweepActive;

        private bool _disposed = false;

        private readonly Sender _sender;

        private readonly ProtocolVersionRegistry _versions;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncPlayManager" /> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="sessionManager">The session manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        public SyncPlayManagerV2(
            ILoggerFactory loggerFactory,
            IUserManager userManager,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            Sender sender,
            ProtocolVersionRegistry versions)
        {
            _loggerFactory = loggerFactory;
            _userManager = userManager;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _sender = sender;
            _versions = versions;
            _logger = loggerFactory.CreateLogger<SyncPlayManagerV2>();
            _sessionManager.SessionEnded += OnSessionEnded;
            _sessionManager.SessionControllerConnected += OnSessionControllerConnected;
            _sweepTimer = new Timer(SweepGroups, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public GroupInfoDto NewGroup(SessionInfo session, NewGroupRequest request, CancellationToken cancellationToken)
        {
            if (session is null)
            {
                throw new InvalidOperationException("Session is null!");
            }

            if (request is null)
            {
                throw new InvalidOperationException("Request is null!");
            }

            // Locking required to access list of groups.
            lock (_groupsLock)
            {
                // Make sure that session has not joined another group.
                if (_sessionToGroupMap.ContainsKey(session.Id))
                {
                    var leaveGroupRequest = new LeaveGroupRequest();
                    LeaveGroup(session, leaveGroupRequest, cancellationToken);
                }

                var group = new Group(_loggerFactory, _userManager, _sessionManager, _libraryManager, _sender, _versions);
                _groups[group.GroupId] = group;

                if (!_sessionToGroupMap.TryAdd(session.Id, group))
                {
                    throw new InvalidOperationException("Could not add session to group!");
                }

                UpdateSessionsCounter(session.UserId, 1);
                group.CreateGroup(session, request, cancellationToken);
                return group.GetInfo();
            }
        }

        /// <inheritdoc />
        public void JoinGroup(SessionInfo session, JoinGroupRequest request, CancellationToken cancellationToken)
        {
            if (session is null)
            {
                throw new InvalidOperationException("Session is null!");
            }

            if (request is null)
            {
                throw new InvalidOperationException("Request is null!");
            }

            var user = _userManager.GetUserById(session.UserId);

            // Locking required to access list of groups.
            lock (_groupsLock)
            {
                _groups.TryGetValue(request.GroupId, out Group group);

                if (group is null)
                {
                    _logger.LogWarning("Session {SessionId} tried to join group {GroupId} that does not exist.", session.Id, request.GroupId);

                    var error = new SyncPlayGroupDoesNotExistUpdate(Guid.Empty, string.Empty);
                    _sessionManager.SendSyncPlayGroupUpdate(session.Id, error, CancellationToken.None);
                    return;
                }

                // Group lock required to let other requests end first.
                lock (group)
                {
                    if (!group.HasAccessToPlayQueue(user))
                    {
                        _logger.LogWarning("Session {SessionId} tried to join group {GroupId} but does not have access to some content of the playing queue.", session.Id, group.GroupId.ToString());

                        var error = new SyncPlayLibraryAccessDeniedUpdate(group.GroupId, string.Empty);
                        _sessionManager.SendSyncPlayGroupUpdate(session.Id, error, CancellationToken.None);
                        return;
                    }

                    if (_sessionToGroupMap.TryGetValue(session.Id, out var existingGroup))
                    {
                        if (existingGroup.GroupId.Equals(request.GroupId))
                        {
                            // Restore session. The session is already counted as a member,
                            // so the sessions counter must not be incremented again.
                            group.SessionJoin(session, request, cancellationToken);
                            return;
                        }

                        var leaveGroupRequest = new LeaveGroupRequest();
                        LeaveGroup(session, leaveGroupRequest, cancellationToken);
                    }

                    if (!_sessionToGroupMap.TryAdd(session.Id, group))
                    {
                        throw new InvalidOperationException("Could not add session to group!");
                    }

                    UpdateSessionsCounter(session.UserId, 1);
                    group.SessionJoin(session, request, cancellationToken);
                }
            }
        }

        /// <inheritdoc />
        public void LeaveGroup(SessionInfo session, LeaveGroupRequest request, CancellationToken cancellationToken)
        {
            if (session is null)
            {
                throw new InvalidOperationException("Session is null!");
            }

            if (request is null)
            {
                throw new InvalidOperationException("Request is null!");
            }

            // Locking required to access list of groups.
            lock (_groupsLock)
            {
                if (_sessionToGroupMap.TryGetValue(session.Id, out var group))
                {
                    // Group lock required to let other requests end first.
                    lock (group)
                    {
                        if (_sessionToGroupMap.TryRemove(session.Id, out var tempGroup))
                        {
                            if (!tempGroup.GroupId.Equals(group.GroupId))
                            {
                                throw new InvalidOperationException("Session was in wrong group!");
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException("Could not remove session from group!");
                        }

                        UpdateSessionsCounter(session.UserId, -1);
                        group.SessionLeave(session, request, cancellationToken);

                        if (group.IsGroupEmpty())
                        {
                            _logger.LogInformation("Group {GroupId} is empty, removing it.", group.GroupId);
                            _groups.Remove(group.GroupId, out _);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Session {SessionId} does not belong to any group.", session.Id);

                    var error = new SyncPlayNotInGroupUpdate(Guid.Empty, string.Empty);
                    _sessionManager.SendSyncPlayGroupUpdate(session.Id, error, CancellationToken.None);
                }
            }
        }

        /// <inheritdoc />
        public List<GroupInfoDto> ListGroups(SessionInfo session, ListGroupsRequest request)
        {
            if (session is null)
            {
                throw new InvalidOperationException("Session is null!");
            }

            if (request is null)
            {
                throw new InvalidOperationException("Request is null!");
            }

            var user = _userManager.GetUserById(session.UserId);
            List<GroupInfoDto> list = new List<GroupInfoDto>();

            lock (_groupsLock)
            {
                foreach (var (_, group) in _groups)
                {
                    // Locking required as group is not thread-safe.
                    lock (group)
                    {
                        if (group.HasAccessToPlayQueue(user))
                        {
                            list.Add(group.GetInfo());
                        }
                    }
                }
            }

            return list;
        }

        /// <inheritdoc />
        public List<WireGroupInfo> ListGroupsDetailed(SessionInfo session, bool requesterIsV2)
        {
            ArgumentNullException.ThrowIfNull(session);

            var user = _userManager.GetUserById(session.UserId);
            var list = new List<WireGroupInfo>();

            lock (_groupsLock)
            {
                foreach (var (_, group) in _groups)
                {
                    // Locking required as group is not thread-safe.
                    lock (group)
                    {
                        if (group.HasAccessToPlayQueue(user))
                        {
                            list.Add(group.GetWireInfo(requesterIsV2));
                        }
                    }
                }
            }

            return list;
        }

        /// <inheritdoc />
        public GroupInfoDto GetGroup(SessionInfo session, Guid groupId)
        {
            ArgumentNullException.ThrowIfNull(session);

            var user = _userManager.GetUserById(session.UserId);

            lock (_groupsLock)
            {
                foreach (var (_, group) in _groups)
                {
                    // Locking required as group is not thread-safe.
                    lock (group)
                    {
                        if (group.GroupId.Equals(groupId) && group.HasAccessToPlayQueue(user))
                        {
                            return group.GetInfo();
                        }
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public void HandleRequest(SessionInfo session, IGroupPlaybackRequest request, CancellationToken cancellationToken)
        {
            if (session is null)
            {
                throw new InvalidOperationException("Session is null!");
            }

            if (request is null)
            {
                throw new InvalidOperationException("Request is null!");
            }

            // Hold back buffering reports while the group is playing: most rebuffers
            // resolve within the grace period, in which case nobody else gets paused.
            if (request is BufferGroupRequest bufferRequest && TryDeferBuffering(session, bufferRequest))
            {
                return;
            }

            if (request is ReadyGroupRequest)
            {
                CancelDeferredBuffering(session.Id);
            }

            HandleRequestInternal(session, request, cancellationToken);
        }

        private void HandleRequestInternal(SessionInfo session, IGroupPlaybackRequest request, CancellationToken cancellationToken)
        {
            if (_sessionToGroupMap.TryGetValue(session.Id, out var group))
            {
                // Group lock required as Group is not thread-safe.
                lock (group)
                {
                    // Make sure that session still belongs to this group.
                    if (_sessionToGroupMap.TryGetValue(session.Id, out var checkGroup) && !checkGroup.GroupId.Equals(group.GroupId))
                    {
                        // Drop request.
                        return;
                    }

                    // Drop request if group is empty.
                    if (group.IsGroupEmpty())
                    {
                        return;
                    }

                    // Activity proves the member is alive: re-attach it if its session
                    // was considered disconnected or has been replaced by a new instance.
                    group.TouchSession(session);

                    // Apply requested changes to group.
                    group.HandleRequest(session, request, cancellationToken);
                }
            }
            else
            {
                _logger.LogWarning("Session {SessionId} does not belong to any group.", session.Id);

                var error = new SyncPlayNotInGroupUpdate(Guid.Empty, string.Empty);
                _sessionManager.SendSyncPlayGroupUpdate(session.Id, error, CancellationToken.None);
            }
        }

        /// <inheritdoc />
        public bool IsUserActive(Guid userId)
        {
            if (_activeUsers.TryGetValue(userId, out var sessionsCounter))
            {
                return sessionsCounter > 0;
            }

            return false;
        }

        /// <inheritdoc />
        public void RequestSnapshot(SessionInfo session, CancellationToken cancellationToken)
        {
            if (session is null)
            {
                throw new InvalidOperationException("Session is null!");
            }

            if (_sessionToGroupMap.TryGetValue(session.Id, out var group))
            {
                lock (group)
                {
                    // Make sure that session still belongs to this group.
                    if (_sessionToGroupMap.TryGetValue(session.Id, out var checkGroup) && checkGroup.GroupId.Equals(group.GroupId))
                    {
                        group.TouchSession(session);
                        group.ResyncSession(session, cancellationToken);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Session {SessionId} does not belong to any group.", session.Id);

                var error = new SyncPlayNotInGroupUpdate(Guid.Empty, string.Empty);
                _sessionManager.SendSyncPlayGroupUpdate(session.Id, error, CancellationToken.None);
            }
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _sessionManager.SessionEnded -= OnSessionEnded;
            _sessionManager.SessionControllerConnected -= OnSessionControllerConnected;
            _sweepTimer.Dispose();
            _disposed = true;
        }

        private void OnSessionEnded(object sender, SessionEventArgs e)
        {
            MarkSessionDisconnected(e.SessionInfo);
        }

        /// <summary>
        /// Marks the session's member disconnected (grace window starts). Called on
        /// SessionEnded, and by <see cref="SocketLiveness"/> for zombie sockets that
        /// stock never aborts — the transport died but the core session lives on.
        /// </summary>
        /// <param name="session">The session.</param>
        public void MarkSessionDisconnected(SessionInfo session)
        {
            CancelDeferredBuffering(session.Id);

            if (_sessionToGroupMap.TryGetValue(session.Id, out var group))
            {
                lock (group)
                {
                    // Make sure that session still belongs to this group.
                    if (!_sessionToGroupMap.TryGetValue(session.Id, out var checkGroup) || !checkGroup.GroupId.Equals(group.GroupId))
                    {
                        return;
                    }

                    // A transport death is not a leave: keep the membership for the grace
                    // window so that the member can resume where it left off on reconnect.
                    _logger.LogInformation("Session {SessionId} ended, keeping its membership of group {GroupId} for {Grace}.", session.Id, group.GroupId.ToString(), DisconnectedGracePeriod);
                    group.SetMemberDisconnected(session);

                    if (group.State.Equals(GroupStateType.Waiting))
                    {
                        // Re-evaluate the group wait now that this member is not waited on.
                        group.HandleRequest(session, new IgnoreWaitGroupRequest(true), CancellationToken.None);
                    }
                }
            }
        }

        private void OnSessionControllerConnected(object sender, SessionEventArgs e)
        {
            ReattachSession(e.SessionInfo);
        }

        /// <summary>
        /// Re-attaches a session's member and brings it up to date. Called when a
        /// WebSocket connects for the session, and by <see cref="SocketLiveness"/>
        /// when keep-alives resume on a socket previously presumed dead.
        /// </summary>
        /// <param name="session">The session.</param>
        public void ReattachSession(SessionInfo session)
        {
            if (_sessionToGroupMap.TryGetValue(session.Id, out var group))
            {
                lock (group)
                {
                    // Make sure that session still belongs to this group.
                    if (_sessionToGroupMap.TryGetValue(session.Id, out var checkGroup) && checkGroup.GroupId.Equals(group.GroupId))
                    {
                        // The session opened a (new) WebSocket: re-attach it if it was
                        // considered disconnected and bring it up to date, as messages
                        // sent while it had no open socket have been dropped.
                        group.ReconnectSession(session, CancellationToken.None);
                    }
                }
            }
        }

        private bool TryDeferBuffering(SessionInfo session, BufferGroupRequest request)
        {
            if (BufferingGracePeriod <= TimeSpan.Zero)
            {
                return false;
            }

            if (!_sessionToGroupMap.TryGetValue(session.Id, out var group))
            {
                return false;
            }

            lock (group)
            {
                // Make sure that session still belongs to this group.
                if (!_sessionToGroupMap.TryGetValue(session.Id, out var checkGroup) || !checkGroup.GroupId.Equals(group.GroupId))
                {
                    return false;
                }

                // Only reports that would interrupt everyone's playback are held back.
                if (!group.State.Equals(GroupStateType.Playing))
                {
                    return false;
                }

                lock (_deferredBufferingLock)
                {
                    // Keep the first report; a member that is still buffering does not get more grace.
                    if (!_deferredBuffering.ContainsKey(session.Id))
                    {
                        _deferredBuffering[session.Id] = new DeferredBuffering(session, request, group.GroupId, DateTime.UtcNow.Add(BufferingGracePeriod));
                        _logger.LogDebug("Session {SessionId} started buffering in group {GroupId}, holding back the report for {Grace}.", session.Id, group.GroupId.ToString(), BufferingGracePeriod);
                    }
                }

                return true;
            }
        }

        private void CancelDeferredBuffering(string sessionId)
        {
            lock (_deferredBufferingLock)
            {
                if (_deferredBuffering.Remove(sessionId))
                {
                    _logger.LogDebug("Session {SessionId} recovered within the buffering grace period.", sessionId);
                }
            }
        }

        private void SweepGroups(object state)
        {
            if (Interlocked.CompareExchange(ref _sweepActive, 1, 0) != 0)
            {
                return;
            }

            try
            {
                ApplyDueBufferingRequests();
                IgnoreStalledMembers();
                RemoveExpiredDisconnectedMembers();
                SendPositionBeacons();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error while sweeping SyncPlay groups.");
            }
            finally
            {
                Interlocked.Exchange(ref _sweepActive, 0);
            }
        }

        private void ApplyDueBufferingRequests()
        {
            List<DeferredBuffering> due = null;
            lock (_deferredBufferingLock)
            {
                if (_deferredBuffering.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                foreach (var (_, deferred) in _deferredBuffering)
                {
                    if (deferred.ApplyAt <= now)
                    {
                        (due ??= new List<DeferredBuffering>()).Add(deferred);
                    }
                }

                if (due is not null)
                {
                    foreach (var deferred in due)
                    {
                        _deferredBuffering.Remove(deferred.Session.Id);
                    }
                }
            }

            if (due is null)
            {
                return;
            }

            foreach (var deferred in due)
            {
                if (!_sessionToGroupMap.TryGetValue(deferred.Session.Id, out var group) || !group.GroupId.Equals(deferred.GroupId))
                {
                    continue;
                }

                lock (group)
                {
                    // If the group stopped playing during the grace period the member's
                    // buffering has either been accounted for already (group-wide wait)
                    // or no longer interrupts anyone.
                    if (group.State.Equals(GroupStateType.Playing))
                    {
                        _logger.LogDebug("Session {SessionId} did not recover within the grace period, pausing group {GroupId}.", deferred.Session.Id, group.GroupId.ToString());
                        group.HandleRequest(deferred.Session, deferred.Request, CancellationToken.None);
                    }
                }
            }
        }

        private void IgnoreStalledMembers()
        {
            lock (_groupsLock)
            {
                foreach (var (_, group) in _groups)
                {
                    // Locking required as group is not thread-safe.
                    lock (group)
                    {
                        if (!group.State.Equals(GroupStateType.Waiting))
                        {
                            continue;
                        }

                        foreach (var session in group.GetStalledBufferingSessions(GroupWaitTimeout))
                        {
                            // This is the moment the group gives up on a member,
                            // and until now giving up meant abandoning it: the
                            // group played on and the member was left wherever it
                            // happened to be, with nothing to bring it back but
                            // the next group command.
                            //
                            // It is also, for a member whose transport cannot seek
                            // accurately, the *only* moment reached. Measured on a
                            // transcoding Kodi client: a group Seek is answered
                            // with one correction at ~7s, this timeout fires at
                            // 10s, and the member's next report lands after the
                            // group has already left Waiting — so a policy that
                            // needs a second correction to trigger never triggers.
                            //
                            // A v2 member gets a rendezvous instead: the group
                            // still stops waiting, but the member is pushed a
                            // snapshot and its next Ready is answered with a
                            // private scheduled Unpause at the live position. v1
                            // members cannot be told any of that, so they are
                            // abandoned exactly as before.
                            if (group.IsV2Member(session.Id)
                                && SyncPlayV2Plugin.Instance?.Configuration.HotJoin != false)
                            {
                                group.RendezvousMember(
                                    session,
                                    $"kept the group waiting for over {GroupWaitTimeout}",
                                    CancellationToken.None);
                            }
                            else
                            {
                                _logger.LogWarning("Session {SessionId} kept group {GroupId} waiting for over {Timeout}, ignoring it until it reports again.", session.Id, group.GroupId.ToString(), GroupWaitTimeout);

                                group.MarkIgnoredByTimeout(session);
                            }

                            group.HandleRequest(session, new IgnoreWaitGroupRequest(true), CancellationToken.None);
                        }
                    }
                }
            }
        }

        private void RemoveExpiredDisconnectedMembers()
        {
            lock (_groupsLock)
            {
                List<SessionInfo> expired = null;
                foreach (var (_, group) in _groups)
                {
                    // Locking required as group is not thread-safe.
                    lock (group)
                    {
                        foreach (var session in group.GetExpiredDisconnectedSessions(DisconnectedGracePeriod))
                        {
                            (expired ??= new List<SessionInfo>()).Add(session);
                        }
                    }
                }

                if (expired is null)
                {
                    return;
                }

                foreach (var session in expired)
                {
                    _logger.LogInformation("Session {SessionId} did not reconnect within {Grace}, removing it from its group.", session.Id, DisconnectedGracePeriod);
                    LeaveGroup(session, new LeaveGroupRequest(), CancellationToken.None);
                }
            }
        }

        private void SendPositionBeacons()
        {
            lock (_groupsLock)
            {
                foreach (var (_, group) in _groups)
                {
                    // Locking required as group is not thread-safe.
                    lock (group)
                    {
                        group.SendPositionBeaconIfDue(PositionBeaconInterval, CancellationToken.None);
                    }
                }
            }
        }

        private void UpdateSessionsCounter(Guid userId, int toAdd)
        {
            // Update sessions counter.
            var newSessionsCounter = _activeUsers.AddOrUpdate(
                userId,
                1,
                (_, sessionsCounter) => sessionsCounter + toAdd);

            // Should never happen.
            if (newSessionsCounter < 0)
            {
                throw new InvalidOperationException("Sessions counter is negative!");
            }

            // Clean record if user has no more active sessions.
            if (newSessionsCounter == 0)
            {
                _activeUsers.TryRemove(new KeyValuePair<Guid, int>(userId, newSessionsCounter));
            }
        }

        /// <summary>
        /// A buffering report that is being held back for the duration of the grace period.
        /// </summary>
        private sealed class DeferredBuffering
        {
            public DeferredBuffering(SessionInfo session, BufferGroupRequest request, Guid groupId, DateTime applyAt)
            {
                Session = session;
                Request = request;
                GroupId = groupId;
                ApplyAt = applyAt;
            }

            public SessionInfo Session { get; }

            public BufferGroupRequest Request { get; }

            public Guid GroupId { get; }

            public DateTime ApplyAt { get; }
        }
    }
}
