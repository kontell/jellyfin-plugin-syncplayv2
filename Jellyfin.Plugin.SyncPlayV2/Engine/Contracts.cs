using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;

namespace Jellyfin.Plugin.SyncPlayV2.Engine;

/// <summary>
/// The v2 additions to the group state context. The vendored WaitingGroupState
/// casts to this for the ping-scaled tolerance (upstream this is an addition
/// to IGroupStateContext itself — phase1 @ 336ac05456).
/// </summary>
public interface IGroupStateContextV2 : IGroupStateContext
{
    /// <summary>
    /// Maximum accepted position offset for the member, in milliseconds:
    /// clamp(2 x ping, 500, 2000).
    /// </summary>
    long GetMemberPlaybackOffset(SessionInfo session);
}

/// <summary>
/// The v2 additions to the manager, consumed by the plugin's own controllers.
/// </summary>
public interface ISyncPlayManagerV2 : ISyncPlayManager
{
    /// <summary>
    /// Push a full state snapshot of the session's group to the session
    /// (v2 members get a StateSnapshot; v1 members the GroupJoined triple).
    /// </summary>
    void RequestSnapshot(SessionInfo session, CancellationToken cancellationToken);

    /// <summary>
    /// The enriched group list (Members, member-scoped ProtocolVersion) for
    /// the shadowed GET /SyncPlay/List.
    /// </summary>
    List<Wire.WireGroupInfo> ListGroupsDetailed(SessionInfo session, bool requesterIsV2);
}
