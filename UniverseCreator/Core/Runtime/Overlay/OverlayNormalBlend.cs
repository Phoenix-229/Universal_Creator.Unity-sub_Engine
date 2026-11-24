// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    OverlayNormalBlend.cs
//  Purpose: The cross-fade that hides the ring's outer edge. Inside the micro-relief
//           ring the surface normal is the overlay's own analytic normal (recomputed
//           from the height field's gradient); past the ring there is no overlay
//           geometry at all, only the native terrain wearing a baked distant normal
//           map of the SAME field's gradient. If the switch between them were a line,
//           a player walking outward would see the surface character change along a
//           circle that moves with the camera - which reads as a material seam, worse
//           than a geometry seam because it slides. This class is the pure weight that
//           turns that line into a band.
//
//  WHY A BAND WITH ZERO SLOPE AT BOTH ENDS  (L-49)
//  -----------------------------------------------
//  A linear fade removes the value jump but leaves a slope jump where the band meets
//  the fully-analytic interior and the fully-baked exterior, and the eye catches a
//  slope discontinuity as a crease just as readily as a value one. The weight is
//  therefore a smoothstep across the band, so it arrives at 1 (interior) and 0
//  (exterior) with zero rate of change - no crease at either shoulder.
//
//  WHY THE TWO NORMALS AGREE AT THE BOUNDARY BY CONSTRUCTION
//  --------------------------------------------------------
//  The blend only has to hide anything if the two sides differ. They do not: the near
//  analytic normal and the far baked normal are both the gradient of the one relief
//  field, so at the boundary they are the same vector and the fade is invisible even
//  before the smoothstep does its work. This class owns only the WEIGHT; the agreement
//  of the two inputs is a property of sampling one field, the same discipline the edge
//  factor and the height query already rest on.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// Pure static cross-fade weight between the overlay's analytic normal (near) and the terrain's baked
    /// distant normal map (far), as a function of distance from the ring centre. Testable without a device;
    /// the overlay/terrain shaders mirror this exact curve so the seen fade matches the pinned one.
    /// </summary>
    public static class OverlayNormalBlend
    {
        /// <summary>Fraction of the ring's outer radius over which the fade runs. A band, not a line.</summary>
        public const float DefaultBlendBandFraction = 0.25f;

        /// <summary>
        /// Weight on the overlay's ANALYTIC normal at a point this far from the ring centre: 1 well inside
        /// the ring (use the analytic normal outright), 0 at and beyond the outer radius (use the baked
        /// distant normal), smoothstepped across the band between so both shoulders are crease-free. The
        /// complement, 1 - this, is the weight on the baked normal.
        /// </summary>
        public static float AnalyticWeight(float distanceM, float ringOuterRadiusM, float blendBandFraction)
        {
            float outer = Mathf.Max(0f, ringOuterRadiusM);
            float band = Mathf.Clamp01(blendBandFraction) * outer;

            // Degenerate band: no room to fade, so it is a hard edge - inside is analytic, outside is baked.
            if (band <= 1e-4f) return distanceM < outer ? 1f : 0f;

            float inner = outer - band;
            float t = Mathf.Clamp01(Mathf.InverseLerp(inner, outer, distanceM)); // 0 at inner edge, 1 at outer
            return 1f - Smoothstep01(t);                                          // 1 inside -> 0 outside
        }

        /// <summary>Hermite smoothstep on [0,1] with zero slope at both ends - the crease-free interpolant.</summary>
        private static float Smoothstep01(float t)
        {
            return t * t * (3f - 2f * t);
        }
    }
}
