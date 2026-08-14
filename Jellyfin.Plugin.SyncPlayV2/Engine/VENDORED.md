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
| `Group.cs` | `Emby.Server.Implementations/SyncPlay/Group.cs` | plugin namespace; implements `IGroupStateContextV2`; ctor takes `Sender` + `ProtocolVersionRegistry`; `AddSession` reads the registry instead of `request.ProtocolVersion`; `SendGroupUpdate`/`SendCommand` translate stock typed updates to wire DTOs at the choke point and deliver via member `Session` refs; `GroupJoined`/`StateSnapshot`/`PositionBeacon` built as wire DTOs directly (`GetWireInfo` is member-scoped per the §5.2 advertisement rule); `GetInfo()` reverted to the stock 5-arg `GroupInfoDto` (interface surface) |
| `SyncPlayManagerV2.cs` | `Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs` | plugin namespace; class renamed; implements `ISyncPlayManagerV2`; ctor takes `Sender` + registry (passed to groups); adds `ListGroupsDetailed`; error updates stay on the stock `ISessionManager.SendSyncPlayGroupUpdate` path (StateVersion 0 by design) |
| `GroupMember.cs` | `MediaBrowser.Controller/SyncPlay/GroupMember.cs` (fork version) | plugin namespace only |
| `GroupStates/*.cs` | `MediaBrowser.Controller/SyncPlay/GroupStates/*.cs` (fork versions) | plugin namespace + `using` for the stock parent namespace; `WaitingGroupState` casts the context to `IGroupStateContextV2` for `GetMemberPlaybackOffset` (upstream: interface member). Vendored wholesale because the states hard-construct each other, so partial vendoring cannot work |

Not vendored (reused from the server's shipped assemblies): `PlayQueueManager`,
all request/enum types, `IGroupState`/`IGroupStateContext`, the stock typed
`GroupUpdate` subclasses the states construct (translated to wire at send).
