#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using Jellyfin.Plugin.SyncPlayV2.Engine.GroupStates;
using MediaBrowser.Controller.SyncPlay.Queue;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// Class Group.
    /// </summary>
    /// <remarks>
    /// Class is not thread-safe, external locking is required when accessing methods.
    /// </remarks>
    public class Group : IGroupStateContextV2
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<Group> _logger;

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
        /// The participants, or members of the group.
        /// </summary>
        private readonly Dictionary<string, GroupMember> _participants =
            new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The internal group state.
        /// </summary>
        private IGroupState _state;

        /// <summary>
        /// The group state version, incremented on every group mutation. Outbound
        /// messages are stamped with it so that clients can detect missed updates.
        /// </summary>
        private long _stateVersion;

        /// <summary>
        /// The earliest time the next position beacon may be sent at.
        /// </summary>
        private DateTime _nextBeaconAt = DateTime.MaxValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="Group" /> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="sessionManager">The session manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        public Group(
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
            _logger = loggerFactory.CreateLogger<Group>();

            _state = new IdleGroupState(loggerFactory);
        }

        private readonly Sender _sender;

        private readonly ProtocolVersionRegistry _versions;

        /// <summary>
        /// Gets the default ping value used for sessions.
        /// </summary>
        /// <value>The default ping.</value>
        public long DefaultPing { get; } = 500;

        /// <summary>
        /// Gets the maximum time offset error accepted for dates reported by clients, in milliseconds.
        /// </summary>
        /// <value>The maximum time offset error.</value>
        public long TimeSyncOffset { get; } = 2000;

        /// <summary>
        /// Gets the maximum offset error accepted for position reported by clients, in milliseconds.
        /// </summary>
        /// <value>The maximum offset error.</value>
        public long MaxPlaybackOffset { get; } = 500;

        /// <summary>
        /// Gets the group identifier.
        /// </summary>
        /// <value>The group identifier.</value>
        public Guid GroupId { get; } = Guid.NewGuid();

        /// <summary>
        /// Gets the group name.
        /// </summary>
        /// <value>The group name.</value>
        public string GroupName { get; private set; }

        /// <summary>
        /// Gets the type of the current state of the group.
        /// </summary>
        /// <value>The type of the current state.</value>
        public GroupStateType State => _state.Type;

        /// <summary>
        /// Gets the group identifier.
        /// </summary>
        /// <value>The group identifier.</value>
        public PlayQueueManager PlayQueue { get; } = new PlayQueueManager();

        /// <summary>
        /// Gets the runtime ticks of current playing item.
        /// </summary>
        /// <value>The runtime ticks of current playing item.</value>
        public long RunTimeTicks { get; private set; }

        /// <summary>
        /// Gets or sets the position ticks.
        /// </summary>
        /// <value>The position ticks.</value>
        public long PositionTicks { get; set; }

        /// <summary>
        /// Gets or sets the last activity.
        /// </summary>
        /// <value>The last activity.</value>
        public DateTime LastActivity { get; set; }

        /// <summary>
        /// Adds the session to the group.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="protocolVersion">The SyncPlay protocol version the session's client speaks.</param>
        private void AddSession(SessionInfo session, int protocolVersion)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember member))
            {
                // The session re-joined: re-attach it, as the session instance may be a
                // new one (with the same identifier) if the previous one ended.
                member.Session = session;
                member.IsConnected = true;
                member.ProtocolVersion = protocolVersion;
            }
            else
            {
                _participants.Add(
                    session.Id,
                    new GroupMember(session)
                    {
                        Ping = DefaultPing,
                        IsBuffering = false,
                        ProtocolVersion = protocolVersion
                    });
            }

            BumpStateVersion();
        }

        /// <summary>
        /// Removes the session from the group.
        /// </summary>
        /// <param name="session">The session.</param>
        private void RemoveSession(SessionInfo session)
        {
            _participants.Remove(session.Id);
            BumpStateVersion();
        }

        /// <summary>
        /// Increments the group state version.
        /// </summary>
        private void BumpStateVersion()
        {
            _stateVersion++;
        }

        /// <summary>
        /// Filters sessions of this group.
        /// </summary>
        /// <param name="fromId">The current session identifier.</param>
        /// <param name="type">The filtering type.</param>
        /// <returns>The list of sessions matching the filter.</returns>
        private IEnumerable<string> FilterSessions(string fromId, SyncPlayBroadcastType type)
        {
            // Disconnected members have no live session to deliver to; they are brought
            // up to date with a state snapshot when they reconnect.
            return type switch
            {
                // A session that is not a member (e.g. one that just left) is still addressable.
                SyncPlayBroadcastType.CurrentSession when _participants.TryGetValue(fromId, out GroupMember fromMember) && !fromMember.IsConnected
                    => Enumerable.Empty<string>(),
                SyncPlayBroadcastType.CurrentSession => new string[] { fromId },
                SyncPlayBroadcastType.AllGroup => _participants
                    .Values
                    .Where(member => member.IsConnected)
                    .Select(member => member.SessionId),
                SyncPlayBroadcastType.AllExceptCurrentSession => _participants
                    .Values
                    .Where(member => member.IsConnected)
                    .Select(member => member.SessionId)
                    .Where(sessionId => !sessionId.Equals(fromId, StringComparison.OrdinalIgnoreCase)),
                SyncPlayBroadcastType.AllReady => _participants
                    .Values
                    .Where(member => member.IsConnected && !member.IsBuffering)
                    .Select(member => member.SessionId),
                _ => Enumerable.Empty<string>()
            };
        }

        /// <summary>
        /// Checks if a given user can access all items of a given queue, that is,
        /// the user has the required minimum parental access and has access to all required folders.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="queue">The queue.</param>
        /// <returns><c>true</c> if the user can access all the items in the queue, <c>false</c> otherwise.</returns>
        private bool HasAccessToQueue(User user, IReadOnlyList<Guid> queue)
        {
            // Check if queue is empty.
            if (queue is null || queue.Count == 0)
            {
                return true;
            }

            foreach (var itemId in queue)
            {
                // Fix divergence (VENDORED.md): GetItemById answers null for
                // an unknown or deleted id, and upstream dereferences it.
                var item = _libraryManager.GetItemById(itemId);
                if (item is null || !item.IsVisibleStandalone(user))
                {
                    return false;
                }
            }

            return true;
        }

        private bool AllUsersHaveAccessToQueue(IReadOnlyList<Guid> queue)
        {
            // Check if queue is empty.
            if (queue is null || queue.Count == 0)
            {
                return true;
            }

            // Get list of users.
            var users = _participants
                .Values
                .Select(participant => _userManager.GetUserById(participant.UserId));

            // Find problematic users.
            var usersWithNoAccess = users.Where(user => !HasAccessToQueue(user, queue));

            // All users must be able to access the queue.
            return !usersWithNoAccess.Any();
        }

        /// <summary>
        /// Checks if the group is empty.
        /// </summary>
        /// <returns><c>true</c> if the group is empty, <c>false</c> otherwise.</returns>
        public bool IsGroupEmpty() => _participants.Count == 0;

        /// <summary>
        /// Initializes the group with the session's info.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void CreateGroup(SessionInfo session, NewGroupRequest request, CancellationToken cancellationToken)
        {
            GroupName = request.GroupName;
            AddSession(session, _versions.Resolve(session));

            var sessionIsPlayingAnItem = session.FullNowPlayingItem is not null;

            RestartCurrentItem();

            if (sessionIsPlayingAnItem)
            {
                var playlist = session.NowPlayingQueue.Select(item => item.Id).ToList();
                PlayQueue.Reset();
                PlayQueue.SetPlaylist(playlist);
                PlayQueue.SetPlayingItemById(session.FullNowPlayingItem.Id);
                RunTimeTicks = session.FullNowPlayingItem.RunTimeTicks ?? 0;
                PositionTicks = session.PlayState.PositionTicks ?? 0;

                // Maintain playstate.
                var waitingState = new WaitingGroupState(_loggerFactory)
                {
                    ResumePlaying = !session.PlayState.IsPaused
                };
                SetState(waitingState);
            }

            SendWireUpdate(session, "GroupJoined", GetWireInfo(IsV2Member(session.Id)), cancellationToken);

            _state.SessionJoined(this, _state.Type, session, cancellationToken);

            _logger.LogInformation("Session {SessionId} created group {GroupId}.", session.Id, GroupId.ToString());
        }

        /// <summary>
        /// Adds the session to the group.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void SessionJoin(SessionInfo session, JoinGroupRequest request, CancellationToken cancellationToken)
        {
            AddSession(session, _versions.Resolve(session));

            SendWireUpdate(session, "GroupJoined", GetWireInfo(IsV2Member(session.Id)), cancellationToken);

            var updateOthers = new SyncPlayUserJoinedUpdate(GroupId, session.UserName);
            SendGroupUpdate(session, SyncPlayBroadcastType.AllExceptCurrentSession, updateOthers, cancellationToken);

            _state.SessionJoined(this, _state.Type, session, cancellationToken);

            _logger.LogInformation("Session {SessionId} joined group {GroupId}.", session.Id, GroupId.ToString());
        }

        /// <summary>
        /// Removes the session from the group.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void SessionLeave(SessionInfo session, LeaveGroupRequest request, CancellationToken cancellationToken)
        {
            _state.SessionLeaving(this, _state.Type, session, cancellationToken);

            RemoveSession(session);

            var updateSession = new SyncPlayGroupLeftUpdate(GroupId, GroupId.ToString());
            SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, updateSession, cancellationToken);

            var updateOthers = new SyncPlayUserLeftUpdate(GroupId, session.UserName);
            SendGroupUpdate(session, SyncPlayBroadcastType.AllExceptCurrentSession, updateOthers, cancellationToken);

            _logger.LogInformation("Session {SessionId} left group {GroupId}.", session.Id, GroupId.ToString());
        }

        /// <summary>
        /// Handles the requested action by the session.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="request">The requested action.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void HandleRequest(SessionInfo session, IGroupPlaybackRequest request, CancellationToken cancellationToken)
        {
            // The server's job is to maintain a consistent state for clients to reference
            // and notify clients of state changes. The actual syncing of media playback
            // happens client side. Clients are aware of the server's time and use it to sync.
            _logger.LogInformation("Session {SessionId} requested {RequestType} in group {GroupId} that is {StateType}.", session.Id, request.Action, GroupId.ToString(), _state.Type);

            // Apply requested changes to this group given its current state.
            // Every request has a slightly different outcome depending on the group's state.
            // There are currently four different group states that accomplish different goals:
            // - Idle: in this state no media is playing and clients should be idle (playback is stopped).
            // - Waiting: in this state the group is waiting for all the clients to be ready to start the playback,
            //      that is, they've either finished loading the media for the first time or they've finished buffering.
            //      Once all clients report to be ready the group's state can change to Playing or Paused.
            // - Playing: clients have some media loaded and playback is unpaused.
            // - Paused: clients have some media loaded but playback is currently paused.
            request.Apply(this, _state, session, cancellationToken);
        }

        /// <summary>
        /// Gets the info about the group for the clients.
        /// </summary>
        /// <returns>The group info for the clients.</returns>
        public GroupInfoDto GetInfo()
        {
            var participants = _participants.Values.Select(session => session.UserName).Distinct().ToList();
            return new GroupInfoDto(GroupId, GroupName, _state.Type, participants, DateTime.UtcNow);
        }

        /// <summary>
        /// The enriched group info for the wire (GroupJoined payload, shadowed List):
        /// upstream's GroupInfoDto v2 additions, with the ProtocolVersion advertisement
        /// member-scoped per the plugin-binding rule (docs/feasibility.md §5.2).
        /// </summary>
        /// <param name="forV2Requester">Whether the recipient negotiated protocol v2.</param>
        /// <returns>The wire group info.</returns>
        public WireGroupInfo GetWireInfo(bool forV2Requester)
        {
            return new WireGroupInfo
            {
                ProtocolVersion = forV2Requester ? 2 : null,
                GroupId = GroupId,
                GroupName = GroupName,
                State = _state.Type,
                Participants = _participants.Values.Select(member => member.UserName).Distinct().ToList(),
                LastUpdatedAt = DateTime.UtcNow,
                Members = GetMembersStatus(),
            };
        }

        /// <summary>
        /// Whether the member negotiated protocol v2.
        /// </summary>
        /// <param name="sessionId">The member's session id.</param>
        /// <returns>true when the member is v2.</returns>
        public bool IsV2Member(string sessionId)
            => _participants.TryGetValue(sessionId, out GroupMember member) && member.ProtocolVersion >= 2;

        /// <inheritdoc />
        public bool IsHotJoining(string sessionId)
            => _participants.TryGetValue(sessionId, out GroupMember member) && member.HotJoining;

        /// <inheritdoc />
        public void BeginHotJoin(SessionInfo session, CancellationToken cancellationToken)
        {
            if (!_participants.TryGetValue(session.Id, out GroupMember member))
            {
                return;
            }

            // The group must not wait on a member that is still catching the
            // running playback; its own reports clear these flags.
            member.HotJoining = true;
            member.IsBuffering = true;
            member.IgnoreGroupWait = true;
            member.IgnoredByTimeout = true;
            BumpStateVersion();

            // Everything the joiner needs to rendezvous: the complete state,
            // with position-at-time to extrapolate the running playback from.
            SendWireUpdate(session, "StateSnapshot", GetSnapshot(), cancellationToken);

            _logger.LogInformation(
                "Session {SessionId} hot-joining group {GroupId}: playback continues for everyone else.",
                session.Id,
                GroupId.ToString());
        }

        /// <inheritdoc />
        public bool ShouldRendezvous(SessionInfo session, long delayTicks)
        {
            if (!_participants.TryGetValue(session.Id, out GroupMember member))
            {
                return false;
            }

            var previous = member.LastCorrectionDelayTicks;
            var attempts = ++member.CorrectionAttempts;
            member.LastCorrectionDelayTicks = delayTicks;

            return CorrectionPolicy.CannotConverge(attempts, previous, delayTicks);
        }

        /// <inheritdoc />
        public void RendezvousMember(SessionInfo session, string reason, CancellationToken cancellationToken)
        {
            if (!_participants.TryGetValue(session.Id, out GroupMember member))
            {
                return;
            }

            member.CorrectionAttempts = 0;
            member.LastCorrectionDelayTicks = 0;

            _logger.LogInformation(
                "Session {SessionId} rendezvousing in group {GroupId}: {Reason}.",
                session.Id,
                GroupId.ToString(),
                reason);

            // Everything from here is the ordinary hot join: BeginHotJoin stops
            // the group waiting and pushes a snapshot to reload from, and the
            // member's next Ready is answered by CompleteHotJoin with a private
            // scheduled Unpause. Nothing here is rendezvous-specific, which is
            // the point — a member that cannot catch up by seeking is in
            // exactly the position of one that has just walked in.
            BeginHotJoin(session, cancellationToken);
        }

        /// <inheritdoc />
        public void CompleteHotJoin(SessionInfo session, CancellationToken cancellationToken)
        {
            if (!_participants.TryGetValue(session.Id, out GroupMember member) || !member.HotJoining)
            {
                return;
            }

            member.HotJoining = false;
            SetBuffering(session, false);
            BumpStateVersion();

            // The rendezvous: a private scheduled start at the position the
            // group will occupy at that instant — the same mechanism as a
            // group start, so the joiner arrives with start-grade tightness.
            var now = DateTime.UtcNow;
            var lead = TimeSpan.FromMilliseconds(Math.Max(2 * member.Ping, DefaultPing));
            var when = now + lead;
            var positionTicks = PositionTicks + Math.Max((when - LastActivity).Ticks, 0);
            var command = new SendCommand(
                GroupId,
                PlayQueue.GetPlayingItemPlaylistId(),
                when,
                SendCommandType.Unpause,
                positionTicks,
                now);
            SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

            _logger.LogInformation(
                "Session {SessionId} rendezvous in group {GroupId}: Unpause at {PositionTicks} ticks, lead {Lead}ms.",
                session.Id,
                GroupId.ToString(),
                positionTicks,
                (int)lead.TotalMilliseconds);
        }

        /// <summary>
        /// Sends a plugin-wire group update (open Type string, StateVersion stamped)
        /// to a single session. Delivery faults are logged by the sender.
        /// </summary>
        /// <summary>
        /// Update types the protocol added at v2. Spec §2: these are only ever
        /// sent to members that negotiated v2, which is what lets one group hold
        /// v1 and v2 members at once.
        /// </summary>
        private static readonly HashSet<string> V2OnlyUpdates =
            new(StringComparer.Ordinal) { "StateSnapshot", "PositionBeacon" };

        private void SendWireUpdate(SessionInfo to, string type, object data, CancellationToken cancellationToken)
        {
            // The beacon and the reconnect resync each check the member's
            // version at their own call site; this one did not, so a v1 member
            // pulled onto the hot-join path was sent a StateSnapshot it cannot
            // read. Gate centrally instead, so the rule holds for every caller
            // rather than for the callers that remembered.
            if (V2OnlyUpdates.Contains(type)
                && _participants.TryGetValue(to.Id, out GroupMember member)
                && member.ProtocolVersion < 2)
            {
                _logger.LogDebug(
                    "Withholding {Type} from session {SessionId} in group {GroupId}: protocol v{Version}.",
                    type,
                    to.Id,
                    GroupId.ToString(),
                    member.ProtocolVersion);
                return;
            }

            _ = _sender.SendGroupUpdate(
                to,
                new WireGroupUpdate
                {
                    GroupId = GroupId,
                    Type = type,
                    StateVersion = _stateVersion,
                    Data = data,
                },
                cancellationToken);
        }

        /// <summary>
        /// Sends the current group state to a member whose connection was (re)established,
        /// so that it recovers from messages that were dropped while it had no open WebSocket.
        /// </summary>
        /// <param name="session">The session to bring up to date.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void ResyncSession(SessionInfo session, CancellationToken cancellationToken)
        {
            if (!_participants.TryGetValue(session.Id, out GroupMember member))
            {
                return;
            }

            if (member.ProtocolVersion >= 2)
            {
                // Protocol version 2 clients get the state in a single message.
                SendWireUpdate(session, "StateSnapshot", GetSnapshot(), cancellationToken);
            }
            else
            {
                SendWireUpdate(session, "GroupJoined", GetWireInfo(false), cancellationToken);

                var queueUpdate = new SyncPlayPlayQueueUpdate(GroupId, GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist));
                SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, queueUpdate, cancellationToken);

                var commandType = _state.Type switch
                {
                    GroupStateType.Playing => SendCommandType.Unpause,
                    GroupStateType.Idle => SendCommandType.Stop,
                    _ => SendCommandType.Pause
                };
                var command = NewSyncPlayCommand(commandType);
                SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
            }

            _logger.LogInformation("Sent state snapshot to session {SessionId} in group {GroupId}.", session.Id, GroupId.ToString());
        }

        /// <summary>
        /// Builds a snapshot of the full group state.
        /// </summary>
        /// <returns>The group state snapshot.</returns>
        public WireGroupSnapshot GetSnapshot()
        {
            var now = DateTime.UtcNow;
            var isPlaying = _state.Type.Equals(GroupStateType.Playing);
            var positionTicks = PositionTicks;
            if (isPlaying)
            {
                // Playback may be scheduled to unpause in the future, in which case
                // LastActivity is in the future too and no time has elapsed yet.
                positionTicks += Math.Max((now - LastActivity).Ticks, 0);
            }

            return new WireGroupSnapshot
            {
                GroupName = GroupName,
                State = _state.Type,
                PlayQueue = GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist),
                PositionTicks = positionTicks,
                When = now,
                IsPlaying = isPlaying,
                Members = GetMembersStatus(),
            };
        }

        /// <summary>
        /// Sends a position beacon to protocol version 2 members if the group is playing
        /// and enough time has passed since the previous beacon.
        /// </summary>
        /// <param name="interval">The minimum interval between beacons.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void SendPositionBeaconIfDue(TimeSpan interval, CancellationToken cancellationToken)
        {
            if (!_state.Type.Equals(GroupStateType.Playing))
            {
                _nextBeaconAt = DateTime.MaxValue;
                return;
            }

            var now = DateTime.UtcNow;
            if (_nextBeaconAt == DateTime.MaxValue)
            {
                // The group just entered the playing state: beacon right away.
                _nextBeaconAt = now;
            }

            if (now < _nextBeaconAt)
            {
                return;
            }

            _nextBeaconAt = now + interval;

            var positionTicks = PositionTicks + Math.Max((now - LastActivity).Ticks, 0);
            var beacon = new WireGroupUpdate
            {
                GroupId = GroupId,
                Type = "PositionBeacon",
                StateVersion = _stateVersion,
                Data = new WirePositionBeacon
                {
                    PlaylistItemId = PlayQueue.GetPlayingItemPlaylistId(),
                    PositionTicks = positionTicks,
                    When = now,
                },
            };

            foreach (var member in _participants.Values)
            {
                if (member.IsConnected && member.ProtocolVersion >= 2)
                {
                    _ = _sender.SendGroupUpdate(member.Session, beacon, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Marks a member as disconnected: the group stops waiting on it and stops
        /// sending messages to it, but keeps it as a member so that it can resume
        /// where it left off when it reconnects within the grace window.
        /// </summary>
        /// <param name="session">The session of the member.</param>
        public void SetMemberDisconnected(SessionInfo session)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember member))
            {
                member.IsConnected = false;
                member.DisconnectedSince = DateTime.UtcNow;
                // Flag like a group-wait timeout so that the member is automatically
                // waited on again the next time one of its reports is processed.
                member.IgnoreGroupWait = true;
                member.IgnoredByTimeout = true;
                BumpStateVersion();
            }
        }

        /// <summary>
        /// Re-attaches the session to its member if the member was considered disconnected
        /// or if the session instance was replaced by a new one with the same identifier.
        /// Called for activity that proves the member is alive (playback requests).
        /// </summary>
        /// <param name="session">The session.</param>
        public void TouchSession(SessionInfo session)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember member))
            {
                member.Session = session;
                if (!member.IsConnected)
                {
                    member.IsConnected = true;
                    BumpStateVersion();
                }
            }
        }

        /// <summary>
        /// Re-attaches a reconnected session to its member and brings it up to date.
        /// The new session instance replaces the ended one with the same identifier.
        /// </summary>
        /// <param name="session">The new session.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void ReconnectSession(SessionInfo session, CancellationToken cancellationToken)
        {
            if (!_participants.TryGetValue(session.Id, out GroupMember member))
            {
                return;
            }

            if (member.IsConnected && ReferenceEquals(member.Session, session))
            {
                // Same live session opened a new socket: it only needs to catch up on
                // messages dropped while it had no open socket.
                ResyncSession(session, cancellationToken);
                return;
            }

            member.Session = session;
            member.IsConnected = true;

            // Same rule as the report path: reconnecting undoes the group's own
            // "stop waiting for this one", not a spectator choice the member
            // made and has not taken back.
            member.ResumeWaiting();
            BumpStateVersion();

            _logger.LogInformation("Session {SessionId} reconnected to group {GroupId} within the grace window.", session.Id, GroupId.ToString());

            ResyncSession(session, cancellationToken);
        }

        /// <summary>
        /// Gets the sessions of members that have been disconnected for longer than the grace window.
        /// </summary>
        /// <param name="grace">The grace window.</param>
        /// <returns>The list of expired sessions.</returns>
        public IReadOnlyList<SessionInfo> GetExpiredDisconnectedSessions(TimeSpan grace)
        {
            List<SessionInfo> expired = null;
            var now = DateTime.UtcNow;
            foreach (var member in _participants.Values)
            {
                if (!member.IsConnected && now - member.DisconnectedSince > grace)
                {
                    (expired ??= new List<SessionInfo>()).Add(member.Session);
                }
            }

            return expired ?? (IReadOnlyList<SessionInfo>)Array.Empty<SessionInfo>();
        }

        private List<WireMemberStatus> GetMembersStatus()
        {
            return _participants.Values
                .Select(member => new WireMemberStatus
                {
                    UserName = member.UserName,
                    IsBuffering = member.IsBuffering,
                    IgnoreGroupWait = member.IgnoreGroupWait,
                    Ping = member.Ping,
                    IsConnected = member.IsConnected,
                })
                .ToList();
        }

        /// <summary>
        /// Gets the sessions of members that the group has been waiting on for longer than the given timeout.
        /// </summary>
        /// <param name="timeout">The maximum time a member is allowed to keep the group waiting.</param>
        /// <returns>The list of sessions that outlived the timeout.</returns>
        public IReadOnlyList<SessionInfo> GetStalledBufferingSessions(TimeSpan timeout)
        {
            List<SessionInfo> stalled = null;
            var now = DateTime.UtcNow;
            foreach (var member in _participants.Values)
            {
                if (member.IsBuffering && !member.IgnoreGroupWait && now - member.BufferingSince > timeout)
                {
                    (stalled ??= new List<SessionInfo>()).Add(member.Session);
                }
            }

            return stalled ?? (IReadOnlyList<SessionInfo>)Array.Empty<SessionInfo>();
        }

        /// <summary>
        /// Flags a member as being ignored because it kept the group waiting for too long.
        /// The flag is cleared when the member reports again, restoring normal group-wait behavior.
        /// </summary>
        /// <param name="session">The session of the member.</param>
        public void MarkIgnoredByTimeout(SessionInfo session)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember value))
            {
                value.IgnoredByTimeout = true;
            }
        }

        /// <summary>
        /// Checks if a user has access to all content in the play queue.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <returns><c>true</c> if the user can access the play queue; <c>false</c> otherwise.</returns>
        public bool HasAccessToPlayQueue(User user)
        {
            var items = PlayQueue.GetPlaylist().Select(item => item.ItemId).ToList();
            return HasAccessToQueue(user, items);
        }

        /// <inheritdoc />
        public void SetIgnoreGroupWait(SessionInfo session, bool ignoreGroupWait)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember value))
            {
                value.IgnoreGroupWait = ignoreGroupWait;
            }
        }

        /// <summary>
        /// Remembers that not being waited for was the member's own choice, so
        /// that a later report or reconnect cannot undo it the way those undo
        /// the group's own giving up.
        ///
        /// Deliberately separate from <see cref="SetIgnoreGroupWait"/>: the
        /// engine synthesizes an IgnoreWaitGroupRequest of its own twice — the
        /// wait-timeout sweep and a transport death while the group waits — and
        /// both reach the same state handler as a real one from the wire.
        /// Stamping inside the setter therefore marked every timed-out member as
        /// having asked, so the group never waited for it again for the rest of
        /// the group's life. Measured: a member the group gave up on at 10s
        /// stayed IgnoreGroupWait=true across its next report.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="byRequest">Whether the member asked.</param>
        public void RecordIgnoreWaitByRequest(SessionInfo session, bool byRequest)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember value))
            {
                value.IgnoreGroupWaitByRequest = byRequest;
            }
        }

        /// <inheritdoc />
        public void SetState(IGroupState state)
        {
            _logger.LogInformation("Group {GroupId} switching from {FromStateType} to {ToStateType}.", GroupId.ToString(), _state.Type, state.Type);
            this._state = state;
            BumpStateVersion();

            if (!state.Type.Equals(GroupStateType.Playing))
            {
                // Leaving Playing ends any in-flight hot joins: those members
                // take part in the new choreography like everyone else (their
                // not-waited-on flags clear on their next report as usual).
                foreach (var member in _participants.Values)
                {
                    member.HotJoining = false;
                }
            }
        }

        /// <inheritdoc />
        public Task SendGroupUpdate<T>(SessionInfo from, SyncPlayBroadcastType type, GroupUpdate<T> message, CancellationToken cancellationToken)
        {
            // Translate the stock typed update (built by the vendored states) into
            // the plugin wire shape — StateVersion stamped, Type as string — and
            // deliver through the session controllers (the M0-proven path).
            var wire = WireGroupUpdate.From(message, _stateVersion);

            IEnumerable<Task> GetTasks()
            {
                foreach (var sessionId in FilterSessions(from.Id, type))
                {
                    var target = ResolveTarget(sessionId, from);
                    if (target is not null)
                    {
                        yield return _sender.SendGroupUpdate(target, wire, cancellationToken);
                    }
                }
            }

            return ObserveSendFaults(Task.WhenAll(GetTasks()));
        }

        /// <summary>
        /// Resolves a target session for delivery: the requester itself (still
        /// addressable right after leaving) or a member's live session.
        /// </summary>
        private SessionInfo ResolveTarget(string sessionId, SessionInfo from)
        {
            if (string.Equals(sessionId, from.Id, StringComparison.OrdinalIgnoreCase))
            {
                return from;
            }

            return _participants.TryGetValue(sessionId, out GroupMember member) ? member.Session : null;
        }

        /// <inheritdoc />
        public Task SendCommand(SessionInfo from, SyncPlayBroadcastType type, SendCommand message, CancellationToken cancellationToken)
        {
            var wire = WireSendCommand.From(message, _stateVersion);

            IEnumerable<Task> GetTasks()
            {
                foreach (var sessionId in FilterSessions(from.Id, type))
                {
                    var target = ResolveTarget(sessionId, from);
                    if (target is not null)
                    {
                        yield return _sender.SendCommand(target, wire, cancellationToken);
                    }
                }
            }

            return ObserveSendFaults(Task.WhenAll(GetTasks()));
        }

        /// <summary>
        /// Logs delivery failures of the given send task; callers of the send methods
        /// do not await them, so faults would otherwise vanish as unobserved exceptions.
        /// </summary>
        /// <param name="task">The send task.</param>
        /// <returns>The same task.</returns>
        private Task ObserveSendFaults(Task task)
        {
            _ = task.ContinueWith(
                t => _logger.LogWarning(t.Exception, "Failed to deliver a SyncPlay message to one or more sessions in group {GroupId}.", GroupId.ToString()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return task;
        }

        /// <inheritdoc />
        public SendCommand NewSyncPlayCommand(SendCommandType type)
        {
            return new SendCommand(
                GroupId,
                PlayQueue.GetPlayingItemPlaylistId(),
                LastActivity,
                type,
                PositionTicks,
                DateTime.UtcNow);
        }

        /// <inheritdoc />
        public long SanitizePositionTicks(long? positionTicks)
        {
            // Fix divergence (VENDORED.md): a runtime of 0 is unbounded, not
            // a clamp-everything-to-zero; the arithmetic lives in Positions.
            return Positions.Sanitize(positionTicks, RunTimeTicks);
        }

        /// <inheritdoc />
        public void UpdatePing(SessionInfo session, long ping)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember value))
            {
                value.Ping = ping;
            }
        }

        /// <inheritdoc />
        public long GetHighestPing()
        {
            long max = long.MinValue;
            foreach (var session in _participants.Values)
            {
                max = Math.Max(max, session.Ping);
            }

            return max;
        }

        /// <inheritdoc />
        public long GetMemberPlaybackOffset(SessionInfo session)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember member))
            {
                // Higher-latency members get a proportionally larger tolerance before
                // being corrected, bounded so that direct-play members stay tight.
                return Math.Clamp(2 * member.Ping, MaxPlaybackOffset, 2000);
            }

            return MaxPlaybackOffset;
        }

        /// <inheritdoc />
        public bool IsIgnoredByTimeout(string sessionId)
        {
            return _participants.TryGetValue(sessionId, out GroupMember member) && member.IgnoredByTimeout;
        }

        /// <inheritdoc />
        public void SetBuffering(SessionInfo session, bool isBuffering)
        {
            if (_participants.TryGetValue(session.Id, out GroupMember value))
            {
                SetMemberBuffering(value, isBuffering);

                if (!isBuffering)
                {
                    // It arrived: the next member to fall behind starts its own
                    // correction sequence from scratch.
                    value.CorrectionAttempts = 0;
                    value.LastCorrectionDelayTicks = 0;
                }

                if (!isBuffering)
                {
                    // The member reported again after being ignored for keeping the group
                    // waiting; let the group wait for it once more.
                    value.ResumeWaiting();
                }
            }
        }

        /// <inheritdoc />
        public void SetAllBuffering(bool isBuffering)
        {
            foreach (var session in _participants.Values)
            {
                SetMemberBuffering(session, isBuffering);
            }
        }

        private static void SetMemberBuffering(GroupMember member, bool isBuffering)
        {
            if (isBuffering && !member.IsBuffering)
            {
                member.BufferingSince = DateTime.UtcNow;
            }

            member.IsBuffering = isBuffering;
        }

        /// <inheritdoc />
        public bool IsBuffering()
        {
            foreach (var session in _participants.Values)
            {
                if (session.IsBuffering && !session.IgnoreGroupWait)
                {
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public bool SetPlayQueue(IReadOnlyList<Guid> playQueue, int playingItemPosition, long startPositionTicks)
        {
            // Ignore on empty queue or invalid item position.
            if (playQueue.Count == 0 || playingItemPosition >= playQueue.Count || playingItemPosition < 0)
            {
                return false;
            }

            // Check if participants can access the new playing queue.
            if (!AllUsersHaveAccessToQueue(playQueue))
            {
                return false;
            }

            PlayQueue.Reset();
            PlayQueue.SetPlaylist(playQueue);
            PlayQueue.SetPlayingItemByIndex(playingItemPosition);
            // Fix divergence (VENDORED.md): null-guarded — an item deleted
            // between the access check and here NRE'd inside the group lock.
            RunTimeTicks = _libraryManager.GetItemById(PlayQueue.GetPlayingItemId())?.RunTimeTicks ?? 0;
            PositionTicks = startPositionTicks;
            LastActivity = DateTime.UtcNow;
            BumpStateVersion();

            return true;
        }

        /// <inheritdoc />
        public bool SetPlayingItem(Guid playlistItemId)
        {
            var itemFound = PlayQueue.SetPlayingItemByPlaylistId(playlistItemId);

            if (itemFound)
            {
                // Fix divergence (VENDORED.md): null-guarded (see HasAccessToQueue).
                RunTimeTicks = _libraryManager.GetItemById(PlayQueue.GetPlayingItemId())?.RunTimeTicks ?? 0;
            }
            else
            {
                RunTimeTicks = 0;
            }

            RestartCurrentItem();
            BumpStateVersion();

            return itemFound;
        }

        /// <inheritdoc />
        public void ClearPlayQueue(bool clearPlayingItem)
        {
            PlayQueue.ClearPlaylist(clearPlayingItem);
            if (clearPlayingItem)
            {
                RestartCurrentItem();
            }

            BumpStateVersion();
        }

        /// <inheritdoc />
        public bool RemoveFromPlayQueue(IReadOnlyList<Guid> playlistItemIds)
        {
            var playingItemRemoved = PlayQueue.RemoveFromPlaylist(playlistItemIds);
            if (playingItemRemoved)
            {
                var itemId = PlayQueue.GetPlayingItemId();
                if (!itemId.IsEmpty())
                {
                    // Fix divergence (VENDORED.md): null-guarded (see HasAccessToQueue).
                    RunTimeTicks = _libraryManager.GetItemById(itemId)?.RunTimeTicks ?? 0;
                }
                else
                {
                    RunTimeTicks = 0;
                }

                RestartCurrentItem();
            }

            BumpStateVersion();

            return playingItemRemoved;
        }

        /// <inheritdoc />
        public bool MoveItemInPlayQueue(Guid playlistItemId, int newIndex)
        {
            var moved = PlayQueue.MovePlaylistItem(playlistItemId, newIndex);
            if (moved)
            {
                BumpStateVersion();
            }

            return moved;
        }

        /// <inheritdoc />
        public bool AddToPlayQueue(IReadOnlyList<Guid> newItems, GroupQueueMode mode)
        {
            // Ignore on empty list.
            if (newItems.Count == 0)
            {
                return false;
            }

            // Check if participants can access the new playing queue.
            if (!AllUsersHaveAccessToQueue(newItems))
            {
                return false;
            }

            if (mode.Equals(GroupQueueMode.QueueNext))
            {
                PlayQueue.QueueNext(newItems);
            }
            else
            {
                PlayQueue.Queue(newItems);
            }

            BumpStateVersion();

            return true;
        }

        /// <inheritdoc />
        public void RestartCurrentItem()
        {
            PositionTicks = 0;
            LastActivity = DateTime.UtcNow;
        }

        /// <inheritdoc />
        public bool NextItemInQueue()
        {
            var update = PlayQueue.Next();
            if (update)
            {
                // Fix divergence (VENDORED.md): null-guarded — upstream's NRE
                // here fired *after* the queue pointer advanced, so the group's
                // index and every client's view diverged permanently.
                RunTimeTicks = _libraryManager.GetItemById(PlayQueue.GetPlayingItemId())?.RunTimeTicks ?? 0;
                RestartCurrentItem();
                BumpStateVersion();
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public bool PreviousItemInQueue()
        {
            var update = PlayQueue.Previous();
            if (update)
            {
                // Fix divergence (VENDORED.md): null-guarded (see NextItemInQueue).
                RunTimeTicks = _libraryManager.GetItemById(PlayQueue.GetPlayingItemId())?.RunTimeTicks ?? 0;
                RestartCurrentItem();
                BumpStateVersion();
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public void SetRepeatMode(GroupRepeatMode mode)
        {
            PlayQueue.SetRepeatMode(mode);
            BumpStateVersion();
        }

        /// <inheritdoc />
        public void SetShuffleMode(GroupShuffleMode mode)
        {
            PlayQueue.SetShuffleMode(mode);
            BumpStateVersion();
        }

        /// <inheritdoc />
        public PlayQueueUpdate GetPlayQueueUpdate(PlayQueueUpdateReason reason)
        {
            var startPositionTicks = PositionTicks;
            var isPlaying = _state.Type.Equals(GroupStateType.Playing);

            if (isPlaying)
            {
                var currentTime = DateTime.UtcNow;
                var elapsedTime = currentTime - LastActivity;
                // Elapsed time is negative if event happens
                // during the delay added to account for latency.
                // In this phase clients haven't started the playback yet.
                // In other words, LastActivity is in the future,
                // when playback unpause is supposed to happen.
                // Adjust ticks only if playback actually started.
                startPositionTicks += Math.Max(elapsedTime.Ticks, 0);
            }

            return new PlayQueueUpdate(
                reason,
                PlayQueue.LastChange,
                PlayQueue.GetPlaylist(),
                PlayQueue.PlayingItemIndex,
                startPositionTicks,
                isPlaying,
                PlayQueue.ShuffleMode,
                PlayQueue.RepeatMode);
        }
    }
}
