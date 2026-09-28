using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SyncPlayV2.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SyncPlayV2;

/// <summary>
/// SyncPlay protocol v2 as a server plugin: the phase-1 engine (versioned
/// state, snapshots, beacons, adaptive tolerances, disconnect grace) serving
/// stock v1 clients and v2 clients from one group registry. Shadows the core
/// ISyncPlayManager while installed; disable the plugin to restore stock
/// SyncPlay.
/// </summary>
public class SyncPlayV2Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public SyncPlayV2Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static SyncPlayV2Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "SyncPlayV2",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }

    /// <inheritdoc />
    public override Guid Id => new Guid("181f9934-bf71-4941-974e-a5f2cdcccc4e");

    /// <inheritdoc />
    public override string Name => "SyncPlay v2";

    /// <inheritdoc />
    public override string Description
        => "SyncPlay protocol v2 (M0 spike build): versioned state, snapshots, position beacons and robust reconnects, served from a plugin.";
}
