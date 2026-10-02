using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Jellyfin.Plugin.SyncPlayV2.Tests.Harness;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The queue a joiner is sent is dated when it is taken. Jellyfin Web ignores
/// a queue dated at or before the last one it applied (milliseconds, <c>&lt;=</c>),
/// remembers that date across groups, and extrapolates the start position from
/// it as if playing.
/// </summary>
public class JoinQueueStampTests
{
    private const long Minute = TimeSpan.TicksPerMinute;

    private static readonly DateTime Now = new(2026, 9, 30, 22, 32, 39, 534, DateTimeKind.Utc);

    [Fact]
    public void AnOldQueueIsDatedAMillisecondBeforeItIsTaken()
    {
        var stamp = Group.JoinQueueStamp(Now.AddMinutes(-5), Now.AddTicks(1234));

        Assert.Equal(Now.AddMilliseconds(-1), stamp);
    }

    [Fact]
    public void TheStampIsNeverBeforeTheQueuesOwnDate()
    {
        var lastChange = Now.AddTicks(100);

        Assert.Equal(lastChange, Group.JoinQueueStamp(lastChange, Now.AddTicks(200)));
    }

    [Fact]
    public void AChangeInTheSameMillisecondStillReadsAsNewerToJellyfinWeb()
    {
        // Jellyfin Web compares Date values (milliseconds) and drops an update
        // unless it is strictly newer than the one it applied.
        var taken = Now.AddTicks(1);
        var stamp = Group.JoinQueueStamp(Now.AddMinutes(-5), taken);
        var nextChange = taken.AddTicks(1);
        Assert.Equal(Milliseconds(taken), Milliseconds(nextChange));

        Assert.True(Milliseconds(nextChange) > Milliseconds(stamp));
    }

    [Fact]
    public void AJoinerOfAPausedGroupGetsTheQueueDatedNowAtThePausedPosition()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, 5 * Minute);
        a.Pause();
        var lastChange = harness.Group.PlayQueue.LastChange;
        var paused = harness.Group.PositionTicks;
        Thread.Sleep(50);

        var before = DateTime.UtcNow;
        var b = harness.Join("b");
        var after = DateTime.UtcNow;

        var queue = LastQueue(b);
        Assert.True(queue.LastUpdate > lastChange, "not newer than the queue's own date");
        Assert.InRange(queue.LastUpdate, before.AddMilliseconds(-2), after);
        Assert.Equal(paused, queue.StartPositionTicks);
        Assert.False(queue.IsPlaying);
    }

    [Fact]
    public void AJoinerOfAPlayingGroupGetsTheQueueDatedNow()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, 5 * Minute);
        var lastChange = harness.Group.PlayQueue.LastChange;
        Thread.Sleep(50);

        var before = DateTime.UtcNow;
        var b = harness.Join("b");
        var after = DateTime.UtcNow;

        var queue = LastQueue(b);
        Assert.True(queue.LastUpdate > lastChange);
        Assert.InRange(queue.LastUpdate, before.AddMilliseconds(-2), after);
        Assert.Equal(harness.Group.PositionTicks, queue.StartPositionTicks);
    }

    [Fact]
    public void OnlyTheJoinerGetsTheNewPlaylist()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, Minute);
        a.Pause();
        a.Forget();
        Thread.Sleep(5);

        var b = harness.Join("b");

        var queues = b.Updates.Where(u => u.Type == "PlayQueue").ToList();
        Assert.Single(queues);
        Assert.Equal(PlayQueueUpdateReason.NewPlaylist, Reason(queues[0]));
        Assert.DoesNotContain(a.Updates, u => u.Type == "PlayQueue");
    }

    [Fact]
    public void AMemberThatRejoinsGetsANewerQueueThanTheOneItHad()
    {
        // What Jellyfin Web remembers from its first membership must not make
        // it ignore the queue of the second.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlaying(new[] { a }, Minute);
        var b = harness.Join("b");
        var first = LastQueue(b).LastUpdate;
        b.Leave();
        Thread.Sleep(5);

        b.Rejoin();

        Assert.True(Milliseconds(LastQueue(b).LastUpdate) > Milliseconds(first));
    }

    [Fact]
    public void AReconnectResyncKeepsTheQueuesOwnDate()
    {
        // A client already playing must see the resync's queue as the one it
        // has, not as a new playlist to restart.
        var harness = new GroupHarness();
        var a = harness.Join("a");
        var b = harness.Join("b");
        harness.StartPlaying(new[] { a, b }, Minute);
        Thread.Sleep(5);

        harness.Group.ResyncSession(b.Session, CancellationToken.None);

        Assert.Equal(harness.Group.PlayQueue.LastChange, LastQueue(b).LastUpdate);
    }

    [Fact]
    public void AQueueChangeAfterAJoinIsNewerThanTheJoinersQueue()
    {
        var harness = new GroupHarness();
        var a = harness.Join("a");
        harness.StartPlayingTwoItems(new[] { a });

        // People, not the same millisecond: the queue is set, someone joins,
        // someone presses Next. (Within one millisecond of the queue's own
        // change the stamp is that change's date, as stock would send.)
        Thread.Sleep(5);
        var b = harness.Join("b");
        var joined = LastQueue(b).LastUpdate;
        a.Ready();
        b.Ready();
        Thread.Sleep(5);

        a.NextItem();

        Assert.True(Milliseconds(LastQueue(b).LastUpdate) > Milliseconds(joined));
    }

    private static long Milliseconds(DateTime time) => time.Ticks / TimeSpan.TicksPerMillisecond;

    private static PlayQueueUpdateReason Reason(WireGroupUpdate update) => update.Data switch
    {
        WirePlayQueueUpdate wire => wire.Reason,
        PlayQueueUpdate stock => stock.Reason,
        _ => throw new InvalidOperationException("unexpected PlayQueue payload " + update.Data?.GetType()),
    };

    private static (DateTime LastUpdate, long StartPositionTicks, bool IsPlaying) LastQueue(Member member)
    {
        var update = member.Updates.Last(u => u.Type == "PlayQueue");
        return update.Data switch
        {
            WirePlayQueueUpdate wire => (wire.LastUpdate, wire.StartPositionTicks, wire.IsPlaying),
            PlayQueueUpdate stock => (stock.LastUpdate, stock.StartPositionTicks, stock.IsPlaying),
            _ => throw new InvalidOperationException("unexpected PlayQueue payload " + update.Data?.GetType()),
        };
    }
}
