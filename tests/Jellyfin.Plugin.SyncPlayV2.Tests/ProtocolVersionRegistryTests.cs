using System;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class ProtocolVersionRegistryTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void Unknown_device_defaults_to_v1()
    {
        var registry = new ProtocolVersionRegistry();

        Assert.Equal(1, registry.Resolve("web", "device-1", User));
    }

    [Fact]
    public void Registered_version_is_resolved_for_the_same_identity()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", "device-1", User, 2);

        Assert.Equal(2, registry.Resolve("kofin", "device-1", User));
        Assert.Equal(1, registry.Resolve("kofin", "device-2", User));
        Assert.Equal(1, registry.Resolve("web", "device-1", User));
    }

    [Fact]
    public void Identity_matching_is_case_insensitive()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("Kofin", "Device-1", User, 2);

        Assert.Equal(2, registry.Resolve("kofin", "device-1", User));
    }

    [Fact]
    public void Later_registration_wins_including_downgrade_to_v1()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", "device-1", User, 2);
        registry.Register("kofin", "device-1", User, 1);

        Assert.Equal(1, registry.Resolve("kofin", "device-1", User));
    }

    [Fact]
    public void A_client_asking_for_more_than_the_server_speaks_negotiates_the_server_version()
    {
        // Today every check is ">= 2", so a stored 3 behaves like a 2 — until
        // the day a v3 feature is gated on ">= 3" and a v3 client talking to
        // this v2 server is sent messages the server cannot produce correctly.
        var registry = new ProtocolVersionRegistry();

        registry.RegisterHello("kofin", "device-1", User, 3, externalContent: false);

        Assert.Equal(ProtocolVersionRegistry.ServerVersion, registry.Resolve("kofin", "device-1", User));
    }

    [Fact]
    public void A_version_below_one_is_a_v1_client()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", "device-1", User, 0);
        registry.Register("web", "device-2", User, -7);

        Assert.Equal(1, registry.Resolve("kofin", "device-1", User));
        Assert.Equal(1, registry.Resolve("web", "device-2", User));
    }

    [Fact]
    public void Missing_device_id_is_never_registered()
    {
        var registry = new ProtocolVersionRegistry();

        registry.Register("kofin", null, User, 2);
        registry.Register("kofin", string.Empty, User, 2);

        Assert.Equal(1, registry.Resolve("kofin", null, User));
        Assert.Equal(1, registry.Resolve("kofin", string.Empty, User));
    }
}

public class RegistryCapabilityTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void AHelloDeclarationSticksAndDefaultsOff()
    {
        var registry = new ProtocolVersionRegistry();

        Assert.False(registry.HasExternalContent("kofin", "dev-1", User));

        registry.RegisterHello("kofin", "dev-1", User, 2, externalContent: true);

        Assert.True(registry.HasExternalContent("kofin", "dev-1", User));
        Assert.Equal(2, registry.Resolve("kofin", "dev-1", User));
    }

    [Fact]
    public void ASnifferWriteMustNotWipeTheCapability()
    {
        // The body sniffer re-registers the version on every stock Join/New,
        // moments after the Hello that declared the capability — a plain
        // version write preserves the declaration.
        var registry = new ProtocolVersionRegistry();
        registry.RegisterHello("kofin", "dev-1", User, 2, externalContent: true);

        registry.Register("kofin", "dev-1", User, 2);

        Assert.True(registry.HasExternalContent("kofin", "dev-1", User));
    }

    [Fact]
    public void TheNextHelloWinsWithdrawalsIncluded()
    {
        var registry = new ProtocolVersionRegistry();
        registry.RegisterHello("kofin", "dev-1", User, 2, externalContent: true);

        registry.RegisterHello("kofin", "dev-1", User, 2, externalContent: false);

        Assert.False(registry.HasExternalContent("kofin", "dev-1", User));
    }

    [Fact]
    public void Two_users_of_one_app_on_one_device_negotiate_apart()
    {
        // Two users of one app on one device are two Jellyfin 12 sessions:
        // one's Hello must not make the other a v2 member, nor declare
        // capabilities for it.
        var registry = new ProtocolVersionRegistry();
        var other = Guid.NewGuid();

        registry.RegisterHello("kofin", "device-1", User, 2, externalContent: true);

        Assert.Equal(2, registry.Resolve("kofin", "device-1", User));
        Assert.Equal(1, registry.Resolve("kofin", "device-1", other));
        Assert.False(registry.HasExternalContent("kofin", "device-1", other));
    }

    [Fact]
    public void A_caller_without_a_user_negotiates_nothing()
    {
        // A missing or malformed user claim reads as Guid.Empty.
        var registry = new ProtocolVersionRegistry();

        registry.RegisterHello("kofin", "device-1", Guid.Empty, 2, externalContent: true);
        registry.Register("kofin", "device-2", Guid.Empty, 2);

        Assert.Equal(1, registry.Resolve("kofin", "device-1", Guid.Empty));
        Assert.Equal(1, registry.Resolve("kofin", "device-2", Guid.Empty));
        Assert.False(registry.HasExternalContent("kofin", "device-1", Guid.Empty));
    }
}
