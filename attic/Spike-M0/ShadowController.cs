using System.Collections.Generic;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// The route-shadowing probe (feasibility §4.7): duplicates the stock
/// GET /SyncPlay/List route at Order = -1. If precedence works, responses
/// carry the X-SyncPlayV2-Shadow header; if it does not, requests 500 with
/// AmbiguousMatchException. Either outcome — and what happens to
/// /api-docs/openapi.json — is the finding.
/// </summary>
[ApiController]
[Route("SyncPlay", Order = -1)]
[Produces(MediaTypeNames.Application.Json)]
public class ShadowController : ControllerBase
{
    private readonly SpikeDiagnostics _diag;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ISessionManager _sessionManager;

    public ShadowController(SpikeDiagnostics diag, ISyncPlayManager syncPlayManager, ISessionManager sessionManager)
    {
        _diag = diag;
        _syncPlayManager = syncPlayManager;
        _sessionManager = sessionManager;
    }

    [HttpGet("List")]
    [Authorize(Policy = Policies.SyncPlayJoinGroup)]
    public ActionResult<IEnumerable<GroupInfoDto>> ShadowedList()
    {
        _diag.Add("shadow-route", "GET /SyncPlay/List served by the plugin's Order=-1 shadow");
        Response.Headers["X-SyncPlayV2-Shadow"] = "1";

        // Same behavior as stock: hand off to the (shadowed) manager.
        var deviceId = User.FindFirst("Jellyfin-DeviceId")?.Value;
        SessionInfo? session = null;
        if (deviceId is not null)
        {
            foreach (var s in _sessionManager.Sessions)
            {
                if (string.Equals(s.DeviceId, deviceId, System.StringComparison.OrdinalIgnoreCase))
                {
                    session = s;
                    break;
                }
            }
        }

        return Ok(_syncPlayManager.ListGroups(session!, new ListGroupsRequest()));
    }
}
