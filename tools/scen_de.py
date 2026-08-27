#!/usr/bin/env python3
"""scen_de — the correction-policy and mixed-group cells.

E2/E6 and D6/D7 need a member whose reports the harness chooses, which is
what tools/wireclient.py provides; G3/G4 need a genuine v1 member in a group
with a real kofin, which is the same thing at protocol 1.

    tools/scen_de.py e2      # genuine convergence must not rendezvous
    tools/scen_de.py d67     # stall past / short of the 10s wait timeout
    tools/scen_de.py e6      # both rendezvous paths in one session
    tools/scen_de.py g34     # v1 drives kofin, kofin drives v1
    tools/scen_de.py all
"""

import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import rig        # noqa: E402
import spgroup    # noqa: E402
import svrlog     # noqa: E402
import wireclient  # noqa: E402

REAL = "PRS"
ITEM = rig.AV1_ITEM
START_MS = 60000
RESULTS = []


def banner(text):
    print("\n=== %s ===" % text, flush=True)


def record(cell, ok, detail):
    RESULTS.append((cell, ok, detail))
    print("  [%s] %-4s %s" % ("PASS" if ok else "FAIL", cell, detail[:140]), flush=True)


def group_with(probe, name="scen"):
    rig.reset((REAL, "OMG"))
    try:
        probe.leave()
    except Exception:  # noqa: BLE001
        pass
    rig.new_group(REAL, name)
    gid = spgroup.first_group_id(REAL)
    probe.join(gid)
    time.sleep(1)
    rig.queue(REAL, ITEM, START_MS)
    for _ in range(25):
        time.sleep(1)
        probe.ready(START_MS, is_playing=False)
        if rig.state(REAL) == "Playing":
            break
    return gid


def e2():
    """A member that is genuinely closing the gap must keep being corrected,
    not rendezvoused: each correction has to buy more than ProgressTicks."""
    probe = wireclient.WireClient("V2", protocol=2)
    probe.mint_token(); probe.connect()
    group_with(probe, "E2")
    banner("E2 — genuine convergence is not a rendezvous")
    if rig.state(REAL) != "Playing":
        print("  not playing"); return
    target = START_MS + 120000
    logmark = svrlog.mark(); t0 = time.time()
    rig.seek(REAL, target)
    # Each report halves the gap — comfortably more than the 250ms the policy
    # asks for, so the member should be corrected for as long as it improves.
    for gap in (8000, 3000, 1000):
        time.sleep(1.2)
        probe.ready(target - gap, is_playing=False)
        time.sleep(1.2)
    time.sleep(2)
    snaps = probe.received("StateSnapshot", t0)
    seeks = [c for c in probe.commands(t0) if c[1] == "Seek"]
    rz = [l for l in svrlog.grep(["rendezvousing"], logmark)
          if probe.session_id[:8] in l]
    record("E2", not rz and not snaps,
           "seeks=%d snapshots=%d rendezvous=%d" % (len(seeks), len(snaps), len(rz)))
    probe.leave(); probe.close()


def d67():
    """The wait timeout: a stall past 10s rendezvouses a v2 member, a stall
    short of it does not."""
    probe = wireclient.WireClient("V2", protocol=2)
    probe.mint_token(); probe.connect()
    banner("D6/D7 — stall past and short of the 10s wait timeout")
    for cell, silence, expect in (("D7", 6, False), ("D6", 15, True)):
        group_with(probe, cell)
        if rig.state(REAL) != "Playing":
            print("  not playing"); continue
        logmark = svrlog.mark(); t0 = time.time()
        rig.seek(REAL, START_MS + 90000)
        time.sleep(silence)                       # the stall
        probe.ready(START_MS + 90000, is_playing=True)
        time.sleep(3)
        rz = [l for l in svrlog.grep(["rendezvousing"], logmark)
              if probe.session_id[:8] in l]
        snaps = probe.received("StateSnapshot", t0)
        record(cell, bool(rz) == expect,
               "silence=%ds rendezvous=%d snapshots=%d (expected rendezvous=%s)"
               % (silence, len(rz), len(snaps), expect))
    probe.leave(); probe.close()


def e6():
    """Both rendezvous paths in one session: the correction branch in
    WaitingGroupState and the wait-timeout sweep in SyncPlayManagerV2."""
    probe = wireclient.WireClient("V2", protocol=2)
    probe.mint_token(); probe.connect()
    group_with(probe, "E6")
    banner("E6 — the correction path and the timeout path in one session")
    if rig.state(REAL) != "Playing":
        print("  not playing"); return
    logmark = svrlog.mark()

    # (a) corrections that do not converge
    rig.seek(REAL, START_MS + 120000)
    for _ in range(3):
        time.sleep(1.2)
        probe.ready(0, is_playing=False)
        time.sleep(1.2)
    time.sleep(2)
    probe.ready(START_MS + 120000, is_playing=True)   # rejoin the group
    time.sleep(3)

    # (b) a stall past the wait timeout
    rig.seek(REAL, START_MS + 150000)
    time.sleep(15)
    probe.ready(START_MS + 150000, is_playing=True)
    time.sleep(3)

    lines = [l for l in svrlog.grep(["rendezvousing"], logmark)
             if probe.session_id[:8] in l]
    reasons = [l.split('": "')[-1].rstrip('".') for l in lines]
    record("E6", len(set(reasons)) >= 2, "reasons=%s" % reasons)
    probe.leave(); probe.close()


def g34():
    """A v1 member and a real kofin in one group, each driving in turn."""
    v1 = wireclient.WireClient("V1", protocol=1)
    v1.mint_token(); v1.connect()
    group_with(v1, "G34")
    banner("G3/G4 — v1 drives kofin, kofin drives v1")
    if rig.state(REAL) != "Playing":
        print("  not playing"); return

    # G3: the v1 member drives.
    t0 = time.time()
    before = rig.pos_ms(REAL)
    v1.call("/SyncPlay/Pause")
    time.sleep(4)
    paused = rig.speed(REAL)
    v1.call("/SyncPlay/Seek", {"PositionTicks": (START_MS + 200000) * 10000})
    time.sleep(6)
    v1.ready(START_MS + 200000, is_playing=True)
    time.sleep(5)
    landed = rig.pos_ms(REAL)
    record("G3", paused == 0 and landed is not None
           and abs(landed - (START_MS + 200000)) < 8000,
           "kofin paused=%s then landed at %s (asked %d)"
           % (paused == 0, landed, START_MS + 200000))

    # G4: kofin drives, the v1 member is told.
    t1 = time.time()
    rig.seek(REAL, START_MS + 30000)
    time.sleep(4)
    cmds = [c[1] for c in v1.commands(t1)]
    ups = sorted({u[1] for u in v1.group_updates(t1)})
    record("G4", "Seek" in cmds, "v1 saw commands=%s updates=%s" % (cmds, ups))
    record("G6", not v1.received("PositionBeacon", t0)
           and not v1.received("StateSnapshot", t0),
           "v1 beacons=%d snapshots=%d"
           % (len(v1.received("PositionBeacon", t0)),
              len(v1.received("StateSnapshot", t0))))
    v1.leave(); v1.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("what", choices=["e2", "d67", "e6", "g34", "all"])
    args = parser.parse_args()
    for name in (["e2", "d67", "e6", "g34"] if args.what == "all" else [args.what]):
        globals()[name]()
    print("\n--- results ---")
    for cell, ok, detail in RESULTS:
        print("  [%s] %-4s %s" % ("PASS" if ok else "FAIL", cell, detail[:150]))
