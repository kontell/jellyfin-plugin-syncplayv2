using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.SyncPlayV2.Diagnostics;

/// <summary>
/// The last events of one group, for the diagnostics page: what the members
/// asked for and what the engine did about it, so a stall can be explained
/// after it has passed. Bounded, in memory, and touched only under the
/// group's lock like the rest of the group.
/// </summary>
public sealed class GroupHistory
{
    /// <summary>How many events a group keeps.</summary>
    public const int Capacity = 50;

    private readonly Queue<Entry> _entries = new();

    /// <summary>Records an event, dropping the oldest once full.</summary>
    /// <param name="at">When it happened.</param>
    /// <param name="kind">What happened (a request type or an engine event).</param>
    /// <param name="member">The member it concerns, if any (user name; the report aliases it).</param>
    /// <param name="detail">Numbers and flags only: never a title or a name.</param>
    /// <param name="state">The group's state when it is recorded (a request's: the state it arrived in).</param>
    public void Add(DateTime at, string kind, string? member, string? detail, GroupStateType state)
    {
        if (_entries.Count == Capacity)
        {
            _entries.Dequeue();
        }

        _entries.Enqueue(new Entry(at, kind, member, detail, state));
    }

    /// <summary>The events, newest first, timed relative to <paramref name="now"/>.</summary>
    /// <param name="now">The time the snapshot is taken.</param>
    /// <returns>The events.</returns>
    public List<GroupEvent> Snapshot(DateTime now)
        => _entries.Reverse()
            .Select(e => new GroupEvent
            {
                SecondsAgo = Math.Round((now - e.At).TotalSeconds, 1),
                Event = e.Kind,
                Member = e.Member,
                Detail = e.Detail,
                State = e.State,
            })
            .ToList();

    private sealed record Entry(DateTime At, string Kind, string? Member, string? Detail, GroupStateType State);
}
