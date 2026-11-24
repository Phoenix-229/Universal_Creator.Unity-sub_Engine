// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    BiomeClassifierNode.cs
//  Purpose: Classifies each pixel into a biome from temperature and moisture,
//           following the Whittaker biome diagram — the standard ecological model
//           that places biomes on a two-axis climate plot.
//
//  MODULE 1 SCOPE: this node produces a biome *index field* and nothing more.
//  What each biome means in terms of vegetation, textures, and props is Module 5
//  (Biomes). The classification itself is real, not a stub, because downstream
//  masks need something to select against.
//
//  The output field encodes the biome index normalised to 0-1 so it travels
//  through the same Matrix pipeline as every other product. BiomeMaskNode decodes
//  it back to an index.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    /// <summary>
    /// Whittaker biome classes. The order is load-bearing: BiomeMaskNode and the
    /// encoded field both index into it, so appending is safe but reordering is not.
    /// </summary>
    public enum BiomeType
    {
        Tundra = 0,
        BorealForest = 1,       // Taiga
        TemperateGrassland = 2,
        TemperateForest = 3,
        TemperateRainforest = 4,
        Desert = 5,
        Savanna = 6,
        TropicalSeasonalForest = 7,
        TropicalRainforest = 8,
        Ice = 9
    }

    [System.Serializable]
    public class BiomeClassifierNode : Generator, IPreparedGraphInputs, IGenerationSnapshotInputs
    {
        public override string NodeName { get { return "Biome Classifier"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Classify pixels into Whittaker biomes from temperature and moisture."; } }

        /// <summary>Total number of biome classes. Used to encode and decode the field.</summary>
        public const int BiomeCount = Ecology.WhittakerBiome.Count;

        // Cached: GetInputs is called every editor repaint, so it must not allocate.
        private static readonly Port[] ClimateInputs =
        {
            new Port("Temperature", PortType.Matrix, PortDirection.Input),
            new Port("Moisture", PortType.Matrix, PortDirection.Input),
            // BIO-2: an OPTIONAL edaphic/drainage axis. Unwired = climate-only (today). Wired, a
            // high wetness lifts the effective moisture, so a saturated valley in a dry climate
            // reclassifies toward the wet class (riparian/wetland) instead of reading as desert.
            new Port("Wetness", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] BiomeOutput =
        {
            new Port("Biome", PortType.Matrix, PortDirection.Output, true),
            // BIO-1: an optional graded transition field, 0 inside a biome rising toward 1 at a
            // class boundary. Additive - a graph that wires only "Biome" is byte-unchanged; a graph
            // that wires "Blend" gets the real ecotone gradient to soft-blend splats/scatter with.
            new Port("Blend", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return ClimateInputs; }
        public override Port[] GetOutputs() { return BiomeOutput; }

        /// <summary>Below this temperature everything reads as permanent ice.</summary>
        public float iceThreshold = 0.05f;

        /// <summary>
        /// BIO-4/6/7: author-placed biome overrides, pushed from the scene's UCBiomeRegion
        /// components on the main thread (like water-body footprints). Where a region covers a
        /// pixel its biome wins over climate - highest-precedence (lowest priority) region first.
        /// Null = pure procedural classification (today).
        /// </summary>
        [System.NonSerialized] public List<BiomeRegionFootprint> regionOverrides;

        // UC-O01 / UC-A03: the graph asset stays scene-independent; only the bake hashes this data.
        [System.Serializable]
        private sealed class PreparedBiomeRegions
        {
            public List<BiomeRegionFootprint> regions;
        }

        [System.NonSerialized] private PreparedBiomeRegions preparedRegionState;

        /// <summary>Assigns prepared scene-region inputs on the main thread before hashing a bake.</summary>
        /// <param name="regions">Ordered world-metre footprints; null clears overrides.</param>
        /// <remarks>Keep the list stable until its consumers finish; this does not isolate shared graph runs.</remarks>
        public void SetRegionOverrides(List<BiomeRegionFootprint> regions) { regionOverrides = regions; }

        /// <summary>UC-T01-06: deep-copies pushed region geometry and precedence into a detached node.</summary>
        /// <param name="snapshot">Same-type generation node; another type is rejected. Main thread only.</param>
        public void CopyInputsTo(Generator snapshot)
        {
            BiomeClassifierNode target = snapshot as BiomeClassifierNode;
            if (target == null) throw new System.ArgumentException("Expected a Biome Classifier snapshot.", "snapshot");
            PreparedBiomeRegions data = new PreparedBiomeRegions { regions = regionOverrides };
            target.SetRegionOverrides(JsonUtility.FromJson<PreparedBiomeRegions>(JsonUtility.ToJson(data)).regions);
        }

        /// <summary>UC-O01 / UC-A03: exposes all current footprint fields to dependency hashing.</summary>
        /// <returns>A reusable serializable region wrapper, or null for no effective overrides.</returns>
        /// <remarks>Main-thread only, before worker dispatch. Region order preserves equal-priority precedence.</remarks>
        public object GetPreparedInputsForHash()
        {
            if (regionOverrides == null || regionOverrides.Count == 0)
            {
                if (preparedRegionState != null) preparedRegionState.regions = null;
                return null;
            }
            if (preparedRegionState == null) preparedRegionState = new PreparedBiomeRegions();
            preparedRegionState.regions = regionOverrides;
            return preparedRegionState;
        }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix temperature = GetInputMatrix(tile, 0, stop);
            Matrix moisture = GetInputMatrix(tile, 1, stop);
            Matrix wetness = GetInputMatrix(tile, 2, stop);   // BIO-2 optional drainage axis

            if (stop != null && stop.StopRequested) return null;

            Matrix result = tile.CreateMatrix();
            bool wantBlend = outputIndex == 1;   // BIO-1 graded transition field
            float band = Ecology.EcoConstants.EcotoneBiomeClimateBand;

            // BIO-4/6/7: author-placed region overrides. Resolved per pixel in world XZ, highest
            // precedence (lowest priority) first; a covered pixel is that biome, no climate blend.
            bool hasRegions = regionOverrides != null && regionOverrides.Count > 0;
            Vector2 ps = result.PixelSize;
            float ox = result.worldPos.x, oz = result.worldPos.z;

            // Missing climate inputs default to the middle of the diagram rather
            // than to zero, which would classify the whole world as ice.
            for (int z = 0; z < result.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * result.rect.size.x;

                for (int x = 0; x < result.rect.size.x; x++)
                {
                    int i = rowStart + x;

                    if (hasRegions)
                    {
                        int ov = BiomeRegionField.ResolveBiome(
                            regionOverrides, ox + x * ps.x, oz + z * ps.y);
                        if (ov >= 0)
                        {
                            // Authored biome wins over climate; blend 0 = a pure (non-transition) patch.
                            result.arr[i] = wantBlend ? 0f : Ecology.WhittakerBiome.Encode(ov);
                            continue;
                        }
                    }

                    float t = temperature != null ? InterpolationMath.Clamp01(temperature.arr[i]) : 0.5f;
                    float mo = moisture != null ? InterpolationMath.Clamp01(moisture.arr[i]) : 0.5f;

                    // BIO-2: a wired wetness axis lifts the effective moisture, so standing water /
                    // a high water table reclassifies toward the wet class. Unwired leaves mo as-is.
                    if (wetness != null)
                        mo = Mathf.Max(mo, InterpolationMath.Clamp01(wetness.arr[i]));

                    if (wantBlend)
                    {
                        // BIO-1 / BIO-8: the classified ecotone gradient, not a splat-time smear.
                        result.arr[i] = Ecology.WhittakerBiome.BoundaryBlend(t, mo, iceThreshold, band);
                    }
                    else
                    {
                        BiomeType biome = Classify(t, mo);

                        // Encoded through the same helper species selection decodes with,
                        // so the two cannot disagree about what a field value means.
                        result.arr[i] = Ecology.WhittakerBiome.Encode((int)biome);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Whittaker lookup. Temperature is the primary axis because it gates which
        /// biomes are possible at all; moisture then picks among them.
        /// </summary>
        /// <remarks>
        /// The thresholds themselves live in Ecology.WhittakerBiome, because species
        /// selection has to reach the same verdict this node bakes into the splat
        /// map. A second copy here would let the ground say tundra while the trees
        /// growing on it think boreal, with nothing to report the disagreement.
        /// </remarks>
        public BiomeType Classify(float temperature, float moisture)
        {
            return (BiomeType)Ecology.WhittakerBiome.Classify(temperature, moisture, iceThreshold);
        }
    }
}
