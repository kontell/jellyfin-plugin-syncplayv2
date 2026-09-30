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
    // Every wait on the handler is bounded: a regression that blocks it
    // fails the test instead of hanging the suite.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>Verifies that changing device IDs cannot bypass the token cap and another token is still admitted.</summary>
    [Fact]
    public async Task ChangingTheDeviceIdDoesNotGetPastTheCap()
    {
        // Client and device id come from the caller's own Authorization
        // header; keyed on them, a caller got four more sockets per made-up
        // device id. The cap is on the token the server validated.
        var handler = Handler();
        var held = Enumerable.Range(1, 4).Select(i => Hold(handler, "token-1", "device-" + i)).ToList();
        await Task.WhenAll(held.Select(h => h.Accepted)).WaitAsync(Bound);

        var fifth = Request("token-1", "device-5");
        await handler.WebSocketRequestHandler(fifth).WaitAsync(Bound);

        Assert.Equal(StatusCodes.Status429TooManyRequests, fifth.Response.StatusCode);

        var otherToken = Hold(handler, "token-2", "device-1");
        await otherToken.Accepted.WaitAsync(Bound);
        otherToken.Close();
        await otherToken.Done.WaitAsync(Bound);

        held.ForEach(h => h.Close());
        await Task.WhenAll(held.Select(h => h.Done)).WaitAsync(Bound);
    }

    /// <summary>Verifies that repeated failed WebSocket upgrades release their reserved slots.</summary>
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
            await Assert.ThrowsAnyAsync<Exception>(() => handler.WebSocketRequestHandler(context)).WaitAsync(Bound);
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        }
    }

    /// <summary>Verifies that closing sockets frees all four token slots for subsequent connections.</summary>
    [Fact]
    public async Task AClosedSocketGivesItsSlotBack()
    {
        var handler = Handler();
        for (var round = 0; round < 3; round++)
        {
            var held = Enumerable.Range(1, 4).Select(_ => Hold(handler, "token-1", "device-1")).ToList();
            await Task.WhenAll(held.Select(h => h.Accepted)).WaitAsync(Bound);
            held.ForEach(h => h.Close());
            await Task.WhenAll(held.Select(h => h.Done)).WaitAsync(Bound);
        }
    }

    /// <summary>Creates a time-sync handler with query-based authentication and no backing services.</summary>
    private static TimeSyncSocket Handler()
        => new(NoServices.Create(), FakeAuth.Create(), NullLogger<TimeSyncSocket>.Instance);

    /// <summary>Creates a time-sync request carrying the token and device ID consumed by the fake authenticator.</summary>
    private static DefaultHttpContext Request(string token, string deviceId)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/SyncPlay/TimeSync";
        context.Request.QueryString = new QueryString("?token=" + token + "&device=" + deviceId);
        return context;
    }

    /// <summary>Starts a request with a fake socket that stays open until the test closes it.</summary>
    private static HeldConnection Hold(TimeSyncSocket handler, string token, string deviceId)
    {
        var socket = new HeldSocket();
        var context = Request(token, deviceId);
        context.Features.Set<IHttpWebSocketFeature>(new AcceptingFeature(socket));
        var done = handler.WebSocketRequestHandler(context);
        return new HeldConnection(socket, done, context);
    }

    /// <summary>Tracks a held socket, its handler task, and the request used to assert admission.</summary>
    private sealed record HeldConnection(HeldSocket Socket, Task Done, HttpContext Context)
    {
        /// <summary>Gets a task that asserts the handler reached socket receive instead of refusing the request.</summary>
        public Task Accepted => Task.WhenAny(Socket.Receiving, Done).ContinueWith(_ =>
            Assert.True(Socket.Receiving.IsCompleted, "refused with " + Context.Response.StatusCode));

        /// <summary>Signals a peer close frame so the handler can finish and release its slot.</summary>
        public void Close() => Socket.Close();
    }

    private sealed class AcceptingFeature : IHttpWebSocketFeature
    {
        private readonly WebSocket _socket;

        /// <summary>Initializes a new instance of the <see cref="AcceptingFeature"/> class with the socket to accept.</summary>
        public AcceptingFeature(WebSocket socket) => _socket = socket;

        /// <summary>Gets a value indicating whether the fake feature accepts WebSocket upgrades; always true.</summary>
        public bool IsWebSocketRequest => true;

        /// <summary>Completes the upgrade by returning the socket supplied by the test.</summary>
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
    }

    /// <summary>An open socket that waits for a message until the test closes it.</summary>
    private sealed class HeldSocket : WebSocket
    {
        private readonly TaskCompletionSource _receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebSocketReceiveResult> _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Gets a task that completes when the handler starts receiving from the socket.</summary>
        public Task Receiving => _receiving.Task;

        /// <summary>Gets the close status; this fake does not record one.</summary>
        public override WebSocketCloseStatus? CloseStatus => null;

        /// <summary>Gets the close description; this fake does not record one.</summary>
        public override string? CloseStatusDescription => null;

        /// <summary>Gets the state tracked by the fake receive, close, and abort operations.</summary>
        public override WebSocketState State => _state;

        /// <summary>Gets the negotiated subprotocol; this fake does not negotiate one.</summary>
        public override string? SubProtocol => null;

        /// <summary>Completes the pending receive with a peer close frame.</summary>
        public void Close() => _close.TrySetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        /// <summary>Signals that receiving has started and waits for a simulated peer close or cancellation.</summary>
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

        /// <summary>Marks the fake socket closed without performing a network handshake.</summary>
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        /// <summary>Closes the fake socket using the same state transition as <see cref="CloseAsync"/>.</summary>
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        /// <summary>Completes a send without recording or transmitting the supplied data.</summary>
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>Marks the fake socket as aborted.</summary>
        public override void Abort() => _state = WebSocketState.Aborted;

        /// <summary>Performs no cleanup because the fake owns no network resources.</summary>
        public override void Dispose()
        {
        }
    }
}

/// <summary>An IAuthService that authenticates the token and device named in the query.</summary>
public class FakeAuth : DispatchProxy
{
    /// <summary>Creates an authentication proxy that reads credentials from the test request query.</summary>
    public static IAuthService Create() => Create<IAuthService, FakeAuth>();

    /// <summary>Returns authenticated authorization info using the request query token and device ID.</summary>
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
    /// <summary>Creates a service provider proxy that returns null for every service request.</summary>
    public static IServiceProvider Create() => Create<IServiceProvider, NoServices>();

    /// <summary>Returns null for every proxied service lookup.</summary>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
}
