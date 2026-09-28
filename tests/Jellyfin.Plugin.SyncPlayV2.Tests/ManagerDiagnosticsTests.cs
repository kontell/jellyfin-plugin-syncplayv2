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
    public void TheReportCarriesTheCountersAndEveryGroup()
    {
        var (a, _) = Session("a");
        var (c, _) = Session("c");
        _manager.NewGroup(a, new NewGroupRequest("one"), CancellationToken.None);
        _manager.NewGroup(c, new NewGroupRequest("two"), CancellationToken.None);

        var report = _manager.GetDiagnostics();

        Assert.Equal(new[] { "one", "two" }, report.Groups.Select(g => g.GroupName).OrderBy(n => n));
        Assert.Equal(_counters.Since, report.Counters.Since);
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
        _versions.RegisterHello(Client, session.DeviceId, 1, externalContent: true);
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
