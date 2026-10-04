using System;
using System.Linq;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// A wait that ends paused: the position stays where the wait froze it, and
/// everyone is sent the Pause and the Paused state.
/// </summary>
public class WaitExitTests
{
    private static readonly long At100 = TimeSpan.FromSeconds(100).Ticks;

    [Fact]
    public void APauseDuringAWaitFromPlayingEndsPausedWhereTheWaitFroze()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, At100);
        b.Buffer(At100, isPlaying: true);
        Assert.Equal(GroupStateType.Waiting, harness.State);
        var frozen = harness.Group.PositionTicks;

        // Ten seconds of waiting, without sleeping.
        harness.Group.LastActivity = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        a.Pause();
        a.Forget();

        b.Ready(frozen, isPlaying: false);

        Assert.Equal(GroupStateType.Paused, harness.State);
        Assert.InRange(harness.Group.PositionTicks, frozen - (TimeSpan.TicksPerSecond / 2), frozen + (TimeSpan.TicksPerSecond / 2));
        AssertToldPaused(a);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWaitFromPausedThatNoReadyEndsTellsEveryoneItIsPaused(bool leaves)
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, At100);
        a.Pause();
        var position = harness.Group.PositionTicks;
        b.Buffer(position, isPlaying: false);
        Assert.Equal(GroupStateType.Waiting, harness.State);
        a.Forget();

        if (leaves)
        {
            b.Leave();
        }
        else
        {
            b.IgnoreWait(true);
        }

        Assert.Equal(GroupStateType.Paused, harness.State);
        Assert.Equal(position, harness.Group.PositionTicks);
        AssertToldPaused(a);
    }

    [Fact]
    public void AStaleItemStallFromPausedTellsTheOthersTheGroupWaits()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, At100);
        a.Pause();
        var position = harness.Group.PositionTicks;
        a.Forget();

        b.Buffer(position, isPlaying: false, playlistItemId: Guid.NewGuid());

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Contains(a.Updates, update => IsState(update, GroupStateType.Waiting));
        b.Ready(position);
        Assert.Equal(GroupStateType.Paused, harness.State);
        Assert.Equal(position, harness.Group.PositionTicks);
    }

    private static bool IsState(WireGroupUpdate update, GroupStateType state)
        => update.Type == "StateUpdate" && update.Data is GroupStateUpdate { } stateUpdate && stateUpdate.State == state;

    private static void AssertToldPaused(Member member)
    {
        Assert.Contains(member.Commands, command => command.Command == "Pause");
        Assert.Contains(member.Updates, update => IsState(update, GroupStateType.Paused)
            && ((GroupStateUpdate)update.Data!).Reason == PlaybackRequestType.Ready);
    }
}
