// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ThermalGovernor.cs
//  Purpose: A thermal governor, because a phone throttles whether or not we plan for it
//           (§ 9.10 G-65, L-52). Sustained load makes an SoC reduce its clocks, and the frame
//           time drifts up with the SCENE UNCHANGED. That drift is the observable, and it needs
//           no platform-specific API: compare the current rolling median against the median
//           recorded at the SAME triangle-and-draw-call complexity earlier in the session. If
//           the frame time has risen beyond thermalDriftThreshold (default 15 %) at matched
//           complexity, the device is throttling. Step down one tier BEFORE it does - the
//           device's own response is coarser and arrives as a visible collapse, not a step.
//
//  MATCHED COMPLEXITY IS THE TRICK
//  -------------------------------
//  A higher frame time at a heavier scene is not throttling - it is just more work. So drift is
//  only meaningful against a baseline captured at the same complexity bucket. The governor records
//  the COOLEST (minimum) median it has seen at each bucket, and compares later readings in that
//  same bucket against it. A drift at a bucket with no baseline yet is not actionable.
//
//  STEP ONCE, THEN HOLD
//  --------------------
//  After a thermal step-down the governor latches: it does not step again until it is re-baselined
//  at the new tier. Without the latch it would keep firing on the same drift and sawtooth. Pure and
//  device-free; the self-test pins the drift maths, the matched-complexity requirement, and the
//  step-once-and-hold over a simulated sustained run.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Detects SoC throttling as frame-time drift at matched complexity (§ 9.10 G-65): compare the current median
    /// against the coolest median seen earlier at the same triangle/draw-call bucket; drift beyond the threshold
    /// steps the tier down once, then holds. No platform API. Device-free.
    /// </summary>
    public sealed class ThermalGovernor
    {
        private readonly Dictionary<long, float> _baselineByComplexity = new Dictionary<long, float>();
        private readonly float _driftThreshold;
        private readonly int _triBucket;
        private readonly int _drawBucket;
        private bool _steppedDown;

        public ThermalGovernor(float thermalDriftThreshold = 0.15f, int triangleBucket = 50000, int drawCallBucket = 50)
        {
            _driftThreshold = thermalDriftThreshold < 0f ? 0f : thermalDriftThreshold;
            _triBucket = triangleBucket < 1 ? 1 : triangleBucket;
            _drawBucket = drawCallBucket < 1 ? 1 : drawCallBucket;
        }

        /// <summary>The complexity bucket key for a triangle and draw-call count: two readings map to the same key
        /// when they are the "same complexity".</summary>
        public long ComplexityKey(float triangles, float drawCalls)
        {
            long tb = (long)(triangles / _triBucket);
            long db = (long)(drawCalls / _drawBucket);
            return (tb << 20) ^ db;
        }

        /// <summary>The fractional frame-time rise from a baseline: (current − baseline) / baseline. Zero on a
        /// non-positive baseline.</summary>
        public static float DriftFraction(float currentMs, float baselineMs)
        {
            return baselineMs <= 0f ? 0f : (currentMs - baselineMs) / baselineMs;
        }

        /// <summary>Record a median reading at its complexity bucket, keeping the COOLEST (minimum) seen there as the
        /// baseline. Feed this the early-session, un-throttled readings.</summary>
        public void Observe(float triangles, float drawCalls, float medianMs)
        {
            long key = ComplexityKey(triangles, drawCalls);
            if (!_baselineByComplexity.TryGetValue(key, out float b) || medianMs < b)
                _baselineByComplexity[key] = medianMs;
        }

        /// <summary>Whether the tier should step down for thermal drift: a matched-complexity baseline exists and the
        /// current median has risen past the drift threshold, and we have not already stepped down. Returns false
        /// once latched, so the governor steps once and holds.</summary>
        public bool ShouldStepDown(float triangles, float drawCalls, float medianMs)
        {
            if (_steppedDown) return false;
            long key = ComplexityKey(triangles, drawCalls);
            if (!_baselineByComplexity.TryGetValue(key, out float baseline)) return false;
            return DriftFraction(medianMs, baseline) > _driftThreshold;
        }

        /// <summary>Latch after a thermal step-down: the governor holds until re-baselined at the new tier.</summary>
        public void MarkSteppedDown() => _steppedDown = true;

        /// <summary>Whether a thermal step-down has already been taken and is being held.</summary>
        public bool HasSteppedDown => _steppedDown;

        /// <summary>Re-baseline at a new tier: clear the recorded baselines and the step-down latch.</summary>
        public void Rebaseline()
        {
            _baselineByComplexity.Clear();
            _steppedDown = false;
        }
    }
}
