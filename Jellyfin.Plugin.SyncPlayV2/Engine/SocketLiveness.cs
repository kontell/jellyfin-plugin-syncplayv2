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

    /// <summary>
    /// How long a socket reported dead is still watched. A zombie can stay
    /// open long after its client left; by then its member has left the group.
    /// </summary>
    private static readonly TimeSpan ForgetDeadAfter = TimeSpan.FromMinutes(10);

    private readonly ISessionManager _sessionManager;
    private readonly Action<SessionInfo> _markDisconnected;
    private readonly Action<SessionInfo> _reattach;
    private readonly ILogger<SocketLiveness> _logger;
    private readonly Func<DateTime> _now;
    private readonly EngineCounters _counters;
    private readonly ConcurrentDictionary<IWebSocketConnection, Entry> _sockets = new();

    /// <summary>
    /// The sessions (client, device, user) reported disconnected and not yet
    /// alive again. Liveness is a session's, not a socket's: its sockets come
    /// and go, and the session is dead only while none of them is alive.
    /// </summary>
    private readonly ConcurrentDictionary<(string Client, string DeviceId, Guid UserId), byte> _deadSessions = new();

    /// <summary>
    /// Serializes the sweep with a new socket's arrival: a sweep that found a
    /// session with no live socket must not report it dead after a new socket
    /// for it has arrived and cleared its mark.
    /// </summary>
    private readonly Lock _gate = new();
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
            SessionKey = (client.ToUpperInvariant(), deviceId.ToUpperInvariant(), userId);
        }

        public string Client { get; }

        public string DeviceId { get; }

        public Guid UserId { get; }

        public DateTime ConnectedAt { get; }

        /// <summary>Gets the session the socket belongs to; client and device compare ignoring case.</summary>
        public (string Client, string DeviceId, Guid UserId) SessionKey { get; }

        /// <summary>Gets or sets when the socket went quiet; null while it is alive.</summary>
        public DateTime? DeadSince { get; set; }
    }

    /// <summary>Gets how many sockets are watched, for tests.</summary>
    internal int WatchedSockets => _sockets.Count;

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
            var entry = new Entry(auth.Client, auth.DeviceId, auth.UserId, _now());
            lock (_gate)
            {
                _sockets[connection] = entry;

                // The session is alive again. Jellyfin re-attaches it on
                // SessionControllerConnected, which can run before this; one
                // the sweep reported dead is re-attached here as well, so a
                // report that came after Jellyfin's re-attach is undone.
                if (_deadSessions.TryRemove(entry.SessionKey, out _))
                {
                    Notify(entry, dead: false);
                }
            }

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
        // Timer callbacks can overlap; two sweeps would both report (and
        // count) one death.
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                SweepUnderGate();
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

    private void SweepUnderGate()
    {
        var now = _now();
        var sessions = new Dictionary<(string Client, string DeviceId, Guid UserId), (Entry Entry, bool Alive)>();
        foreach (var (connection, entry) in _sockets)
        {
            if (connection.State is not WebSocketState.Open and not WebSocketState.Connecting)
            {
                _sockets.TryRemove(connection, out _);
                continue;
            }

            var stale = IsStale(connection, entry);
            if (stale && entry.DeadSince is null)
            {
                // A dead socket is counted once, whether or not another
                // socket keeps its session alive.
                entry.DeadSince = now;
                _counters.ZombieSocket();
            }
            else if (!stale)
            {
                entry.DeadSince = null;
            }

            if (entry.DeadSince is { } since && now - since >= ForgetDeadAfter)
            {
                _sockets.TryRemove(connection, out _);
                continue;
            }

            // Another app on the same device, or another user on the same
            // app and device, is a different session: alive or not, its
            // sockets say nothing of this one.
            var alive = !stale || (sessions.TryGetValue(entry.SessionKey, out var seen) && seen.Alive);
            sessions[entry.SessionKey] = (entry, alive);
        }

        foreach (var (key, (entry, alive)) in sessions)
        {
            if (!alive && _deadSessions.TryAdd(key, 0))
            {
                Notify(entry, dead: true);
            }
            else if (alive && _deadSessions.TryRemove(key, out _))
            {
                // Keep-alives resumed on a socket presumed dead.
                Notify(entry, dead: false);
            }
        }

        // A session none of whose sockets is watched any more is no longer
        // this sweep's to report.
        foreach (var key in _deadSessions.Keys)
        {
            if (!sessions.ContainsKey(key))
            {
                _deadSessions.TryRemove(key, out _);
            }
        }
    }

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
