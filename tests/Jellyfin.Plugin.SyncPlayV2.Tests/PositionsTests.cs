using System;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class PositionsTests
{
    private static long Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;

    [Fact]
    public void APositionInsideTheRuntimePassesThrough()
    {
        Assert.Equal(Ms(5000), Positions.Sanitize(Ms(5000), Ms(10000)));
    }

    [Fact]
    public void APositionPastTheRuntimeClampsToIt()
    {
        Assert.Equal(Ms(10000), Positions.Sanitize(Ms(15000), Ms(10000)));
    }

    [Fact]
    public void ANegativePositionClampsToZeroWhateverTheRuntime()
    {
        // A client extrapolating across a clock offset can report just below
        // zero; the floor holds with and without a runtime to clamp against.
        Assert.Equal(0, Positions.Sanitize(Ms(-240), Ms(10000)));
        Assert.Equal(0, Positions.Sanitize(Ms(-240), 0));
    }

    [Fact]
    public void AMissingPositionIsZero()
    {
        Assert.Equal(0, Positions.Sanitize(null, Ms(10000)));
    }

    [Fact]
    public void AnUnknownRuntimeIsUnboundedNotZero()
    {
        // Upstream clamps to [0, 0] here: every Seek became a no-op and every
        // Ready report read as position 0, so the member's extrapolated offset
        // grew without bound and the correction machinery fired forever. A
        // live channel, a deleted item and an external-content entry all
        // carry runtime 0.
        Assert.Equal(Ms(90 * 60 * 1000), Positions.Sanitize(Ms(90 * 60 * 1000), 0));
    }
}
