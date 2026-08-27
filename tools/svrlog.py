#!/usr/bin/env python3
"""svrlog — read the Jellyfin server log without shell access to the server.

The plugin's own decisions (rendezvous, hot join, withholding) are only
visible server-side, and the log directory is jellyfin:adm 750 with no group
membership for the ssh user. The admin API serves the same file, so the
scenarios read it over HTTP with the same mark/since shape rig.py uses for
Kodi logs.
"""

import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import spgroup  # noqa: E402


def _token(member="PRS"):
    return spgroup.read_settings(member)[0]


def current_name(member="PRS"):
    logs = spgroup.call(member, "/System/Logs", method="GET") or []
    named = [l for l in logs if (l.get("Name") or "").startswith("jellyfin")]
    named.sort(key=lambda l: l.get("DateModified") or "", reverse=True)
    return named[0]["Name"] if named else None


def fetch(name=None, member="PRS"):
    name = name or current_name(member)
    request = urllib.request.Request(
        "%s/System/Logs/Log?name=%s" % (spgroup.SERVER, name))
    request.add_header("Authorization", 'MediaBrowser Token="%s"' % _token(member))
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read().decode("utf-8", "replace")


def mark(member="PRS"):
    return len(fetch(member=member))


def since(offset, contains=None, member="PRS"):
    text = fetch(member=member)[offset:]
    lines = text.splitlines()
    if contains:
        lines = [l for l in lines if contains in l]
    return lines


def grep(patterns, offset=0, member="PRS"):
    """Lines after `offset` containing any of `patterns` (a list)."""
    text = fetch(member=member)[offset:]
    return [l for l in text.splitlines() if any(p in l for p in patterns)]


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--grep", nargs="*", default=["SyncPlay"])
    parser.add_argument("--tail", type=int, default=40)
    args = parser.parse_args()
    for line in grep(args.grep)[-args.tail:]:
        print(line)
