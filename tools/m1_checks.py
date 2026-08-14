#!/usr/bin/env python3
"""M1 engine checks: a real mixed v1+v2 group against the plugin engine.

Usage: m1_checks.py --base http://127.0.0.1:8096 --v2 syncbot-a:sp-test --v1 syncbot-b:sp-test
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from spike_checks import Client, RESULTS, record  # noqa: E402

import websockets  # noqa: E402


def group_updates(c: Client, gtype: str) -> list[dict]:
    out = []
    for _, mt, data in c.ws_msgs:
        if mt == "SyncPlayGroupUpdate" and isinstance(data, dict) and data.get("Type") == gtype:
            out.append(data)
    return out


def commands(c: Client, name: str | None = None) -> list[dict]:
    out = []
    for _, mt, data in c.ws_msgs:
        if mt == "SyncPlayCommand" and isinstance(data, dict) and (name is None or data.get("Command") == name):
            out.append(data)
    return out


async def wait_group_update(c: Client, gtype: str, timeout: float = 6.0):
    return await c.wait_ws(
        "SyncPlayGroupUpdate",
        timeout=timeout,
        predicate=lambda d: isinstance(d, dict) and d.get("Type") == gtype,
    )


async def find_movie(c: Client) -> str | None:
    resp = await c.get("/Items?IncludeItemTypes=Movie&Recursive=true&Limit=1")
    if resp.status != 200:
        return None
    items = (await resp.json()).get("Items") or []
    return items[0]["Id"] if items else None


async def run(base: str, v2_user: str, v1_user: str) -> None:
    u2, _, p2 = v2_user.partition(":")
    u1, _, p1 = v1_user.partition(":")

    a = Client(base, u2, p2)  # v2
    b = Client(base, u1, p1)  # v1
    await a.login()
    await b.login()
    await a.connect_ws()
    await b.connect_ws()
    record("INFO", "setup", f"v2={u2} v1={u1}")

    # --- Hello: probe + transport descriptor -------------------------------
    resp = await a.post("/SyncPlay/Hello", {"ProtocolVersion": 2})
    hello = await resp.json() if resp.status == 200 else {}
    ok = resp.status == 200 and hello.get("ProtocolVersion") == 2 and (hello.get("TimeSync") or {}).get("WebSocketPath")
    record("PASS" if ok else "FAIL", "hello", f"{resp.status} {hello}")

    # --- v2 New via stock route (body-sniffed) -----------------------------
    resp = await a.post("/SyncPlay/New", {"GroupName": "m1", "ProtocolVersion": 2})
    record("PASS" if resp.status == 200 else "FAIL", "new-group", f"status {resp.status}")

    joined_a = await wait_group_update(a, "GroupJoined")
    info_a = (joined_a or {}).get("Data") or {}
    ok = joined_a is not None and info_a.get("ProtocolVersion") == 2 and isinstance(info_a.get("Members"), list)
    record("PASS" if ok else "FAIL", "v2-groupjoined", f"ProtocolVersion={info_a.get('ProtocolVersion')} Members={len(info_a.get('Members') or [])}")
    group_id = info_a.get("GroupId")

    # --- v1 Join (no version anywhere) -------------------------------------
    resp = await b.post("/SyncPlay/Join", {"GroupId": group_id})
    record("INFO", "v1-join", f"status {resp.status}")
    joined_b = await wait_group_update(b, "GroupJoined")
    info_b = (joined_b or {}).get("Data") or {}
    ok = joined_b is not None and info_b.get("ProtocolVersion") in (None, 1) and isinstance(info_b.get("Members"), list)
    record(
        "PASS" if ok else "FAIL",
        "v1-groupjoined-memberscoped",
        f"ProtocolVersion={info_b.get('ProtocolVersion')!r} (must be absent/null for v1) Members={len(info_b.get('Members') or [])}",
    )
    user_joined = await wait_group_update(a, "UserJoined")
    record("PASS" if user_joined is not None else "FAIL", "userjoined-broadcast", str((user_joined or {}).get("Data")))

    # --- queue + ready + unpause -------------------------------------------
    movie = await find_movie(a)
    if not movie:
        record("FAIL", "movie", "no movie in library; cannot exercise playback")
    resp = await a.post("/SyncPlay/SetNewQueue", {"PlayingQueue": [movie], "PlayingItemPosition": 0, "StartPositionTicks": 0})
    record("INFO", "setnewqueue", f"status {resp.status}")

    pq_a = await wait_group_update(a, "PlayQueue")
    pq_b = await wait_group_update(b, "PlayQueue")
    ok = pq_a is not None and pq_b is not None
    record("PASS" if ok else "FAIL", "playqueue-both", "PlayQueue update reached v2 and v1 members" if ok else f"a={pq_a is not None} b={pq_b is not None}")
    playlist = ((pq_a or {}).get("Data") or {}).get("Playlist") or []
    playlist_item = playlist[0].get("PlaylistItemId") if playlist else None

    now_iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + "Z"
    ready = {"When": now_iso, "PositionTicks": 0, "IsPlaying": False, "PlaylistItemId": playlist_item}
    await a.post("/SyncPlay/Ready", ready)
    await b.post("/SyncPlay/Ready", ready)

    cmd_a = await a.wait_ws("SyncPlayCommand", timeout=8, predicate=lambda d: isinstance(d, dict) and d.get("Command") == "Unpause")
    cmd_b = await b.wait_ws("SyncPlayCommand", timeout=8, predicate=lambda d: isinstance(d, dict) and d.get("Command") == "Unpause")
    ok = cmd_a is not None and cmd_b is not None and "StateVersion" in (cmd_a or {})
    record("PASS" if ok else "FAIL", "unpause-both", f"a={cmd_a is not None} b={cmd_b is not None} StateVersion={(cmd_a or {}).get('StateVersion')}")

    # --- beacons: v2 only ---------------------------------------------------
    await asyncio.sleep(7)
    beacons_a = group_updates(a, "PositionBeacon")
    beacons_b = group_updates(b, "PositionBeacon")
    record("PASS" if len(beacons_a) >= 1 else "FAIL", "beacons-v2", f"{len(beacons_a)} beacon(s) to v2 member in 7s")
    record("PASS" if len(beacons_b) == 0 else "FAIL", "beacons-v1-isolation", f"{len(beacons_b)} beacon(s) to v1 member (must be 0)")
    if beacons_a:
        d = beacons_a[-1].get("Data") or {}
        ok = d.get("PlaylistItemId") == playlist_item and isinstance(d.get("PositionTicks"), int) and d.get("PositionTicks") > 0
        record("PASS" if ok else "FAIL", "beacon-shape", f"{d}")

    # --- snapshot on demand -------------------------------------------------
    resp = await a.post("/SyncPlay/Snapshot")
    record("INFO", "snapshot-endpoint", f"status {resp.status}")
    snap = await wait_group_update(a, "StateSnapshot")
    sd = (snap or {}).get("Data") or {}
    ok = (
        snap is not None
        and sd.get("State") == "Playing"
        and sd.get("IsPlaying") is True
        and isinstance(sd.get("PlayQueue"), dict)
        and len(sd.get("Members") or []) == 2
    )
    record("PASS" if ok else "FAIL", "snapshot", f"State={sd.get('State')} IsPlaying={sd.get('IsPlaying')} Members={len(sd.get('Members') or [])}")

    # --- state versions monotonic ------------------------------------------
    versions = [d.get("StateVersion") for _, mt, d in a.ws_msgs
                if mt in ("SyncPlayGroupUpdate", "SyncPlayCommand") and isinstance(d, dict)
                and d.get("StateVersion") is not None and d.get("GroupId") == group_id]
    monotonic = all(x <= y for x, y in zip(versions, versions[1:]))
    record("PASS" if versions and monotonic and versions[-1] > versions[0] else "FAIL", "stateversion-monotonic", f"{versions[:12]}...")

    # --- buffering grace: brief rebuffer must not pause the group -----------
    pause_before = len(commands(a, "Pause"))
    buf = {"When": now_iso, "PositionTicks": 0, "IsPlaying": True, "PlaylistItemId": playlist_item}
    await b.post("/SyncPlay/Buffering", buf)
    await asyncio.sleep(0.8)
    await b.post("/SyncPlay/Ready", {**buf, "IsPlaying": True})
    await asyncio.sleep(3.5)
    pause_after = len(commands(a, "Pause"))
    record(
        "PASS" if pause_after == pause_before else "FAIL",
        "buffering-grace",
        f"{pause_after - pause_before} Pause command(s) to the other member after a 0.8s rebuffer (must be 0)",
    )

    # --- shadowed List: member-scoped ProtocolVersion -----------------------
    resp = await a.get("/SyncPlay/List")
    la = (await resp.json())[0] if resp.status == 200 and (await resp.json()) else {}
    resp2 = await b.get("/SyncPlay/List")
    lb = (await resp2.json())[0] if resp2.status == 200 and (await resp2.json()) else {}
    ok = la.get("ProtocolVersion") == 2 and isinstance(la.get("Members"), list) and lb.get("ProtocolVersion") in (None, 1)
    record("PASS" if ok else "FAIL", "list-shadow", f"v2 sees PV={la.get('ProtocolVersion')}, v1 sees PV={lb.get('ProtocolVersion')!r}, Members={len(la.get('Members') or [])}")

    # --- reconnect: v1 triple vs v2 snapshot --------------------------------
    for c, label, expect_snapshot in ((b, "v1", False), (a, "v2", True)):
        assert c.ws is not None
        before = len(c.ws_msgs)
        await c.ws.close()
        if c._reader:
            c._reader.cancel()
        await asyncio.sleep(1.5)
        await c.connect_ws()
        await asyncio.sleep(2.5)
        new_msgs = [(mt, d) for _, mt, d in c.ws_msgs[before:]]
        got_snapshot = any(mt == "SyncPlayGroupUpdate" and isinstance(d, dict) and d.get("Type") == "StateSnapshot" for mt, d in new_msgs)
        got_joined = any(mt == "SyncPlayGroupUpdate" and isinstance(d, dict) and d.get("Type") == "GroupJoined" for mt, d in new_msgs)
        got_queue = any(mt == "SyncPlayGroupUpdate" and isinstance(d, dict) and d.get("Type") == "PlayQueue" for mt, d in new_msgs)
        got_cmd = any(mt == "SyncPlayCommand" for mt, d in new_msgs)
        if expect_snapshot:
            ok = got_snapshot and not got_joined
            record("PASS" if ok else "FAIL", f"reconnect-{label}", f"snapshot={got_snapshot} joined={got_joined} (v2 expects snapshot only)")
        else:
            ok = got_joined and got_queue and got_cmd and not got_snapshot
            record("PASS" if ok else "FAIL", f"reconnect-{label}", f"joined={got_joined} queue={got_queue} cmd={got_cmd} snapshot={got_snapshot} (v1 expects the triple)")

    # no UserLeft should have been broadcast for transient reconnects
    userlefts = group_updates(a, "UserLeft") + group_updates(b, "UserLeft")
    record("PASS" if len(userlefts) == 0 else "FAIL", "no-userleft-on-reconnect", f"{len(userlefts)} UserLeft during grace-window reconnects (must be 0)")

    # --- dedicated WS time sync ---------------------------------------------
    try:
        ts = await websockets.connect(
            a.ws_base + "/SyncPlay/TimeSync",
            additional_headers={"Authorization": a.auth_header},
        )
        t0 = int(time.time() * 1000)
        await ts.send(json.dumps({"MessageType": "TimeSync", "Data": t0}))
        reply = json.loads(await asyncio.wait_for(ts.recv(), timeout=5))
        d = reply.get("Data") or {}
        record("PASS" if d.get("T0") == t0 and "T1" in d else "FAIL", "ws-timesync", str(d))
        await ts.close()
    except Exception as exc:  # noqa: BLE001
        record("FAIL", "ws-timesync", f"{type(exc).__name__}: {exc}")

    # --- teardown ------------------------------------------------------------
    await a.post("/SyncPlay/Leave")
    await b.post("/SyncPlay/Leave")
    resp = await a.get("/SyncPlay/List")
    remaining = await resp.json() if resp.status == 200 else ["?"]
    record("PASS" if remaining == [] else "FAIL", "leave-cleanup", f"{len(remaining)} group(s) remain (must be 0)")

    await a.close()
    await b.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:8096")
    parser.add_argument("--v2", required=True, help="v2 user:password")
    parser.add_argument("--v1", required=True, help="v1 user:password")
    args = parser.parse_args()

    asyncio.run(run(args.base, args.v2, args.v1))

    fails = [r for r in RESULTS if r[0] == "FAIL"]
    print(f"\n===== {len([r for r in RESULTS if r[0] == 'PASS'])} PASS, {len(fails)} FAIL =====")
    for _, name, detail in fails:
        print(f"  FAIL {name}: {detail}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
