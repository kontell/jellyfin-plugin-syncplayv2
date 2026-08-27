#!/usr/bin/env python3
"""R-D: the impaired-network cells, with PXL behind tools/wanshape.py."""

import re
import subprocess
import sys
import time

import rig

A, B = "BRV", "PXL"
ITEM = rig.AV1_ITEM
HERE = "/media/minipie/bluecon/dev/jellyfin-plugin-syncplayv2/tools"
PROXY = "http://192.168.1.112:8099"
REAL = "https://jelly.konell.xyz"
SETTINGS = ("/storage/emulated/0/Android/data/org.xbmc.kodi/files/.kodi/"
            "userdata/addon_data/plugin.video.kofin/settings.xml")


def shape(cmd):
    return subprocess.run([sys.executable, HERE + "/wanshape.py", "--send", cmd],
                          capture_output=True, text=True, timeout=30).stdout.strip()


def point_at(url):
    serial = rig.ADB[B]
    subprocess.run(["adb", "-s", serial, "shell", "am force-stop org.xbmc.kodi"],
                   capture_output=True, timeout=60)
    time.sleep(3)
    local = "/tmp/scen_d-settings.xml"
    subprocess.run(["adb", "-s", serial, "pull", SETTINGS, local],
                   capture_output=True, timeout=60)
    text = open(local).read()
    text = re.sub(r'(<setting id="serverAddress"[^>]*>)[^<]*(</setting>)',
                  r"\g<1>%s\g<2>" % url, text)
    open(local, "w").write(text)
    subprocess.run(["adb", "-s", serial, "push", local, SETTINGS],
                   capture_output=True, timeout=60)
    subprocess.run(["adb", "-s", serial, "shell",
                    "monkey -p org.xbmc.kodi -c android.intent.category.LAUNCHER 1"],
                   capture_output=True, timeout=60)
    for _ in range(40):
        try:
            if rig.rpc(B, "JSONRPC.Ping"):
                return True
        except Exception:  # noqa: BLE001
            time.sleep(3)
    return False


def ping_of(member_index=1):
    ms = rig.members_of(A)
    return ms[member_index].get("Ping") if len(ms) > member_index else None


def fresh(name, settle=14):
    rig.reset()
    rig.hello(A); rig.hello(B)
    rig.new_group(A, name); time.sleep(1); rig.join(B); time.sleep(2)
    rig.queue(A, ITEM)
    ok = rig.wait_playing(A, 40) and rig.wait_playing(B, 40)
    time.sleep(settle)
    return ok


def offset():
    a, b = rig.pos_ms(A), rig.pos_ms(B)
    return None if a is None or b is None else a - b


def rtt_sweep():
    """Tolerance is clamp(2*ping, 500, 2000) ms server-side, so RTT walks it."""
    for cell, rtt, expect in (("D1", 0, 500), ("D2", 300, 500),
                              ("D3", 1000, 1000), ("D4", 2500, 2000)):
        shape("reset"); shape("rtt %d" % rtt)
        time.sleep(22)                      # let time sync re-measure
        ping = ping_of()
        off = offset()
        tol = min(2000, max(500, 2 * (ping or 0)))
        rig.record(cell, "added RTT %d ms" % rtt,
                   off is not None and abs(off) < 2500,
                   "reported ping %s ms -> tolerance %s ms (expected ~%s); offset %s ms"
                   % (ping, tol, expect, off))
    shape("reset")


def d5_bandwidth():
    shape("reset"); shape("down 800")       # well under the ~2.2 Mbps asset
    mark = rig.logmark(B)
    time.sleep(40)
    lines = rig.since(B, mark)
    buffering = [ln for ln in lines if "buffering" in ln.lower()]
    off = offset()
    shape("reset")
    time.sleep(15)
    rig.record("D5", "bandwidth below the asset bitrate",
               True,
               "buffering events=%d; offset during cap %s ms" % (len(buffering), off))


def d7_stall_inside_timeout():
    fresh("D7", settle=12)
    mark = rig.logmark(B)
    before = rig.members_of(A)
    rig.seek(A, 200000)
    shape("stall 6")                        # inside the 10 s GroupWaitTimeout
    time.sleep(26)
    flags = [m.get("IgnoreGroupWait") for m in rig.members_of(A)]
    off = offset()
    rig.record("D7", "stall 6 s: group waits, no rendezvous",
               not any(flags) and off is not None and abs(off) < 3000,
               "IgnoreGroupWait %s (must be all False); offset %s ms" % (flags, off))


def d6_stall_past_timeout():
    fresh("D6", settle=12)
    rig.seek(A, 260000)
    shape("stall 15")                       # past the 10 s timeout
    time.sleep(34)
    off = offset()
    ok_recovered = off is not None and abs(off) < 3000
    rig.record("D6", "stall 15 s: rendezvous fires, group carries on",
               ok_recovered,
               "offset after recovery %s ms" % off)


def d8_blackhole_within_grace():
    fresh("D8", settle=12)
    shape("blackhole 30")
    time.sleep(45)
    conn = [m.get("IsConnected") for m in rig.members_of(A)]
    still = bool(rig.players(A))
    time.sleep(20)
    off = offset()
    rig.record("D8", "blackhole 30 s: reconnect inside the 90 s grace",
               still and off is not None,
               "connected flags %s; group kept playing=%s; offset after %s ms"
               % (conn, still, off))


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    print("  pointing %s at the proxy" % B)
    if not point_at(PROXY):
        print("  could not bring %s back on the proxy" % B)
        return 1
    try:
        if not fresh("D", settle=14):
            print("  group did not start")
            return 1
        if which in ("all", "rtt"):
            print("=== R-D: RTT sweep ===")
            rtt_sweep()
        if which in ("all", "bw"):
            print("=== R-D: bandwidth ===")
            d5_bandwidth()
        if which in ("all", "stall"):
            print("=== R-D: stalls ===")
            d7_stall_inside_timeout(); d6_stall_past_timeout()
        if which in ("all", "black"):
            print("=== R-D: blackhole ===")
            d8_blackhole_within_grace()
    finally:
        shape("reset")
        rig.reset()
        print("  restoring %s to the real server" % B)
        point_at(REAL)
    return rig.dump("/tmp/shakedown-results.jsonl")


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
