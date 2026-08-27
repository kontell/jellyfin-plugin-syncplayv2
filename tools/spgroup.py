#!/usr/bin/env python3
"""spgroup — drive a SyncPlay group as the members' own sessions.

The shakedown harness (docs/shakedown.md). Creates groups, joins and leaves
them, sets the queue, and sends transport commands — each call authenticated
as the member's *real* session, so every message reaches its running kofin over
the websocket exactly as a user-driven one would.

Getting that identity right is the whole trick. Jellyfin keys a session on
client + device id, and kofin's service authenticates as **Client="Kofin"**.
Calling with Client="Kodi" and the same token creates a *second*, websocket-less
session: the REST call succeeds, the group appears in /SyncPlay/List, the server
advances it through Waiting to Playing — and no client ever hears a word,
because the member the group is holding has no socket. That failure is silent
from the server side and looks exactly like a client that ignored its commands.
``spgroup members`` prints what each device is really registered as.

    tools/spgroup.py members
    tools/spgroup.py new --as L22 --name Gate0
    tools/spgroup.py join --as TAB
    tools/spgroup.py queue --as L22 --item <id>
    tools/spgroup.py pause --as L22
    tools/spgroup.py seek --as L22 --ms 120000
    tools/spgroup.py spectator --as TAB --on
    tools/spgroup.py leave --as TAB
"""

import argparse
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request

SERVER = "http://192.168.1.167:8096"
CLIENT = "Kofin"          # what kofin's service actually authenticates as
VERSION = "0.21.1"

MEMBERS = {
    "PRS": {
        "settings": "/home/conor/.var/app/tv.kodi.Kodi/data/userdata/"
                    "addon_data/plugin.video.kofin/settings.xml",
        "device_name": "Kodi (P1D)",
    },
    "OMG": {
        # Omega runs the ``kofin-test`` profile, not master — its live
        # credentials (deviceId ``bench-kofin``) are under profiles/, and the
        # master-profile settings.xml is stale.
        "settings": "/home/conor/.kodi/userdata/profiles/kofin-test/"
                    "addon_data/plugin.video.kofin/settings.xml",
        "device_name": "Kodi (P1D)",
    },
    "TAB": {
        "adb": "192.168.1.150:42753",
        "settings": "/storage/emulated/0/Android/data/org.xbmc.kodi/files/"
                    ".kodi/userdata/addon_data/plugin.video.kofin/settings.xml",
        "device_name": "Kodi (192.168.1.150)",
    },
    "BRV": {
        "adb": "192.168.1.198:46301",
        "settings": "/storage/emulated/0/Android/data/org.xbmc.kodi/files/"
                    ".kodi/userdata/addon_data/plugin.video.kofin/settings.xml",
        "device_name": "Sokoni",
    },
}
MEMBERS["L22"] = MEMBERS["PRS"]      # legacy alias from the first run


CRED_CACHE = "/tmp/claude-1000/spgroup-creds.json"


def _cache():
    try:
        with open(CRED_CACHE) as handle:
            return json.load(handle)
    except Exception:  # noqa: BLE001
        return {}


def read_settings(member):
    """Token and device id for a member, cached.

    A member's credentials live in its own settings.xml, which on the Android
    members is only reachable over adb — and adb over TCP does not survive a
    device reboot or a dropped wireless-debugging session. Losing it mid-run
    used to abort every remaining scenario; the cache keeps the group
    operations working off the last good read.
    """
    spec = MEMBERS[member]
    if "adb" in spec:
        text = subprocess.run(
            ["adb", "-s", spec["adb"], "shell", "cat '%s'" % spec["settings"]],
            capture_output=True, text=True, timeout=30,
        ).stdout
    else:
        with open(spec["settings"]) as handle:
            text = handle.read()
    token = re.search(r'id="accessToken"[^>]*>([^<]*)<', text)
    device = re.search(r'id="deviceId"[^>]*>([^<]*)<', text)
    if not token or not device:
        cached = _cache().get(member)
        if cached:
            return cached[0], cached[1]
        raise SystemExit("could not read credentials for %s" % member)

    data = _cache()
    data[member] = [token.group(1), device.group(1)]
    try:
        with open(CRED_CACHE, "w") as handle:
            json.dump(data, handle)
    except Exception:  # noqa: BLE001
        pass
    return token.group(1), device.group(1)


def call(member, path, body=None, method="POST", server=SERVER):
    token, device = read_settings(member)
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(server + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    request.add_header(
        "Authorization",
        'MediaBrowser Client="%s", Device="%s", DeviceId="%s", '
        'Version="%s", Token="%s"'
        % (CLIENT, MEMBERS[member].get("device_name", member), device,
           VERSION, token),
    )
    # Wifi on the Android members power-saves and the server is restarted by
    # the install loop, so a single transient URLError should not abort a
    # ten-minute scenario. HTTP errors are answers and are not retried.
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                raw = response.read().decode()
                return json.loads(raw) if raw.strip() else None
        except urllib.error.HTTPError:
            break
        except urllib.error.URLError:
            if attempt == 2:
                raise
            import time as _time
            _time.sleep(2)

    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            raw = response.read().decode()
            return json.loads(raw) if raw.strip() else None
    except urllib.error.HTTPError as error:
        return {"error": error.code, "body": error.read().decode()[:300]}


def groups(member):
    return call(member, "/SyncPlay/List", method="GET") or []


def first_group_id(member):
    found = groups(member)
    if not found:
        raise SystemExit("no groups")
    return found[0]["GroupId"]


def show(member):
    for group in groups(member):
        print("%s  %s  state=%s  v%s"
              % (group.get("GroupId", "")[:8], group.get("GroupName"),
                 group.get("State"), group.get("ProtocolVersion")))
        for m in group.get("Members") or []:
            print("    %-10s connected=%-5s buffering=%-5s ignoreWait=%-5s ping=%s"
                  % (m.get("UserName"), m.get("IsConnected"),
                     m.get("IsBuffering"), m.get("IgnoreGroupWait"),
                     m.get("Ping")))


def members_report(args):
    """Which session each device is really registered as — the check that
    catches the websocket-less impostor before it wastes a run."""
    token, _ = read_settings("L22")
    request = urllib.request.Request(SERVER + "/Sessions")
    request.add_header("Authorization", 'MediaBrowser Token="%s"' % token)
    with urllib.request.urlopen(request, timeout=20) as response:
        sessions = json.loads(response.read().decode())

    wanted = {name: read_settings(name)[1] for name in MEMBERS}
    print("%-6s %-24s %-10s %-8s %s" % ("member", "device name", "client", "socket", "device id"))
    for name, device_id in wanted.items():
        rows = [s for s in sessions if (s.get("DeviceId") or "") == device_id]
        if not rows:
            print("%-6s %-24s %-10s %-8s %s" % (name, "(no session)", "-", "-", device_id[:12]))
            continue
        for s in rows:
            live = s.get("SupportsRemoteControl")
            flag = "yes" if live else "NO"
            print("%-6s %-24s %-10s %-8s %s%s"
                  % (name, (s.get("DeviceName") or "")[:24], s.get("Client"),
                     flag, device_id[:12],
                     "   <-- the one groups reach" if s.get("Client") == CLIENT and live else ""))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="cmd", required=True)

    def add(name, *flags):
        p = sub.add_parser(name)
        p.add_argument("--as", dest="member", default="L22", choices=list(MEMBERS))
        for flag, kwargs in flags:
            p.add_argument(flag, **kwargs)
        return p

    sub.add_parser("members")
    add("list")
    add("hello")
    add("new", ("--name", {"default": "Shakedown"}))
    add("join", ("--group", {"default": None}))
    add("leave")
    add("queue", ("--item", {"required": True}),
                 ("--ms", {"type": int, "default": 0}))
    add("pause")
    add("unpause")
    add("stop")
    add("seek", ("--ms", {"type": int, "required": True}))
    add("ready", ("--ms", {"type": int, "default": 0}))
    p = add("spectator")
    p.add_argument("--on", action="store_true")
    p.add_argument("--off", action="store_true")

    args = parser.parse_args()

    if args.cmd == "members":
        members_report(args)
        return 0

    member = args.member
    if args.cmd == "list":
        show(member)
    elif args.cmd == "hello":
        print(call(member, "/SyncPlay/Hello", {"ProtocolVersion": 2}))
    elif args.cmd == "new":
        print(call(member, "/SyncPlay/New", {"GroupName": args.name}))
    elif args.cmd == "join":
        gid = args.group or first_group_id(member)
        print(call(member, "/SyncPlay/Join", {"GroupId": gid}) or "joined %s" % gid[:8])
    elif args.cmd == "leave":
        print(call(member, "/SyncPlay/Leave") or "left")
    elif args.cmd == "queue":
        print(call(member, "/SyncPlay/SetNewQueue", {
            "PlayingQueue": [args.item],
            "PlayingItemPosition": 0,
            "StartPositionTicks": args.ms * 10000,
        }) or "queued")
    elif args.cmd == "pause":
        print(call(member, "/SyncPlay/Pause") or "paused")
    elif args.cmd == "unpause":
        print(call(member, "/SyncPlay/Unpause") or "unpaused")
    elif args.cmd == "stop":
        print(call(member, "/SyncPlay/Stop") or "stopped")
    elif args.cmd == "seek":
        print(call(member, "/SyncPlay/Seek",
                   {"PositionTicks": args.ms * 10000}) or "seeked")
    elif args.cmd == "ready":
        print(call(member, "/SyncPlay/Ready", {
            "When": "2026-01-01T00:00:00.000Z",
            "PositionTicks": args.ms * 10000,
            "IsPlaying": False,
            "PlaylistItemId": "",
        }) or "ready")
    elif args.cmd == "spectator":
        value = bool(args.on) and not args.off
        print(call(member, "/SyncPlay/SetIgnoreWait",
                   {"IgnoreWait": value}) or "ignoreWait=%s" % value)
    return 0


if __name__ == "__main__":
    sys.exit(main())
