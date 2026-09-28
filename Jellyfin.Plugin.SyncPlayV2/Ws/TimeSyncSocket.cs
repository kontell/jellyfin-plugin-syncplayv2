using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Ws;

/// <summary>
/// IWebSocketManager shadow (M0-proven): Jellyfin's WebSocketHandlerMiddleware
/// routes EVERY WebSocket upgrade on any path here, so the dedicated time-sync
/// socket requires owning this service. GET /SyncPlay/TimeSync (advertised by
/// Hello) gets an NTP echo loop measuring the channel commands travel on
/// (spec §3 v2); everything else delegates to the core WebSocketManager,
/// found by resolving IEnumerable&lt;IWebSocketManager&gt; and skipping self —
/// no compile reference to the unpublished server assembly.
/// </summary>
public class TimeSyncSocket : IWebSocketManager
{
    private static readonly PathString Path = new("/SyncPlay/TimeSync");

    /// <summary>How long a socket may stay silent before it is closed.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a reply or a close may take. A client that stops reading
    /// fills the send buffer, and a send with no deadline then never returns,
    /// holding the connection past the idle timeout, which only covers receives.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Concurrent time-sync sockets per client and device.</summary>
    private const int SocketsPerDevice = 4;

    private readonly ConnectionLimiter _limiter = new(SocketsPerDevice);

    private readonly IServiceProvider _serviceProvider;
    private readonly IAuthService _authService;
    private readonly ILogger<TimeSyncSocket> _logger;
    private readonly object _innerLock = new();
    private IWebSocketManager? _inner;

    public TimeSyncSocket(IServiceProvider serviceProvider, IAuthService authService, ILogger<TimeSyncSocket> logger)
    {
        _serviceProvider = serviceProvider;
        _authService = authService;
        _logger = logger;
    }

    public async Task WebSocketRequestHandler(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(Path))
        {
            await HandleTimeSync(context).ConfigureAwait(false);
            return;
        }

        await Inner().WebSocketRequestHandler(context).ConfigureAwait(false);
    }

    private IWebSocketManager Inner()
    {
        if (_inner is not null)
        {
            return _inner;
        }

        lock (_innerLock)
        {
            // Both registrations remain in the collection; ours shadows the
            // core one for single resolution, the enumerable yields both.
            return _inner ??= _serviceProvider.GetServices<IWebSocketManager>()
                .First(m => !ReferenceEquals(m, this));
        }
    }

    private async Task HandleTimeSync(HttpContext context)
    {
        var auth = await _authService.Authenticate(context.Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var key = auth.Client + "|" + auth.DeviceId;
        if (!_limiter.TryEnter(key))
        {
            _logger.LogWarning("Refusing a time-sync socket for device {DeviceId} ({Client}): {Limit} already open.", auth.DeviceId, auth.Client, SocketsPerDevice);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            _logger.LogDebug("Time-sync socket open for device {DeviceId}.", auth.DeviceId);
            await Serve(socket).ConfigureAwait(false);
        }
        finally
        {
            _limiter.Exit(key);
        }
    }

    private async Task Serve(WebSocket socket)
    {
        var buffer = new byte[1024];
        var assembler = new MessageAssembler(TimeSyncProtocol.MaxMessageBytes);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                using (var idle = new CancellationTokenSource(IdleTimeout))
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                }

                var receivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                // Tolerant: incomplete, oversized and unknown messages are ignored.
                if (!assembler.Append(buffer.AsSpan(0, result.Count), result.EndOfMessage, out var message)
                    || !TimeSyncProtocol.TryReadT0(message, out var t0))
                {
                    continue;
                }

                var reply = TimeSyncProtocol.Reply(t0, receivedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                using var send = new CancellationTokenSource(SendTimeout);
                await socket.SendAsync(reply, WebSocketMessageType.Text, true, send.Token).ConfigureAwait(false);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(SendTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, close.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Time-sync socket idle-closed or stopped reading.");
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("Time-sync socket error: {Message}", ex.Message);
        }
    }
}
