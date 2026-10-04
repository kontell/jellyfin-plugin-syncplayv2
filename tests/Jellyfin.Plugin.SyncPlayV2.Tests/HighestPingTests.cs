using System;
using System.Linq;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The group schedules a resume twice the highest ping ahead, so that every
/// member it sends the command to has it in time: only those members count.
/// </summary>
public class HighestPingTests
{
    [Fact]
    public void ADisconnectedMembersPingDoesNotDelayTheResume()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        var c = harness.Join("c");
        harness.StartPlaying(new[] { a, b, c });
        a.Ping(50);
        b.Ping(50);
        c.Ping(3000);
        harness.Group.SetMemberDisconnected(c.Session);
        a.Pause();
        Assert.Equal(GroupStateType.Paused, harness.State);

        a.Unpause();

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(50, harness.Group.GetHighestPing());
        AssertResumeLead(a, TimeSpan.FromMilliseconds(harness.Group.DefaultPing));
    }

    [Fact]
    public void AConnectedMembersPingStillDelaysTheResume()
    {
        // The member the command has to reach in time is the slowest one
        // that is sent it, a spectator included.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });
        a.Ping(50);
        b.Ping(3000);
        b.IgnoreWait(true);
        a.Pause();

        a.Unpause();

        Assert.Equal(GroupStateType.Playing, harness.State);
        AssertResumeLead(a, TimeSpan.FromMilliseconds(6000));
    }

    [Fact]
    public void NobodyConnectedIsNoPing()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        a.Ping(3000);
        harness.Group.SetMemberDisconnected(a.Session);

        Assert.Equal(0, harness.Group.GetHighestPing());
    }

    private static void AssertResumeLead(Member member, TimeSpan expected)
    {
        // The start is scheduled a moment before the command is stamped.
        var unpause = member.Commands.Last(command => command.Command == "Unpause");
        Assert.InRange(unpause.When - unpause.EmittedAt, expected - TimeSpan.FromMilliseconds(200), expected);
    }
}
