#!/usr/bin/env python3
"""scen_h3 — the server restarted underneath a live group.

Groups live in the plugin's memory, so a restart destroys every one of them
while the members carry on playing. What matters is what the clients do about
it: notice the socket has gone, stop pretending to be in a group, and be able
to form a new one — rather than sitting in a phantom group whose commands will
never come.

Run this only with someone at the keyboard. The server's own restart route has
been seen to stop Jellyfin outright (the unit is Restart=on-failure and the
process exits 0), and the ssh account cannot start it again.
"""

import json
import os
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import rig      # noqa: E402
import spgroup  # noqa: E402

MEMBERS = ("PRS", "OMG", "TAB")
ITEM = rig.AV1_ITEM
START_MS = 60000


def server_up():
    try:
        with urllib.request.urlopen(
                spgroup.SERVER + "/System/Info/Public", timeout=4) as response:
            return response.status == 200
    except Exception:  # noqa: BLE001
        return False


def snapshot(tag):
    print("  %-22s state=%-8s positions=%s" % (
        tag, rig.state("PRS"),
        {m: rig.pos_ms(m) for m in MEMBERS}), flush=True)


def main():
    print("=== H3 — server restart mid-group ===", flush=True)
    rig.reset(MEMBERS)
    rig.new_group("PRS", "H3")
    gid = spgroup.first_group_id("PRS")
    for m in ("OMG", "TAB"):
        rig.join(m, gid)
        time.sleep(1)
    rig.queue("PRS", ITEM, START_MS)
    for _ in range(30):
        time.sleep(1)
        if rig.state("PRS") == "Playing":
            break
    if rig.state("PRS") != "Playing":
        print("  could not reach Playing: %s" % rig.state("PRS"), flush=True)
        return 1
    time.sleep(8)
    snapshot("before the restart")
    marks = {m: rig.logmark(m) for m in MEMBERS}

    print("  RESTARTING THE SERVER", flush=True)
    down_at = time.time()
    try:
        spgroup.call("PRS", "/System/Restart")
    except Exception as error:  # noqa: BLE001
        print("  restart call: %s" % error, flush=True)

    # It has to actually go away before coming back counts for anything.
    went_down = False
    for _ in range(30):
        time.sleep(1)
        if not server_up():
            went_down = True
            break
    print("  server went down: %s (after %.1fs)"
          % (went_down, time.time() - down_at), flush=True)

    back = None
    for _ in range(150):
        if server_up():
            back = time.time() - down_at
            break
        time.sleep(2)
    if back is None:
        print("  !! SERVER DID NOT COME BACK — needs a human with root", flush=True)
    else:
        print("  server answered again after %.0fs" % back, flush=True)

    # What did the members do while it was away?
    for tick in range(6):
        time.sleep(10)
        alive = {m: bool(rig.players(m)) for m in MEMBERS}
        pos = {m: rig.pos_ms(m) for m in MEMBERS}
        print("  t+%02ds  playing=%s positions=%s" % (10 * (tick + 1), alive, pos),
              flush=True)

    print("  --- what each client logged ---", flush=True)
    for m in MEMBERS:
        lines = rig.since(m, marks[m], "kofin")
        keep = [l for l in lines if any(k in l for k in (
            "websocket", "syncplay group", "rejoin", "syncplay/state",
            "SyncPlay", "group"))]
        print("  [%s] %d kofin lines, showing the group-relevant ones:" % (m, len(lines)),
              flush=True)
        for l in keep[-10:]:
            print("      %s" % l[-150:], flush=True)

    print("  --- server side ---", flush=True)
    try:
        print("  groups now: %s" % json.dumps(
            spgroup.call("PRS", "/SyncPlay/List", method="GET")), flush=True)
    except Exception as error:  # noqa: BLE001
        print("  list failed: %s" % error, flush=True)

    # Can they form a new group and play together again?
    print("  --- recovery: form a new group ---", flush=True)
    rig.reset(MEMBERS)
    rig.new_group("PRS", "H3-after")
    gid2 = spgroup.first_group_id("PRS")
    for m in ("OMG", "TAB"):
        rig.join(m, gid2)
        time.sleep(1)
    rig.queue("PRS", ITEM, 120000)
    for _ in range(30):
        time.sleep(1)
        if rig.state("PRS") == "Playing":
            break
    time.sleep(8)
    snapshot("after recovery")
    members = rig.members_of("PRS")
    print("  new group has %d members, state %s" % (len(members), rig.state("PRS")),
          flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
