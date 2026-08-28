// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    RefreshCadence.cs
//  Purpose: Pace the probe grid's refills on the wall clock, not the frame. The
//           multi-bounce feedback (S-67) advances one bounce every time a probe is
//           refilled, so if refills are budgeted per frame the light compounds faster
//           on a 144 Hz monitor than on a 30 fps phone - and a scene whose sun is
//           always moving never fully settles, so the faster device sits permanently
//           closer to the steady state and reads brighter. Drive a fixed number of
//           refills per real second instead and both devices do the same bounce work
//           per second, so the same place at the same simulated time looks the same on
//           both. The fractional remainder is carried between frames, so the long-run
//           rate is exact rather than rounded down every frame; a per-frame cap still
//           guards a hitch from spiking the cost, and a device too slow to hold the
//           rate simply converges more slowly toward the identical steady state.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// A wall-clock refresh pacer for <see cref="ProbeRefreshScheduler"/>. Holds one
    /// float of carried fractional budget and turns a frame's delta time into an integer
    /// count of probes to drain this frame, at a fixed per-second rate. Frame-rate
    /// independent by construction: summed over any real second the count equals the
    /// rate (until the per-frame cap bites on a slow device), so the feedback series
    /// advances at the same speed on every machine and converges to the same brightness.
    /// A value type - the driver keeps one per grid and it costs four bytes.
    /// </summary>
    public struct RefreshCadence
    {
        // Probes owed but not yet spent, carried forward so flooring each frame does not
        // quietly lose a fraction of the rate over thousands of frames.
        private float carry;

        /// <summary>
        /// How many probes to refill this frame to hold <paramref name="probesPerSecond"/>
        /// regardless of frame rate. The fractional remainder is carried between calls, so
        /// the long-run average is exactly the rate. The count is capped at
        /// <paramref name="perFrameCap"/> so a long frame cannot spike the cost; when the
        /// cap bites the unpaid deficit is dropped rather than carried, or it would burst
        /// on the following frame and re-spike. Non-positive time or rate yields zero.
        /// </summary>
        public int ProbesThisFrame(float deltaTime, float probesPerSecond, int perFrameCap)
        {
            if (deltaTime <= 0f || probesPerSecond <= 0f || perFrameCap <= 0) return 0;

            carry += probesPerSecond * deltaTime;
            int n = Mathf.FloorToInt(carry);
            if (n <= 0) return 0;
            carry -= n;

            if (n > perFrameCap)
            {
                n = perFrameCap;
                carry = 0f;   // drop the deficit: a slow device converges slower, never bursts
            }
            return n;
        }

        /// <summary>Convenience overload using the module's paced rate and per-frame cap.</summary>
        public int ProbesThisFrame(float deltaTime)
        {
            return ProbesThisFrame(deltaTime,
                                   LightingConstants.IrradianceRefreshProbesPerSecond,
                                   LightingConstants.IrradianceRefreshBudgetPerFrame);
        }

        /// <summary>Clear the carried budget - for a hard reset or a teleport, so the pace
        /// starts clean rather than paying a large one-frame catch-up after a stall.</summary>
        public void Reset() { carry = 0f; }
    }
}
