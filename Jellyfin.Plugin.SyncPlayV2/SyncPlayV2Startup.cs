using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2;

/// <summary>
/// Startup self-check (feasibility §8.1): forces the engine into existence at
/// host start (so no session event is missed) and asserts the DI shadows
/// actually resolved to the plugin — the shadow is an emergent property of
/// registration order, so a future server change must fail LOUDLY here, not
/// silently serve stock SyncPlay.
/// </summary>
public class SyncPlayV2Startup : IHostedService
{
    private readonly ILogger<SyncPlayV2Startup> _logger;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly IWebSocketManager _webSocketManager;

    public SyncPlayV2Startup(
        ILogger<SyncPlayV2Startup> logger,
        ISyncPlayManager syncPlayManager,
        IWebSocketManager webSocketManager)
    {
        _logger = logger;
        _syncPlayManager = syncPlayManager;
        _webSocketManager = webSocketManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var managerOk = _syncPlayManager is SyncPlayManagerV2;
        var socketOk = _webSocketManager is Ws.TimeSyncSocket;

        if (managerOk && socketOk)
        {
            _logger.LogInformation(
                "[SyncPlayV2] engine active: ISyncPlayManager => {Manager}, IWebSocketManager => {Ws}",
                _syncPlayManager.GetType().Name,
                _webSocketManager.GetType().Name);
        }
        else
        {
            _logger.LogError(
                "[SyncPlayV2] DI SHADOW FAILED — SyncPlay v2 is NOT active. ISyncPlayManager => {Manager} (plugin: {ManagerOk}), IWebSocketManager => {Ws} (plugin: {SocketOk}). A server change likely altered service registration order.",
                _syncPlayManager.GetType().FullName,
                managerOk,
                _webSocketManager.GetType().FullName,
                socketOk);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
