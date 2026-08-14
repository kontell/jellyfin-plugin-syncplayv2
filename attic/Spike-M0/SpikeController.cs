using System;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// M0 spike probes. /SyncPlayV2Spike/* is the diagnostics surface; the
/// absolute-routed POST /SyncPlay/Hello proves a plugin can add NEW sub-routes
/// under the stock /SyncPlay prefix (and reuse the stock auth policies) with
/// no conflict.
/// </summary>
[ApiController]
[Route("SyncPlayV2Spike")]
[Produces(MediaTypeNames.Application.Json)]
public class SpikeController : ControllerBase
{
    private readonly SpikeDiagnostics _diag;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;

    public SpikeController(
        SpikeDiagnostics diag,
        ISyncPlayManager syncPlayManager,
        ISessionManager sessionManager,
        IUserManager userManager)
    {
        _diag = diag;
        _syncPlayManager = syncPlayManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
    }

    /// <summary>
    /// The spike scoreboard: which implementations DI resolved, plus all
    /// recorded evidence.
    /// </summary>
    [HttpGet("Status")]
    [Authorize]
    public ActionResult GetStatus()
    {
        return Ok(new
        {
            ResolvedSyncPlayManager = _syncPlayManager.GetType().FullName,
            ManagerIsSpike = _syncPlayManager is SpikeSyncPlayManager,
            Evidence = _diag.Snapshot(),
        });
    }

    /// <summary>
    /// New sub-route under the stock prefix + stock policy reuse. A 404 from a
    /// stock server / 200 here is the client capability probe from the
    /// feasibility study §5.2.
    /// </summary>
    [HttpPost("/SyncPlay/Hello")]
    [Authorize(Policy = Policies.SyncPlayHasAccess)]
    public ActionResult Hello([FromBody] HelloRequest? request)
    {
        _diag.Add("hello", $"ProtocolVersion={request?.ProtocolVersion.ToString() ?? "absent"} user={User.FindFirstValue("Jellyfin-UserId")}");
        return Ok(new
        {
            ProtocolVersion = 2,
            PluginVersion = typeof(SpikeController).Assembly.GetName().Version?.ToString(),
            Spike = true,
        });
    }

    /// <summary>
    /// Push a v2-only group update (plugin DTO, Type as string) to the calling
    /// session's WebSocket. Observed by the kit client / web console.
    /// </summary>
    [HttpPost("SendGroupUpdate")]
    [Authorize]
    public async Task<ActionResult> SendGroupUpdate([FromQuery] string type = "StateSnapshot")
    {
        var session = await ResolveSession().ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest("no session for this device");
        }

        if (_syncPlayManager is not SpikeSyncPlayManager spike)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "manager is not the spike manager");
        }

        spike.Send(session, new SpikeGroupUpdate
        {
            GroupId = Guid.NewGuid(),
            Type = type,
            StateVersion = 42,
            Data = new { Marker = "syncplayv2-spike", When = DateTime.UtcNow },
        });
        return NoContent();
    }

    /// <summary>
    /// Push a SyncPlayCommand-shaped plugin DTO (default Stop, which stock web
    /// executes without a playlist-item match) to the calling session.
    /// </summary>
    [HttpPost("SendCommand")]
    [Authorize]
    public async Task<ActionResult> SendCommand([FromQuery] string command = "Stop")
    {
        var session = await ResolveSession().ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest("no session for this device");
        }

        if (_syncPlayManager is not SpikeSyncPlayManager spike)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "manager is not the spike manager");
        }

        var now = DateTime.UtcNow;
        spike.Send(
            session,
            new SpikeSendCommand
            {
                GroupId = Guid.NewGuid(),
                PlaylistItemId = Guid.Empty,
                When = now.AddSeconds(1),
                PositionTicks = 0,
                Command = command,
                EmittedAt = now,
                StateVersion = 42,
            },
            SessionMessageType.SyncPlayCommand);
        return NoContent();
    }

    /// <summary>
    /// Mirrors the stock controller's RequestHelpers.GetSession: identify the
    /// caller from auth claims and let the session manager create/refresh the
    /// session.
    /// </summary>
    private async Task<SessionInfo?> ResolveSession()
    {
        var userId = User.FindFirstValue("Jellyfin-UserId");
        var deviceId = User.FindFirstValue("Jellyfin-DeviceId");
        var device = User.FindFirstValue("Jellyfin-Device") ?? "spike";
        var client = User.FindFirstValue("Jellyfin-Client") ?? "spike";
        var version = User.FindFirstValue("Jellyfin-Version") ?? "0";

        if (deviceId is null || !Guid.TryParse(userId, out var userGuid))
        {
            _diag.Add("session-resolve", $"missing claims: userId={userId} deviceId={deviceId}");
            return null;
        }

        var user = _userManager.GetUserById(userGuid);
        if (user is null)
        {
            return null;
        }

        var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        return await _sessionManager
            .LogSessionActivity(client, version, deviceId, device, remote, user)
            .ConfigureAwait(false);
    }

    public class HelloRequest
    {
        public int ProtocolVersion { get; set; } = 1;
    }
}
