using System;
using System.Collections.Generic;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.SyncPlayV2.Wire;

// Plugin-defined wire DTOs, JSON byte-equivalent to what the patched server
// (integration/syncplay-phase1) emits. Envelope MessageTypes stay stock
// (SyncPlayGroupUpdate / SyncPlayCommand); everything below is Data. Type is
// an open string, which is how the closed GroupUpdateType enum gains
// "StateSnapshot"/"PositionBeacon" without core changes. Serialized by core
// with JsonDefaults.Options: PascalCase names, enums as strings.

/// <summary>GroupUpdate + the v2 StateVersion field.</summary>
public class WireGroupUpdate
{
    public Guid GroupId { get; set; }

    public string Type { get; set; } = string.Empty;

    public long StateVersion { get; set; }

    public object? Data { get; set; }

    /// <summary>Translate a stock typed update (built by the vendored states) to the wire.</summary>
    public static WireGroupUpdate From<T>(GroupUpdate<T> update, long stateVersion)
        => new()
        {
            GroupId = update.GroupId,
            Type = update.Type.ToString(),
            StateVersion = stateVersion,
            Data = update.Data,
        };
}

/// <summary>SendCommand + the v2 StateVersion field.</summary>
public class WireSendCommand
{
    public Guid GroupId { get; set; }

    public Guid PlaylistItemId { get; set; }

    public DateTime When { get; set; }

    public long? PositionTicks { get; set; }

    public string Command { get; set; } = string.Empty;

    public DateTime EmittedAt { get; set; }

    public long StateVersion { get; set; }

    public static WireSendCommand From(SendCommand command, long stateVersion)
        => new()
        {
            GroupId = command.GroupId,
            PlaylistItemId = command.PlaylistItemId,
            When = command.When,
            PositionTicks = command.PositionTicks,
            Command = command.Command.ToString(),
            EmittedAt = command.EmittedAt,
            StateVersion = stateVersion,
        };
}

/// <summary>GroupInfoDto + the v2 additions (member-scoped ProtocolVersion, Members).</summary>
public class WireGroupInfo
{
    /// <summary>Null (omitted) for members that did not negotiate v2 — spec §2 + the §5.2 advertisement rule.</summary>
    public int? ProtocolVersion { get; set; }

    public Guid GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public GroupStateType State { get; set; }

    public List<string> Participants { get; set; } = new();

    public DateTime LastUpdatedAt { get; set; }

    public List<WireMemberStatus> Members { get; set; } = new();
}

public class WireMemberStatus
{
    public bool IsConnected { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public bool IsBuffering { get; set; }

    public bool IgnoreGroupWait { get; set; }

    public long Ping { get; set; }
}

/// <summary>The complete group state (v2 StateSnapshot payload).</summary>
public class WireGroupSnapshot
{
    public string GroupName { get; set; } = string.Empty;

    public GroupStateType State { get; set; }

    public PlayQueueUpdate? PlayQueue { get; set; }

    public long PositionTicks { get; set; }

    public DateTime When { get; set; }

    public bool IsPlaying { get; set; }

    public List<WireMemberStatus> Members { get; set; } = new();
}

/// <summary>The v2 PositionBeacon payload.</summary>
public class WirePositionBeacon
{
    public Guid PlaylistItemId { get; set; }

    public long PositionTicks { get; set; }

    public DateTime When { get; set; }
}
