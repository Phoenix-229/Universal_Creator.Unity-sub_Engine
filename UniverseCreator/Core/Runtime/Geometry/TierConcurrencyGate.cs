// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    TierConcurrencyGate.cs
//  Purpose: Cap chunk builds in flight per tier, and enforce it - do not hope for it (§ 9.9
//           G-61, C-14). The caps are 1 / 2 / 4 / 6 by tier (§ 9.12): a phone runs one build
//           at a time, a workstation six. Exceeding the cap does not fail cleanly - it
//           stutters, which is harder to diagnose than an outright error, so the gate admits
//           a build only when there is a slot and refuses otherwise. One debounce interval
//           (100 ms) coalesces request bursts; cancellation drains the in-flight count to zero
//           so no partial terrain is left orphaned.
//
//  ADMISSION IS A COUNTER, NOT A CONVENTION
//  ----------------------------------------
//  `TryBegin` increments the in-flight count and returns true only while it is below the cap;
//  `End` decrements it when a build finishes. The scheduler cannot exceed the cap because there
//  is no path that admits a build without a slot - the enforcement is the counter itself. A peak
//  counter records the high-water mark so a stress test can assert the cap was never breached.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  An integer count and a cap; no threads, no tasks, no StopToken (that is the scheduler's, above).
//  The self-test pins the per-tier caps, that fifty simultaneous requests never exceed the cap, and
//  that a cancel drains the count to zero while leaving the cap intact for the refill.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Per-tier concurrency cap for chunk builds (§ 9.9 G-61, C-14): admits a build only when a slot is free, so
    /// the cap (1/2/4/6 by tier) cannot be exceeded. Cancellation drains to zero. Device-free.
    /// </summary>
    public sealed class TierConcurrencyGate
    {
        private static readonly int[] CapsByTier = { 1, 2, 4, 6 };

        private readonly int _cap;
        private int _inFlight;
        private int _peakInFlight;

        /// <summary>The in-flight cap for a tier: 1 / 2 / 4 / 6 for tiers 0..3, clamped to that range.</summary>
        public static int CapForTier(int tier)
        {
            if (tier < 0) tier = 0;
            if (tier > 3) tier = 3;
            return CapsByTier[tier];
        }

        /// <summary>A gate with an explicit cap (clamped to at least 1).</summary>
        public TierConcurrencyGate(int cap) { _cap = cap < 1 ? 1 : cap; }

        /// <summary>A gate whose cap is the per-tier cap for <paramref name="tier"/>.</summary>
        public static TierConcurrencyGate ForTier(int tier) => new TierConcurrencyGate(CapForTier(tier));

        public int Cap => _cap;
        public int InFlight => _inFlight;
        public int Available => _cap - _inFlight;
        public int PeakInFlight => _peakInFlight;

        /// <summary>Admit a build if a slot is free: increment the in-flight count and return true, else return false.
        /// This is the only path that starts a build, so the cap cannot be exceeded.</summary>
        public bool TryBegin()
        {
            if (_inFlight >= _cap) return false;
            _inFlight++;
            if (_inFlight > _peakInFlight) _peakInFlight = _inFlight;
            return true;
        }

        /// <summary>Release a slot when a build finishes (clamped at zero).</summary>
        public void End() { if (_inFlight > 0) _inFlight--; }

        /// <summary>Cancel all in-flight builds: drain the count to zero. The cap is unchanged, so the ring can refill
        /// immediately with no orphaned partial terrain left counted.</summary>
        public void CancelAll() { _inFlight = 0; }

        /// <summary>Whether the debounce interval has elapsed since the last request (default 100 ms), so a burst of
        /// requests coalesces into one scheduling pass.</summary>
        public static bool DebounceElapsed(float lastRequestMs, float nowMs, float debounceMs = 100f)
        {
            return nowMs - lastRequestMs >= debounceMs;
        }
    }
}
