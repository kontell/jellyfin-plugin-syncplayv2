using System;
using System.Linq;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// How far ahead a group resumes after waiting: at least DefaultPing (500 ms),
/// more for slow links — the same floor PlayingGroupState applies to an
/// ordinary Unpause.
/// </summary>
public class ResumeLeadTests
{
    [Fact]
    public void TheLastMemberReadyResumesWithAtLeastTheDefaultLead()
    {
        // On a LAN the measured pings are tens of milliseconds, so 2 x ping
        // is below the floor and the floor is what applies. Measured on a
        // deployment before the fix: "group ... has 0.142 seconds to
        // recover" with a 71 ms ping — the floor was compared in ticks.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        a.Ping(71);
        b.Ping(71);

        harness.Play(a);
        a.Ready();
        b.Ready();

        Assert.Equal(GroupStateType.Playing, harness.State);
        var unpause = b.Commands.Last(command => command.Command == "Unpause");
        Assert.True(
            unpause.When - unpause.EmittedAt >= TimeSpan.FromMilliseconds(490),
            $"lead was {(unpause.When - unpause.EmittedAt).TotalMilliseconds} ms");
    }

    [Fact]
    public void ASlowLinkStillGetsTwiceItsPing()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        a.Ping(71);
        b.Ping(400);

        harness.Play(a);
        a.Ready();
        b.Ready();

        var unpause = b.Commands.Last(command => command.Command == "Unpause");
        Assert.True(
            unpause.When - unpause.EmittedAt >= TimeSpan.FromMilliseconds(790),
            $"lead was {(unpause.When - unpause.EmittedAt).TotalMilliseconds} ms");
    }
}
