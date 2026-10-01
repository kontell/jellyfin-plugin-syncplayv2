using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The liveness sweep, driven directly against a clock the test moves: which
/// session a quiet socket takes down, and which it must leave alone.
/// </summary>
public class SocketLivenessTests
{
    private const string Web = "Jellyfin Web";
    private const string Kodi = "Kofin";

    // AuthorizationInfo.UserId is read from its User; one per name, so a
    // socket and a session of the same user carry the same id.
    private static readonly object AliceUser = NewUser("alice");
    private static readonly object BobUser = NewUser("bob");
    private static readonly Guid Alice = UserIdOf(AliceUser);
    private static readonly Guid Bob = UserIdOf(BobUser);

    private readonly List<SessionInfo> _sessions = new();
    private readonly List<string> _disconnected = new();
    private readonly List<string> _reattached = new();
    private DateTime _now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ADeadSocketTakesDownItsOwnAppNotTheOtherOneOnTheDevice()
    {
        // Two apps on one device each have a session under the same device
        // id. The first match used to be taken, so the live app's member
        // could be marked disconnected while the dead one stayed attached.
        var liveness = Liveness();
        Session("kodi", Kodi, "device-1", Alice);
        Session("web", Web, "device-1", Alice);
        var web = Connect(liveness, Web, "device-1", Alice);
        var kodi = Connect(liveness, Kodi, "device-1", Alice);

        Advance(TimeSpan.FromSeconds(61));
        kodi.KeepAlive(_now);
        liveness.Sweep();

        Assert.Equal(new[] { "web" }, _disconnected);
    }

    [Fact]
    public void AnotherUserOnTheSameAppAndDeviceIsNotASibling()
    {
        // Jellyfin 12 keys a session by client, device id AND user: two users
        // signed in to the same app on the same device are two sessions. The
        // live one's socket used to count as the dead one's sibling, so the
        // dead session was never marked.
        var liveness = Liveness();
        Session("alice", Web, "device-1", Alice);
        Session("bob", Web, "device-1", Bob);
        Connect(liveness, Web, "device-1", Alice);
        var bob = Connect(liveness, Web, "device-1", Bob);

        Advance(TimeSpan.FromSeconds(61));
        bob.KeepAlive(_now);
        liveness.Sweep();

        Assert.Equal(new[] { "alice" }, _disconnected);
    }

    [Fact]
    public void AReconnectsOldSocketDoesNotTakeTheSessionDown()
    {
        var liveness = Liveness();
        Session("web", Web, "device-1", Alice);
        Connect(liveness, Web, "device-1", Alice);
        Advance(TimeSpan.FromSeconds(30));
        var fresh = Connect(liveness, Web, "device-1", Alice);

        Advance(TimeSpan.FromSeconds(31));
        fresh.KeepAlive(_now);
        liveness.Sweep();

        Assert.Empty(_disconnected);
    }

    [Fact]
    public void KeepAlivesResumingReattachOnlyTheirOwnSession()
    {
        var liveness = Liveness();
        Session("alice", Web, "device-1", Alice);
        Session("bob", Web, "device-1", Bob);
        var alice = Connect(liveness, Web, "device-1", Alice);
        Connect(liveness, Web, "device-1", Bob);
        Advance(TimeSpan.FromSeconds(61));
        liveness.Sweep();
        Assert.Equal(new[] { "alice", "bob" }, _disconnected.OrderBy(id => id));

        alice.KeepAlive(_now);
        liveness.Sweep();

        Assert.Equal(new[] { "alice" }, _reattached);
    }

    [Fact]
    public void AZombieIsReportedOnceTheLiveSocketBesideItCloses()
    {
        // A reconnect leaves the old socket open but quiet. While the new one
        // lives the session does; once it closes, only the zombie is left.
        var liveness = Liveness();
        Session("web", Web, "device-1", Alice);
        Connect(liveness, Web, "device-1", Alice);
        Advance(TimeSpan.FromSeconds(30));
        var fresh = Connect(liveness, Web, "device-1", Alice);
        Advance(TimeSpan.FromSeconds(31));
        fresh.KeepAlive(_now);
        liveness.Sweep();
        Assert.Empty(_disconnected);

        fresh.Close();
        liveness.Sweep();
        liveness.Sweep();

        Assert.Equal(new[] { "web" }, _disconnected);
    }

    [Fact]
    public void ANewSocketReattachesADeadSessionOnce()
    {
        // The new socket re-attaches it at once (Jellyfin's own re-attach may
        // have run before the dead report); the sweep does not do it again.
        var liveness = Liveness();
        Session("web", Web, "device-1", Alice);
        Connect(liveness, Web, "device-1", Alice);
        Advance(TimeSpan.FromSeconds(61));
        liveness.Sweep();
        Assert.Equal(new[] { "web" }, _disconnected);

        Connect(liveness, Web, "device-1", Alice);
        liveness.Sweep();

        Assert.Equal(new[] { "web" }, _reattached);
    }

    [Fact]
    public void ASocketDeadForLongIsNoLongerWatched()
    {
        var liveness = Liveness();
        Session("web", Web, "device-1", Alice);
        Connect(liveness, Web, "device-1", Alice);
        Advance(TimeSpan.FromSeconds(61));
        liveness.Sweep();
        Assert.Equal(1, liveness.WatchedSockets);

        Advance(TimeSpan.FromMinutes(10));
        liveness.Sweep();

        Assert.Equal(0, liveness.WatchedSockets);
        Assert.Equal(new[] { "web" }, _disconnected);
    }

    [Fact]
    public void ASocketThatCannotBeTiedToOneSessionIsNotWatched()
    {
        // With no client to go by, "every session of the device" is the only
        // match, and that is how the wrong app gets disconnected.
        // A watched socket subscribes to Closed; these must not be watched
        // at all, not merely match no session.
        var liveness = Liveness();
        Session("kodi", Kodi, "device-1", Alice);
        Session("web", Web, "device-1", Alice);
        var sockets = new[]
        {
            Connect(liveness, null, "device-1", Alice),
            Connect(liveness, string.Empty, "device-1", Alice),
            Connect(liveness, Web, null, Alice),
            Connect(liveness, Web, string.Empty, Alice),
        };

        Advance(TimeSpan.FromSeconds(61));
        liveness.Sweep();

        Assert.Empty(_disconnected);
        Assert.All(sockets, socket => Assert.Equal(0, socket.ClosedSubscriptions));
        Assert.Equal(1, Connect(liveness, Web, "device-1", Alice).ClosedSubscriptions);
    }

    [Fact]
    public void ClientAndDeviceMatchIgnoringCase()
    {
        var sessions = new[] { new SessionInfo(null, null) { Id = "web", Client = Web, DeviceId = "Device-1", UserId = Alice } };

        Assert.Single(SocketLiveness.SessionsOf(sessions, "jellyfin web", "device-1", Alice));
        Assert.Empty(SocketLiveness.SessionsOf(sessions, "jellyfin web", "device-1", Bob));
    }

    private SocketLiveness Liveness()
        => new(
            SessionList.Create(_sessions),
            session => _disconnected.Add(session.Id),
            session => _reattached.Add(session.Id),
            NullLogger<SocketLiveness>.Instance,
            () => _now,
            startTimer: false);

    private void Session(string id, string client, string deviceId, Guid userId)
        => _sessions.Add(new SessionInfo(null, null) { Id = id, Client = client, DeviceId = deviceId, UserId = userId });

    private FakeSocket Connect(SocketLiveness liveness, string? client, string? deviceId, Guid userId)
    {
        var auth = new AuthorizationInfo { Client = client!, DeviceId = deviceId! };
        UserProperty.SetValue(auth, userId == Alice ? AliceUser
            : userId == Bob ? BobUser
            : throw new ArgumentOutOfRangeException(nameof(userId), "Only Alice and Bob have users here."));
        var socket = FakeSocket.Create(auth, _now);
        liveness.ProcessWebSocketConnectedAsync((IWebSocketConnection)(object)socket, null!).GetAwaiter().GetResult();
        return socket;
    }

    private void Advance(TimeSpan by) => _now += by;

    // By reflection: the user entity's namespace differs between the
    // Jellyfin ABIs this suite builds against.
    private static PropertyInfo UserProperty => typeof(AuthorizationInfo).GetProperty(nameof(AuthorizationInfo.User))!;

    private static object NewUser(string name)
        => Activator.CreateInstance(UserProperty.PropertyType, name, "Default", "Default")!;

    private static Guid UserIdOf(object user)
    {
        var auth = new AuthorizationInfo();
        UserProperty.SetValue(auth, user);
        return auth.UserId;
    }
}

/// <summary>An ISessionManager whose Sessions is the given list.</summary>
public class SessionList : DispatchProxy
{
    private IEnumerable<SessionInfo> _sessions = Array.Empty<SessionInfo>();

    public static ISessionManager Create(IEnumerable<SessionInfo> sessions)
    {
        var proxy = Create<ISessionManager, SessionList>();
        ((SessionList)(object)proxy)._sessions = sessions;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name == "get_Sessions" ? _sessions : throw new NotSupportedException(targetMethod.Name);
}

/// <summary>An open IWebSocketConnection whose activity dates the test sets.</summary>
public class FakeSocket : DispatchProxy
{
    private AuthorizationInfo _auth = new();
    private DateTime _lastActivity;
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>Gets how many handlers subscribed to Closed: 1 when watched.</summary>
    public int ClosedSubscriptions { get; private set; }

    public static FakeSocket Create(AuthorizationInfo auth, DateTime connectedAt)
    {
        var proxy = (FakeSocket)(object)Create<IWebSocketConnection, FakeSocket>();
        proxy._auth = auth;
        proxy._lastActivity = connectedAt;
        return proxy;
    }

    public void KeepAlive(DateTime at) => _lastActivity = at;

    public void Close() => _state = WebSocketState.Closed;

    private object? Subscribed()
    {
        ClosedSubscriptions++;
        return null;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name switch
        {
            "get_AuthorizationInfo" => _auth,
            "get_State" => _state,
            "get_LastActivityDate" => _lastActivity,
            "get_LastKeepAliveDate" => _lastActivity,
            "add_Closed" => Subscribed(),
            "remove_Closed" => null,
            _ => throw new NotSupportedException(targetMethod.Name),
        };
}
