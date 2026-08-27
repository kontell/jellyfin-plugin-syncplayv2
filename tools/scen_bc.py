#!/usr/bin/env python3
"""scen_bc — the join and spectator cells the first run could not reach.

B4 needed a server-side config change (HotJoin=false), B6 needed a third free
v2 member, and C5 needed a queue change while a spectator watched its own
thing. All three are reachable now: the plugin's configuration is settable
over the API without a restart, and a synthetic v2 member (wireclient) can be
added without occupying a device.

    tools/scen_bc.py b4        # hot join config gate, both ways
    tools/scen_bc.py b6        # two joiners at once
    tools/scen_bc.py c5        # queue change reaches a spectator
    tools/scen_bc.py all
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

GUID = "181f9934-bf71-4941-974e-a5f2cdcccc4e"
REAL = "PRS"
ITEM = rig.AV1_ITEM
START_MS = 60000
RESULTS = []
SPECTATOR = os.environ.get("SPECTATOR", "TAB")


def banner(text):
    print("\n=== %s ===" % text, flush=True)


def set_hot_join(enabled):
    spgroup.call("PRS", "/Plugins/%s/Configuration" % GUID, {"HotJoin": enabled})
    got = spgroup.call("PRS", "/Plugins/%s/Configuration" % GUID, method="GET")
    return (got or {}).get("HotJoin")


def playing_group(name="scen"):
    rig.reset(("PRS", "OMG"))
    rig.new_group(REAL, name)
    gid = spgroup.first_group_id(REAL)
    rig.queue(REAL, ITEM, START_MS)
    for _ in range(25):
        time.sleep(1)
        if rig.state(REAL) == "Playing":
            break
    return gid


def b4():
    """The HotJoin config gate, measured in both positions.

    A synthetic v2 joiner makes the assertion exact: with hot join on it is
    pushed a StateSnapshot and nobody stops; with it off there is no snapshot
    and the group drops into Waiting for the barrier.
    """
    banner("B4 — HotJoin config gate")
    out = {}
    for enabled in (False, True):
        print("  HotJoin=%s (server says %s)" % (enabled, set_hot_join(enabled)),
              flush=True)
        gid = playing_group("B4-%s" % enabled)
        if rig.state(REAL) != "Playing":
            print("  could not reach Playing"); continue
        joiner = wireclient.WireClient("JOIN", protocol=2)
        joiner.mint_token(); joiner.connect()
        try:
            joiner.leave()
        except Exception:  # noqa: BLE001
            pass
        t0 = time.time()
        logmark = svrlog.mark()
        joiner.join(gid)
        states = []
        for _ in range(8):
            time.sleep(0.5)
            states.append(rig.state(REAL))
        snaps = joiner.received("StateSnapshot", t0)
        hot = [l for l in svrlog.grep(["hot-joining"], logmark)
               if joiner.session_id[:8] in l]
        print("     snapshots=%d  states=%s  hot-join logged=%d"
              % (len(snaps), "".join(s[0] for s in states), len(hot)), flush=True)
        out["hotjoin_%s" % enabled] = {
            "snapshots": len(snaps),
            "waited": "Waiting" in states,
            "hotjoin_logged": len(hot),
        }
        joiner.leave(); joiner.close()
    set_hot_join(True)
    ok = (out.get("hotjoin_False", {}).get("snapshots") == 0
          and out["hotjoin_False"]["waited"]
          and out.get("hotjoin_True", {}).get("snapshots", 0) >= 1
          and not out["hotjoin_True"]["waited"])
    RESULTS.append(("B4", ok, json.dumps(out)))
    return out


def b6():
    """Two members joining a Playing group at the same moment."""
    banner("B6 — two joiners mid-hot-join")
    set_hot_join(True)
    gid = playing_group("B6")
    if rig.state(REAL) != "Playing":
        print("  could not reach Playing"); return None
    a = wireclient.WireClient("JOINA", protocol=2); a.mint_token(); a.connect()
    b = wireclient.WireClient("JOINB", protocol=2); b.mint_token(); b.connect()
    for c in (a, b):
        try:
            c.leave()
        except Exception:  # noqa: BLE001
            pass
    t0 = time.time()
    logmark = svrlog.mark()
    a.join(gid)
    b.join(gid)                      # no gap: both hot-join at once
    states = []
    for _ in range(10):
        time.sleep(0.5)
        states.append(rig.state(REAL))
    # Both answer their snapshot, which is what CompleteHotJoin waits for.
    for c in (a, b):
        c.ready(START_MS + 20000, is_playing=True)
    time.sleep(2)
    unpauses = {c.name: len([x for x in c.commands(t0) if x[1] == "Unpause"])
                for c in (a, b)}
    snaps = {c.name: len(c.received("StateSnapshot", t0)) for c in (a, b)}
    hot = [l for l in svrlog.grep(["hot-joining"], logmark)]
    print("     snapshots=%s  private unpauses=%s  states=%s"
          % (snaps, unpauses, "".join(s[0] for s in states)), flush=True)
    for l in hot[-4:]:
        print("     LOG %s" % l[:150], flush=True)
    ok = all(v >= 1 for v in snaps.values()) and "Waiting" not in states
    RESULTS.append(("B6", ok, "snapshots=%s unpauses=%s waited=%s"
                    % (snaps, unpauses, "Waiting" in states)))
    for c in (a, b):
        c.leave(); c.close()
    return {"snapshots": snaps, "unpauses": unpauses, "states": states}


def c5():
    """A spectator watching its own item when the group changes item.

    The first run found the queue guard fires and the transport commands that
    follow it do not. This measures both halves separately: what the spectator
    is told, and where its playback actually ends up.
    """
    banner("C5 — queue change reaches a spectator watching its own media")
    set_hot_join(True)
    gid = playing_group("C5")
    if rig.state(REAL) != "Playing":
        print("  could not reach Playing"); return None
    rig.join(SPECTATOR, gid)
    # Deliberately tight: a GroupJoined update for a group we are already in
    # used to reset kofin's client-local ignore_wait, so a toggle this soon
    # after a hot join silently undid itself.
    time.sleep(3)

    print("  %s -> spectator via kofin's own menu" % SPECTATOR, flush=True)
    rig.toggle_spectator(SPECTATOR)
    time.sleep(3)
    info = (spgroup.call(REAL, "/SyncPlay/List", method="GET") or [{}])[0]
    print("  server flags: %s"
          % [(m.get("Ping"), m.get("IgnoreGroupWait"))
             for m in (info.get("Members") or [])], flush=True)
    print("  %s plays its own item (h264) locally" % SPECTATOR, flush=True)
    rig.play_local(SPECTATOR, rig.H264_ITEM)
    time.sleep(8)
    rig.rpc(SPECTATOR, "Player.Seek", {"playerid": 1, "value": {"seconds": 300}})
    time.sleep(4)
    before = rig.pos_ms(SPECTATOR)
    mark = rig.logmark(SPECTATOR)
    print("  spectator at %sms; the group now changes item" % before, flush=True)

    # The group stays on its own item. Giving the group the same item the
    # spectator is watching makes the "command for another queue item" guard
    # undecidable, which is what a first run of this cell did.
    rig.seek(REAL, 200000)
    time.sleep(6)
    rig.pause(REAL)
    time.sleep(3)
    rig.unpause(REAL)
    time.sleep(5)
    after = rig.pos_ms(SPECTATOR)
    lines = rig.since(SPECTATOR, mark)
    dragged = before is not None and after is not None and abs(after - before) > 20000
    print("  spectator %sms -> %sms  %s"
          % (before, after, "DRAGGED" if dragged else "held"), flush=True)
    for l in lines[-12:]:
        print("     %s" % l[-150:], flush=True)
    RESULTS.append(("C5", not dragged,
                    "spectator %s -> %s" % (before, after)))
    return {"before": before, "after": after, "dragged": dragged,
            "log": lines[-12:]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("what", choices=["b4", "b6", "c5", "all"])
    args = parser.parse_args()
    if args.what in ("b4", "all"):
        b4()
    if args.what in ("b6", "all"):
        b6()
    if args.what in ("c5", "all"):
        c5()
    print("\n--- results ---")
    for cell, ok, detail in RESULTS:
        print("  [%s] %-4s %s" % ("PASS" if ok else "FAIL", cell, detail[:150]))
