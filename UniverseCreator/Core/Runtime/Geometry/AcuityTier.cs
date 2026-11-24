// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    AcuityTier.cs
//  Purpose: The ONE place the product's quality tier turns into a visual-acuity
//           budget (L-49). Every geometry cost the tier moves - terrain patch LOD,
//           and through the shared model the basemap/detail/tree/billboard draw
//           distances - rides on this single per-tier pixel budget, so the tiers
//           differ in exactly one authored number plus the per-class budget table.
//
//  NOT A SECOND ENUM
//  -----------------
//  The tier ladder is AdaptiveQuality.QualityTier (Ultra/High/Medium/Low), shared
//  with lighting, vegetation and effects. This file keys off THAT enum and returns
//  one float. The design's device columns map by name: Low = Mobile Low, Medium =
//  Mobile High, High = Desktop, Ultra = the flagship.
//
//  WHY DESKTOP AND FLAGSHIP SHARE acuityPixels = 1
//  -----------------------------------------------
//  Stated as acuity, not as raw pixel error, so the budget means the same thing to
//  the eye across screens of different height (that is the whole point of the
//  angular-error law). One pixel of tolerated error is already the acuity limit; a
//  finer budget buys geometry no display can resolve. The flagship's extra fidelity
//  is therefore NOT a finer acuity - the angular-error core replaced the old authored
//  falloff curve, so there is no falloff left to double - it is the larger triangle,
//  draw-call, overlay-ring and CPU budgets the device class carries on its own.
// -----------------------------------------------------------------------------

using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Resolves <see cref="AdaptiveQuality.QualityTier"/> to the acuity budget in
    /// pixels. Static and pure: the same tier always yields the same budget, so the
    /// density core and any tier driver never disagree about what a class allows.
    /// </summary>
    public static class AcuityTier
    {
        /// <summary>The per-tier table. Values are the device-class budget table.</summary>
        public static float Resolve(AdaptiveQuality.QualityTier tier)
        {
            switch (tier)
            {
                case AdaptiveQuality.QualityTier.Low:    return 4f; // Mobile Low
                case AdaptiveQuality.QualityTier.Medium: return 2f; // Mobile High
                case AdaptiveQuality.QualityTier.High:   return 1f; // Desktop
                default:                                 return 1f; // Ultra / flagship
            }
        }

        /// <summary>
        /// Convenience for a driver: resolve straight from a live AdaptiveQuality, or
        /// the Desktop budget when none is present, so geometry never depends on the
        /// tier sampler existing in the scene.
        /// </summary>
        public static float Resolve(AdaptiveQuality source)
        {
            return Resolve(source != null ? source.CurrentTier : AdaptiveQuality.QualityTier.High);
        }
    }
}
