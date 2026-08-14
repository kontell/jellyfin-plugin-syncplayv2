using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// M0 spike stand-in for the core SyncPlayManager. Registered after the core's
/// ISyncPlayManager so DI last-wins resolution shadows it. Implements the stock
/// interface with a minimal in-memory group registry — just enough that:
/// the stock /SyncPlay/* controller demonstrably drives this instance, the
/// SyncPlayIsInGroup policy handler demonstrably consults IsUserActive here,
/// SessionEnded/SessionControllerConnected demonstrably reach us, and joins
/// emit the v2 wire shape (plugin DTO with StateVersion/Members/ProtocolVersion)
/// through ISessionController.SendMessage under a stock MessageType.
/// </summary>
public class SpikeSyncPlayManager : ISyncPlayManager
{
    private readonly ILogger<SpikeSyncPlayManager> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly SpikeDiagnostics _diag;

    private readonly ConcurrentDictionary<Guid, SpikeGroup> _groups = new();
    private readonly ConcurrentDictionary<string, Guid> _sessionToGroup = new(StringComparer.OrdinalIgnoreCase);
    private long _stateVersion;

    public SpikeSyncPlayManager(
        ILogger<SpikeSyncPlayManager> logger,
        ISessionManager sessionManager,
        SpikeDiagnostics diag)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _diag = diag;

        _sessionManager.SessionEnded += OnSessionEnded;
        _sessionManager.SessionControllerConnected += OnSessionControllerConnected;

        _diag.Add("manager", "SpikeSyncPlayManager constructed; subscribed SessionEnded + SessionControllerConnected");
        _logger.LogInformation("[SyncPlayV2 spike] manager constructed, core SyncPlayManager is shadowed");
    }

    private sealed class SpikeGroup
    {
        public Guid Id { get; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        // sessionId -> (userId, userName)
        public ConcurrentDictionary<string, (Guid UserId, string UserName)> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public GroupInfoDto NewGroup(SessionInfo session, NewGroupRequest request, CancellationToken cancellationToken)
    {
        var group = new SpikeGroup { Name = request.GroupName };
        group.Members[session.Id] = (session.UserId, session.UserName);
        _groups[group.Id] = group;
        _sessionToGroup[session.Id] = group.Id;
        Interlocked.Increment(ref _stateVersion);

        _diag.Add("controller-call", $"NewGroup('{request.GroupName}') from session {session.Id} user {session.UserName}");

        SendGroupUpdate(session, group, "GroupJoined", BuildInfo(group));
        return ToStockInfo(group);
    }

    /// <inheritdoc />
    public void JoinGroup(SessionInfo session, JoinGroupRequest request, CancellationToken cancellationToken)
    {
        if (!_groups.TryGetValue(request.GroupId, out var group))
        {
            _diag.Add("controller-call", $"JoinGroup({request.GroupId}) from {session.Id}: group does not exist");
            SendGroupUpdate(session, null, "GroupDoesNotExist", request.GroupId.ToString());
            return;
        }

        group.Members[session.Id] = (session.UserId, session.UserName);
        _sessionToGroup[session.Id] = group.Id;
        Interlocked.Increment(ref _stateVersion);

        _diag.Add("controller-call", $"JoinGroup({request.GroupId}) from session {session.Id} user {session.UserName}");

        SendGroupUpdate(session, group, "GroupJoined", BuildInfo(group));
        foreach (var otherSessionId in group.Members.Keys.Where(id => !string.Equals(id, session.Id, StringComparison.OrdinalIgnoreCase)))
        {
            SendGroupUpdateToSessionId(otherSessionId, group, "UserJoined", session.UserName);
        }
    }

    /// <inheritdoc />
    public void LeaveGroup(SessionInfo session, LeaveGroupRequest request, CancellationToken cancellationToken)
    {
        _diag.Add("controller-call", $"LeaveGroup from session {session.Id}");
        RemoveFromGroup(session.Id, session.UserName, "leave");
    }

    /// <inheritdoc />
    public List<GroupInfoDto> ListGroups(SessionInfo session, ListGroupsRequest request)
    {
        _diag.Add("controller-call", $"ListGroups from session {session.Id}");
        return _groups.Values.Select(ToStockInfo).ToList();
    }

    /// <inheritdoc />
    public GroupInfoDto GetGroup(SessionInfo session, Guid groupId)
    {
        _diag.Add("controller-call", $"GetGroup({groupId}) from session {session.Id}");
        return _groups.TryGetValue(groupId, out var group) ? ToStockInfo(group) : null!;
    }

    /// <inheritdoc />
    public void HandleRequest(SessionInfo session, IGroupPlaybackRequest request, CancellationToken cancellationToken)
    {
        // Reaching this line means the SyncPlayIsInGroup policy handler asked
        // THIS manager's IsUserActive and got true — that is the evidence.
        _diag.Add("playback-request", $"{request.Action} (type {request.Type}) from session {session.Id}");
    }

    /// <inheritdoc />
    public bool IsUserActive(Guid userId)
    {
        var active = _groups.Values.Any(g => g.Members.Values.Any(m => m.UserId == userId));
        _diag.Add("policy-check", $"IsUserActive({userId}) => {active}");
        return active;
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        _diag.Add("event-session-ended", $"session {e.SessionInfo.Id} user {e.SessionInfo.UserName}");
        RemoveFromGroup(e.SessionInfo.Id, e.SessionInfo.UserName, "session ended");
    }

    private void OnSessionControllerConnected(object? sender, SessionEventArgs e)
    {
        _diag.Add("event-controller-connected", $"session {e.SessionInfo.Id} user {e.SessionInfo.UserName} controllers {e.SessionInfo.SessionControllers.Count}");
    }

    private void RemoveFromGroup(string sessionId, string userName, string reason)
    {
        if (!_sessionToGroup.TryRemove(sessionId, out var groupId) || !_groups.TryGetValue(groupId, out var group))
        {
            return;
        }

        group.Members.TryRemove(sessionId, out _);
        Interlocked.Increment(ref _stateVersion);
        SendGroupUpdateToSessionId(sessionId, group, "GroupLeft", group.Id.ToString());

        if (group.Members.IsEmpty)
        {
            _groups.TryRemove(group.Id, out _);
            _diag.Add("group", $"group '{group.Name}' emptied ({reason}) and removed");
            return;
        }

        foreach (var otherSessionId in group.Members.Keys)
        {
            SendGroupUpdateToSessionId(otherSessionId, group, "UserLeft", userName);
        }
    }

    private SpikeGroupInfo BuildInfo(SpikeGroup group)
    {
        return new SpikeGroupInfo
        {
            GroupId = group.Id,
            GroupName = group.Name,
            State = "Idle",
            Participants = group.Members.Values.Select(m => m.UserName).Distinct().ToList(),
            LastUpdatedAt = DateTime.UtcNow,
            Members = group.Members.Values
                .Select(m => new SpikeMemberStatus { UserName = m.UserName })
                .ToList(),
        };
    }

    private static GroupInfoDto ToStockInfo(SpikeGroup group)
    {
        var participants = group.Members.Values.Select(m => m.UserName).Distinct().ToList();
        return new GroupInfoDto(group.Id, group.Name, GroupStateType.Idle, participants, DateTime.UtcNow);
    }

    private void SendGroupUpdate(SessionInfo session, SpikeGroup? group, string type, object? data)
        => Send(session, new SpikeGroupUpdate
        {
            GroupId = group?.Id ?? Guid.Empty,
            Type = type,
            StateVersion = Interlocked.Read(ref _stateVersion),
            Data = data,
        });

    private void SendGroupUpdateToSessionId(string sessionId, SpikeGroup group, string type, object? data)
    {
        var session = _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            _diag.Add("send", $"no live session {sessionId} for {type}, dropped");
            return;
        }

        SendGroupUpdate(session, group, type, data);
    }

    /// <summary>
    /// The load-bearing send path: a plugin-defined payload pushed through the
    /// session's controllers under a stock SessionMessageType. This is the
    /// mechanism §4.4 of the feasibility study claims produces byte-identical
    /// wire messages.
    /// </summary>
    internal void Send<T>(SessionInfo session, T payload, SessionMessageType messageType = SessionMessageType.SyncPlayGroupUpdate)
    {
        var controllers = session.SessionControllers;
        if (controllers.Count == 0)
        {
            _diag.Add("send", $"session {session.Id} has no session controllers; {messageType} dropped");
            return;
        }

        foreach (var controller in controllers)
        {
            _ = controller.SendMessage(messageType, Guid.NewGuid(), payload, CancellationToken.None)
                .ContinueWith(
                    t => _diag.Add("send", $"{messageType} to {session.Id} faulted: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }

        _diag.Add("send", $"{messageType} pushed to session {session.Id} via {controllers.Count} controller(s)");
    }
}
