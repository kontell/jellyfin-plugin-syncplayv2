using System;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Jellyfin.Plugin.SyncPlayV2.Filters;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jellyfin.Plugin.SyncPlayV2;

/// <summary>
/// Runs AFTER the core's RegisterServices (ApplicationHost.cs:460 then :462 on
/// release-10.11.z), so the AddSingleton shadows below win last-registration
/// resolution — the M0-proven central mechanism. SyncPlayV2Startup asserts the
/// outcome at every server start.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Wire.Sender>();
        serviceCollection.AddSingleton<ProtocolVersionRegistry>();
        serviceCollection.AddSingleton<Api.SessionResolver>();

        // One engine instance behind both interfaces; the second registration
        // shadows the core SyncPlayManager (which is then never constructed).
        serviceCollection.AddSingleton<SyncPlayManagerV2>();
        serviceCollection.AddSingleton<ISyncPlayManager>(sp => sp.GetRequiredService<SyncPlayManagerV2>());
        serviceCollection.AddSingleton<ISyncPlayManagerV2>(sp => sp.GetRequiredService<SyncPlayManagerV2>());

        // All WS upgrades funnel through IWebSocketManager: shadow it with the
        // path router that serves the dedicated time-sync socket and delegates
        // the rest to the core manager.
        serviceCollection.AddSingleton<IWebSocketManager, Ws.TimeSyncSocket>();

        // Zombie-socket detection: stock never aborts lost sockets, so the
        // engine's disconnect grace needs a plugin-side liveness signal.
        serviceCollection.AddSingleton<IWebSocketListener, SocketLiveness>();

        // Body-transparent ProtocolVersion negotiation on the stock Join/New.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add(typeof(ProtocolVersionSniffer)));

        // Eager engine init + DI self-check at every start.
        serviceCollection.AddHostedService<SyncPlayV2Startup>();

        // The List shadow (Order = -1) needs a Swagger conflict resolver or
        // /api-docs generation 500s (M0-proven both ways). Guarded so a
        // Swashbuckle binding problem degrades OpenAPI, not the plugin.
        try
        {
            RegisterSwaggerConflictResolver(serviceCollection);
        }
        catch (Exception)
        {
            // Logged implicitly by the missing resolver's effect; nothing to do here —
            // there is no logger this early.
        }
    }

    // Out-of-line so Swashbuckle type resolution happens when THIS method is
    // jitted, inside the caller's try/catch.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterSwaggerConflictResolver(IServiceCollection serviceCollection)
    {
        serviceCollection.PostConfigure<SwaggerGenOptions>(
            options => options.ResolveConflictingActions(descriptions =>
            {
                foreach (var description in descriptions)
                {
                    return description;
                }

                return null!;
            }));
    }
}
