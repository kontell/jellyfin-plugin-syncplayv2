using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SyncPlayV2.Configuration;

/// <summary>
/// Plugin configuration. Empty in the M0 spike: registration-time behavior
/// cannot be config-gated anyway (IPluginServiceRegistrator runs before the
/// plugin instance and its configuration exist).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
}
