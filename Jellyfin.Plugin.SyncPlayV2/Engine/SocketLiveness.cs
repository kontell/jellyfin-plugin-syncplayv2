using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Engine;

/// <summary>
/// Plugin-side zombie-socket detection (feasibility §7.4). Stock Jellyfin
/// notices a socket that stopped keep-aliving after 60s but only drops it from
/// its watchlist (the core TODO the fork's reliability branch fixes with
/// Abort()); the session — and therefore the SyncPlay member — lives on and
/// the disconnect grace never engages. A plugin cannot abort the socket, but
/// it CAN observe every connection via IWebSocketListener and drive the
/// engine's member-level state directly: mark the member disconnected when
/// its device has no live socket, and re-attach it when keep-alives resume on
/// the same socket (a NEW socket re-attaches via SessionControllerConnected
/// as usual). The dead core session lingers — that hygiene needs the upstream
/// fix — but group behavior matches the integrated build.
/// </summary>
public class SocketLiveness : IWebSocketListener, IDisposable
{
    private static readonly TimeSpan LostTimeout = TimeSpan.FromSeconds(60);

    private readonly ISessionManager _sessionManager;
    private readonly SyncPlayManagerV2 _engine;
    private readonly ILogger<SocketLiveness> _logger;
    private readonly ConcurrentDictionary<IWebSocketConnection, Entry> _sockets = new();
    private readonly Timer _timer;

    public SocketLiveness(ISessionManager sessionManager, SyncPlayManagerV2 engine, ILogger<SocketLiveness> logger)
    {
        _sessionManager = sessionManager;
        _engine = engine;
        _logger = logger;
        _timer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private sealed class Entry
    {
        public Entry(string deviceId)
        {
            DeviceId = deviceId;
            ConnectedAt = DateTime.UtcNow;
        }

        public string DeviceId { get; }

        public DateTime ConnectedAt { get; }

        public bool ReportedDead { get; set; }
    }

    /// <inheritdoc />
    public Task ProcessMessageAsync(WebSocketMessageInfo message) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ProcessWebSocketConnectedAsync(IWebSocketConnection connection, HttpContext httpContext)
    {
        var deviceId = connection.AuthorizationInfo?.DeviceId;
        if (!string.IsNullOrEmpty(deviceId))
        {
            _sockets[connection] = new Entry(deviceId);
            connection.Closed += (_, _) => _sockets.TryRemove(connection, out _);
        }

        return Task.CompletedTask;
    }

    private static bool IsStale(IWebSocketConnection connection, Entry entry)
    {
        var last = connection.LastActivityDate;
        if (connection.LastKeepAliveDate > last)
        {
            last = connection.LastKeepAliveDate;
        }

        if (entry.ConnectedAt > last)
        {
            last = entry.ConnectedAt;
        }

        return DateTime.UtcNow - last >= LostTimeout;
    }

    private void Sweep()
    {
        try
        {
            foreach (var (connection, entry) in _sockets)
            {
                if (connection.State is not WebSocketState.Open and not WebSocketState.Connecting)
                {
                    _sockets.TryRemove(connection, out _);
                    continue;
                }

                var stale = IsStale(connection, entry);
                if (stale == entry.ReportedDead)
                {
                    continue;
                }

                if (stale)
                {
                    // Only presume the DEVICE dead when it has no other live socket
                    // (a reconnect opens a fresh socket while the zombie lingers).
                    var hasLiveSibling = _sockets.Any(kv =>
                        !ReferenceEquals(kv.Key, connection)
                        && string.Equals(kv.Value.DeviceId, entry.DeviceId, StringComparison.OrdinalIgnoreCase)
                        && !IsStale(kv.Key, kv.Value));

                    entry.ReportedDead = true;
                    if (!hasLiveSibling)
                    {
                        Notify(entry.DeviceId, dead: true);
                    }
                }
                else
                {
                    // Keep-alives resumed on a socket previously presumed dead.
                    entry.ReportedDead = false;
                    Notify(entry.DeviceId, dead: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Socket liveness sweep failed.");
        }
    }

    private void Notify(string deviceId, bool dead)
    {
        var session = _sessionManager.Sessions.FirstOrDefault(
            s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            return;
        }

        if (dead)
        {
            _logger.LogInformation("Device {DeviceId} stopped keep-aliving (socket not aborted by core); marking session {SessionId} disconnected for SyncPlay.", deviceId, session.Id);
            _engine.MarkSessionDisconnected(session);
        }
        else
        {
            _logger.LogInformation("Device {DeviceId} resumed keep-aliving; re-attaching session {SessionId}.", deviceId, session.Id);
            _engine.ReattachSession(session);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
