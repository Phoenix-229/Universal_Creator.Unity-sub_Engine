// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    RecoveryController.cs
//  Purpose: Recover from degradation with hysteresis, in reverse order (§ 9.10 G-64).
//           Recovery is not the mirror of degradation - if the renderer stepped back up the
//           instant the frame time dipped under the ceiling, it would oscillate between two
//           states once a second, which is more noticeable than simply staying degraded. So
//           recovery has two guards degradation does not:
//             - a MARGIN: the metric must be under recoverThreshold × ceiling (default 0.8),
//               not merely under the ceiling, and
//             - a HOLD: it must stay under that margin continuously for recoverHoldSeconds
//               (default 5 s) before the first step back.
//           Then it steps back up one at a time, reversing the G-63 order.
//
//  THE DEAD BAND IS [margin, ceiling]
//  ----------------------------------
//  A metric sitting between 0.8 × ceiling and the ceiling neither degrades (that needs to exceed
//  the ceiling) nor recovers (that needs to drop under the margin). That gap is what stops the
//  sawtooth. Any excursion back above the margin during the hold resets the hold to zero, so a
//  flickering load cannot creep the system back up a step at a time.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  A hold accumulator and a fraction; the actual step-up is DegradationLadder.TryStepUp (G-63),
//  gated by the same interval. The self-test pins the margin, the reset-on-breach, the dead band,
//  and a transient breach recovering fully well within fifteen seconds with no step-down.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Recovery hysteresis for the degradation ladder (§ 9.10 G-64): a margin (recoverThreshold × ceiling) and a
    /// continuous hold (recoverHoldSeconds) before stepping back up in reverse order. The dead band between margin
    /// and ceiling kills the sawtooth. Device-free.
    /// </summary>
    public sealed class RecoveryController
    {
        private readonly float _holdSeconds;
        private readonly float _thresholdFraction;
        private float _held;

        public RecoveryController(float recoverHoldSeconds = 5f, float recoverThreshold = 0.8f)
        {
            _holdSeconds = recoverHoldSeconds < 0f ? 0f : recoverHoldSeconds;
            _thresholdFraction = recoverThreshold < 0f ? 0f : (recoverThreshold > 1f ? 1f : recoverThreshold);
        }

        /// <summary>The recovery margin: recoverThreshold × ceiling. The metric must drop under this, not merely
        /// under the ceiling, before recovery may begin.</summary>
        public float MarginThreshold(float ceiling) => ceiling * _thresholdFraction;

        /// <summary>Seconds the metric has been continuously under the margin.</summary>
        public float HeldSeconds => _held;

        /// <summary>Reset the hold (e.g. after a fresh degradation or a pin change).</summary>
        public void Reset() => _held = 0f;

        /// <summary>Observe the current median metric for a step of <paramref name="dt"/> seconds and return whether
        /// recovery is permitted now: true once the metric has stayed under the margin for the full hold. An
        /// excursion above the margin resets the hold to zero.</summary>
        public bool Observe(float dt, float medianMetric, float ceiling)
        {
            if (medianMetric < MarginThreshold(ceiling)) _held += (dt < 0f ? 0f : dt);
            else _held = 0f;
            return _held >= _holdSeconds;
        }
    }
}
