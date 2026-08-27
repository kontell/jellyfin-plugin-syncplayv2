#!/usr/bin/env python3
"""R-A (group formation) and R-C (spectator) from docs/shakedown.md."""

import sys
import time

import rig

A, B = "BRV", "PXL"          # the reported pair
ITEM = rig.AV1_ITEM


def settle(seconds=8):
    time.sleep(seconds)


def offset_ms():
    a, b = rig.pos_ms(A), rig.pos_ms(B)
    if a is None or b is None:
        return None
    return a - b


# ------------------------------------------------------------------ R-A

def a1_idle_group():
    rig.reset()
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "A1"); time.sleep(1); rig.join(B); time.sleep(2)
    st = rig.state(A)
    rig.queue(A, ITEM)
    ok = rig.wait_playing(A, 30) and rig.wait_playing(B, 30)
    settle(10)
    off = offset_ms()
    rig.record("A1", "nothing playing, group created",
               ok and off is not None and abs(off) < 1500,
               "entered from %s; offset %s ms" % (st, off))


def a2_playing_then_group():
    """The gesture that lands on the degraded path: already watching, then
    sync up. The running item was never routed through inputstream.tempo,
    because the route is stamped at play time from a property the join sets."""
    rig.reset()
    ok_play = rig.play_local(A, ITEM, 30)
    settle(6)
    mark = rig.logmark(A)
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "A2"); time.sleep(2); rig.join(B); time.sleep(2)
    rig.queue(A, ITEM)
    ok = rig.wait_playing(A, 30) and rig.wait_playing(B, 30)
    settle(12)
    lines = rig.since(A, mark)
    routed = [ln for ln in lines if "not routed" in ln]
    off = offset_ms()
    rig.record("A2", "video playing, then group created",
               ok_play and ok and off is not None and abs(off) < 2000,
               "offset %s ms; unrouted-log=%s" % (off, "yes" if routed else "no"))


def a3_paused_then_group():
    rig.reset()
    rig.play_local(A, ITEM, 30)
    settle(5)
    rig.rpc(A, "Player.PlayPause", {"playerid": 1, "play": False})
    time.sleep(3)
    paused_at = rig.pos_ms(A)
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "A3"); time.sleep(2); rig.join(B); time.sleep(2)
    st = rig.state(A)
    rig.queue(A, ITEM, start_ms=paused_at or 0)
    ok = rig.wait_playing(A, 30) and rig.wait_playing(B, 30)
    settle(10)
    off = offset_ms()
    rig.record("A3", "video paused, group created",
               ok and off is not None and abs(off) < 2000,
               "paused at %s ms; group entered %s; offset %s ms"
               % (paused_at, st, off))


def a4_control():
    """The clean path, as the control for A2."""
    rig.reset()
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "A4"); time.sleep(1); rig.join(B); time.sleep(2)
    mark = rig.logmark(A)
    rig.queue(A, ITEM)
    ok = rig.wait_playing(A, 30) and rig.wait_playing(B, 30)
    settle(12)
    lines = rig.since(A, mark)
    armed = [ln for ln in lines if "fine sync armed for" in ln]
    off = offset_ms()
    rig.record("A4", "group first, then play (control)",
               ok and off is not None and abs(off) < 1000,
               "offset %s ms; fine sync armed=%s" % (off, "yes" if armed else "no"))


# ------------------------------------------------------------------ R-C

def c1_to_spectator():
    """Group must stop waiting on a spectator."""
    rig.reset()
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "C1"); time.sleep(1); rig.join(B); time.sleep(2)
    rig.queue(A, ITEM)
    rig.wait_playing(A, 30); rig.wait_playing(B, 30)
    settle(10)
    rig.spectator(B, True); time.sleep(3)
    flags = [m.get("IgnoreGroupWait") for m in rig.members_of(A)]
    still_playing = bool(rig.players(A))
    rig.record("C1", "-> spectator while the group plays",
               any(flags) and still_playing,
               "IgnoreGroupWait flags %s; group still playing=%s"
               % (flags, still_playing))


def c2_spectator_own_media():
    """A spectator's own play must not be forwarded to the group."""
    mark_a = rig.logmark(A)
    before = rig.pos_ms(A)
    rig.play_local(B, rig.H264_ITEM, 30)   # a *different* item
    settle(10)
    after = rig.pos_ms(A)
    lines = rig.since(B, mark_a if False else rig.logmark(B) - 40)
    kept = [ln for ln in lines if "Spectator playing own media" in ln]
    undisturbed = (before is not None and after is not None and after > before)
    st = rig.state(A)
    rig.record("C2", "spectator plays its own media",
               undisturbed and st == "Playing",
               "group %s and advancing (%s->%s ms); log=%s"
               % (st, before, after, "yes" if kept else "no"))


def c3_back_from_spectator():
    rig.spectator(B, False)
    time.sleep(4)
    flags = [m.get("IgnoreGroupWait") for m in rig.members_of(A)]
    rig.record("C3", "spectator -> back",
               not any(flags),
               "IgnoreGroupWait flags now %s" % flags)


def c4_toggle_during_hotjoin():
    """Suspected interaction: BeginHotJoin sets IgnoreGroupWait, and an
    explicit SetIgnoreWait(false) writes the same field with no knowledge of
    the hot join in flight."""
    rig.reset()
    rig.hello(A); rig.hello(B)
    rig.new_group(A, "C4"); time.sleep(1); rig.join(B); time.sleep(2)
    rig.queue(A, ITEM)
    rig.wait_playing(A, 30); rig.wait_playing(B, 30)
    settle(15)
    # B leaves and cold-joins; while it is hot-joining, clear the flag.
    rig.leave(B); rig.stop(B); rig.wait_stopped(B, 15); time.sleep(3)
    mark = rig.logmark(B)
    rig.join(B)
    time.sleep(1.2)                      # inside the hot join, before Ready
    rig.spectator(B, False)              # the provocation
    ok_play = rig.wait_playing(B, 40)
    settle(14)
    off = offset_ms()
    lines = rig.since(B, mark)
    stuck = [ln for ln in lines if "align" in ln and "0000" in ln]
    rig.record("C4", "spectator toggled during a hot join",
               ok_play and off is not None and abs(off) < 2500,
               "started=%s; offset %s ms%s"
               % (ok_play, off, "; SUSPECT" if stuck else ""))


def c5_spectator_item_change():
    rig.spectator(B, True); time.sleep(2)
    mark = rig.logmark(B)
    rig.queue(A, rig.H264_ITEM)
    rig.wait_playing(A, 30)
    settle(10)
    lines = rig.since(B, mark)
    ignored = [ln for ln in lines if "Spectator playing own media" in ln]
    a_ok = bool(rig.players(A))
    rig.record("C5", "spectator when the group changes item",
               a_ok,
               "group followed=%s; spectator kept its own=%s"
               % (a_ok, "yes" if ignored else "n/a (was idle)"))
    rig.spectator(B, False)


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    print("=== R-A: group formation ===")
    if which in ("all", "a"):
        a1_idle_group(); a2_playing_then_group(); a3_paused_then_group(); a4_control()
    print("=== R-C: spectator ===")
    if which in ("all", "c"):
        c1_to_spectator(); c2_spectator_own_media(); c3_back_from_spectator()
        c5_spectator_item_change(); c4_toggle_during_hotjoin()
    rig.reset()
    return rig.dump("/tmp/shakedown-results.jsonl")


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
