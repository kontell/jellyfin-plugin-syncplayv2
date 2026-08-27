# Gate 0 results — output truth

Run 2026-08-26 against `minipie` 10.11.11, SyncPlay v2 plugin 10.11.0.4, kofin
0.21.0. Members: **L22** (flatpak Kodi 22.0b1, this box) and **TAB** (Galaxy Tab
S5e, Kodi 22.0b1). Asset: `SyncPlay Timecode` — h264 High 1080p @ 23.976,
6.8 Mbps, stereo AAC, 10 min, burned-in `HH:MM:SS.mmm` + frame counter.

**Verdict: Gate 0 partially passed. The channel is established and calibrated on
both available devices. The ~2 s divergence did NOT reproduce in any
configuration tested — the worst steady-state disagreement measured is 275 ms —
but the mechanism that would produce it is confirmed present and is shown to
scale with the transcode path and with a seek.**

---

## 1. Channel 2 is established — but not the way the plan assumed

**Kodi's own screenshot is unusable on L22.** `Input.ExecuteAction
{"action":"screenshot"}` returns `OK` and writes nothing. Three consecutive
attempts, three failures in `kodi.log`:

```
error <general>: KODI::RENDERING::CAPTURE::CCaptureService::Fail(...):
capture request failed (content 0, 0x0)
```

Kodi 22.0-BETA1 (`20260825-ab5284f`), flatpak, X11 session. So the capture has
to come from outside Kodi. That turned out to be better anyway — it is uniform
across platforms and it does not depend on a Kodi subsystem under test.

| device | working capture | timed window | notes |
|---|---|---|---|
| L22 | `import -window 0x9200002` (ImageMagick, X11) | **~140–210 ms** | window, not root: root is 3840×2160 and took 2.6 s, and grabbing it also captures the user's desktop |
| TAB | `adb shell screencap <file>` (raw), pull + convert afterwards | **~500 ms** | `screencap -p` costs 1131 ms because of the on-device PNG encode; raw is 501 ms |

`adb screencap` **does** capture the video surface on the Tab — no black-frame
or secure-surface problem on this device.

### 1.1 The OCR recipe that actually works

Greyscale + threshold fails on this asset. The timecode is white text in a
semi-transparent box over saturated colour bars; on the Tab the bars convert to
a light grey and every threshold blew the crop out to solid white.

**`min(R,G,B)` separates them** — high only where all three channels are high,
so white text survives and every saturated bar collapses to near zero. It reads
on both devices, and as a by-product gets the frame counter right where
greyscale misread `f=2791` as `f=2797`.

Also load-bearing, and non-obvious: **tesseract returns an empty string on a
clean, correctly-thresholded crop that runs to the image edge.** It reads it
perfectly once a white margin is added. `-bordercolor white -border 30`.

The ladder ends with thresholds down to 35% because **a Kodi dialog dims the
video behind it**, dropping white text to ~45% and making every higher
threshold miss.

### 1.2 The measurement trap that produced two false readings

Two successive false results before the estimator was right, both worth
recording because both looked plausible:

1. **OCR inside the timed bracket.** First run read a confident **+500 ms**
   bias. It was exactly half the OCR ladder's own runtime — bias tracked
   bracket width at almost exactly `bracket/2`. Fix: time only the capture.
2. **Fetch inside the timed bracket.** The raw-adb path then read **+985 ms**
   with a 2.7 s window, because the pull and the local decode were still inside
   the timed subprocess. Fix: split the on-device grab (timed) from the fetch
   and decode (untimed).

**Where in the capture call the frame is sampled was then measured, not
assumed.** Running the same device at two window sizes:

| TAB capture | window | bias if frame at midpoint | bias if frame at **start** |
|---|---|---|---|
| raw grab | 500 ms | −102 ms | **−352 ms** |
| png encode | 1250 ms | +257 ms | **−368 ms** |

The start model agrees to 16 ms; the midpoint model disagrees by 359 ms. The
frame is sampled at the start of the capture call, and `biasprobe --capture-at`
defaults to 0.0 on that evidence.

---

## 2. Per-device bias (reported − on screen)

| cell | median | sd | n |
|---|---|---|---|
| L22, kofin DirectStream, no group, no tempo | **−264 ms** | 62 | 10 |
| TAB, kofin DirectStream, no group, no tempo | **−384 ms** | 79 | 10 |

Negative = the screen is **ahead** of the reported position, i.e. Kodi reports a
position slightly behind what it is displaying. Both sub-half-second, both
stable, and the two devices differ from each other by ~120 ms — which is a real
inter-device output-latency difference and is what a group inherits.

---

## 3. Group measurements — screen vs API

Measured with `screendiff`, which compares the two members' **screens** directly
and needs no bias model, reporting the API's answer beside it.

| configuration | on screen | API says | disagreement |
|---|---|---|---|
| both DirectStream + fine sync | −195 ms | −335 ms | **+139 ms** |
| L22 DirectStream+tempo, TAB **Transcode** | −75 ms | −350 ms | **+275 ms** |
| same, immediately after a group seek | −416 ms | −687 ms | **+271 ms** |

Predicted disagreement from §2 alone is −264 − (−384) = **+120 ms**, which
matches the direct/direct case (+139 ms) closely. **The transcode adds roughly a
further 135 ms of reported-vs-actual error.**

The transient at the seek reached **−1225 ms** before settling to −270 ms —
caught live by `syncwatch`, not found afterwards in a log.

### 3.1 kofin already knows the transcode lands off

The seek made the mechanism explicit. On the transcoding member:

```
[ syncplay/landed ] 420.3s, wanted 420.0s (+320ms) transcoding
[ syncplay/align ] -317ms carried: transcoding
```

It measures the landing error and **carries** it rather than correcting — a
deliberate choice, since re-seeking a transcode lands somewhere else again. That
carried offset is real displacement that the reported position does not show,
and it is the confirmed contributor behind the +275 ms.

Meanwhile the direct member resumed 815 ms out and handed it to fine sync:

```
[ syncplay/resumed ] residual +815ms (threshold 250ms)
[ syncplay/align ] +815ms after the resume: left to fine sync
[ syncplay/pulse ] +868ms: 1.174x for 5.0s ramped
```

---

## 4. Where the 2 s is likely to be, given this

Not reproduced, so it stays open — but three untested conditions are now much
better motivated than before:

1. **The Bravia's 16 s queue.** Both tested devices run a 4.0 s native queue,
   shortened to 1.0 s for the session. The Bravia's is 160 (16.0 s). The
   `queue_secs` correction is that much larger, so any mis-stamp there is that
   much bigger — and `OMEGA_QUEUE_SECS = 8.0` is the silent fallback whenever
   the RPC read fails.
2. **A worse transcode landing error.** +320 ms was measured on a LAN with a
   fast server. A longer segment or a slower encode start scales it directly.
3. **A slow link** (`wanshape` — built, not yet used against a member).

---

## 5. Incidental findings

* **Two sessions per device, and only one of them works.** kofin's service
  authenticates as **`Client="Kofin"`**. A REST call with `Client="Kodi"` and
  the same token creates a *second*, websocket-less session; the group forms,
  `/SyncPlay/List` shows it, the server advances Idle → Waiting → Playing, and
  **no client ever hears a word**. This wasted a run and is silent from the
  server side. `spgroup members` now prints which session groups actually
  reach.
* **The wait-timeout path fired for real** while that was happening: both
  websocket-less members ended up `IsBuffering=true, IgnoreGroupWait=true` and
  the group went Playing without them — D6 behaviour, observed by accident.
* **Real pings are 1–34 ms, not 0.** The `Math.Max(delayTicks, DefaultPing)`
  unit bug needs `GetHighestPing() == 0` to bite; on this LAN it does not. Note
  members start at the `Ping = 500` default and only drop to real values once
  time sync runs.
* **TAB's clock offset is −523.9 ms** against the server, stable across the
  whole sync window (rtt 3.9 ms); L22's is −3.9 ms. The offset exists precisely
  to absorb this, and the screen measurements say it is being absorbed — but it
  means TAB's group-position estimate rests entirely on that measurement being
  right.
* **Local `Player.Stop` on a group member raises kofin's "Playback stopped —
  what should the group do?" dialog**, which dims the video and blocks. Use the
  group's own Stop in harnesses.
* **`adb shell grep 'a\|b'` silently matches nothing.** Cost a wrong conclusion
  ("TAB armed no fine sync") that the single-pattern re-run reversed. Hazard §8
  in the plan, hit in practice.

---

## 6. Gate 0 exit criteria status

| # | criterion | status |
|---|---|---|
| 1 | channel 2 proven per device/path | **met** for L22 + TAB (via external capture; Kodi's own is broken on L22) |
| 2 | `bias_ms` per device × path, n ≥ 10 | **met** for the two no-tempo baselines; tempo/transcode cells measured at group level instead |
| 3 | channels 1 and 2 agree within a frame | **not done** — no camera pass yet |
| 4 | ~2 s reproduced and attributed | **not reproduced.** Conditions recorded above; stays open with the watchdog armed |
| 5 | bias watchdog exists | **met** — `syncwatch --ocr`, plus `screendiff` for the direct two-member comparison |

Blocking status: Gate 0 does **not** block phases 2–5 on L22/TAB, because the
measured disagreement (≤275 ms) is small and characterised. It **does** remain
open for the Bravia, which is the device most likely to carry the reported 2 s.

## 7. Tools built

| tool | purpose | state |
|---|---|---|
| `tools/wanshape.py` | L4 proxy: RTT, bandwidth, `stall`, `blackhole` | verified, unused so far |
| `tools/syncwatch.py` | live group watch, heartbeats, JSONL | in use; caught the seek transient |
| `tools/grabtc.py` | capture + OCR the burned-in timecode | verified both devices |
| `tools/biasprobe.py` | reported − screen, with a measured capture-instant model | verified both devices |
| `tools/screendiff.py` | two members' screens vs the API | the Gate 0 workhorse |
| `tools/spgroup.py` | drive groups as the members' real sessions | verified |
