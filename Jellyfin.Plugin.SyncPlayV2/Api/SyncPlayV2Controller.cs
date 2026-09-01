using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SyncPlayV2.Api;

/// <summary>
/// The plugin's additions under the stock /SyncPlay prefix: Hello (capability
/// probe + explicit version registration) and Snapshot (spec §5.4/§6). Both
/// are new sub-routes — no conflict with the stock controller.
/// </summary>
[ApiController]
[Route("SyncPlay")]
[Produces(MediaTypeNames.Application.Json)]
public class SyncPlayV2Controller : ControllerBase
{
    private readonly ISyncPlayManagerV2 _syncPlayManager;
    private readonly ProtocolVersionRegistry _versions;
    private readonly SessionResolver _sessions;

    public SyncPlayV2Controller(
        ISyncPlayManagerV2 syncPlayManager,
        ProtocolVersionRegistry versions,
        SessionResolver sessions)
    {
        _syncPlayManager = syncPlayManager;
        _versions = versions;
        _sessions = sessions;
    }

    /// <summary>
    /// Capability probe + protocol negotiation in one round trip: a 404 means
    /// a stock server (client stays v1); a 200 carries the server's protocol
    /// version and the time-sync transport descriptor, and registers the
    /// caller's requested version for its device.
    /// </summary>
    /// <param name="request">The client's requested protocol version.</param>
    /// <response code="200">Capabilities returned and version registered.</response>
    /// <returns>The server capability document.</returns>
    [HttpPost("Hello")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Authorize(Policy = Policies.SyncPlayHasAccess)]
    public ActionResult Hello([FromBody] HelloRequest? request)
    {
        var (client, deviceId) = SessionResolver.Identity(User);
        var externalContent = request?.Capabilities?.Contains(ProtocolVersionRegistry.ExternalContentCapability) == true;
        _versions.RegisterHello(client, deviceId, request?.ProtocolVersion ?? 1, externalContent);

        return Ok(new
        {
            ProtocolVersion = 2,
            PluginVersion = typeof(SyncPlayV2Controller).Assembly.GetName().Version?.ToString(),
            TimeSync = new { WebSocketPath = "/SyncPlay/TimeSync" },
            Capabilities = new[] { ProtocolVersionRegistry.ExternalContentCapability },
        });
    }

    /// <summary>
    /// Request a full state snapshot of the joined group, pushed over the
    /// session's WebSocket (v2 members get a StateSnapshot; v1 members the
    /// GroupJoined + PlayQueue + command triple).
    /// </summary>
    /// <response code="204">Snapshot sent over the session's WebSocket.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpPost("Snapshot")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Authorize(Policy = Policies.SyncPlayIsInGroup)]
    public async Task<ActionResult> Snapshot()
    {
        var session = await _sessions.Resolve(HttpContext).ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest("could not resolve the calling session");
        }

        _syncPlayManager.RequestSnapshot(session, CancellationToken.None);
        return NoContent();
    }

    /// <summary>
    /// SetNewQueue whose entries may be external content (plan G3.3): a
    /// library item id or a content descriptor per entry. Descriptor entries
    /// ride the queue as sentinel ids with their descriptors registered on
    /// the group; the server never resolves them.
    /// </summary>
    /// <param name="body">The mixed queue request.</param>
    /// <response code="204">Queue update sent to all group members.</response>
    /// <response code="400">An entry was invalid.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpPost("SetNewQueueEx")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = Policies.SyncPlayIsInGroup)]
    public async Task<ActionResult> SetNewQueueEx([FromBody] SetNewQueueExRequestDto body)
    {
        var session = await _sessions.Resolve(HttpContext).ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest("could not resolve the calling session");
        }

        if (!ExternalContent.TryBuildEntries(body?.PlayingQueue, out var itemIds, out var content, out var error))
        {
            return BadRequest(error);
        }

        var request = new PlayGroupRequest(itemIds, body!.PlayingItemPosition, body.StartPositionTicks);
        _syncPlayManager.HandleRequestWithContent(session, request, content, CancellationToken.None);
        return NoContent();
    }

    /// <summary>
    /// Queue whose entries may be external content (plan G3.3); the Ex
    /// counterpart of the stock Queue, appending mixed entries.
    /// </summary>
    /// <param name="body">The mixed append request.</param>
    /// <response code="204">Queue update sent to all group members.</response>
    /// <response code="400">An entry was invalid.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpPost("QueueEx")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = Policies.SyncPlayIsInGroup)]
    public async Task<ActionResult> QueueEx([FromBody] QueueExRequestDto body)
    {
        var session = await _sessions.Resolve(HttpContext).ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest("could not resolve the calling session");
        }

        if (!ExternalContent.TryBuildEntries(body?.Entries, out var itemIds, out var content, out var error))
        {
            return BadRequest(error);
        }

        var request = new QueueGroupRequest(itemIds, body!.Mode);
        _syncPlayManager.HandleRequestWithContent(session, request, content, CancellationToken.None);
        return NoContent();
    }

    public class HelloRequest
    {
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>Gets or sets the capabilities the device declares (plan G3.2), e.g. "ExternalContent".</summary>
        public System.Collections.Generic.List<string>? Capabilities { get; set; }
    }
}
