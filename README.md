# Jellyfin SyncPlay v2 plugin

SyncPlay protocol v2 served from a server plugin: versioned state, on-demand
snapshots, position beacons, ping-scaled position tolerances, buffering grace,
bounded group-waits, and a 90-second disconnect grace with per-version resync —
serving stock v1 clients (jellyfin-web, unmodified) and v2 clients from **one
group registry** on an unpatched Jellyfin 10.11 server.

While the plugin is enabled it replaces the built-in SyncPlay (it shadows the
core `ISyncPlayManager`); disable it and stock SyncPlay returns. Even plain v1
clients gain the robustness fixes: a member that stalls no longer freezes the
group forever, short rebuffers no longer pause everyone, and a dropped
connection no longer means an instant kick.

The protocol is specified in the `kontell/jellyfin` fork's
[`docs/SYNCPLAY.md`](https://github.com/kontell/jellyfin/blob/docs/syncplay-protocol/docs/SYNCPLAY.md)
(this plugin is its "plugin binding": negotiation also works body-transparently
on the stock `Join`/`New` routes, `POST /SyncPlay/Hello` is the capability
probe, and the WebSocket time-sync exchange lives on a dedicated socket at
`/SyncPlay/TimeSync` because a plugin cannot answer `TimeSync` on `/socket`).
Conformance: the full [syncplay-conformance](https://github.com/kontell/syncplay-conformance)
suite — 14/14 scenarios, the same score as the patched-server build.

## Install

From the [Kontell repository](https://github.com/kontell/repository.kontell)
(Dashboard → Plugins → Repositories), or manually:

```
sudo tools/install.sh syncplay-v2_<version>.zip --restart
```

Requires Jellyfin 10.11.x. The engine logs `[SyncPlayV2] engine active` at
startup — if it logs `DI SHADOW FAILED` instead, the server build changed
service registration and the plugin is inactive (stock SyncPlay still works).

## Layout

- `Jellyfin.Plugin.SyncPlayV2/Engine/` — the protocol engine, vendored from the
  fork's `integration/syncplay-phase1` branch and intended to return upstream;
  provenance and deliberate divergences in [`Engine/VENDORED.md`](Jellyfin.Plugin.SyncPlayV2/Engine/VENDORED.md).
- `Jellyfin.Plugin.SyncPlayV2/Wire/` — plugin wire DTOs, JSON byte-equivalent
  to the patched server's, sent under stock envelope types.
- `docs/` — the [feasibility study](docs/feasibility.md), the
  [M0 spike record](docs/m0-spike.md) and the [M1 engine record](docs/m1-engine.md).
- `tools/` — packaging (`package.sh`), server install (`install.sh`), and the
  integration batteries (`m1_checks.py`, `spike_checks.py`).

## Releasing

Tag `v<AssemblyVersion>` → CI tests, packages and drafts a GitHub release;
publishing the draft is a human act, after which
[repository.kontell](https://github.com/kontell/repository.kontell) picks it up
(dispatch or its scheduled reconcile) and serves it in `jellyfin/manifest.json`.

## License

GPL-3.0 — the engine is derived from Jellyfin server sources
([kontell/jellyfin](https://github.com/kontell/jellyfin), GPL).
