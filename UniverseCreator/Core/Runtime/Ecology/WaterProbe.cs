using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// One water body, reduced to the four facts the ecology layer needs, all of
    /// them value types.
    ///
    /// The reduction is deliberate and it is what makes off-thread sampling
    /// legal. A live water body is a MonoBehaviour, and reading a
    /// MonoBehaviour's fields from a worker thread is not allowed, so the
    /// scatter pass cannot hold one. The caller flattens the bodies into an
    /// array of these on the main thread, once per chunk, and the workers read
    /// only the copy.
    ///
    /// surfaceY is the still-water level with no wave motion in it. Waves move
    /// with time, and a placement that shifted with the clock would not be
    /// reproducible - the same seed would grow a different forest on a second
    /// run, which is the one thing the determinism constraint forbids.
    /// </summary>
    public struct WaterProbe
    {
        /// <summary>World-space bounds of the body. Its XZ footprint is the shoreline distance is measured to.</summary>
        public Bounds bounds;

        /// <summary>m. Still-water surface height, sampled with wave motion excluded.</summary>
        public float surfaceY;

        public WaterBodyKind kind;
    }

    /// <summary>
    /// Nearest-water queries over a flattened set of bodies.
    ///
    /// Distances are horizontal. A lake at the foot of a cliff is a few metres
    /// away from the cliff top in plan view and a hundred below it, and it is
    /// the plan-view distance that decides whether roots can reach the water
    /// table or whether spray carries - so height is deliberately not in the
    /// measure, and submersion is tested separately against the surface level.
    /// </summary>
    public static class WaterProximity
    {
        /// <summary>
        /// Nearest body of any kind within the search radius.
        ///
        /// Returns false when nothing is in range, in which case the caller
        /// reports the search radius as the distance rather than infinity: a
        /// riparian species then simply fails its distance band instead of
        /// needing a no-water special case anywhere downstream.
        ///
        /// Equidistant bodies resolve to the lower array index, so a reordered
        /// list cannot change the result.
        /// </summary>
        public static bool Nearest(
            WaterProbe[] probes,
            int count,
            float worldX,
            float worldZ,
            float searchRadiusM,
            out float distanceM,
            out int index)
        {
            distanceM = searchRadiusM;
            index = -1;

            if (probes == null) return false;
            if (count > probes.Length) count = probes.Length;

            float best = searchRadiusM;

            for (int i = 0; i < count; i++)
            {
                float d = HorizontalDistanceTo(probes[i].bounds, worldX, worldZ);
                if (d < best)
                {
                    best = d;
                    index = i;
                }
            }

            if (index < 0) return false;

            distanceM = best;
            return true;
        }

        /// <summary>
        /// Distance to the nearest salt body, for the spray decay.
        ///
        /// Only the ocean carries salt. A freshwater lake at the same distance
        /// puts none on the leaves, so it must not be allowed to stand in for
        /// one - hence a separate query rather than a distance to any water.
        /// </summary>
        public static float DistanceToSaltM(
            WaterProbe[] probes,
            int count,
            float worldX,
            float worldZ,
            float searchRadiusM)
        {
            if (probes == null) return searchRadiusM;
            if (count > probes.Length) count = probes.Length;

            float best = searchRadiusM;

            for (int i = 0; i < count; i++)
            {
                if (probes[i].kind != WaterBodyKind.Ocean) continue;

                float d = HorizontalDistanceTo(probes[i].bounds, worldX, worldZ);
                if (d < best) best = d;
            }

            return best;
        }

        /// <summary>
        /// Exact distance from a point to an axis-aligned rectangle in plan
        /// view, and zero when the point is over it.
        /// </summary>
        public static float HorizontalDistanceTo(Bounds bounds, float worldX, float worldZ)
        {
            float dx = Mathf.Max(0f, Mathf.Max(bounds.min.x - worldX, worldX - bounds.max.x));
            float dz = Mathf.Max(0f, Mathf.Max(bounds.min.z - worldZ, worldZ - bounds.max.z));

            if (dx <= 0f && dz <= 0f) return 0f;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
