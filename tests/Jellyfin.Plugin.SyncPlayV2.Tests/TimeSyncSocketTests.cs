using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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

    [Fact]
    public async Task ChangingTheDeviceIdDoesNotGetPastTheCap()
    {
        // The fifth uses a new device id: the caller picks it, the token it cannot.
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

    [Fact]
    public async Task AUserDeniedSyncPlayGetsNoSocket()
    {
        // Hello requires SyncPlay access; the time-sync socket only checked
        // that the caller was authenticated.
        var handler = Handler();
        var denied = Request("token-1", "device-1", "none");

        await handler.WebSocketRequestHandler(denied).WaitAsync(Bound);

        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);

        var allowed = Hold(handler, "token-2", "device-1", "join");
        await allowed.Accepted.WaitAsync(Bound);
        allowed.Close();
        await allowed.Done.WaitAsync(Bound);
    }

    [Fact]
    public async Task AUserStillActiveInAGroupKeepsItsSocketAfterAccessIsDenied()
    {
        // The policy's own exception: access revoked mid-session does not
        // cut off a user who is still in a group.
        var handler = new TimeSyncSocket(ActiveUsers.Create(), FakeAuth.Create(), NullLogger<TimeSyncSocket>.Instance);

        var active = Hold(handler, "token-1", "device-1", "none");
        await active.Accepted.WaitAsync(Bound);
        active.Close();
        await active.Done.WaitAsync(Bound);
    }

    private static TimeSyncSocket Handler()
        => new(NoServices.Create(), FakeAuth.Create(), NullLogger<TimeSyncSocket>.Instance);

    private static DefaultHttpContext Request(string token, string deviceId, string? access = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/SyncPlay/TimeSync";
        context.Request.QueryString = new QueryString("?token=" + token + "&device=" + deviceId + (access is null ? string.Empty : "&access=" + access));
        return context;
    }

    private static HeldConnection Hold(TimeSyncSocket handler, string token, string deviceId, string? access = null)
    {
        var socket = new HeldSocket();
        var context = Request(token, deviceId, access);
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

/// <summary>
/// An IAuthService that authenticates the token and device named in the
/// query, and, with <c>access=none|join</c>, a user with that SyncPlay access.
/// </summary>
public class FakeAuth : DispatchProxy
{
    public static IAuthService Create() => Create<IAuthService, FakeAuth>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var request = (HttpRequest)args![0]!;
        var access = request.Query["access"].ToString();
        return Task.FromResult(new AuthorizationInfo
        {
            IsAuthenticated = true,
            Token = request.Query["token"].ToString(),
            DeviceId = request.Query["device"].ToString(),
            Client = "harness",
            User = access.Length == 0 ? null! : new User("harness-user", "Default", "Default")
            {
                SyncPlayAccess = access == "none" ? SyncPlayUserAccessType.None : SyncPlayUserAccessType.JoinGroups,
            },
        });
    }
}

/// <summary>An IServiceProvider the time-sync path never asks.</summary>
public class NoServices : DispatchProxy
{
    public static IServiceProvider Create() => Create<IServiceProvider, NoServices>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
}

/// <summary>An IServiceProvider whose ISyncPlayManager says every user is active in a group.</summary>
public class ActiveUsers : DispatchProxy
{
    public static IServiceProvider Create() => Create<IServiceProvider, ActiveUsers>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => (Type)args![0]! == typeof(MediaBrowser.Controller.SyncPlay.ISyncPlayManager) ? EveryoneActive.Create() : null;
}

/// <summary>An ISyncPlayManager that answers IsUserActive with true and nothing else.</summary>
public class EveryoneActive : DispatchProxy
{
    public static MediaBrowser.Controller.SyncPlay.ISyncPlayManager Create()
        => Create<MediaBrowser.Controller.SyncPlay.ISyncPlayManager, EveryoneActive>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name == "IsUserActive" ? true : throw new NotSupportedException(targetMethod.Name);
}
