using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SyncPlayV2.Spike;

// Plugin-defined wire DTOs. These must serialize to JSON byte-equivalent to
// what the patched server (integration/syncplay-phase1) emits. The envelope's
// MessageType stays a stock SessionMessageType value; everything inside Data
// is ours — which is how the closed GroupUpdateType enum is bypassed (Type is
// a plain string here) and how StateVersion rides along.
//
// Server WS serialization uses JsonDefaults.Options: PascalCase property
// names, enums as strings — so plain C# property names are already correct.

/// <summary>
/// The SyncPlayGroupUpdate payload: mirrors MediaBrowser.Model.SyncPlay.GroupUpdate&lt;T&gt;
/// plus the v2 StateVersion field, with Type as an open string.
/// </summary>
public class SpikeGroupUpdate
{
    public Guid GroupId { get; set; }

    public string Type { get; set; } = string.Empty;

    public long StateVersion { get; set; }

    public object? Data { get; set; }
}

/// <summary>
/// GroupInfoDto with the v2 additions (ProtocolVersion, Members).
/// </summary>
public class SpikeGroupInfo
{
    public int ProtocolVersion { get; set; } = 2;

    public Guid GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public string State { get; set; } = "Idle";

    public List<string> Participants { get; set; } = new();

    public DateTime LastUpdatedAt { get; set; }

    public List<SpikeMemberStatus> Members { get; set; } = new();
}

public class SpikeMemberStatus
{
    public bool IsConnected { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public bool IsBuffering { get; set; }

    public bool IgnoreGroupWait { get; set; }

    public long Ping { get; set; } = 500;
}

/// <summary>
/// The SyncPlayCommand payload: mirrors MediaBrowser.Model.SyncPlay.SendCommand
/// plus the v2 StateVersion field.
/// </summary>
public class SpikeSendCommand
{
    public Guid GroupId { get; set; }

    public Guid PlaylistItemId { get; set; }

    public DateTime When { get; set; }

    public long? PositionTicks { get; set; }

    public string Command { get; set; } = "Stop";

    public DateTime EmittedAt { get; set; }

    public long StateVersion { get; set; }
}
