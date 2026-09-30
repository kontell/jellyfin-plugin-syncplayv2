using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// Members the group stops waiting for — by timeout or by their own choice —
/// must never leave the group in a Waiting state nobody is waited on in.
/// </summary>
public class StalledMemberTests
{
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    // Short enough to keep the suite fast, long enough that "immediately"
    // cannot be mistaken for "after the timeout" on a loaded CI runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void AMemberTheGroupGaveUpOnThatBuffersAgainIsWaitedForAgain()
    {
        // #14: after the wait timeout the group plays on; the same member's
        // next Buffer moved the group to Waiting, but the member still carried
        // IgnoreGroupWait, so IsBuffering() was false and the sweep skipped it
        // — the group sat in Waiting until someone pressed play twice.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        b.TimeOut();
        Assert.Equal(GroupStateType.Playing, harness.State);

        b.Buffer(Minute);

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.True(harness.Group.IsBuffering(), "the group is not waiting for anyone");
    }

    [Fact]
    public void AMemberTheGroupGaveUpOnGetsAFreshTimeoutWhenItBuffersAgain()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        Thread.Sleep(Timeout + Timeout);
        b.TimeOut();

        b.Buffer(Minute);
        Assert.Empty(harness.Group.GetStalledBufferingSessions(Timeout));

        Thread.Sleep(Timeout + TimeSpan.FromMilliseconds(100));
        Assert.Contains(harness.Group.GetStalledBufferingSessions(Timeout), session => session.Id == b.Session.Id);
    }

    [Fact]
    public void ASpectatorsBufferingDoesNotStopTheGroup()
    {
        // #14, second case: a member that asked not to be waited for
        // ("stop following group playback") is not waited for when it
        // buffers either; moving the group to Waiting for it is a Waiting
        // nobody can end.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.IgnoreWait(true);
        a.Forget();

        b.Buffer(Minute);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.DoesNotContain(a.Commands, command => command.Command == "Pause");
    }

    [Fact]
    public void ANewWaitRestartsTheClockForMembersAlreadyBuffering()
    {
        // A Seek (or a new item) starts a new wait: everyone reloads. A
        // member that happened to be buffering already kept its old
        // BufferingSince, so the sweep could give up on it the moment the
        // new wait began.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        Thread.Sleep(Timeout + Timeout);

        a.Seek(2 * Minute);

        Assert.Empty(harness.Group.GetStalledBufferingSessions(Timeout));

        // Restarted, not cancelled: the member still times out on the new wait.
        Thread.Sleep(Timeout + TimeSpan.FromMilliseconds(100));
        Assert.Contains(harness.Group.GetStalledBufferingSessions(Timeout), session => session.Id == b.Session.Id);
    }

    [Fact]
    public void AMemberTheGroupGaveUpOnThatBuffersOnAStaleItemIsWaitedForAgain()
    {
        // The wrong-item check answers with the current item and returns;
        // the member must be waited for again before it, or the group enters
        // a Waiting that neither IsBuffering() nor the sweep counts it in.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        b.TimeOut();

        b.Buffer(Minute, playlistItemId: Guid.NewGuid());

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.True(harness.Group.IsBuffering(), "the group is not waiting for anyone");

        Thread.Sleep(Timeout + TimeSpan.FromMilliseconds(100));
        Assert.Contains(harness.Group.GetStalledBufferingSessions(Timeout), session => session.Id == b.Session.Id);
    }

    [Fact]
    public void AStallOnAStaleItemPausesAPlayingGroupAndResumesIt()
    {
        // A client still on an item the group has left reports Buffer while
        // the group plays. The wrong-item branch returned before pausing
        // anyone and before setting ResumePlaying: the others played on, and
        // the member's Ready ended the wait in Paused.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute, playlistItemId: Guid.NewGuid());

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Contains(a.Commands, command => command.Command == "Pause");
        Assert.DoesNotContain(b.Commands, command => command.Command == "Pause");

        a.Forget();
        b.Ready(Minute, isPlaying: false);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(a.Commands, command => command.Command == "Unpause");
    }

    [Fact]
    public void AStallOnAStaleItemThatTimesOutResumesThePlayingGroup()
    {
        // The same wait ended by the timeout instead of a Ready: the group
        // gives up on the member and carries on playing, not paused.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute, playlistItemId: Guid.NewGuid());
        a.Forget();
        b.TimeOut();

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(a.Commands, command => command.Command == "Unpause");
    }

    [Fact]
    public void ASpectatorsBufferingDoesNotStopAPausedGroup()
    {
        // Paused moved every Buffer to Waiting unconditionally, a spectator's
        // included, and the group did not wait for it there either.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        a.Pause();
        Assert.Equal(GroupStateType.Paused, harness.State);

        b.IgnoreWait(true);
        b.Buffer(Minute, isPlaying: false);

        Assert.Equal(GroupStateType.Paused, harness.State);
    }

    [Fact]
    public void ASpectatorsBufferingWhileTheGroupWaitsIsNotWaitedFor()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        var c = harness.Join("c");
        harness.StartPlaying(new[] { a, b, c }, Minute);
        c.IgnoreWait(true);

        a.Buffer(Minute);
        Assert.Equal(GroupStateType.Waiting, harness.State);
        c.Buffer(Minute);

        a.Ready(Minute, isPlaying: false);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.False(c.IsListedBuffering);
    }

    [Fact]
    public void ASpectatorsStallLeavesNoBufferingFlagBehind()
    {
        // Nothing outside Waiting clears IsBuffering on a Ready from a member
        // that is neither hot-joining nor timed out, so a recorded spectator
        // stall stayed listed for good — and counted as soon as the member
        // followed the group again.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        b.IgnoreWait(true);

        b.Buffer(Minute);
        b.Ready(Minute, isPlaying: true);

        Assert.False(b.IsListedBuffering);

        b.IgnoreWait(false);
        a.Buffer(Minute);
        a.Ready(Minute, isPlaying: false);

        Assert.Equal(GroupStateType.Playing, harness.State);
    }

    [Fact]
    public void ANewItemRestartsTheClockForMembersAlreadyBuffering()
    {
        // Same rule from another of SetAllBuffering's callers: every
        // group-wide wait restarts the clock, not only a Seek's.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        Thread.Sleep(Timeout + Timeout);

        harness.Play(a);

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Empty(harness.Group.GetStalledBufferingSessions(Timeout));

        Thread.Sleep(Timeout + TimeSpan.FromMilliseconds(100));
        Assert.Contains(harness.Group.GetStalledBufferingSessions(Timeout), session => session.Id == b.Session.Id);
    }
}
