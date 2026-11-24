// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ConservingHeightBrush.cs
//  Purpose: Mass-conserving height painting (§ 9.8 G-51, L-32/L-35). A raise brush that
//           only ADDS material is a cheat and it reads like one - the ground inflates like a
//           balloon instead of being pushed. Real ground moves FROM somewhere TO somewhere:
//           material lifted inside the brush core is taken from an annulus just outside it,
//           so total volume is unchanged and the result reads as a push with a visible
//           source depression around the mound. This computes the per-sample height deltas
//           for that conserving mode; an unconserving mode (removal scale 0) is kept for
//           authored fantasy, and the task list labels it as such.
//
//  HOW THE BALANCE IS STRUCK
//  -------------------------
//  The core deposits a falloff profile; the annulus removes a unit ring profile. Summing
//  each over the grid gives an added volume and an annulus unit volume, and scaling the
//  removal by (added / annulusUnit) makes the two exactly equal - so the net volume is
//  zero to floating-point precision, not merely small. The core cells rise, the annulus
//  cells fall, and nothing outside the outer radius is touched.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Height deltas as functions of radial distance; no TerrainData, no heightmap. The
//  self-test assembles a grid from these primitives and pins conservation to well under
//  1 %, the source depression, and that the unconserving mode adds net volume. Writing the
//  deltas into the heightmap (and re-clamping to [0,1] normalised height) is the scene layer.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure mass-conserving height brush arithmetic: a core deposition profile, an annulus removal profile, and the
    /// scale that balances them so a raise stroke moves material rather than creating it (L-32/L-35). Device-free.
    /// </summary>
    public static class ConservingHeightBrush
    {
        /// <summary>The material deposited at a radial distance inside the brush core: strength·(1 − d/r)^exponent
        /// for d &lt; coreRadius, else 0. The mound that the stroke raises. Zero on a non-positive core radius.</summary>
        public static float CoreDeposit(float distanceM, float coreRadiusM, float strength, float exponent)
        {
            if (coreRadiusM <= 0f) return 0f;
            float t = distanceM / coreRadiusM;
            if (t >= 1f) return 0f;
            if (t < 0f) t = 0f;
            return strength * Mathf.Pow(1f - t, exponent);
        }

        /// <summary>The unit removal shape in the source annulus: 1 for coreRadius ≤ d &lt; outerRadius, else 0. The
        /// ring the lifted material is taken from. Zero unless the outer radius exceeds the core.</summary>
        public static float AnnulusUnit(float distanceM, float coreRadiusM, float outerRadiusM)
        {
            if (outerRadiusM <= coreRadiusM) return 0f;
            return (distanceM >= coreRadiusM && distanceM < outerRadiusM) ? 1f : 0f;
        }

        /// <summary>The factor to scale the annulus removal by so its volume equals the deposited volume:
        /// addedVolume / annulusUnitVolume. Zero (no removal) when the annulus has no area - degrading to the
        /// unconserving mode rather than dividing by zero.</summary>
        public static float RemovalScale(float addedVolume, float annulusUnitVolume)
        {
            if (annulusUnitVolume <= 0f) return 0f;
            return addedVolume / annulusUnitVolume;
        }

        /// <summary>
        /// The net height delta at a radial distance: the core deposit minus the scaled annulus removal. With the
        /// balanced <paramref name="removalScale"/> the grid sum is zero (conserving); with a removal scale of 0 it
        /// is the pure raise (the labelled unconserving fantasy mode).
        /// </summary>
        public static float NetDelta(float distanceM, float coreRadiusM, float outerRadiusM, float strength, float exponent, float removalScale)
        {
            return CoreDeposit(distanceM, coreRadiusM, strength, exponent)
                 - removalScale * AnnulusUnit(distanceM, coreRadiusM, outerRadiusM);
        }
    }
}
