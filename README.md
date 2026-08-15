# Jellyfin SyncPlay v2 plugin

SyncPlay protocol v2 served from a server plugin: versioned state, on-demand snapshots, position beacons, ping-scaled position tolerances, buffering grace, bounded group-waits, and a 90-second disconnect grace with per-version resync - serving stock v1 clients (jellyfin-web, unmodified) and v2 clients from one group registry on an unpatched Jellyfin 10.11 server.

While the plugin is enabled it replaces the built-in SyncPlay (it shadows the core `ISyncPlayManager`); disable it and stock SyncPlay returns. Even plain v1 clients gain the robustness fixes: a member that stalls no longer freezes the group forever, short rebuffers no longer pause everyone, and a dropped connection no longer means an instant kick.

The protocol is specified in the [syncplay-conformance](https://github.com/kontell/syncplay-conformance) repo.

## Install

Add the Kontell plugin repository, then install from the catalog - Jellyfin unpacks the plugin into the right place (with the right ownership) itself:

1.  Dashboard -> Plugins -> Manange Repositoires -> New Repository: https://repository.kontell.workers.dev/jellyfin/manifest.json
2.  Dashboard -> Plugins -> Install SyncPlay v2, then restart the server (more reliable to do this from systemd rather than the dashboard).

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
