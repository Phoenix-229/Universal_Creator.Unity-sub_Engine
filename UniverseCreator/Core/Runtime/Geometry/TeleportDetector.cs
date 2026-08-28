// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    TeleportDetector.cs
//  Purpose: A teleport abandons prediction and refills the ring by radius (§ 9.9 G-60).
//           Prediction (G-57) and the priority queue (G-58) both assume continuous motion;
//           across a fast-travel jump they are meaningless. Keeping a stale queue after a
//           teleport is exactly how an arrival shows untextured ground for several seconds -
//           the scheduler is still draining chunks around the OLD position. So a jump greater
//           than teleportThresholdM must: clear the pending queue, reset the velocity smoother
//           to the new position (zero velocity), and refill outward by radius from there with
//           the frustum priority of G-58.
//
//  DETECTION IS A DISTANCE, NOT A FLAG
//  -----------------------------------
//  There is no "teleport event" to trust - the camera just appears somewhere else. `IsTeleport`
//  compares the frame-to-frame jump against the threshold; a normal fast run stays under it, a
//  fast-travel warp blows past it. `Observe` tracks the last position and reports the jump so the
//  scheduler can act on the same frame the camera moved.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Positions and a threshold; the queue-clear, the predictor reset and the radial refill are the
//  streaming layer. The self-test pins that a normal step is not a teleport, a warp is, and that
//  near-first ordering resolves the ring around the NEW position.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Detects a teleport as a frame-to-frame jump past a threshold (§ 9.9 G-60): the signal to clear the pending
    /// queue, reset prediction to the new position and refill by radius. Device-free; the streaming layer performs
    /// the clear and refill.
    /// </summary>
    public sealed class TeleportDetector
    {
        private Vector3 _lastPos;
        private bool _hasLast;

        /// <summary>Whether a jump between two positions exceeds the teleport threshold.</summary>
        public static bool IsTeleport(Vector3 previousPos, Vector3 newPos, float teleportThresholdM)
        {
            float t = teleportThresholdM < 0f ? 0f : teleportThresholdM;
            return (newPos - previousPos).sqrMagnitude > t * t;
        }

        /// <summary>Reset the detector's reference to a known position (call after handling a teleport, or on start).</summary>
        public void Reset(Vector3 position) { _lastPos = position; _hasLast = true; }

        /// <summary>Observe the camera at a new position and report whether the move was a teleport. The first
        /// observation seeds the reference and is never a teleport. The reference always advances to the new
        /// position, so the next frame is measured from where the camera now is.</summary>
        public bool Observe(Vector3 position, float teleportThresholdM)
        {
            if (!_hasLast) { Reset(position); return false; }
            bool jumped = IsTeleport(_lastPos, position, teleportThresholdM);
            _lastPos = position;
            return jumped;
        }

        /// <summary>Radial fill order from the new position: nearer chunks first, so the near ring resolves before any
        /// distant chunk (used to seed the refill; G-58 then layers frustum priority on top). Negative if A first.</summary>
        public static int CompareByRadius(float distA, float distB) => distA.CompareTo(distB);
    }
}
