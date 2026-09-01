using Jellyfin.Plugin.SyncPlayV2.Engine;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class ContentDescriptorTests
{
    [Fact]
    public void AValidDescriptorBuilds()
    {
        var ok = ContentDescriptor.TryCreate("youtube", "dQw4w9WgXcQ", "Never Gonna", 21_200_000_000, null, out var descriptor);

        Assert.True(ok);
        Assert.Equal("youtube", descriptor!.Provider);
        Assert.Equal("dQw4w9WgXcQ", descriptor.Key);
        Assert.Equal(21_200_000_000, descriptor.RunTimeTicks);
        Assert.Equal(string.Empty, descriptor.ImageUrl);
    }

    [Fact]
    public void AMissingRuntimeIsZeroMeaningUnbounded()
    {
        Assert.True(ContentDescriptor.TryCreate("pvr", "channel-4", null, null, null, out var descriptor));
        Assert.Equal(0, descriptor!.RunTimeTicks);
        Assert.Equal(string.Empty, descriptor.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("x")]
    [InlineData("jellyfin!")]
    public void ABadProviderNameIsRefused(string? provider)
    {
        Assert.False(ContentDescriptor.TryCreate(provider, "key", null, null, null, out _));
    }

    [Fact]
    public void OversizeFieldsAreRefusedNotTrimmed()
    {
        Assert.False(ContentDescriptor.TryCreate("p1", new string('k', 513), null, null, null, out _));
        Assert.False(ContentDescriptor.TryCreate("p1", "k", new string('n', 257), null, null, out _));
        Assert.False(ContentDescriptor.TryCreate("p1", "k", null, null, new string('u', 1025), out _));
        Assert.False(ContentDescriptor.TryCreate("p1", string.Empty, null, null, null, out _));
    }

    [Fact]
    public void ANegativeRuntimeIsRefused()
    {
        Assert.False(ContentDescriptor.TryCreate("p1", "k", null, -1, null, out _));
    }
}
