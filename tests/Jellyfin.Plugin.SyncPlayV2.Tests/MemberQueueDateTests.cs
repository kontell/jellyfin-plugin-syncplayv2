using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// A member is never sent a queue dated before the last one it was sent. A
/// joiner's queue is dated when it is taken (<see cref="JoinQueueStampTests"/>),
/// later than the queue's own date, and a client that drops a queue older than
/// the one it applied would drop every later send of the unchanged queue.
/// </summary>
public class MemberQueueDateTests
{
    private const long Minute = TimeSpan.TicksPerMinute;

    [Fact]
    public void AQueueResentAfterAStopIsNotOlderThanTheJoinersOwn()
    {
        // Stop, then Unpause: the group sends everyone the unchanged queue to
        // load again. The member that joined while it was paused must not read
        // it as older than the queue it joined with.
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var (b, joined) = JoinPaused(harness, a);
        a.Stop();
        Assert.Equal(GroupStateType.Idle, harness.State);
        a.Forget();
        b.Forget();

        a.Unpause();

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.True(LastQueueDate(b) >= joined, "the joiner's queue went back in time");
    }

    [Fact]
    public void ASnapshotIsNotOlderThanTheJoinersOwnQueue()
    {
        // The reconnect resync, and a snapshot the member asks for.
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var (b, joined) = JoinPaused(harness, a);
        b.Forget();

        harness.Group.ResyncSession(b.Session, CancellationToken.None);

        Assert.True(SnapshotQueueDate(b) >= joined, "the joiner's queue went back in time");
    }

    [Fact]
    public void ARendezvousSnapshotIsNotOlderThanTheJoinersOwnQueue()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var (b, joined) = JoinPaused(harness, a);
        a.Unpause();
        a.Ready(Minute);
        b.Ready(Minute);
        Assert.Equal(GroupStateType.Playing, harness.State);
        b.Buffer(Minute);
        Assert.Equal(GroupStateType.Waiting, harness.State);
        b.Forget();

        b.TimeOut();

        Assert.True(SnapshotQueueDate(b) >= joined, "the joiner's queue went back in time");
    }

    [Fact]
    public void AV1ResyncIsDatedAsTheQueueTheJoinerHas()
    {
        // Jellyfin Web drops a queue dated at or before the one it applied, so
        // a resync dated as the one it joined with is the queue it has, not a
        // new playlist to restart.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var (b, joined) = JoinPaused(harness, a, protocolVersion: 1);
        b.Forget();

        harness.Group.ResyncSession(b.Session, CancellationToken.None);

        Assert.Equal(joined, LastQueueDate(b));
    }

    [Fact]
    public void OneMembersJoinDoesNotMoveTheDateTheOthersAreSent()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        var (b, joined) = JoinPaused(harness, a);
        var lastChange = harness.Group.PlayQueue.LastChange;
        Assert.True(joined > lastChange);
        a.Stop();
        a.Forget();

        a.Unpause();

        Assert.Equal(lastChange, LastQueueDate(a));
    }

    [Fact]
    public void AQueueChangeAfterTheJoinKeepsItsOwnDate()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a", protocolVersion: 2);
        harness.StartPlayingTwoItems(new[] { a });
        a.Pause();
        Thread.Sleep(5);
        var b = harness.Join("b", protocolVersion: 2);
        var joined = LastQueueDate(b);
        a.Ready();
        b.Ready();
        Assert.Equal(GroupStateType.Paused, harness.State);
        Thread.Sleep(5);

        a.NextItem();

        Assert.Equal(harness.Group.PlayQueue.LastChange, LastQueueDate(b));
        Assert.True(LastQueueDate(b) > joined);
    }

    /// <summary>
    /// <paramref name="creator"/> plays and pauses, and b joins the paused
    /// group some time after its queue last changed; both then report ready.
    /// </summary>
    /// <returns>The joiner, and the date of the queue it was sent on joining.</returns>
    private static (Member Joiner, DateTime Joined) JoinPaused(GroupHarness harness, Member creator, int protocolVersion = 2)
    {
        harness.StartPlaying(new[] { creator }, Minute);
        creator.Pause();
        Thread.Sleep(20);

        var joiner = harness.Join("b", protocolVersion);
        var joined = LastQueueDate(joiner);
        Assert.True(joined > harness.Group.PlayQueue.LastChange, "the joiner's queue is not dated when it was taken");
        creator.Ready(Minute);
        joiner.Ready(Minute);
        Assert.Equal(GroupStateType.Paused, harness.State);
        return (joiner, joined);
    }

    private static DateTime LastQueueDate(Member member)
        => QueueDate(member.Updates.Last(update => update.Type == "PlayQueue").Data);

    private static DateTime SnapshotQueueDate(Member member)
    {
        var snapshot = (WireGroupSnapshot)member.Updates.Last(update => update.Type == "StateSnapshot").Data!;
        return QueueDate(snapshot.PlayQueue);
    }

    private static DateTime QueueDate(object? queue) => queue switch
    {
        WirePlayQueueUpdate wire => wire.LastUpdate,
        PlayQueueUpdate stock => stock.LastUpdate,
        _ => throw new InvalidOperationException("unexpected PlayQueue payload " + queue?.GetType()),
    };
}
