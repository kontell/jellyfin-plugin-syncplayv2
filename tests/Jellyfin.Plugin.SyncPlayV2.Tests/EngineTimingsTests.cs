using System;
using Jellyfin.Plugin.SyncPlayV2.Configuration;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class EngineTimingsTests
{
    [Fact]
    public void WithoutAConfigurationTheShippedValuesApply()
    {
        var timings = EngineTimings.From(null);

        Assert.Equal(TimeSpan.FromSeconds(10), timings.StallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), timings.LoadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), timings.BufferingGrace);
        Assert.Equal(TimeSpan.FromSeconds(90), timings.DisconnectGrace);
        Assert.Equal(TimeSpan.FromSeconds(5), timings.PositionBeaconInterval);
    }

    [Fact]
    public void ANewConfigurationMatchesTheShippedValues()
    {
        // The configuration's own defaults and the no-configuration defaults
        // must not drift apart: an admin who never opens the page gets the
        // same engine as a server with no configuration file.
        var fromDefaults = EngineTimings.From(new PluginConfiguration());
        var withoutConfiguration = EngineTimings.From(null);

        Assert.Equal(withoutConfiguration.StallTimeout, fromDefaults.StallTimeout);
        Assert.Equal(withoutConfiguration.LoadTimeout, fromDefaults.LoadTimeout);
        Assert.Equal(withoutConfiguration.BufferingGrace, fromDefaults.BufferingGrace);
        Assert.Equal(withoutConfiguration.DisconnectGrace, fromDefaults.DisconnectGrace);
        Assert.Equal(withoutConfiguration.PositionBeaconInterval, fromDefaults.PositionBeaconInterval);
    }

    [Fact]
    public void ValuesTheEngineCannotLiveWithAreClamped()
    {
        // A 0 s timeout would give up on every member the instant it buffers;
        // a negative grace would never remove anyone; a 0 s beacon interval
        // would beacon on every sweep.
        var timings = EngineTimings.From(new PluginConfiguration
        {
            StallTimeoutSeconds = 0,
            LoadTimeoutSeconds = -5,
            BufferingGraceSeconds = -1,
            DisconnectGraceSeconds = 100_000,
            PositionBeaconSeconds = 0,
        });

        Assert.Equal(TimeSpan.FromSeconds(3), timings.StallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), timings.LoadTimeout);
        Assert.Equal(TimeSpan.Zero, timings.BufferingGrace);
        Assert.Equal(TimeSpan.FromSeconds(600), timings.DisconnectGrace);
        Assert.Equal(TimeSpan.FromSeconds(1), timings.PositionBeaconInterval);
    }

    [Fact]
    public void ALoadIsNeverGivenLessTimeThanAStall()
    {
        var timings = EngineTimings.From(new PluginConfiguration
        {
            StallTimeoutSeconds = 20,
            LoadTimeoutSeconds = 5,
        });

        Assert.Equal(TimeSpan.FromSeconds(20), timings.LoadTimeout);
    }
}
