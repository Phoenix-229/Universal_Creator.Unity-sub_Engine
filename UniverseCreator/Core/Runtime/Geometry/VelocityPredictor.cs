// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    VelocityPredictor.cs
//  Purpose: Prefetch ahead of the camera from SMOOTHED velocity, never raw (§ 9.9 G-57,
//           carried from M5 task 33). predicted = cameraPos + smoothedVelocity ·
//           predictionSeconds. Raw per-frame velocity swings with every mouse flick, and
//           prefetching on it loads the wrong hemisphere and then throws it away - measurably
//           worse than not predicting at all. The fix is an exponential moving average of
//           velocity over velocityWindowSeconds (default 0.5 s).
//
//  VELOCITY COMES FROM TRANSLATION, NOT ROTATION
//  ---------------------------------------------
//  Velocity is measured from the change in POSITION, so a mouse flick that only rotates the
//  camera produces zero velocity and therefore no predicted offset - which is exactly the
//  done-when: a fast flick with no translation triggers no prefetch. Steady forward motion
//  drives the EMA up to the travel speed, so the predicted point runs ahead of the camera and
//  the ring prefetches in the direction of travel.
//
//  THE EMA IS FRAME-RATE INDEPENDENT
//  ---------------------------------
//  The blend weight is 1 − exp(−dt / window), not a fixed per-frame constant, so the smoothing
//  time-constant is the same whether the frame ran at 30 or 240 Hz. `ShouldPrefetch` applies a
//  deadband in metres so a sub-threshold predicted offset (a flick, a jitter) does not kick off
//  a prefetch. Pure and device-free; the self-test pins the flick-vs-travel behaviour, the
//  deadband and the frame-rate independence.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Predicts a prefetch point ahead of the camera from an EMA-smoothed velocity (§ 9.9 G-57): a mouse flick
    /// (rotation, no translation) yields zero velocity and no prefetch; steady travel prefetches ahead. The EMA
    /// weight is frame-rate independent. Device-free.
    /// </summary>
    public sealed class VelocityPredictor
    {
        private Vector3 _smoothedVelocity;
        private Vector3 _lastPos;
        private bool _hasLast;

        /// <summary>The current EMA-smoothed velocity in metres per second.</summary>
        public Vector3 SmoothedVelocity => _smoothedVelocity;

        /// <summary>The frame-rate-independent EMA weight for a step of <paramref name="dt"/> seconds and a smoothing
        /// window of <paramref name="windowSeconds"/>: 1 − exp(−dt/window), in [0, 1]. A non-positive window snaps
        /// (weight 1); a non-positive dt holds (weight 0).</summary>
        public static float ExponentialAlpha(float dt, float windowSeconds)
        {
            if (windowSeconds <= 0f) return 1f;
            if (dt <= 0f) return 0f;
            return 1f - Mathf.Exp(-dt / windowSeconds);
        }

        /// <summary>Reset the smoother to a known position with zero velocity (also used on a teleport, G-60).</summary>
        public void Reset(Vector3 position)
        {
            _lastPos = position;
            _smoothedVelocity = Vector3.zero;
            _hasLast = true;
        }

        /// <summary>Observe the camera at a new position after <paramref name="dt"/> seconds and fold the raw velocity
        /// into the EMA. Returns the updated smoothed velocity. The first observation seeds position at zero velocity.</summary>
        public Vector3 Observe(Vector3 position, float dt, float windowSeconds)
        {
            if (!_hasLast) { Reset(position); return _smoothedVelocity; }
            if (dt > 0f)
            {
                Vector3 raw = (position - _lastPos) / dt;
                float a = ExponentialAlpha(dt, windowSeconds);
                _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, raw, a);
            }
            _lastPos = position;
            return _smoothedVelocity;
        }

        /// <summary>The predicted prefetch point: last observed position + smoothed velocity · predictionSeconds.</summary>
        public Vector3 Predict(float predictionSeconds)
        {
            return _lastPos + _smoothedVelocity * predictionSeconds;
        }

        /// <summary>Whether the predicted offset is worth a prefetch: the predicted point must be at least
        /// <paramref name="thresholdM"/> metres from the current point. The deadband is what makes a flick (or jitter)
        /// trigger no prefetch.</summary>
        public static bool ShouldPrefetch(Vector3 predicted, Vector3 current, float thresholdM)
        {
            float t = thresholdM < 0f ? 0f : thresholdM;
            return (predicted - current).sqrMagnitude >= t * t;
        }
    }
}
