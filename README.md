# Jellyfin SyncPlay v2 plugin

[![License: GPL-3.0-only](https://img.shields.io/badge/License-GPL%20v3-blue.svg)](LICENSE "LICENSE")

Drop-in replacement for Jellyfin syncplay, a server plugin that's backwards compatible with existing clients. Stock Jellyfin Web clients and v2 clients such as [Kofin](https://github.com/kontell/plugin.video.kofin) can share a group.

Features:

- Supports stock SyncPlay clients (v1) and protocol v2 clients in the same group.
- Recovers from stalled loads, brief buffering, and dropped connections with configurable timeouts.
- Adds hot joins, time sync, position beacons, and state snapshots for v2 clients.
- Supports external content in groups where every client has that capability.
- Shows group status and a shareable diagnostics report in the Jellyfin dashboard.

## Install

1. In **Dashboard → Plugins → Manage Repositories**, add `https://repository.kontell.workers.dev/jellyfin/manifest.json`.
2. Install **SyncPlay v2** from the plugin catalog and restart Jellyfin.

Disabling the plugin and restarting Jellyfin restores the built-in SyncPlay server.

## Jellyfin Web setting

Turn off **Play next episode automatically** in the playback settings of each Jellyfin Web account used with SyncPlay, then reload the browser. When this setting is on, Jellyfin Web can expand a one-episode group queue to include later episodes and fail to follow group playback. The same issue occurs with stock Jellyfin SyncPlay.

## Testing your server

[syncplay-conformance](https://github.com/kontell/syncplay-conformance) drives your server with fake clients and checks the behaviours SyncPlay depends on — useful for finding out what your server actually supports before blaming a client.

It needs Python 3.11+ and two or three throwaway users who can all access the same movie and create or join SyncPlay groups:

```bash
git clone https://github.com/kontell/syncplay-conformance
cd syncplay-conformance
python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt

python -m syncplay_kit run \
    --base https://your.jellyfin.server \
    --user syncbot-a:pw --user syncbot-b:pw --user syncbot-c:pw \
    --suite fast
```

`--suite fast` takes 2–3 minutes; `--suite all` adds the disconnect/reconnect scenarios (about 5 minutes more). With only two users, scenarios that need a third member print `SKIP`. Executed checks print `PASS` or `FAIL`, and the run exits nonzero if any check fails. `python -m syncplay_kit list` shows the scenarios individually.

What to expect:

- *Stock Jellyfin (protocol v1):* expect failures. Even `group_info_members` requires the per-member status fields added by v2; the robustness checks (`group_wait_deadline`, `buffering_grace_*`) and v2 scenarios also test behaviour stock SyncPlay does not provide.
- *With a SyncPlay v2 build that supports every scenario, including external content:* the full suite should pass.

If SyncPlay only misbehaves through your reverse proxy, `python tools/doctor.py --base https://your.jellyfin.server --user alice:secret` checks the deployment itself — WebSocket upgrade, keep-alives, clock offset — with proxy templates in the kit's `docs/operators.md`.
