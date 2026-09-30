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
/// its session has no live socket, and re-attach it when keep-alives resume on
/// the same socket (a NEW socket re-attaches via SessionControllerConnected
/// as usual). The dead core session lingers — that hygiene needs the upstream
/// fix — but group behavior matches the integrated build.
/// </summary>
public class SocketLiveness : IWebSocketListener, IDisposable
{
    private static readonly TimeSpan LostTimeout = TimeSpan.FromSeconds(60);

    private readonly ISessionManager _sessionManager;
    private readonly Action<SessionInfo> _markDisconnected;
    private readonly Action<SessionInfo> _reattach;
    private readonly ILogger<SocketLiveness> _logger;
    private readonly Func<DateTime> _now;
    private readonly EngineCounters _counters;
    private readonly ConcurrentDictionary<IWebSocketConnection, Entry> _sockets = new();
    private readonly Timer? _timer;
    private int _sweeping;

    public SocketLiveness(ISessionManager sessionManager, SyncPlayManagerV2 engine, ILogger<SocketLiveness> logger, EngineCounters counters)
        : this(sessionManager, engine.MarkSessionDisconnected, engine.ReattachSession, logger, () => DateTime.UtcNow, startTimer: true, counters)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocketLiveness"/> class
    /// with its effects, clock and timer supplied: tests drive
    /// <see cref="Sweep"/> themselves against a clock they move.
    /// </summary>
    internal SocketLiveness(
        ISessionManager sessionManager,
        Action<SessionInfo> markDisconnected,
        Action<SessionInfo> reattach,
        ILogger<SocketLiveness> logger,
        Func<DateTime> now,
        bool startTimer,
        EngineCounters? counters = null)
    {
        _counters = counters ?? new EngineCounters();
        _sessionManager = sessionManager;
        _markDisconnected = markDisconnected;
        _reattach = reattach;
        _logger = logger;
        _now = now;
        if (startTimer)
        {
            _timer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// What a socket is known by, the same three values a Jellyfin 12
    /// session is keyed by: client, device id and user.
    /// </summary>
    private sealed class Entry
    {
        public Entry(string client, string deviceId, Guid userId, DateTime connectedAt)
        {
            Client = client;
            DeviceId = deviceId;
            UserId = userId;
            ConnectedAt = connectedAt;
        }

        public string Client { get; }

        public string DeviceId { get; }

        public Guid UserId { get; }

        public DateTime ConnectedAt { get; }

        public bool ReportedDead { get; set; }
    }

    /// <inheritdoc />
    public Task ProcessMessageAsync(WebSocketMessageInfo message) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ProcessWebSocketConnectedAsync(IWebSocketConnection connection, HttpContext httpContext)
    {
        // A socket that cannot be tied to one session is not watched: marking
        // "every session of the device" dead is how the wrong client gets
        // disconnected. Jellyfin fills Client and DeviceId from the token's
        // device when the header leaves them out, so a real client has both.
        var auth = connection.AuthorizationInfo;
        if (auth is not null && !string.IsNullOrEmpty(auth.DeviceId) && !string.IsNullOrEmpty(auth.Client))
        {
            _sockets[connection] = new Entry(auth.Client, auth.DeviceId, auth.UserId, _now());
            connection.Closed += (_, _) => _sockets.TryRemove(connection, out _);
        }

        return Task.CompletedTask;
    }

    private bool IsStale(IWebSocketConnection connection, Entry entry)
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

        return _now() - last >= LostTimeout;
    }

    internal void Sweep()
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
                    // Only presume the SESSION dead when it has no other live
                    // socket (a reconnect opens a fresh socket while the zombie
                    // lingers). Another app on the same device, or another user
                    // on the same app and device, is not a sibling: it is a
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
            && string.Equals(a.Client, b.Client, StringComparison.OrdinalIgnoreCase)
            && a.UserId.Equals(b.UserId);

    /// <summary>
    /// The sessions a socket belongs to: same client, device id and user,
    /// the identity a Jellyfin 12 session is keyed by.
    /// </summary>
    /// <param name="sessions">The server's sessions.</param>
    /// <param name="client">The socket's client.</param>
    /// <param name="deviceId">The socket's device id.</param>
    /// <param name="userId">The socket's user.</param>
    /// <returns>The matching sessions.</returns>
    internal static IReadOnlyList<SessionInfo> SessionsOf(IEnumerable<SessionInfo> sessions, string client, string deviceId, Guid userId)
        => sessions
            .Where(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Client, client, StringComparison.OrdinalIgnoreCase)
                && s.UserId.Equals(userId))
            .ToList();

    private void Notify(Entry entry, bool dead)
    {
        foreach (var session in SessionsOf(_sessionManager.Sessions, entry.Client, entry.DeviceId, entry.UserId))
        {
            if (dead)
            {
                _logger.LogInformation("Device {DeviceId} ({Client}) stopped keep-aliving (socket not aborted by core); marking session {SessionId} disconnected for SyncPlay.", entry.DeviceId, entry.Client, session.Id);
                _markDisconnected(session);
            }
            else
            {
                _logger.LogInformation("Device {DeviceId} ({Client}) resumed keep-aliving; re-attaching session {SessionId}.", entry.DeviceId, entry.Client, session.Id);
                _reattach(session);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
