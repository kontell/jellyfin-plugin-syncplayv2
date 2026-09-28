using System;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// A member that rebuffered and reports ready behind the group: the group
/// resumes when the member will have caught up (#15).
/// </summary>
public class RecoveringMemberTests
{
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    private static readonly long NineSecondsBehind = Minute - TimeSpan.FromSeconds(9).Ticks;

    [Fact]
    public void ARecoveringMemberThatIsAlreadyPlayingIsToldTheGroupResumed()
    {
        // jellyfin-web answers the group's Ready state update with its
        // "schedule-play" indicator and only clears it on a scheduled
        // Unpause; left out of the Unpause, the recovering member shows the
        // indicator over its playing video for good.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        a.Buffer(Minute, isPlaying: true);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        a.Ready(NineSecondsBehind, isPlaying: true);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(b.Commands, command => command.Command == "Unpause");
        Assert.Contains(a.Commands, command => command.Command == "Unpause");
    }

    [Fact]
    public void AV2MemberThatIsAlreadyPlayingIsNotSentTheUnpause()
    {
        // A v2 client (Kofin) lines its player up on a scheduled Unpause's
        // PositionTicks as soon as it arms it. That position is where the
        // group will be at the resume, nine seconds ahead of a member that is
        // playing behind it, so the member would jump there early and the
        // group would resume on top of it.
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        a.Buffer(Minute, isPlaying: true);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        a.Ready(NineSecondsBehind, isPlaying: true);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(b.Commands, command => command.Command == "Unpause");
        Assert.DoesNotContain(a.Commands, command => command.Command == "Unpause");
    }

    [Fact]
    public void AV2MemberThatRecoversPausedIsSentTheUnpause()
    {
        // A member that is not playing needs the Unpause to start at all,
        // whatever its protocol. The recovering branch takes a delay above
        // twice the highest ping, and a paused member further behind than
        // its tolerance (at least 500 ms) is lost, not recovering, and is
        // seeked instead: 100 ms pings put 300 ms between the two.
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        a.Ping(100);
        b.Ping(100);

        a.Buffer(Minute, isPlaying: true);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        a.Ready(Minute - TimeSpan.FromMilliseconds(300).Ticks, isPlaying: false);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(b.Commands, command => command.Command == "Unpause");
        Assert.Contains(a.Commands, command => command.Command == "Unpause");
    }

    [Fact]
    public void AV1MemberRecoversTheSameWayInAGroupWithAV2Member()
    {
        // The filter follows the recovering member's protocol, not the
        // group's: a v2 member elsewhere in the group changes nothing for a
        // jellyfin-web member, and the v2 member, which is not the one that
        // recovered, is sent the Unpause as it always was.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b }, Minute);

        a.Buffer(Minute, isPlaying: true);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        a.Ready(NineSecondsBehind, isPlaying: true);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(b.Commands, command => command.Command == "Unpause");
        Assert.Contains(a.Commands, command => command.Command == "Unpause");
    }
}
