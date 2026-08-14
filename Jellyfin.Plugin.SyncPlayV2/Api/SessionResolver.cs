using System;
using System.Security.Claims;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Api;

/// <summary>
/// Resolves the calling session from auth claims, mirroring the stock
/// controllers' RequestHelpers.GetSession (which is internal to Jellyfin.Api):
/// identify the device from claims and let the session manager create or
/// refresh its session.
/// </summary>
public class SessionResolver
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<SessionResolver> _logger;

    public SessionResolver(ISessionManager sessionManager, IUserManager userManager, ILogger<SessionResolver> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<SessionInfo?> Resolve(HttpContext httpContext)
    {
        var user = httpContext.User;
        var userId = user.FindFirstValue("Jellyfin-UserId");
        var deviceId = user.FindFirstValue("Jellyfin-DeviceId");
        var device = user.FindFirstValue("Jellyfin-Device") ?? "unknown";
        var client = user.FindFirstValue("Jellyfin-Client") ?? "unknown";
        var version = user.FindFirstValue("Jellyfin-Version") ?? "0";

        if (deviceId is null || !Guid.TryParse(userId, out var userGuid))
        {
            _logger.LogWarning("Cannot resolve session: missing claims (userId={UserId}, deviceId={DeviceId}).", userId, deviceId);
            return null;
        }

        var jellyfinUser = _userManager.GetUserById(userGuid);
        if (jellyfinUser is null)
        {
            return null;
        }

        var remote = httpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        return await _sessionManager
            .LogSessionActivity(client, version, deviceId, device, remote, jellyfinUser)
            .ConfigureAwait(false);
    }

    /// <summary>Claim pair used as the protocol-version registry key.</summary>
    public static (string? Client, string? DeviceId) Identity(ClaimsPrincipal user)
        => (user.FindFirstValue("Jellyfin-Client"), user.FindFirstValue("Jellyfin-DeviceId"));
}
