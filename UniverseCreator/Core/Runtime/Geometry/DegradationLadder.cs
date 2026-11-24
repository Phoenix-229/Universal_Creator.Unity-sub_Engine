// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    DegradationLadder.cs
//  Purpose: An ordered, published degradation sequence (§ 9.10 G-63) - and vegetation counts
//           are NOT in it (G-40). When the budget is exceeded the renderer degrades in exactly
//           this order, one step per degradeIntervalSeconds, logging each step once:
//             0. raise acuityPixels (terrain silhouette error) toward the tier cap
//             1. shrink the § 9.5 overlay ring by one row
//             2. pull the impostor switch distance in
//             3. drop shadow distance for props (G-38)
//             4. reduce § 9.7 cache range by one ring
//             5. disable the overlay's tessellation (fall back to G-24's pre-subdivided mesh)
//             6. disable the overlay entirely
//
//  STEM COUNTS ARE NOT A DEGRADATION STEP
//  --------------------------------------
//  § 5 and § 6 decide how many stems exist; this section decides only what they COST. A renderer
//  that thins a forest to make a frame rate has changed the world the player is in, and no frame
//  rate justifies that. `AffectsStemCount` returns false for every step, and the self-test walks
//  the whole ladder asserting a stem count handed alongside it is untouched - the done-when.
//
//  THE LADDER IS MONOTONIC, SO EACH STEP LOGS ONCE
//  -----------------------------------------------
//  A single level index advances 0→7 going down and back up on recovery (G-64 adds the hysteresis
//  around it). Because it moves one level at a time and never revisits a level while descending,
//  each step is entered exactly once - which is how "one log line per step" is guaranteed rather
//  than hoped. Pure and device-free; applying a step to real view state is the scene layer.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>The ordered degradation steps (§ 9.10 G-63), applied top to bottom. Every step is a view-side cost
    /// change; none alters the world (C-04, G-67).</summary>
    public enum DegradeStep
    {
        AcuityPixels = 0,
        OverlayRingRow = 1,
        ImpostorSwitchDistance = 2,
        PropShadowDistance = 3,
        CacheRangeRing = 4,
        OverlayTessellation = 5,
        OverlayDisabled = 6,
    }

    /// <summary>
    /// The monotonic degradation ladder (§ 9.10 G-63): descends one ordered step at a time so each logs exactly
    /// once, and provably never touches stem counts. G-64 layers recovery hysteresis on top. Device-free.
    /// </summary>
    public sealed class DegradationLadder
    {
        /// <summary>The number of degradation steps.</summary>
        public const int StepCount = 7;

        private int _level;   // 0 = undegraded, StepCount = fully degraded

        /// <summary>The current degradation level: 0 undegraded, up to <see cref="StepCount"/> fully degraded.</summary>
        public int Level => _level;
        public bool AtTop => _level <= 0;
        public bool AtBottom => _level >= StepCount;

        /// <summary>The step that the next <see cref="TryStepDown"/> would apply (only valid when not at the bottom).</summary>
        public DegradeStep NextDownStep => (DegradeStep)_level;

        /// <summary>Apply the next degradation step in order. Returns false at the bottom. Because the level advances
        /// one at a time and never revisits a level while descending, the caller logs each step exactly once.</summary>
        public bool TryStepDown(out DegradeStep applied)
        {
            if (AtBottom) { applied = DegradeStep.OverlayDisabled; return false; }
            applied = (DegradeStep)_level;
            _level++;
            return true;
        }

        /// <summary>Undo the most recent degradation step (recovery reverses the order). Returns false at the top.</summary>
        public bool TryStepUp(out DegradeStep removed)
        {
            if (AtTop) { removed = DegradeStep.AcuityPixels; return false; }
            _level--;
            removed = (DegradeStep)_level;
            return true;
        }

        /// <summary>Whether the degradation interval has elapsed, gating one step per degradeIntervalSeconds.</summary>
        public static bool IntervalElapsed(float lastStepSeconds, float nowSeconds, float intervalSeconds)
        {
            return nowSeconds - lastStepSeconds >= intervalSeconds;
        }

        /// <summary>Whether a step changes vegetation stem COUNT: always false. Counts are owned by § 5 and § 6;
        /// this ladder only changes cost, never the world (G-40).</summary>
        public static bool AffectsStemCount(DegradeStep step) => false;
    }
}
