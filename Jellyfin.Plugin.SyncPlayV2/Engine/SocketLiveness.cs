using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Diagnostics;
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
    private readonly EngineCounters _counters;
    private readonly ConcurrentDictionary<IWebSocketConnection, Entry> _sockets = new();
    private readonly Timer _timer;
    private int _sweeping;

    public SocketLiveness(ISessionManager sessionManager, SyncPlayManagerV2 engine, ILogger<SocketLiveness> logger, EngineCounters counters)
    {
        _counters = counters;
        _sessionManager = sessionManager;
        _engine = engine;
        _logger = logger;
        _timer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private sealed class Entry
    {
        public Entry(string? client, string deviceId)
        {
            Client = client;
            DeviceId = deviceId;
            ConnectedAt = DateTime.UtcNow;
        }

        public string? Client { get; }

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
            _sockets[connection] = new Entry(connection.AuthorizationInfo?.Client, deviceId);
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
        // Timer callbacks can overlap; two sweeps reading the same entry's
        // ReportedDead would both report (and count) one death.
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
        {
            return;
        }

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
                    // Only presume the CLIENT dead when it has no other live socket
                    // (a reconnect opens a fresh socket while the zombie lingers).
                    // Another app on the same device is not a sibling: it is a
                    // different session, and alive or not says nothing of this one.
                    var hasLiveSibling = _sockets.Any(kv =>
                        !ReferenceEquals(kv.Key, connection)
                        && SameIdentity(kv.Value, entry)
                        && !IsStale(kv.Key, kv.Value));

                    entry.ReportedDead = true;

                    // A dead socket is counted whether or not a live sibling
                    // keeps its session connected.
                    _counters.ZombieSocket();
                    if (!hasLiveSibling)
                    {
                        Notify(entry, dead: true);
                    }
                }
                else
                {
                    // Keep-alives resumed on a socket previously presumed dead.
                    entry.ReportedDead = false;
                    Notify(entry, dead: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Socket liveness sweep failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    private static bool SameIdentity(Entry a, Entry b)
        => string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase)
            && (a.Client is null || b.Client is null || string.Equals(a.Client, b.Client, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The sessions a socket belongs to: same device id and same client, the
    /// identity a Jellyfin session is keyed by. The device id alone is not
    /// unique — two apps on one device each have a session under it — and
    /// taking the first match could disconnect the wrong one, or leave the
    /// dead one attached. A socket that did not say which client it is
    /// matches every session of its device, as before.
    /// </summary>
    /// <param name="sessions">The server's sessions.</param>
    /// <param name="client">The socket's client, if it declared one.</param>
    /// <param name="deviceId">The socket's device id.</param>
    /// <returns>The matching sessions.</returns>
    public static IReadOnlyList<SessionInfo> SessionsOf(IEnumerable<SessionInfo> sessions, string? client, string deviceId)
        => sessions
            .Where(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                && (client is null || string.Equals(s.Client, client, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private void Notify(Entry entry, bool dead)
    {
        var sessions = SessionsOf(_sessionManager.Sessions, entry.Client, entry.DeviceId);
        foreach (var session in sessions)
        {
            if (dead)
            {
                _logger.LogInformation("Device {DeviceId} ({Client}) stopped keep-aliving (socket not aborted by core); marking session {SessionId} disconnected for SyncPlay.", entry.DeviceId, entry.Client, session.Id);
                _engine.MarkSessionDisconnected(session);
            }
            else
            {
                _logger.LogInformation("Device {DeviceId} ({Client}) resumed keep-aliving; re-attaching session {SessionId}.", entry.DeviceId, entry.Client, session.Id);
                _engine.ReattachSession(session);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
