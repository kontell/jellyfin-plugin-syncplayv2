#!/usr/bin/env python3
"""ratecheck — is the stream playing at real time? Ask the screen, not the API.

The transcode finding in round 2 rested on Player.GetProperties, and the same
playback gave contradictory answers on two different windows — net +795 ms over
51s in one, a 7 percent deficit over 115s in another. Both cannot be true, so
this measures the burned-in timecode instead and carries the reported position
alongside it as a second channel rather than as the truth.

Each sample records, in this order: the wall clock, the reported position, and
then the frame. The capture is retried because Kodi's OSD intermittently draws a
rule across the timecode and tesseract will not read through it; a sample that
never OCRs is reported as a miss rather than dropped silently.

    tools/ratecheck.py --window 0x6c00002 --samples 13 --every 10
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


def hide_seekbar(member):
    """Kodi's seek bar draws a rule straight through the burned-in timecode and
    tesseract will not read past it. `back` dismisses it; `back` sent while it
    is *not* showing leaves fullscreen video, so the visibility is checked
    first rather than fired blindly."""
    try:
        vis = rig.rpc(member, "XBMC.GetInfoBooleans",
                      {"booleans": ["Window.IsVisible(seekbar)"]}) or {}
        if vis.get("Window.IsVisible(seekbar)"):
            rig.rpc(member, "Input.ExecuteAction", {"action": "back"})
            time.sleep(0.4)
            return True
    except Exception:  # noqa: BLE001
        pass
    return False


def read_screen(window, member, tries=4, gap=0.5):
    """The timecode, and the wall clock at the instant the frame was taken."""
    for _ in range(tries):
        hide_seekbar(member)
        taken = time.time()
        out = subprocess.run(
            [sys.executable, os.path.join(HERE, "grabtc.py"),
             "--x11-window", window],
            capture_output=True, text=True, timeout=60)
        text = (out.stdout or "").strip()
        if text.isdigit():
            return int(text), taken
        time.sleep(gap)
    return None, None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--member", default="PRS")
    parser.add_argument("--window", required=True)
    parser.add_argument("--samples", type=int, default=13)
    parser.add_argument("--every", type=float, default=10.0)
    parser.add_argument("--label", default="")
    args = parser.parse_args()

    rows = []
    print("sample  wall_s   reported_ms  screen_ms   rep-wall   scr-wall  scr-rep",
          flush=True)
    t_first = None
    ts_first = None
    for i in range(args.samples):
        t = time.time()
        pos = rig.pos_ms(args.member)
        screen, t_screen = read_screen(args.window, args.member)
        if t_first is None:
            t_first, pos_first, screen_first = t, pos, screen
            ts_first = t_screen
        rows.append({"t": t, "pos": pos, "screen": screen, "t_screen": t_screen})
        wall = (t - t_first) * 1000.0
        rep = None if None in (pos, pos_first) else pos - pos_first
        scr = None if None in (screen, screen_first) else screen - screen_first
        print("%5d  %6.1f   %11s  %9s  %9s  %9s  %7s"
              % (i, (t - t_first),
                 pos, screen,
                 "" if rep is None else "%+d" % (rep - wall),
                 "" if scr is None else "%+d" % (scr - wall),
                 "" if None in (screen, pos) else "%+d" % (screen - pos)),
              flush=True)
        if i < args.samples - 1:
            time.sleep(max(0.0, args.every - (time.time() - t)))

    good = [r for r in rows if r["screen"] is not None and r["t_screen"] is not None]
    print("\n%s: %d/%d samples OCR'd" % (args.label or args.member, len(good), len(rows)),
          flush=True)
    if len(good) >= 2:
        span = (good[-1]["t_screen"] - good[0]["t_screen"]) * 1000.0
        scr = good[-1]["screen"] - good[0]["screen"]
        print("  screen advanced %+d ms over %d ms wall  ->  rate %.4f, drift %+d ms"
              % (scr, span, scr / span, scr - span), flush=True)
    both = [r for r in rows if r["screen"] is not None and r["pos"] is not None]
    if len(both) >= 2:
        span = (both[-1]["t"] - both[0]["t"]) * 1000.0
        rep = both[-1]["pos"] - both[0]["pos"]
        print("  reported advanced %+d ms over %d ms wall  ->  rate %.4f, drift %+d ms"
              % (rep, span, rep / span, rep - span), flush=True)
        offs = [r["screen"] - r["pos"] for r in both]
        print("  screen - reported: min %+d  max %+d  mean %+.0f ms"
              % (min(offs), max(offs), sum(offs) / len(offs)), flush=True)
    print(json.dumps(rows))


if __name__ == "__main__":
    main()
