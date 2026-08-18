# Jellyfin SyncPlay v2 plugin

SyncPlay protocol v2 served from a server plugin: versioned state, on-demand snapshots, position beacons, ping-scaled position tolerances, buffering grace, bounded group-waits, and a 90-second disconnect grace with per-version resync - serving stock v1 clients (jellyfin-web, unmodified) and v2 clients from one group registry on an unpatched Jellyfin 10.11 server.

While the plugin is enabled it replaces the built-in SyncPlay (it shadows the core `ISyncPlayManager`); disable it and stock SyncPlay returns. Even plain v1 clients gain the robustness fixes: a member that stalls no longer freezes the group forever, short rebuffers no longer pause everyone, and a dropped connection no longer means an instant kick.

The protocol is specified in [`docs/SYNCPLAY.md`](https://github.com/kontell/syncplay-conformance/blob/master/docs/SYNCPLAY.md) in the [syncplay-conformance](https://github.com/kontell/syncplay-conformance) repo. This plugin is the spec's "plugin binding": `POST /SyncPlay/Hello` (§2.1), the dedicated time-sync socket (§3.1), and hot join (§7.1), which no other server implements.

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

One source tree ships to several Jellyfin server lines. Jellyfin serves a single
`manifest.json` in which every entry carries its own `targetAbi` and the server
picks the highest one it can run, so supporting 10.11 and 12 is a **build
matrix, not a branch pair** — the only difference between them is the target
framework, the `Jellyfin.Controller` pin and the version prefix. The supported
rows live in one place, the `abis` job of
[`release.yml`](.github/workflows/release.yml).

Tag `v<PluginVersion>` — the *primary* row's version, the one `build.yaml`
documents — and CI tests, packages and drafts a single GitHub release carrying
one zip per row: `syncplay-v2_10.11.0.N.zip`, `syncplay-v2_12.0.0.N.zip`. The
build number `N` is shared, because a v12 server that can see both must rank the
v12 zip higher; Jellyfin treats `targetAbi` as a floor and will otherwise offer
the older build ([jellyfin#11331](https://github.com/jellyfin/jellyfin/issues/11331),
closed *Not A Bug*).

A row built against a Jellyfin prerelease is marked `preview` and may fail
without holding up the rest; the release step says which rows made it in.

Publishing the draft is a human act, after which
[repository.kontell](https://github.com/kontell/repository.kontell) picks it up
(dispatch or its scheduled reconcile) and serves it in `jellyfin/manifest.json`.

To build one row locally:

```sh
tools/package.sh dist                      # the primary row
ABI_BASE=12.0.0 TARGET_ABI=12.0.0.0 \
  FRAMEWORK=net10.0 JELLYFIN_VERSION=12.0.0-rc5 tools/package.sh dist
```

## License

GPL-3.0 — the engine is derived from Jellyfin server sources
([kontell/jellyfin](https://github.com/kontell/jellyfin), GPL).
