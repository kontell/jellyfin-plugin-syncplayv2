using System;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// Next and Previous name the entry the client thinks is playing, so that two
/// members pressing Next at once move the group one item, not two. A request
/// for an entry the group has left is dropped; it must not stop the group.
/// </summary>
public class StaleQueueRequestTests
{
    [Fact]
    public void AStaleNextFromAPlayingGroupKeepsItPlaying()
    {
        // Two members press Next; the group has moved on and is playing again
        // by the time the second request arrives.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;

        a.NextItem();
        a.Ready();
        b.Ready();
        Assert.Equal(GroupStateType.Playing, harness.State);
        var second = harness.PlaylistItemId;
        a.Forget();
        b.Forget();

        b.NextItem(first);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(second, harness.PlaylistItemId);
        Assert.Empty(a.Commands);
        Assert.Empty(b.Commands);
    }

    [Fact]
    public void AStalePreviousFromAPlayingGroupKeepsItPlaying()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;
        a.NextItem();
        a.Ready();
        b.Ready();
        var second = harness.PlaylistItemId;

        b.PreviousItem(first);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(second, harness.PlaylistItemId);
    }

    [Fact]
    public void AStaleNextFromAPausedGroupKeepsItPaused()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;
        a.NextItem();
        a.Ready();
        b.Ready();
        a.Pause();
        Assert.Equal(GroupStateType.Paused, harness.State);

        b.NextItem(first);

        Assert.Equal(GroupStateType.Paused, harness.State);
    }

    [Fact]
    public void AStalePreviousFromAPausedGroupKeepsItPaused()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;
        a.NextItem();
        a.Ready();
        b.Ready();
        a.Pause();
        var second = harness.PlaylistItemId;

        b.PreviousItem(first);

        Assert.Equal(GroupStateType.Paused, harness.State);
        Assert.Equal(second, harness.PlaylistItemId);
    }

    [Fact]
    public void AStaleNextFromAnIdleGroupKeepsItIdle()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;
        a.NextItem();
        a.Ready();
        b.Ready();
        a.Stop();
        Assert.Equal(GroupStateType.Idle, harness.State);

        b.NextItem(first);

        Assert.Equal(GroupStateType.Idle, harness.State);
    }

    [Fact]
    public void ACurrentNextStillMovesTheGroup()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;

        b.NextItem(first);

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.NotEqual(first, harness.PlaylistItemId);
    }
}
