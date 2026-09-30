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
/// Keyed by client + device id + user — the identity a Jellyfin 12 session
/// is keyed by — because the sniffer runs before a SessionInfo necessarily
/// exists, and two users of one app on one device are two sessions whose
/// versions must not leak into each other. Entries slide for 12h; a device that stops negotiating v2 (client
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

    /// <summary>
    /// The highest protocol version this server speaks, and so the highest
    /// a device can negotiate: a client asking for more gets this.
    /// </summary>
    public const int ServerVersion = 2;

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (int Version, bool ExternalContent, DateTime At)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string? client, string? deviceId, Guid userId) => $"{client}|{deviceId}|{userId:N}";

    /// <summary>
    /// Register a negotiated version, preserving any capability declaration:
    /// this is the sniffer's write (Join/New bodies carry no capabilities),
    /// and it must not wipe what a Hello declared moments earlier.
    /// </summary>
    public void Register(string? client, string? deviceId, Guid userId, int version)
    {
        var externalContent = _entries.TryGetValue(Key(client, deviceId, userId), out var existing)
            && existing.ExternalContent;
        RegisterHello(client, deviceId, userId, version, externalContent);
    }

    /// <summary>
    /// The Hello write: version and capability set together — the most
    /// recent Hello wins, withdrawals included (spec §2.1 semantics).
    /// </summary>
    public void RegisterHello(string? client, string? deviceId, Guid userId, int version, bool externalContent)
    {
        // Without a device or a user the entry names no one session, and one
        // keyed on an empty user would be shared by every caller missing it.
        if (string.IsNullOrEmpty(deviceId) || userId.Equals(Guid.Empty))
        {
            return;
        }

        // Negotiated, not declared: the lower of what the client asked for and
        // what the server speaks, and never below v1. A client asking for v3
        // speaks v2 to this server; a 0 or a negative is a v1 client.
        _entries[Key(client, deviceId, userId)] = (Math.Clamp(version, 1, ServerVersion), externalContent, DateTime.UtcNow);

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
    public int Resolve(SessionInfo session) => Resolve(session.Client, session.DeviceId, session.UserId);

    /// <summary>Resolve by session identity; defaults to 1.</summary>
    public int Resolve(string? client, string? deviceId, Guid userId)
    {
        if (_entries.TryGetValue(Key(client, deviceId, userId), out var entry)
            && DateTime.UtcNow - entry.At < Ttl)
        {
            return entry.Version;
        }

        return 1;
    }

    /// <summary>Whether the session's device declared the external-content capability.</summary>
    public bool HasExternalContent(SessionInfo session)
        => HasExternalContent(session.Client, session.DeviceId, session.UserId);

    /// <summary>Whether the session declared the external-content capability; defaults to false.</summary>
    public bool HasExternalContent(string? client, string? deviceId, Guid userId)
        => _entries.TryGetValue(Key(client, deviceId, userId), out var entry)
            && DateTime.UtcNow - entry.At < Ttl
            && entry.ExternalContent;
}
