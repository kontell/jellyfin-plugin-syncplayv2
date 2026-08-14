# M1 — the engine port

*2026-08-14, same day as M0. The real protocol-v2 engine, vendored from `integration/syncplay-phase1` @ `336ac05456`, now runs inside the plugin on stock 10.11.11. **Full conformance: 14/14 kit scenarios (fast 12/12 + slow 2/2) — the same score the fork's integrated build recorded**, plus the plugin's own 19-check mixed-group battery.*

## What was built

~3,300 lines vendored (provenance + deliberate divergences: `Jellyfin.Plugin.SyncPlayV2/Engine/VENDORED.md`) plus ~900 new:

- **Engine**: fork `Group` + `SyncPlayManagerV2` + the five state classes + extended `GroupMember`. All phase-1 behavior: `StateVersion` on every message, snapshots, 5s position beacons (v2 members only), ping-scaled tolerance `clamp(2×ping, 500ms, 2000ms)`, 2s buffering grace, 10s group-wait deadline, 90s disconnect grace with per-version resync (v2 → `StateSnapshot`; v1 → `GroupJoined` + `PlayQueue` + command triple).
- **Wire layer**: the vendored states keep constructing stock typed updates; `Group.SendGroupUpdate`/`SendCommand` translate to plugin wire DTOs (open `Type` string, `StateVersion` stamped) at one choke point and deliver via member `Session` refs through `ISessionController.SendMessage`. Manager error updates stay on the stock send path (StateVersion 0 by design).
- **Negotiation**: the resource-filter body sniffer feeds a device-keyed `ProtocolVersionRegistry` (12h sliding TTL) that the engine reads at member attach — spec clients negotiate on the stock `Join`/`New` bodies unchanged. `POST /SyncPlay/Hello` is the capability probe and returns the time-sync transport descriptor.
- **Member-scoped advertisement** (feasibility §5.2): `GroupInfoDto.ProtocolVersion` is emitted only to members/requesters that negotiated v2 — v1 clients see it null/absent everywhere (GroupJoined and the shadowed List).
- **Routes**: `POST /SyncPlay/Hello`, `POST /SyncPlay/Snapshot` (new, conflict-free); `GET /SyncPlay/List` shadowed at `Order=-1` with the enriched shape (`Members[]` always — a harmless superset for v1, proven in M0; `ProtocolVersion` member-scoped) + the required Swagger conflict resolver.
- **Time sync**: dedicated WebSocket at `/SyncPlay/TimeSync` via the `IWebSocketManager` router (~1ms T1−T0 on loopback).
- **`SocketLiveness`** — the piece M1 added beyond the fork's own code: an `IWebSocketListener` tracking every connection's `LastKeepAliveDate`/`LastActivityDate`. Stock never aborts zombie sockets (their sessions never end), so the listener presumes a device dead after the stock 60s keep-alive timeout — guarding against live sibling sockets on the same device — and drives `MarkSessionDisconnected`/`ReattachSession` on the engine directly. The dead core *session* still lingers (that hygiene needs the upstream reliability fix); *group* behavior matches the integrated build, which is what the kit measures.

## Results

| Suite | Result |
|---|---|
| Conformance fast (12 scenarios) | **12/12** — `v2_negotiation` and `group_info_members` pass with body-transparent negotiation + the shadowed List; `ws_timesync` passes via transport discovery (below) |
| Conformance slow (2 scenarios) | **2/2** — `reconnect_grace`: flagged `IsConnected=false` at +62.9s, zero `UserLeft`, snapshot on the reconnect socket; `grace_expiry`: `UserLeft` at +151.6s (expect ~150 = 60s detection + 90s grace) |
| `tools/m1_checks.py` (19 checks) | **19/19** — mixed v1+v2 group end-to-end: member-scoped `ProtocolVersion`, beacon isolation, snapshot, monotonic `StateVersion`, buffering grace, per-version reconnect, no `UserLeft` on transient reconnects, List shadow, WS time sync, leave cleanup |

First failure and fix worth recording: the slow suite initially failed 0/2 — the kit's zombie (socket open, keep-alives stopped) never triggers `SessionEnded` on stock because core never aborts lost sockets, so the engine's grace machinery never engaged. That was the feasibility study's predicted Tier-1 delta #4, confirmed by test, and closed by `SocketLiveness` (member-level detection at the same 60s the fork achieves via `Abort()`).

## Conformance-kit change (the only one needed)

`../syncplay-conformance` gained transport discovery for the one scenario that is transport-bound, backward-compatible with integrated servers:

- `syncplay_kit/client.py`: `timesync_transport()` (POST `/SyncPlay/Hello` → dedicated WS path or None) and `timesync_ws_dedicated(path)`.
- `scenarios/protocol_v2.py` `ws_timesync`: use the dedicated path when advertised, else the original `/socket` exchange. Against a patched integrated server the behavior is unchanged; against stock v1 the diagnostic failure modes are preserved.

Everything else — negotiation bodies, `member_of()`'s List reads, `/SyncPlay/Snapshot` — passed **unmodified**, as designed.

## What this means operationally

While the plugin is installed, the server serves **real SyncPlay v2**: stock v1 clients (web) get the phase-0 robustness (bounded waits, buffering grace, no instant kick on disconnect) through the stock routes; v2 clients get the full protocol. Disable the plugin → stock v1 SyncPlay returns (groups are in-memory only).

Deliberate M1 scope bounds: constants are the fork's fixed values (no config page yet); dead core sessions linger until the upstream reliability fix (member behavior is correct regardless); the `Order=-1` List shadow is the one stock route replaced.

## Next

- **M3 — kofin v2 adoption** (client-side, transplanting from the jellyfin-kodi reference branch): probe `Hello`, track `StateVersion`, apply snapshots, consume beacons, optionally the dedicated time-sync socket.
- Upstream: file the WS error-path crash (`notes/jellyfin-issue-websocket-error-log-kills-socket.md`) and fold it into `fix/websocket-serialize-sends-and-abort-lost-sockets`.
- Optional hardening: config page for the constants; a dashboard status page.
