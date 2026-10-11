using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// A Next at the last entry and a Previous at the first have no entry to
/// move to. The group is left as it was: no state change, and so no
/// StateVersion a member was not sent, which a v2 member reads off the next
/// beacon as an update it missed.
/// </summary>
public class QueueEdgeStepTests
{
    [Fact]
    public void ANextAtTheLastEntryLeavesAPlayingGroupAsItWas()
    {
        // What a client sends when the last entry plays out.
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b });
        var item = harness.PlaylistItemId;
        var version = BeaconVersion(harness, b);
        a.Forget();
        b.Forget();

        a.NextItem();

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(item, harness.PlaylistItemId);
        Assert.Empty(a.Commands);
        Assert.Empty(a.Updates);
        Assert.Empty(b.Commands);
        Assert.Empty(b.Updates);
        Assert.Equal(version, BeaconVersion(harness, b));
    }

    [Fact]
    public void APreviousAtTheFirstEntryLeavesAPlayingGroupAsItWas()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlayingTwoItems(new[] { a, b });
        var item = harness.PlaylistItemId;
        var version = BeaconVersion(harness, b);
        a.Forget();
        b.Forget();

        a.PreviousItem();

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(item, harness.PlaylistItemId);
        Assert.Empty(a.Commands);
        Assert.Empty(a.Updates);
        Assert.Empty(b.Commands);
        Assert.Empty(b.Updates);
        Assert.Equal(version, BeaconVersion(harness, b));
    }

    [Fact]
    public void ANextAtTheLastEntryLeavesAPausedGroupAsItWas()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b });
        a.Pause();
        Assert.Equal(GroupStateType.Paused, harness.State);
        var version = harness.Group.GetDiagnostics().StateVersion;
        a.Forget();
        b.Forget();

        a.NextItem();

        Assert.Equal(GroupStateType.Paused, harness.State);
        Assert.Empty(a.Commands);
        Assert.Empty(b.Commands);
        Assert.Empty(b.Updates);
        Assert.Equal(version, harness.Group.GetDiagnostics().StateVersion);
    }

    [Fact]
    public void ANextAtTheLastEntryLeavesAnIdleGroupAsItWas()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b });
        a.Stop();
        Assert.Equal(GroupStateType.Idle, harness.State);
        var version = harness.Group.GetDiagnostics().StateVersion;
        a.Forget();
        b.Forget();

        a.NextItem();

        Assert.Equal(GroupStateType.Idle, harness.State);
        Assert.Empty(b.Commands);
        Assert.Empty(b.Updates);
        Assert.Equal(version, harness.Group.GetDiagnostics().StateVersion);
    }

    [Theory]
    [InlineData(GroupRepeatMode.RepeatOne)]
    [InlineData(GroupRepeatMode.RepeatAll)]
    public void ANextAtTheLastEntryOfARepeatingQueueStillMovesTheGroup(GroupRepeatMode mode)
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b });
        a.Repeat(mode);
        a.Forget();
        b.Forget();

        a.NextItem();

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Contains(b.Updates, update => update.Type == "PlayQueue");
    }

    [Theory]
    [InlineData(GroupRepeatMode.RepeatOne)]
    [InlineData(GroupRepeatMode.RepeatAll)]
    public void APreviousAtTheFirstEntryOfARepeatingQueueStillMovesTheGroup(GroupRepeatMode mode)
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlayingTwoItems(new[] { a, b });
        a.Repeat(mode);
        a.Forget();
        b.Forget();

        a.PreviousItem();

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Contains(b.Updates, update => update.Type == "PlayQueue");
    }

    [Fact]
    public void ANextBeforeTheLastEntryStillMovesTheGroup()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlayingTwoItems(new[] { a, b });
        var first = harness.PlaylistItemId;

        a.NextItem();

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.NotEqual(first, harness.PlaylistItemId);
        Assert.Contains(b.Updates, update => update.Type == "PlayQueue");
    }

    /// <summary>The StateVersion a v2 member reads off the group's next position beacon.</summary>
    private static long BeaconVersion(GroupHarness harness, Member member)
    {
        harness.Group.SendPositionBeaconIfDue(TimeSpan.Zero, CancellationToken.None);
        return member.Updates.Last(update => update.Type == "PositionBeacon").StateVersion;
    }
}
