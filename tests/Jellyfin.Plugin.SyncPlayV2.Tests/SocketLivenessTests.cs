using System.Linq;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class SocketLivenessTests
{
    private static SessionInfo Session(string id, string client, string deviceId)
        => new(null, null) { Id = id, Client = client, DeviceId = deviceId };

    [Fact]
    public void ADeadSocketIsTheSessionOfItsOwnClientNotTheFirstOnTheDevice()
    {
        // Two apps on one device each have a session under the same device
        // id. The first match used to be taken, so the wrong one could be
        // marked disconnected while the dead one stayed attached.
        var sessions = new[]
        {
            Session("kodi", "Kofin", "device-1"),
            Session("web", "Jellyfin Web", "device-1"),
        };

        var matched = SocketLiveness.SessionsOf(sessions, "Jellyfin Web", "device-1");

        Assert.Equal("web", Assert.Single(matched).Id);
    }

    [Fact]
    public void ClientAndDeviceMatchIgnoringCase()
    {
        var sessions = new[] { Session("web", "Jellyfin Web", "Device-1") };

        Assert.Single(SocketLiveness.SessionsOf(sessions, "jellyfin web", "device-1"));
    }

    [Fact]
    public void ASocketThatDidNotNameItsClientMatchesEverySessionOfItsDevice()
    {
        var sessions = new[]
        {
            Session("kodi", "Kofin", "device-1"),
            Session("web", "Jellyfin Web", "device-1"),
            Session("other", "Jellyfin Web", "device-2"),
        };

        var matched = SocketLiveness.SessionsOf(sessions, null, "device-1");

        Assert.Equal(new[] { "kodi", "web" }, matched.Select(s => s.Id).OrderBy(id => id));
    }
}
