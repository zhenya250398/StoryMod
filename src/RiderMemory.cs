using System.Collections.Generic;

namespace Mechworks
{
    /// <summary>
    /// Remembers who was recently riding a moving platform, across strokes.
    ///
    /// A carrier entity lives for one stroke only, so memory kept inside it dies every
    /// time. A machine like the rope hoist runs stroke after stroke with the same rider
    /// standing there the whole time, and losing the memory in between means every stroke
    /// has to re-acquire them with the strict pick-up tolerance. One marginal stroke and
    /// they fall.
    ///
    /// Lives on the mod system, so client and server keep separate copies.
    /// </summary>
    public class RiderMemory
    {
        readonly Dictionary<long, long> lastSupportedMs = new Dictionary<long, long>();

        /// <summary>Entries older than this are dropped during cleanup.</summary>
        const long ForgetAfterMs = 10000;

        long lastCleanupMs;

        public void Touch(long entityId, long nowMs)
        {
            lastSupportedMs[entityId] = nowMs;
            Cleanup(nowMs);
        }

        public bool WasRecentlySupported(long entityId, long nowMs, long graceMs)
        {
            return lastSupportedMs.TryGetValue(entityId, out long lastMs)
                && nowMs - lastMs <= graceMs;
        }

        public void Forget(long entityId)
        {
            lastSupportedMs.Remove(entityId);
        }

        void Cleanup(long nowMs)
        {
            if (nowMs - lastCleanupMs < ForgetAfterMs) return;
            lastCleanupMs = nowMs;

            List<long> stale = null;
            foreach (KeyValuePair<long, long> pair in lastSupportedMs)
            {
                if (nowMs - pair.Value <= ForgetAfterMs) continue;
                (stale ??= new List<long>()).Add(pair.Key);
            }

            if (stale == null) return;
            foreach (long id in stale) lastSupportedMs.Remove(id);
        }
    }
}
