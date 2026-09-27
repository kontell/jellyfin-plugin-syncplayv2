using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Ws;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The time-sync socket's admission, through the real request handler: what
/// the per-token cap is keyed by, and that a slot is always given back.
/// </summary>
public class TimeSyncSocketTests
{
    [Fact]
    public async Task ChangingTheDeviceIdDoesNotGetPastTheCap()
    {
        // Client and device id come from the caller's own Authorization
        // header; keyed on them, a caller got four more sockets per made-up
        // device id. The cap is on the token the server validated.
        var handler = Handler();
        var held = Enumerable.Range(1, 4).Select(i => Hold(handler, "token-1", "device-" + i)).ToList();
        await Task.WhenAll(held.Select(h => h.Accepted));

        var fifth = Request("token-1", "device-5");
        await handler.WebSocketRequestHandler(fifth);

        Assert.Equal(StatusCodes.Status429TooManyRequests, fifth.Response.StatusCode);

        var otherToken = Hold(handler, "token-2", "device-1");
        await otherToken.Accepted;
        otherToken.Close();
        await otherToken.Done;

        held.ForEach(h => h.Close());
        await Task.WhenAll(held.Select(h => h.Done));
    }

    [Fact]
    public async Task AFailedUpgradeGivesItsSlotBack()
    {
        // No WebSocket feature on the request: accepting throws after the
        // slot was taken. Every attempt must fail the same way; a leaked slot
        // turns the fifth into a 429 instead.
        var handler = Handler();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var context = Request("token-1", "device-1");
            await Assert.ThrowsAnyAsync<Exception>(() => handler.WebSocketRequestHandler(context));
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task AClosedSocketGivesItsSlotBack()
    {
        var handler = Handler();
        for (var round = 0; round < 3; round++)
        {
            var held = Enumerable.Range(1, 4).Select(_ => Hold(handler, "token-1", "device-1")).ToList();
            await Task.WhenAll(held.Select(h => h.Accepted));
            held.ForEach(h => h.Close());
            await Task.WhenAll(held.Select(h => h.Done));
        }
    }

    private static TimeSyncSocket Handler()
        => new(NoServices.Create(), FakeAuth.Create(), NullLogger<TimeSyncSocket>.Instance);

    private static DefaultHttpContext Request(string token, string deviceId)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/SyncPlay/TimeSync";
        context.Request.QueryString = new QueryString("?token=" + token + "&device=" + deviceId);
        return context;
    }

    private static HeldConnection Hold(TimeSyncSocket handler, string token, string deviceId)
    {
        var socket = new HeldSocket();
        var context = Request(token, deviceId);
        context.Features.Set<IHttpWebSocketFeature>(new AcceptingFeature(socket));
        var done = handler.WebSocketRequestHandler(context);
        return new HeldConnection(socket, done, context);
    }

    private sealed record HeldConnection(HeldSocket Socket, Task Done, HttpContext Context)
    {
        public Task Accepted => Task.WhenAny(Socket.Receiving, Done).ContinueWith(_ =>
            Assert.True(Socket.Receiving.IsCompleted, "refused with " + Context.Response.StatusCode));

        public void Close() => Socket.Close();
    }

    private sealed class AcceptingFeature : IHttpWebSocketFeature
    {
        private readonly WebSocket _socket;

        public AcceptingFeature(WebSocket socket) => _socket = socket;

        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
    }

    /// <summary>An open socket that waits for a message until the test closes it.</summary>
    private sealed class HeldSocket : WebSocket
    {
        private readonly TaskCompletionSource _receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebSocketReceiveResult> _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public Task Receiving => _receiving.Task;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public void Close() => _close.TrySetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            _receiving.TrySetResult();
            cancellationToken.Register(() => _close.TrySetCanceled());
            return _close.Task.ContinueWith(
                t =>
                {
                    _state = WebSocketState.CloseReceived;
                    return t.Result;
                },
                TaskScheduler.Default);
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose()
        {
        }
    }
}

/// <summary>An IAuthService that authenticates the token and device named in the query.</summary>
public class FakeAuth : DispatchProxy
{
    public static IAuthService Create() => Create<IAuthService, FakeAuth>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var request = (HttpRequest)args![0]!;
        return Task.FromResult(new AuthorizationInfo
        {
            IsAuthenticated = true,
            Token = request.Query["token"].ToString(),
            DeviceId = request.Query["device"].ToString(),
            Client = "harness",
        });
    }
}

/// <summary>An IServiceProvider the time-sync path never asks.</summary>
public class NoServices : DispatchProxy
{
    public static IServiceProvider Create() => Create<IServiceProvider, NoServices>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
}
