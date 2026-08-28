// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SurficialCoverResolver.cs
//  Purpose: The § 9.4 answer to WHAT SITS ON TOP OF THE BEDROCK. Most of the real
//           land surface is not bare rock: it is a thin, transported blanket -
//           talus below a cliff, alluvium on a valley floor, wind sand in a dune
//           field, residual soil on a stable upland. This reads the exposed bedrock
//           (OutcropResolver) together with the LOCAL SLOPE and a valley-floor
//           weight, and returns which cover material lies there and how deep it is.
//
//  WHY SLOPE DECIDES IT, NOT NOISE (L-34)
//  --------------------------------------
//  Loose material cannot rest on a slope steeper than its angle of repose. So the
//  single most honest control on cover is the local gradient measured against the
//  source rock's repose angle (L-34): above it the slope sheds to bare rock, below
//  it a talus wedge thickens as the ground flattens, and on the flats the cover
//  becomes valley alluvium or a residual soil instead. Cover IDENTITY is transported
//  - it comes from where the material came from, not the bed directly beneath - so a
//  footslope shows the CLIFF'S rock as clasts, and a valley floor shows allochthonous
//  sediment (source unit -1) that § 7's discharge will grade by Hjulström (L-32) at
//  G-45/G-51. Like the outcrop, this is a pure function of its inputs, so two cells
//  meeting at an edge that see the same slope and bedrock resolve the same cover.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Geology
{
    /// <summary>The family of surficial material lying on the bedrock at a point.</summary>
    public enum CoverType
    {
        /// <summary>No blanket: the slope is too steep to hold loose material, so rock shows.</summary>
        BedrockExposed = 0,
        /// <summary>Talus/colluvium shed from the source rock above, resting at its repose angle (L-34).</summary>
        Colluvium = 1,
        /// <summary>Allochthonous valley-floor sediment, sorted by § 7 discharge (L-32) downstream.</summary>
        Alluvium = 2,
        /// <summary>Wind-laid sand where exposure is high and a source lies upwind.</summary>
        Aeolian = 3,
        /// <summary>In-place weathered soil on a stable, gentle surface, from soilFormationRate.</summary>
        ResidualSoil = 4
    }

    /// <summary>
    /// The cover answer at one point: the material family, how deep it is in metres, the grain size it
    /// presents, and the index of the bedrock unit it was sourced from (-1 for allochthonous cover that
    /// did not come from the local column). A value type - produced per sample, never stored.
    /// </summary>
    public struct CoverSample
    {
        public CoverType type;
        public float depthM;
        public float grainSizePhi;
        public int sourceUnitIndex;

        /// <summary>True where the blanket is thin enough that bedrock relief and § 9.5 processes still read.</summary>
        public bool BedrockShowsThrough { get { return depthM < 0.05f; } }

        public static CoverSample Bedrock(int unitIndex, float grainPhi)
        {
            return new CoverSample
            {
                type = CoverType.BedrockExposed, depthM = 0f,
                grainSizePhi = grainPhi, sourceUnitIndex = unitIndex
            };
        }
    }

    /// <summary>
    /// Resolves the surficial cover at a point from the exposed bedrock, the local slope, and a
    /// valley-floor weight. Pure and static: given the same inputs it always returns the same cover,
    /// so cover stays coherent across a shared cell edge exactly as the outcrop does.
    /// </summary>
    public static class SurficialCoverResolver
    {
        // Slope below which a flat-floor position reads as a valley bottom rather than a footslope.
        private const float ValleyMaxSlopeDeg = 6f;
        // Valley-floor weight (0 ridge .. 1 thalweg) above which alluvium wins over colluvium/soil.
        private const float ValleyFloorThreshold = 0.6f;
        // Thickest talus wedge a footslope carries, in metres, at the base of a repose slope.
        private const float MaxColluviumM = 8f;
        // Grain size of valley sand before § 7 discharge grades it (Udden-Wentworth φ; ~medium sand).
        private const float DefaultAlluvialPhi = 2f;
        // Reference exposure that turns a mm/ka production rate into an equilibrium soil depth in metres.
        private const float SoilEquilibriumKa = 25f;

        /// <summary>
        /// The cover at (implicitly) a point whose exposed bedrock is <paramref name="bedrock"/>, whose
        /// local surface gradient is <paramref name="slopeDeg"/>, and whose valley-floor weight is
        /// <paramref name="valleyFactor01"/> (0 on a ridge, 1 on a thalweg). <paramref name="windExposure01"/>
        /// biases a flat, dry, non-valley surface toward wind sand; pass 0 to disable aeolian cover.
        /// </summary>
        public static CoverSample Resolve(LithologyProfile profile, LithoSample bedrock,
                                          float slopeDeg, float valleyFactor01, float windExposure01)
        {
            if (profile == null || bedrock.unit == null)
                return CoverSample.Bedrock(bedrock.unitIndex, DefaultAlluvialPhi);

            float repose = Mathf.Clamp(bedrock.unit.angleOfReposeDeg, 1f, 60f);
            float slope = Mathf.Max(0f, slopeDeg);
            float valley = Mathf.Clamp01(valleyFactor01);
            int srcIndex = bedrock.unitIndex;
            float srcPhi = bedrock.unit.grainSizePhi;

            // 1) Too steep to hold loose material - the slope sheds to bare rock (ridge crests, cliffs).
            if (slope >= repose)
                return CoverSample.Bedrock(srcIndex, srcPhi);

            // 2) A flat valley floor collects allochthonous sediment, sorted downstream by discharge.
            if (valley >= ValleyFloorThreshold && slope <= ValleyMaxSlopeDeg)
            {
                float depth = Mathf.Lerp(0.5f, 6f, valley);   // thicker toward the thalweg
                return new CoverSample
                {
                    type = CoverType.Alluvium, depthM = depth,
                    grainSizePhi = DefaultAlluvialPhi, sourceUnitIndex = -1
                };
            }

            // 3) Wind sand on an exposed, gentle, non-valley surface (source assumed upwind).
            if (windExposure01 > 0.5f && slope <= ValleyMaxSlopeDeg)
            {
                float depth = Mathf.Lerp(0.2f, 3f, Mathf.Clamp01(windExposure01));
                return new CoverSample
                {
                    type = CoverType.Aeolian, depthM = depth,
                    grainSizePhi = DefaultAlluvialPhi, sourceUnitIndex = -1
                };
            }

            // 4) A footslope below the repose angle carries the SOURCE rock's talus, thickening as the
            //    slope flattens toward the base (zero right at the repose angle, most on the gentlest slope).
            if (slope > ValleyMaxSlopeDeg)
            {
                float wedge = Mathf.Clamp01((repose - slope) / repose);
                return new CoverSample
                {
                    type = CoverType.Colluvium, depthM = MaxColluviumM * wedge,
                    grainSizePhi = srcPhi, sourceUnitIndex = srcIndex
                };
            }

            // 5) A stable, gentle upland weathers in place. Equilibrium soil depth from the source's
            //    production rate (mm/ka -> m), thinned by slope wash as the ground steepens.
            float soilM = (bedrock.unit.soilFormationRate * SoilEquilibriumKa) / 1000f;
            soilM *= Mathf.Clamp01(1f - slope / repose);
            return new CoverSample
            {
                type = CoverType.ResidualSoil, depthM = Mathf.Max(0f, soilM),
                grainSizePhi = srcPhi + 2f,           // weathers finer than the parent rock
                sourceUnitIndex = srcIndex
            };
        }
    }
}

