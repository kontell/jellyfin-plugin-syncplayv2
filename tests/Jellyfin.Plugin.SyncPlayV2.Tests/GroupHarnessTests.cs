using System;
using System.Linq;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The harness itself: a group built without a server goes through the
/// ordinary start-up choreography and delivers through the real Sender.
/// </summary>
public class GroupHarnessTests
{
    [Fact]
    public void TwoMembersStartTogether()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");

        harness.Play(a);
        Assert.Equal(GroupStateType.Waiting, harness.State);

        a.Ready();
        Assert.Equal(GroupStateType.Waiting, harness.State);

        b.Ready();
        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Contains(a.Commands, command => command.Command == "Unpause");
        Assert.Contains(b.Commands, command => command.Command == "Unpause");
    }

    [Fact]
    public void AJoinerIsToldItJoined()
    {
        var harness = new GroupHarness();
        harness.Join("a");
        var b = harness.Join("b");

        Assert.Equal("GroupJoined", b.Updates.First().Type);
    }

    [Fact]
    public void StartPlayingLandsOnThePosition()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");

        harness.StartPlaying(new[] { a, b }, TimeSpan.FromSeconds(60).Ticks);

        Assert.True(harness.Group.PositionTicks >= TimeSpan.FromSeconds(60).Ticks);
    }
}
