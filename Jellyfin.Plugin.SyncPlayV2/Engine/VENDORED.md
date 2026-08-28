# Vendored engine provenance

The files in this directory are vendored from the `kontell/jellyfin` fork,
branch `integration/syncplay-phase1` @ `336ac05456` ("SyncPlay protocol v2:
versioned state, snapshots, beacons, WS time sync, reconnect grace"), whose
SyncPlay sources are byte-identical to `release-10.11.z` @ `1fbd873929`
(10.11.11) outside the phase-1 changes — verified before vendoring.

Keep divergence deliberate: upstream SyncPlay changes in server releases are a
review trigger (`git diff <old> <new> -- Emby.Server.Implementations/SyncPlay
MediaBrowser.Controller/SyncPlay MediaBrowser.Model/SyncPlay` in the server
repo), and these files are the future upstream PR series, so gratuitous edits
here make the eventual upstreaming harder.

| File | Fork source | Deliberate divergences from the fork |
|---|---|---|
| `Group.cs` | `Emby.Server.Implementations/SyncPlay/Group.cs` | plugin namespace; implements `IGroupStateContextV2`; ctor takes `Sender` + `ProtocolVersionRegistry`; `AddSession` reads the registry instead of `request.ProtocolVersion`; `SendGroupUpdate`/`SendCommand` translate stock typed updates to wire DTOs at the choke point and deliver via member `Session` refs; `GroupJoined`/`StateSnapshot`/`PositionBeacon` built as wire DTOs directly (`GetWireInfo` is member-scoped per the §5.2 advertisement rule); `GetInfo()` reverted to the stock 5-arg `GroupInfoDto` (interface surface). **Feature divergence: rendezvous** — `ShouldRendezvous`/`RendezvousMember` hand a member the hot-join path instead of correcting it again — from the correction branch when the corrections are not converging, and from the wait timeout, which measurement showed is the only one of the two a slow-reloading member actually reaches; `SetBuffering(.., false)` resets the correction counters; `IsIgnoredByTimeout` (interface surface, for the state machine's recovery path) |
| `SyncPlayManagerV2.cs` | `Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs` | plugin namespace; class renamed; implements `ISyncPlayManagerV2`; ctor takes `Sender` + registry (passed to groups); adds `ListGroupsDetailed`; error updates stay on the stock `ISessionManager.SendSyncPlayGroupUpdate` path (StateVersion 0 by design). **Feature divergence: rendezvous on wait timeout** — `IgnoreStalledMembers` rendezvouses a v2 member instead of merely ignoring it, so the group stops waiting *and* the member is given a scheduled catch-up; v1 members are abandoned as before |
| `GroupMember.cs` | `MediaBrowser.Controller/SyncPlay/GroupMember.cs` (fork version) | plugin namespace; `HotJoining`; `CorrectionAttempts` + `LastCorrectionDelayTicks` for the rendezvous decision; `ResumeWaiting()` — the one place the "the group waits for this member again, without reversing a spectator choice of its own" rule lives, called from `SetBuffering`, `ReconnectSession` and the state machine's recovery path, which had two inline copies of it between them |
| `GroupStates/*.cs` | `MediaBrowser.Controller/SyncPlay/GroupStates/*.cs` (fork versions) | plugin namespace + `using` for the stock parent namespace; `WaitingGroupState` casts the context to `IGroupStateContextV2` for `GetMemberPlaybackOffset` (upstream: interface member). Vendored wholesale because the states hard-construct each other, so partial vendoring cannot work. **Feature divergence (beyond the fork): hot join** — `PlayingGroupState.SessionJoined` keeps the group Playing for v2 joiners (`BeginHotJoin`: not-waited-on flags + snapshot push), their `Buffering` is absorbed, and their `Ready` is answered with a private scheduled `Unpause` at the live position (`CompleteHotJoin`); v1 joiners keep the classic barrier. Config-gated (`HotJoin`, default on). The plugin is the protocol lab here — this is candidate upstream material, not drift. **Feature divergence: rendezvous** — `WaitingGroupState` answers a hot-joining member's `Ready` with `CompleteHotJoin` and absorbs its `Buffering` (both mirroring `PlayingGroupState`), and its position-correction branch rendezvouses a member that cannot seek accurately rather than re-seeking it, releasing the group with `ResumeIfNobodyElseIsWaiting`. **Fix divergence: a timed-out member's Ready** — `AbstractGroupState` answers a `ReadyGroupRequest` from a member with `IgnoredByTimeout` set by clearing it, instead of dropping the report as unhandled. Upstream only `WaitingGroupState` handles `Ready`, and the wait-timeout sweep runs while the group is Playing, so upstream a member timed out there can never be waited for again — contradicting `IgnoredByTimeout`'s own documented contract and `SetMemberDisconnected`'s reliance on it. Upstreamable as-is |

New in this directory, vendored from nothing: `CorrectionPolicy.cs` — the pure
decision behind the rendezvous divergence, kept out of `Group` because `Group`
cannot be constructed without a server around it and the decision is the whole
of the behaviour. It lives here rather than beside the wire layer because it is
engine logic and belongs in the same upstream PR series. Note that the CI format
job excludes this whole directory (see `.github/workflows/ci.yml`), so a new file
here is not format-gated — this one was checked by hand with `--include`.

Not vendored (reused from the server's shipped assemblies): `PlayQueueManager`,
all request/enum types, `IGroupState`/`IGroupStateContext`, the stock typed
`GroupUpdate` subclasses the states construct (translated to wire at send).
