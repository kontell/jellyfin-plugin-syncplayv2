using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The diagnostics page shows what decided each of #14 and #15: the member
/// flags and the counts of what the engine did. These check that what it
/// shows is what the engine holds.
/// </summary>
public class DiagnosticsTests
{
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    [Fact]
    public void AGroupLoadingAnItemShowsItsMembersLoading()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.Join("b");

        harness.Play(a);
        var diagnostics = harness.Group.GetDiagnostics();

        Assert.Equal(GroupStateType.Waiting, diagnostics.State);
        Assert.Equal("Item", diagnostics.ItemName);
        Assert.Equal(1, diagnostics.QueueLength);
        Assert.All(diagnostics.Members, member =>
        {
            Assert.True(member.IsBuffering);
            Assert.True(member.BufferingForLoad);
        });
    }

    [Fact]
    public void AMemberTheGroupGaveUpOnIsShownAsSuch()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        b.TimeOut();
        var member = harness.Group.GetDiagnostics().Members.Single(m => m.UserName == "b");

        Assert.True(member.IgnoredByTimeout);
        Assert.True(member.IgnoreGroupWait);
        Assert.False(member.Spectator);
        Assert.False(member.BufferingForLoad);
    }

    [Fact]
    public void ASpectatorIsShownAsNotFollowing()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.IgnoreWait(true);
        var member = harness.Group.GetDiagnostics().Members.Single(m => m.UserName == "b");

        Assert.True(member.Spectator);
        Assert.False(member.IgnoredByTimeout);
    }

    [Fact]
    public void TheTimeInStateRestartsOnAStateChange()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a });
        Thread.Sleep(300);
        Assert.True(harness.Group.GetDiagnostics().StateSeconds >= 0.2);

        a.Pause();

        Assert.True(harness.Group.GetDiagnostics().StateSeconds < 0.2);
    }

    [Fact]
    public void ACorrectionIsCounted()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.Join("b");
        harness.Play(a);

        // Ready far from where the group is, while paused: "got lost in time".
        a.Ready(10 * Minute);

        Assert.Equal(1, harness.Counters.Snapshot().Corrections);
    }

    [Fact]
    public void ARendezvousIsNotCountedAsAHotJoin()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        b.TimeOut();

        var counters = harness.Counters.Snapshot();
        Assert.Equal(1, counters.Rendezvous);
        Assert.Equal(0, counters.HotJoins);
    }

    [Fact]
    public void AHotJoinIsCounted()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        harness.StartPlaying(new[] { a }, Minute);

        harness.Join("b", protocolVersion: 2);

        var counters = harness.Counters.Snapshot();
        Assert.Equal(1, counters.HotJoins);
        Assert.Equal(0, counters.Rendezvous);
    }

    [Fact]
    public void ADisconnectionAndReconnectionAreCountedOnce()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        // SessionEnded and the socket-liveness sweep can both report the same
        // death; it is one disconnection.
        harness.Group.SetMemberDisconnected(b.Session);
        harness.Group.SetMemberDisconnected(b.Session);
        var member = harness.Group.GetDiagnostics().Members.Single(m => m.UserName == "b");
        Assert.False(member.IsConnected);
        Assert.NotNull(member.DisconnectedSeconds);

        harness.Group.ReconnectSession(b.Session, CancellationToken.None);

        var counters = harness.Counters.Snapshot();
        Assert.Equal(1, counters.Disconnects);
        Assert.Equal(1, counters.Reconnects);
    }

    [Fact]
    public void ARepeatedJoinIsNotAnotherHotJoin()
    {
        // The manager answers a Join from a member already in the group by
        // joining it again ("restore session"); the catch-up runs again, but
        // nobody new joined.
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        harness.StartPlaying(new[] { a }, Minute);
        var b = harness.Join("b", protocolVersion: 2);

        harness.Group.SessionJoin(b.Session, new JoinGroupRequest(harness.Group.GroupId), CancellationToken.None);

        Assert.Equal(1, harness.Counters.Snapshot().HotJoins);
    }

    [Fact]
    public void RejoiningAfterADisconnectionIsAReconnection()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        harness.Group.SetMemberDisconnected(b.Session);

        harness.Group.SessionJoin(b.Session, new JoinGroupRequest(harness.Group.GroupId), CancellationToken.None);

        var counters = harness.Counters.Snapshot();
        Assert.Equal(1, counters.Disconnects);
        Assert.Equal(1, counters.Reconnects);
    }
}
