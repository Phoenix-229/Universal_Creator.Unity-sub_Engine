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
    public class BiomeClassifierNode : Generator
    {
        public override string NodeName { get { return "Biome Classifier"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Classify pixels into Whittaker biomes from temperature and moisture."; } }

        /// <summary>Total number of biome classes. Used to encode and decode the field.</summary>
        public const int BiomeCount = 10;

        // Cached: GetInputs is called every editor repaint, so it must not allocate.
        private static readonly Port[] ClimateInputs =
        {
            new Port("Temperature", PortType.Matrix, PortDirection.Input),
            new Port("Moisture", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] BiomeOutput =
        {
            new Port("Biome", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return ClimateInputs; }
        public override Port[] GetOutputs() { return BiomeOutput; }

        /// <summary>Below this temperature everything reads as permanent ice.</summary>
        public float iceThreshold = 0.05f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix temperature = GetInputMatrix(tile, 0, stop);
            Matrix moisture = GetInputMatrix(tile, 1, stop);

            if (stop != null && stop.StopRequested) return null;

            Matrix result = tile.CreateMatrix();

            // Missing climate inputs default to the middle of the diagram rather
            // than to zero, which would classify the whole world as ice.
            for (int z = 0; z < result.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * result.rect.size.x;

                for (int x = 0; x < result.rect.size.x; x++)
                {
                    int i = rowStart + x;

                    float t = temperature != null ? InterpolationMath.Clamp01(temperature.arr[i]) : 0.5f;
                    float mo = moisture != null ? InterpolationMath.Clamp01(moisture.arr[i]) : 0.5f;

                    BiomeType biome = Classify(t, mo);

                    // Encode as index / (count - 1) so the field spans 0-1.
                    result.arr[i] = (float)(int)biome / (BiomeCount - 1);
                }
            }

            return result;
        }

        /// <summary>
        /// Whittaker lookup. Temperature is the primary axis because it gates which
        /// biomes are possible at all; moisture then picks among them.
        /// </summary>
        public BiomeType Classify(float temperature, float moisture)
        {
            if (temperature < iceThreshold) return BiomeType.Ice;

            // Cold: below roughly 0.25 normalised temperature.
            if (temperature < 0.25f)
            {
                return moisture < 0.35f ? BiomeType.Tundra : BiomeType.BorealForest;
            }

            // Cool temperate.
            if (temperature < 0.5f)
            {
                if (moisture < 0.2f) return BiomeType.TemperateGrassland;
                if (moisture < 0.6f) return BiomeType.TemperateForest;
                return BiomeType.TemperateRainforest;
            }

            // Warm temperate.
            if (temperature < 0.75f)
            {
                if (moisture < 0.15f) return BiomeType.Desert;
                if (moisture < 0.4f) return BiomeType.TemperateGrassland;
                if (moisture < 0.7f) return BiomeType.TemperateForest;
                return BiomeType.TemperateRainforest;
            }

            // Hot.
            if (moisture < 0.15f) return BiomeType.Desert;
            if (moisture < 0.4f) return BiomeType.Savanna;
            if (moisture < 0.7f) return BiomeType.TropicalSeasonalForest;
            return BiomeType.TropicalRainforest;
        }
    }
}
