#!/usr/bin/env python3
"""biasprobe — measure how far a member's reported position is from its screen.

Gate 0's calibration step (docs/shakedown.md §1). For one device and one
playback path it answers the only question that matters before any sync number
can be believed:

    bias_ms = reported_position_ms - actual_timecode_on_screen_ms

Each sample brackets the capture between two JSON-RPC position reads, so the
reported position is interpolated to the instant the frame was grabbed rather
than compared against a read taken who-knows-when. The bracket width is
reported too: a wide bracket means the capture was slow and that sample is
correspondingly less precise, which is a fact about the measurement rather
than about the device.

    tools/biasprobe.py --member 127.0.0.1:8080 --x11-window 0x9200002 -n 12
    tools/biasprobe.py --member 192.168.1.150:8080 --adb 192.168.1.150:42753 \\
        -n 12 --label "TAB DirectStream no-tempo"
"""

import argparse
import base64
import json
import os
import statistics
import subprocess
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))


def rpc(host, method, params=None, user="kodi", password="kodi"):
    payload = json.dumps(
        {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
    ).encode()
    request = urllib.request.Request(
        "http://%s/jsonrpc" % host,
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    token = base64.b64encode(("%s:%s" % (user, password)).encode()).decode()
    request.add_header("Authorization", "Basic %s" % token)
    with urllib.request.urlopen(request, timeout=6) as response:
        body = json.loads(response.read().decode())
    if "error" in body:
        raise RuntimeError(body["error"])
    return body.get("result")


def position_ms(host, user, password):
    players = rpc(host, "Player.GetActivePlayers", user=user, password=password)
    if not players:
        return None
    result = rpc(
        host, "Player.GetProperties",
        {"playerid": players[0]["playerid"], "properties": ["time", "speed"]},
        user=user, password=password,
    )
    clock = result.get("time") or {}
    return (
        clock.get("hours", 0) * 3600000
        + clock.get("minutes", 0) * 60000
        + clock.get("seconds", 0) * 1000
        + clock.get("milliseconds", 0)
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--member", required=True, help="host:port for JSON-RPC")
    parser.add_argument("--user", default="kodi")
    parser.add_argument("--password", default="kodi")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--x11-window")
    source.add_argument("--adb")
    source.add_argument("--adb-raw")
    parser.add_argument("-n", "--samples", type=int, default=12)
    parser.add_argument("--interval", type=float, default=1.5)
    parser.add_argument("--label", default="", help="which device × path cell this is")
    parser.add_argument("--jsonl", help="append raw samples here")
    parser.add_argument(
        "--capture-at", type=float, default=0.0,
        help="where in the capture call the frame is actually sampled, as a "
             "fraction of the call. Default 0.0 (the start), which is not an "
             "assumption but a measurement: running the same device at two "
             "capture-window sizes (TAB, 500ms raw vs 1250ms png) agreed at "
             "-352 and -368 ms under this model and disagreed wildly (-102 vs "
             "+257) under a midpoint one.")
    args = parser.parse_args()

    grabtc = [sys.executable, os.path.join(HERE, "grabtc.py")]
    # PPM, not PNG: for the X11 path the whole write sits inside the timed
    # window, so skipping compression takes it from ~166 ms to ~71 ms.
    frame_path = os.path.join(
        os.environ.get("TMPDIR", "/tmp"), "biasprobe-frame.ppm")

    # Split into a timed step (the frame is sampled) and an untimed one (it is
    # fetched and decoded), so the measurement window is the grab alone.
    collect_step = None
    if args.x11_window:
        timed_step = ["--x11-window", args.x11_window, "--capture-only", frame_path]
    elif args.adb_raw:
        timed_step = ["--adb-grab", args.adb_raw]
        collect_step = ["--adb-collect", args.adb_raw]
    else:
        timed_step = ["--adb", args.adb, "--capture-only", frame_path]

    samples = []
    misses = 0
    stream = open(args.jsonl, "a") if args.jsonl else None

    print("biasprobe: %s%s, %d samples"
          % (args.member, " (%s)" % args.label if args.label else "", args.samples))
    print("  %-4s %12s %12s %10s %9s  %s"
          % ("#", "reported", "screen", "bias", "capwin", "band"))

    for i in range(args.samples):
        # The media clock tracks wall clock at 1x, so a position read plus a
        # wall-clock stamp lets the reported position be projected to any
        # instant. The capture happens somewhere inside [t1, t2] and nothing
        # reveals exactly where, so rather than assume a point (assuming the
        # midpoint is what produced a spurious +500 ms on the first run) the
        # bias is reported as the band the capture window actually implies.
        try:
            t0 = time.time()
            before = position_ms(args.member, args.user, args.password)
            t0b = time.time()
        except Exception as error:  # noqa: BLE001
            print("  rpc failed: %s" % str(error)[:100])
            break
        if before is None:
            print("  nothing playing on %s" % args.member)
            break

        # Only the capture sits inside the window. OCR is slower and far more
        # variable (a five-variant × three-PSM ladder), and timing it here is
        # what made the first run read a spurious bias equal to half the OCR's
        # own runtime.
        t1 = time.time()
        subprocess.run(grabtc + timed_step, capture_output=True, text=True, timeout=60)
        t2 = time.time()
        if collect_step:
            subprocess.run(grabtc + collect_step + ["--capture-only", frame_path],
                           capture_output=True, text=True, timeout=120)

        result = subprocess.run(grabtc + ["--from-file", frame_path],
                                capture_output=True, text=True, timeout=90)
        text = result.stdout.strip()

        if not text:
            misses += 1
            print("  %-4d %12.0f %12s %10s %9s" % (i + 1, before, "MISS", "-", "-"))
            time.sleep(args.interval)
            continue

        screen = int(text)
        # Reported position projected to each end of the capture window.
        # Anchor at the midpoint of the (very short) position read itself.
        anchor = (t0 + t0b) / 2.0
        at_open = before + (t1 - anchor) * 1000.0
        at_close = before + (t2 - anchor) * 1000.0
        lo, hi = sorted((at_open - screen, at_close - screen))
        bias = (at_open + (at_close - at_open) * args.capture_at) - screen
        window = (t2 - t1) * 1000.0
        samples.append(bias)
        print("  %-4d %12.0f %12d %+10.0f %9.0f  [%+.0f..%+.0f]"
              % (i + 1, before, screen, bias, window, lo, hi))

        if stream:
            stream.write(json.dumps({
                "t": time.time(), "label": args.label, "member": args.member,
                "reported_ms": before, "screen_ms": screen,
                "bias_ms": bias, "bias_lo_ms": lo, "bias_hi_ms": hi,
                "capture_window_ms": window,
            }) + "\n")
            stream.flush()

        time.sleep(args.interval)

    if stream:
        stream.close()

    print()
    if not samples:
        print("  NO SAMPLES — channel 2 unavailable for this cell.")
        print("  Record it as such and fall back to channel 1 (camera).")
        return 1

    median = statistics.median(samples)
    spread = statistics.pstdev(samples) if len(samples) > 1 else 0.0
    print("  n=%d (misses %d)" % (len(samples), misses))
    print("  median bias %+.0f ms   mean %+.0f ms   sd %.0f ms   range %+.0f..%+.0f"
          % (median, statistics.mean(samples), spread, min(samples), max(samples)))
    frame = 1000.0 / 23.976
    print("  = %+.2f frames at 23.976 fps" % (median / frame))
    if abs(median) <= frame:
        print("  VERDICT: within one frame — reported position tracks the screen.")
    elif abs(median) < 250:
        print("  VERDICT: sub-quarter-second offset — real, but not the ~2 s case.")
    else:
        print("  VERDICT: *** %.2f s offset — this is the Gate 0 failure. ***"
              % (median / 1000.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
