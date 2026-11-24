// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ProbeRefreshScheduler.cs
//  Purpose: Decide which stored probes to refill this frame and in what order, so the
//           bounce light tracks the world without ever spiking a frame. Probes go dirty
//           on three events - a chunk loading or rebuilding, the sun moving past a few
//           degrees of elevation, or cloud cover crossing its hysteresis band - and drain
//           at a fixed per-frame budget from a queue ordered by distance to the camera and
//           then by grid index. That second key is the point: distance alone can tie, and
//           a hash set's order is not stable across runs, so the index tiebreaker is what
//           makes the refresh order total and therefore identical on two runs (C-06).
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// A deterministic, budgeted work queue for probe refills. The three static trigger
    /// tests answer "has this input moved enough to matter"; the instance holds the dirty
    /// set and drains it nearest-first at the per-frame budget. It stores only probe
    /// indices - the grid and the filler own the probes themselves - so it is cheap to keep
    /// one per grid.
    /// </summary>
    public sealed class ProbeRefreshScheduler
    {
        private readonly HashSet<int> dirtySet = new HashSet<int>();
        private readonly List<int> pending = new List<int>();
        private System.Func<int, float> currentDistance;
        private readonly System.Comparison<int> compareDistance;

        /// <summary>Creates one main-thread queue; caches its sort delegate so paced drains do not allocate a closure per frame.</summary>
        public ProbeRefreshScheduler() { compareDistance = CompareDistance; } // UC-T08-12: native owner drives this queue every runtime frame.
        private int CompareDistance(int a, int b)
        {
            int result = currentDistance(a).CompareTo(currentDistance(b));
            return result != 0 ? result : a.CompareTo(b);
        }

        /// <summary>The number of probes still waiting to be refilled.</summary>
        public int PendingCount { get { return pending.Count; } }

        /// <summary>True once the sun's elevation has moved at least the refresh band from
        /// where the grid was last filled - the direct-light trigger.</summary>
        public static bool SunMovedEnough(float lastElevationDeg, float currentElevationDeg)
        {
            return Mathf.Abs(currentElevationDeg - lastElevationDeg) >= LightingConstants.IrradianceSunRefreshDeg;
        }

        /// <summary>True once cloud cover has crossed its shared hysteresis band - the
        /// diffuse-light and cloud-shadow trigger. Defers to the one cover-delta constant so
        /// the shadow cookie and the probe grid refresh on the same threshold.</summary>
        public static bool CloudMovedEnough(float lastCover01, float currentCover01)
        {
            return CloudShadows.ShouldRefreshProbes(lastCover01, currentCover01);
        }

        /// <summary>Mark one probe dirty (a chunk rebuild, or a targeted refill). Ignored if
        /// it is already queued, so a probe is never filled twice in one drain.</summary>
        public void MarkDirty(int probeIndex)
        {
            if (probeIndex < 0) return;
            if (dirtySet.Add(probeIndex)) pending.Add(probeIndex);
        }

        /// <summary>Mark a contiguous run of probes dirty - a whole grid on a global trigger
        /// (sun or cloud), or one chunk's slice on a rebuild.</summary>
        public void MarkRange(int startIndex, int count)
        {
            int end = startIndex + count;
            for (int i = startIndex; i < end; i++) MarkDirty(i);
        }

        /// <summary>Drop every pending probe without filling any - for a hard reset.</summary>
        public void Clear()
        {
            dirtySet.Clear();
            pending.Clear();
        }

        /// <summary>
        /// Take up to the per-frame budget of probes, nearest camera first with grid index
        /// as the tiebreaker, remove them from the queue and write their indices into
        /// <paramref name="outIndices"/>. Returns how many were written. The sort key is a
        /// total order (index is unique), so the drained order is identical on two runs with
        /// the same camera and the same dirty set - independent of insertion or hash order.
        /// Uses the fixed per-frame cap; for the wall-clock paced count (S-68) pass an
        /// explicit budget through the overload below.
        /// </summary>
        public int Drain(int[] outIndices, System.Func<int, float> distanceToCamera)
        {
            return Drain(outIndices, distanceToCamera, LightingConstants.IrradianceRefreshBudgetPerFrame);
        }

        /// <summary>
        /// As above, but drain at most <paramref name="maxThisTick"/> probes - the count a
        /// <see cref="RefreshCadence"/> hands out for this frame so the refresh rate is paced
        /// on the wall clock rather than the frame rate (S-68). The ordering is unchanged and
        /// still a total order, so two machines draining the same dirty set in the same paced
        /// counts refill the same probes in the same sequence.
        /// </summary>
        public int Drain(int[] outIndices, System.Func<int, float> distanceToCamera, int maxThisTick)
        {
            if (outIndices == null || pending.Count == 0 || maxThisTick <= 0) return 0;

            if (distanceToCamera != null)
            {
                currentDistance = distanceToCamera;
                pending.Sort(compareDistance);
                currentDistance = null;
            }
            else
            {
                pending.Sort();   // pure index order when there is no camera
            }

            int budget = Mathf.Max(0, maxThisTick);
            int take = Mathf.Min(budget, Mathf.Min(outIndices.Length, pending.Count));
            for (int i = 0; i < take; i++)
            {
                int idx = pending[i];
                outIndices[i] = idx;
                dirtySet.Remove(idx);
            }
            pending.RemoveRange(0, take);
            return take;
        }
    }
}
