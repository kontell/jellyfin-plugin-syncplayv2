using System;

namespace Jellyfin.Plugin.SyncPlayV2.Configuration;

/// <summary>
/// The engine's timings, read from the saved configuration and clamped to
/// ranges the engine can live with. A configuration file is admin-edited
/// text: a timeout of 0 would give up on every member the instant it
/// buffers, and a negative grace would never remove anyone, so neither is
/// passed through. With no plugin instance (tests, or before the plugin
/// loads) the defaults apply — the values the engine shipped with.
/// </summary>
public sealed class EngineTimings
{
    /// <summary>The default stall timeout, in seconds.</summary>
    public const int DefaultStallTimeoutSeconds = 10;

    /// <summary>The default load timeout, in seconds.</summary>
    public const int DefaultLoadTimeoutSeconds = 30;

    /// <summary>The default buffering grace, in seconds.</summary>
    public const int DefaultBufferingGraceSeconds = 2;

    /// <summary>The default disconnect grace, in seconds.</summary>
    public const int DefaultDisconnectGraceSeconds = 90;

    /// <summary>The default position beacon interval, in seconds.</summary>
    public const int DefaultPositionBeaconSeconds = 5;

    private EngineTimings(PluginConfiguration? configuration)
    {
        StallTimeout = Seconds(configuration?.StallTimeoutSeconds, DefaultStallTimeoutSeconds, 3, 120);

        // Never shorter than the stall timeout: a load is a stall with more to do.
        LoadTimeout = Seconds(configuration?.LoadTimeoutSeconds, DefaultLoadTimeoutSeconds, 3, 300);
        if (LoadTimeout < StallTimeout)
        {
            LoadTimeout = StallTimeout;
        }

        BufferingGrace = Seconds(configuration?.BufferingGraceSeconds, DefaultBufferingGraceSeconds, 0, 10);
        DisconnectGrace = Seconds(configuration?.DisconnectGraceSeconds, DefaultDisconnectGraceSeconds, 10, 600);
        PositionBeaconInterval = Seconds(configuration?.PositionBeaconSeconds, DefaultPositionBeaconSeconds, 1, 60);
    }

    /// <summary>Gets the timings of the running plugin's saved configuration.</summary>
    public static EngineTimings Current => From(SyncPlayV2Plugin.Instance?.Configuration);

    /// <summary>
    /// Gets how long the group waits for a member that stalled on its own
    /// before it plays on without it.
    /// </summary>
    public TimeSpan StallTimeout { get; }

    /// <summary>
    /// Gets how long the group waits for a member to load when the group
    /// made everyone load an item (new item, restart, classic join). A seek
    /// within the loaded item keeps <see cref="StallTimeout"/>.
    /// </summary>
    public TimeSpan LoadTimeout { get; }

    /// <summary>Gets how long a buffering report is held back while the group plays.</summary>
    public TimeSpan BufferingGrace { get; }

    /// <summary>Gets how long a disconnected member keeps its place.</summary>
    public TimeSpan DisconnectGrace { get; }

    /// <summary>Gets how often position beacons are sent while playing.</summary>
    public TimeSpan PositionBeaconInterval { get; }

    /// <summary>The timings a configuration describes, clamped.</summary>
    /// <param name="configuration">The configuration, or null for the defaults.</param>
    /// <returns>The timings.</returns>
    public static EngineTimings From(PluginConfiguration? configuration) => new(configuration);

    private static TimeSpan Seconds(int? value, int fallback, int min, int max)
        => TimeSpan.FromSeconds(Math.Clamp(value ?? fallback, min, max));
}
