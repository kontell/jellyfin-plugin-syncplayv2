#!/usr/bin/env python3
"""M0 spike checks against a Jellyfin server with the SyncPlayV2 spike plugin.

Usage: spike_checks.py --base http://127.0.0.1:8096 --user spikebot1:botpass

Each check prints PASS/FAIL/INFO with the observed evidence; exit code 1 if
any hard check fails. The server-side evidence log is dumped at the end
(GET /SyncPlayV2Spike/Status).
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
import uuid

import aiohttp
import websockets

RESULTS: list[tuple[str, str, str]] = []  # (verdict, name, detail)


def record(verdict: str, name: str, detail: str = "") -> None:
    RESULTS.append((verdict, name, detail))
    print(f"[{verdict}] {name}" + (f" — {detail}" if detail else ""), flush=True)


class Client:
    def __init__(self, base: str, username: str, password: str) -> None:
        self.base = base.rstrip("/")
        self.ws_base = self.base.replace("https://", "wss://").replace("http://", "ws://")
        self.username = username
        self.password = password
        self.device_id = f"spike-{uuid.uuid4().hex[:8]}"
        self.token: str | None = None
        self.session: aiohttp.ClientSession | None = None
        self.ws = None
        self.ws_msgs: list[tuple[float, str, object]] = []
        self._reader: asyncio.Task | None = None

    @property
    def auth_header(self) -> str:
        parts = f'MediaBrowser Client="spike", Device="spike", DeviceId="{self.device_id}", Version="0.90"'
        if self.token:
            parts += f', Token="{self.token}"'
        return parts

    async def login(self) -> None:
        self.session = aiohttp.ClientSession()
        async with self.session.post(
            f"{self.base}/Users/AuthenticateByName",
            json={"Username": self.username, "Pw": self.password},
            headers={"Authorization": self.auth_header},
        ) as resp:
            resp.raise_for_status()
            data = await resp.json()
            self.token = data["AccessToken"]

    async def close(self) -> None:
        if self._reader:
            self._reader.cancel()
        if self.ws:
            await self.ws.close()
        if self.session:
            await self.session.close()

    async def post(self, path: str, body: object | None = None) -> aiohttp.ClientResponse:
        assert self.session is not None
        kwargs: dict = {"headers": {"Authorization": self.auth_header}}
        if body is not None:
            kwargs["json"] = body
        resp = await self.session.post(f"{self.base}{path}", **kwargs)
        await resp.read()
        return resp

    async def get(self, path: str) -> aiohttp.ClientResponse:
        assert self.session is not None
        resp = await self.session.get(f"{self.base}{path}", headers={"Authorization": self.auth_header})
        await resp.read()
        return resp

    async def connect_ws(self) -> None:
        self.ws = await websockets.connect(
            f"{self.ws_base}/socket?deviceId={self.device_id}",
            additional_headers={"Authorization": self.auth_header},
        )
        self._reader = asyncio.create_task(self._read_loop())
        await asyncio.sleep(0.5)  # let the session controller attach

    async def _read_loop(self) -> None:
        assert self.ws is not None
        try:
            async for raw in self.ws:
                try:
                    msg = json.loads(raw)
                except json.JSONDecodeError:
                    continue
                self.ws_msgs.append((time.time(), msg.get("MessageType", "?"), msg.get("Data")))
        except websockets.ConnectionClosed:
            pass

    async def wait_ws(self, mtype: str, timeout: float = 5.0, predicate=None):
        deadline = time.time() + timeout
        seen = 0
        while time.time() < deadline:
            while seen < len(self.ws_msgs):
                _, mt, data = self.ws_msgs[seen]
                seen += 1
                if mt == mtype and (predicate is None or predicate(data)):
                    return data
            await asyncio.sleep(0.05)
        return None

    async def ws_alive(self) -> bool:
        """KeepAlive round trip proves the socket still works."""
        assert self.ws is not None
        before = len(self.ws_msgs)
        try:
            await self.ws.send(json.dumps({"MessageType": "KeepAlive"}))
        except websockets.ConnectionClosed:
            return False
        deadline = time.time() + 3
        while time.time() < deadline:
            for _, mt, _data in self.ws_msgs[before:]:
                if mt == "KeepAlive":
                    return True
            await asyncio.sleep(0.05)
        return False


async def run(base: str, username: str, password: str) -> None:
    c = Client(base, username, password)
    await c.login()
    record("INFO", "login", f"user {username} device {c.device_id}")

    await c.connect_ws()
    record("INFO", "websocket", "connected to /socket")

    # --- Check 1: new sub-route under /SyncPlay + stock policy reuse -------
    resp = await c.post("/SyncPlay/Hello", {"ProtocolVersion": 2})
    if resp.status == 200:
        body = await resp.json()
        ok = body.get("ProtocolVersion") == 2 and body.get("Spike") is True
        record("PASS" if ok else "FAIL", "hello-subroute", f"200 {body}")
    else:
        record("FAIL", "hello-subroute", f"status {resp.status}")

    # --- Check 2: stock /SyncPlay/New drives the plugin manager, and the
    # GroupJoined push is the plugin's v2 wire shape ------------------------
    resp = await c.post("/SyncPlay/New", {"GroupName": "spike", "ProtocolVersion": 2})
    record("PASS" if resp.status == 200 else "FAIL", "stock-new-route", f"status {resp.status}")
    group = await resp.json() if resp.status == 200 else {}
    group_id = group.get("GroupId")

    joined = await c.wait_ws(
        "SyncPlayGroupUpdate",
        predicate=lambda d: isinstance(d, dict) and d.get("Type") == "GroupJoined",
    )
    if joined is None:
        record("FAIL", "wire-groupjoined", "no GroupJoined over WS within 5s")
    else:
        info = joined.get("Data") or {}
        shape_ok = (
            "StateVersion" in joined
            and info.get("ProtocolVersion") == 2
            and isinstance(info.get("Members"), list)
            and isinstance(info.get("Participants"), list)
        )
        record(
            "PASS" if shape_ok else "FAIL",
            "wire-groupjoined",
            f"StateVersion={joined.get('StateVersion')} ProtocolVersion={info.get('ProtocolVersion')} "
            f"Members={len(info.get('Members') or [])} Participants={info.get('Participants')}",
        )

    # --- Check 3: body sniffer sees ProtocolVersion on stock Join ----------
    if group_id:
        resp = await c.post("/SyncPlay/Join", {"GroupId": group_id, "ProtocolVersion": 2})
        record("INFO", "stock-join", f"status {resp.status}")

    # --- Check 4: policy handler consults the plugin manager ---------------
    # Pause has [Authorize(SyncPlayIsInGroup)] and NO BODY (web client parity):
    # 204 means the policy asked OUR IsUserActive and got true.
    resp = await c.post("/SyncPlay/Pause")
    record(
        "PASS" if resp.status == 204 else "FAIL",
        "policy-isingroup",
        f"bodyless POST /SyncPlay/Pause => {resp.status} (204 proves SyncPlayIsInGroup consulted the plugin manager)",
    )

    # --- Check 5: shadow route Order=-1 ------------------------------------
    resp = await c.get("/SyncPlay/List")
    shadow = resp.headers.get("X-SyncPlayV2-Shadow")
    if resp.status == 200 and shadow == "1":
        record("PASS", "shadow-route", "GET /SyncPlay/List served by plugin shadow (Order=-1 wins)")
    elif resp.status == 200:
        record("INFO", "shadow-route", "served by STOCK controller (Order=-1 did not take precedence)")
    else:
        record("FAIL", "shadow-route", f"status {resp.status} (ambiguous match?) body={await resp.text()}")

    # --- Check 6: OpenAPI generation with the duplicate route --------------
    resp = await c.get("/api-docs/openapi.json")
    record(
        "PASS" if resp.status == 200 else "FAIL",
        "openapi-with-shadow",
        f"status {resp.status}" + ("" if resp.status == 200 else f" body={(await resp.text())[:200]}"),
    )

    # --- Check 7: v2-only update through stock envelope --------------------
    resp = await c.post("/SyncPlayV2Spike/SendGroupUpdate?type=StateSnapshot")
    record("INFO", "send-update-endpoint", f"status {resp.status}")
    snap = await c.wait_ws(
        "SyncPlayGroupUpdate",
        predicate=lambda d: isinstance(d, dict) and d.get("Type") == "StateSnapshot",
    )
    record(
        "PASS" if snap is not None else "FAIL",
        "wire-statesnapshot",
        "StateSnapshot with string Type delivered over stock envelope" if snap else "not received",
    )

    # --- Check 8: SyncPlayCommand with StateVersion ------------------------
    await c.post("/SyncPlayV2Spike/SendCommand?command=Stop")
    cmd = await c.wait_ws("SyncPlayCommand")
    if cmd is None:
        record("FAIL", "wire-command", "no SyncPlayCommand received")
    else:
        ok = isinstance(cmd, dict) and "StateVersion" in cmd and cmd.get("Command") == "Stop"
        record("PASS" if ok else "FAIL", "wire-command", f"Command={cmd.get('Command')} StateVersion={cmd.get('StateVersion')}")

    # --- Check 9: dedicated TimeSync WebSocket path ------------------------
    try:
        ts_ws = await websockets.connect(
            f"{c.ws_base}/SyncPlayV2Spike/TimeSync",
            additional_headers={"Authorization": c.auth_header},
        )
        t0 = int(time.time() * 1000)
        await ts_ws.send(json.dumps({"MessageType": "TimeSync", "Data": t0}))
        raw = await asyncio.wait_for(ts_ws.recv(), timeout=5)
        reply = json.loads(raw)
        data = reply.get("Data") or {}
        ok = data.get("T0") == t0 and "T1" in data and "T2" in data
        rtt = int(time.time() * 1000) - t0 - (data.get("T2", 0) - data.get("T1", 0))
        record("PASS" if ok else "FAIL", "ws-timesync-router", f"reply {data} rtt~{rtt}ms")
        await ts_ws.close()
    except Exception as exc:  # noqa: BLE001 - report any failure mode verbatim
        record("FAIL", "ws-timesync-router", f"{type(exc).__name__}: {exc}")

    # --- Check 10: unknown MessageType on the MAIN socket ------------------
    # (Core transport behavior: drop-and-log vs socket teardown.)
    assert c.ws is not None
    await c.ws.send(json.dumps({"MessageType": "TimeSync", "Data": 12345}))
    await asyncio.sleep(2)
    alive = await c.ws_alive()
    record(
        "INFO",
        "unknown-ws-type",
        "socket SURVIVED unknown MessageType (drop-and-log)" if alive else "socket DIED after unknown MessageType (teardown)",
    )

    # --- Check 11: reconnect raises SessionControllerConnected again -------
    if not alive:
        record("INFO", "reconnect", "reconnecting after teardown")
    if c.ws:
        await c.ws.close()
        if c._reader:
            c._reader.cancel()
    await asyncio.sleep(1)
    await c.connect_ws()
    await asyncio.sleep(1)

    # --- Server-side evidence ----------------------------------------------
    resp = await c.get("/SyncPlayV2Spike/Status")
    if resp.status != 200:
        record("FAIL", "status-endpoint", f"status {resp.status}")
    else:
        status = await resp.json()
        record(
            "PASS" if status.get("ManagerIsSpike") else "FAIL",
            "di-shadow",
            f"ISyncPlayManager => {status.get('ResolvedSyncPlayManager')}",
        )
        evidence: list[str] = status.get("Evidence", [])

        def count(tag: str) -> int:
            return sum(1 for line in evidence if f"[{tag}]" in line)

        checks = [
            ("evidence-hosted-service", count("hosted-service") >= 1),
            ("evidence-controller-calls", count("controller-call") >= 2),
            ("evidence-policy-check", count("policy-check") >= 1),
            ("evidence-playback-request", count("playback-request") >= 1),
            ("evidence-controller-connected", count("event-controller-connected") >= 2),
            ("evidence-body-sniffer", any("[body-sniffer]" in line and "ProtocolVersion=2" in line for line in evidence)),
        ]
        for name, ok in checks:
            record("PASS" if ok else "FAIL", name)

        print("\n===== server evidence log =====")
        for line in evidence:
            print("  " + line)

    await c.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:8096")
    parser.add_argument("--user", required=True, help="username:password")
    args = parser.parse_args()
    username, _, password = args.user.partition(":")

    asyncio.run(run(args.base, username, password))

    fails = [r for r in RESULTS if r[0] == "FAIL"]
    print(f"\n===== {len([r for r in RESULTS if r[0] == 'PASS'])} PASS, {len(fails)} FAIL =====")
    for _, name, detail in fails:
        print(f"  FAIL {name}: {detail}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
