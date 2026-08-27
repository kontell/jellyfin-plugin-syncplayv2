#!/usr/bin/env python3
"""rt_v1v2 — the three server-side fixes in SyncPlay v2 10.11.0.5.

Each fix is about what the wire does for a member at a given protocol version,
so each is tested with a synthetic member whose version, reports and socket are
all under the harness's control (tools/wireclient.py). The v2 run of the same
scenario is the control: a gate that never fires is indistinguishable from a
gate that always fires unless both sides are measured.

    tools/rt_v1v2.py rendezvous      # RT1  + RT1b control
    tools/rt_v1v2.py ignorewait      # RT2
    tools/rt_v1v2.py beacon          # RT3
    tools/rt_v1v2.py all
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

REAL = "PRS"           # the member that actually plays
ITEM = rig.AV1_ITEM
START_MS = 60000


def banner(text):
    print("\n=== %s ===" % text, flush=True)


def fresh_group(probe):
    """A two-member group: one real kofin, one synthetic at `probe.protocol`."""
    rig.reset((REAL, "OMG", "TAB", "BRV"))
    try:
        probe.leave()
    except Exception:  # noqa: BLE001
        pass
    rig.new_group(REAL, "RT")
    gid = spgroup.first_group_id(REAL)
    probe.join(gid)
    time.sleep(1)
    return gid


def start_playing(probe, gid):
    """Queue and get to Playing; the probe answers the start barrier honestly."""
    rig.queue(REAL, ITEM, START_MS)
    # The group waits for every member, the synthetic one included.
    for _ in range(20):
        time.sleep(1)
        probe.ready(START_MS, is_playing=False)
        if rig.state(REAL) == "Playing":
            return True
    return rig.state(REAL) == "Playing"


def rendezvous(protocol):
    """A member whose corrections never close the gap.

    v2: must be rendezvoused (hot join, StateSnapshot).
    v1: must NOT be — it cannot read a snapshot, so it keeps getting the stock
    Seek corrections until the group's own wait timeout releases everyone.
    """
    name = "V%d" % protocol
    probe = wireclient.WireClient(name, protocol=protocol)
    probe.mint_token()
    probe.connect()
    gid = fresh_group(probe)
    banner("RT1%s — non-converging corrections, protocol v%d (session %s)"
           % ("" if protocol == 1 else "b", protocol, probe.session_id[:8]))
    if not start_playing(probe, gid):
        print("  could not reach Playing: state=%s" % rig.state(REAL))
        return None

    # A Seek while Playing is the way into WaitingGroupState with
    # ResumePlaying set — Paused+Unpause goes straight back to Playing and
    # never waits, which is why the first attempt at this scenario saw no
    # corrections at all.
    logmark = svrlog.mark()
    t0 = time.time()
    target = START_MS + 120000
    print("  seek to %ds -> the group waits; the probe reports 0ms three times"
          % (target // 1000), flush=True)
    rig.seek(REAL, target)

    for attempt in range(3):
        time.sleep(1.0)
        probe.ready(0, is_playing=False)       # deliberately ~3 minutes adrift
        time.sleep(1.0)
        cmds = [c[1] for c in probe.commands(t0)]
        ups = [u[1] for u in probe.group_updates(t0)]
        print("     report %d -> commands %s updates %s"
              % (attempt + 1, cmds[-4:], ups[-4:]), flush=True)

    time.sleep(2)
    snapshots = probe.received("StateSnapshot", t0)
    seeks = [c for c in probe.commands(t0) if c[1] == "Seek"]
    lines = svrlog.grep(["rendezvousing", "hot-joining", "got lost in time",
                         "Withholding", "stopped waiting"], logmark)
    mine = [l for l in lines if probe.session_id[:8] in l or "Withholding" in l]
    print("  seeks=%d snapshots=%d" % (len(seeks), len(snapshots)), flush=True)
    for l in mine:
        print("     LOG %s" % l[:170], flush=True)

    probe.leave()
    probe.close()
    return {"protocol": protocol, "session": probe.session_id,
            "seeks": len(seeks), "snapshots": len(snapshots),
            "log": mine}


def _member_flags():
    info = (spgroup.call(REAL, "/SyncPlay/List", method="GET") or [{}])[0]
    return [(m.get("Ping"), m.get("IgnoreGroupWait"))
            for m in (info.get("Members") or [])]


PING_MARK = 4321


def _probe_flag(probe=None):
    """Identify the probe's row by a ping only it reports.

    The obvious marker — a v1 member keeps the 500ms DefaultPing — is not
    unique: a real member that has not yet completed a time sync (after a
    server restart, say) carries 500 too, and picking the first such row read
    another member's flag entirely.
    """
    if probe is not None:
        probe.ping(PING_MARK)
        time.sleep(0.6)
    for ping, ignore in _member_flags():
        if ping == PING_MARK:
            return ignore
    return None


def _time_out_the_probe(label, holder, target):
    """Let the group's 10s wait timeout give up on the probe — and only it.

    The holder answers throughout, so the sweep has exactly one member to give
    up on. (A holder that also stayed silent was rendezvoused by the same
    sweep, and then had nothing left to hold with.)
    """
    logmark = svrlog.mark()
    rig.seek(REAL, target)
    print("  [%s] seek; probe silent for 14s so the group gives up" % label,
          flush=True)
    for _ in range(14):
        holder.ready(target, is_playing=True)
        time.sleep(1)
    for l in svrlog.grep(["kept group", "rendezvousing"], logmark)[-2:]:
        print("     LOG %s" % l[:150], flush=True)


def _report_in_position(probe, holder, label, target):
    """The gesture that clears the group's give-up.

    It has to land while the group is *waiting*: SetBuffering(false) is only
    reached from WaitingGroupState's ready branch, and a Ready sent to a
    Playing group goes to a handler that never touches the flag — which made a
    first version of this cell read as a failure of the fix rather than of the
    test.

    Keeping the group waiting is itself the difficulty, because an ignored
    member is exactly the one the group will not wait for: as soon as every
    other member is ready the state leaves Waiting, and with only one real
    member that happens inside a second. A second synthetic member that simply
    never answers holds Waiting open for the full 10s instead, which makes the
    window deterministic rather than a race.
    """
    rig.seek(REAL, target)
    time.sleep(1.0)
    print("  [%s] group state at report time: %s" % (label, rig.state(REAL)),
          flush=True)
    probe.ready(target, is_playing=True)
    time.sleep(2)
    holder.ready(target, is_playing=True)      # release the group again
    time.sleep(1)


def ignorewait():
    """IgnoreGroupWait has two meanings and used to have one field.

    RT2a: the group gave up on the member (wait timeout). When the member
    reports again the group must start waiting for it once more.
    RT2b: the member asked not to be waited for. That must survive both a
    report and a reconnect.
    """
    probe = wireclient.WireClient("V1", protocol=1)
    probe.mint_token()
    probe.connect()
    holder = wireclient.WireClient("HOLD", protocol=2)
    holder.mint_token()
    holder.connect()
    try:
        holder.leave()
    except Exception:  # noqa: BLE001
        pass
    fresh_group(probe)
    holder.join(probe.group_id)
    time.sleep(1)
    banner("RT2 — the two meanings of IgnoreGroupWait")

    rig.queue(REAL, ITEM, START_MS)
    for _ in range(20):
        time.sleep(1)
        probe.ready(START_MS, is_playing=False)
        holder.ready(START_MS, is_playing=False)
        if rig.state(REAL) == "Playing":
            break
    if rig.state(REAL) != "Playing":
        print("  could not reach Playing: %s" % rig.state(REAL))
        return None

    # --- RT2a: the group's own give-up must be undone by a report ----------
    print("  RT2a: no explicit request; flags %s" % _member_flags(), flush=True)
    _time_out_the_probe("RT2a", holder, START_MS + 90000)
    a_after_timeout = _probe_flag(probe)
    print("  RT2a after timeout:  IgnoreGroupWait=%s" % a_after_timeout, flush=True)
    _report_in_position(probe, holder, "RT2a", START_MS + 100000)
    a_after_report = _probe_flag(probe)
    print("  RT2a after report:   IgnoreGroupWait=%s  (expected False)"
          % a_after_report, flush=True)

    # --- RT2b: the member's own choice must survive ------------------------
    probe.ignore_wait(True)
    time.sleep(1)
    b_after_request = _probe_flag(probe)
    print("  RT2b after request:  IgnoreGroupWait=%s" % b_after_request, flush=True)
    _time_out_the_probe("RT2b", holder, START_MS + 130000)
    _report_in_position(probe, holder, "RT2b", START_MS + 140000)
    b_after_report = _probe_flag(probe)
    print("  RT2b after report:   IgnoreGroupWait=%s  (expected True)"
          % b_after_report, flush=True)

    probe.close()
    time.sleep(4)
    probe.connect()
    time.sleep(3)
    b_after_reconnect = _probe_flag(probe)
    print("  RT2b after reconnect:IgnoreGroupWait=%s  (expected True)"
          % b_after_reconnect, flush=True)

    probe.leave()
    probe.close()
    holder.leave()
    holder.close()
    return {
        "a_after_timeout": a_after_timeout,
        "a_after_report": a_after_report,
        "b_after_request": b_after_request,
        "b_after_report": b_after_report,
        "b_after_reconnect": b_after_reconnect,
    }


def transport_death():
    """RT2c — the other place the engine synthesizes an IgnoreWait.

    A transport death while the group is waiting takes the same path as the
    wait timeout (SyncPlayManagerV2.cs:573), so it had the same defect: the
    member was marked as having asked not to be waited for, and reconnecting
    could then never undo it. RT2b's reconnect arm is not this case — there the
    member really had asked.
    """
    probe = wireclient.WireClient("V1", protocol=1)
    probe.mint_token()
    probe.connect()
    holder = wireclient.WireClient("HOLD", protocol=2)
    holder.mint_token()
    holder.connect()
    try:
        holder.leave()
    except Exception:  # noqa: BLE001
        pass
    fresh_group(probe)
    holder.join(probe.group_id)
    time.sleep(1)
    banner("RT2c — a transport death while the group waits, then a reconnect")
    rig.queue(REAL, ITEM, START_MS)
    for _ in range(20):
        time.sleep(1)
        probe.ready(START_MS, is_playing=False)
        holder.ready(START_MS, is_playing=False)
        if rig.state(REAL) == "Playing":
            break
    if rig.state(REAL) != "Playing":
        print("  could not reach Playing: %s" % rig.state(REAL))
        return None

    print("  before:            IgnoreGroupWait=%s" % _probe_flag(probe), flush=True)
    logmark = svrlog.mark()
    target = START_MS + 90000
    rig.seek(REAL, target)          # the group is now Waiting
    time.sleep(0.8)
    print("  group state: %s; killing the probe's socket" % rig.state(REAL), flush=True)
    probe.close()
    for _ in range(10):
        time.sleep(1)
        holder.ready(target, is_playing=True)
    after_death = _probe_flag(probe)
    print("  after the socket died: IgnoreGroupWait=%s" % after_death, flush=True)
    for l in svrlog.grep(["keeping its membership"], logmark)[-2:]:
        print("     LOG %s" % l[:150], flush=True)

    probe.connect()
    time.sleep(3)
    after_reconnect = _probe_flag(probe)
    print("  after reconnect:      IgnoreGroupWait=%s  (expected False)"
          % after_reconnect, flush=True)
    for l in svrlog.grep(["reconnected to group"], logmark)[-2:]:
        print("     LOG %s" % l[:150], flush=True)

    probe.leave(); probe.close()
    holder.leave(); holder.close()
    return {"after_death": after_death, "after_reconnect": after_reconnect}


def beacon():
    """PositionBeacon and StateSnapshot are v2-only; a v1 socket must see
    neither, over a window long enough for several beacons."""
    v1 = wireclient.WireClient("V1", protocol=1)
    v1.mint_token(); v1.connect()
    v2 = wireclient.WireClient("V2", protocol=2)
    v2.mint_token(); v2.connect()

    rig.reset((REAL, "OMG", "TAB", "BRV"))
    for c in (v1, v2):
        try:
            c.leave()
        except Exception:  # noqa: BLE001
            pass
    rig.new_group(REAL, "RT-beacon")
    gid = spgroup.first_group_id(REAL)
    v1.join(gid)
    v2.join(gid)
    time.sleep(1)
    banner("RT3 — v2-only updates never reach a v1 socket")
    rig.queue(REAL, ITEM, START_MS)
    t0 = time.time()
    for _ in range(20):
        time.sleep(1)
        v1.ready(START_MS, is_playing=False)
        v2.ready(START_MS, is_playing=False)
        if rig.state(REAL) == "Playing":
            break
    print("  state=%s; watching 30s of beacons" % rig.state(REAL), flush=True)
    for i in range(6):
        time.sleep(5)
        print("     t+%02ds  v1 beacons=%d snapshots=%d | v2 beacons=%d snapshots=%d"
              % (5 * (i + 1),
                 len(v1.received("PositionBeacon", t0)), len(v1.received("StateSnapshot", t0)),
                 len(v2.received("PositionBeacon", t0)), len(v2.received("StateSnapshot", t0))),
              flush=True)
    out = {
        "v1_beacons": len(v1.received("PositionBeacon", t0)),
        "v1_snapshots": len(v1.received("StateSnapshot", t0)),
        "v1_types": sorted({u[1] for u in v1.group_updates(t0)}),
        "v2_beacons": len(v2.received("PositionBeacon", t0)),
        "v2_snapshots": len(v2.received("StateSnapshot", t0)),
        "v2_types": sorted({u[1] for u in v2.group_updates(t0)}),
    }
    print("  v1 saw: %s" % out["v1_types"], flush=True)
    print("  v2 saw: %s" % out["v2_types"], flush=True)
    for c in (v1, v2):
        c.leave(); c.close()
    return out


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("what", choices=["rendezvous", "ignorewait", "transport", "beacon", "all"])
    args = parser.parse_args()
    results = {}
    if args.what in ("rendezvous", "all"):
        results["rt1_v1"] = rendezvous(1)
        results["rt1b_v2"] = rendezvous(2)
    if args.what in ("ignorewait", "all"):
        results["rt2"] = ignorewait()
    if args.what in ("transport", "all"):
        results["rt2c"] = transport_death()
    if args.what in ("beacon", "all"):
        results["rt3"] = beacon()
    print("\n" + json.dumps(results, indent=2, default=str))
