using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Api;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Filters;

/// <summary>
/// Body-transparent protocol negotiation (spec §2, M0-proven): a global MVC
/// resource filter that reads ProtocolVersion from the raw bodies of the STOCK
/// POST /SyncPlay/Join and /SyncPlay/New requests before model binding drops
/// the unknown field, and records it in the version registry the engine reads
/// at member attach time. Spec clients need no changes.
/// </summary>
public class ProtocolVersionSniffer : IAsyncResourceFilter
{
    private readonly ProtocolVersionRegistry _versions;
    private readonly ILogger<ProtocolVersionSniffer> _logger;

    public ProtocolVersionSniffer(ProtocolVersionRegistry versions, ILogger<ProtocolVersionSniffer> logger)
    {
        _versions = versions;
        _logger = logger;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.Path.StartsWithSegments("/SyncPlay", StringComparison.OrdinalIgnoreCase, out var rest)
            && (rest.Equals("/Join", StringComparison.OrdinalIgnoreCase) || rest.Equals("/New", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                request.EnableBuffering();
                string body;
                using (var reader = new StreamReader(request.Body, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                request.Body.Position = 0;

                if (body.Length > 0)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("ProtocolVersion", out var v)
                        && v.ValueKind == JsonValueKind.Number)
                    {
                        var (client, deviceId) = SessionResolver.Identity(context.HttpContext.User);
                        _versions.Register(client, deviceId, v.GetInt32());
                    }
                }
            }
            catch (Exception ex)
            {
                // Sniffing is an enhancement; a failure must never break the
                // stock route (the client can still negotiate via Hello).
                _logger.LogWarning(ex, "ProtocolVersion sniff failed for {Path}.", request.Path);
            }
        }

        await next().ConfigureAwait(false);
    }
}
