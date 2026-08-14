# SyncPlay v2 as a server plugin — feasibility study

> **Status update 2026-08-14:** the M0 spike ran against a from-source 10.11.11 server **and the packaged production 10.11.11 deployment — 16/16 checks passed on both** ([docs/m0-spike.md](m0-spike.md)), including the browser-level confirmation that stock jellyfin-web (through the Caddy reverse proxy) applies plugin-wire messages, ignores additive fields, and survives a v2-only leak with a single console error. Every "verified in source" claim held at runtime and every "inference — spike required" mechanism worked, including body-transparent negotiation (§5.2a) and route shadowing with a plugin-supplied Swagger resolver (§4.7). The unknown-WS-type discrepancy (§4.5/§8.6) is resolved: **teardown**, root-caused to a new upstream bug (use-after-advance in the error-logging path, present through master). **Same day, M1 shipped the real engine** ([docs/m1-engine.md](m1-engine.md)): the phase-1 code vendored into the plugin reaches **full conformance — 14/14 kit scenarios, the score the integrated fork build recorded** — on stock 10.11.11, with one backward-compatible kit change (time-sync transport discovery). The predicted Tier-1 zombie-socket delta surfaced exactly as forecast (slow suite 0/2 initially) and was closed member-level by a plugin `IWebSocketListener` liveness tracker. This study's architecture is no longer a proposal; it is the running design. Sections below are annotated where the spikes upgraded or corrected them.

*2026-08-14. Sources examined: `kontell/jellyfin` fork (`../../ref/jellyfin`, branches `integration/syncplay-phase1` @ `336ac05456`, base `7f3e27c007`, plus `origin/release-10.11.z` and master @ `e6338bdd8f`), the protocol spec (`docs/syncplay-protocol:docs/SYNCPLAY.md`), the design report (`../../ref/syncplay-report.md`), `../syncplay-conformance`, `../plugin.video.kofin`, `../../ref/jellyfin-web`, and the existing `../jellyfin-plugin-kofinsyncqueue` plugin. All server facts below were verified against `release-10.11.z` (the deployment target) and cross-checked on master.*

---

## 1. Verdict

**Feasible, with high confidence, for ~90% of the phase-1 feature set through fully supported plugin mechanisms — and a credible route to 100%.** The single architecture that satisfies every constraint is a plugin that **shadows the core `ISyncPlayManager` singleton** with a vendored, v2-capable engine. Because the stock `/SyncPlay/*` controller and the SyncPlay authorization policy both consume that interface from DI, an unmodified jellyfin-web client keeps working through the stock routes, v1 and v2 members share one group registry, and the whole v1 surface is served by the new engine *without touching a single core route*.

Three things cannot be reproduced bit-for-bit from a plugin, all in the WebSocket transport layer:

1. **`TimeSync` on the shared `/socket`** — inbound message types are parsed into a closed enum inside core `WebSocketConnection` before any listener runs; unknown types never reach plugin code. Replacement: a dedicated time-sync WebSocket path served by a thin `IWebSocketManager` wrapper (mechanism verified; needs a spike), with HTTP `/GetUtcTime` as the always-available fallback — which is what kofin uses today anyway.
2. **The per-connection send lock** (fix 0.4) — mitigable (the plugin serializes its own sends), not fully fixable from a plugin.
3. **Aborting zombie sockets** (fix 0.1) — approximated by a plugin-side staleness sweep; the user-visible failure (groups stalled forever) is eliminated regardless, because the group-wait deadline and disconnect grace live in the plugin's engine.

All three are exactly the content of the fork's `fix/websocket-serialize-sends-and-abort-lost-sockets` branch — a small, SyncPlay-independent reliability bugfix (it resolves a literal `// TODO` in core) that is a strong candidate to submit upstream *now* as an ordinary PR. **Plugin + that one small upstream PR ≈ full phase-1 parity.**

Client and test impact: jellyfin-web needs **no changes** — verified against both its WebSocket dispatch generations (v12 SDK and 10.11 legacy): unknown message types are dropped without side effects and nothing validates payload shapes (§6.1). A **complete v2 client already exists** — the `kontell/jellyfin-kodi` `feat/syncplay-protocol-v2` branch (abandoned upstream, used here purely as kofin's reference implementation and as evidence for the §5.2 advertisement rule, §6.3). kofin itself needs the v2 adoption work it already planned ("adopt by capability probe when it lands, never raise a floor" — `docs/phase4-implementation-plan.md:13`) plus nothing extra; the probe becomes the plugin's hello endpoint. The conformance kit needs a handful of mechanical changes (negotiation call, one route re-point, a transport flag for the time-sync scenario) — all funneled through 2–3 helper functions the kit already has.

## 2. Constraints this study answers to

1. **Existing clients keep working unmodified.** In practice jellyfin-web, speaking protocol v1, against a server with the plugin installed.
2. **One group must be able to contain v1 and v2 members simultaneously** — which rules out any "parallel SyncPlay" design with its own group registry next to the core one. The plugin must *own* the only registry and serve the v1 surface too.
3. kofin is the primary v2 client; modifying it is acceptable (and required regardless — it is a pure v1 client today).
4. The conformance kit should keep working, with modifications acceptable.
5. The work should remain upstreamable once proven.

## 3. What "SyncPlay v2" actually is

`integration/syncplay-phase1` = three commits, 27 files, +1,294/−37 lines:

| Commit | Content |
|---|---|
| `79ab593479` | WS reliability: send lock in `WebSocketConnection`, `Abort()` on `IWebSocketConnection`, abort lost sockets in `SessionWebSocketListener`, log dropped messages |
| `23ad887394` | Phase 0 behavior: group-wait deadline (10s), buffering grace (2s), disconnect grace + reconnect snapshot (90s), member status in `GroupInfoDto` |
| `336ac05456` | Protocol v2: `ProtocolVersion` negotiation, `StateVersion` on every message, `StateSnapshot`, `PositionBeacon` (5s), WS `TimeSync`, ping-scaled tolerance `clamp(2×ping, 500ms, 2000ms)` |

By implementation zone — this is the decisive categorization:

| Zone | Files (added lines) | Plugin-reachable? |
|---|---|---|
| **A. SyncPlay engine** | `Group.cs` (+400), `SyncPlayManager.cs` (+377), `WaitingGroupState.cs` (1-line change) | **Yes** — vendored into the plugin (§5.1) |
| **B. New DTOs / interface additions** | `GroupSnapshotDto`, `PositionBeaconDto`, `GroupMemberStatusDto`, `TimeSyncMessageData`, fields on `GroupInfoDto`/`GroupUpdate`/`SendCommand`, enum values on `GroupUpdateType`/`SessionMessageType` | **Yes** — plugin defines its own wire DTOs with identical JSON shape (§4.4); the closed core enums are bypassed, not extended |
| **C. API surface** | `ProtocolVersion` field on `New`/`Join` bodies, new `POST /SyncPlay/Snapshot` | **Mostly** — new sub-routes under `/SyncPlay/*` are conflict-free; the two body fields need a negotiation endpoint or a route-shadowing spike (§5.3) |
| **D. WS transport** | `WebSocketConnection` (+81), `SessionWebSocketListener` (+18), `WebSocketController` (+1) | **Not directly** — reachable only via an `IWebSocketManager` swap (§5.4/§5.5) or upstreaming (§9) |

The protocol was *designed* for coexistence, which is what makes the plugin viable at all: version is a property of the **member**, not the group; v2-only messages (`StateSnapshot`, `PositionBeacon`) are only ever sent to v2 members; everything else is additive fields that v1 clients ignore (spec §2).

## 4. Load-bearing facts about Jellyfin's plugin surface

Everything in this section was verified by reading `release-10.11.z` (production target) and confirmed unchanged on master (12.0.0-era).

### 4.1 A plugin can shadow `ISyncPlayManager` — verified

- Core registers services first, then plugin registrators run: `ApplicationHost.cs:460` (`RegisterServices(serviceCollection)`) followed by `:462` (`_pluginManager.RegisterServices(serviceCollection)`).
- `ISyncPlayManager` is a plain `AddSingleton` (`ApplicationHost.cs:560`); **the entire server contains zero `TryAdd*` registrations**, so a later plugin `AddSingleton<ISyncPlayManager, SyncPlayManagerV2>()` wins under MS DI last-registration-wins semantics.
- Nothing resolves `IEnumerable<ISyncPlayManager>`. The only consumers are `SyncPlayController` and `SyncPlayAccessHandler` (the `SyncPlayIsInGroup`/access policy), both injecting the single interface — so REST routing *and* authorization decisions automatically follow the plugin's manager. No split-brain.
- The shadowed core `SyncPlayManager` is never constructed, so its `SessionEnded` subscription never happens. There is no second registry, no competing event handler, nothing to disable.

**Caveat:** this works as an *emergent property* of registration order, not a documented contract (no `TryAdd`, no docs either way). Mitigation in §8.1.

### 4.2 The v1 surface comes for free

With the manager shadowed, all 17 stock `/SyncPlay/*` routes and `GET /GetUtcTime` continue to exist and now drive the plugin's engine. This is what satisfies constraints 1 and 2 simultaneously: jellyfin-web joins through `POST /SyncPlay/Join` exactly as before and becomes a v1 member of a plugin-managed group; kofin negotiates v2 and joins the *same* group.

### 4.3 The events the engine needs exist on 10.11.z

- `ISessionManager.SessionEnded` (`ISessionManager.cs:47`) — drives the disconnect-grace window.
- `ISessionManager.SessionControllerConnected` (`ISessionManager.cs:54`, raised from `SessionWebSocketListener.EnsureController` at `:129`) — drives reconnect detection + snapshot push. This is the exact event the fork's own `SyncPlayManager` uses; it predates the fork.
- Plugins can register `IHostedService` and run timers (the kofinsyncqueue plugin already registers hosted services); the engine's 1s sweep is a plain `System.Threading.Timer`.

### 4.4 Outbound wire compatibility is exact — verified

`SessionInfo.SessionControllers` is public (`SessionInfo.cs:182`) and `ISessionController.SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken)` (`ISessionController.cs:33`) has an **unconstrained generic payload**. `WebSocketController.SendMessage` wraps it as `new OutboundWebSocketMessage<T> { Data = data, MessageType = name, MessageId = messageId }` — the identical envelope the core uses, serialized with the same `JsonDefaults.Options`.

Consequence: the plugin sends `SessionMessageType.SyncPlayGroupUpdate` / `SyncPlayCommand` (both exist in the stock enum) with **its own payload classes** — a `GroupUpdate`-shaped DTO whose `Type` is a *string* property (so it can say `"StateSnapshot"` and `"PositionBeacon"` even though the core `GroupUpdateType` enum lacks them) and which carries `StateVersion`, `Members`, `ProtocolVersion`, etc. The resulting JSON is **byte-equivalent** to what the patched server emits. Clients cannot tell the difference; the conformance kit's wire-shape assertions hold.

### 4.5 Inbound WS: new message types cannot ride `/socket` — verified

`WebSocketConnection.ProcessInternal` deserializes the envelope into `InboundWebSocketMessage<object>` whose `MessageType` is the closed `SessionMessageType` enum, **before** any `IWebSocketListener` runs. An unknown string (`"TimeSync"` on a stock enum) throws `JsonException`.

**M0 resolved the drop-vs-teardown question: teardown, for an unexpected reason.** The catch that should drop-and-log the message calls `reader.AdvanceTo(buffer.End)` and *then* `Encoding.UTF8.GetString(buffer)` on the invalidated sequence — `ArgumentOutOfRangeException` escapes the catch and kills the connection. Present on 10.11.11 and current master; one-line fix; see [m0-spike.md](m0-spike.md) and `../../notes/jellyfin-issue-websocket-error-log-kills-socket.md`. The conclusion is unchanged and now stronger: **a plugin listener never sees a `TimeSync` frame on `/socket`, and sending one to an unfixed server kills the shared socket** — clients must capability-probe first.

Plugin-registered `IWebSocketListener`s *do* otherwise work (core registers its four listeners with plain `AddSingleton<IWebSocketListener, …>`; the collection is open), and listener exceptions are caught per-message — but they only ever see known enum values.

### 4.6 All WebSocket upgrades are captured centrally — and that's an opportunity

`WebSocketHandlerMiddleware` has **no path filter**: any WS upgrade on any path is handed to `IWebSocketManager` before MVC endpoints run (`Startup.cs:216` vs `:225`). So a plugin controller action can never accept an upgrade itself. But `IWebSocketManager` is a plain `AddSingleton` (`ApplicationHost.cs:536`) with a one-method interface and a single per-request consumer — **a plugin can shadow it** with a thin router:

- path = `/SyncPlay/TimeSync` (or similar) → plugin's ~80-line NTP echo loop (auth via the same `Authorization` header clients already send on WS handshakes);
- anything else → delegate to a normally-constructed core `WebSocketManager` (public class; the kofinsyncqueue build already compiles against server implementation assemblies, so referencing `Emby.Server.Implementations` follows established practice).

This yields spec-quality WS time sync on a **dedicated** socket — arguably better than the fork's shared-socket variant (no head-of-line blocking behind beacons/commands) — without reimplementing any core socket handling.

### 4.7 Routes: new sub-routes free; shadowing existing ones is a spike

- A plugin controller (`ControllerBase` + `[Route("SyncPlay")]`; `BaseJellyfinApiController` is not in a published package, but the auth `Policies` constants are, in `MediaBrowser.Common.Api`) can freely add **new** sub-routes: `POST /SyncPlay/Snapshot` conflicts with nothing, ditto a hello/info endpoint and an enriched list endpoint.
- Re-claiming an **existing** route (`Join`, `New`, `List`) needs `[Route(..., Order = -1)]` precedence plus a Swagger conflict resolver. **M0 confirmed both**: the plugin's `Order = -1` duplicate of `GET /SyncPlay/List` won routing with stock routes unaffected, and the resolver is *required and sufficient* — `/api-docs/openapi.json` 500s without it, 200s with the plugin's `PostConfigure<SwaggerGenOptions>` registration. Shadowing is now a proven tool for routes whose *response shape* must change (e.g. `List` with `Members[]`).

### 4.8 Versioning reality

Master has been renumbered **12.0.0** (`#16758`, 2026-05-06) on net10.0; `release-10.11.z` is net9.0. The fork's branches sit just before the renumber. The plugin should target **10.11 ABI first** (matches the production server, the conformance kit's verified baseline, and kofinsyncqueue's `targetAbi: 10.11.0.0` convention), with a 12.x build later. Note the fork's `SyncPlayManager` uses .NET 9's `System.Threading.Lock` — fine on net9.0.

## 5. Proposed architecture

### 5.1 Tier 1 — the baseline (every mechanism verified)

```
jellyfin-plugin-syncplayv2
├── PluginServiceRegistrator      AddSingleton<ISyncPlayManager, SyncPlayManagerV2>()
├── Engine/ (vendored + extended)
│   ├── GroupV2.cs                ← fork's Group.cs   (~1,080 lines incl. v2 additions)
│   ├── SyncPlayManagerV2.cs      ← fork's SyncPlayManager.cs (~800; sweep timer,
│   │                               deferred buffering, grace windows, beacons)
│   ├── GroupStates/              ← vendored 5 state classes (~1,400) so transitions
│   │                               stay in plugin types + the 1-line adaptive-tolerance
│   │                               change lands (states hard-construct each other, so
│   │                               partial vendoring doesn't work)
│   └── GroupMemberV2.cs          IsConnected, DisconnectedSince, ProtocolVersion, …
├── Wire/                         plugin-defined DTOs, JSON-identical to the fork:
│   │                             GroupUpdate-with-StateVersion (Type as string),
│   │                             SendCommand-with-StateVersion, GroupInfo/Snapshot/
│   │                             Beacon/MemberStatus DTOs
│   └── Sender.cs                 per-session send via SessionInfo.SessionControllers →
│                                 ISessionController.SendMessage<T>; serializes the
│                                 plugin's own sends per session (send-lock mitigation)
├── Api/SyncPlayV2Controller.cs   [Route("SyncPlay")]: POST Hello, POST Snapshot,
│                                 GET ListDetailed — all new, conflict-free sub-routes,
│                                 guarded by the stock SyncPlay policies
└── (reused, NOT vendored)        PlayQueueManager (550 lines), all request/enum types,
                                  IGroupStateContext + a plugin extension interface
```

Behavioral parity in Tier 1: group-wait deadline, buffering grace, disconnect grace + reconnect snapshot (via `SessionEnded`/`SessionControllerConnected`), state versioning, snapshots, position beacons, ping-scaled tolerance, v1/v2 mixed groups — **all identical to the fork**, because the code *is* the fork's, relocated.

Two Tier-1 deviations from the integrated build:

- **Time sync** stays HTTP (`/GetUtcTime`, untouched core) until Tier 1.5. kofin uses HTTP today; the HTTP-bias argument from the design report (§R4) is browser-centric — Kodi's add-on requests don't share a connection with media segments.
- **Zombie sockets** aren't aborted; instead the engine's sweep marks members disconnected when their reports/pings go stale (v2 clients ping ≤60s by spec), which bounds every stall via the group-wait deadline. Session-table hygiene stays core's problem until §9's upstream fix.

### 5.2 Version negotiation without touching stock routes

The stock `Join`/`New` DTOs silently drop unknown JSON fields, so `ProtocolVersion` in those bodies never reaches the manager through the stock controller. Baseline design:

- **`POST /SyncPlay/Hello` `{ProtocolVersion: 2}`** → registers the *session* as v2 in the plugin and returns the server's capabilities (`ProtocolVersion`, plugin version, time-sync transport descriptor, constants). `404` ⇒ stock server ⇒ v1.
- One endpoint serves as **capability probe + negotiation** in a single round trip — the exact "capability probe" kofin's plan already commits to, and the same pattern as kofinsyncqueue's `GET Kofin/SyncQueue/Info`.
- Clients still send `ProtocolVersion` in `Join`/`New` bodies per spec (harmless to stock; self-documenting), but the plugin takes the session-scoped registration as authoritative.
- **Rule: `GroupInfoDto.ProtocolVersion` must be member-scoped, not the fork's constant `2`.** The fork advertises the *server's* version to everyone; under a body-blind transport that misnegotiates: a spec client (jellyfin-kodi v2, §6.3) sends `ProtocolVersion: 2` in `Join` (dropped by the stock DTO ⇒ member registered v1), then sees server `ProtocolVersion: 2` in `GroupJoined` and concludes v2 is active — and starts sending WS `TimeSync` at a socket that cannot answer. Advertising v2 only to members whose v2 request the plugin actually received makes every unmodified spec client degrade *cleanly* to v1. (Spec appendix: under the plugin binding, the server states v2 only where it heard the request.)
- The spec gains a short "plugin binding" appendix noting the alternative negotiation channel and the advertisement rule.
- *Transparency mechanisms — **both confirmed by M0**, and (a) is promoted to the primary negotiation channel:* (a) a **global resource filter** registered via `Configure<MvcOptions>` from the plugin registrator — on `/SyncPlay/Join|New` it buffers the request and reads `ProtocolVersion` from the raw body before model binding drops it; proven end-to-end on both routes. **Spec clients and the conformance kit negotiate v2 with zero changes.** `Hello` remains as the capability *probe* (server presence + time-sync transport descriptor). (b) **Route shadowing** with `Order=-1` plus the (required, working) Swagger conflict resolver — reserved for response-shape changes like `List`.

### 5.3 Tier 1.5 — dedicated WS time sync (`IWebSocketManager` router, §4.6)

Adds the spec §3 v2 time-sync quality: NTP exchange on a WebSocket, `T1` stamped in the plugin's own receive loop, doubles as a liveness signal for the plugin (a live time-sync socket = connected member, sharpening the staleness sweep). Client cost: one extra lightweight socket for v2 clients that opt in; the `Hello` response advertises the path. kofin can adopt it later or stay on HTTP — both are spec-conformant.

### 5.4 Tier 2 — full transport swap (probably unnecessary)

Shadowing `IWebSocketManager` *and* shipping a full replacement `IWebSocketConnection` implementation would add: main-socket `TimeSync`, the send lock under core traffic too, and true zombie aborts — 100% parity. It means owning the receive loop for **all** WS traffic of every client. Feasible (all interfaces public), but the risk/benefit is poor next to §9's alternative: upstream the small reliability fix and keep the plugin in Tiers 1/1.5.

## 6. Compatibility analysis

### 6.1 jellyfin-web (v1, unmodified) — verified safe, with five implementation rules

Audited directly against the local `jellyfin-web` checkout — both master (v12.0-rc2, SDK WebSocket) and `origin/release-10.11.z` (legacy apiclient socket, the version matching the production server). The SyncPlay core logic is near-identical between them (4 files, 25 changed lines).

**Additive v2 traffic is safe.** Unknown WS `MessageType`: v12 looks the type up in a handler map and no-ops on miss; 10.11 fires it at an event bus with no listeners. Unknown `SyncPlayGroupUpdate.Type` (if a v2-only update ever leaked to a v1 member): a single `console.error` in `Manager.js:245-248`, no throw, no state change. Extra fields (`StateVersion`, `Members`, `ProtocolVersion`): invisible — a repo-wide search found **no** code enumerating payload keys, and the SDK's TypeScript DTOs are compile-time only. The web client reads exactly `When`/`EmittedAt`/`PositionTicks`/`Command`/`PlaylistItemId` from commands and `GroupId`/`GroupName`/`Participants`/`LastUpdatedAt` from group info.

**Rules the plugin must honor for v1 web compatibility** (each traced to source):

1. **Always populate `Participants`** in every `GroupInfoDto` — the legacy group menu calls `.Participants.join(', ')` unguarded (`groupSelectionMenu.js:58,162`); `null` breaks the group list.
2. **Keep `GroupInfoDto.LastUpdatedAt ≤ EmittedAt` of every subsequent command**, from the same clock. The client stores `LastUpdatedAt` at join and silently drops any command whose `EmittedAt` predates it (`Manager.js:269-272`) — get this wrong and v1 members "join but never play". The vendored engine inherits the stock stamping (both from `DateTime.UtcNow`), so this is a don't-break-it invariant for any *new* send path.
3. **Never send `UserJoined`/`UserLeft` to a session outside the group's member set** — `UserJoined` dereferences `groupInfo.Participants` unguarded (`Manager.js:198`) and a non-member's `groupInfo` may be `null`. The fork's `FilterSessions` already excludes disconnected members; the grace-window logic must not leak trailing membership deltas.
4. **v1 web clients never report ping.** The `POST /SyncPlay/Ping` call is dead code in stock web — it is gated on `this.syncEnabled`, a property that has never existed on `Manager` (`Manager.js:64`; identical on both branches; this is design-report finding R5). Web members therefore always carry the 500ms default → 1000ms tolerance under the adaptive formula — same as under the patched server, so no plugin delta, but v2 timing quality genuinely benefits only clients that ping (kofin does).
5. **Don't resend byte-identical commands** to v1 members: the duplicate-detection path treats an exact `When`+`PositionTicks`+`Command`+`PlaylistItemId` match as a state-correction and (for `Seek`) injects a ±50ms random offset (`PlaybackCore.js:174-240`). The fork's resync builds fresh commands (new `When`/`EmittedAt`), which is the pattern to keep.

**Semantic changes v1 web members experience — all acceptable:** a 90s-late `UserLeft` toast (cosmetic; nothing waits on `UserLeft`); buffering held 2s server-side stacks under the client's own 3s debounce (nothing on the client awaits a `Pause` in response to its `Buffering` report — fire-and-forget with no timers); the group-wait deadline unpausing without a stalled member is fine (a still-buffering member schedules the unpause and starts late; drift correction is off by default in stock web). Ghost participants during the grace window are visible only on v12 (its toolbar menu polls `/SyncPlay/List` every 60s and re-renders on deltas); 10.11's menu snapshots participants at join and shows only the group name.

**Two stock-web observations worth knowing (not plugin-caused):** on socket drop, web never rejoins or re-requests state — membership is assumed to persist, and REST errors are fire-and-forget with no `.catch()`, so a v1 client only learns it lost membership via a `NotInGroup` update over the socket (the plugin's reconnect-snapshot push is exactly the medicine for this). And the v12 SDK's `ForceKeepAlive` handling appears unit-buggy (`data.Data / 2` without `×1000`, one-shot `setTimeout`) — verify empirically in M0, since flappy v12 sockets would exercise the 90s disconnect grace constantly.

### 6.2 kofin

Today kofin is a **pure v1 client**: 17 hard-coded `/SyncPlay/*` literals + `/GetUtcTime` in one contiguous block (`lib/kofin/core/api.py:517-613`), one shared `/socket` WS, HTTP NTP sampling (min-RTT-of-8, 30s cadence), ping reporting, and zero v2 fields anywhere. Unknown WS message types are debug-logged and dropped (`service/main.py:830`); unknown `SyncPlayGroupUpdate` types likewise (`syncplay/manager.py:479`); `GroupJoined` already tolerates `Members` *or* `Participants` (`manager.py:523-525`). So the plugin changes nothing for kofin-as-v1.

kofin's v2 adoption work — required identically for a patched server or a plugin — plus the one plugin-specific piece:

| Change | Plugin-specific? | Size |
|---|---|---|
| Capability probe on connect → `POST /SyncPlay/Hello` | The endpoint is, the probe isn't (its own plan requires one) | small |
| Track `StateVersion`, request `POST /SyncPlay/Snapshot` on gap | no | medium |
| Apply `StateSnapshot` (≡ GroupJoined + PlayQueue + synthetic command) | no | medium |
| Consume `PositionBeacon` as drift reference | no | small |
| Rejoin-on-reconnect simplification (server now pushes state) | no | small (removes workarounds) |
| Optional: dedicated WS time sync | yes (different URL than integrated variant) | small, deferrable |

Its "v1-server survival quirks" (kicked-probe, hold choreography, 45s load watchdog) remain valid against stock servers and harmless against the plugin.

### 6.3 jellyfin-kodi — a complete v2 client, used as reference only

*(jellyfin-kodi is abandoned upstream; it is **not** a target client for the plugin. Its v2 branch matters here for two reasons: it is the reference implementation kofin's adoption transplants from, and it demonstrates concretely how any spec-conformant client misbehaves without the §5.2 advertisement rule.)*

`kontell/jellyfin-kodi` branch `feat/syncplay-protocol-v2` (fetched locally as `kontell/feat-syncplay-protocol-v2`; 3 commits on top of upstream 2.1.0, ~3,755 lines including ~1,050 lines of tests) is a full spec-conformant v2 client: it sends `ProtocolVersion` in `Join`/`New` bodies (`jellyfin/api.py:373-385`), detects v2 **solely** from `GroupInfoDto.ProtocolVersion` in the joined-group info (`syncplay/manager.py:557-558`), tracks `StateVersion` with snapshot-on-gap via `POST /SyncPlay/Snapshot` (`manager.py:442-472,641`), handles `StateSnapshot` and `PositionBeacon` (`manager.py:495-497`), reports ping, and prefers WS `TimeSync` — gated only on the detected protocol version (`manager.py:188-189`) — with a clean HTTP fallback when the exchange times out after 3s (`timesync.py:_measure_ws`).

Against the plugin it lands in one of three postures:

1. **Unmodified, plugin without the member-scoped advertisement rule (§5.2):** misnegotiation. Its `Join` body field is dropped (member registered v1) while the fork-style DTO advertises server v2 → the client believes v2 is active. Consequence: one wasted 3s WS-`TimeSync` timeout per measurement cycle before the HTTP fallback (drop-mode server) — or a socket-kill loop if live 10.11.11 really tears down on unknown types (§8.6). It also waits for beacons/snapshots that never come (harmless — beacons are advisory and its gap detection simply never fires). Degraded, possibly hazardous. **This concrete failure is why the §5.2 advertisement rule exists.**
2. **Unmodified, plugin with the rule:** the client is never told v2, stays a clean v1 member — correct, safe degradation with zero changes.
3. **(Hypothetical) full v2 under the plugin** would need only a `Hello` call plus gating `can_ws_timesync` on the transport descriptor — noted because kofin's v2 client, which will follow the same spec, needs exactly the same two things.

The practical value of this branch is as the **reference implementation** for kofin's own v2 adoption (M3) — the message handling, versioning and snapshot logic can be transplanted along with its test suite.

### 6.4 Conformance kit

The kit is a pure black-box client (never inspects the server build; `--base` URL + users). Against a Tier 1+1.5 plugin with **no kit changes**, the expected result is: most v2 scenarios pass, and these specific breaks:

| Scenario | Why | Fix |
|---|---|---|
| `v2_negotiation`, and every scenario relying on v2 membership | `ProtocolVersion` in `New`/`Join` bodies is dropped by the stock DTOs | add the `Hello` call in `client.py` setup (~5 lines; all REST funnels through `post()`/`get_json()` at `client.py:101-109`) — or nothing, if route shadowing pans out |
| `group_info_members`, `reconnect_grace`, `grace_expiry` (member polling) | they read `Members[]` from stock `GET /SyncPlay/List`, which serializes the stock DTO | re-point `member_of()` (`common.py:59-61`) at `GET /SyncPlay/ListDetailed` |
| `ws_timesync` + doctor probe | `TimeSync` on `/socket` doesn't exist under the plugin | point at the dedicated WS path from the `Hello` descriptor; skip when absent |

Hygiene items worth doing regardless (the kit's own gaps): assert HTTP status codes in `post()` (a 404 is currently invisible until a confusing timeout), fix `member_of`'s `groups[0]` to select by `GroupId`, and add a probe-driven SKIP so v2 scenarios skip rather than fail on stock servers.

### 6.5 Other v1 clients

Anything speaking stock SyncPlay (jellyfin-mpv-shim, jellyfin-kodi, JMP) continues through the stock routes against the plugin engine, same as web — including benefiting from the phase-0 robustness (bounded waits, no instant kick on clean disconnect).

## 7. Honest deltas vs. the integrated fork

1. **No `TimeSync` on the shared `/socket`** — dedicated-socket variant instead (Tier 1.5), HTTP fallback always. Spec/kit need the transport descriptor.
2. **Stock `GET /SyncPlay/List` stays v1-shaped** (no `ProtocolVersion`/`Members`) — v2 callers use `ListDetailed`; web doesn't read the new fields anyway.
3. **Send-lock coverage is partial** — the plugin serializes its own sends; core `KeepAlive` replies can still theoretically interleave (the same exposure stock has today). Fixed properly only upstream (§9) or in Tier 2.
4. **Zombie detection is member-level, not socket-level** — implemented in M1 as `SocketLiveness` (an `IWebSocketListener` watching per-connection keep-alive dates at the fork's 60s threshold): the kit's grace scenarios pass with identical timing (+62.9s detect, +151.6s expiry), but the dead core *session* lingers in the session table until the upstream reliability fix lands.
5. **`ProtocolVersion` negotiation adds one endpoint** unless route shadowing proves out.
6. **OpenAPI**: plugin endpoints appear in the server's api-docs; if shadowing is used, a Swagger conflict resolver must be `PostConfigure`d.
7. **Rollback is clean**: disable the plugin → stock v1 SyncPlay returns (groups are in-memory only; nothing persists).

## 8. Risks & mitigations

1. **The DI shadow is emergent, not contractual.** A future server refactor (`TryAddSingleton`, moving registration into `Startup.ConfigureServices`) would silently break it. → Startup self-check: resolve `ISyncPlayManager`, assert it is the plugin type, else log loudly + surface in the dashboard config page; CI integration test against each targeted server release; pin `targetAbi` per release line.
2. **Vendored engine drift** (~3,300 lines from the fork). → `VENDORED.md` recording source SHAs per file; a diff script comparing vendored files against the fork branch; treat upstream SyncPlay changes in server releases as a review trigger (history says they are rare — single-digit commits/year).
3. **Two plugins overriding the same service** would race by load order. Not a real-world concern here; the self-check in (1) detects it.
4. **10.11 → 12 transition**: renumbered ABI, net10, possible core SyncPlay changes. → branch the plugin per server major, same as kofinsyncqueue's versioning convention.
5. **Behavioral regressions while porting.** → the conformance kit is the safety net; it already validated the same logic in integrated form (`fast 12/12, slow 2/2` against phase-1). Getting the kit green against the plugin *is* the acceptance gate.
6. ~~**`/socket` unknown-type behavior discrepancy**~~ **Resolved by M0: teardown**, caused by a use-after-advance crash in the error-logging path (present through master; one-line upstream fix drafted in `notes/`). Clients MUST capability-gate before sending any nonstock message type to `/socket`.
7. **ABI patch pinning** (found in M0): a plugin compiled against a newer 10.11 patch than the server is refused at load. Plugin package refs are pinned to `[10.11.0]`; keep them at the oldest supported patch. (kofinsyncqueue's `10.*-*` float shares this latent issue.)

## 9. The upstream path

The plugin *improves* the upstream story rather than replacing it:

1. **Submit `fix/websocket-serialize-sends-and-abort-lost-sockets` upstream now.** It is SyncPlay-independent, fixes a documented core TODO and a real concurrency bug, and is small enough to review in minutes. If merged, deltas #3 and #4 above disappear and Tier 2 becomes moot.
2. The plugin field-proves the protocol with real users; the conformance kit doubles as the evidence (server-agnostic by design — same suite runs against plugin and patched builds).
3. When upstreaming resumes, the already-sliced branches (`integration/syncplay-phase0` → phase-1 features) are the PR series; the plugin's vendored engine *is* that code, so divergence stays low by policy (risk 2's mitigation).
4. The spec needs only a short appendix (negotiation-via-`Hello`, time-sync transport descriptor) — and both additions remain sensible even for an eventual integrated server.

## 10. Milestones & effort

| Milestone | Content | Exit criterion | Effort |
|---|---|---|---|
| **M0 — spike** ✅ **DONE** ([results](m0-spike.md): 16/16 local **and** 16/16 on the production deployment) | All mechanisms demonstrated on from-source and packaged 10.11.11: manager shadow, stock-route + policy delegation, wire DTOs, `SessionControllerConnected`, WS router, body sniffer, Order=-1 + Swagger resolver, and the browser-level stock-web check (join through the shadow-served list; v2 leak = one console error; `StateVersion` invisible) — via the reverse proxy. Unknown-WS-type = teardown on both builds, root-caused (new upstream bug). | each mechanism demonstrated or ruled out | done |
| **M1 — engine port** ✅ **DONE** ([results](m1-engine.md)) | Engine vendored (~3,300 lines + wire layer + negotiation + `SocketLiveness` zombie detection); `Hello`/`Snapshot`/List-shadow live; 19/19 mixed v1+v2 battery | web client works v1; two fake clients mix v1+v2 in one group | done (same day) |
| **M2 — conformance green** ✅ **DONE** | One backward-compatible kit change: time-sync transport discovery via `Hello` (client.py + ws_timesync scenario) | **14/14 — fast 12/12 + slow 2/2**, matching the integrated fork build | done (same day) |
| **M3 — kofin v2** | §6.2 adoption list, transplanting from jellyfin-kodi's v2 branch (§6.3, reference only) | kit's 3-user mixed scenarios + a real web+kofin watch session | 1–2 weeks (already-planned work) |
| **M4 — optional** | Tier 1.5 WS time sync (if not in M1); upstream reliability PR | — | 2–3 days |

Code volume: ≈4,500 lines total, of which ≈3,300 are vendored-with-modifications from the fork (a maintenance liability by policy, see §8.2) and ≈1,200 genuinely new.

---

## Appendix A — key verified references

| Fact | Reference (release-10.11.z unless noted) |
|---|---|
| Core registers before plugins | `Emby.Server.Implementations/ApplicationHost.cs:460,462` |
| `ISyncPlayManager` plain singleton | `ApplicationHost.cs:560`; zero `TryAdd*` repo-wide |
| Consumers: controller + auth policy only | `Jellyfin.Api/Controllers/SyncPlayController.cs`, `Jellyfin.Api/Auth/SyncPlayAccessPolicy/SyncPlayAccessHandler.cs` |
| `SessionControllerConnected` | `MediaBrowser.Controller/Session/ISessionManager.cs:54`; raised `SessionWebSocketListener.cs:129` |
| Per-session generic send | `MediaBrowser.Controller/Session/ISessionController.cs:33`; `SessionInfo.cs:182`; envelope built in `WebSocketController.SendMessage` (master `:121-128`) |
| Unknown WS type: parsed pre-listener, `JsonException` caught, dropped | `Emby.Server.Implementations/HttpServer/WebSocketConnection.cs` (`ProcessInternal`) |
| WS middleware catches all upgrades, any path | `Jellyfin.Api/Middleware/WebSocketHandlerMiddleware.cs`; wired `Startup.cs:216` before `UseEndpoints:225`; `UseWebSockets` global `:164` |
| `IWebSocketManager` shadowable | `ApplicationHost.cs:536`; one consumer, resolved per request |
| Swagger lacks conflict resolver | `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:203` region |
| Master renumbered 12.0.0 / net10 | `SharedVersion.cs` @ master; commit `bc074b5283` (#16758) |
| Engine sizes (base `7f3e27c007`) | Group 679, Manager 419, states 1,366, PlayQueueManager 550, controller 440 lines |

## Appendix B — fork branch inventory

All based on `7f3e27c007` (2026-04-05, pre-renumber master):

- `fix/websocket-serialize-sends-and-abort-lost-sockets` → `79ab593479` — standalone upstream candidate
- `fix/syncplay-group-wait-buffering-and-reconnect` → `8cda984c43` (= `integration/syncplay-phase0`)
- `feat/websocket-time-sync` → `5edca0c89f`
- `fix/syncplay-adaptive-playback-tolerance` → `007686bc27`
- `feat/syncplay-protocol-v2` → `e8aa373748`
- `integration/syncplay-phase1` → `336ac05456` — **the full feature set** (union of the above)
- `testing/syncplay` = phase1 + `docs/SYNCPLAY.md`; `docs/syncplay-protocol` → spec only

Client side: `kontell/jellyfin-kodi` branch `feat/syncplay-protocol-v2` → `664d68b6` (3 commits on upstream 2.1.0; fetched locally in `../../ref/jellyfin-kodi` as `kontell/feat-syncplay-protocol-v2`) — the complete v2 reference client (§6.3).
