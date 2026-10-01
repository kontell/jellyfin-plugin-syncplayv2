using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SyncPlayV2.Configuration;

/// <summary>
/// Plugin configuration. Registration-time behavior cannot be config-gated
/// (IPluginServiceRegistrator runs before the plugin instance exists), so
/// only runtime behavior lives here. Every timing is read on use, so a
/// saved change applies without a restart; out-of-range values are clamped
/// by <see cref="EngineTimings"/>, never trusted.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether v2 members joining a Playing
    /// group catch the running playback without pausing anyone (hot join).
    /// When disabled, every join uses the classic group-wait barrier.
    /// </summary>
    public bool HotJoin { get; set; } = true;

    /// <summary>
    /// Gets or sets how long, in seconds, the group waits for a member that
    /// stalled on its own mid-playback before it plays on without it.
    /// </summary>
    public int StallTimeoutSeconds { get; set; } = EngineTimings.DefaultStallTimeoutSeconds;

    /// <summary>
    /// Gets or sets how long, in seconds, the group waits for a member to
    /// load an item: a new item, a restart, a classic join. Loading can
    /// mean starting a transcode, which routinely takes longer than a
    /// rebuffer does. A seek within the loaded item keeps the stall timeout.
    /// </summary>
    public int LoadTimeoutSeconds { get; set; } = EngineTimings.DefaultLoadTimeoutSeconds;

    /// <summary>
    /// Gets or sets how long, in seconds, a member's buffering report is held
    /// back while the group keeps playing, so a short rebuffer does not pause
    /// everyone. 0 disables the grace.
    /// </summary>
    public int BufferingGraceSeconds { get; set; } = EngineTimings.DefaultBufferingGraceSeconds;

    /// <summary>
    /// Gets or sets how long, in seconds, a member whose connection dropped
    /// keeps its place in the group before it is removed.
    /// </summary>
    public int DisconnectGraceSeconds { get; set; } = EngineTimings.DefaultDisconnectGraceSeconds;

    /// <summary>
    /// Gets or sets how often, in seconds, position beacons are sent to
    /// protocol v2 members while the group plays.
    /// </summary>
    public int PositionBeaconSeconds { get; set; } = EngineTimings.DefaultPositionBeaconSeconds;
}
