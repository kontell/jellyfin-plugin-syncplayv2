using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// Removing the playing entry moves the queue on: everyone loads the next
/// entry, and the group waits for them as after a new selection.
/// </summary>
public class QueueRemovalTests
{
    [Fact]
    public void RemovingThePlayingEntryWhilePlayingWaitsForEveryoneThenResumes()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var removed = harness.PlaylistItemId;

        a.RemovePlayingEntry();

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.NotEqual(removed, harness.PlaylistItemId);
        Assert.Equal(0, harness.Group.PositionTicks);
        Assert.True(a.IsListedBuffering);
        Assert.True(b.IsListedBuffering);
        a.Ready();
        Assert.Equal(GroupStateType.Waiting, harness.State);
        b.Ready();
        Assert.Equal(GroupStateType.Playing, harness.State);
    }

    [Fact]
    public void RemovingThePlayingEntryWhilePausedEndsPaused()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        a.Pause();

        a.RemovePlayingEntry();

        Assert.Equal(GroupStateType.Waiting, harness.State);
        a.Ready();
        b.Ready();
        Assert.Equal(GroupStateType.Paused, harness.State);
    }

    [Fact]
    public void RemovingTheOnlyEntryStillStopsTheGroup()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });

        a.RemovePlayingEntry();

        Assert.Equal(GroupStateType.Idle, harness.State);
        Assert.Contains(a.Commands, command => command.Command == "Stop");
    }
}
