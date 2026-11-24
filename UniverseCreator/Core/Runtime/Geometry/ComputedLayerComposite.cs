// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ComputedLayerComposite.cs
//  Purpose: The composited ground layer weights are COMPUTED from § 9.4's physics, not
//           authored by a painted mask (§ 9.7 G-45, the "nothing is random" case). An
//           unpainted world is not an empty one: exposed bedrock shows where cover is
//           thin, colluvium at and below the angle of repose, alluvium in valley bottoms,
//           aeolian sand downwind of a source, § 6's litter under canopy, § 7's snow above
//           the snowline, § 9.5's clast lag where the surface is armoured. § 9.8's paint
//           composites OVER this result, so an author corrects a computed world instead of
//           painting an empty one - which is exactly why this file takes only physical
//           inputs and no paint argument at all.
//
//  A TOP-DOWN OCCLUSION ORDER, NOT AN ARGMAX
//  -----------------------------------------
//  Real ground layers by process, from the top down: seasonal snow covers everything below
//  it; organic litter sits on the mineral soil under a canopy; and the mineral surface
//  itself is whichever transport process won - bedrock exposure, colluvium, alluvium,
//  aeolian sand, or a clast armour lag. So the visible weight is not the largest single
//  suitability but a partition of unity built top-down: snow takes its fraction, litter
//  takes its fraction of what snow leaves, and the mineral substrate partitions the rest
//  by its normalised suitabilities. The result always sums to one and is always
//  attributable - the debug view names a pixel's dominant layer by the rule that produced
//  it, because each weight IS a rule's output.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Numbers in, a weight vector out; no Terrain, no texture, no paint. The self-test pins
//  the partition of unity, the snow-over-litter-over-mineral precedence, each site's
//  dominant layer, and the never-empty fallback to bedrock. Rasterising the weights into
//  the splat cache is the scene layer above.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>The ground layer sources § 9.4 computes, ordered base-upward: the mineral substrate
    /// (bedrock, colluvium, alluvium, aeolian sand, clast armour), then litter, then snow on top.</summary>
    public enum GroundLayerSource
    {
        ExposedBedrock = 0,
        Colluvium = 1,
        Alluvium = 2,
        AeolianSand = 3,
        ClastArmour = 4,
        Litter = 5,
        Snow = 6,
    }

    /// <summary>
    /// Pure computed layer-weight compositor: turns § 9.4's physical suitabilities into a partition-of-unity
    /// weight vector by real top-down cover order, and names the dominant source for the debug attribution view.
    /// Takes no paint input by construction - the author corrects this result, never authors it. Device-free.
    /// </summary>
    public static class ComputedLayerComposite
    {
        /// <summary>The number of ground layer sources (the length <paramref name="weightsInto"/> must have).</summary>
        public const int SourceCount = 7;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float NonNeg(float v) => v < 0f ? 0f : v;

        /// <summary>
        /// Fills <paramref name="weightsInto"/> (length ≥ 7, indexed by <see cref="GroundLayerSource"/>) with the
        /// computed partition of unity for one site. Snow covers its fraction; litter covers its fraction of what
        /// remains; the mineral substrate partitions the rest by normalised suitability, falling back to bedrock
        /// when no mineral process is active - so the ground is never empty. Snow/litter are clamped to [0,1];
        /// mineral suitabilities are clamped non-negative. No-ops on a too-small array.
        /// </summary>
        public static void Composite(float snow01, float litter01,
            float bedrock, float colluvium, float alluvium, float aeolian, float clast,
            float[] weightsInto)
        {
            if (weightsInto == null || weightsInto.Length < SourceCount) return;
            for (int i = 0; i < SourceCount; i++) weightsInto[i] = 0f;

            float snow = Clamp01(snow01);
            float litter = Clamp01(litter01);

            float b = NonNeg(bedrock), c = NonNeg(colluvium), a = NonNeg(alluvium), e = NonNeg(aeolian), k = NonNeg(clast);
            float mineralSum = b + c + a + e + k;
            if (mineralSum <= 0f) { b = 1f; mineralSum = 1f; }   // bare rock underlies everything: never empty

            float snowW = snow;
            float litterW = (1f - snow) * litter;
            float mineralShare = (1f - snow) * (1f - litter);

            weightsInto[(int)GroundLayerSource.Snow] = snowW;
            weightsInto[(int)GroundLayerSource.Litter] = litterW;
            weightsInto[(int)GroundLayerSource.ExposedBedrock] = mineralShare * (b / mineralSum);
            weightsInto[(int)GroundLayerSource.Colluvium] = mineralShare * (c / mineralSum);
            weightsInto[(int)GroundLayerSource.Alluvium] = mineralShare * (a / mineralSum);
            weightsInto[(int)GroundLayerSource.AeolianSand] = mineralShare * (e / mineralSum);
            weightsInto[(int)GroundLayerSource.ClastArmour] = mineralShare * (k / mineralSum);
        }

        /// <summary>The dominant (largest-weight) source, for the debug view that attributes a pixel to its rule.
        /// Ties resolve to the lower enum value. Returns <see cref="GroundLayerSource.ExposedBedrock"/> on bad input.</summary>
        public static GroundLayerSource DominantSource(float[] weights)
        {
            if (weights == null || weights.Length < SourceCount) return GroundLayerSource.ExposedBedrock;
            int best = 0;
            for (int i = 1; i < SourceCount; i++)
                if (weights[i] > weights[best]) best = i;
            return (GroundLayerSource)best;
        }
    }
}
