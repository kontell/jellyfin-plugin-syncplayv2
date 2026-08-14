using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

/// <summary>
/// Collects the M0 spike evidence: which mechanisms fired, when, with what.
/// Read back via GET /SyncPlayV2Spike/Status.
/// </summary>
public class SpikeDiagnostics
{
    /// <summary>
    /// Evidence recorded before the DI container exists (service registration
    /// time); drained into the instance log on first use.
    /// </summary>
    private static readonly ConcurrentQueue<string> _bootstrapLog = new();

    private readonly ConcurrentQueue<string> _log = new();

    public SpikeDiagnostics()
    {
        while (_bootstrapLog.TryDequeue(out var line))
        {
            _log.Enqueue(line);
        }

        Add("diagnostics", "SpikeDiagnostics constructed");
    }

    /// <summary>
    /// Record evidence from IPluginServiceRegistrator, where no DI exists yet.
    /// </summary>
    public static void AddBootstrap(string category, string detail)
        => _bootstrapLog.Enqueue($"{DateTime.UtcNow:O} [{category}] {detail}");

    public void Add(string category, string detail)
        => _log.Enqueue($"{DateTime.UtcNow:O} [{category}] {detail}");

    public IReadOnlyList<string> Snapshot() => _log.ToArray();

    public int Count(string category) => _log.Count(l => l.Contains($"[{category}]", StringComparison.Ordinal));
}
