using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// The external-content entries riding a group's play queue (plan G3.3).
    ///
    /// Keyed by the sentinel ItemId minted per entry — deliberately not by
    /// PlaylistItemId: the ItemId rides the stock <c>PlayQueueManager</c>
    /// unchanged through every mutation (move, remove, next/previous), so
    /// the table needs no bookkeeping to follow the queue; it only needs a
    /// prune when entries leave. A sentinel is a fresh random GUID, so it
    /// collides with neither library items nor other sentinels.
    ///
    /// Not thread-safe on its own: accessed under the group lock, like
    /// everything else hanging off <see cref="Group"/>.
    /// </summary>
    public class ContentTable
    {
        private readonly Dictionary<Guid, ContentDescriptor> _entries = new Dictionary<Guid, ContentDescriptor>();

        /// <summary>Gets the number of registered entries.</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Whether an item id names a registered external-content entry.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <returns>Whether it is registered.</returns>
        public bool Contains(Guid itemId) => _entries.ContainsKey(itemId);

        /// <summary>
        /// The registered runtime for an item id, or null when the id is not
        /// an external-content entry. A registered runtime of 0 is a real
        /// answer — unbounded, per <see cref="Positions.Sanitize"/> — and
        /// deliberately not folded into the null.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <returns>The runtime in ticks, or null.</returns>
        public long? RuntimeOf(Guid itemId)
            => _entries.TryGetValue(itemId, out var descriptor) ? descriptor.RunTimeTicks : null;

        /// <summary>
        /// The descriptor for an item id, or null.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <returns>The descriptor, or null.</returns>
        public ContentDescriptor? Get(Guid itemId)
            => _entries.TryGetValue(itemId, out var descriptor) ? descriptor : null;

        /// <summary>
        /// Registers entries; a re-registered id is replaced.
        /// </summary>
        /// <param name="entries">Sentinel item id to descriptor.</param>
        public void Register(IReadOnlyDictionary<Guid, ContentDescriptor> entries)
        {
            foreach (var pair in entries)
            {
                _entries[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// Drops every entry not in the given id set — called after a queue
        /// mutation so entries that left the queue (or never made it in, a
        /// refused SetNewQueueEx included) do not accumulate.
        /// </summary>
        /// <param name="keep">The item ids still in the queue.</param>
        public void PruneTo(IEnumerable<Guid> keep)
        {
            var keepSet = keep as ISet<Guid> ?? new HashSet<Guid>(keep);

            foreach (var stale in _entries.Keys.Where(id => !keepSet.Contains(id)).ToList())
            {
                _entries.Remove(stale);
            }
        }
    }
}
