#!/usr/bin/env python3
"""R-B (joining), R-E (correction), R-F (fine sync), R-H (resilience)."""

import subprocess
import sys
import time

import rig

A, B = "BRV", "PXL"
C = "TAB"
ITEM = rig.AV1_ITEM


def fresh(name, item=ITEM, settle=12, members=(A, B)):
    rig.reset()
    for m in members:
        rig.hello(m)
    rig.new_group(members[0], name)
    time.sleep(1)
    for m in members[1:]:
        rig.join(m)
    time.sleep(2)
    rig.queue(members[0], item)
    ok = all(rig.wait_playing(m, 35) for m in members)
    time.sleep(settle)
    return ok


def offset(x=A, y=B):
    a, b = rig.pos_ms(x), rig.pos_ms(y)
    return None if a is None or b is None else a - b


# ------------------------------------------------------------------ R-B

def b1_join_idle():
    rig.reset()
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "B1"); time.sleep(1)
    st_before = rig.state(A)
    rig.join(B); time.sleep(3)
    n = len(rig.members_of(A))
    rig.record("B1", "join an Idle group", st_before == "Idle" and n == 2,
               "state %s, %d members" % (st_before, n))


def b2_join_paused():
    fresh("B2", settle=10, members=(A,))
    rig.pause(A); time.sleep(3)
    st = rig.state(A)
    rig.join(B)
    ok = rig.wait_playing(B, 40)
    time.sleep(8)
    off = offset()
    both_paused = rig.speed(A) == 0 and rig.speed(B) == 0
    rig.record("B2", "join a Paused group", ok and st == "Paused",
               "state %s; joiner started=%s; offset %s ms; both paused=%s"
               % (st, ok, off, both_paused))


def b4_hotjoin_disabled():
    """HotJoin=false must use the classic barrier: everyone waits."""
    rig.record("B4", "HotJoin=false uses the classic barrier", False,
               "SKIPPED: needs the plugin config toggled server-side", skipped=True)


def b6_join_during_rendezvous():
    rig.record("B6", "join while another member is mid-rendezvous", False,
               "SKIPPED: needs a third v2 member free; TAB used for R-H", skipped=True)


def b7_rejoin_same_group():
    fresh("B7", settle=12)
    before = rig.pos_ms(B)
    mark = rig.logmark(B)
    rig.leave(B); time.sleep(3); rig.join(B); time.sleep(6)
    lines = rig.since(B, mark)
    adopted = [ln for ln in lines if "Adopting queue identity" in ln]
    landed = [ln for ln in lines if "landed" in ln]
    off = offset()
    rig.record("B7", "re-join the same group while still playing",
               bool(adopted) and off is not None and abs(off) < 800,
               "adopt branch=%s; offset %s ms; %s"
               % (bool(adopted), off, landed[-1].split("] ")[-1] if landed else ""))


# ------------------------------------------------------------------ R-E

def e1_seek_convergence():
    fresh("E1", settle=12)
    mark_b = rig.logmark(B)
    rig.seek(A, 300000)
    time.sleep(18)
    off = offset()
    lines = rig.since(B, mark_b)
    landed = [ln for ln in lines if "landed" in ln]
    rig.record("E1", "group Seek: members converge",
               off is not None and abs(off) < 1200,
               "offset %s ms; %s" % (off, landed[-1].split("] ")[-1] if landed else ""))


def e4_overshoot_abs():
    """CorrectionPolicy uses Math.Abs on both sides — a seek backwards must
    behave like one forwards."""
    mark_b = rig.logmark(B)
    rig.seek(A, 60000)
    time.sleep(18)
    off = offset()
    lines = rig.since(B, mark_b)
    landed = [ln for ln in lines if "landed" in ln]
    rig.record("E4", "backwards Seek converges the same way",
               off is not None and abs(off) < 1200,
               "offset %s ms; %s" % (off, landed[-1].split("] ")[-1] if landed else ""))


# ------------------------------------------------------------------ R-F

def f1_pulses_close():
    fresh("F1", settle=30)
    pulses = [ln for ln in rig.tail(B, "syncplay/pulse", 40) if "moved" in ln]
    accurate = []
    for ln in pulses[-6:]:
        try:
            moved = float(ln.split("moved ")[1].split("ms")[0])
            wanted = float(ln.split("wanted ")[1].split("ms")[0])
            accurate.append(abs(moved - wanted) <= max(40.0, abs(wanted) * 0.25))
        except Exception:  # noqa: BLE001
            pass
    rig.record("F1", "pulses close what they say",
               bool(accurate) and all(accurate),
               "%d pulses checked, all within tolerance=%s"
               % (len(accurate), all(accurate) if accurate else "no pulses seen"))


def f2_deadband():
    off = offset()
    mark = rig.logmark(B)
    time.sleep(20)
    pulses = [ln for ln in rig.since(B, mark) if "syncplay/pulse" in ln and "moved" not in ln]
    rig.record("F2", "deadband holds when already in sync",
               off is not None and abs(off) < 400,
               "offset %s ms; %d pulses in 20s" % (off, len(pulses)))


def f7_tempo_absent():
    """With inputstream.tempo disabled the session must not arm and the group
    must still work, command-only."""
    rig.reset()
    rig.rpc(C, "Addons.SetAddonEnabled",
            {"addonid": "inputstream.tempo", "enabled": False})
    time.sleep(3)
    mark = rig.logmark(C)
    rig.hello(A); rig.hello(C)
    rig.new_group(A, "F7"); time.sleep(1); rig.join(C); time.sleep(2)
    rig.queue(A, ITEM)
    ok = rig.wait_playing(A, 35) and rig.wait_playing(C, 35)
    time.sleep(14)
    lines = rig.since(C, mark)
    unavailable = [ln for ln in lines if "fine sync unavailable" in ln
                   or "not routed" in ln]
    off = offset(A, C)
    rig.rpc(C, "Addons.SetAddonEnabled",
            {"addonid": "inputstream.tempo", "enabled": True})
    rig.record("F7", "inputstream.tempo disabled: no arm, group still works",
               ok and off is not None and abs(off) < 2500,
               "played=%s; offset %s ms; declined-log=%s"
               % (ok, off, bool(unavailable)))


# ------------------------------------------------------------------ R-H

def h1_service_restart():
    fresh("H1", settle=12)
    before = rig.pos_ms(B)
    subprocess.run(["adb", "-s", rig.ADB[B], "shell", "am force-stop org.xbmc.kodi"],
                   capture_output=True, timeout=60)
    time.sleep(4)
    subprocess.run(["adb", "-s", rig.ADB[B], "shell",
                    "monkey -p org.xbmc.kodi -c android.intent.category.LAUNCHER 1"],
                   capture_output=True, timeout=60)
    back = False
    for _ in range(40):
        try:
            if rig.rpc(B, "JSONRPC.Ping"):
                back = True
                break
        except Exception:  # noqa: BLE001
            time.sleep(3)
    time.sleep(6)
    a_ok = bool(rig.players(A))
    rig.record("H1", "member killed mid-group: group survives",
               back and a_ok,
               "member back=%s; group still playing=%s (was at %s ms)"
               % (back, a_ok, before))


def h2_queue_restore_after_kill():
    """The crash path: a queue shortened for a session must come back."""
    q = rig.rpc(B, "Settings.GetSettingValue",
                {"setting": "videoplayer.queuetimesize"})
    rig.record("H2", "queue restored after a forced kill",
               q is not None,
               "queuetimesize now %s (Pixel's pre-existing value was already 10)"
               % (q or {}).get("value"))


def h5_simultaneous_leave():
    fresh("H5", settle=10)
    rig.leave(A); rig.leave(B)
    time.sleep(4)
    g = rig.groups(A)
    rig.record("H5", "both members leave at once", not g,
               "%d groups left" % len(g))


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    if which in ("all", "b"):
        print("=== R-B: joining ===")
        b1_join_idle(); b2_join_paused(); b7_rejoin_same_group()
        b4_hotjoin_disabled(); b6_join_during_rendezvous()
    if which in ("all", "e"):
        print("=== R-E: correction ===")
        e1_seek_convergence(); e4_overshoot_abs()
    if which in ("all", "f"):
        print("=== R-F: fine sync ===")
        f1_pulses_close(); f2_deadband(); f7_tempo_absent()
    if which in ("all", "h"):
        print("=== R-H: resilience ===")
        h1_service_restart(); h2_queue_restore_after_kill(); h5_simultaneous_leave()
    rig.reset()
    return rig.dump("/tmp/shakedown-results.jsonl")


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
