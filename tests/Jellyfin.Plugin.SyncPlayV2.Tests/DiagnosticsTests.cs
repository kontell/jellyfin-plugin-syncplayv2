using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Diagnostics;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using MediaBrowser.Controller.Library;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMembersClientVersionAndNextEpisodeAutoplayAreReported(bool autoplay)
    {
        // With "Play next episode automatically" on, Jellyfin Web expands a
        // group's queue; the report has to show it to explain a web member
        // that stops following the group.
        var harness = new GroupHarness();
        harness.Join("a");
        var group = harness.Group.GetDiagnostics();

        MemberUsers.FillAutoplay(new[] { group }, UserDirectory.Create(autoplay));

        var member = group.Members.Single();
        Assert.Equal("1.2.3", member.ClientVersion);
        Assert.Equal(autoplay, member.AutoplayNextEpisode);
    }

    [Fact]
    public void AnUnknownUsersAutoplayIsReportedAsUnknown()
    {
        var harness = new GroupHarness();
        harness.Join("a");
        var group = harness.Group.GetDiagnostics();

        MemberUsers.FillAutoplay(new[] { group }, NullService<IUserManager>.Create());

        Assert.Null(group.Members.Single().AutoplayNextEpisode);
    }

    [Fact]
    public void AUserThatCannotBeReadLeavesItsAutoplayUnknown()
    {
        // The lookup can fail (a database error; an empty id throws): the
        // report still comes back, with that one flag unknown.
        var harness = new GroupHarness();
        harness.Join("a");
        var group = harness.Group.GetDiagnostics();

        MemberUsers.FillAutoplay(new[] { group }, FailingUsers.Create());

        Assert.Null(group.Members.Single().AutoplayNextEpisode);
    }

    [Fact]
    public void TheGroupReadsNoUserUnderItsLock()
    {
        // The group's own diagnostics are built under its lock, which play,
        // seek and ready take too: no user lookup there.
        var harness = new GroupHarness(FailingUsers.Create());
        harness.Join("a");

        Assert.Null(harness.Group.GetDiagnostics().Members.Single().AutoplayNextEpisode);
    }

    [Fact]
    public void TheHistoryExplainsAStallAfterItHasPassed()
    {
        // The panel shows the present; by the time an admin opens it the
        // stall is over. The history keeps what happened, newest first.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute);
        b.TimeOut();
        a.Ping(120);

        var history = harness.Group.GetDiagnostics().History;
        var events = history.Select(e => e.Event).ToList();

        Assert.True(events.IndexOf("Wait timed out") >= 0 && events.IndexOf("Wait timed out") < events.IndexOf("Buffer"), string.Join(", ", events));
        Assert.Contains(history, e => e.Event == "State" && e.Detail == "Playing -> Waiting");
        Assert.Contains(history, e => e.Event == "Joined" && e.Member == "b");
        Assert.DoesNotContain("Ping", events);
        Assert.True(history.Zip(history.Skip(1)).All(p => p.First.SecondsAgo <= p.Second.SecondsAgo));
    }

    [Fact]
    public void TheHistoryKeepsOnlyTheLastEvents()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, Minute);

        for (var i = 0; i < 2 * Diagnostics.GroupHistory.Capacity; i++)
        {
            a.Pause();
            a.Unpause();
        }

        a.Seek(2 * Minute);

        // The last ones: the newest is kept and the oldest (the join) is gone.
        var history = harness.Group.GetDiagnostics().History;
        Assert.Equal(Diagnostics.GroupHistory.Capacity, history.Count);
        Assert.Contains(history.Take(3), e => e.Event == "Seek");
        Assert.DoesNotContain(history, e => e.Event == "Joined");
    }

    [Fact]
    public void AStaleItemIsMarkedInTheHistory()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);

        b.Buffer(Minute, playlistItemId: Guid.NewGuid());

        var buffer = harness.Group.GetDiagnostics().History.First(e => e.Event == "Buffer");
        Assert.Equal("b", buffer.Member);
        Assert.Contains("other item", buffer.Detail);
    }

    [Fact]
    public void TheHistoryNamesTheRendezvousCauseNotTheCallersText()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var b = harness.Join("b", protocolVersion: 2);
        harness.StartPlaying(new[] { a, b }, Minute);

        harness.Group.RendezvousMember(b.Session, "kept the group waiting for over 00:00:10", CancellationToken.None);
        harness.Group.RendezvousMember(a.Session, "free text from somewhere", CancellationToken.None);

        var causes = harness.Group.GetDiagnostics().History.Where(e => e.Event == "Rendezvous").Select(e => e.Detail).ToList();
        Assert.Equal(new[] { "other", "wait timeout" }, causes);
        Assert.DoesNotContain(harness.Group.GetDiagnostics().History, e => e.Event == "Joined" && e.Detail != "v2");
    }
}

/// <summary>An IUserManager whose every call fails, as a broken user database would.</summary>
public class FailingUsers : DispatchProxy
{
    public static IUserManager Create() => Create<IUserManager, FailingUsers>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("user database unavailable");
}
