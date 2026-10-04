using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// A member's buffering flag describes the wait it is in: none outlives that
/// wait to hold up a later one.
/// </summary>
public class BufferingFlagTests
{
    [Fact]
    public void AReconnectedMembersReadyClearsTheBufferingItLeftBehind()
    {
        // b stalls, drops (the group stops waiting for it and plays on) and
        // reconnects, which waits for it again; its next Ready lands in Playing.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });
        b.Buffer();
        harness.Group.SetMemberDisconnected(b.Session);
        harness.Group.HandleRequest(b.Session, new IgnoreWaitGroupRequest(true), CancellationToken.None);
        Assert.Equal(GroupStateType.Playing, harness.State);
        harness.Group.ReconnectSession(b.Session, CancellationToken.None);

        b.Ready(isPlaying: true);

        Assert.False(b.IsListedBuffering);
        a.Buffer();
        a.Ready();
        Assert.Equal(GroupStateType.Playing, harness.State);
    }

    [Fact]
    public void AMemberThatAsksNotToBeWaitedForLeavesNoBufferingBehind()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });
        b.Buffer();
        Assert.Equal(GroupStateType.Waiting, harness.State);

        b.IgnoreWait(true);

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.False(b.IsListedBuffering);
        b.IgnoreWait(false);
        a.Buffer();
        a.Ready();
        Assert.Equal(GroupStateType.Playing, harness.State);
    }

    [Fact]
    public void AWaitTheEngineGivesUpOnKeepsTheMemberFlagged()
    {
        // The timeout's IgnoreWait is not the member's choice: it is still
        // loading, and its own report ends that.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });
        b.Buffer();

        b.TimeOut();

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.True(b.IsListedBuffering);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEngineIgnoreWaitKeepsASpectatorsNewBuffering(bool disconnects)
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });
        b.IgnoreWait(true);
        a.Seek(0);
        Assert.True(b.IsListedBuffering);

        if (disconnects)
        {
            harness.Group.SetMemberDisconnected(b.Session);
            harness.Group.HandleRequest(b.Session, new IgnoreWaitGroupRequest(true), CancellationToken.None);
            harness.Group.ReconnectSession(b.Session, CancellationToken.None);
        }
        else
        {
            b.TimeOut();
        }

        Assert.True(b.IsListedBuffering);
        Assert.True(harness.Group.IsSpectator(b.Session.Id));
        b.Ready();
        Assert.False(b.IsListedBuffering);
        Assert.True(harness.Group.IsSpectator(b.Session.Id));
        b.IgnoreWait(false);
        Assert.False(harness.Group.IsSpectator(b.Session.Id));
    }

    [Fact]
    public void ARequestToResumeWaitingKeepsTheMembersCurrentBuffering()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b });
        b.IgnoreWait(true);
        a.Seek(0);

        b.IgnoreWait(false);

        Assert.True(b.IsListedBuffering);
        Assert.False(harness.Group.IsSpectator(b.Session.Id));
        a.Ready();
        Assert.Equal(GroupStateType.Waiting, harness.State);
        b.Ready();
        Assert.Equal(GroupStateType.Playing, harness.State);
    }
}
