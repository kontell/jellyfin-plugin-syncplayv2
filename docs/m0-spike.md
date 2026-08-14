# M0 spike — results

*2026-08-14. Environment: Jellyfin server built from `release-10.11.z` @ `1fbd873929` ("Bump version to 10.11.11"), run headless from source on this machine (`dev/temp/jellyfin-10.11-spike`, data in `run/`); spike plugin `Jellyfin.Plugin.SyncPlayV2` 10.11.0.90 (this repo) compiled against NuGet `Jellyfin.Controller` **10.11.0**; driver `tools/spike_checks.py` (aiohttp + websockets) as bot user provisioned by the conformance kit's bootstrap. Full battery: **16 PASS, 0 FAIL.***

Every mechanism the feasibility study rated "verified in source" held at runtime, and every mechanism rated "inference — spike required" **worked**. One check produced a new upstream bug discovery (#12). Two checks remain deferred to the real deployment (§ deferred).

## Results

| # | Mechanism | Result |
|---|---|---|
| 1 | **`ISyncPlayManager` DI shadow** | **CONFIRMED.** Plugin-registered manager resolved everywhere (`hosted-service` evidence: `ISyncPlayManager => SpikeSyncPlayManager`); core manager never constructed. |
| 2 | **Stock `/SyncPlay/*` routes drive the plugin manager** | **CONFIRMED.** `NewGroup`, `JoinGroup`, `ListGroups`, `HandleRequest(Pause)` all logged from stock-controller calls. |
| 3 | **Auth policy consults the plugin manager** | **CONFIRMED.** `SyncPlayIsInGroup`-guarded bodyless `POST /SyncPlay/Pause` → 204, with `IsUserActive(...) => True` evidence — the policy handler resolved our manager. |
| 4 | **Bodyless POSTs (stock-web parity)** | **CONFIRMED.** No 415/400 on body-free `Pause`. |
| 5 | **Plugin wire DTOs under stock envelope types** | **CONFIRMED.** Via `SessionInfo.SessionControllers → ISessionController.SendMessage<T>`: `GroupJoined` with `StateVersion`/`ProtocolVersion: 2`/`Members[]`; `SyncPlayGroupUpdate` with `Type: "StateSnapshot"` (a value the core enum does not have); `SyncPlayCommand` with `StateVersion`. All received intact by a real client on `/socket`. |
| 6 | **`SessionControllerConnected`** | **CONFIRMED.** Fired on first WS connect and again on reconnect — the reconnect-snapshot hook works. |
| 7 | **Plugin-registered `IHostedService`** | **CONFIRMED.** Started at host start; also forces the manager into existence before any session event. |
| 8 | **Body-transparent negotiation** (`Configure<MvcOptions>` resource filter) | **CONFIRMED.** The filter read `ProtocolVersion=2` from the raw bodies of stock `POST /SyncPlay/New` **and** `/SyncPlay/Join` before model binding dropped the field. **Spec clients negotiate v2 with zero route changes.** |
| 9 | **Route shadowing, `Order = -1`** | **CONFIRMED.** Plugin's duplicate `GET /SyncPlay/List` won routing (marker header served); stock routes not shadowed were untouched. |
| 10 | **OpenAPI with a shadowed route** | **Resolver REQUIRED and sufficient.** Without it `/api-docs/openapi.json` → **500**; with the plugin's `PostConfigure<SwaggerGenOptions>` conflict resolver → **200**. (Env gate `SYNCPLAYV2_SPIKE_NO_SWAGGER_RESOLVER` reproduces both.) |
| 11 | **`IWebSocketManager` path router** | **CONFIRMED.** Plugin router claims `GET /SyncPlayV2Spike/TimeSync` (header-authenticated via `IAuthService`, NTP echo, ~4 ms RTT loopback) and delegates everything else to the core `WebSocketManager` — obtained by resolving `IEnumerable<IWebSocketManager>` and skipping itself, **no `Emby.Server.Implementations` compile reference needed**. `/socket` behaved identically through the router. |
| 12 | **Unknown `MessageType` on `/socket`** | **TEARDOWN, root-caused — new upstream bug.** See below. |
| 13 | **ABI pinning** | **LESSON.** A plugin compiled against `Jellyfin.Controller` 10.11.11 is *refused* by a 10.11.7 server ("references an incompatible version of one of the shared libraries") — .NET binds assembly references upward, never downward, and `targetAbi` does not protect against it. **Compile against the oldest supported patch** (now pinned `[10.11.0]`). Note: kofinsyncqueue's `10.*-*` float has the same latent issue for servers older than build-day-latest. |

## The new upstream bug (#12)

Sending any unrecognized `MessageType` (or malformed JSON) on `/socket` kills the connection — but **not** where the feasibility study guessed. The drop-and-log path *exists*; its own logging line crashes:

```csharp
// Emby.Server.Implementations/HttpServer/WebSocketConnection.cs (ProcessInternal)
catch (JsonException ex)
{
    reader.AdvanceTo(buffer.End);   // returns the buffer to the pipe…
    _logger.LogError(ex, "Error processing web socket message: {Data}",
        Encoding.UTF8.GetString(buffer));   // …then reads the invalidated sequence
    return;
}
```

Observed at runtime as `System.ArgumentOutOfRangeException` from `EncodingExtensions.GetString` escaping the catch, propagating out of the receive loop, and ending the connection (server log: `WS "127.0.0.1" WebSocketRequestHandler error`). **Present verbatim on `release-10.11.z` (10.11.11) and current master (12.0).** The fix is to capture the string (or slice) *before* `AdvanceTo`. Consequences:

- The conformance kit's recorded "stock tears down on unknown types" and the study's code-read "drop-and-log" were both right — the catch exists and then crashes.
- v2 clients must never send `TimeSync` (or anything unknown) to `/socket` on an unfixed server; the `Hello` capability probe + transport descriptor design is confirmed as necessary, and the §5.2 member-scoped advertisement rule stays as defense-in-depth.
- The fork's `fix/websocket-serialize-sends-and-abort-lost-sockets` branch does **not** fix this (it widens the `ReceiveAsync` catch, not this one) — fold the one-liner into that branch before upstreaming. Issue note: `../notes/jellyfin-issue-websocket-error-log-kills-socket.md`.

## Design consequences for M1

1. **Negotiation:** the resource-filter body sniffer is promoted to the *primary* mechanism — spec-conformant clients (and the conformance kit) send `ProtocolVersion` in `Join`/`New` bodies and it reaches the plugin unchanged. `POST /SyncPlay/Hello` remains as the capability **probe** (server v2 presence, transport descriptor for time sync) — both proven.
2. **Route shadowing is available** where a stock route's *response* must change shape (e.g. `List` with `Members[]`) — ship it with the Swagger resolver, which is mandatory.
3. **Dedicated WS time-sync path is the design** for spec §3-v2: the router mechanism is proven and cheap; main-socket `TimeSync` stays impossible (and, pre-fix, dangerous).
4. **Pin plugin package refs to `[10.11.0]`**; keep `targetAbi: 10.11.0.0`.

## Deployment pass (192.168.1.167, packaged 10.11.11 "minipie") — all complete

Same day, the zip built by `tools/package.sh` was installed on the production server with `tools/install.sh` and the full battery re-run remotely: **16 PASS, 0 FAIL — identical to local**, including the packaged-binary flavor of check #12 (socket teardown confirmed on the release build) and an organically-captured `SessionEnded` → `SessionControllerConnected` cycle in the evidence log.

**Browser-level stock-web confirmation — done, with a bonus.** A human participant joined the plugin-hosted group `spike-web` from the real web client — via **`https://jelly.konell.xyz`, i.e. through the Caddy reverse proxy over WSS** — exercising: the group list served by the plugin's Order=-1 shadow route, `Join` through the stock route into the plugin manager, and the plugin-wire `GroupJoined` applied by stock web (visible join, no errors). Then, pushed at the live web session from its own console:

- `SendGroupUpdate?type=StateSnapshot` (v2-only leak test) → **exactly one** console error, `SyncPlay processGroupUpdate: command StateSnapshot not recognised.` (the 10.11 wording, matching the §6.1 audit) — nothing else affected.
- `SendCommand?command=Stop` (SyncPlayCommand carrying the extra `StateVersion` field) → applied **with zero console output** — additive fields fully invisible.

`UserJoined`/`UserLeft` broadcasts flowed both ways throughout (the participant's two web sessions produced two join/leave pairs; the spike stub's non-idempotent re-join is a known simplification — the real engine re-attaches per spec §4).

Not tested anywhere (out of scope while targeting 10.11): v12-web `ForceKeepAlive` cadence.

## Reproducing

```bash
# server (from dev/temp/jellyfin-10.11-spike):
~/.dotnet/dotnet Jellyfin.Server/bin/Release/net9.0/jellyfin.dll --nowebclient \
  --datadir <repo>/run/data --cachedir <repo>/run/cache --logdir <repo>/run/log
# plugin:
tools/install-local.sh <repo>/run/data/plugins   # then restart the server
# provision + run:
python -m syncplay_kit bootstrap --base http://127.0.0.1:8096 --media-dir <dev>/temp/spike-media
tools/spike_checks.py --base http://127.0.0.1:8096 --user syncbot-a:sp-test
```
