using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SyncPlayV2.Api;

/// <summary>
/// Shadows the stock GET /SyncPlay/List at Order = -1 (M0-proven, with the
/// Swagger conflict resolver) so the response can carry the v2 additions:
/// Members[] always (harmless superset for v1 clients — verified against
/// stock web), ProtocolVersion only for requesters that negotiated v2.
/// </summary>
[ApiController]
[Route("SyncPlay", Order = -1)]
[Produces(MediaTypeNames.Application.Json)]
public class ListShadowController : ControllerBase
{
    private readonly ISyncPlayManagerV2 _syncPlayManager;
    private readonly ProtocolVersionRegistry _versions;
    private readonly SessionResolver _sessions;

    public ListShadowController(
        ISyncPlayManagerV2 syncPlayManager,
        ProtocolVersionRegistry versions,
        SessionResolver sessions)
    {
        _syncPlayManager = syncPlayManager;
        _versions = versions;
        _sessions = sessions;
    }

    /// <summary>
    /// Gets all SyncPlay groups accessible to the caller, in the enriched
    /// v2 shape.
    /// </summary>
    /// <response code="200">Groups returned.</response>
    /// <returns>The list of groups.</returns>
    [HttpGet("List")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Authorize(Policy = Policies.SyncPlayJoinGroup)]
    public async Task<ActionResult<IEnumerable<WireGroupInfo>>> List()
    {
        var session = await _sessions.Resolve(HttpContext).ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest("could not resolve the calling session");
        }

        var requesterIsV2 = _versions.Resolve(session) >= 2;
        return Ok(_syncPlayManager.ListGroupsDetailed(session, requesterIsV2));
    }
}
