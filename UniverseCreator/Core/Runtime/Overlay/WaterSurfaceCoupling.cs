// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    WaterSurfaceCoupling.cs
//  Purpose: Where the micro-relief overlay meets a water body from section 7, the two must
//           agree, and the agreement is one-way: the overlay reads the existing still-water
//           level and bends to it - it never authors a second water surface. This is the
//           clamp that enforces the one invariant a player notices instantly: displaced
//           ground must not poke up through a lake or sea surface. Given the point's base
//           terrain height, the proposed micro-relief displacement, and the section-7
//           still-water datum, it returns the displacement clamped so the displaced surface
//           stays a hair below the water plane wherever the ground is submerged, while
//           leaving above-water berms and below-water notches untouched.
//
//  WHY A CLAMP AND NOT A SHAPE (L-48)
//  ----------------------------------
//  The shore SHAPE - the wave-cut notch, the planed platform, the run-up berm - is built by
//  the beach process in the field, from the same still-water datum. This file does not
//  restate that shape; it guarantees the boundary condition the shape must respect, so the
//  waterline traces the notch and nothing breaches the surface. Keeping the datum single
//  (read, never authored) is what stops the overlay and section 7 from disagreeing.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// The section-7 water datum at a point, plus the terrain height the overlay is displacing. All heights
    /// are world Y in metres. <see cref="stillWaterLevelM"/> is READ from section 7 - never set here.
    /// </summary>
    public struct WaterCoupling
    {
        /// <summary>True where a section-7 water body is in range of this point; false leaves displacement untouched.</summary>
        public bool hasWater;
        /// <summary>World Y of the still-water surface, from the section-7 water model.</summary>
        public float stillWaterLevelM;
        /// <summary>World Y of the terrain surface here, before micro-relief displacement.</summary>
        public float baseHeightM;
    }

    /// <summary>Clamps overlay displacement against the still-water plane so nothing breaches it. Pure, allocation-free.</summary>
    public static class WaterSurfaceCoupling
    {
        /// <summary>Displaced submerged ground is held this far BELOW the surface, so it never coincides with the plane.</summary>
        public const float SurfaceClearanceM = 0.001f;

        /// <summary>True where the base terrain sits below the still-water surface - i.e. this point is under water.</summary>
        public static bool IsSubmerged(in WaterCoupling w)
        {
            return w.hasWater && w.baseHeightM < w.stillWaterLevelM;
        }

        /// <summary>
        /// Returns the displacement (mm) clamped so a submerged vertex cannot rise to or through the water
        /// plane. Deepening (negative) is always allowed; above-water points (berms) pass through unchanged.
        /// </summary>
        public static float ClampDisplacementMm(float displacementMm, in WaterCoupling w)
        {
            if (!w.hasWater) return displacementMm;

            float displacedM = w.baseHeightM + displacementMm * 0.001f;
            float ceilingM = w.stillWaterLevelM - SurfaceClearanceM;

            // Only the submerged column (up to and including the waterline) has a ceiling; a berm standing
            // above still water is left alone, so the waterline itself meets the plane and traces the notch.
            if (w.baseHeightM <= w.stillWaterLevelM && displacedM > ceilingM)
                return (ceilingM - w.baseHeightM) * 1000f;

            return displacementMm;
        }

        /// <summary>The resulting world-Y surface height after the clamp - what a consumer places geometry at.</summary>
        public static float SurfaceHeightM(float displacementMm, in WaterCoupling w)
        {
            return w.baseHeightM + ClampDisplacementMm(displacementMm, w) * 0.001f;
        }
    }
}
