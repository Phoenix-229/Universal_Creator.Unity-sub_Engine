// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ChunkBuildPriority.cs
//  Purpose: Order pending chunk builds by frustum first, then predicted distance (§ 9.9
//           G-58). A chunk behind the camera is worth less than a chunk in front of it no
//           matter how close it is, and distance-only ordering spends the whole build budget
//           behind the player - who never sees it. The priority is a BAND first, distance
//           second:
//             0. inside the frustum and near
//             1. inside the frustum and far
//             2. outside the frustum but along the predicted path (G-57)
//             3. everything else
//           and within a band, nearer builds first.
//
//  THE BAND IS RECOMPUTED FROM THE CURRENT FRUSTUM
//  -----------------------------------------------
//  Because the band is a pure function of the CURRENT frustum and predicted path, turning 180°
//  reclassifies every pending chunk: what was band 0 in front becomes band 3 behind, and a
//  chunk that was behind jumps to band 0. Re-sorting on the next debounce therefore re-prioritises
//  the queue instead of finishing the stale one first - which is the done-when. This class holds
//  no camera; the streaming layer classifies each candidate against the live frustum and sorts.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Booleans and a distance. The self-test pins the four bands, the nearer-first tiebreak, and the
//  180°-turn reordering of a whole list.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>The four priority bands for a pending chunk build, best (0) to worst (3).</summary>
    public enum BuildBand { InFrustumNear = 0, InFrustumFar = 1, AlongPredictedPath = 2, Elsewhere = 3 }

    /// <summary>
    /// Frustum-first, then predicted-distance ordering of pending chunk builds (§ 9.9 G-58). The band is a pure
    /// function of the current frustum and predicted path, so a 180° turn reclassifies and re-sorts the queue.
    /// Device-free.
    /// </summary>
    public static class ChunkBuildPriority
    {
        /// <summary>Classify a pending chunk into its priority band from the CURRENT frustum, predicted path and
        /// distance. Near means within <paramref name="nearRadiusM"/> metres.</summary>
        public static BuildBand Classify(bool insideFrustum, bool alongPredictedPath, float distanceM, float nearRadiusM)
        {
            if (insideFrustum)
                return distanceM <= nearRadiusM ? BuildBand.InFrustumNear : BuildBand.InFrustumFar;
            return alongPredictedPath ? BuildBand.AlongPredictedPath : BuildBand.Elsewhere;
        }

        /// <summary>Order two candidates: lower band first, then nearer first. Negative if A builds before B.</summary>
        public static int Compare(BuildBand bandA, float distA, BuildBand bandB, float distB)
        {
            if (bandA != bandB) return ((int)bandA).CompareTo((int)bandB);
            return distA.CompareTo(distB);
        }

        /// <summary>A pending chunk reduced to what ordering needs: its band and its distance.</summary>
        public readonly struct Candidate
        {
            public readonly BuildBand Band;
            public readonly float DistanceM;
            public Candidate(BuildBand band, float distanceM) { Band = band; DistanceM = distanceM; }
        }

        /// <summary>An <see cref="IComparer{T}"/> over candidates for <c>List.Sort</c>: band first, distance second.</summary>
        public sealed class CandidateComparer : IComparer<Candidate>
        {
            public static readonly CandidateComparer Instance = new CandidateComparer();
            public int Compare(Candidate a, Candidate b) => ChunkBuildPriority.Compare(a.Band, a.DistanceM, b.Band, b.DistanceM);
        }
    }
}
