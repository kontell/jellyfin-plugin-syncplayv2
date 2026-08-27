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

## 4. Transcoding: the parked question, answered

The parked finding was that fine sync arms on a transcoded stream and pulses
fire, but the displacement comes back 10–20 % short and once in the wrong
direction. Three explanations were open: the actuator does not shift an HLS
stream, the group loop measures the wrong thing, or the head counters are read
wrongly.

**None of them. The transcoded stream does not play at real time.**

The experiment drives `inputstream.tempo` directly (`tools/tempoprobe.py`),
which takes the group, the residual estimator and the pulse planner out of it:
start playback in a group so the route is stamped, then *leave* the group so
kofin's own scheduler stops writing the file, then write rates by hand and read
back the add-on's head counters (`content_ms − output_ms`), Kodi's reported
position, and the wall clock. A rate `r` held for `T` should displace the
content by `(r−1)×T` on all three.

Doing this inside the group first produced nonsense, because kofin's scheduler
and the probe were fighting over the same file — its pulses are in the log
interleaved with the probe's. That is worth stating: the first version of this
experiment measured the two of us, not the actuator.

**DirectStream — the actuator is exact.**

| rate × 5 s | expected | head Δ | Kodi position − wall |
|---|---|---|---|
| 1.25× | +1250 ms | **+1250 ms** | +1251 ms |
| 0.80× | −1000 ms | −1007 ms | −1033 ms |
| 1.10× | +500 ms | +497 ms | +483 ms |
| 0.90× | −500 ms | −488 ms | −505 ms |

Two independent channels agree with the ask to within 1 %.

**Transcode — the actuator still delivers; the stream does not hold rate.**
Head Δ came back −999 / +469 / −466 against asks of −1000 / +500 / −500. But
the *null* trial — rate exactly 1.0, no pulse at all — showed the position
losing **1.1–1.3 s per 10.5 s**, with `Stillframe left`, `SetCaching` and
`GENERAL_RESYNC` in the log.

So the control that settles it: the same transcode with `inputstream.tempo`
**out of the pipeline entirely**, sampled every 5 s for two minutes, against
DirectStream on the same machine and asset.

| | transcode, no tempo | DirectStream |
|---|---|---|
| segment rate range | **0.754 – 1.486** | 0.976 – 1.018 |
| cumulative rate | **0.926** | **1.0000** |
| worst drift | **−9.1 s in 115 s** | **±95 ms in 105 s** |

The DirectStream ±2 % is the position readout alternating on a 5 s sample; its
cumulative rate is 1.0000 and its drift never leaves ±100 ms. The transcode
runs ~15 % slow for tens of seconds at a time and then catches up in bursts of
~40 % — a saw-tooth, not a rate offset.

**And the readout is honest.** Three paired samples of the burned-in timecode
against the reported position, on the transcode:

| screen | reported | difference |
|---|---|---|
| 302 172 ms | 302 013 ms | +159 ms |
| 333 036 ms | 333 032 ms | +4 ms |
| 354 140 ms | 354 003 ms | +137 ms |

That answers **T7** — a transcoding member does report the position it is
showing — and it means the wander is real playback, not a reporting artefact.

**What this means for fine sync.** Every parked observation follows:

* "requested 1.250× → achieved 1.025×" was an achieved *rate* inferred from
  position deltas over a window in which the stream's own rate error dominated.
* "requested 0.969× → achieved 1.016×, wrong direction" is a −3 % ask inside a
  +40 % catch-up burst.
* "10–20 % short regardless of magnitude" is ±5 % of a 5 s window — an additive
  disturbance that looks proportional when the pulse length is fixed.
* "group offset swung ±6 s with a transcoding member" is the saw-tooth.

The pulse budget is 25 % for at most 10 s, so at most 2.5 s of displacement per
pulse followed by a settle window. Against a member losing 8–9 s a minute and
returning it in bursts, that is an order of magnitude short. **Raising the
budget would not help and should not be done**: the disturbance is not a
constant rate error, so a bigger pulse would overshoot the catch-up bursts as
badly as it undershoots the stalls.

The open question is now a different one, and it is not a SyncPlay question:
why does a transcoded HLS stream on this rig fail to hold real time when the
server encodes at ~18× and the client is not reporting a cache stall? Until
that is answered, a transcoding member cannot be finely synchronised by any
actuator, and the honest behaviour for the group is the one it already has —
rendezvous it rather than seek it repeatedly.

---

## Still open

1. **The Tab and the Bravia** — the Bravia is in use; the Tab needs its
   wireless-debugging session restarted before it can rejoin the rig.
2. **H3** (server restart mid-group) — not attempted again. The one restart
   this session stopped Jellyfin outright and it needed a human with root.
3. **H4** (Android standby), **F8** (per-pulse against channel 2), **H8** (the
   60-minute soak), **Gate 0 channel 1** (camera) — all need hardware that left
   the rig.
4. **Why a transcode does not hold real time** — §4.
5. **A/B of the RT2 regression against 10.11.0.5** — §1.
