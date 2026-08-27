#!/usr/bin/env python3
"""devrepo — install a locally built plugin zip on the live server.

The server runs as its own user and the ssh account has no write access to
its plugins directory, and Jellyfin will only install a package that comes
from a *configured* repository. So the fast iteration loop is: serve dist/
over HTTP from the dev box, add it as a repository for the length of the
run, install from it, restart, and take the repository back out again.

    tools/devrepo.py serve      # foreground file server (or use --daemon)
    tools/devrepo.py install --version 10.11.0.6
    tools/devrepo.py remove     # drop the dev repository again
"""

import argparse
import hashlib
import http.server
import json
import os
import socketserver
import sys
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DIST = os.path.join(REPO, "dist")
sys.path.insert(0, HERE)
import spgroup  # noqa: E402

HOST = "192.168.1.112"
PORT = 8099
NAME = "SyncPlay v2 (dev)"
GUID = "181f9934-bf71-4941-974e-a5f2cdcccc4e"
URL = "http://%s:%d/manifest.json" % (HOST, PORT)


def build_manifest():
    versions = []
    for entry in sorted(os.listdir(DIST)):
        if not entry.startswith("syncplay-v2_10.11") or not entry.endswith(".zip"):
            continue
        version = entry[len("syncplay-v2_"):-len(".zip")]
        path = os.path.join(DIST, entry)
        with open(path, "rb") as handle:
            checksum = hashlib.md5(handle.read()).hexdigest()  # noqa: S324
        versions.append({
            "version": version,
            "changelog": "dev build from the shakedown working tree",
            "targetAbi": "10.11.0.0",
            "sourceUrl": "http://%s:%d/%s" % (HOST, PORT, entry),
            "checksum": checksum,
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(
                os.path.getmtime(path))),
            "repositoryName": NAME,
            "repositoryUrl": URL,
        })
    versions.sort(key=lambda v: [int(p) for p in v["version"].split(".")],
                  reverse=True)
    manifest = [{
        "guid": GUID,
        "name": "SyncPlay v2",
        "description": "SyncPlay protocol v2 served from a plugin (dev build)",
        "overview": "SyncPlay protocol v2 served from a plugin",
        "owner": "kontell",
        "category": "General",
        "imageUrl": None,
        "versions": versions,
    }]
    with open(os.path.join(DIST, "manifest.json"), "w") as handle:
        json.dump(manifest, handle, indent=1)
    return manifest


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=DIST, **kw)

    def log_message(self, fmt, *args):
        print("  http %s" % (fmt % args), flush=True)


def serve(daemon=False):
    build_manifest()
    if daemon:
        # A server may already be up from an earlier step of the same run.
        try:
            urllib.request.urlopen(URL, timeout=3).read()
            return None
        except Exception:  # noqa: BLE001
            pass
    socketserver.TCPServer.allow_reuse_address = True
    server = socketserver.TCPServer(("0.0.0.0", PORT), Handler)
    if daemon:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        return server
    print("serving %s on %s" % (DIST, URL), flush=True)
    server.serve_forever()


def repositories():
    return spgroup.call("PRS", "/Repositories", method="GET") or []


def set_repositories(repos):
    return spgroup.call("PRS", "/Repositories", repos, method="POST")


def add_repo():
    repos = [r for r in repositories() if r.get("Url") != URL]
    repos.append({"Name": NAME, "Url": URL, "Enabled": True})
    set_repositories(repos)
    return repos


def remove_repo():
    set_repositories([r for r in repositories() if r.get("Url") != URL])


def installed_version():
    for p in spgroup.call("PRS", "/Plugins", method="GET") or []:
        if p.get("Id", "").replace("-", "") == GUID.replace("-", ""):
            return p.get("Version")
    return None


def install(version, restart=True):
    build_manifest()
    add_repo()
    time.sleep(1)
    path = ("/Packages/Installed/SyncPlay%%20v2?version=%s&assemblyGuid=%s"
            % (version, GUID))
    print("  install ->", spgroup.call("PRS", path), flush=True)
    for _ in range(30):
        time.sleep(2)
        pending = spgroup.call("PRS", "/Packages", method="GET") and None
        if not pending:
            break
        print("  installing: %s" % json.dumps(pending)[:120], flush=True)
    if restart:
        print("  restarting the server", flush=True)
        spgroup.call("PRS", "/System/Restart")
        time.sleep(5)
        for attempt in range(60):
            try:
                got = installed_version()
                if got:
                    print("  server back: plugin %s" % got, flush=True)
                    return got
            except Exception:  # noqa: BLE001
                pass
            time.sleep(2)
    return installed_version()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("what", choices=["serve", "install", "remove", "manifest"])
    parser.add_argument("--version", default=None)
    args = parser.parse_args()
    if args.what == "serve":
        serve()
    elif args.what == "manifest":
        print(json.dumps(build_manifest(), indent=1)[:800])
    elif args.what == "remove":
        remove_repo()
        print("removed", URL)
    else:
        server = serve(daemon=True)
        print(install(args.version))
