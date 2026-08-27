#!/usr/bin/env python3
"""tempoprobe — drive inputstream.tempo directly and measure what it delivers.

The transcode question the shakedown parked: fine sync arms on a transcoded
stream and pulses fire, but the displacement comes back 10-20 % short of what
was asked, and once in the wrong direction. Three explanations were still
open — the actuator does not shift an HLS stream properly, the group loop is
measuring the wrong thing, or the head counters are being read wrongly.

Driving the add-on straight from here removes the group, the residual
estimator and the pulse planner from the experiment. A single-member group is
enough to get the stream routed through the add-on and the tempo file
published; from then on this writes the rate itself, holds it, and reads back
three independent accounts of what happened:

* the add-on's own head counters (content_ms - output_ms),
* Kodi's reported position against the wall clock,
* the burned-in timecode on screen, read after the queue has drained.

A rate r held for T seconds should displace the content by (r - 1) x T on all
three. Which of them disagrees says where the fault is.

    tools/tempoprobe.py --member PRS --rate 1.25 --hold 5
    tools/tempoprobe.py --member PRS --sweep            # +/- pulses, both routes
"""

import argparse
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import rig  # noqa: E402

KODI_HOME = {
    "PRS": "/home/conor/.var/app/tv.kodi.Kodi/data",
    "OMG": "/home/conor/.kodi",
}
TEMPO = "temp/kofin_syncplay_tempo"


def tempo_paths(member):
    base = os.path.join(KODI_HOME[member], TEMPO)
    return base, base + ".state"


def read_state(member):
    _, path = tempo_paths(member)
    try:
        with open(path) as handle:
            lines = [l for l in handle.read().splitlines() if l.strip()]
        return json.loads(lines[-1]) if lines else None
    except (OSError, ValueError, IndexError):
        return None


def head_delta(line):
    if not line:
        return None
    content = float(line.get("content_ms") or -1.0)
    output = float(line.get("output_ms") or -1.0)
    if content < 0 or output < 0:
        return None
    return content - output


def write_rate(member, rate):
    path, _ = tempo_paths(member)
    tmp = path + ".probe"
    with open(tmp, "w") as handle:
        handle.write("%.4f\n" % rate)
    os.replace(tmp, path)


def screen_ms(member, window="Kodi"):
    """The burned-in timecode, via grabtc's X11 backend."""
    out = subprocess.run(
        [sys.executable, os.path.join(HERE, "grabtc.py"),
         "--x11-window", window, "--json"],
        capture_output=True, text=True, timeout=40).stdout
    try:
        return json.loads(out.strip().splitlines()[-1]).get("ms")
    except Exception:  # noqa: BLE001
        return None


def sample(member, want_screen):
    return {
        "t": time.time(),
        "state": read_state(member),
        "pos": rig.pos_ms(member),
        "screen": screen_ms(member) if want_screen else None,
    }


def trial(member, rate, hold, settle, want_screen=False):
    """One pulse, measured three ways."""
    before = sample(member, want_screen)
    seq0 = (before["state"] or {}).get("seq")
    write_rate(member, rate)
    time.sleep(hold)
    write_rate(member, 1.0)
    # The head keeps moving until the add-on applies 1.0; give it a moment,
    # then let the queue drain before believing the screen.
    time.sleep(1.5)
    at_end = sample(member, False)
    time.sleep(settle)
    after = sample(member, want_screen)

    d0 = head_delta(before["state"])
    d1 = head_delta(at_end["state"])
    d2 = head_delta(after["state"])
    wall = after["t"] - before["t"]
    expected = (rate - 1.0) * hold * 1000.0

    row = {
        "rate": rate, "hold": hold,
        "expected_ms": round(expected, 1),
        "head_at_end_ms": None if None in (d0, d1) else round(d1 - d0, 1),
        "head_after_settle_ms": None if None in (d0, d2) else round(d2 - d0, 1),
        "seq": [seq0, (after["state"] or {}).get("seq")],
        "tempo_reported": (after["state"] or {}).get("tempo"),
        "queue_secs": (after["state"] or {}).get("queue_secs"),
        "pos_advance_ms": (None if None in (before["pos"], after["pos"])
                           else after["pos"] - before["pos"]),
        "wall_ms": round(wall * 1000.0),
        "pos_minus_wall_ms": (None if None in (before["pos"], after["pos"])
                              else round(after["pos"] - before["pos"] - wall * 1000.0)),
    }
    if want_screen and before["screen"] is not None and after["screen"] is not None:
        row["screen_advance_ms"] = after["screen"] - before["screen"]
        row["screen_minus_wall_ms"] = round(
            after["screen"] - before["screen"] - wall * 1000.0)
    return row


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--member", default="PRS")
    parser.add_argument("--rate", type=float, default=1.25)
    parser.add_argument("--hold", type=float, default=5.0)
    parser.add_argument("--settle", type=float, default=4.0)
    parser.add_argument("--screen", action="store_true")
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--sweep", action="store_true")
    args = parser.parse_args()

    rates = ([1.25, 0.80, 1.10, 0.90] if args.sweep else [args.rate])
    print("state file:", tempo_paths(args.member)[1], flush=True)
    print("initial   :", json.dumps(read_state(args.member))[:200], flush=True)
    rows = []
    for _ in range(args.repeat):
        for rate in rates:
            row = trial(args.member, rate, args.hold, args.settle, args.screen)
            rows.append(row)
            print("  %.2fx x%.1fs  expected %+7.0f  head@end %s  head+settle %s  "
                  "pos-wall %s  screen-wall %s"
                  % (rate, args.hold, row["expected_ms"],
                     row["head_at_end_ms"], row["head_after_settle_ms"],
                     row["pos_minus_wall_ms"], row.get("screen_minus_wall_ms")),
                  flush=True)
    print(json.dumps(rows, indent=1))
