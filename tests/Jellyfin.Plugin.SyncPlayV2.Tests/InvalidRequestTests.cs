using System;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// A request that changes nothing (a selection naming no entry, an empty
/// queue, play with nothing queued) leaves the group as it was.
/// </summary>
public class InvalidRequestTests
{
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    [Fact]
    public void ASelectionNamingNoEntryWhilePlayingLeavesTheClockAlone()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        var item = harness.PlaylistItemId;
        var position = harness.Group.PositionTicks;
        var runtime = harness.Group.RunTimeTicks;

        harness.Group.HandleRequest(a.Session, new SetPlaylistItemGroupRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(item, harness.PlaylistItemId);
        Assert.Equal(position, harness.Group.PositionTicks);
        Assert.Equal(runtime, harness.Group.RunTimeTicks);
    }

    [Fact]
    public void AnInvalidSelectionOrQueueDuringAWaitKeepsTheWait()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        b.Buffer(Minute);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        harness.Group.HandleRequest(a.Session, new SetPlaylistItemGroupRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, harness.State);
        harness.Group.HandleRequest(a.Session, new PlayGroupRequest(Array.Empty<Guid>(), 0, 0), CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        b.Ready(Minute);
        Assert.Equal(GroupStateType.Playing, harness.State);
    }

    [Fact]
    public void AnInvalidSelectionDuringAPausedWaitDoesNotMakeItResume()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        a.Pause();
        var position = harness.Group.PositionTicks;
        b.Buffer(position, isPlaying: false);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        harness.Group.HandleRequest(a.Session, new SetPlaylistItemGroupRequest(Guid.NewGuid()), CancellationToken.None);

        b.Ready(position);
        Assert.Equal(GroupStateType.Paused, harness.State);
    }

    [Fact]
    public void PlayWithNothingQueuedKeepsTheGroupIdle()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        Assert.Equal(GroupStateType.Idle, harness.State);
        a.Forget();

        a.Unpause();

        Assert.Equal(GroupStateType.Idle, harness.State);
        Assert.Contains(a.Commands, command => command.Command == "Stop");
        Assert.False(a.IsListedBuffering);
    }
}
