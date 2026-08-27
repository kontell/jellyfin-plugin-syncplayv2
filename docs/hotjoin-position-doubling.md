# Hot join starts the joiner at group position + elapsed-since-queue-change

**Reproduced on the reported rig and configuration: Bravia playing, Pixel hot
joining, AV1 content. Root-caused. One-line client fix.**

Run 2026-08-26. BRV = Bravia 4K AE2 (Android 14, Kodi 22.0b1), PXL = Pixel 7 Pro
(Android 17, Kodi 22.0b1), both kofin 0.21.0 / inputstream.tempo 22.4.3, server
`minipie` 10.11.11 with SyncPlay v2 plugin 10.11.0.4. Asset: a Frasier S01E01
re-encode carrying a burned-in timecode — AV1 1080p 23.976, 10 min.

## The symptom

A member cold-joining a Playing group starts at roughly **twice** the group's
position, then visibly seeks back.

| group had been playing | joiner started at | error |
|---|---|---|
| 11.2 s | 27.0 s | ~16 s |
| 271.6 s | 546.0 s | ~274 s |
| 283.9 s | 570.0 s | ~286 s |

It looks like exactly 2× only because the queue is normally set at position 0,
so elapsed-since-queue-change equals the group position. The discriminating run
(join after 11 s instead of 272 s) separates the two: the error tracks
**elapsed since the play queue last changed**, not the position.

The client's own logs show both the bad instruction and the correction:

```
[ syncplay/play ]   ade40d… at 26.6s (+1.4s load allowance)
[ syncplay/landed ] 26.9s, wanted 26.5s (+444ms)
[ syncplay/align ]  -14679ms to the start position
```

and on the long-running group:

```
[ syncplay/play ]   ade40d… at 113.2s
[ syncplay/landed ] 113.0s, wanted 56.5s (+56442ms)
```

`syncwatch` caught the transient live as `DIVERGE BRV vs PXL: -274423ms`.

## Root cause

Two halves of one message disagree about what instant the position refers to.

**Server** — `Jellyfin.Plugin.SyncPlayV2/Engine/Group.cs`,
`GetPlayQueueUpdate()`:

```csharp
var startPositionTicks = PositionTicks;
if (isPlaying)
{
    var elapsedTime = currentTime - LastActivity;
    startPositionTicks += Math.Max(elapsedTime.Ticks, 0);   // extrapolated to NOW
}

return new PlayQueueUpdate(
    reason,
    PlayQueue.LastChange,      // <-- but the update is stamped with the QUEUE's change time
    ...
    startPositionTicks,
    ...);
```

`StartPositionTicks` is the position **now**. `LastChange` is when the *queue*
last changed — for a group that has been playing an hour, an hour ago. The two
fields are about different instants, and nothing in the message says so. This
matches stock Jellyfin, so it is not a plugin divergence.

**Client** — `plugin.video.kofin/lib/kofin/syncplay/manager.py`,
`_apply_play_queue()`:

```python
# Position reference: extrapolate from LastUpdate while playing.
reference_ms = last_update if last_update is not None else self.server_now_ms()
self.playback.set_reference(start_ticks, reference_ms, is_playing)
```

It pairs the position with `LastUpdate` and extrapolates forward from there —
so the whole elapsed playback is added a second time.

## The fix

The fallback already present on that line is the correct behaviour. The position
is extrapolated to send time, so its reference instant is *now*, and
`LastUpdate` should only be used for the queue-version dedup it already does
twenty lines above.

```python
# The server extrapolates StartPositionTicks to send time, so the position's
# reference instant is now — not LastUpdate, which timestamps the *queue's*
# last change and can be arbitrarily far in the past on a group that has been
# playing a while. Pairing the two added the whole elapsed playback twice and
# started a hot-joining member at roughly double the group position.
reference_ms = self.server_now_ms()
self.playback.set_reference(start_ticks, reference_ms, is_playing)
```

`queue_last_update` is assigned earlier in the same function and is untouched by
this, so queue dedup is unaffected.

### Why it self-heals, and why that is not enough

`PositionBeacon` (every 5 s) and every `SendCommand` carry a correctly paired
`PositionTicks` + `When`, so the member converges within a few seconds. Measured
settled state after the hot join, screen-to-screen: **BRV +36 ms vs PXL, and the
API agrees** — genuinely in sync once recovered.

What the user sees in the meantime is the joiner starting far ahead and then
cutting backwards by 15 s (or 274 s). On the long-running case the joiner also
starts *past the end* of shorter content, which is the "timestamps messed up"
report.

## Scope

Any `PlayQueueUpdate` delivered to a member that is loading, on a group that has
been playing since the queue was last set. Reached by:

* **cold hot join** (the reported case) — joiner not already playing;
* **rendezvous**, which routes through `BeginHotJoin` and the same snapshot path;
* a snapshot applied while `phase == "loading"`, where `_apply_snapshot` returns
  early and leaves the start position to the queue path.

Not reached by a re-join while already playing the same item: that takes the
"adopt the queue identity" branch and never sets a start position. Measured:
`landed 230.3s, wanted 230.4s (-42ms)`.

## Verification to run after the fix

1. Cold hot join at ~10 s and at ~270 s of group playback; joiner must start
   within a second of the group, and no `[ syncplay/align ]` of more than a
   second may follow.
2. `screendiff` immediately after the join, not only after settling.
3. Re-join-while-playing must stay on the adopt branch (no regression).
4. Rendezvous, which shares the path.
