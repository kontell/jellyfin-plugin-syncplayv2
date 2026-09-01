using System;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.SyncPlayV2.Engine;

/// <summary>
/// Session-scoped protocol-version negotiation. Written by the body sniffer
/// (reading ProtocolVersion from the stock Join/New bodies before model
/// binding drops it — spec §2 transparent) and by POST /SyncPlay/Hello
/// (explicit probe). Read by the engine when a member is (re)attached.
///
/// Keyed by client + device id — the same identity a Jellyfin session key is
/// derived from — because the sniffer runs before a SessionInfo necessarily
/// exists. Entries slide for 12h; a device that stops negotiating v2 (client
/// downgrade) falls back to v1 after expiry or an explicit v1 registration.
/// </summary>
public class ProtocolVersionRegistry
{
    /// <summary>
    /// The one capability name this server understands (plan G3.2): the
    /// device can resolve external-content queue entries. Declared in the
    /// Hello body's Capabilities list, spelled exactly like this.
    /// </summary>
    public const string ExternalContentCapability = "ExternalContent";

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (int Version, bool ExternalContent, DateTime At)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string? client, string? deviceId) => $"{client}|{deviceId}";

    /// <summary>
    /// Register a negotiated version, preserving any capability declaration:
    /// this is the sniffer's write (Join/New bodies carry no capabilities),
    /// and it must not wipe what a Hello declared moments earlier.
    /// </summary>
    public void Register(string? client, string? deviceId, int version)
    {
        var externalContent = _entries.TryGetValue(Key(client, deviceId), out var existing)
            && existing.ExternalContent;
        RegisterHello(client, deviceId, version, externalContent);
    }

    /// <summary>
    /// The Hello write: version and capability set together — the most
    /// recent Hello wins, withdrawals included (spec §2.1 semantics).
    /// </summary>
    public void RegisterHello(string? client, string? deviceId, int version, bool externalContent)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        _entries[Key(client, deviceId)] = (version, externalContent, DateTime.UtcNow);

        // Opportunistic sweep; the registry stays tiny (one entry per device).
        if (_entries.Count > 4096)
        {
            var cutoff = DateTime.UtcNow - Ttl;
            foreach (var (key, value) in _entries)
            {
                if (value.At < cutoff)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    /// <summary>Resolve the negotiated version for a session; defaults to 1.</summary>
    public int Resolve(SessionInfo session) => Resolve(session.Client, session.DeviceId);

    /// <summary>Resolve by device identity; defaults to 1.</summary>
    public int Resolve(string? client, string? deviceId)
    {
        if (_entries.TryGetValue(Key(client, deviceId), out var entry)
            && DateTime.UtcNow - entry.At < Ttl)
        {
            return entry.Version;
        }

        return 1;
    }

    /// <summary>Whether the session's device declared the external-content capability.</summary>
    public bool HasExternalContent(SessionInfo session)
        => HasExternalContent(session.Client, session.DeviceId);

    /// <summary>Whether the device declared the external-content capability; defaults to false.</summary>
    public bool HasExternalContent(string? client, string? deviceId)
        => _entries.TryGetValue(Key(client, deviceId), out var entry)
            && DateTime.UtcNow - entry.At < Ttl
            && entry.ExternalContent;
}
