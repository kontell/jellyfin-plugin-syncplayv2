#!/usr/bin/env python3
"""rig — the shakedown's scenario primitives.

Everything the matrix in docs/shakedown.md needs to drive a member and read
back what happened, in one place: group operations as the members' own
sessions (via spgroup), player state over JSON-RPC, and log capture scoped to
a scenario so an assertion reads only the lines that scenario produced.

Log reads go through a single-pattern grep on purpose. ``adb shell`` mangles
an escaped alternation and silently matches nothing, which during this
exercise produced a confident wrong conclusion ("that member armed no fine
sync") that a single-pattern re-run reversed. ``since()`` therefore greps one
fixed string and filters in Python.
"""

import base64
import json
import os
import re
import subprocess
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import spgroup  # noqa: E402

RPC = {
    "PRS": "127.0.0.1:8081",       # flatpak Kodi 22 Piers
    "OMG": "127.0.0.1:8080",       # Debian Kodi 21.3 Omega
    "L22": "127.0.0.1:8081",
    "TAB": "192.168.1.150:8080",
    "BRV": "192.168.1.198:8080",
}

KODI_LOG = {
    "PRS": ("local", "/home/conor/.var/app/tv.kodi.Kodi/data/temp/kodi.log"),
    "OMG": ("local", "/home/conor/.kodi/temp/kodi.log"),
    "L22": ("local", "/home/conor/.var/app/tv.kodi.Kodi/data/temp/kodi.log"),
    "TAB": ("192.168.1.150:38585",
            "/storage/emulated/0/Android/data/org.xbmc.kodi/files/.kodi/temp/kodi.log"),
    "BRV": ("192.168.1.198:46301",
            "/storage/emulated/0/Android/data/org.xbmc.kodi/files/.kodi/temp/kodi.log"),
}

ADB = {"TAB": "192.168.1.150:38585", "BRV": "192.168.1.198:46301"}

AV1_ITEM = "ade40d5b06247a8b4dab2588e9d9e2d9"     # Frasier Timecode AV1, 600s
H264_ITEM = "fc7185543e6c3b0664c81b8822bd2b14"    # SyncPlay Timecode h264, 600s


# ---------------------------------------------------------------- players

def rpc(member, method, params=None):
    payload = json.dumps(
        {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
    ).encode()
    request = urllib.request.Request(
        "http://%s/jsonrpc" % RPC[member], data=payload,
        headers={"Content-Type": "application/json"})
    token = base64.b64encode(b"kodi:kodi").decode()
    request.add_header("Authorization", "Basic %s" % token)
    with urllib.request.urlopen(request, timeout=8) as response:
        return json.loads(response.read().decode()).get("result")


def players(member):
    try:
        return rpc(member, "Player.GetActivePlayers") or []
    except Exception:  # noqa: BLE001
        return []


def pos_ms(member):
    """Position in ms, or None when nothing is playing."""
    p = players(member)
    if not p:
        return None
    try:
        r = rpc(member, "Player.GetProperties",
                {"playerid": p[0]["playerid"], "properties": ["time", "speed"]})
    except Exception:  # noqa: BLE001
        return None
    c = (r or {}).get("time") or {}
    return (c.get("hours", 0) * 3600000 + c.get("minutes", 0) * 60000
            + c.get("seconds", 0) * 1000 + c.get("milliseconds", 0))


def speed(member):
    p = players(member)
    if not p:
        return None
    try:
        r = rpc(member, "Player.GetProperties",
                {"playerid": p[0]["playerid"], "properties": ["speed"]})
    except Exception:  # noqa: BLE001
        return None
    return (r or {}).get("speed")


def stop(member):
    try:
        rpc(member, "Player.Stop", {"playerid": 1})
    except Exception:  # noqa: BLE001
        pass


def play_local(member, item=AV1_ITEM, wait=25):
    """Start an item outside any group — the A2/C2 gesture."""
    rpc(member, "Player.Open",
        {"item": {"file": "plugin://plugin.video.kofin/?id=%s&mode=play" % item}})
    return wait_playing(member, wait)


def wait_playing(member, timeout=25):
    for _ in range(timeout):
        if players(member):
            return True
        time.sleep(1)
    return False


def wait_stopped(member, timeout=20):
    for _ in range(timeout):
        if not players(member):
            return True
        time.sleep(1)
    return False


# ---------------------------------------------------------------- logs

def logmark(member):
    kind, path = KODI_LOG[member]
    try:
        if kind == "local":
            with open(path, "rb") as handle:
                return sum(1 for _ in handle)
        out = subprocess.run(
            ["adb", "-s", kind, "shell", "wc -l < %s" % path],
            capture_output=True, text=True, timeout=30).stdout
        return int(out.strip() or 0)
    except Exception:  # noqa: BLE001
        return 0


def since(member, mark, contains="syncplay/"):
    """Lines after `mark` containing `contains`. One fixed pattern — see the
    module docstring on adb and alternations."""
    kind, path = KODI_LOG[member]
    try:
        if kind == "local":
            with open(path, errors="replace") as handle:
                lines = handle.readlines()[mark:]
        else:
            out = subprocess.run(
                ["adb", "-s", kind, "shell",
                 "tail -n +%d %s | grep -a '%s'" % (mark + 1, path, contains)],
                capture_output=True, text=True, timeout=60).stdout
            lines = out.splitlines()
    except Exception:  # noqa: BLE001
        return []
    return [ln.rstrip() for ln in lines if contains in ln]


def tail(member, contains="syncplay/", n=12):
    kind, path = KODI_LOG[member]
    try:
        if kind == "local":
            with open(path, errors="replace") as handle:
                lines = [ln for ln in handle if contains in ln]
        else:
            out = subprocess.run(
                ["adb", "-s", kind, "shell",
                 "grep -a '%s' %s" % (contains, path)],
                capture_output=True, text=True, timeout=60).stdout
            lines = out.splitlines()
    except Exception:  # noqa: BLE001
        return []
    return [ln.rstrip() for ln in lines][-n:]


# ---------------------------------------------------------------- group

def hello(member):
    return spgroup.call(member, "/SyncPlay/Hello", {"ProtocolVersion": 2})


def new_group(member, name="Shakedown"):
    return spgroup.call(member, "/SyncPlay/New", {"GroupName": name})


def join(member, group_id=None):
    gid = group_id or spgroup.first_group_id(member)
    spgroup.call(member, "/SyncPlay/Join", {"GroupId": gid})
    return gid


def leave(member):
    return spgroup.call(member, "/SyncPlay/Leave")


def queue(member, item=AV1_ITEM, start_ms=0):
    return spgroup.call(member, "/SyncPlay/SetNewQueue", {
        "PlayingQueue": [item], "PlayingItemPosition": 0,
        "StartPositionTicks": start_ms * 10000})


def pause(member):
    return spgroup.call(member, "/SyncPlay/Pause")


def unpause(member):
    return spgroup.call(member, "/SyncPlay/Unpause")


def seek(member, ms):
    return spgroup.call(member, "/SyncPlay/Seek", {"PositionTicks": ms * 10000})


def spectator_api(member, on):
    """Set the SERVER's IgnoreGroupWait only.

    Not the user-facing toggle: kofin's own `ignore_wait` is local state set by
    its menu action, and nothing reads the flag back off a group update
    (`grep IgnoreGroupWait` across manager.py and ui.py finds nothing). Driving
    this alone leaves the client unaware it is a spectator, which invalidated a
    first run of the C cells — the "spectator keeps its own media" guard needs
    the *client* flag and so never fired.
    """
    return spgroup.call(member, "/SyncPlay/SetIgnoreWait", {"IgnoreWait": bool(on)})


def toggle_spectator(member, timeout=10):
    """The real gesture: kofin's group menu, driven by what it says.

    Walking a fixed number of Downs was fragile — the dialog takes ~2s to
    populate on the Omega build against ~0.5s on the Tab, and a run where the
    walk started early left the member a full participant while the scenario
    believed it was a spectator. Read the focused item instead and stop on the
    one that mentions spectating, whatever its index.
    """
    rpc(member, "Addons.ExecuteAddon",
        {"addonid": "plugin.video.kofin", "params": {"mode": "syncplay"}})

    def control():
        try:
            info = rpc(member, "XBMC.GetInfoLabels",
                       {"labels": ["System.CurrentWindow", "System.CurrentControl"]}) or {}
        except Exception:  # noqa: BLE001
            return None, None
        return (info.get("System.CurrentWindow") or "",
                info.get("System.CurrentControl") or "")

    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(0.5)
        window, item = control()
        if "dialog" in window.lower() and item:
            break
    else:
        return False

    for _ in range(8):
        window, item = control()
        if "dialog" not in window.lower():
            return False
        if "spectat" in item.lower():
            rpc(member, "Input.Select")
            time.sleep(3)
            return True
        rpc(member, "Input.Down")
        time.sleep(0.5)

    rpc(member, "Input.Back")
    return False


def group_stop(member):
    return spgroup.call(member, "/SyncPlay/Stop")


def groups(member="PRS"):
    return spgroup.groups(member)


def state(member="PRS"):
    g = groups(member)
    return g[0].get("State") if g else None


def members_of(member="PRS"):
    g = groups(member)
    return (g[0].get("Members") or []) if g else []


def reset(members=("PRS", "OMG", "TAB")):
    """Everything back to nothing: out of groups, nothing playing."""
    for m in members:
        try:
            leave(m)
        except Exception:  # noqa: BLE001
            pass
    for m in members:
        stop(m)
    for m in members:
        wait_stopped(m, 10)
    time.sleep(2)


def wait_state(target, timeout=30, member="PRS"):
    for _ in range(timeout):
        if state(member) == target:
            return True
        time.sleep(1)
    return state(member) == target


# ---------------------------------------------------------------- results

RESULTS = []


def record(cell, name, ok, detail="", skipped=False):
    RESULTS.append({"cell": cell, "name": name, "ok": bool(ok),
                    "skipped": skipped, "detail": detail})
    mark = "SKIP" if skipped else ("PASS" if ok else "FAIL")
    print("  [%s] %-5s %-46s %s" % (mark, cell, name[:46], detail[:110]), flush=True)


def dump(path):
    with open(path, "a") as handle:
        for row in RESULTS:
            handle.write(json.dumps(row) + "\n")
    passed = sum(1 for r in RESULTS if r["ok"] and not r["skipped"])
    failed = sum(1 for r in RESULTS if not r["ok"] and not r["skipped"])
    skipped = sum(1 for r in RESULTS if r["skipped"])
    print("\n  %d passed, %d failed, %d skipped" % (passed, failed, skipped))
    return failed
