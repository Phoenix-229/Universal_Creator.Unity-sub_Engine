// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WhittakerBiome.cs
//  Purpose: The Whittaker climate-to-biome lookup, and the encode/decode pair that
//           carries a biome index through a Matrix. One implementation, two callers.
//
//  WHY THIS MOVED OUT OF THE CLASSIFIER NODE. Two places now need to name a biome:
//  the Biome Classifier, which bakes an index field for splats and masks, and
//  species selection, which asks whether a plant belongs to the biome it is
//  standing in. Copying ten thresholds into a second file would mean a world whose
//  splat map says tundra while its trees think boreal - a disagreement with no
//  error message anywhere and no way for an author to tell which half was wrong.
//  So the lookup lives here and the node calls it.
//
//  ECOLOGY DOES NOT NAME BiomeType. The enum is serialised into graph assets and
//  into BiomeSplatRule, so it stays where it is; moving it would discard authored
//  data. This file mirrors its integer values as consts instead, and the self-test
//  asserts the two agree member for member. That turns the one real risk of the
//  split - the enum being renumbered while these consts are not - into a test
//  failure rather than a silently mislabelled world.
//
//  THE THRESHOLDS ARE THE DIAGRAM'S, NOT TUNING. Whittaker's plot places biomes on
//  a temperature axis crossed with a precipitation axis; temperature is read first
//  because it gates which biomes are possible at all, and moisture then chooses
//  among the survivors. Normalised 0-1 on both axes because that is what the
//  climate nodes produce - the node upstream owns the conversion from degrees and
//  millimetres, and doing it twice is how the two would drift apart.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static class WhittakerBiome
    {
        // ---- Index mirror ----------------------------------------------------
        //
        //  These must equal UniverseCreator.Nodes.Biome.BiomeType member for member.
        //  Written out rather than cast, because Ecology may not depend on Nodes:
        //  scatter nodes reach into Ecology, and a reference the other way would
        //  close a cycle. Guarded by a self-test that compares the two.

        public const int Tundra = 0;
        public const int BorealForest = 1;
        public const int TemperateGrassland = 2;
        public const int TemperateForest = 3;
        public const int TemperateRainforest = 4;
        public const int Desert = 5;
        public const int Savanna = 6;
        public const int TropicalSeasonalForest = 7;
        public const int TropicalRainforest = 8;
        public const int Ice = 9;

        /// <summary>How many biome classes exist. Ten, and Ice is the last, not the first.</summary>
        public const int Count = 10;

        /// <summary>Below this normalised temperature the ground reads as permanent ice.</summary>
        public const float DefaultIceThreshold = 0.05f;

        // ---- Temperature bands, normalised 0-1 -------------------------------

        private const float ColdMax = 0.25f;
        private const float CoolMax = 0.50f;
        private const float WarmMax = 0.75f;

        // ---- Moisture breaks within each temperature band --------------------
        //
        //  Each band has its own breaks because the same rainfall means different
        //  things at different temperatures: 400 mm is grassland in the cool band
        //  and desert in the hot one, since evaporation rises with warmth.

        private const float ColdTundraMax = 0.35f;

        private const float CoolGrasslandMax = 0.20f;
        private const float CoolForestMax = 0.60f;

        private const float WarmDesertMax = 0.15f;
        private const float WarmGrasslandMax = 0.40f;
        private const float WarmForestMax = 0.70f;

        private const float HotDesertMax = 0.15f;
        private const float HotSavannaMax = 0.40f;
        private const float HotSeasonalMax = 0.70f;

        /// <summary>
        /// The biome at one point of the climate diagram. Both axes are 0-1.
        /// </summary>
        public static int Classify(float temperature, float moisture)
        {
            return Classify(temperature, moisture, DefaultIceThreshold);
        }

        /// <summary>
        /// The biome at one point of the climate diagram, with the caller's own ice
        /// threshold. Both axes are 0-1.
        /// </summary>
        public static int Classify(float temperature, float moisture, float iceThreshold)
        {
            if (temperature < iceThreshold) return Ice;

            if (temperature < ColdMax)
            {
                return moisture < ColdTundraMax ? Tundra : BorealForest;
            }

            if (temperature < CoolMax)
            {
                if (moisture < CoolGrasslandMax) return TemperateGrassland;
                if (moisture < CoolForestMax) return TemperateForest;
                return TemperateRainforest;
            }

            if (temperature < WarmMax)
            {
                if (moisture < WarmDesertMax) return Desert;
                if (moisture < WarmGrasslandMax) return TemperateGrassland;
                if (moisture < WarmForestMax) return TemperateForest;
                return TemperateRainforest;
            }

            if (moisture < HotDesertMax) return Desert;
            if (moisture < HotSavannaMax) return Savanna;
            if (moisture < HotSeasonalMax) return TropicalSeasonalForest;
            return TropicalRainforest;
        }

        /// <summary>
        /// BIO-1 / BIO-8: 0-1 "how much of a transition is this climate point". Zero deep inside a
        /// biome, rising toward 1 as the point nears Whittaker class breaks, measured by how many of
        /// the four climate-neighbours one ecotone-band away fall in a DIFFERENT class. This turns the
        /// hard index into a graded membership the splat/scatter can soft-blend against, and it reuses
        /// the SAME <see cref="Classify"/> thresholds (no second lookup to drift). <paramref name="climateBand"/>
        /// is the ecotone half-width in climate units (<see cref="EcoConstants.EcotoneBiomeClimateBand"/>).
        /// </summary>
        public static float BoundaryBlend(float temperature, float moisture, float iceThreshold, float climateBand)
        {
            int primary = Classify(temperature, moisture, iceThreshold);
            float d = Mathf.Max(1e-4f, climateBand);

            int differ = 0;
            if (Classify(temperature - d, moisture, iceThreshold) != primary) differ++;
            if (Classify(temperature + d, moisture, iceThreshold) != primary) differ++;
            if (Classify(temperature, moisture - d, iceThreshold) != primary) differ++;
            if (Classify(temperature, moisture + d, iceThreshold) != primary) differ++;

            return differ * 0.25f;
        }

        /// <summary>
        /// A biome index as the 0-1 value a Matrix carries it as.
        /// </summary>
        public static float Encode(int index)
        {
            if (index < 0) return 0f;
            if (index > Count - 1) index = Count - 1;

            return (float)index / (Count - 1);
        }

        /// <summary>
        /// A 0-1 field value back to a biome index. Rounding rather than truncating,
        /// so a value that has been through bilinear filtering lands on the nearest
        /// class instead of always on the lower one.
        /// </summary>
        /// <remarks>
        /// This is the same decision boundary BiomeMaskNode applies with its
        /// half-step tolerance, expressed as one operation instead of a comparison
        /// pair. A filtered value halfway between two classes is genuinely ambiguous,
        /// and that ambiguity is what the blend fraction beside it is for.
        /// </remarks>
        public static int Decode(float encoded)
        {
            int index = Mathf.RoundToInt(Mathf.Clamp01(encoded) * (Count - 1));

            if (index < 0) return 0;
            if (index > Count - 1) return Count - 1;

            return index;
        }

        /// <summary>
        /// The single-bit mask for one biome index, for testing against an envelope's
        /// allowedBiomes. Zero for an index outside the range, which reads as "no
        /// biome" rather than as biome zero.
        /// </summary>
        public static int MaskOf(int index)
        {
            if (index < 0 || index > Count - 1) return 0;
            return 1 << index;
        }

        /// <summary>Mask with every biome set. What "no restriction" looks like written out.</summary>
        public static int AllMask { get { return (1 << Count) - 1; } }
    }
}
