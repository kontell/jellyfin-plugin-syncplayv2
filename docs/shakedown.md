# SyncPlay v2 + kofin — shakedown plan

Server plugin (`Jellyfin.Plugin.SyncPlayV2` 10.11.0.4, Active on `minipie`
10.11.11) and its primary v2 client (`plugin.video.kofin` 0.21.0), exercised
together on real devices, real codecs, and a real network — including a
deliberately bad one.

This is a lengthy exercise by design. It is sequenced so that the cheap gates
that invalidate everything downstream run first.

---

## 0. Why Gate 0 exists

A run was observed in which **both members' OSD showed near-perfect sync while
the actual pictures were about 2 s apart.** Until that is explained, every
position number this system produces — the OSD, `Player.GetProperties.time`,
the client's report to the server, the group's own drift estimate, and the fine
sync scheduler's residual — is a measurement of the same possibly-lying
quantity. They agree with each other because they *share a source*, not because
they are right.

That is not a detail to fold into a scenario. It invalidates the pass/fail
criterion of every other test here, so it runs first and blocks the rest.

It also matters more than a reporting nuisance, because the reported position
is an **actuator input**, not just a readout:

* `PlaybackController.estimate_position_ms` / `_correct_drift` compare the group
  estimate against `player.getTime()`;
* `PulseScheduler` builds its 3 s median residual window from the same number
  and issues rate pulses to close it;
* the server's `WaitingGroupState` computes `delayTicks` from the position the
  client *reports*, and `CorrectionPolicy.CannotConverge` decides from that
  whether to keep seeking or hand the member to a rendezvous.

So a member whose reported position is biased by +2 s is not merely
mis-displayed. It will be actively driven 2 s away from the group by the
controller that believes it is closing the gap, and the server will see its
corrections "converge" onto the wrong place and stop correcting.

### 0.1 The three candidate mechanisms, all measurable, none decidable from logs

**(a) Transcode segment-boundary landing.** A seek on an HLS transcode lands on
a segment boundary, not where it was asked. The engine already says so in
`CorrectionPolicy.cs` ("a transcoded stream snaps to its segment boundary") and
`WaitingGroupState.cs:471-479`. If the client *reports* the position it
requested while *playing* the segment it actually got, the OSD is wrong by up to
one segment — and typical Jellyfin segment lengths (3 s, 6 s) bracket the
observed ~2 s.

**(b) Queue-depth accounting.** `inputstream.tempo` reports time at the playing
point by subtracting the queue depth stamped as `queue_secs`. `TempoSession`
publishes that once at group join (`tempo.py::_shorten_queue`) and every
subsequent play is stamped with it. The branches are careful — each returns the
depth *actually* in force — but the value is a snapshot:

* anything that changes `videoplayer.queuetimesize` after the join makes the
  stamp stale (bias = the difference);
* `OMEGA_QUEUE_SECS = 8.0` is the fallback whenever the RPC read fails or
  returns something unparseable, so a Kodi 22 box with a transient RPC failure
  reports 8 s of correction against a 1 s queue — **7 s of silent bias**;
* Kodi 21 (fixed 8 s) and Kodi 22 (`syncPlayShortQueue` → 1 s) in one group are
  each individually correct but wildly different, so a single mis-stamp on
  either side is large.

**(c) Output latency the position API cannot see.** Audio sink buffering, HDMI
passthrough to an AVR, and the Bravia's own picture processing all delay what
you see and hear relative to what Kodi's clock says. Individually hundreds of
ms; on a TV with processing on, more.

These are distinguishable only by measuring the **output**. That is Gate 0.

---

## 1. Gate 0 — establish output truth (blocking)

**Goal:** a trusted, repeatable measurement of where a member's picture and
sound actually are, independent of any position API, plus a per-device
`bias_ms` (reported − actual) for every playback path. Nothing downstream is
believed until this passes.

Three channels, in descending trust. Every later scenario states its pass/fail
in channel-1 or channel-2 terms and records the reported-position number
*alongside* as a second measurement — where the two disagree, the disagreement
is the finding.

### Channel 1 — camera, both panels in one frame (ground truth)

Two devices side by side playing the timecode asset (§3), one photo containing
both burned-in timecodes. Frame-exact, unfalsifiable, and it is the thing the
user actually complained about. Manual, so it is the arbiter rather than the
workhorse: used to validate channels 2 and 3, then spot-checked.

### Channel 2 — screenshot + OCR (the automatable detector)

This is the answer to "some way of being able to detect this".

Kodi's `TakeScreenshot` captures a rendered frame. OCR the burned-in timecode
out of it and compare against `Player.GetProperties.time` sampled at the same
instant on the **same device**:

```
bias_ms = reported_position_ms − ocr_timecode_ms
```

That single number is exactly the lie. It needs no second device, no camera,
and no group — so it can run as a preflight on every box and as a periodic
watchdog inside any long run.

**Prove the channel before trusting it.** On some platforms and some render
paths a Kodi screenshot contains the GUI layer without the video surface (or
the reverse). Verify per device that a screenshot of a playing timecode clip
actually contains a readable timecode, at each of: Kodi 21 / Kodi 22, direct
play / transcode, with and without `inputstream.tempo` in the path. Any device
where the screenshot has no video layer falls back to channel 1 for that path
and is recorded as such. (`kodi-drive:kodi-screenshot-review` covers when to
shoot so an animation or the debug overlay is not captured; the overlay must be
off.)

Calibration matrix per device — `bias_ms` for each:

| path | why it can differ |
|---|---|
| DirectPlay, no tempo | the baseline; should be ≈ output latency only |
| DirectStream, tempo armed | the `queue_secs` correction is in force |
| DirectStream, tempo armed, mid-pulse | the correction during a rate excursion |
| Transcode (no tempo — see §7) | segment-boundary landing |
| after a seek, each of the above | the landing error is the suspect |

### Channel 3 — audio cross-correlation (what the user hears)

One recorder, both devices audible, cross-correlate the asset's 1 Hz beeps.
Sub-millisecond, and it measures the sound rather than the frame — which is the
channel that catches sink and passthrough latency that channel 2 cannot see.

### Gate 0 exit criteria

1. Channel 2 is proven to capture video on each device/path, or explicitly
   recorded as unavailable there.
2. `bias_ms` measured for every device × path cell, with its spread over ≥ 10
   samples.
3. Channels 1 and 2 agree within one frame (~42 ms) on at least one cell per
   device — otherwise channel 2 is not measuring what we think.
4. **The ~2 s observation is reproduced and attributed** to (a), (b), (c), or
   something else. If it will not reproduce, the conditions that produced it
   are recorded and it stays open, but the shakedown proceeds with the bias
   watchdog armed.
5. A bias watchdog exists: a periodic screenshot+OCR check that any run can
   enable, alarming when `bias_ms` moves outside its calibrated band.

**If `bias_ms` is nonzero and stable, it is applied to every reported position
before members are compared.** If it is nonzero and *unstable*, fine sync must
not be trusted to close anything on that path, and that is a finding with more
weight than any scenario below.

---

## 2. The rig

| id | device | Kodi | role | availability |
|---|---|---|---|---|
| **L22** | this box, flatpak `tv.kodi.Kodi` | 22.0~b1 Piers | primary local member | now |
| **L21** | this box, Debian package | 21.3 Omega | Kodi 21 arm (8 s queue, no short-queue path) | now, **not concurrently with L22** |
| **TAB** | Galaxy Tab S5e (SM-T720), Android 13 | 22 | second member, ADB `192.168.1.150:42753` | attached now |
| **BRV** | Bravia Android TV | 22 | third member; 16 s native queue, picture processing | when available |
| **PXL** | Pixel 7 Pro | 22 | AV1 decoder regression target | when available |
| **WEB** | jellyfin-web via Caddy | — | **the v1 member** | always |

L21 and L22 share a display and a JSON-RPC port, so the Kodi 21 arm is a
separate pass, not a concurrent member.

**WEB is not optional.** Constraint 2 of the whole design is that one group
holds v1 and v2 members at once, and hot join and rendezvous are v2-only paths
with deliberate v1 fallbacks. A group without a v1 member never tests the
asymmetry.

Server: `minipie` 10.11.11 at `192.168.1.167:8096`, SyncPlay v2 10.11.0.4
Active, `POST /SyncPlay/Hello` → 200. Also reachable at
`https://jelly.konell.xyz` through Caddy — worth one pass each way, since the
proxy path (§4) bypasses Caddy.

---

## 3. Test assets

The server's `VideoCodecs` filter was verified non-functional on this
deployment (`&VideoCodecs=av1|hevc|h264` all return the same 6297 items), so
codec coverage cannot come from the existing library — the codec under test has
to be **known**, not searched for.

Build one timecode clip per codec, extending the recipe already used for the
drift gate (`plugin.video.kofin/docs/syncplay-drift-shakedown.md` §4). Same
source, same burned-in `%{pts\:hms}` + frame counter, same 1 Hz beep, same
**23.976 fps** — deliberately, because it is what real films are and the rate
with no matching mode on a 60 Hz panel:

| asset | video | why |
|---|---|---|
| `Timecode H264` | libx264 High/L4.0 yuv420p | direct play everywhere; the control |
| `Timecode HEVC` | libx265 Main/Main10 | direct play on the Android boxes, transcode target on some |
| `Timecode AV1` | libsvtav1 | the **PXL decoder regression**: rate change + seek wedged its AV1 decoder before inputstream.tempo x.4.1 |
| `Timecode H264 (transcode bait)` | as H264 but in a container/profile the device profile rejects | forces a server transcode without needing `force_transcode` |

Keep stereo AAC on all four so audio never becomes the variable, and keep one
HEVC/TrueHD title for the passthrough arm of Gate 0 channel 3.

The burned-in timecode is what makes channels 1 and 2 possible at all, so every
run below uses these assets, not library content.

---

## 4. Tooling (both built and verified on this rig)

### 4.1 `tools/wanshape.py` — the WAN proxy

A layer-4 TCP proxy. Point **one member's** `serverAddress` at it and that
member alone becomes remote; the rest of the group keeps its LAN.

Layer 4 rather than an HTTP proxy because kofin's control channel (the SyncPlay
websocket) and its media channel (the range/HLS GETs) are the same host and
port, and the websocket stops looking like HTTP after the upgrade. Forwarding
bytes shapes both with nothing to parse. Not `tc netem`, because that shapes a
whole interface — it cannot make one member remote while its group stays local
— and it needs root, which an unrooted Android TV does not offer.

```sh
tools/wanshape.py --listen 0.0.0.0:8099 --target 192.168.1.167:8096 \
                  --control 127.0.0.1:8098 --rtt 300 --down 3000

tools/wanshape.py --send 'stall 12'       # hold data, sockets stay up
tools/wanshape.py --send 'blackhole 120'  # cut connections, refuse new ones
tools/wanshape.py --send 'down 800'
tools/wanshape.py --send status
```

Then set that member's `serverAddress` to `http://192.168.1.112:8099`.

**Two injectors, because the engine has two paths and they are reached
differently:**

| injector | what survives | engine path it drives |
|---|---|---|
| `stall N` | session + websocket stay up; data held | member reports Buffering → group waits → `GroupWaitTimeout` (10 s) → **rendezvous on timeout** |
| `blackhole N` | connections aborted, new ones refused | socket dies with no close → `SocketLiveness` (60 s) → `DisconnectedGracePeriod` (90 s) → reconnect snapshot |

Measured on this rig during development:

| behaviour | result |
|---|---|
| `--rtt 300` | 0.0027 s → 0.307 s per request |
| `--rtt 400` | 0.0057 s → 0.406 s, payload byte-identical |
| `down 2000` (target 250 000 B/s) | 249 414 B/s — 0.2 % error |
| `stall 5` mid-transfer | 0.975 s → 5.716 s, **HTTP 200** (socket survived) |
| `blackhole 3` | curl exit 56 (reset), clean recovery after expiry |
| connection accounting | `conns` returns to 0; no leak |

Caveat: the proxy speaks plain TCP to `192.168.1.167:8096`, so a shaped member
bypasses Caddy and TLS. Run the Caddy path unshaped as its own arm rather than
mixing the two.

### 4.2 `tools/syncwatch.py` — watching a run live

**A run is watched, not autopsied.** The ~2 s divergence in §0 survived an
entire session precisely because everything was read back from logs afterwards,
by which time the only surviving evidence was the number that was lying. Every
scenario below is run with `syncwatch` attached and its output in front of you.

It samples every member over JSON-RPC and the server over `/SyncPlay/List`,
reconciles them, and emits a line the moment anything is worth knowing — group
state changes, member state transitions, divergence crossing tolerance, a
member that stops answering, and the reported-vs-screen bias. Every sample is
also written to JSONL for the post-mortem; stdout carries only events.

```sh
tools/syncwatch.py \
    --server http://192.168.1.167:8096 --token-from <settings.xml> \
    --member L22=127.0.0.1:8080 \
    --member TAB=192.168.1.150:8080,adb=192.168.1.150:42753,bias=-180 \
    --tolerance 250 --jsonl run.jsonl --ocr '<ocr command for {member}>'
```

Three properties make it usable during a run rather than after one:

* **Silence is never success.** A heartbeat is emitted on a fixed interval
  regardless of activity, so a dead poller, a hung Kodi and a healthy quiet
  group cannot look the same. This is the failure mode a plain background shell
  has, and the reason one is not good enough here.
* **Every terminal condition emits**, not just the happy path — `LOST` when a
  member stops answering, `SERVER` when the group list errors, `STATE` on any
  transition. A watcher that only reports good news is indistinguishable from a
  watcher that has died.
* **It carries the Gate 0 channel.** `--ocr` reads the burned-in timecode back
  off a screenshot every `--ocr-every` seconds and reports `BIAS` whenever a
  member's reported position moves away from what its own screen shows. That is
  the live form of the §1 detector: had it been running, the ~2 s would have
  announced itself instead of being discovered by eye. A calibrated `bias=<ms>`
  per member is subtracted before members are compared; the raw figure stays in
  the JSONL.

Event kinds: `START` `STATE` `GROUP` `DIVERGE` `INBAND` `BIAS` `OCR?` `LOST`
`BACK` `SERVER` `HB` `STOP`.

Verified on this rig: members reachable and unreachable, server reachable,
heartbeat, JSONL, and the `LOST` path on a deliberately dead member.

---

## 5. Preflight (per device, before any run)

1. **`inputstream.tempo` ≥ x.4.1** on its channel — below that the add-on drops
   the first packets after a seek (the cause of the PXL AV1 wedge) and
   `TempoSession.begin()` refuses to arm. Confirm via `Addons.GetAddonDetails`,
   and confirm from the log that fine sync actually armed rather than silently
   declining.
2. **`videoplayer.usedisplayasclock` OFF.** The drift gate measured up to
   +42 763 ppm of rate error with it on (the PAL speed-up, 4.3 % on the Bravia),
   against a few hundred ppm free-running with it off. Fine sync's give-up path
   exists for exactly this and will fire.
3. **Record `videoplayer.queuetimesize` before the run and after** — 40 (4.0 s)
   on desktop and Tab, 160 (16.0 s) on the Bravia. Check `syncPlayQueueRestore`
   is empty at start; a non-empty one means a previous session died mid-run.
   This is Gate 0 hypothesis (b)'s primary evidence.
4. **Debug logging on, on-screen overlay off** (the overlay corrupts channel 2).
5. **Screen-off timeouts longer than the run**, screensaver `None`, and on
   Android `svc power stayon true` while charging. Record and restore.
6. **Same Jellyfin user on every member** for the whole exercise — user-level
   playback settings change the transcode decision.
7. Note PXL thermals; an hour of AV1 will throttle it, and that is a real-world
   condition, not an artefact.
8. **`syncwatch` running and its events visible before the scenario starts** —
   not started alongside it, and never left to be read back at the end.

---

## 6. Scenario matrix

Every cell is run with **`syncwatch` attached and its output watched live**
(§4.2), and records: channel-1/2 offset (truth), reported offset (the API),
`bias_ms`, and the relevant log lines from both sides. **A cell where truth and
report disagree is a failure even if the reported number looks perfect** — that
is the whole point of Gate 0.

### R-A — group formation from each starting state

The user's three, plus the one that falls out of the code.

| id | starting state | what it exercises |
|---|---|---|
| A1 | nothing playing, group created | `IdleGroupState`; queue proposal from empty |
| A2 | **video playing**, group created | the running item is **not** routed through `inputstream.tempo` — the route is stamped at play time from `state.syncplay_tempo()`, which is empty before the join. So the first item of a group formed this way is command-only even with fine sync on. Verify it is logged, and measure how much worse it syncs. |
| A3 | **video paused**, group created | `PausedGroupState` entry; the paused position becomes the group position |
| A4 | group created, second member joins, *then* play starts | the clean path; the control for A2 |

A2 is the interesting one and is easy to miss: it is a real user gesture ("we're
both watching, let's sync up") that silently lands on the degraded path.

### R-B — joining

| id | scenario | notes |
|---|---|---|
| B1 | join an Idle group | baseline |
| B2 | join a Paused group | |
| B3 | **hot join a Playing group** (`HotJoin=true`, default) | `BeginHotJoin` sets `IgnoreGroupWait`, pushes a snapshot; the joiner's `Ready` is answered by `CompleteHotJoin` with a private scheduled `Unpause` at the live position. Nobody else should pause. |
| B4 | same with `HotJoin=false` | classic barrier; the whole group waits. Config-gate regression. |
| B5 | **v1 member (WEB) hot joins** | must fall back to the barrier — v1 cannot be told any of it |
| B6 | join while another member is mid-rendezvous | two members on the hot-join path at once |
| B7 | rejoin the same group after leaving | `TempoSession.begin()` is join-scoped, not re-join-scoped |

### R-C — spectator (`IgnoreWait`)

| id | scenario | notes |
|---|---|---|
| C1 | → spectator while the group plays | `toggle_spectator` → `SetIgnoreWait`; the group must stop waiting on it |
| C2 | spectator plays its own media | `manager.py:993` — "a spectator watching their own thing"; its plays must **not** be forwarded to the group (`:1123`, `:1160`) |
| C3 | spectator → back | re-attach; `_leave_locally` resets `ignore_wait`. Does the member get a snapshot and land correctly? |
| C4 | toggle spectator **during** a hot join or rendezvous | `BeginHotJoin` sets `IgnoreGroupWait = true`; an explicit `SetIgnoreGroupWait(false)` from the user (`Group.cs:837`) writes the same field with no knowledge of the hot join. **Suspected interaction** — a user coming back from spectator mid-rendezvous may clear the flag the rendezvous is relying on. Worth deliberate provocation. |
| C5 | spectator when the group changes item | queue update to a member that is not following |
| C6 | last non-spectator leaves; group is all spectators | who holds the group position |

### R-D — impaired network (`wanshape`)

The tolerance the server grants a member is `clamp(2 × ping, 500, 2000)` ms
(`Group.cs:984`), so RTT sweeps it directly:

| RTT | member ping | tolerance | cell |
|---|---|---|---|
| 0 | ~0 | 500 (floor) | D1 baseline |
| 300 | ~150 | 500 (still floored) | D2 |
| 1000 | ~500 | 1000 | D3 |
| 2500 | ~1250 | 2000 (saturated) | D4 |

| id | injection | what it must do |
|---|---|---|
| D1–D4 | `rtt` sweep above | tolerance tracks; unpause lead scales; no spurious corrections |
| D5 | `down` below the asset bitrate | member buffers → `BufferingGracePeriod` (2 s) defers the report → group waits |
| D6 | `stall 12` (> the 10 s `GroupWaitTimeout`) | **rendezvous on wait timeout.** Group must resume without the stalled member; the member gets a snapshot and a private scheduled `Unpause`. This is the path the recent work added — and per its own commit note, the *only* path a slow-reloading member actually reaches. |
| D7 | `stall 6` (< 10 s) | group waits and resumes normally; **no** rendezvous |
| D8 | `blackhole 30` | socket dies; `SocketLiveness` (60 s) marks the member disconnected; reconnect inside `DisconnectedGracePeriod` (90 s) → `ReconnectSession` → snapshot |
| D9 | `blackhole 120` | grace expires → member removed. Client `_attempt_rejoin` (≥ 30 s apart) must recover it cleanly. |
| D10 | `rtt 800` + `down` cap together, sustained 30 min | the realistic remote member; soak for divergence |
| D11 | shaped member **plus** a transcode | §7's worst case: slow link and a stream that cannot seek |

D6/D7 as a pair are the important ones — they bracket the timeout and prove the
rendezvous fires when it should and *only* when it should.

**Also check here:** `GetHighestPing()` returning 0. On an all-LAN group every
member may report ping 0, and `WaitingGroupState.cs:540` floors `delayTicks`
against `context.DefaultPing`'s raw `500` — which is 500 **ticks**, 0.05 ms, not
500 ms. The clamp only bites when the highest ping is 0, and then the intended
500 ms recovery grace becomes 0.05 ms. Record the reported ping values on a
quiet LAN; if they are 0, this is reachable in normal use. (Upstream Jellyfin's
line, verbatim — present in 10.11 and v12 — not introduced here.)

### R-E — correction and rendezvous

| id | scenario | notes |
|---|---|---|
| E1 | group Seek with one member slow to land | first correction is always granted (`CannotConverge` returns false while `attempts < 2`) |
| E2 | member converging slowly but genuinely | must **not** rendezvous: each correction closes ≥ 250 ms (`ProgressTicks`) |
| E3 | member not converging (transcode) | `MaxAttempts = 3` or sub-250 ms progress → rendezvous, group released by `ResumeIfNobodyElseIsWaiting` |
| E4 | member overshooting | `CannotConverge` uses `Math.Abs` on both sides — overshoot by 4 s must count as failure, not progress |
| E5 | rendezvous with a **v1** member | must abandon as before (`MarkIgnoredByTimeout`), never rendezvous |
| E6 | both rendezvous paths in one session | the correction path (`WaitingGroupState`) and the timeout path (`SyncPlayManagerV2` sweep) are different code with different triggers |

E2 is the one `wanshape` makes reachable at all: without a way to make a member
slow-but-converging, only the transcode failure mode gets tested, and the
policy's *negative* case never runs.

### R-F — fine sync (where it is armed)

| id | scenario |
|---|---|
| F1 | routed item (DirectPlay/DirectStream video): pulses close an injected residual; `moved` ≈ `wanted` |
| F2 | deadband (75 ms) holds; no pulsing against noise |
| F3 | residual > pulse budget (2500 ms default) → one `[ syncplay/align ]` seek, then ≤ 1 pulse |
| F4 | commands cut pulses (`cut by Pause`), file returns to 1.0 |
| F5 | queue set at join / restored at leave; restore after a forced kill |
| F6 | give-up on a one-signed regrowing residual (turn `usedisplayasclock` on for one member) |
| F7 | `inputstream.tempo` absent, disabled, or < x.4.1 → does not arm, group behaves as command-only |
| F8 | **each pulse verified against channel 2**, not just against `moved`/`wanted` — this is where Gate 0 pays off |

### R-G — mixed v1/v2

| id | scenario |
|---|---|
| G1 | WEB (v1) + kofin (v2) in one group, all of R-A |
| G2 | v2-only messages (`StateSnapshot`, `PositionBeacon`) never reach WEB; WEB shows no console errors |
| G3 | WEB drives (pause/seek/queue) and kofin follows |
| G4 | kofin drives and WEB follows |
| G5 | negotiation: `ProtocolVersionRegistry` TTL is 12 h keyed on client+device — downgrade a device to v1 and confirm the fallback, then re-negotiate |
| G6 | `PositionBeacon` every 5 s reaches v2 members only |

### R-H — resilience

| id | scenario |
|---|---|
| H1 | kofin service restarted mid-group |
| H2 | Kodi force-stopped mid-group → `syncPlayQueueRestore` recovers the queue at next start |
| H3 | server restarted mid-group |
| H4 | Android standby / wake with the group running (`on_sleep` / `on_wake`) |
| H5 | two members leave simultaneously |
| H6 | group destroyed while a member is inside its disconnect grace |
| H7 | `StateVersion` ordering — snapshot arriving after a newer beacon must not regress state |
| H8 | 60-minute soak, all available members, bias watchdog armed |

### R-I — codec matrix

Every codec × every device × {direct, transcode}, with H264 as the control:

| | L22 | L21 | TAB | BRV | PXL |
|---|---|---|---|---|---|
| H264 direct | | | | | |
| HEVC direct | | | | | |
| AV1 direct | | | | | **regression cell** |
| any → transcode | | | | | |

The PXL/AV1 cell is a named regression: rate change followed by a seek
previously wedged its AV1 decoder (`dequeueInputBuffer` storm, frozen picture
with the clock running, and a watchdog reboot on the second run). Fixed
add-on-side in `inputstream.tempo` 22.4.1/21.4.1. Run F1/F3 on that cell
specifically, and check the decoder error count is zero — the desktop's
post-seek `[hevc] Could not find ref with POC` errors were the same root cause
and are a cheaper canary for it.

---

## 7. Transcoding assessment

**Correction to the brief: fine sync cannot currently be enabled for transcoded
playback, and no setting turns it on.**

`plugin.video.kofin/lib/kofin/plugin/play.py:54`:

```python
TEMPO_METHODS = frozenset({"DirectPlay", "DirectStream"})
```

`tempo_route()` returns `None` for anything else, so no `inputstream.tempo`
properties are stamped, the claim carries no `Tempo`, and `PulseScheduler` never
arms for that item. `docs/syncplay-fine-sync.md` §2 states the reason: the
add-on's HLS path is unqualified.

So a transcoding member gets **command-only sync**, and that is the floor this
shakedown must measure rather than assume away:

| id | scenario | what it measures |
|---|---|---|
| T1 | forced transcode, steady state, no commands | how far a command-only member drifts over 30 min, in channel-1/2 terms |
| T2 | group Seek to a transcoding member | `reload_current_item` — the seek path restarts the stream at the target because an in-stream seek cannot be accurate. Measure the gap it leaves and how it is closed. |
| T3 | load allowance convergence | `_load_allowance_ms` measured ~9 s for a transcode start, EMA at 0.5, capped at 15 s. Confirm it converges within one item and that the first item of a session is the bad one. |
| T4 | T3 under `wanshape` | a throttled link lengthens the load, so the allowance grows — does it stay inside the 15 s cap and still aim correctly? |
| T5 | transcode → rendezvous | the motivating case: correction cannot converge, so the member should be rendezvoused rather than seeked repeatedly |
| T6 | mixed group: one direct member (fine sync armed) + one transcoding member (command-only) | the realistic bad case, and the one most likely to reproduce the ~2 s complaint |
| T7 | **Gate 0 (a) specifically**: does a transcoding member report the position it requested while playing the segment it got? | the leading hypothesis for the original observation |

T7 is the highest-value single test in this document. If it confirms, the fix is
on the client — report the position actually playing, not the one requested —
and it would also explain why the server's corrections appear to converge onto
the wrong place.

**The open question this exercise should answer, not assume:** is extending
`inputstream.tempo` to the HLS path worth doing? That is a prerequisite study on
the add-on side (its §4.7), not something this shakedown can enable. What this
shakedown can supply is the number that decides it — T1's measured
command-only drift. If command-only holds a transcode inside a frame or two,
the extension is not worth it; if it does not, T1 is the justification.

---

## 8. Known hazards

* **`pkill -f wanshape`** matches the shell running it and kills your own
  session. Use the pidfile. (Cost me a shell during development;
  `kodi-drive:kodi-process-control` documents the general form.)
* JSON-RPC setting writes are **not saved to disk** by Kodi, so a queue
  shortening can survive a clean exit with no record. Check
  `syncPlayQueueRestore` before and after every run.
* An empty `videoscreen.whitelist` (Kodi's default) disables mode switching
  entirely, and on Android the display mode is the platform's decision anyway —
  so a content/refresh mismatch is not configurable away on this hardware.
  Keep `usedisplayasclock` off and the question does not arise.
* The debug overlay corrupts channel 2. Off before every screenshot.
* PXL thermal throttling on long AV1 runs — sample `dumpsys thermalservice`
  alongside, and do not mistake it for a sync defect.
* `adb shell` escaping silently answers zero on some forms; verify each
  command's output rather than its exit code.
* A device that blanks mid-run reads as a hung Kodi to any poller.

---

## 9. Sequencing

| phase | contents | blocking? |
|---|---|---|
| 0 | **Gate 0** — output truth, `bias_ms`, reproduce the 2 s | **yes** |
| 1 | Assets (§3), preflight (§5), `wanshape` wired to TAB, `syncwatch` armed | yes |
| 2 | R-A, R-B, R-C on L22 + TAB + WEB | |
| 3 | R-D (the proxy work) and R-E — the rendezvous pair D6/D7 first | |
| 4 | R-F and §7 transcoding, T7 early | |
| 5 | R-G mixed v1/v2 | |
| 6 | R-I codec matrix as BRV and PXL become available | |
| 7 | R-H resilience, ending with the H8 soak | |

Phases 2–5 run on what is available now (L22, TAB, WEB). BRV and PXL gate only
R-I and the wider arms of R-D/R-F.

## 10. Exit criteria

1. Gate 0 passed: output truth established, `bias_ms` known per device × path,
   and the ~2 s observation attributed or explicitly left open with its
   conditions recorded.
2. Every R-A/R-B/R-C cell passes **in channel-1/2 terms** on at least L22 + TAB
   + WEB.
3. D6/D7 demonstrate the rendezvous fires at the wait timeout and not before;
   D8/D9 demonstrate the disconnect grace and rejoin.
4. E2 demonstrates the *negative* case — a slow but converging member is not
   rendezvoused.
5. G1–G4 pass with a v1 member present throughout.
6. §7 yields a measured command-only drift figure for transcodes, and a
   yes/no on T7.
7. R-I complete for every available device, with the PXL/AV1 cell clean.
8. H8 soak completes with the bias watchdog quiet — and quiet is only
   meaningful because `syncwatch` heartbeats throughout, so a silent watcher
   cannot be mistaken for a synchronised group.

Results go to `plugin.video.kofin/tests/live/results/` alongside the existing
S4.x gates, and anything learned about Kodi itself goes to `kodi-drive` rather
than into a `CLAUDE.md`.
