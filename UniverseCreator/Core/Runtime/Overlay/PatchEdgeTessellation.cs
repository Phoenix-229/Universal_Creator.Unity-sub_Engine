// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PatchEdgeTessellation.cs
//  Purpose: The per-EDGE tessellation factor for the micro-relief patch ring. Hardware
//           tessellation subdivides each patch edge independently; if two patches that
//           share an edge feed the hull stage two different factors for it, the GPU
//           subdivides that seam two different ways and a crack opens along it. The one
//           rule that closes the seam: derive the factor for an edge from a quantity the
//           two patches sharing it can only agree on. That quantity is the edge's own
//           midpoint - never the patch centre, which differs across the seam. This file
//           is the pure, allocation-free maths of that: it addresses each edge by a
//           canonical grid coordinate the neighbour computes identically, and turns the
//           camera distance to that midpoint into a subdivision factor.
//
//  WHY A SHARED INTEGER ADDRESS, NOT centre +/- size/2  (L-50 adjacent)
//  --------------------------------------------------------------------
//  It is not enough to "use the midpoint": if patch A computes its east edge as
//  centreA + size/2 and neighbour B computes its west edge as centreB - size/2, the two
//  are equal in algebra but can differ by a floating-point ULP, and one ULP is a visible
//  crack at distance. So the midpoint is addressed in HALF-PATCH integer steps: the edge
//  between cell k and cell k+1 has the single half-index 2k+1 from either side, and its
//  world position is that one integer times size/2 - bit-identical for both patches. The
//  factor is then a pure function of that shared address and the camera, so a shared edge
//  yields one factor by construction, not by luck. That is what makes the seam provably
//  closed rather than closed-until-someone-reorders-an-add.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.Overlay
{
    /// <summary>The four edges of a square patch, named by compass side. Each borders one neighbour.</summary>
    public enum PatchEdge { South, North, West, East }

    /// <summary>
    /// Pure, static per-edge tessellation maths for the patch ring. Every factor is a function of the
    /// edge's shared half-integer grid address and the camera - never of a patch centre - so two patches
    /// meeting at an edge compute the identical factor and no crack opens. Allocates nothing.
    /// </summary>
    public static class PatchEdgeTessellation
    {
        /// <summary>
        /// Target angular size (radians) of one tessellated segment. Derived, not guessed: at 1080 px over
        /// a ~60 deg vertical FOV one pixel subtends ~0.00097 rad, and aiming for ~8 px per segment gives
        /// ~0.0078 rad. Larger = coarser. The caller may override per tier; this is the desktop default.
        /// </summary>
        public const float DefaultSegmentAngleRad = 0.0075f;

        /// <summary>
        /// The GPU subdivision ceiling for a quality tier. Low ships no hardware tessellation (it takes
        /// the G-24 pre-subdivided fallback instead), so its ceiling is 1 - a factor of 1 means "do not
        /// subdivide this edge". Higher tiers climb toward the hardware maximum of 64.
        /// </summary>
        public static float MaxFactorForTier(AdaptiveQuality.QualityTier tier)
        {
            switch (tier)
            {
                case AdaptiveQuality.QualityTier.Low:    return 1f;   // no HW tess - G-24 fallback owns Low
                case AdaptiveQuality.QualityTier.Medium: return 16f;
                case AdaptiveQuality.QualityTier.High:   return 32f;
                default:                                 return 64f;  // Ultra - the hardware ceiling
            }
        }

        /// <summary>
        /// The canonical half-patch integer address of an edge midpoint. Coordinates are in units of
        /// (patchSize / 2): a cell centre sits on even indices, an edge midpoint on one odd index. The
        /// edge between cell k and k+1 resolves to the single index 2k+1 from EITHER side, which is the
        /// whole reason the two patches agree bit-for-bit on where their shared edge is.
        /// </summary>
        public static void EdgeMidpointHalfIndex(int cellX, int cellZ, PatchEdge edge, out int halfIx, out int halfIz)
        {
            halfIx = 2 * cellX;
            halfIz = 2 * cellZ;
            switch (edge)
            {
                case PatchEdge.East:  halfIx = 2 * cellX + 1; break;
                case PatchEdge.West:  halfIx = 2 * cellX - 1; break;
                case PatchEdge.North: halfIz = 2 * cellZ + 1; break;
                case PatchEdge.South: halfIz = 2 * cellZ - 1; break;
            }
        }

        /// <summary>World XZ of an edge midpoint: its shared half-index times half the patch size. The
        /// multiply-by-one-integer is what keeps both neighbours' results identical to the last bit.</summary>
        public static Vector2 EdgeMidpointXZ(int cellX, int cellZ, float patchSizeM, PatchEdge edge)
        {
            EdgeMidpointHalfIndex(cellX, cellZ, edge, out int halfIx, out int halfIz);
            float halfStep = patchSizeM * 0.5f;
            return new Vector2(halfIx * halfStep, halfIz * halfStep);
        }

        /// <summary>
        /// Turns the camera distance to an edge midpoint into a subdivision factor, from an angular-size
        /// model: an edge of length L at distance d subtends ~L/d radians, so holding each segment to
        /// <paramref name="targetSegmentAngleRad"/> asks for L/(d * angle) segments. Near edges saturate at
        /// the tier ceiling; far edges bottom out at 1 (no subdivision). Monotone decreasing in distance
        /// and continuous, so a patch that drifts outward only ever loses detail smoothly.
        /// </summary>
        public static float FactorFromDistance(float distanceM, float edgeLengthM, float targetSegmentAngleRad, float maxFactor)
        {
            float angle = Mathf.Max(1e-5f, targetSegmentAngleRad);
            float d = Mathf.Max(1e-3f, distanceM);
            float segments = edgeLengthM / (d * angle);
            return Mathf.Clamp(segments, 1f, Mathf.Max(1f, maxFactor));
        }

        /// <summary>
        /// The subdivision factor for one edge of the patch at integer cell (<paramref name="cellX"/>,
        /// <paramref name="cellZ"/>): address the shared midpoint, measure the camera's distance to it in
        /// 3D (the midpoint carries the shared edge's world height), and map that distance to a factor. Two
        /// patches sharing this edge pass the same cell-relative arguments for it and so receive one factor.
        /// </summary>
        public static float EdgeFactor(int cellX, int cellZ, float midpointY, float patchSizeM, PatchEdge edge,
                                       Vector3 cameraPos, float targetSegmentAngleRad, float maxFactor)
        {
            Vector2 mid = EdgeMidpointXZ(cellX, cellZ, patchSizeM, edge);
            float dx = mid.x - cameraPos.x;
            float dy = midpointY - cameraPos.y;
            float dz = mid.y - cameraPos.z;
            float distanceM = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            return FactorFromDistance(distanceM, patchSizeM, targetSegmentAngleRad, maxFactor);
        }
    }
}

