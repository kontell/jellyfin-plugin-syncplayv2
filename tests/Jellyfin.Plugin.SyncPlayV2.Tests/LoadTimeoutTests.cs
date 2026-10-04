using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// Which timeout the group gives a member it is waiting for: the load
/// timeout when it is loading an item because the group asked, the stall
/// timeout for everything else.
/// </summary>
public class LoadTimeoutTests
{
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    // Far enough apart that "past the stall timeout, within the load timeout"
    // is unambiguous on a loaded CI runner.
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Load = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan PastStall = TimeSpan.FromMilliseconds(450);

    [Fact]
    public void AMemberLoadingANewItemGetsTheLoadTimeout()
    {
        // Measured on a 12.1 server: both jellyfin-web members were still
        // loading a HEVC item when the 10 s wait ran out, and the group went
        // to Playing with nobody ready.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");

        harness.Play(a);
        a.Ready();
        Thread.Sleep(PastStall);

        Assert.Empty(harness.Group.GetStalledMembers(Stall, Load));

        Thread.Sleep(Load);
        var stalled = Assert.Single(harness.Group.GetStalledMembers(Stall, Load));
        Assert.Equal(b.Session.Id, stalled.Session.Id);
        Assert.Equal(Load, stalled.Timeout);
    }

    [Fact]
    public void ASeekWithinTheItemKeepsTheStallTimeout()
    {
        // Deliberately unchanged: the wait timeout is also what hands a v2
        // member that cannot seek accurately to the rendezvous path, and
        // that was measured and tuned against the stall timeout.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        a.Seek(2 * Minute);
        a.Ready(2 * Minute);
        Thread.Sleep(PastStall);

        var stalled = Assert.Single(harness.Group.GetStalledMembers(Stall, Load));
        Assert.Equal(b.Session.Id, stalled.Session.Id);
        Assert.Equal(Stall, stalled.Timeout);
    }

    [Fact]
    public void AMembersOwnStallKeepsTheStallTimeout()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        Assert.Equal(GroupStateType.Waiting, harness.State);
        Thread.Sleep(PastStall);

        var stalled = Assert.Single(harness.Group.GetStalledMembers(Stall, Load));
        Assert.Equal(Stall, stalled.Timeout);
    }

    [Fact]
    public void AJoinerLoadingTheGroupsItemGetsTheLoadTimeout()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, Minute);

        var b = harness.Join("b");
        Assert.Equal(GroupStateType.Waiting, harness.State);
        Thread.Sleep(PastStall);

        Assert.DoesNotContain(harness.Group.GetStalledMembers(Stall, Load), stalled => stalled.Session.Id == b.Session.Id);
    }

    [Fact]
    public void RestartingTheItemFromIdleIsALoad()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        // Restarting the item from Idle is the same group-wide load a new
        // item is; Stop then Unpause reaches it without a second queue entry.
        a.Stop();
        a.Unpause();
        Assert.Equal(GroupStateType.Waiting, harness.State);
        a.Ready();
        Thread.Sleep(PastStall);

        Assert.Empty(harness.Group.GetStalledMembers(Stall, Load));
        Assert.Contains(harness.Group.GetStalledMembers(Stall, Stall), stalled => stalled.Session.Id == b.Session.Id);
    }

    [Fact]
    public void AJoinerThatNeverLoadsTimesOutOnTheLoadTimeout()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, Minute);

        var b = harness.Join("b");
        Thread.Sleep(Load + TimeSpan.FromMilliseconds(100));

        var stalled = Assert.Single(harness.Group.GetStalledMembers(Stall, Load));
        Assert.Equal(b.Session.Id, stalled.Session.Id);
        Assert.Equal(Load, stalled.Timeout);
    }

    [Fact]
    public void TheNextItemIsALoad()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });

        a.NextItem();
        Assert.Equal(GroupStateType.Waiting, harness.State);
        a.Ready();
        Thread.Sleep(PastStall);

        Assert.Empty(harness.Group.GetStalledMembers(Stall, Load));
    }

    [Fact]
    public void RemovingThePlayingEntryDoesNotMakeTheNextSeekALoad()
    {
        // Removing the playing entry is a load (everyone loads the next one);
        // a Seek within that wait restarts it as a seek, deliberately on the
        // stall timeout: the removal's load mark does not reach it.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlayingTwoItems(new[] { a, b });

        a.RemovePlayingEntry();
        a.Seek(Minute);
        a.Ready(Minute);
        Thread.Sleep(PastStall);

        var stalled = Assert.Single(harness.Group.GetStalledMembers(Stall, Load));
        Assert.Equal(b.Session.Id, stalled.Session.Id);
        Assert.Equal(Stall, stalled.Timeout);
    }
}
