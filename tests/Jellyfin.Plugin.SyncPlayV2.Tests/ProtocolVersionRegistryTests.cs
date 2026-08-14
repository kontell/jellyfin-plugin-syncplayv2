using Jellyfin.Plugin.SyncPlayV2.Engine;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class ProtocolVersionRegistryTests
{
    [Fact]
    public void Unknown_device_defaults_to_v1()
    {
        var registry = new ProtocolVersionRegistry();

        Assert.Equal(1, registry.Resolve("web", "device-1"));
    }

    [Fact]
    public void Registered_version_is_resolved_for_the_same_identity()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", "device-1", 2);

        Assert.Equal(2, registry.Resolve("kofin", "device-1"));
        Assert.Equal(1, registry.Resolve("kofin", "device-2"));
        Assert.Equal(1, registry.Resolve("web", "device-1"));
    }

    [Fact]
    public void Identity_matching_is_case_insensitive()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("Kofin", "Device-1", 2);

        Assert.Equal(2, registry.Resolve("kofin", "device-1"));
    }

    [Fact]
    public void Later_registration_wins_including_downgrade_to_v1()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", "device-1", 2);
        registry.Register("kofin", "device-1", 1);

        Assert.Equal(1, registry.Resolve("kofin", "device-1"));
    }

    [Fact]
    public void Missing_device_id_is_never_registered()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", null, 2);
        registry.Register("kofin", string.Empty, 2);

        Assert.Equal(1, registry.Resolve("kofin", null));
        Assert.Equal(1, registry.Resolve("kofin", string.Empty));
    }
}
