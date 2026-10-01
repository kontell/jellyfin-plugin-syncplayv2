using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Diagnostics;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests.Harness;

/// <summary>
/// A <see cref="Group"/> with no server around it.
///
/// Group only needs its collaborators for three things: a user per member
/// (the access check), a library item per queue entry (the access check and
/// the runtime), and somewhere to deliver messages. The first two are taken
/// out of the picture by queueing an external-content entry, which the
/// engine resolves from its own <see cref="ContentTable"/> and never asks
/// the library about; the third is <see cref="Sender"/>'s own path, through
/// a session controller that records instead of sending. Everything else is
/// the real engine: the real states, the real Sender, the real wire DTOs.
///
/// What it does not model is the manager: no buffering grace, no sweep
/// timer, no session events. Scenarios that depend on those drive the same
/// Group calls the manager would make (see <see cref="Member.TimeOut"/>).
/// </summary>
internal sealed class GroupHarness
{
    private const string Client = "harness";

    private readonly ProtocolVersionRegistry _versions = new();
    private readonly ISessionManager _sessionManager = NullService<ISessionManager>.Create();

    public GroupHarness()
    {
        Group = new Group(
            NullLoggerFactory.Instance,
            NullService<IUserManager>.Create(),
            _sessionManager,
            NullService<ILibraryManager>.Create(),
            new Sender(NullLogger<Sender>.Instance),
            _versions,
            Counters);
    }

    public EngineCounters Counters { get; } = new();

    public Group Group { get; }

    /// <summary>Gets the queue entry every scenario plays.</summary>
    public Guid Item { get; } = Guid.NewGuid();

    /// <summary>Gets the second queue entry, for scenarios that change items.</summary>
    public Guid SecondItem { get; } = Guid.NewGuid();

    public Guid PlaylistItemId => Group.PlayQueue.GetPlayingItemPlaylistId();

    public GroupStateType State => Group.State;

    /// <summary>
    /// Adds a member. The first one creates the group. Every member's device
    /// declares the external-content capability, which is what lets the
    /// scenario queue skip the library.
    /// </summary>
    public Member Join(string name, int protocolVersion = 1)
    {
        var controller = new RecordingController();
        var session = new SessionInfo(_sessionManager, NullLogger.Instance)
        {
            Id = name,
            UserId = Guid.NewGuid(),
            UserName = name,
            DeviceId = name + "-device",
            Client = Client,
            SessionControllers = new ISessionController[] { controller },
        };

        _versions.RegisterHello(Client, session.DeviceId, protocolVersion, externalContent: true);

        if (Group.GroupName is null)
        {
            Group.CreateGroup(session, new NewGroupRequest("harness"), CancellationToken.None);
        }
        else
        {
            Group.SessionJoin(session, new JoinGroupRequest(Group.GroupId), CancellationToken.None);
        }

        return new Member(this, session, controller);
    }

    /// <summary>Queues <see cref="Item"/> and asks the group to play it.</summary>
    public void Play(Member by, long runTimeTicks = TimeSpan.TicksPerHour)
    {
        Assert.True(ContentDescriptor.TryCreate("harness", "item", "Item", runTimeTicks, null, out var descriptor));
        Group.Content.Register(new Dictionary<Guid, ContentDescriptor> { [Item] = descriptor! });
        Group.HandleRequest(by.Session, new PlayGroupRequest(new[] { Item }, 0, 0), CancellationToken.None);
    }

    /// <summary>
    /// Queues <see cref="Item"/> then <see cref="SecondItem"/>, plays the
    /// first and brings every member to Playing, then forgets what was sent.
    /// </summary>
    public void StartPlayingTwoItems(IReadOnlyList<Member> members)
    {
        Assert.True(ContentDescriptor.TryCreate("harness", "item", "Item", TimeSpan.TicksPerHour, null, out var first));
        Assert.True(ContentDescriptor.TryCreate("harness", "second", "Second", TimeSpan.TicksPerHour, null, out var second));
        Group.Content.Register(new Dictionary<Guid, ContentDescriptor> { [Item] = first!, [SecondItem] = second! });
        Group.HandleRequest(members[0].Session, new PlayGroupRequest(new[] { Item, SecondItem }, 0, 0), CancellationToken.None);
        foreach (var member in members)
        {
            member.Ready();
        }

        Assert.Equal(GroupStateType.Playing, State);
        foreach (var member in members)
        {
            member.Forget();
        }
    }

    /// <summary>
    /// Plays <see cref="Item"/> and brings every member to Playing at
    /// <paramref name="positionTicks"/>, then forgets what was sent on the way.
    /// </summary>
    public void StartPlaying(IReadOnlyList<Member> members, long positionTicks = 0)
    {
        Play(members[0]);
        foreach (var member in members)
        {
            member.Ready();
        }

        if (positionTicks > 0)
        {
            members[0].Seek(positionTicks);
            foreach (var member in members)
            {
                member.Ready(positionTicks);
            }
        }

        Assert.Equal(GroupStateType.Playing, State);
        foreach (var member in members)
        {
            member.Forget();
        }
    }
}

/// <summary>One member of a <see cref="GroupHarness"/> group, and what it received.</summary>
internal sealed class Member
{
    private readonly GroupHarness _harness;
    private readonly RecordingController _controller;

    public Member(GroupHarness harness, SessionInfo session, RecordingController controller)
    {
        _harness = harness;
        Session = session;
        _controller = controller;
    }

    public SessionInfo Session { get; }

    public IReadOnlyList<WireSendCommand> Commands
        => _controller.Sent.Select(sent => sent.Data).OfType<WireSendCommand>().ToList();

    public IReadOnlyList<WireGroupUpdate> Updates
        => _controller.Sent.Select(sent => sent.Data).OfType<WireGroupUpdate>().ToList();

    public void Forget() => _controller.Sent.Clear();

    public void Ready(long positionTicks = 0, bool isPlaying = false)
        => Send(new ReadyGroupRequest(DateTime.UtcNow, positionTicks, isPlaying, _harness.PlaylistItemId));

    /// <summary>
    /// Reports a stall. <paramref name="playlistItemId"/> other than the
    /// group's current entry is a client still on an item the group left.
    /// </summary>
    public void Buffer(long positionTicks = 0, bool isPlaying = true, Guid? playlistItemId = null)
        => Send(new BufferGroupRequest(DateTime.UtcNow, positionTicks, isPlaying, playlistItemId ?? _harness.PlaylistItemId));

    /// <summary>Gets whether the group lists this member as buffering.</summary>
    public bool IsListedBuffering
        => _harness.Group.GetWireInfo(false).Members.Single(member => member.UserName == Session.UserName).IsBuffering;

    public void Seek(long positionTicks) => Send(new SeekGroupRequest(positionTicks));

    public void Ping(long milliseconds) => Send(new PingGroupRequest(milliseconds));

    public void Pause() => Send(new PauseGroupRequest());

    public void Unpause() => Send(new UnpauseGroupRequest());

    public void Stop() => Send(new StopGroupRequest());

    public void NextItem() => Send(new NextItemGroupRequest(_harness.PlaylistItemId));

    /// <summary>Removes the group's playing entry from the queue.</summary>
    public void RemovePlayingEntry()
        => Send(new RemoveFromPlaylistGroupRequest(new[] { _harness.PlaylistItemId }, false, false));

    /// <summary>
    /// The wire's SetIgnoreWait, attributed to the member the way
    /// SyncPlayManagerV2.HandleRequest attributes it.
    /// </summary>
    public void IgnoreWait(bool ignoreWait)
    {
        _harness.Group.RecordIgnoreWaitByRequest(Session, ignoreWait);
        Send(new IgnoreWaitGroupRequest(ignoreWait));
    }

    /// <summary>
    /// What SyncPlayManagerV2.IgnoreStalledMembers does to a member that kept
    /// the group waiting past GroupWaitTimeout (HotJoin enabled, the default).
    /// </summary>
    public void TimeOut()
    {
        var group = _harness.Group;
        if (group.IsV2Member(Session.Id))
        {
            group.RendezvousMember(Session, "harness wait timeout", CancellationToken.None);
        }
        else
        {
            group.MarkIgnoredByTimeout(Session);
        }

        group.HandleRequest(Session, new IgnoreWaitGroupRequest(true), CancellationToken.None);
    }

    private void Send(IGroupPlaybackRequest request)
        => _harness.Group.HandleRequest(Session, request, CancellationToken.None);
}

/// <summary>A session controller that keeps what it is asked to send.</summary>
internal sealed class RecordingController : ISessionController
{
    public List<(SessionMessageType Type, object? Data)> Sent { get; } = new();

    public bool IsSessionActive => true;

    public bool SupportsMediaControl => false;

    public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
    {
        Sent.Add((name, data));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An interface implementation that answers every call with its return
/// type's default (a completed task for Task). Public and unsealed because
/// <see cref="DispatchProxy"/> requires it.
/// </summary>
/// <typeparam name="T">The interface.</typeparam>
public class NullService<T> : DispatchProxy
    where T : class
{
    public static T Create() => Create<T, NullService<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
