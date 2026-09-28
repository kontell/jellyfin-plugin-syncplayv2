using System;
using System.Collections.Generic;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.SyncPlayV2.Diagnostics;

/// <summary>The counts in <see cref="EngineCounters"/> at one moment.</summary>
public class CountersSnapshot
{
    /// <summary>Gets or sets when counting started (server start).</summary>
    public DateTime Since { get; set; }

    /// <summary>Gets or sets how often the group stopped waiting for a member's own stall.</summary>
    public long StallTimeouts { get; set; }

    /// <summary>Gets or sets how often the group stopped waiting for a member loading an item.</summary>
    public long LoadTimeouts { get; set; }

    /// <summary>Gets or sets how many rebuffers ended within the buffering grace.</summary>
    public long BufferingRecovered { get; set; }

    /// <summary>Gets or sets how many rebuffers outlived the grace and paused the group.</summary>
    public long BufferingApplied { get; set; }

    /// <summary>Gets or sets how many position corrections were sent.</summary>
    public long Corrections { get; set; }

    /// <summary>Gets or sets how many v2 members joined a playing group without pausing it.</summary>
    public long HotJoins { get; set; }

    /// <summary>Gets or sets how many v2 members were handed a private catch-up.</summary>
    public long Rendezvous { get; set; }

    /// <summary>Gets or sets how many member connections ended.</summary>
    public long Disconnects { get; set; }

    /// <summary>Gets or sets how many disconnected members came back in time.</summary>
    public long Reconnects { get; set; }

    /// <summary>Gets or sets how many disconnected members were removed.</summary>
    public long GraceExpiries { get; set; }

    /// <summary>Gets or sets how many dead sockets the core had not noticed.</summary>
    public long ZombieSockets { get; set; }
}

/// <summary>One group as the engine sees it.</summary>
public class GroupDiagnostics
{
    /// <summary>Gets or sets the group id.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Gets or sets the group name.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Gets or sets the group state.</summary>
    public GroupStateType State { get; set; }

    /// <summary>Gets or sets how long the group has been in <see cref="State"/>, in seconds.</summary>
    public double StateSeconds { get; set; }

    /// <summary>Gets or sets the state version stamped on outbound messages.</summary>
    public long StateVersion { get; set; }

    /// <summary>Gets or sets the playing item's name, if any.</summary>
    public string? ItemName { get; set; }

    /// <summary>Gets or sets the group's position, extrapolated while playing, in seconds.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Gets or sets the playing item's runtime in seconds; 0 when unknown.</summary>
    public double RunTimeSeconds { get; set; }

    /// <summary>Gets or sets the number of entries in the queue.</summary>
    public int QueueLength { get; set; }

    /// <summary>Gets or sets the members.</summary>
    public List<MemberDiagnostics> Members { get; set; } = new();
}

/// <summary>One member as the engine sees it: the flags that decide how the group treats it.</summary>
public class MemberDiagnostics
{
    /// <summary>Gets or sets the user name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the client application.</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the negotiated protocol version.</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>Gets or sets whether the device declared the external-content capability.</summary>
    public bool ExternalContent { get; set; }

    /// <summary>Gets or sets the last reported ping, in milliseconds.</summary>
    public long Ping { get; set; }

    /// <summary>Gets or sets whether the member has a live connection.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Gets or sets how long the member has been disconnected, in seconds, if it is.</summary>
    public double? DisconnectedSeconds { get; set; }

    /// <summary>Gets or sets whether the member is loading or stalled.</summary>
    public bool IsBuffering { get; set; }

    /// <summary>Gets or sets how long the member has been buffering, in seconds, if it is.</summary>
    public double? BufferingSeconds { get; set; }

    /// <summary>Gets or sets whether the current wait is an item load (load timeout) rather than a stall.</summary>
    public bool BufferingForLoad { get; set; }

    /// <summary>Gets or sets whether the group is not waiting for the member, for any reason.</summary>
    public bool IgnoreGroupWait { get; set; }

    /// <summary>Gets or sets whether the group gave up waiting for the member after a timeout.</summary>
    public bool IgnoredByTimeout { get; set; }

    /// <summary>Gets or sets whether the member asked not to be waited for.</summary>
    public bool Spectator { get; set; }

    /// <summary>Gets or sets whether the member is catching a running playback (hot join or rendezvous).</summary>
    public bool HotJoining { get; set; }

    /// <summary>Gets or sets the position corrections sent in the current sequence.</summary>
    public int CorrectionAttempts { get; set; }
}

/// <summary>The whole diagnostic document: what is happening now and what has happened.</summary>
public class DiagnosticsReport
{
    /// <summary>Gets or sets when the report was taken.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>Gets or sets the plugin version.</summary>
    public string? PluginVersion { get; set; }

    /// <summary>Gets or sets the counts since the server started.</summary>
    public CountersSnapshot Counters { get; set; } = new();

    /// <summary>Gets or sets the groups.</summary>
    public List<GroupDiagnostics> Groups { get; set; } = new();
}
