using System;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// When to stop correcting a member's position and rendezvous it instead.
    ///
    /// Pure arithmetic, deliberately outside <see cref="Group"/>: the decision
    /// is the whole of the behaviour and Group cannot be constructed without a
    /// server around it, so keeping it here is what makes it testable at all.
    /// </summary>
    public static class CorrectionPolicy
    {
        /// <summary>
        /// How much closer a correction must bring a member for the next one to
        /// be worth sending. Below this the seek is landing where the transport
        /// chose rather than where the group asked — a transcoded stream snaps
        /// to its segment boundary — and no number of retries will fix that.
        /// </summary>
        public static readonly long ProgressTicks = TimeSpan.FromMilliseconds(250).Ticks;

        /// <summary>
        /// Corrections a member gets before it is rendezvoused regardless of
        /// progress. The backstop for a member that improves a little each time
        /// but would need a dozen rounds, with the group waiting throughout.
        /// </summary>
        public const int MaxAttempts = 3;

        /// <summary>
        /// Decides whether another position correction is worth sending.
        /// </summary>
        /// <param name="attempts">Corrections sent so far in this sequence, including the one just counted.</param>
        /// <param name="previousDelayTicks">How far out the member was at the previous correction.</param>
        /// <param name="delayTicks">How far out it is now.</param>
        /// <returns>true when correcting again is pointless and the member should be rendezvoused.</returns>
        public static bool CannotConverge(int attempts, long previousDelayTicks, long delayTicks)
        {
            // The first correction always gets its chance: most members are
            // simply late, and one seek fixes them.
            if (attempts < 2)
            {
                return false;
            }

            if (attempts >= MaxAttempts)
            {
                return true;
            }

            // Absolute, because overshooting by 4s is no better than
            // undershooting by 4s — both mean the seek did not land.
            var improvement = Math.Abs(previousDelayTicks) - Math.Abs(delayTicks);

            return improvement < ProgressTicks;
        }
    }
}
