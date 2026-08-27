# Shakedown, round 2

Run 2026-08-27 against `minipie` 10.11.11. Everything here was measured; where
a claim comes from reading the code rather than from the rig it says so.

**Roster.** The first round used four devices; this one lost two of them
mid-run. The Bravia dropped off the network and was then claimed by its owner,
and the Galaxy Tab's wireless-debugging session died (its Kodi stayed up and
answered JSON-RPC, but without adb there was no way to read its credentials or
its log). What remained is two desktop Kodis on one X display — **OMG**, Kodi
21.3 Omega under `~/.kodi` on the `kofin-test` profile, and **PRS**, the Kodi
22 Piers flatpak — plus as many synthetic members as a scenario needs.

**The instrument that made this round possible: `tools/wireclient.py`.** A
synthetic SyncPlay member with a real websocket, at a protocol version of the
harness's choosing. Three of the four fixes under test are about what the wire
does for a member at a given version, and neither kofin (v2 by construction)
nor a browser (which decides for itself when and what to report) can be made
to exercise them repeatably. Two things had to be got right:

* **Its own token.** Jellyfin binds a websocket to the *token's* device, not to
  the `deviceId` query parameter, so a probe borrowing another member's token
  lands on that member's session — measured: a "v1" probe received a
  StateSnapshot that had been correctly sent to the v2 member whose token it
  was, which would have read as the withholding gate failing. Quick Connect
  mints a token against the probe's own identity without needing the account
  password: initiate as the probe, approve as an already-authenticated
  session, exchange the secret.
* **The playing item.** A `Ready` whose `PlaylistItemId` does not match the
  group's playing item is discarded by the waiting state without a word, so a
  probe that does not track it looks exactly like a probe the server is
  ignoring. The client now reads the id off the `PlayQueue` update.

Version comes out right by construction: a Join body with no `ProtocolVersion`
field leaves the registry with no entry and `Resolve` defaults to 1, which is
what a stock client looks like. Confirmed both ways — `/SyncPlay/List` omits
`ProtocolVersion` for the v1 probe and reports 2 for a v2 one, and the v1 probe
carries the 500 ms `DefaultPing` that marks a member which never negotiated.

---

## 1. The three 10.11.0.5 fixes, and a regression in one of them

All three shipped unverified. Two hold. The third was wrong, and the way it was
wrong was worse than the defect it fixed.

### RT1 — rendezvous is gated on the member's version (holds)

A member whose corrections cannot converge, driven by a probe that answers
every correction with the same wildly wrong position.

| | v1 probe | v2 probe (control) |
|---|---|---|
| Seek corrections received | **4** | 2 |
| StateSnapshot received | **0** | 1 |
| server log | 3 × "got lost in time, correcting" | "rendezvousing … corrections are not closing the gap" → "hot-joining" → "resuming without session" |

The v1 member is corrected and never rendezvoused; the v2 member is
rendezvoused on its second failed correction, exactly as `CorrectionPolicy`
intends (the first is always granted). The control matters: a gate that never
fires is indistinguishable from one that always fires unless both sides are
measured.

### RT3 — v2-only updates never reach a v1 socket (holds)

Thirty seconds of a Playing group, one v1 probe and one v2 probe side by side:

| | PositionBeacon | StateSnapshot | update types seen |
|---|---|---|---|
| v1 | **0** | **0** | `StateUpdate` |
| v2 | **6** | 0 | `PositionBeacon`, `StateUpdate` |

Note that the central `SendWireUpdate` gate added in 10.11.0.5 is now
*unreachable*: both callers that can send a v2-only update to a v1 member
(`ResyncSession` and the rendezvous path) gate correctly on their own, so the
"Withholding …" line never fires. It is defence in depth, not dead code, but no
test can reach it while the callers are right.

### RT2 — the two meanings of IgnoreGroupWait (was regressed; now fixed)

`IgnoreGroupWait` carries two things: *the group gave up on you* and *you asked
not to be waited for*. 10.11.0.5 separated them with an
`IgnoreGroupWaitByRequest` flag stamped inside `SetIgnoreGroupWait`.

That is the wrong seam. The engine synthesizes an `IgnoreWaitGroupRequest` of
its own twice — the wait-timeout sweep (`SyncPlayManagerV2.cs:788`) and a
transport death while the group is waiting (`:573`) — and both reach the same
state handler as a real request from the wire. So **every member the group
timed out on was marked as having asked**, and `IgnoreGroupWait` could never be
cleared again for the life of the group: not by its next report, not by a
reconnect. One 10-second stall and the group would never wait for that member
again.

Fixed by stamping where the request actually comes from the wire —
`SyncPlayManagerV2.HandleRequest`, which the engine's own synthesized requests
bypass — and leaving `SetIgnoreGroupWait` to set only the flag it is named for.

Measured on 10.11.0.6, both arms:

| | after the group's 10s give-up | after the member reports in position | after a socket reconnect |
|---|---|---|---|
| RT2a: no request from the member | `IgnoreGroupWait=True` | **False** — the group waits for it again | — |
| RT2b: the member asked | `True` | **True** | **True** |

Two things about this cell were themselves wrong before they were right, and
both are worth keeping:

* **The clearing gesture has to land while the group is still waiting.**
  `SetBuffering(false)` is only reached from `WaitingGroupState`'s ready
  branch; a `Ready` sent to a Playing group goes to a handler that never
  touches the flag. A first version of this cell reported the fix as failing
  when it was the test that could not reach the code.
* **Keeping the group waiting needs a second member that never answers.** An
  ignored member is precisely the one the group will not wait for, so with one
  real member the state leaves Waiting inside a second. A silent v2 holder is
  itself rendezvoused by the same sweep, so the holder has to answer during the
  timeout phase and go quiet only for the clearing seek.

**RT2c — the other synthesized IgnoreWait.** The fix moves the stamp to the
wire's entry point, which covers *both* places the engine raises an
`IgnoreWaitGroupRequest` of its own. Only the wait-timeout one was measured
above; this is the other — a transport death while the group is waiting
(`SyncPlayManagerV2.cs:573`).

| | IgnoreGroupWait |
|---|---|
| before | false |
| socket killed while the group waits | **true** — the group correctly stops waiting for it |
| after reconnecting inside the grace window | true |
| after reporting in position while the group waits | **false** |

The reconnect does not clear it, and that is not the fix failing: with the same
live session and a new socket, `ReconnectSession` takes its early branch and
only resyncs. The clear happens at the same place RT2a's does — the member's
next in-position report while the group is waiting. Worth knowing rather than
worth changing: a member that reconnects and then never reports stays
un-waited-for until it does, which is the same condition as any ignored member.

Not carried out: an A/B of this cell against 10.11.0.5 itself. The downgrade
needs a server restart, and the restart during the first attempt stopped
Jellyfin outright (`Restart=on-failure`, process exited 0, so systemd left it
down and the ssh account cannot start it). The regression is established from
the code and the fix is measured; the before-and-after pair is not.

---

## 2. Cells the first round could not reach

| cell | result | evidence |
|---|---|---|
| **B4** | pass | `HotJoin=false`: joiner gets 0 snapshots and the group drops to Waiting for the whole sample. `HotJoin=true`: 1 snapshot, group stays Playing throughout, hot join logged. Both positions measured, config set over the API with no restart. |
| **B6** | pass | Two joiners 25 ms apart: one snapshot and one private Unpause each, group never left Playing. |
| **C5** | **defect, now fixed** | see §3 |
| **D6** | pass | 15 s stall → rendezvous + snapshot |
| **D7** | pass | 6 s stall → no rendezvous, no snapshot |
| **E2** | pass | a member halving its gap each report (8 s → 3 s → 1 s) is corrected three times and never rendezvoused |
| **E6** | pass | both rendezvous paths in one session: "corrections are not closing the gap" and "kept the group waiting for over 00:00:10" |
| **G3** | pass | the v1 member pauses kofin, then seeks it: kofin lands at 259 841 ms against an ask of 260 000 |
| **G4** | pass | kofin's seek reaches the v1 member as a stock `Seek` command with a `StateUpdate` |
| **G6** | pass | covered by RT3 |

---

## 3. The spectator defect, fixed

Carried over from round 1 as the most significant open defect, reproduced
three times out of three, and now three defects rather than one.

Become a spectator through the group menu, start something of your own, and the
group takes it away. `_apply_play_queue` already refused to let the group's
queue tear down a spectator's playback — but:

1. **`_handle_command` had no such guard.** The group's Seek/Pause/Unpause were
   scheduled against private playback. Measured on the Bravia: a spectator at
   **422.5 s** of its own episode, seeked to **209.6 s** by the group's next
   command.
2. **Fine sync was not stopped either.** The member is still in phase
   `synced`, so the pulse scheduler went on measuring its private playback
   against the group and rate-shifting it to close the difference — observed
   asking **0.750× for 10 s against a residual of −306.6 s**.
3. **The choice did not survive being made.** `ignore_wait` is client-local
   (the wire's `Members` carry no session identity, so there is nothing to read
   it back from) and `_on_group_joined` reset it unconditionally. A toggle
   three seconds into a hot join was undone by the join's own update, and the
   group went back to driving that member. This is what made the fix look like
   it had failed for four consecutive runs.

The guard asks what is actually playing rather than tracking a flag: a
spectator whose player is showing an item that is not the group's item is
watching its own. That leaves an idle spectator following a queue update and
then starting with everyone else, which a guard on `ignore_wait` alone would
have stranded paused.

**Verified**: a spectator at 310.1 s of its own h264 item held through a group
Seek, Pause and Unpause and reached 324.0 s — playing forward at 1× throughout
— against 380.9 s → 209.8 s before the fix. On Omega, with the toggle three
seconds into the join, which is the case that used to undo itself.

A harness defect fell out of the same work: `rig.toggle_spectator` walked a
fixed number of Downs, and the menu takes ~2 s to populate on Omega against
~0.5 s on the Tab. A run where the walk started early left the member a full
participant while the scenario believed it was a spectator. It now reads the
focused item and stops on the one that mentions spectating.

---

## 3a. H3 — the server restarted underneath a live group

Run with the user at the keyboard, because the server's own restart route has
now stopped Jellyfin outright **twice out of two**: the unit is
`Restart=on-failure` and the process exits 0, so systemd leaves it down, and
the ssh account cannot start it. That is an environment defect rather than a
plugin one, but it makes H3 a two-person cell until the unit is changed.

Group of three (PRS, OMG, TAB) playing, all within 50 ms. The server was away
for **84 s**.

| | result |
|---|---|
| Direct-playing members (PRS, OMG) | **kept playing throughout, and stayed within 30 ms of each other** — 161 291/161 120, 171 422/171 426, 181 498/181 499, 191 817/191 789 |
| The transcoding member (TAB) | **stopped** — its HLS stream died with the server |
| Groups | gone; `/SyncPlay/List` empty afterwards (they live in the plugin's memory) |
| Recovery | a fresh three-member group formed and reached Playing, all within ~780 ms |

The two free-running clocks holding to 30 ms across an 84-second outage is the
strongest agreement measured anywhere in this exercise.

**Defect found: kofin retries a rejoin into a group that no longer exists,
forever.** `GroupDoesNotExist` is handled by calling `_attempt_rejoin()`, which
asks to join the very group the server has just said does not exist. The server
answers 204 and pushes `GroupDoesNotExist` again, so the two loop at the
`AUTO_REJOIN_INTERVAL` spacing (~34 s observed) with no give-up — against a
docstring that reads "§9: one automatic re-join before surfacing an error".
Observed on all three members after the restart. Not yet fixed.

Note on the Tab: it does **not** direct-play AV1 — the codec is absent from its
`directPlayVideoCodecs` and `forceDirectPlay` is off, so the AV1 asset
transcodes there. Every timing figure attributed to the Tab in either round is
therefore a transcoding member's, which matters given §4.

---

## 4. Transcoding: what the parked question actually was

**Read the correction first.** An earlier version of this section concluded
that "a transcoded stream does not play at real time" — segment rates between
0.754 and 1.486, a cumulative rate of 0.926, 9.1 s of drift in 115 s. That was
wrong, and it was wrong because of the harness, not the transcode.

The member under test had `videoplayer.queuetimesize` set to **1.0 s** with an
empty `syncPlayQueueRestore` record. Fine sync shortens the Kodi 22 player
queue to 1 s for the session on purpose, so a tempo pulse lands in ~2 s instead
of ~5, and records the original so it can be put back. The restore had been
lost (see below), so the member had been running on a 1 s queue for hours.

The server's HLS segments are **3 s**. A queue shorter than a segment cannot
bridge the boundary, so the player drains, caches and resyncs at every one.
The user saw it before the harness did — *"the video that's playing is being
momentarily paused/played every few seconds"* — and Kodi's log shows the cycle
at ~3 s intervals:

```
CDVDAudio::Pause - pausing audio stream
CVideoPlayer::SetCaching - caching state 2
CVideoPlayerVideo - Stillframe left, switching to normal playback
CVideoPlayer::HandleMessages - player started 2
CVideoPlayer::SetCaching - caching state 0
CVideoPlayerVideo - CDVDMsg::GENERAL_RESYNC(...)
```

### The measurement, redone

Same asset, same machine, same 5 s sampling — but with the wall clock taken
either side of the RPC rather than after it (the original loop's other flaw,
though it turned out not to be the one that mattered: RPC latency measured 1–8
ms throughout).

| | queue 1.0 s (as measured before) | queue 4.0 s (Kodi's default) |
|---|---|---|
| segment rate range | 0.754 – 1.486 | 0.977 – 1.021 |
| cumulative rate over ~110 s | 0.926 | **1.0003** |
| drift | **−9.1 s** | **−15 … +173 ms** |

A transcode holds real time as well as a direct stream does. The ±2 % per
segment is the position readout quantising on a 5 s sample, the same as
DirectStream shows.

### The actuator

Driving `inputstream.tempo` directly (`tools/tempoprobe.py`) — group, residual
estimator and pulse planner all taken out of it — a rate `r` held for `T`
should displace the content by `(r−1)×T`. On DirectStream it is exact:

| rate × 5 s | expected | head Δ | Kodi position − wall |
|---|---|---|---|
| 1.25× | +1250 ms | **+1250 ms** | +1251 ms |
| 0.80× | −1000 ms | −1007 ms | −1033 ms |
| 1.10× | +500 ms | +497 ms | +483 ms |
| 0.90× | −500 ms | −488 ms | −505 ms |

On a transcode, with the routing change that is still parked in a stash
(`TEMPO_METHODS` extended to `Transcode`, plus a `manifest_type` for the
ffmpeg open path):

| rate × 5 s | expected | head Δ, queue 1.0 s | head Δ, queue 4.0 s |
|---|---|---|---|
| 1.25× | +1250 ms | +775 | **+1203** |
| 0.80× | −1000 ms | −440 → −999 | −746 |
| 1.10× | +500 ms | +469 | +549 |
| 0.90× | −500 ms | −466 | −668 |

Zero stalls during the 4.0 s run, against a continuous stall cycle in the 1.0 s
one. The gross under-delivery is gone. What is left is scatter of up to ~35 %,
which is still well short of the ±1 % DirectStream manages and is **not
explained**. One candidate is visible in the data and untested: the add-on is
told `queue_secs = 1.0` through the ListItem property, published at join before
the queue was forced back, so its own accounting and the player's real 4 s
queue disagree.

### What this means for fine sync on a transcode

Not "a transcoding member cannot be synchronised". The obstacle is the queue
shortening itself: **1 s is below the segment duration**, so arming fine sync on
a transcoded stream is what breaks its playback. Any attempt to extend fine sync
to transcodes has to keep the queue at or above the segment duration and accept
that a pulse then takes a queue-depth longer to become audible — or not shorten
the queue for HLS at all.

The `±6 s` group swing with a transcoding member, and the "achieved 1.025×
when 1.250× was asked", were both measured on a member in this state.

### The defect behind it

`restore_queue()` cleared the record as soon as `set_kodi_setting` returned
true. That only means Kodi took the value into memory — a JSON-RPC settings
write reaches `guisettings.xml` when Kodi *saves*, so a Kodi killed in between
comes back up shortened with no record left to undo it. This is the second
device found in that state; the code comments already record the first. Fixed
in kofin: the record is kept until a later start can see the value really came
back. See `plugin.video.kofin#195`.

**Rig note:** check `videoplayer.queuetimesize` before trusting any timing
measurement from a member. Kodi 22's default is 40 (4.0 s); Kodi 21 has no such
setting and is fixed at 8 s.

---

## Still open

1. **The Tab and the Bravia** — the Bravia is in use; the Tab needs its
   wireless-debugging session restarted before it can rejoin the rig.
2. **H3** (server restart mid-group) — not attempted again. The one restart
   this session stopped Jellyfin outright and it needed a human with root.
3. **H4** (Android standby), **F8** (per-pulse against channel 2), **H8** (the
   60-minute soak), **Gate 0 channel 1** (camera) — all need hardware that left
   the rig.
4. **The residual ~35 % scatter in transcode pulse displacement** — §4. The
   gross failure is explained; this is not.
5. **A/B of the RT2 regression against 10.11.0.5** — §1.
