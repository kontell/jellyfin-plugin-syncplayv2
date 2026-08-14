using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// Body-transparency probe (feasibility §5.2a): a global MVC resource filter,
/// registered from the plugin via Configure&lt;MvcOptions&gt;, that reads the raw
/// request body of the STOCK /SyncPlay/Join and /SyncPlay/New routes before
/// model binding drops unknown fields. If this fires and extracts
/// ProtocolVersion, spec clients negotiate v2 with no route changes at all.
/// </summary>
public class SpikeBodySnifferFilter : IAsyncResourceFilter
{
    private readonly SpikeDiagnostics _diag;

    public SpikeBodySnifferFilter(SpikeDiagnostics diag)
    {
        _diag = diag;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var path = request.Path;

        if (path.StartsWithSegments("/SyncPlay", StringComparison.OrdinalIgnoreCase, out var rest)
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

                int? protocolVersion = null;
                if (body.Length > 0)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("ProtocolVersion", out var v)
                        && v.ValueKind == JsonValueKind.Number)
                    {
                        protocolVersion = v.GetInt32();
                    }
                }

                var deviceId = context.HttpContext.User.FindFirst("Jellyfin-DeviceId")?.Value ?? "?";
                _diag.Add(
                    "body-sniffer",
                    $"{path} deviceId={deviceId} bodyBytes={body.Length} ProtocolVersion={(protocolVersion?.ToString() ?? "absent")}");
            }
            catch (Exception ex)
            {
                _diag.Add("body-sniffer", $"{path} FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await next().ConfigureAwait(false);
    }
}
