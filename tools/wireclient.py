#!/usr/bin/env python3
"""wireclient — a synthetic SyncPlay client with a real websocket.

Built for the three server-side fixes in plugin 10.11.0.5, all of which are
about what the *wire* does for a member at a given protocol version. Driving
those from kofin is impossible (kofin is v2 by construction) and driving them
from jellyfin-web is possible but unrepeatable: the browser decides when to
report, what to report, and whether to converge.

This client decides all three. It authenticates as its own Jellyfin session,
holds a real websocket so the server's SessionControllers list is non-empty
(without which every send is dropped and nothing can be observed), and either
negotiates v2 or deliberately does not — a Join body with no ``ProtocolVersion``
field leaves the registry with no entry, and ``Resolve`` defaults to 1. That is
exactly how a stock client looks.

Every inbound message is timestamped and kept, so an assertion can be made on
what a v1 socket did *not* receive, which is the only way to test a withholding
rule.

    c = WireClient("V1", protocol=1)
    c.connect(); c.join(group_id)
    c.ready(position_ms=90000, is_playing=False)      # deliberately adrift
    c.wait_for("Seek")                                 # the correction
    assert not c.received("StateSnapshot")
"""

import json
import ssl
import sys
import threading
import time
import urllib.error
import urllib.request

sys.path.insert(0, "/home/conor/.kodi/addons/script.module.websocket/lib")
import websocket  # noqa: E402

SERVER = "http://192.168.1.167:8096"
TICKS = 10000


def _token_from(path):
    import re
    with open(path) as handle:
        text = handle.read()
    return re.search(r'id="accessToken"[^>]*>([^<]*)<', text).group(1)


DEFAULT_TOKEN_FILE = ("/home/conor/.var/app/tv.kodi.Kodi/data/userdata/"
                      "addon_data/plugin.video.kofin/settings.xml")


class WireClient:
    """One synthetic member: REST identity + websocket, at a chosen version."""

    def __init__(self, name, protocol=1, token=None, server=SERVER,
                 client=None, version="10.11.11"):
        self.name = name
        self.protocol = protocol
        self.server = server
        self.token = token or _token_from(DEFAULT_TOKEN_FILE)
        self.device_id = "wire-%s" % name.lower()
        # A v1 probe wears a v1 client's name so the registry key (client +
        # device) can never collide with a v2 run of the same probe.
        self.client = client or ("Jellyfin Web" if protocol == 1 else "Kofin")
        self.version = version
        self.inbox = []              # (t, message_type, data)
        self.lock = threading.Lock()
        self.ws = None
        self.thread = None
        self.group_id = None
        self.playlist_item_id = None
        self.log = []
        self.session_id = None

    # ------------------------------------------------------------- REST

    def _auth_header(self):
        return ('MediaBrowser Client="%s", Device="%s", DeviceId="%s", '
                'Version="%s", Token="%s"'
                % (self.client, self.name, self.device_id, self.version, self.token))

    def call(self, path, body=None, method="POST"):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.server + path, data=data, method=method)
        request.add_header("Content-Type", "application/json")
        request.add_header("Authorization", self._auth_header())
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                raw = response.read().decode()
                return json.loads(raw) if raw.strip() else None
        except urllib.error.HTTPError as error:
            return {"_error": error.code, "_body": error.read().decode()[:300]}

    # ------------------------------------------------------ own token

    def mint_token(self, authoriser="PRS"):
        """Get a token of this client's own, via Quick Connect.

        Needed because Jellyfin binds a websocket to the *token's* device, not
        to the ``deviceId`` query parameter: borrowing another member's token
        lands the socket on that member's session, where it sees that member's
        traffic and nothing of its own. Measured — a v1 probe on a borrowed
        token received a StateSnapshot that had been correctly sent to the v2
        member whose token it was.

        Quick Connect mints a token against this identity without needing the
        account password: initiate as ourselves, approve as an already
        authenticated session, then exchange the secret.
        """
        import sys as _sys, os as _os
        _sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
        import spgroup

        self.session_id = None
        cache = _cache_read()
        if self.device_id in cache:
            self.token = cache[self.device_id]
            probe = self.call("/System/Info", method="GET")
            if probe and "_error" not in probe:
                self.session_id = self._resolve_session()
                return self.token

        init = self.call("/QuickConnect/Initiate", None, method="POST")
        if not init or "Secret" not in init:
            raise SystemExit("QuickConnect/Initiate failed: %s" % init)
        spgroup.call(authoriser, "/QuickConnect/Authorize?code=%s" % init["Code"])
        auth = self.call("/Users/AuthenticateWithQuickConnect", {"Secret": init["Secret"]})
        if not auth or "AccessToken" not in auth:
            raise SystemExit("QuickConnect exchange failed: %s" % auth)
        self.token = auth["AccessToken"]
        self.session_id = (auth.get("SessionInfo") or {}).get("Id")
        cache[self.device_id] = self.token
        _cache_write(cache)
        self.session_id = self.session_id or self._resolve_session()
        return self.token

    def _resolve_session(self):
        for s in (self.call("/Sessions", method="GET") or []):
            if s.get("DeviceId") == self.device_id:
                return s.get("Id")
        return None

    # -------------------------------------------------------- websocket

    def connect(self, timeout=15):
        # Touch REST first so the session exists before the socket looks it up.
        self.call("/Sessions/Capabilities/Full", {
            "PlayableMediaTypes": ["Video", "Audio"],
            "SupportedCommands": ["PlayState", "Play"],
            "SupportsMediaControl": True,
        })
        url = ("%s/socket?api_key=%s&deviceId=%s"
               % (self.server.replace("http://", "ws://").replace("https://", "wss://"),
                  self.token, self.device_id))
        self.ws = websocket.WebSocketApp(
            url,
            on_message=self._on_message,
            on_error=lambda _ws, e: self._note("ws-error", str(e)),
            on_close=lambda _ws, *a: self._note("ws-close", ""),
        )
        self.thread = threading.Thread(
            target=self.ws.run_forever,
            kwargs={"sslopt": {"cert_reqs": ssl.CERT_NONE}}, daemon=True)
        self.thread.start()
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.ws.sock and self.ws.sock.connected:
                # KeepAlive so the server does not reap us mid-scenario.
                self.send({"MessageType": "KeepAlive"})
                # The session only exists while something is attached to it, so
                # resolve the id here rather than at mint time.
                self.session_id = self._resolve_session() or self.session_id
                return True
            time.sleep(0.2)
        return False

    def _note(self, kind, text):
        with self.lock:
            self.log.append((time.time(), kind, text))

    def _on_message(self, _ws, raw):
        try:
            msg = json.loads(raw)
        except Exception:  # noqa: BLE001
            return
        mtype = msg.get("MessageType")
        data = msg.get("Data")
        with self.lock:
            self.inbox.append((time.time(), mtype, data))
        if mtype == "ForceKeepAlive":
            self.send({"MessageType": "KeepAlive"})
        if mtype == "SyncPlayGroupUpdate":
            self._track_queue(data)

    def _track_queue(self, data):
        """Keep the current PlaylistItemId in view.

        A Ready whose PlaylistItemId does not match the group's playing item is
        discarded by the waiting state without a word, so a probe that does not
        track it looks exactly like a probe the server is ignoring — which cost
        one wrong reading of the correction path before this was added.
        """
        payload = (data or {}).get("Data")
        for candidate in (payload, (payload or {}).get("PlayQueue")):
            if not isinstance(candidate, dict):
                continue
            playlist = candidate.get("Playlist")
            if isinstance(playlist, list) and playlist:
                index = candidate.get("PlayingItemIndex") or 0
                if 0 <= index < len(playlist):
                    self.playlist_item_id = playlist[index].get("PlaylistItemId")
                    return

    def send(self, obj):
        try:
            self.ws.send(json.dumps(obj))
        except Exception as error:  # noqa: BLE001
            self._note("send-error", str(error))

    def close(self):
        try:
            self.ws.close()
        except Exception:  # noqa: BLE001
            pass

    # ----------------------------------------------------------- syncplay

    def new_group(self, name="Wire"):
        body = {"GroupName": name}
        if self.protocol >= 2:
            body["ProtocolVersion"] = self.protocol
        return self.call("/SyncPlay/New", body)

    def join(self, group_id):
        """v1 joins with no ProtocolVersion field at all — the registry then
        has no entry for this identity and Resolve() falls back to 1."""
        body = {"GroupId": group_id}
        if self.protocol >= 2:
            body["ProtocolVersion"] = self.protocol
        self.group_id = group_id
        return self.call("/SyncPlay/Join", body)

    def leave(self):
        return self.call("/SyncPlay/Leave")

    def ready(self, position_ms, is_playing=False, playlist_item_id=None, when=None):
        return self.call("/SyncPlay/Ready", {
            "When": when or _utcnow(),
            "PositionTicks": int(position_ms) * TICKS,
            "IsPlaying": bool(is_playing),
            "PlaylistItemId": playlist_item_id or self.playlist_item_id
            or "00000000-0000-0000-0000-000000000000",
        })

    def buffering(self, position_ms, is_playing=False, playlist_item_id=None):
        return self.call("/SyncPlay/Buffering", {
            "When": _utcnow(),
            "PositionTicks": int(position_ms) * TICKS,
            "IsPlaying": bool(is_playing),
            "PlaylistItemId": playlist_item_id or self.playlist_item_id
            or "00000000-0000-0000-0000-000000000000",
        })

    def ignore_wait(self, on=True):
        return self.call("/SyncPlay/SetIgnoreWait", {"IgnoreWait": bool(on)})

    def ping(self, ms=100):
        return self.call("/SyncPlay/Ping", {"Ping": ms})

    # ------------------------------------------------------------ inbox

    def snapshot(self):
        with self.lock:
            return list(self.inbox)

    def group_updates(self, since=0.0):
        out = []
        for t, mtype, data in self.snapshot():
            if t < since or mtype != "SyncPlayGroupUpdate":
                continue
            out.append((t, (data or {}).get("Type"), data))
        return out

    def commands(self, since=0.0):
        return [(t, (data or {}).get("Command"), data)
                for t, mtype, data in self.snapshot()
                if t >= since and mtype == "SyncPlayCommand"]

    def received(self, update_type, since=0.0):
        return [u for u in self.group_updates(since) if u[1] == update_type]

    def wait_for_command(self, command, since, timeout=15):
        deadline = time.time() + timeout
        while time.time() < deadline:
            hits = [c for c in self.commands(since) if c[1] == command]
            if hits:
                return hits[-1]
            time.sleep(0.2)
        return None

    def wait_for_update(self, update_type, since, timeout=15):
        deadline = time.time() + timeout
        while time.time() < deadline:
            hits = self.received(update_type, since)
            if hits:
                return hits[-1]
            time.sleep(0.2)
        return None


CACHE = "/tmp/claude-1000/wiretokens.json"


def _cache_read():
    try:
        with open(CACHE) as handle:
            return json.load(handle)
    except Exception:  # noqa: BLE001
        return {}


def _cache_write(data):
    try:
        with open(CACHE, "w") as handle:
            json.dump(data, handle)
    except Exception:  # noqa: BLE001
        pass


def _utcnow():
    import datetime
    return datetime.datetime.now(datetime.timezone.utc).strftime(
        "%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
