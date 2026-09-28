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
}
