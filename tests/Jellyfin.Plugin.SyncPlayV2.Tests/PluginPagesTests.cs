using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class PluginPagesTests
{
    [Fact]
    public void TheConfigurationPageIsEmbeddedWhereTheDashboardLooksForIt()
    {
        // The dashboard serves the page from EmbeddedResourcePath; a renamed
        // file or namespace makes the page 404 with no build error.
        var plugin = (SyncPlayV2Plugin)RuntimeHelpers.GetUninitializedObject(typeof(SyncPlayV2Plugin));

        var page = Assert.Single(plugin.GetPages());

        Assert.Equal("SyncPlayV2", page.Name);
        using var stream = typeof(SyncPlayV2Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 0);
    }
}
