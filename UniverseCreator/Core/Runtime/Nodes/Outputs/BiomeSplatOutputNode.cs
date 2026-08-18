// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    BiomeSplatOutputNode.cs
//  Purpose: Pipeline stage 6 - builds the terrain splatmap from the biome field,
//           the surface slope and the altitude, instead of from four hand-wired
//           masks.
//
//  TextureOutputNode already covers the manual case: connect four masks, get four
//  weights. That stops scaling the moment a world has ten biomes and three ground
//  types each, because there is no fifth input to connect. This node inverts the
//  wiring - one biome field in, a rule per biome, and the weights fall out of the
//  terrain itself.
//
//  Three things decide the ground you are standing on, and they are ranked:
//    slope    beats everything. A cliff is rock whatever the climate says, because
//             soil and litter do not sit on 60 degrees.
//    altitude beats biome. Above the snowline every biome is snow.
//    biome    picks the ground when the surface is walkable and low enough.
//  The rules are evaluated in that order and blended, never switched, so a
//  hillside reads as grass fading into rock rather than as a hard stencil edge.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes.Biome;

namespace UniverseCreator.Nodes.Outputs
{
    /// <summary>
    /// One biome's ground assignment. A struct of scalars on purpose: NodeDrawer
    /// renders a List of those inline, so the rule table needs no custom editor.
    /// </summary>
    [System.Serializable]
    public struct BiomeSplatRule
    {
        public BiomeType biome;

        /// <summary>Terrain layer on level, low ground - the biome's own floor.</summary>
        public int groundLayer;

        /// <summary>Terrain layer once the slope test passes - normally rock.</summary>
        public int slopeLayer;

        /// <summary>Degrees where the slope layer starts bleeding in.</summary>
        public float slopeStart;

        /// <summary>Degrees where the slope layer has fully taken over.</summary>
        public float slopeEnd;

        /// <summary>Layer above the biome's own treeline - alpine scree, snow, sand.</summary>
        public int highLayer;

        /// <summary>Normalised height where the high layer starts.</summary>
        public float highStart;

        /// <summary>Normalised height where the high layer is total.</summary>
        public float highEnd;

        /// <summary>Multiplies this biome's contribution. 0 disables the rule.</summary>
        public float weight;

        public static BiomeSplatRule Default(BiomeType b, int ground)
        {
            BiomeSplatRule r = new BiomeSplatRule();
            r.biome = b;
            r.groundLayer = ground;
            r.slopeLayer = 1;
            r.slopeStart = 28f;
            r.slopeEnd = 46f;
            r.highLayer = ground;
            r.highStart = 1f;
            r.highEnd = 1f;
            r.weight = 1f;
            return r;
        }
    }

    [System.Serializable]
    public class BiomeSplatOutputNode : OutputNode
    {
        public override string NodeName { get { return "Biome Splat Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }

        public override string Tooltip
        {
            get { return "Build the splatmap from biome, slope and altitude with a rule per biome."; }
        }

        private static readonly Port[] SplatInputs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Biome", PortType.Matrix, PortDirection.Input),
            new Port("Wetness", PortType.Matrix, PortDirection.Input),
            new Port("Override", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return SplatInputs; }

        [Tooltip("How many terrain layers the splatmap carries. Must match the layer " +
                 "count on the world, or Unity rejects the write.")]
        public int layerCount = 8;

        [Tooltip("Ground assignment per biome. Any biome with no rule falls back to " +
                 "the default layer.")]
        public List<BiomeSplatRule> rules = new List<BiomeSplatRule>();

        [Tooltip("Layer used where no rule matches.")]
        public int defaultLayer = 0;

        [Tooltip("Degrees of blur applied to the biome field before it is read. The " +
                 "classifier snaps to whole biomes, so without this every biome " +
                 "border is a one-pixel staircase.")]
        public float biomeSoftening = 2.5f;

        [Tooltip("Raises each weight to this power before normalising. Above 1 the " +
                 "dominant layer wins harder and transitions get tighter; below 1 " +
                 "everything smears together.")]
        public float contrast = 1.6f;

        [Header("Wetness")]
        [Tooltip("Layer painted where the Wetness input is high - river silt, mud, " +
                 "the darker band along a shoreline.")]
        public int wetLayer = 2;

        [Tooltip("Wetness needed before the wet layer appears at all.")]
        public float wetThreshold = 0.45f;

        [Tooltip("Full strength of the wet layer where wetness is 1.")]
        public float wetStrength = 1f;

        [Header("Shoreline")]
        [Tooltip("Paint a beach band around the sea level.")]
        public bool beachEnabled = true;

        public int beachLayer = 3;

        [Tooltip("Normalised height of the water plane.")]
        public float seaLevel = 0.32f;

        [Tooltip("How far above sea level the beach reaches, in normalised height.")]
        public float beachHeight = 0.012f;

        [Tooltip("Beaches do not form on cliffs. Above this slope the band is skipped.")]
        public float beachMaxSlope = 18f;

        [Header("Snow")]
        [Tooltip("Paint snow above the snowline, independent of biome. Biome rules " +
                 "already carry a high layer; this is the world-wide cap on top.")]
        public bool snowEnabled = true;

        public int snowLayer = 4;

        [Tooltip("Normalised height where snow starts.")]
        public float snowLine = 0.68f;

        [Tooltip("Height band over which snow fades in.")]
        public float snowBlend = 0.09f;

        [Tooltip("Snow slides off anything steeper than this, leaving bare rock.")]
        public float snowMaxSlope = 42f;

        [Tooltip("Softness of that slide-off, in degrees.")]
        public float snowSlopeBlend = 9f;

        [Header("Override input")]
        [Tooltip("Layer the Override mask paints. Wired for hand-painted regions and " +
                 "for road and path masks, which have to beat every procedural rule.")]
        public int overrideLayer = 5;

        // Baked once per Generate so the inner loop does a lookup instead of a
        // linear scan over the rule list per pixel.
        [System.NonSerialized] private BiomeSplatRule[] ruleByBiome;

        /// <summary>Fills the rule list with a sane assignment for all ten biomes.</summary>
        public void PopulateDefaults()
        {
            rules.Clear();

            // Layer slots this assumes, and the reason each biome points where it
            // does. Users will retarget the indices; the shape is what matters.
            //   0 rock   1 cliff rock   2 mud/silt   3 sand   4 snow
            //   5 road   6 grass        7 forest floor
            rules.Add(Rule(BiomeType.Tundra, 6, 1, 0.55f, 0.75f));
            rules.Add(Rule(BiomeType.BorealForest, 7, 1, 0.62f, 0.80f));
            rules.Add(Rule(BiomeType.TemperateGrassland, 6, 1, 0.70f, 0.88f));
            rules.Add(Rule(BiomeType.TemperateForest, 7, 1, 0.68f, 0.86f));
            rules.Add(Rule(BiomeType.TemperateRainforest, 7, 1, 0.72f, 0.90f));
            rules.Add(Rule(BiomeType.Desert, 3, 1, 0.80f, 0.95f));
            rules.Add(Rule(BiomeType.Savanna, 6, 1, 0.78f, 0.93f));
            rules.Add(Rule(BiomeType.TropicalSeasonalForest, 7, 1, 0.75f, 0.92f));
            rules.Add(Rule(BiomeType.TropicalRainforest, 7, 1, 0.78f, 0.94f));
            rules.Add(Rule(BiomeType.Ice, 4, 1, 0.30f, 0.50f));

            MarkDirty();
        }

        private static BiomeSplatRule Rule(BiomeType b, int ground, int slope, float hs, float he)
        {
            BiomeSplatRule r = BiomeSplatRule.Default(b, ground);
            r.slopeLayer = slope;

            // Alpine biomes give up their soil sooner. Cold ground has less of it
            // and what there is gets stripped by frost heave, so the rock line sits
            // lower on a tundra slope than on a rainforest one.
            if (b == BiomeType.Ice || b == BiomeType.Tundra) { r.slopeStart = 20f; r.slopeEnd = 36f; }
            else if (b == BiomeType.Desert) { r.slopeStart = 24f; r.slopeEnd = 40f; }

            r.highLayer = b == BiomeType.Desert ? 0 : 4;
            r.highStart = hs;
            r.highEnd = he;
            return r;
        }

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            Matrix biome = GetInputMatrix(tile, 1, stop);
            Matrix wetness = GetInputMatrix(tile, 2, stop);
            Matrix overrideMask = GetInputMatrix(tile, 3, stop);

            if (stop != null && stop.StopRequested) return;

            // Softened before it is read, not after: the field is an encoded index,
            // so blurring it produces intermediate values that decode to the biomes
            // sitting between the two neighbours in enum order. That is wrong as a
            // classification and right as a blend weight, which is exactly what it
            // is used for below.
            if (biome != null && biomeSoftening > 0.01f)
                biome = MatrixOperations.GaussianBlur(biome, biomeSoftening);

            Matrix slopes = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return;

            BuildRuleTable();

            int layers = Mathf.Max(1, layerCount);
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            float[,,] splat = new float[h, w, layers];

            // One scratch row of weights, reused per pixel. Allocating per pixel on
            // a 1025 grid is a million throwaway arrays per chunk.
            float[] acc = new float[layers];

            float encodeScale = BiomeClassifierNode.BiomeCount - 1;
            float power = Mathf.Max(0.05f, contrast);

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = rowStart + x;

                    for (int L = 0; L < layers; L++) acc[L] = 0f;

                    float height = InterpolationMath.Clamp01(src.arr[i]);
                    float slope = slopes != null ? slopes.arr[i] : 0f;

                    // Biome. The encoded field is fractional after the blur, so the
                    // two enclosing biomes are both applied and cross-faded rather
                    // than the value being rounded to one of them.
                    float encoded = biome != null ? InterpolationMath.Clamp01(biome.arr[i]) : -1f;

                    if (encoded < 0f)
                    {
                        AddWeight(acc, layers, defaultLayer, 1f);
                    }
                    else
                    {
                        float scaled = encoded * encodeScale;
                        int lowIndex = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, (int)encodeScale);
                        int highIndex = Mathf.Min(lowIndex + 1, (int)encodeScale);
                        float frac = scaled - lowIndex;

                        ApplyRule(acc, layers, lowIndex, height, slope, 1f - frac);
                        if (highIndex != lowIndex && frac > 0.0001f)
                            ApplyRule(acc, layers, highIndex, height, slope, frac);
                    }

                    // Wetness. Additive rather than exclusive: wet grass is still
                    // grass with silt over it, and replacing the biome layer outright
                    // makes every riverbank read as bare mud.
                    if (wetness != null && wetStrength > 0f)
                    {
                        float wv = InterpolationMath.Clamp01(wetness.arr[i]);
                        if (wv > wetThreshold)
                        {
                            float t = (wv - wetThreshold) / Mathf.Max(0.0001f, 1f - wetThreshold);
                            AddWeight(acc, layers, wetLayer, InterpolationMath.SmoothStep(t) * wetStrength);
                        }
                    }

                    // Beach. A band, not a threshold - sand below the waterline as
                    // well as above it, because the shallows are the part that is
                    // actually visible through the water surface.
                    if (beachEnabled && beachHeight > 0.0001f)
                    {
                        float d = Mathf.Abs(height - seaLevel);
                        if (d < beachHeight)
                        {
                            float band = 1f - (d / beachHeight);
                            float slopeCut = 1f - InterpolationMath.Clamp01(
                                (slope - beachMaxSlope) / 12f);
                            AddWeight(acc, layers, beachLayer,
                                      InterpolationMath.SmoothStep(band) * slopeCut * 1.4f);
                        }
                    }

                    // Snow, last of the procedural rules so it sits on top of the
                    // biome ground and the beach both.
                    if (snowEnabled)
                    {
                        float t = InterpolationMath.InverseLerp(
                            snowLine, snowLine + Mathf.Max(0.0001f, snowBlend), height);

                        if (t > 0f)
                        {
                            float slopeCut = 1f - InterpolationMath.Clamp01(
                                (slope - snowMaxSlope) / Mathf.Max(0.5f, snowSlopeBlend));

                            float amount = InterpolationMath.SmoothStep(t) * slopeCut;
                            if (amount > 0f)
                            {
                                // Snow scales the ground down as well as adding
                                // itself. Purely additive snow never fully covers,
                                // because the biome weight underneath keeps voting.
                                for (int L = 0; L < layers; L++) acc[L] *= 1f - amount * 0.9f;
                                AddWeight(acc, layers, snowLayer, amount * 2f);
                            }
                        }
                    }

                    // Contrast, before the override. Roads are meant to be absolute,
                    // and sharpening a weight of 1 against a weight of 0 does nothing
                    // anyway.
                    if (power != 1f)
                        for (int L = 0; L < layers; L++)
                            if (acc[L] > 0f) acc[L] = Mathf.Pow(acc[L], power);

                    if (overrideMask != null)
                    {
                        float ov = InterpolationMath.Clamp01(overrideMask.arr[i]);
                        if (ov > 0.0001f)
                        {
                            for (int L = 0; L < layers; L++) acc[L] *= 1f - ov;
                            AddWeight(acc, layers, overrideLayer, ov);
                        }
                    }

                    float sum = 0f;
                    for (int L = 0; L < layers; L++) sum += acc[L];

                    if (sum < 1e-6f)
                    {
                        // Nothing voted. Falling through to an all-zero pixel makes
                        // Unity render untextured grey, which reads as a bug rather
                        // than as an unpainted spot.
                        splat[z, x, Mathf.Clamp(defaultLayer, 0, layers - 1)] = 1f;
                        continue;
                    }

                    float inv = 1f / sum;
                    for (int L = 0; L < layers; L++) splat[z, x, L] = acc[L] * inv;
                }
            }

            tile.outputs.splatmap = splat;
        }

        private void ApplyRule(float[] acc, int layers, int biomeIndex,
                               float height, float slope, float share)
        {
            if (share <= 0f) return;

            BiomeSplatRule r = ruleByBiome != null && biomeIndex < ruleByBiome.Length
                             ? ruleByBiome[biomeIndex]
                             : BiomeSplatRule.Default((BiomeType)biomeIndex, defaultLayer);

            if (r.weight <= 0f) return;
            share *= r.weight;

            // Slope first. It is a fraction of the biome's own share, so a cliff in
            // a forest is forest-rock and a cliff in a desert is desert-rock, and
            // the two still cross-fade correctly along the biome border.
            float slopeT = 0f;
            if (r.slopeEnd > r.slopeStart)
                slopeT = InterpolationMath.SmoothStep(
                    InterpolationMath.InverseLerp(r.slopeStart, r.slopeEnd, slope));

            // Then altitude, on whatever the slope left.
            float highT = 0f;
            if (r.highEnd > r.highStart)
                highT = InterpolationMath.SmoothStep(
                    InterpolationMath.InverseLerp(r.highStart, r.highEnd, height));

            float toSlope = share * slopeT;
            float rest = share - toSlope;
            float toHigh = rest * highT;
            float toGround = rest - toHigh;

            AddWeight(acc, layers, r.slopeLayer, toSlope);
            AddWeight(acc, layers, r.highLayer, toHigh);
            AddWeight(acc, layers, r.groundLayer, toGround);
        }

        private static void AddWeight(float[] acc, int layers, int layer, float amount)
        {
            if (amount <= 0f) return;
            if (layer < 0 || layer >= layers) return;
            acc[layer] += amount;
        }

        private void BuildRuleTable()
        {
            int count = BiomeClassifierNode.BiomeCount;

            if (ruleByBiome == null || ruleByBiome.Length != count)
                ruleByBiome = new BiomeSplatRule[count];

            for (int i = 0; i < count; i++)
                ruleByBiome[i] = BiomeSplatRule.Default((BiomeType)i, defaultLayer);

            if (rules == null) return;

            for (int i = 0; i < rules.Count; i++)
            {
                int index = (int)rules[i].biome;
                if (index >= 0 && index < count) ruleByBiome[index] = rules[i];
            }
        }
    }
}
