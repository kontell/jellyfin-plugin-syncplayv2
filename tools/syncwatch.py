#!/usr/bin/env python3
"""syncwatch — watch a SyncPlay group in real time and say so out loud.

The companion to ``wanshape.py``. Samples every member over JSON-RPC and the
server over its SyncPlay routes, reconciles them into one view, and emits a
line the moment anything is worth knowing. A full record of every sample goes
to a JSONL file for the post-mortem; stdout carries only events.

Written for the Monitor tool's contract, which is what makes it useful during
a run rather than after one:

* stdout is line-buffered and selective — one line per event, not per sample;
* **silence is never success.** A heartbeat is emitted every ``--heartbeat``
  seconds no matter what, so a dead poller, a hung Kodi and a healthy quiet
  group cannot look the same;
* every terminal condition emits, not just the happy path — a member that
  stops answering, a playback that stops, an RPC that errors.

The offset it reports is between members' *reported* positions, which is the
number Gate 0 exists to distrust. Where a device has a calibrated bias, pass
it as ``bias=<ms>`` on that member and it is subtracted before comparison; the
raw figure is kept in the JSONL either way. With ``--ocr`` the burned-in
timecode is read back off a screenshot and the reported-vs-actual bias is
tracked live — which is the whole point, since a group can sit at a perfect
reported offset while two seconds apart on screen.

    tools/syncwatch.py \\
        --server http://192.168.1.167:8096 --token-from <settings.xml> \\
        --member L22=127.0.0.1:8080 \\
        --member TAB=192.168.1.150:8080,adb=192.168.1.150:42753,bias=-180 \\
        --tolerance 250 --jsonl run.jsonl
"""

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

HEARTBEAT_DEFAULT = 30.0
SAMPLE_DEFAULT = 1.0


def summarise(groups):
    """The parts of the group list a change is worth reporting for."""
    if not isinstance(groups, list):
        return groups
    return [
        {
            "GroupId": g.get("GroupId"),
            "GroupName": g.get("GroupName"),
            "State": g.get("State"),
            "ProtocolVersion": g.get("ProtocolVersion"),
            "Members": [
                {
                    "UserName": m.get("UserName"),
                    "IsConnected": m.get("IsConnected"),
                    "IsBuffering": m.get("IsBuffering"),
                    "IgnoreGroupWait": m.get("IgnoreGroupWait"),
                }
                for m in (g.get("Members") or [])
            ],
        }
        for g in groups
    ]


def emit(kind, message):
    """One event, one line, flushed. This is what Monitor turns into a chat
    notification, so it has to be readable on its own."""
    print(
        "%s %-8s %s" % (time.strftime("%H:%M:%S"), kind, message),
        flush=True,
    )


class Member(object):
    def __init__(self, spec):
        name, _, rest = spec.partition("=")
        parts = rest.split(",")
        fields = dict(p.split("=", 1) for p in parts[1:] if "=" in p)
        self.name = name
        self.host = parts[0]
        self.adb = fields.get("adb")
        self.user = fields.get("user", "kodi")
        self.password = fields.get("password", "kodi")
        self.bias_ms = float(fields.get("bias", 0.0))
        self.alive = True
        self.last_state = None
        self.position_ms = None
        self.speed = None
        self.ocr_bias_ms = None

    # -- JSON-RPC -------------------------------------------------------

    def rpc(self, method, params=None):
        payload = json.dumps(
            {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
        ).encode()
        request = urllib.request.Request(
            "http://%s/jsonrpc" % self.host,
            data=payload,
            headers={"Content-Type": "application/json"},
        )
        token = base64.b64encode(
            ("%s:%s" % (self.user, self.password)).encode()
        ).decode()
        request.add_header("Authorization", "Basic %s" % token)
        with urllib.request.urlopen(request, timeout=4) as response:
            body = json.loads(response.read().decode())
        if "error" in body:
            raise RuntimeError(body["error"])
        return body.get("result")

    def sample(self):
        """Position and play state, or None if the box did not answer."""
        players = self.rpc("Player.GetActivePlayers")
        if not players:
            return {"playing": False, "position_ms": None, "speed": None}
        pid = players[0]["playerid"]
        result = self.rpc(
            "Player.GetProperties",
            {"playerid": pid, "properties": ["time", "speed", "percentage"]},
        )
        clock = result.get("time") or {}
        position_ms = (
            clock.get("hours", 0) * 3600000
            + clock.get("minutes", 0) * 60000
            + clock.get("seconds", 0) * 1000
            + clock.get("milliseconds", 0)
        )
        return {
            "playing": result.get("speed", 0) != 0,
            "position_ms": position_ms,
            "speed": result.get("speed"),
        }

    def screenshot_timecode_ms(self, ocr_cmd):
        """Read the burned-in timecode off a screenshot — the output-truth
        channel. Returns None when the channel is unavailable on this device,
        which is a finding rather than an error."""
        try:
            self.rpc("Input.ExecuteAction", {"action": "screenshot"})
        except Exception:
            return None
        time.sleep(0.6)
        try:
            out = subprocess.run(
                ocr_cmd.replace("{member}", self.name),
                shell=True,
                capture_output=True,
                text=True,
                timeout=20,
            ).stdout
        except (subprocess.SubprocessError, OSError):
            return None
        match = re.search(r"(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})", out)
        if not match:
            return None
        h, m, s, frac = match.groups()
        return (
            int(h) * 3600000
            + int(m) * 60000
            + int(s) * 1000
            + int(frac.ljust(3, "0"))
        )


class Server(object):
    def __init__(self, base, token):
        self.base = base.rstrip("/")
        self.token = token
        self.last_signature = None

    def get(self, path):
        request = urllib.request.Request(self.base + path)
        request.add_header(
            "Authorization", 'MediaBrowser Token="%s"' % self.token
        )
        with urllib.request.urlopen(request, timeout=6) as response:
            return json.loads(response.read().decode())

    def groups(self):
        try:
            return self.get("/SyncPlay/List")
        except (urllib.error.URLError, OSError, ValueError) as error:
            return {"error": str(error)}


def read_token(path):
    with open(os.path.expanduser(path)) as handle:
        text = handle.read()
    match = re.search(r'id="accessToken"[^>]*>([^<]*)<', text)
    if not match:
        raise SystemExit("no accessToken in %s" % path)
    return match.group(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--server", required=True)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--token")
    group.add_argument("--token-from", help="a kofin settings.xml to read it from")
    parser.add_argument(
        "--member", action="append", required=True, dest="members",
        help="NAME=host:port[,adb=serial][,user=][,password=][,bias=ms]",
    )
    parser.add_argument("--tolerance", type=float, default=250.0,
                        help="member-to-member offset that counts as diverged, ms")
    parser.add_argument(
        "--recover", type=float, default=0.6,
        help="fraction of --tolerance the worst pair must fall back under "
             "before INBAND is reported (hysteresis; default 0.6)")
    parser.add_argument("--sample", type=float, default=SAMPLE_DEFAULT)
    parser.add_argument("--heartbeat", type=float, default=HEARTBEAT_DEFAULT)
    parser.add_argument("--jsonl", help="write every sample here")
    parser.add_argument(
        "--ocr",
        help="shell command that OCRs {member}'s latest screenshot to stdout; "
             "enables the output-truth channel",
    )
    parser.add_argument("--ocr-every", type=float, default=60.0)
    args = parser.parse_args()

    token = args.token or read_token(args.token_from)
    server = Server(args.server, token)
    members = [Member(spec) for spec in args.members]

    stream = open(args.jsonl, "a") if args.jsonl else None
    emit("START", "%d members, tolerance %gms, heartbeat %gs%s" % (
        len(members), args.tolerance, args.heartbeat,
        ", OCR every %gs" % args.ocr_every if args.ocr else "",
    ))

    last_heartbeat = 0.0
    last_ocr = 0.0
    diverged = False

    while True:
        now = time.time()
        record = {"t": now, "members": {}}

        # -- members ----------------------------------------------------
        for member in members:
            try:
                sample = member.sample()
                if not member.alive:
                    member.alive = True
                    emit("BACK", "%s is answering again" % member.name)
            except Exception as error:  # noqa: BLE001 - any failure is an event
                if member.alive:
                    member.alive = False
                    emit("LOST", "%s stopped answering: %s"
                         % (member.name, str(error)[:120]))
                record["members"][member.name] = {"alive": False}
                member.position_ms = None
                continue

            state = "playing" if sample["playing"] else (
                "paused" if sample["position_ms"] is not None else "stopped"
            )
            if state != member.last_state:
                emit("STATE", "%s -> %s%s" % (
                    member.name, state,
                    " @ %.1fs" % (sample["position_ms"] / 1000.0)
                    if sample["position_ms"] is not None else "",
                ))
                member.last_state = state

            member.position_ms = sample["position_ms"]
            member.speed = sample["speed"]
            record["members"][member.name] = dict(sample, alive=True,
                                                  bias_ms=member.bias_ms)

        # -- output truth ------------------------------------------------
        if args.ocr and now - last_ocr >= args.ocr_every:
            last_ocr = now
            for member in members:
                if member.position_ms is None:
                    continue
                actual = member.screenshot_timecode_ms(args.ocr)
                if actual is None:
                    emit("OCR?", "%s: no timecode in screenshot "
                                 "(channel 2 unavailable here?)" % member.name)
                    continue
                bias = member.position_ms - actual
                record["members"][member.name]["ocr_ms"] = actual
                record["members"][member.name]["ocr_bias_ms"] = bias
                previous = member.ocr_bias_ms
                member.ocr_bias_ms = bias
                if previous is None or abs(bias - previous) > args.tolerance:
                    emit("BIAS", "%s reports %+.0fms vs its own screen "
                                 "(was %s)" % (
                        member.name, bias,
                        "%+.0fms" % previous if previous is not None else "first",
                    ))

        # -- divergence between members ----------------------------------
        live = [m for m in members
                if m.alive and m.position_ms is not None and m.speed]
        worst = 0.0
        pair = None
        for i, a in enumerate(live):
            for b in live[i + 1:]:
                delta = ((a.position_ms - a.bias_ms)
                         - (b.position_ms - b.bias_ms))
                if abs(delta) > abs(worst):
                    worst, pair = delta, (a.name, b.name)
        record["worst_offset_ms"] = worst

        # Hysteresis. A member parked just under the tolerance — a transcoding
        # one sits ~300ms out with excursions past 500ms — otherwise crosses it
        # every few seconds and produces a DIVERGE/INBAND pair each time, which
        # is noise that buries the events that matter.
        if pair and not diverged and abs(worst) > args.tolerance:
            diverged = True
            emit("DIVERGE", "%s vs %s: %+.0fms (tolerance %gms)"
                 % (pair[0], pair[1], worst, args.tolerance))
        elif pair and diverged and abs(worst) < args.tolerance * args.recover:
            diverged = False
            emit("INBAND", "worst pair back to %+.0fms" % worst)

        # -- server ------------------------------------------------------
        groups = server.groups()
        record["server"] = groups
        # Signature on what a person would call a change. LastUpdatedAt ticks
        # every sweep and Ping moves constantly, so including them made every
        # poll look like a state change — enough events to get the monitor
        # rate-limited and killed, which is worse than no monitor at all.
        signature = json.dumps(summarise(groups), sort_keys=True)[:4000]
        if signature != server.last_signature:
            if isinstance(groups, dict) and "error" in groups:
                emit("SERVER", "SyncPlay/List failed: %s" % groups["error"][:120])
            else:
                for entry in groups or []:
                    # NOT `members` — that is the member list this whole loop
                    # is watching, and shadowing it here killed the monitor.
                    roster = entry.get("Members") or []
                    emit("GROUP", "%s: %s, %d member(s)%s" % (
                        entry.get("GroupName", "?"),
                        entry.get("State", "?"),
                        len(roster),
                        "".join(
                            " [%s%s%s]" % (
                                m.get("UserName", "?"),
                                " buffering" if m.get("IsBuffering") else "",
                                " spectator" if m.get("IgnoreGroupWait") else "",
                            )
                            for m in roster
                        ),
                    ))
                if not groups:
                    emit("GROUP", "no groups")
            server.last_signature = signature

        # -- heartbeat: silence must never look like success --------------
        if now - last_heartbeat >= args.heartbeat:
            last_heartbeat = now
            names = ",".join(m.name for m in members if m.alive)
            emit("HB", "alive=[%s] worst=%+.0fms%s" % (
                names, worst,
                " ocr_bias=[%s]" % ",".join(
                    "%s%+.0f" % (m.name, m.ocr_bias_ms)
                    for m in members if m.ocr_bias_ms is not None
                ) if args.ocr else "",
            ))

        if stream:
            stream.write(json.dumps(record) + "\n")
            stream.flush()

        time.sleep(max(0.1, args.sample))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        emit("STOP", "interrupted")
