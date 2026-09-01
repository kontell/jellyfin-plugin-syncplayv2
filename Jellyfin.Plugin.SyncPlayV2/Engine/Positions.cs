using System;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// What a reported or requested position is allowed to be.
    ///
    /// Pure arithmetic, deliberately outside <see cref="Group"/> for the same
    /// reason as <see cref="CorrectionPolicy"/>: the decision is the whole of
    /// the behaviour and Group cannot be constructed without a server around
    /// it, so keeping it here is what makes it testable at all.
    /// </summary>
    public static class Positions
    {
        /// <summary>
        /// Clamps a position to the playing item's runtime — treating an
        /// unknown runtime as unbounded, never as zero.
        ///
        /// Upstream clamps to <c>[0, RunTimeTicks]</c> unconditionally, and a
        /// runtime of 0 is exactly what an item without one produces (a live
        /// TV channel, a deleted item, an external-content entry): every Seek
        /// and every Ready report then clamps to position 0, the member's
        /// extrapolated offset grows without bound, and the correction
        /// machinery fires forever ("Session got lost in time"). A position
        /// can be negative on the wire (a client extrapolating across a clock
        /// offset), so the floor stays.
        /// </summary>
        /// <param name="positionTicks">The reported position, if any.</param>
        /// <param name="runTimeTicks">The playing item's runtime, or 0 when unknown.</param>
        /// <returns>The sanitized position.</returns>
        public static long Sanitize(long? positionTicks, long runTimeTicks)
        {
            var ticks = Math.Max(0, positionTicks ?? 0);
            return runTimeTicks > 0 ? Math.Min(ticks, runTimeTicks) : ticks;
        }
    }
}
