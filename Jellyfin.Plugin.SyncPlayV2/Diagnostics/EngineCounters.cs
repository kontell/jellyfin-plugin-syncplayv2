using System;
using System.Threading;

namespace Jellyfin.Plugin.SyncPlayV2.Diagnostics;

/// <summary>
/// What the engine did since the server started, counted where it already
/// logs it. In memory only: a restart starts them over, which is what a
/// diagnostic needs — "since the server started" — without a store to keep
/// consistent. Incremented from the sweep timer, request threads and the
/// socket-liveness timer at once, hence Interlocked throughout.
/// </summary>
public sealed class EngineCounters
{
    private long _stallTimeouts;
    private long _loadTimeouts;
    private long _bufferingRecovered;
    private long _bufferingApplied;
    private long _corrections;
    private long _hotJoins;
    private long _rendezvous;
    private long _disconnects;
    private long _reconnects;
    private long _graceExpiries;
    private long _zombieSockets;

    /// <summary>Gets when counting started.</summary>
    public DateTime Since { get; } = DateTime.UtcNow;

    /// <summary>The group stopped waiting for a member that stalled on its own.</summary>
    public void StallTimeout() => Interlocked.Increment(ref _stallTimeouts);

    /// <summary>The group stopped waiting for a member loading an item.</summary>
    public void LoadTimeout() => Interlocked.Increment(ref _loadTimeouts);

    /// <summary>A member recovered within the buffering grace; nobody else was paused.</summary>
    public void BufferingRecovered() => Interlocked.Increment(ref _bufferingRecovered);

    /// <summary>A member did not recover within the buffering grace; the group was paused.</summary>
    public void BufferingApplied() => Interlocked.Increment(ref _bufferingApplied);

    /// <summary>A member was sent a position correction.</summary>
    public void Correction() => Interlocked.Increment(ref _corrections);

    /// <summary>A v2 member joined a playing group without pausing it.</summary>
    public void HotJoin() => Interlocked.Increment(ref _hotJoins);

    /// <summary>A v2 member the group could not wait for was handed a private catch-up.</summary>
    public void Rendezvous() => Interlocked.Increment(ref _rendezvous);

    /// <summary>A member's connection ended and its grace window started.</summary>
    public void Disconnect() => Interlocked.Increment(ref _disconnects);

    /// <summary>A disconnected member came back within its grace window.</summary>
    public void Reconnect() => Interlocked.Increment(ref _reconnects);

    /// <summary>A disconnected member did not come back and was removed.</summary>
    public void GraceExpiry() => Interlocked.Increment(ref _graceExpiries);

    /// <summary>A socket stopped keep-aliving without the core noticing.</summary>
    public void ZombieSocket() => Interlocked.Increment(ref _zombieSockets);

    /// <summary>A consistent-enough copy for display: each value is read atomically.</summary>
    /// <returns>The counts.</returns>
    public CountersSnapshot Snapshot() => new()
    {
        Since = Since,
        StallTimeouts = Interlocked.Read(ref _stallTimeouts),
        LoadTimeouts = Interlocked.Read(ref _loadTimeouts),
        BufferingRecovered = Interlocked.Read(ref _bufferingRecovered),
        BufferingApplied = Interlocked.Read(ref _bufferingApplied),
        Corrections = Interlocked.Read(ref _corrections),
        HotJoins = Interlocked.Read(ref _hotJoins),
        Rendezvous = Interlocked.Read(ref _rendezvous),
        Disconnects = Interlocked.Read(ref _disconnects),
        Reconnects = Interlocked.Read(ref _reconnects),
        GraceExpiries = Interlocked.Read(ref _graceExpiries),
        ZombieSockets = Interlocked.Read(ref _zombieSockets),
    };
}
