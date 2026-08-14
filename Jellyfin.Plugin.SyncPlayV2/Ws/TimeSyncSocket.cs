using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
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

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        _logger.LogDebug("Time-sync socket open for device {DeviceId}.", auth.DeviceId);

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

                long t0;
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    if (!doc.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Number)
                    {
                        continue; // tolerant: unknown frames are ignored
                    }

                    t0 = data.GetInt64();
                }
                catch (JsonException)
                {
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

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Time-sync socket idle-closed.");
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("Time-sync socket error: {Message}", ex.Message);
        }
    }
}
