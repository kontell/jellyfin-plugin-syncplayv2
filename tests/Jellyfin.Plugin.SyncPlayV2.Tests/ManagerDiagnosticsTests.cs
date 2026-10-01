using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Diagnostics;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The counters the manager owns — the buffering grace lives there, not in
/// Group — driven through the real SyncPlayManagerV2, sweep timer included.
/// </summary>
public sealed class ManagerDiagnosticsTests : IDisposable
{
    private const string Client = "harness";

    private readonly ProtocolVersionRegistry _versions = new();
    private readonly ISessionManager _sessionManager = NullService<ISessionManager>.Create();
    private readonly EngineCounters _counters = new();
    private readonly SyncPlayManagerV2 _manager;

    public ManagerDiagnosticsTests()
    {
        _manager = new SyncPlayManagerV2(
            NullLoggerFactory.Instance,
            NullService<IUserManager>.Create(),
            _sessionManager,
            NullService<ILibraryManager>.Create(),
            new Sender(NullLogger<Sender>.Instance),
            _versions,
            _counters);
    }

    public void Dispose() => _manager.Dispose();

    [Fact]
    public void ARebufferWithinTheGraceIsCountedAndPausesNobody()
    {
        var (a, aSent) = Session("a");
        var (b, _) = Session("b");
        var group = _manager.NewGroup(a, new NewGroupRequest("manager"), CancellationToken.None);
        _manager.JoinGroup(b, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        var item = Guid.NewGuid();
        Assert.True(ContentDescriptor.TryCreate("harness", "item", "Item", TimeSpan.TicksPerHour, null, out var descriptor));
        _manager.HandleRequestWithContent(
            a,
            new PlayGroupRequest(new[] { item }, 0, 0),
            new Dictionary<Guid, ContentDescriptor> { [item] = descriptor! },
            CancellationToken.None);

        var playlistItemId = PlayingPlaylistItemId(aSent);
        _manager.HandleRequest(a, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playlistItemId), CancellationToken.None);
        Assert.Equal(GroupStateType.Playing, _manager.GetDiagnostics().Groups.Single().State);

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);

        var report = _manager.GetDiagnostics();
        Assert.Equal(1, report.Counters.BufferingRecovered);
        Assert.Equal(0, report.Counters.BufferingApplied);
        Assert.Equal(GroupStateType.Playing, report.Groups.Single().State);
        Assert.Equal(new[] { "a", "b" }, report.Groups.Single().Members.Select(m => m.UserName).OrderBy(n => n));
    }

    [Fact]
    public void TheReportReadsEachMembersAutoplay()
    {
        using var manager = new SyncPlayManagerV2(
            NullLoggerFactory.Instance,
            UserDirectory.Create(true),
            _sessionManager,
            NullService<ILibraryManager>.Create(),
            new Sender(NullLogger<Sender>.Instance),
            _versions,
            _counters);
        var (a, _) = Session("a");
        manager.NewGroup(a, new NewGroupRequest("autoplay"), CancellationToken.None);

        Assert.True(manager.GetDiagnostics().Groups.Single().Members.Single().AutoplayNextEpisode);
    }

    [Fact]
    public void TheReportCarriesTheCountersAndEveryGroup()
    {
        var (a, _) = Session("a");
        var (c, _) = Session("c");
        _manager.NewGroup(a, new NewGroupRequest("one"), CancellationToken.None);
        _manager.NewGroup(c, new NewGroupRequest("two"), CancellationToken.None);

        var report = _manager.GetDiagnostics();

        Assert.Equal(new[] { "one", "two" }, report.Groups.Select(g => g.GroupName).OrderBy(n => n));
        Assert.Equal(_counters.Since, report.Counters.Since);
        Assert.Equal(typeof(ISessionManager).Assembly.GetName().Version?.ToString(3), report.ServerVersion);
    }

    // The shipped grace (2 s) plus the sweep's 1 s period and some slack.
    private static readonly TimeSpan PastTheGrace = TimeSpan.FromSeconds(3.5);

    [Fact]
    public void ARebufferPastTheGracePausesTheGroupAndIsCountedOnce()
    {
        var (_, b, playlistItemId) = StartPlaying();

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        WaitUntil(() => _manager.GetDiagnostics().Groups.Single().State == GroupStateType.Waiting);

        var report = _manager.GetDiagnostics();
        Assert.Equal(GroupStateType.Waiting, report.Groups.Single().State);
        Assert.Equal(1, report.Counters.BufferingApplied);
        Assert.Equal(0, report.Counters.BufferingRecovered);
    }

    [Fact]
    public void ARebufferTheGraceSparesIsInTheHistoryBeforeItsReady()
    {
        // The held-back Buffer never reaches the group when Ready cancels it.
        var (_, b, playlistItemId) = StartPlaying();

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);

        var events = _manager.GetDiagnostics().Groups.Single().History
            .Where(e => e.Member == "b")
            .Select(e => e.Event)
            .Take(2)
            .ToList();
        Assert.Equal(new[] { "Ready", "Buffer held back" }, events);
    }

    [Fact]
    public void ARebufferPastTheGraceIsHeldBackOnceAndAppliedOnce()
    {
        var (_, b, playlistItemId) = StartPlaying();

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        WaitUntil(() => _manager.GetDiagnostics().Groups.Single().State == GroupStateType.Waiting);

        var history = _manager.GetDiagnostics().Groups.Single().History;
        Assert.Single(history, e => e.Event == "Buffer held back");
        Assert.Single(history, e => e.Event == "Buffer");
    }

    [Fact]
    public void ASpectatorsRebufferIsNeitherAbsorbedNorAppliedByTheGrace()
    {
        // The grace holds back any Buffer while the group plays, but a
        // spectator's stall never pauses the group: counting it as a pause
        // let through, or one the grace spared everyone, is counting a
        // pause that could not happen.
        var (_, b, playlistItemId) = StartPlaying();
        _manager.HandleRequest(b, new IgnoreWaitGroupRequest(true), CancellationToken.None);

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);

        // Still Playing proves nothing until the sweep has applied the Buffer:
        // the group records it only then (the first one, cancelled by the
        // Ready, never reached the group). Both were held back; the history
        // keeps that, where the held-back count can already have expired.
        Assert.Equal(2, _manager.GetDiagnostics().Groups.Single().History.Count(e => e.Event == "Buffer held back" && e.Member == "b"));
        WaitUntil(() => AppliedBuffers("b") == 1);
        Assert.Equal(1, AppliedBuffers("b"));
        Assert.Equal(0, _manager.HeldBackBufferingCount);

        var report = _manager.GetDiagnostics();
        Assert.Equal(GroupStateType.Playing, report.Groups.Single().State);
        Assert.Equal(0, report.Counters.BufferingApplied);
        Assert.Equal(0, report.Counters.BufferingRecovered);
    }

    [Fact]
    public void AStallThatBecomesASpectatorsDuringTheGraceIsNotCountedAsSpared()
    {
        // Held back while it would still have paused the group; by the Ready
        // the member is a spectator, whose stall the group never waits for.
        var (_, b, playlistItemId) = StartPlaying();

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new IgnoreWaitGroupRequest(true), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);

        Assert.Equal(0, _manager.GetDiagnostics().Counters.BufferingRecovered);
    }

    [Fact]
    public void AStallThatStopsBeingASpectatorsDuringTheGraceIsCountedAsSpared()
    {
        // The other way round: by the Ready the member would pause the group
        // again, so the grace did spare everyone a pause.
        var (_, b, playlistItemId) = StartPlaying();
        _manager.HandleRequest(b, new IgnoreWaitGroupRequest(true), CancellationToken.None);

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new IgnoreWaitGroupRequest(false), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);

        Assert.Equal(1, _manager.GetDiagnostics().Counters.BufferingRecovered);
    }

    [Fact]
    public void AClosedGroupKeepsItsHistoryInTheReport()
    {
        // A group closes when its last member leaves, and the members of a
        // group that stalled for good are the ones who leave: its history is
        // what the report most needs then.
        var (a, b, playlistItemId) = StartPlaying();
        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);
        _manager.LeaveGroup(a, new LeaveGroupRequest(), CancellationToken.None);
        _manager.LeaveGroup(b, new LeaveGroupRequest(), CancellationToken.None);

        var report = _manager.GetDiagnostics();

        Assert.Empty(report.Groups);
        var closed = Assert.Single(report.ClosedGroups);
        Assert.Equal("manager", closed.GroupName);
        Assert.NotNull(closed.ClosedSecondsAgo);
        Assert.Equal(new[] { "Left", "Left" }, closed.History.Take(2).Select(e => e.Event));
        Assert.Contains(closed.History, e => e.Event == "Play");
    }

    [Fact]
    public void OnlyTheLastClosedGroupsAreKeptNewestFirst()
    {
        for (var i = 0; i < SyncPlayManagerV2.ClosedGroupsKept + 2; i++)
        {
            var (a, _) = Session("a" + i);
            _manager.NewGroup(a, new NewGroupRequest("group " + i), CancellationToken.None);
            _manager.LeaveGroup(a, new LeaveGroupRequest(), CancellationToken.None);
        }

        var names = _manager.GetDiagnostics().ClosedGroups.Select(g => g.GroupName).ToList();

        Assert.Equal(new[] { "group 4", "group 3", "group 2" }, names);
    }

    [Fact]
    public void ALiveGroupHasNoClosingTime()
    {
        StartPlaying();

        Assert.Null(_manager.GetDiagnostics().Groups.Single().ClosedSecondsAgo);
    }

    [Fact]
    public void AHeldBackBufferIsAppliedAsTheMembersCurrentSession()
    {
        // The member reconnects with a new session instance during the grace
        // (no SessionEnded, so the grace is not cancelled). Applying the Buffer
        // with the instance that reported it addressed the group's replies to
        // the old instance's controllers.
        var (a, aSent) = Session("a");
        var (b, bOld) = Session("b");
        var group = _manager.NewGroup(a, new NewGroupRequest("manager"), CancellationToken.None);
        _manager.JoinGroup(b, new JoinGroupRequest(group.GroupId), CancellationToken.None);
        var item = Guid.NewGuid();
        Assert.True(ContentDescriptor.TryCreate("harness", "item", "Item", TimeSpan.TicksPerHour, null, out var descriptor));
        _manager.HandleRequestWithContent(
            a,
            new PlayGroupRequest(new[] { item }, 0, 0),
            new Dictionary<Guid, ContentDescriptor> { [item] = descriptor! },
            CancellationToken.None);
        var playlistItemId = PlayingPlaylistItemId(aSent);
        _manager.HandleRequest(a, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playlistItemId), CancellationToken.None);

        _manager.HandleRequest(b, new BufferGroupRequest(DateTime.UtcNow, 0, true, playlistItemId), CancellationToken.None);

        var bNew = new RecordingController();
        var reconnected = new SessionInfo(_sessionManager, NullLogger.Instance)
        {
            Id = b.Id,
            UserId = b.UserId,
            UserName = b.UserName,
            DeviceId = b.DeviceId,
            Client = b.Client,
            SessionControllers = new ISessionController[] { bNew },
        };
        _manager.ReattachSession(reconnected);
        var oldBefore = bOld.Sent.Count;
        var newBefore = bNew.Sent.Count;

        WaitUntil(() => _manager.GetDiagnostics().Groups.Single().State == GroupStateType.Waiting);

        // Applying the Buffer tells the group it is waiting for this member.
        Assert.Equal(GroupStateType.Waiting, _manager.GetDiagnostics().Groups.Single().State);
        Assert.Equal(oldBefore, bOld.Sent.Count);
        Assert.Contains(
            bNew.Sent.Skip(newBefore).Select(entry => entry.Data).OfType<WireGroupUpdate>(),
            update => update.Type == "StateUpdate"
                && update.Data is GroupStateUpdate { State: GroupStateType.Waiting, Reason: PlaybackRequestType.Buffer });
    }

    private int AppliedBuffers(string member)
        => _manager.GetDiagnostics().Groups.Single().History.Count(e => e.Event == "Buffer" && e.Member == member);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + PastTheGrace;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
    }

    private (SessionInfo A, SessionInfo B, Guid PlaylistItemId) StartPlaying()
    {
        var (a, aSent) = Session("a");
        var (b, _) = Session("b");
        var group = _manager.NewGroup(a, new NewGroupRequest("manager"), CancellationToken.None);
        _manager.JoinGroup(b, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        var item = Guid.NewGuid();
        Assert.True(ContentDescriptor.TryCreate("harness", "item", "Item", TimeSpan.TicksPerHour, null, out var descriptor));
        _manager.HandleRequestWithContent(
            a,
            new PlayGroupRequest(new[] { item }, 0, 0),
            new Dictionary<Guid, ContentDescriptor> { [item] = descriptor! },
            CancellationToken.None);

        var playlistItemId = PlayingPlaylistItemId(aSent);
        _manager.HandleRequest(a, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playlistItemId), CancellationToken.None);
        _manager.HandleRequest(b, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playlistItemId), CancellationToken.None);
        Assert.Equal(GroupStateType.Playing, _manager.GetDiagnostics().Groups.Single().State);
        return (a, b, playlistItemId);
    }

    private (SessionInfo Session, RecordingController Sent) Session(string name)
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
        _versions.RegisterHello(Client, session.DeviceId, session.UserId, 1, externalContent: true);
        return (session, controller);
    }

    private static Guid PlayingPlaylistItemId(RecordingController sent)
    {
        var update = sent.Sent
            .Select(entry => entry.Data)
            .OfType<WireGroupUpdate>()
            .Last(u => u.Type == "PlayQueue");

        return update.Data switch
        {
            WirePlayQueueUpdate wire => wire.Playlist[wire.PlayingItemIndex].PlaylistItemId,
            PlayQueueUpdate stock => stock.Playlist[stock.PlayingItemIndex].PlaylistItemId,
            _ => throw new InvalidOperationException("unexpected PlayQueue payload " + update.Data?.GetType()),
        };
    }
}
