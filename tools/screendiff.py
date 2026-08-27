#!/usr/bin/env python3
"""screendiff — how far apart two members actually are, on screen.

The measurement the OSD could not give. It answers the question a viewer asks
("are these two showing the same thing?") without trusting either device's
reported position, and reports the reported-position answer beside it so the
two can be compared. A gap between them is the Gate 0 defect
(docs/shakedown.md §0) caught in the act.

Captures are interleaved A, B, A. Member A's *screen* timecode is then
interpolated across wall clock to the instant B's frame was taken, so the two
are compared at one moment rather than at whatever moments they happened to be
sampled. Because both captures use the same convention for where in the
capture call the frame is sampled, that convention mostly cancels; what remains
is bounded by the difference in the two capture windows, and is reported.

    tools/screendiff.py \\
        --a L22=127.0.0.1:8080,x11=0x9200002 \\
        --b TAB=192.168.1.150:8080,adb=192.168.1.150:42753 \\
        -n 8 --tolerance 250
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
GRABTC = [sys.executable, os.path.join(HERE, "grabtc.py")]


class Side(object):
    def __init__(self, spec):
        name, _, rest = spec.partition("=")
        parts = rest.split(",")
        fields = dict(p.split("=", 1) for p in parts[1:] if "=" in p)
        self.name = name
        self.host = parts[0]
        self.user = fields.get("user", "kodi")
        self.password = fields.get("password", "kodi")
        self.frame = os.path.join(
            os.environ.get("TMPDIR", "/tmp"), "screendiff-%s.ppm" % name)
        if "x11" in fields:
            self.timed = ["--x11-window", fields["x11"], "--capture-only", self.frame]
            self.collect = None
        elif "adb" in fields:
            self.timed = ["--adb-grab", fields["adb"]]
            self.collect = ["--adb-collect", fields["adb"],
                            "--capture-only", self.frame]
        else:
            raise SystemExit("%s needs x11=<winid> or adb=<serial>" % name)

    def rpc(self, method, params=None):
        payload = json.dumps(
            {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
        ).encode()
        request = urllib.request.Request(
            "http://%s/jsonrpc" % self.host, data=payload,
            headers={"Content-Type": "application/json"})
        token = base64.b64encode(
            ("%s:%s" % (self.user, self.password)).encode()).decode()
        request.add_header("Authorization", "Basic %s" % token)
        with urllib.request.urlopen(request, timeout=6) as response:
            body = json.loads(response.read().decode())
        return body.get("result")

    def reported_ms(self):
        players = self.rpc("Player.GetActivePlayers")
        if not players:
            return None
        result = self.rpc("Player.GetProperties",
                          {"playerid": players[0]["playerid"],
                           "properties": ["time", "speed"]})
        clock = (result or {}).get("time") or {}
        return (clock.get("hours", 0) * 3600000 + clock.get("minutes", 0) * 60000
                + clock.get("seconds", 0) * 1000 + clock.get("milliseconds", 0))

    def grab(self):
        """Sample the surface. Returns (t_open, t_close)."""
        t0 = time.time()
        subprocess.run(GRABTC + self.timed, capture_output=True, timeout=90)
        return t0, time.time()

    def read(self):
        """Fetch + OCR the frame this side last grabbed. Untimed."""
        if self.collect:
            subprocess.run(GRABTC + self.collect, capture_output=True, timeout=120)
        out = subprocess.run(GRABTC + ["--from-file", self.frame],
                             capture_output=True, text=True, timeout=120).stdout
        return int(out.strip()) if out.strip() else None


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--a", required=True, help="NAME=host:port,x11=<id>|adb=<serial>")
    parser.add_argument("--b", required=True)
    parser.add_argument("-n", "--samples", type=int, default=8)
    parser.add_argument("--interval", type=float, default=1.0)
    parser.add_argument("--tolerance", type=float, default=250.0)
    parser.add_argument("--jsonl")
    args = parser.parse_args()

    a, b = Side(args.a), Side(args.b)
    stream = open(args.jsonl, "a") if args.jsonl else None

    print("screendiff: %s vs %s  (positive = %s is ahead)"
          % (a.name, b.name, a.name))
    print("  %-4s %14s %14s %10s %s"
          % ("#", "on-screen", "reported", "disagree", "detail"))

    screen_deltas, reported_deltas = [], []

    for i in range(args.samples):
        # A, B, A so A can be interpolated to B's instant.
        a1_open, a1_close = a.grab()
        a1_rep = a.reported_ms()
        a1 = a.read()

        b_open, b_close = b.grab()
        b_rep = b.reported_ms()
        b_screen = b.read()

        a2_open, a2_close = a.grab()
        a2 = a.read()

        if a1 is None or a2 is None or b_screen is None:
            print("  %-4d %14s %14s %10s  capture miss" % (i + 1, "-", "-", "-"))
            time.sleep(args.interval)
            continue

        span = a2_open - a1_open
        if span <= 0:
            time.sleep(args.interval)
            continue
        frac = (b_open - a1_open) / span
        a_at_b = a1 + (a2 - a1) * frac
        screen_delta = a_at_b - b_screen

        reported_delta = None
        if a1_rep is not None and b_rep is not None:
            a_rep_at_b = a1_rep + (b_open - a1_open) * 1000.0
            reported_delta = a_rep_at_b - b_rep

        # What the two capture windows leave unresolved.
        residual = abs((a1_close - a1_open) - (b_close - b_open)) * 1000.0 / 2.0

        screen_deltas.append(screen_delta)
        if reported_delta is not None:
            reported_deltas.append(reported_delta)
            disagree = screen_delta - reported_delta
            print("  %-4d %+14.0f %+14.0f %+10.0f  +/-%.0fms"
                  % (i + 1, screen_delta, reported_delta, disagree, residual))
        else:
            print("  %-4d %+14.0f %14s %10s  +/-%.0fms"
                  % (i + 1, screen_delta, "n/a", "-", residual))

        if stream:
            stream.write(json.dumps({
                "t": time.time(), "a": a.name, "b": b.name,
                "screen_delta_ms": screen_delta,
                "reported_delta_ms": reported_delta,
                "residual_ms": residual,
                "a_screen_ms": a1, "b_screen_ms": b_screen,
            }) + "\n")
            stream.flush()

        time.sleep(args.interval)

    if stream:
        stream.close()

    print()
    if not screen_deltas:
        print("  no usable samples")
        return 1

    s_med = statistics.median(screen_deltas)
    print("  ON SCREEN : %s is %+.0f ms vs %s (sd %.0f, n=%d)"
          % (a.name, s_med, b.name,
             statistics.pstdev(screen_deltas) if len(screen_deltas) > 1 else 0,
             len(screen_deltas)))
    if reported_deltas:
        r_med = statistics.median(reported_deltas)
        print("  REPORTED  : %s is %+.0f ms vs %s (sd %.0f)"
              % (a.name, r_med, b.name,
                 statistics.pstdev(reported_deltas) if len(reported_deltas) > 1 else 0))
        gap = s_med - r_med
        print("  DISAGREE  : %+.0f ms" % gap)
        print()
        if abs(gap) > args.tolerance:
            print("  *** The position API and the screen disagree by %.2f s." % (abs(gap) / 1000.0))
            print("      This is the Gate 0 defect: the group can read as synced")
            print("      while the pictures are %.2f s apart." % (abs(s_med) / 1000.0))
        elif abs(s_med) > args.tolerance:
            print("  Members really are %.2f s apart, and the API says so too —" % (abs(s_med) / 1000.0))
            print("  a sync problem, not a reporting one.")
        else:
            print("  Members are in sync on screen, and the API agrees.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
