// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SelfThinningDensity.cs
//  Purpose: The count of props to draw is ECOLOGY, not a render setting. § 5's
//           self-thinning - Reineke's stand-density rule (L-26), N_max ∝ D^-1.605 -
//           already decides how many stems of a given size a hectare can hold. LOD
//           selection lowers the COST PER STEM; it must never lower the NUMBER of
//           stems, because that silently changes the forest the player is looking at.
//           This class is the stem-count law, and it deliberately takes NO tier, no
//           bias and no camera - so nothing in the render path can reach in and delete
//           a tree. The only thing that may change the count is § 9.10's explicit,
//           stated degradation order, expressed here as a single separate function.
//
//  WHY THE SEPARATION IS THE POINT
//  -------------------------------
//  Reineke: on the self-thinning line, log N = log k − 1.605 · log D. Bigger trees,
//  fewer per hectare, on a slope of −1.605 that is stable across species. The stem
//  count for a stand is that density times its area - a function of DIAMETER and AREA
//  ONLY. The LOD/impostor distances are a function of tier, bias and screen. The two
//  share no argument, so lowering the tier moves the switch distances and leaves the
//  count bit-identical; that shared-nothing structure IS the done-when, and the
//  self-test pins it by varying every render knob and watching the count not move.
//
//  DEGRADATION IS THE ONE DOOR
//  ---------------------------
//  When the frame still cannot be met after LOD has done its work, § 9.10 decides what
//  gives - visibly and in a stated sequence. That is the ONLY path that may reduce the
//  count, so it is a distinct function with an explicit keep-fraction; at keep = 1 it is
//  the identity, i.e. no silent deletion ever happens by default.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure stem-count law from § 5's self-thinning (Reineke, L-26). Takes stand size and area only - NO tier,
    /// bias or camera - so the render path cannot change how many stems exist. The one function that may lower a
    /// count is the explicit § 9.10 degradation, kept separate. Device-free arithmetic.
    /// </summary>
    public static class SelfThinningDensity
    {
        /// <summary>Reineke's self-thinning slope: the stable exponent of the stand-density line, N ∝ D^(this).</summary>
        public const float ReinekeExponent = -1.605f;

        /// <summary>
        /// Maximum stems per hectare on the self-thinning line for a stand whose quadratic mean diameter is
        /// <paramref name="quadraticMeanDiameterCm"/>: <paramref name="selfThinningK"/> · D^(−1.605). Larger trees
        /// self-thin to fewer stems. A function of diameter and § 5's k ONLY - no tier enters. Fails closed to 0.
        /// </summary>
        public static float MaxStemsPerHectare(float quadraticMeanDiameterCm, float selfThinningK)
        {
            if (quadraticMeanDiameterCm <= 0f || selfThinningK <= 0f) return 0f;
            return selfThinningK * Mathf.Pow(quadraticMeanDiameterCm, ReinekeExponent);
        }

        /// <summary>
        /// The stem count for a stand: max stems per hectare times its area in hectares, floored to a whole count.
        /// A function of diameter, k and area ONLY - the render tier is not an argument, so LOD selection cannot
        /// reduce it. Fails closed to 0.
        /// </summary>
        public static int StemCountForStand(float areaHectares, float quadraticMeanDiameterCm, float selfThinningK)
        {
            if (areaHectares <= 0f) return 0;
            float perHa = MaxStemsPerHectare(quadraticMeanDiameterCm, selfThinningK);
            if (perHa <= 0f) return 0;
            return Mathf.FloorToInt(perHa * areaHectares);
        }

        /// <summary>
        /// The ONLY path that may reduce a stem count: § 9.10's explicit degradation, applying a keep-fraction in
        /// [0,1] to the ecological count. At keepFraction = 1 this is the identity - no stem is ever removed by
        /// default, only by a stated degradation step. Clamped, and floored to a whole count.
        /// </summary>
        public static int DegradedStemCount(int ecologicalStemCount, float keepFraction)
        {
            if (ecologicalStemCount <= 0) return 0;
            float f = Mathf.Clamp01(keepFraction);
            return Mathf.FloorToInt(ecologicalStemCount * f);
        }
    }
}
