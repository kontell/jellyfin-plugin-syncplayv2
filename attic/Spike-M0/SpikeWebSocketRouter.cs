using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// IWebSocketManager shadow (feasibility §4.6): Jellyfin's
/// WebSocketHandlerMiddleware routes EVERY WebSocket upgrade on any path to
/// IWebSocketManager, so a dedicated plugin socket requires owning this
/// service. This router claims GET /SyncPlayV2Spike/TimeSync for an NTP-style
/// echo loop and delegates everything else to the core WebSocketManager —
/// found by resolving IEnumerable&lt;IWebSocketManager&gt; and skipping itself,
/// which avoids referencing the unpublished Emby.Server.Implementations
/// assembly at compile time.
/// </summary>
public class SpikeWebSocketRouter : IWebSocketManager
{
    private static readonly PathString TimeSyncPath = new("/SyncPlayV2Spike/TimeSync");

    private readonly IServiceProvider _serviceProvider;
    private readonly IAuthService _authService;
    private readonly SpikeDiagnostics _diag;
    private readonly ILogger<SpikeWebSocketRouter> _logger;
    private readonly object _innerLock = new();
    private IWebSocketManager? _inner;

    public SpikeWebSocketRouter(
        IServiceProvider serviceProvider,
        IAuthService authService,
        SpikeDiagnostics diag,
        ILogger<SpikeWebSocketRouter> logger)
    {
        _serviceProvider = serviceProvider;
        _authService = authService;
        _diag = diag;
        _logger = logger;
    }

    public async Task WebSocketRequestHandler(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(TimeSyncPath))
        {
            await HandleTimeSyncSocket(context).ConfigureAwait(false);
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
            // Both registrations exist in the collection; ours shadows the
            // core one for single resolution, but the enumerable still yields
            // the core instance.
            _inner ??= _serviceProvider.GetServices<IWebSocketManager>()
                .First(m => !ReferenceEquals(m, this));
            _diag.Add("ws-router", $"delegating non-timesync sockets to {_inner.GetType().FullName}");
            return _inner;
        }
    }

    private async Task HandleTimeSyncSocket(HttpContext context)
    {
        var auth = await _authService.Authenticate(context.Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            _diag.Add("ws-timesync", $"unauthenticated upgrade from {context.Connection.RemoteIpAddress}");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        _diag.Add("ws-timesync", $"socket open for device {auth.DeviceId}");

        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await socket.ReceiveAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                var receivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                long t0 = 0;
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    if (doc.RootElement.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Number)
                    {
                        t0 = data.GetInt64();
                    }
                }
                catch (JsonException)
                {
                    // Tolerant by design: unknown frames are ignored.
                    continue;
                }

                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    MessageType = "TimeSync",
                    Data = new
                    {
                        T0 = t0,
                        T1 = receivedAt,
                        T2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                });
                await socket.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }

            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _diag.Add("ws-timesync", "socket idle-closed after 90s");
        }
        catch (WebSocketException ex)
        {
            _diag.Add("ws-timesync", $"socket error: {ex.Message}");
        }

        _diag.Add("ws-timesync", "socket closed");
    }
}
