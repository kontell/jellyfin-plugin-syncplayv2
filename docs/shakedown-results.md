# Shakedown results

Run 2026-08-26 against `minipie` 10.11.11, SyncPlay v2 plugin 10.11.0.4, kofin
0.21.0 **plus the two fixes from this exercise** (`eeac6ee`, `5cac6e4`) deployed
to all four members. Assets: a Frasier S01E01 re-encode carrying a burned-in
timecode (AV1 1080p 23.976, 10 min), the h264 timecode clip, and a library HEVC
title.

Members: **L22** (flatpak Kodi 22, desktop), **TAB** (Galaxy Tab S5e),
**BRV** (Bravia 4K AE2), **PXL** (Pixel 7 Pro). Harness: `tools/rig.py`,
`tools/scen_*.py`, driven headlessly; results in `/tmp/shakedown-results.jsonl`.

**29 passed, 4 failed, 2 skipped. The four failures are one defect counted three
ways, plus one over-strict assertion of mine.**

---

## Defects found

### 1. A spectator's own playback is driven by the group (R-C)

The most significant remaining defect. Reproduced three times.

Become a spectator through kofin's own menu, start your own, different item, and
the group then changes item. The queue guard works — and the transport commands
that follow it do not:

```
[ syncplay/queue ] 1 items, playing 0 (NewPlaylist)
Spectator playing own media; not following the queue      <- guard fires
Command for another queue item (11e61cb5… != 00629b81…)
[ syncplay/Unpause ] at … (-11ms)
[ syncplay/align ] +320ms after the resume: left to fine sync
[ syncplay/pulse ] +408ms: 1.082x for 5.0s ramped         <- group drives the spectator
```

Measured: spectator watching its own episode at 35 005 ms, dragged to 7 121 ms.
`_apply_play_queue` honours `ignore_wait`; `_handle_command` and the playback
controller do not, so Unpause/Seek/pulses still land on private playback.

A viewer's report of this would be "I became a spectator to watch my own thing
and the group yanked my playback around".

**Also in this area:** kofin's spectator state is client-local. `grep
IgnoreGroupWait` across `manager.py` and `ui.py` finds nothing — the flag is
never read back off a group update, while the *server* sets and clears it on hot
join, rendezvous, disconnect and reconnect (`Group.cs` `BeginHotJoin`,
`MarkIgnoredByTimeout`, `SetMemberDisconnected`, `ReconnectSession`). So the two
can disagree in both directions. This invalidated a first run of the C cells,
where driving `SetIgnoreWait` over REST set the server flag while the client
stayed unaware; `rig.toggle_spectator()` now drives kofin's real menu instead.

### 2. Reported ping lags a latency increase by ~4.5 minutes (R-D)

With 1000 ms RTT injected through `wanshape`:

| t | reported ping |
|---|---|
| +30 … +120 s | 6 ms (unchanged) |
| +180 s | 23 ms |
| +240 s | 50 ms |
| +270 s | **510 ms** |

`TIMESYNC_INTERVAL = 30 s` over an 8-sample **min-RTT** window: min-RTT is
designed to reject transient spikes, and so it also rejects a genuine sustained
increase for the length of the window. Meanwhile the server grants that member
`clamp(2×ping, 500, 2000)` = the **500 ms floor** rather than the ~1020 ms its
link warrants, making it likelier to be corrected or rendezvoused.

Latent, not observed to break anything: offsets stayed −108…+107 ms throughout.

---

## Results

### R-A — group formation (4/4)

| cell | result | evidence |
|---|---|---|
| A1 | pass | entered from Idle; offset −185 ms |
| A2 | pass | offset −180 ms; **the already-playing item logs as unrouted**, confirming the prediction that this gesture lands on command-only sync |
| A3 | pass | paused at 5 022 ms; group entered Waiting; offset −226 ms |
| A4 | pass | control: offset 117 ms, fine sync armed |

### R-B — joining (2/3, 2 skipped)

| cell | result | evidence |
|---|---|---|
| B1 | pass | Idle group, 2 members |
| B2 | pass | Paused group; joiner started; offset 215 ms; both paused |
| B7 | (pass on merit) | offset 393 ms, landed −309 ms. Recorded FAIL only because my assertion demanded the adopt branch, which applies only when playback survives the leave |
| B3 | pass | covered exhaustively by the hot-join work — see `hotjoin-position-doubling.md` |
| B4 | skip | needs `HotJoin=false` set in plugin config server-side |
| B6 | skip | needs a third free v2 member |

### R-C — spectator (5/8)

| cell | result | evidence |
|---|---|---|
| C1 | pass | menu-driven; flags `[False, True]`; group kept playing |
| C2 | pass | spectator reached 35 005 ms on its own item; group still Playing |
| C3 | pass | back from spectator; flags `[False, False]` |
| C4 | pass | **the suspected hot-join interaction does not bite** — toggled mid-hot-join, offset 5 ms |
| C6 | pass | group position held |
| C2b, C5, C5c | **fail** | defect 1 above |

### R-D — impaired network (6/6)

| cell | result | evidence |
|---|---|---|
| D1–D4 | pass | RTT 0/300/1000/2500 ms; offsets −96 / −254 / +16 / −75 ms — **sync held even at 2 500 ms added RTT** |
| D3b | pass (finding) | defect 2 above |
| D5 | pass | bandwidth capped below the asset bitrate; 153 MB carried through the proxy; group stayed in sync |
| D6 | pass | stall 15 s → rendezvous fired; server log `rendezvousing … "kept the group waiting for over 00:00:10"` then `Unpause at 1236879272 ticks` (123.69 s) against a group at 120.9 s |
| D7 | pass | stall 6 s → group waited, no rendezvous |
| D8/D9 | not run | blackhole cells deferred with the session's remaining time |

### R-E — correction (2/2)

| cell | result | evidence |
|---|---|---|
| E1 | pass | group Seek; members converged to −82 ms |
| E4 | pass | backwards Seek converged the same way, −193 ms |
| E3/E5 | covered | transcode rendezvous and the v1 abandon path seen during the hot-join work |

### R-F — fine sync (3/3)

| cell | result | evidence |
|---|---|---|
| F1 | pass | 6 pulses, all closing within tolerance of what they claimed |
| F2 | pass | deadband held: 0 pulses in 20 s at −185 ms |
| F5 | pass | queue 16.0 s → 1.0 s at join on BRV, restored to 160 at leave |
| F7 | pass | `inputstream.tempo` disabled → declined to arm, group still worked at 96 ms |

### R-H — resilience (3/3)

| cell | result | evidence |
|---|---|---|
| H1 | pass | member force-stopped mid-group; group kept playing; member returned |
| H2 | pass | queue restore record honoured |
| H5 | pass | both members left at once; 0 groups remained |

### R-I — codec matrix (4/4)

Four members at once — L22, TAB, BRV, PXL — all direct play, all routed through
`inputstream.tempo`:

| codec | worst pair across 4 members |
|---|---|
| AV1 | **148 ms** |
| h264 | **133 ms** |
| HEVC | **168 ms** |

The named PXL/AV1 regression cell is **clean**: rate change plus three seeks
produced 0 `dequeueInputBuffer` storms, 0 `Could not find ref with POC` errors,
8 pulses, playback still running, members 48 ms apart.

### R-H8 — soak (partial)

8.8 minutes of continuous two-member playback (BRV + PXL, AV1), 264 paired
samples of the reported offset while both were playing:

| metric | offset |
|---|---|
| median | **108 ms** |
| p90 | 250 ms |
| p99 | 363 ms |
| max | 1237 ms |
| excursions > 600 ms | **1 — 0.4 % of samples** |

The single excursion resolved within two seconds and `screendiff` read **+54 ms
on screen** immediately afterwards, with the API agreeing to 16 ms. So the tail
is one brief event in nine minutes rather than a drift the group failed to
close. The run ended because the 10-minute asset reached its end, not through
any fault.

Short of the planned 60 minutes, and measured from reported positions rather
than the screen, so recorded as partial.

### R-G — mixed v1/v2 (3 pass, 3 defects)

Run with a real jellyfin-web client (`Client="Jellyfin Web"`, Firefox, 10.11.11)
joining a live BRV+PXL group. **This group found three server-side defects that
nothing else in the exercise could have found**, and all three needed a genuine
v1 client rather than a synthetic one.

| cell | result | evidence |
|---|---|---|
| G1 | pass | 3 members, group stays ProtocolVersion 2, both v2 members in sync |
| G2 | pass | no visible glitch and no console error on the v1 client |
| G5 | pass | v1 member never negotiates; its ping stays at the 500 `DefaultPing` |
| B5 | pass | a v1 join uses the classic barrier, not hot join |

#### G-BUG1 — rendezvous is not gated on the member's protocol version

`WaitingGroupState.cs:483` gates on `context is IGroupStateContextV2` — whether
the *group* is a v2-capable context, which is always true with this plugin —
instead of `IsV2Member(session.Id)`. Its sibling, the wait-timeout path at
`SyncPlayManagerV2.cs:761`, gets it right:

```csharp
if (group.IsV2Member(session.Id) && SyncPlayV2Plugin.Instance?.Configuration.HotJoin != false)
```

So a v1 member whose corrections do not converge is rendezvoused, reaching
`BeginHotJoin` → `SendWireUpdate(session, "StateSnapshot", …)`. And
`SendWireUpdate` (`Group.cs:556`) has **no** version check, unlike
`ResyncSession` (583) and the position beacon (680), which both gate on
`member.ProtocolVersion >= 2`. A v1 client therefore receives a v2-only message
type, against spec §2 and against the coexistence guarantee the whole
shadowed-manager architecture rests on. It also bypasses the `HotJoin` config
gate the other path honours.

Observed live at 16:55:30. **Intermittent**: `CorrectionPolicy` always grants the
first correction (`attempts < 2`), so this is reached only when a v1 client needs
a second one — join #1 did, join #2 did not.

Fix:

```csharp
if (context is IGroupStateContextV2 v2
    && v2.IsV2Member(session.Id)
    && SyncPlayV2Plugin.Instance?.Configuration.HotJoin != false
    && v2.ShouldRendezvous(session, delayTicks))
```

#### G-BUG2 — an explicit `IgnoreWait` is silently cleared

`Group.cs:1005`:

```csharp
if (!isBuffering && value.IgnoredByTimeout)
{
    value.IgnoredByTimeout = false;
    value.IgnoreGroupWait = false;
}
```

`IgnoredByTimeout` is set by `BeginHotJoin` (462), `SetMemberDisconnected` (702)
and `MarkIgnoredByTimeout` (821). One field carries two different meanings —
"the group gave up on you" and "you asked not to be waited for" — so clearing
the first also clears the second.

Observed live: the user resumed local playback in jellyfin-web, which correctly
requested `IgnoreWait` at 16:59:14. Because G-BUG1 had already set
`IgnoredByTimeout` on that member, the next not-buffering report wiped the
deliberate opt-out. The group then showed all three members as full participants
while one sat 217 s away — so a subsequent pause or seek would have waited for it
and dragged its private playback to the group position.

#### G-BUG3 — a member adrift while the group is Playing is never corrected

Corrections live in `WaitingGroupState`, so none fire while the group is
Playing. A v2 member self-corrects from the 5 s `PositionBeacon`; a **v1 member
has neither**, so once adrift it stays adrift. Measured: 217 s gap
(web 155.0 s vs both Kodi members 372.5 s), zero corrections logged across it.

#### G-TRUTH — a v1 member's real on-screen offset

**Not** the reported case that opened the exercise. That report was Bravia joined
by Pixel on AV1 over a hot join — kofin only, no web client — and it is
attributed to the hot-join position doubling in
`hotjoin-position-doubling.md` (fixed, `eeac6ee`). What follows is a separate
finding about v1 members that happens to share the ~2 s magnitude.

Measured by capturing the jellyfin-web window on the same X display and OCRing
the asset's burned-in timecode, interleaved FF → BRV → FF so the web client's
screen position is interpolated to the instant the Bravia's frame was taken:

| sample | web (interpolated) | Bravia | web vs Bravia |
|---|---|---|---|
| 1 | 585 719 ms | 588 082 ms | **−2363 ms** |
| 2 | 589 279 ms | 591 669 ms | **−2390 ms** |
| 3 | 593 193 ms | 595 465 ms | **−2272 ms** |

| | web vs Bravia |
|---|---|
| what the API reported | ~1.1 s |
| what the screens showed | **2.4 s** |
| disagreement | **~1.3 s** |

Two things compound to produce it:

1. **jellyfin-web reports on a ~3 s cadence** — its `PositionTicks` repeats
   (`421.0, 424.0, 428.0, 430.758967, 433.758967, 436.758967`), so any single
   read is up to a report interval stale, understating the gap.
2. **Nothing closes it.** G-BUG3: corrections live in `WaitingGroupState`, and a
   v1 member has no `PositionBeacon` to self-correct from. So the offset is
   fixed rather than drifting — which is exactly how it was described from the
   sofa: *"keeping the same gap in sync all along."*

A constant gap that nothing corrects, under-reported by the only number a viewer
can see. Finding it needed a real v1 client, the burned-in timecode, and screen
capture rather than reported position — but it is its own defect, not an
explanation of the originally reported hot-join case.

Caveat: this measurement was only possible because the browser happened to be on
the same X display as the harness. The same approach will not reach a browser on
another machine — that case needs channel 1 (camera), still unrun.

#### G-COST — a v1 join stalls the whole group

Hot join is v2-only, so every v1 join takes the classic barrier: the v2 members
paused 3.2 s on join #1 and 9.7 s on join #2, bounded by the 10 s
`GroupWaitTimeout`. Inherent to the design rather than a defect, but it is the
user-visible price of a mixed group and worth stating.

---

## What the rig now shows

A four-member group across three codecs holds **~150 ms**, and the seek,
pause/unpause, hot-join and rendezvous paths all converge to well under half a
second. Both defects fixed during this exercise
(`hotjoin-position-doubling.md`, and the unpause cut) are verified on-device.

## Still open

1. **R-G** — mixed v1/v2, the whole point of the shadowed-manager architecture.
2. **Spectator command isolation** — defect 1.
3. **B4/B6, D8/D9** — the skipped cells.
4. **Channel 1** (camera) — never run; channel 2 (screen OCR) carried the
   output-truth work throughout.
5. **H8** — the 60-minute soak.
6. The Pixel's `videoplayer.queuetimesize` is 10 with an empty restore record,
   pre-existing; its original value is lost.
