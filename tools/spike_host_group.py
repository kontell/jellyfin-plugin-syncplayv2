#!/usr/bin/env python3
"""Hold a spike SyncPlay group open so a human can join it from the web UI.

Creates group 'spike-web' as the given user and keeps the session's WebSocket
alive, printing every SyncPlay message it receives (so UserJoined/UserLeft from
the web participant are visible). Exits after ~20 minutes or on socket loss.
"""

import argparse
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from spike_checks import Client  # noqa: E402


async def main(base: str, username: str, password: str) -> None:
    c = Client(base, username, password)
    await c.login()
    await c.connect_ws()
    resp = await c.post("/SyncPlay/Hello", {"ProtocolVersion": 2})
    print(f"hello: {resp.status}", flush=True)
    resp = await c.post("/SyncPlay/New", {"GroupName": "spike-web", "ProtocolVersion": 2})
    print(f"group spike-web created: {resp.status}", flush=True)

    seen = 0
    for _ in range(120):
        while seen < len(c.ws_msgs):
            _, mtype, data = c.ws_msgs[seen]
            seen += 1
            if mtype in ("SyncPlayGroupUpdate", "SyncPlayCommand"):
                print(f"WS {mtype}: {json.dumps(data)[:220]}", flush=True)
        try:
            assert c.ws is not None
            await c.ws.send(json.dumps({"MessageType": "KeepAlive"}))
        except Exception as exc:  # noqa: BLE001
            print(f"socket lost: {exc}", flush=True)
            return
        await asyncio.sleep(10)

    print("host window over, leaving group", flush=True)
    await c.post("/SyncPlay/Leave")
    await c.close()


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--base", required=True)
    p.add_argument("--user", required=True)
    args = p.parse_args()
    u, _, pw = args.user.partition(":")
    asyncio.run(main(args.base, u, pw))
