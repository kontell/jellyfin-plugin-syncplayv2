using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// Hosted service registered from the plugin registrator. Its existence proves
/// plugins can AddHostedService; its constructor forces the shadowed manager
/// into existence at host start (so session events are never missed) and
/// records which implementations DI actually resolved.
/// </summary>
public class SpikeStartup : IHostedService
{
    private readonly ILogger<SpikeStartup> _logger;
    private readonly SpikeDiagnostics _diag;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly IWebSocketManager _webSocketManager;

    public SpikeStartup(
        ILogger<SpikeStartup> logger,
        SpikeDiagnostics diag,
        ISyncPlayManager syncPlayManager,
        IWebSocketManager webSocketManager)
    {
        _logger = logger;
        _diag = diag;
        _syncPlayManager = syncPlayManager;
        _webSocketManager = webSocketManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var managerType = _syncPlayManager.GetType().FullName;
        var wsType = _webSocketManager.GetType().FullName;

        _diag.Add("hosted-service", $"started; ISyncPlayManager => {managerType}; IWebSocketManager => {wsType}");
        _logger.LogInformation(
            "[SyncPlayV2 spike] hosted service started. ISyncPlayManager resolved to {Manager}; IWebSocketManager resolved to {Ws}",
            managerType,
            wsType);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
