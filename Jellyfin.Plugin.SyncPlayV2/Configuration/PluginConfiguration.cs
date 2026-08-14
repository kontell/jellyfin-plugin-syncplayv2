using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SyncPlayV2.Configuration;

/// <summary>
/// Plugin configuration. Registration-time behavior cannot be config-gated
/// (IPluginServiceRegistrator runs before the plugin instance exists), so
/// only runtime behavior lives here.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether v2 members joining a Playing
    /// group catch the running playback without pausing anyone (hot join).
    /// When disabled, every join uses the classic group-wait barrier.
    /// </summary>
    public bool HotJoin { get; set; } = true;
}
