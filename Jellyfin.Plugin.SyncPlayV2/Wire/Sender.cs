using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Wire;

/// <summary>
/// The M0-proven send path: plugin payloads through the session's controllers
/// under stock SessionMessageType values (SessionInfo.SessionControllers →
/// ISessionController.SendMessage&lt;T&gt;, unconstrained generic). Sends are
/// fire-and-forget with fault logging, matching the engine's semantics; the
/// plugin serializes nothing concurrently itself per session beyond what the
/// core connection does.
/// </summary>
public class Sender
{
    private readonly ILogger<Sender> _logger;

    public Sender(ILogger<Sender> logger)
    {
        _logger = logger;
    }

    public Task SendGroupUpdate(SessionInfo session, WireGroupUpdate update, CancellationToken cancellationToken)
        => Send(session, SessionMessageType.SyncPlayGroupUpdate, update, cancellationToken);

    public Task SendCommand(SessionInfo session, WireSendCommand command, CancellationToken cancellationToken)
        => Send(session, SessionMessageType.SyncPlayCommand, command, cancellationToken);

    private async Task Send<T>(SessionInfo session, SessionMessageType type, T payload, CancellationToken cancellationToken)
    {
        var controllers = session.SessionControllers;
        if (controllers.Count == 0)
        {
            _logger.LogDebug("Session {SessionId} has no session controllers; {Type} dropped.", session.Id, type);
            return;
        }

        foreach (var controller in controllers)
        {
            try
            {
                await controller.SendMessage(type, Guid.NewGuid(), payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deliver {Type} to session {SessionId}.", type, session.Id);
            }
        }
    }
}
