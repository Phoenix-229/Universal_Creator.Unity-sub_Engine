// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World - Refinement #7 (W3)
//
//  File:    HydroExclusion.cs
//  Purpose: The hydrological exclusion kernel: the pure arithmetic that decides,
//           at one site, whether the ground carries a LAND plant, an AQUATIC one,
//           or nothing at all - deep flat calm bed -> marine soft-sediment slots,
//           dry ground -> terrestrial slots, steep face or a current-scoured bed
//           -> nothing a rooted plant can hold. It is a FILTER over the scatter
//           system that already exists, not a second placement system.
//
//  WHY THIS IS A FILTER, NOT A NEW GATE
//  ------------------------------------
//  Everything this routing needs is ALREADY enforced by the ecology stack:
//    - land vs water           : SpeciesSuitability.SubmergenceAdmits +
//                                PlacementClearance.WaterAdmits (submergedDepthRangeM)
//    - salt vs fresh           : saltFraction vs saltTolerance
//    - too steep reads as rock : PlacementClearance.TerrainAdmits (slope > maxSlope)
//    - rock carries no rooting : the SpeciesSuitability rockiness>rockinessMax reject
//  The ONE thing missing was the current a submerged bed feels: a flat seabed swept
//  by a channel is scoured to bare rock just as surely as a cliff is bare, but slope
//  alone cannot see that - the bed is flat. W1 derived the water facts and DEFERRED
//  the flow reading to here. So W3 closes exactly that gap: it turns the bed-flow
//  proxy (HydroField.BedFlow01) into a scoured-rock fraction and RAISES the existing
//  rockiness field. Nothing new rejects anything; the existing rockiness gate now
//  simply sees the scoured bed for what it is - soft-sediment rooters (seagrass,
//  reeds) are turned away and the hard bed is left for holdfast/encrusting species.
//
//  ONE DEFINITION OF THE BED, SHARED WITH THE TEXTURING
//  ----------------------------------------------------
//  UnderwaterSplatMutation.BedCovers (W2) paints a submerged cell as rock where
//  wet*energy is high. This kernel reads the SAME physical idea - energy sweeps
//  fines away to rock, and the effect fades in over the first half-metre of depth so
//  there is no hard rock line at the exact shoreline - so the terrain that textures
//  as rock and the ecology that reads rock can never drift apart. W2 works in
//  calibrated m/s because a texturing driver can supply a velocity; this works on the
//  drainage-derived 0..1 flow proxy because that is what the ecology sampler has.
//  Same law, the measurement each stage actually owns.
//
//  Pure functions of their arguments. No Unity object, no asset, no layer index, no
//  field on EnvironmentSample added - safe on a scatter worker and inside the
//  156-byte sample budget. See EcoEnvelope / SpeciesSuitability for the gates it
//  feeds, and HydroField for the flow reading it consumes.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// What a site is fit to carry once hydrology has spoken. Not a runtime switch -
    /// the runtime routes by raising rockiness into the existing gates - but the
    /// single readable statement of the routing those gates then produce, which the
    /// self-test asserts against so the intent cannot silently rot.
    /// </summary>
    public enum HydroRoute
    {
        /// <summary>Dry ground within the slope limit: land plants.</summary>
        Terrestrial = 0,
        /// <summary>Submerged, calm, soft bed: rooting aquatics (seagrass, reeds).</summary>
        MarineSediment = 1,
        /// <summary>Submerged, current-scoured or already-rocky bed: holdfast species only.</summary>
        MarineHardSubstrate = 2,
        /// <summary>Too steep for any rooted plant, wet or dry.</summary>
        Reject = 3,
    }

    /// <summary>
    /// The hydrological exclusion kernel. Every method is a pure function; see the file
    /// header for why it is a filter over the existing suitability stack and shares its
    /// bed law with the underwater texturing (W2).
    /// </summary>
    public static class HydroExclusion
    {
        /// <summary>
        /// Depth over which the submerged bed fades in from the waterline, metres. The
        /// same half-metre <see cref="UniverseCreator.TerrainSystem.UnderwaterBedProfile"/>
        /// fades its bed over, so scoured rock and scour-driven rockiness begin together
        /// and neither draws a hard line at the exact shore.
        /// </summary>
        public const float BedFadeMetres = 0.5f;

        /// <summary>
        /// Scoured-rock fraction above which the bed reads as hard substrate rather than
        /// soft sediment - half the bed swept clean. Below it a rooting aquatic can hold;
        /// at or above it only a holdfast species can. Physical, not an asset threshold.
        /// </summary>
        public const float HardSubstrateScour = 0.5f;

        /// <summary>
        /// The fraction of a submerged bed the current has scoured to hard substrate:
        /// how far under water the cell sits (faded in over <see cref="BedFadeMetres"/>)
        /// times how strong the flow across it is. Mirrors the rock term of the W2 bed
        /// blend (wet*energy) with the drainage 0..1 flow standing in for velocity. Zero
        /// at or above the waterline (depth &lt;= 0) - dry ground is never scoured here.
        /// </summary>
        /// <param name="submergedDepthM">Water surface above ground, metres. Zero or less on dry land.</param>
        /// <param name="flow01">Bed-flow energy 0..1 from <see cref="HydroField.BedFlow01"/>.</param>
        public static float BedScour01(float submergedDepthM, float flow01)
        {
            if (submergedDepthM <= 0f) return 0f;
            float wet = Smooth01(submergedDepthM, 0f, BedFadeMetres);
            return wet * Mathf.Clamp01(flow01);
        }

        /// <summary>
        /// Rockiness for a site once the bed's scour is folded in: unchanged on dry
        /// ground, and on a submerged bed the greater of the site's own rockiness and
        /// the scoured-rock fraction. MAX rather than add, because a bed that is already
        /// rocky (a steep submerged shelf) does not become more so by also being swept,
        /// and a flat bed the current scours becomes rock it was not before - either way
        /// the harder reading wins. The existing rockiness gate does the rest.
        /// </summary>
        public static float SubmergedRockiness(float baseRockiness, float submergedDepthM, float flow01)
        {
            if (submergedDepthM <= 0f) return baseRockiness;
            return Mathf.Max(baseRockiness, BedScour01(submergedDepthM, flow01));
        }

        /// <summary>
        /// The routing a site resolves to, as one readable truth table. The runtime does
        /// not call this - it raises rockiness (<see cref="SubmergedRockiness"/>) and lets
        /// the existing slope and rockiness gates route - but this states, and the
        /// self-test checks, the SAME outcome those gates produce, keyed off the SAME
        /// scour so the two cannot disagree: dry-and-standable is terrestrial, submerged
        /// splits soft from hard at <see cref="HardSubstrateScour"/>, and anything past
        /// the slope limit is rejected wet or dry (a submerged cliff carries no rooted
        /// plant either, exactly as PlacementClearance.TerrainAdmits already holds).
        /// </summary>
        public static HydroRoute Classify(float submergedDepthM, float slopeDeg,
                                           float flow01, float maxSlopeDeg)
        {
            bool tooSteep = maxSlopeDeg > 0f && slopeDeg > maxSlopeDeg;

            if (submergedDepthM <= 0f)
                return tooSteep ? HydroRoute.Reject : HydroRoute.Terrestrial;

            if (tooSteep) return HydroRoute.Reject;

            return BedScour01(submergedDepthM, flow01) >= HardSubstrateScour
                ? HydroRoute.MarineHardSubstrate
                : HydroRoute.MarineSediment;
        }

        // ---------------------------------------------------------------------

        /// <summary>SmoothStep of v across [a,b] to 0..1, hand-normalised first because
        /// Mathf.SmoothStep takes a 0..1 parameter and is NOT the HLSL smoothstep that
        /// remaps across [a,b]. Guards a degenerate a&gt;=b. Same shape the peer bed and
        /// splat mutations use, kept local to avoid coupling ecology to a texturing type.</summary>
        private static float Smooth01(float v, float a, float b)
        {
            if (b <= a) return v >= b ? 1f : 0f;
            float t = Mathf.Clamp01((v - a) / (b - a));
            return Mathf.SmoothStep(0f, 1f, t);
        }
    }
}
