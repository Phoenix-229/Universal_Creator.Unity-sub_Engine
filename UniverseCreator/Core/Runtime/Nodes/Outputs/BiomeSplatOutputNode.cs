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
using UniverseCreator.Ecology;   // HydroField: the shared whole-world water distance field

namespace UniverseCreator.Nodes.Outputs
{
    /// <summary>
    /// One biome's serialized ground assignment, edited inline by the node's Rules controls.
    /// Generation validates a copy; invalid saved values never mutate terrain geometry or the authored rule.
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

        /// <summary>Creates an enabled rule with a 28–46 degree slope blend and altitude blending disabled.</summary>
        /// <param name="b">Biome category; generation ignores unknown categories.</param>
        /// <param name="ground">Zero-based terrain layer index; generation clamps it to the available layers.</param>
        /// <returns>An independent value with weight 1; no assets or saved rules are modified.</returns>
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
    public class BiomeSplatOutputNode : OutputNode, IPreparedGraphInputs
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
            new Port("Override", PortType.Matrix, PortDirection.Input),
            // BIO-8: the classifier's graded ecotone Blend field (BIO-1). Optional - unwired leaves
            // the index-blur cross-fade exactly as before. Wired, it tells the splat WHERE a real
            // transition is, so biome-interior pixels snap to one biome and only true ecotones mix.
            new Port("Blend", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return SplatInputs; }

        /// <summary>
        /// BIO-8: the biome cross-fade fraction after the classified ecotone has its say. At
        /// blend 0 (biome interior) it snaps to the dominant biome (0 or 1 - no index-blur mixing);
        /// at blend 1 (a real ecotone) it is the raw fraction. Pure/self-testable.
        /// </summary>
        internal static float BlendCrossfade(float frac, float blend01)
        {
            return Mathf.Lerp(frac >= 0.5f ? 1f : 0f, frac, Mathf.Clamp01(blend01));
        }

        [Tooltip("How many terrain layers the splatmap carries. Must match the layer " +
                 "count on the world, or Unity rejects the write.")]
        public int layerCount = 8;

        [Tooltip("Ground assignment per biome. Any biome with no rule falls back to " +
                 "the default layer.")]
        public List<BiomeSplatRule> rules = new List<BiomeSplatRule>();

        [Tooltip("BIO-5: optional shared Biome Splat Palette asset. When assigned AND populated it " +
                 "overrides the per-biome rules above from one reusable asset; ships empty, so an " +
                 "unassigned or empty palette keeps the rules above (today's behaviour).")]
        public BiomeSplatPalette palette;

        [Tooltip("Layer used where no rule matches.")]
        public int defaultLayer = 0;

        // UC-T03-03: GaussianBlur consumes sigma in grid samples, not an angle.
        [Tooltip("UC-T07-04: Gaussian sigma, 0…8 source grid samples, applied independently to membership of actual biome categories. " +
                 "Work and memory are bounded; no intermediate enum categories are created.")]
        [Range(0f, 8f)] public float biomeSoftening = 2.5f;

        [Tooltip("BIO-10: metres of organic wobble added to biome borders by a tileable noise warp " +
                 "of where the border falls, so neighbouring biomes interlock in fingers instead of a " +
                 "straight blurred line. 0 (default) = straight border, unchanged. Deterministic.")]
        public float biomeBlendNoise = 0f;

        [Tooltip("BIO-10: feature size in metres of that border wobble. Larger = broader fingers.")]
        public float biomeBlendNoiseScaleM = 30f;

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

        [Tooltip("Normalised height of the water plane. The world's one waterline - the " +
                 "same default Ocean Level and the Hydrology Spawner use, so the beach " +
                 "is painted where the water actually meets the land.")]
        public float seaLevel = 0.35f;

        [Tooltip("How far above sea level the beach reaches, in normalised height.")]
        public float beachHeight = 0.012f;

        [Tooltip("Beaches do not form on cliffs. Above this slope the band is skipped.")]
        public float beachMaxSlope = 18f;

        // WATER-BARE BANDS (author rule 2026-10-05). A lake/ocean BED and the strip of
        // shore right at the waterline must never receive the blended world texture -
        // they read as grass running into the water. Instead all land weight is dropped
        // and the whole pixel is handed to one reserved layer, so where that layer ships
        // EMPTY (the default - no path is ever hardcoded) the terrain falls back to the
        // native Unity look it had before texturing, and where the author DOES wire a
        // lake-bed / sea-bed texture into that slot, the bed textures itself.
        //
        // bareLayer defaults to 0, which is also `defaultLayer`: that is the OCG
        // "reserve splat index 0 = water / no-land sentinel" idea (§4d) landing as code.
        //
        // Bands are horizontal metres from the water EDGE, measured on the whole-world
        // hydrology field (HydroField), so the band is the same width on every chunk and
        // does not restart at a seam.
        [Header("Water-bare bands")]
        [Tooltip("Leave water beds and the shore strip untextured, falling back to the " +
                 "reserved bare layer instead of the blended world texture.")]
        public bool waterBareEnabled = true;

        [Tooltip("Reserved layer that water beds and shore strips are handed to. Ships " +
                 "with empty texture slots, so it reads as the untextured terrain default " +
                 "unless the author wires a bed texture into it.")]
        // Index 6, NOT 0. It used to default to 0 - the same index as defaultLayer - so the
        // "reserved" bed layer was in fact the general-purpose default layer, which every
        // author fills with grass. The bed override fired correctly and then painted weight 1
        // onto the grass slot, which is why an ocean bed came out grassy for an author who had
        // deliberately left the bed texture empty (author report, 2026-10-06). 6 continues the
        // one-index-per-special-layer convention already used by wet(2)/beach(3)/snow(4)/
        // override(5); ResolveBareLayer below re-homes it anyway if it is ever claimed.
        public int bareLayer = 6;

        [Tooltip("Metres of LAKE shore left bare, measured horizontally from the " +
                 "waterline (author spec: 5-8 m).")]
        public float lakeBareMetres = 6.5f;

        [Tooltip("Metres of RIVER bank left bare, measured horizontally from the " +
                 "waterline. A river is narrower than a lake, so its bare bank is " +
                 "tighter; its BED is covered by the waterline test and needs no band.")]
        public float riverBareMetres = 4f;

        [Tooltip("Metres of OCEAN shore left bare, measured horizontally from the " +
                 "waterline (author spec: 12-15 m).")]
        public float oceanBareMetres = 13.5f;

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

        // Snow paints a WEIGHT into snowLayer; these give that layer its LOOK. All
        // empty by default - the tool ships no texture, and any format Unity imports
        // works. Leave them empty and snow paints as a flat tinted layer (never the
        // grey native stand-in the user reported); drop in a snow albedo/normal/AO
        // and the snow channel textures itself. No path is ever hardcoded.
        [Tooltip("Snow base colour map. Empty = a flat layer tinted by Snow Tint.")]
        public Texture2D snowAlbedo;

        [Tooltip("Snow normal map. Empty = flat-lit snow.")]
        public Texture2D snowNormal;

        [Tooltip("Snow ambient-occlusion map. Empty = none.")]
        public Texture2D snowAO;

        [Tooltip("Flat colour of the snow layer until a Snow Albedo is assigned, and " +
                 "the tint applied over it after. Defaults to a cold off-white.")]
        public Color snowTint = new Color(0.92f, 0.94f, 0.97f, 1f);

        [Tooltip("Metres per snow-texture repeat.")]
        public Vector2 snowTileSize = new Vector2(10f, 10f);

        [Tooltip("Snow surface smoothness (used when no packed mask is supplied), 0..1.")]
        [Range(0f, 1f)] public float snowSmoothness = 0.1f;

        [Header("Override input")]
        [Tooltip("Layer the Override mask paints. Wired for hand-painted regions and " +
                 "for road and path masks, which have to beat every procedural rule.")]
        public int overrideLayer = 5;

        [Header("Layer textures (optional)")]
        [Tooltip("Textures for each splat channel this node reserves. THIS is where the " +
                 "ground layers are dressed - the 4-channel Texture Graph cannot reach " +
                 "channels 4-7, so grass/forest/snow must be textured here. Each entry " +
                 "dresses one channel INDEX; nothing keys off a name. Canonical legend: " +
                 "0 Rock, 1 Cliff Rock, 2 Mud/Silt, 3 Sand, 4 Snow (use the Snow fields " +
                 "above), 5 Road, 6 Grass, 7 Forest Floor. Empty albedo paints the slot's " +
                 "flat tint, never Unity's grey. Slots are seeded labeled the first time; " +
                 "drop a texture into the one you want.")]
        public List<SplatLayerTexture> layerTextures = new List<SplatLayerTexture>();

        // BIO-5: the palette's rules, snapshotted on the main thread in PrepareMainThread so the
        // off-thread BuildRuleTable reads plain managed data, never the ScriptableObject.
        [System.NonSerialized] private List<BiomeSplatRule> paletteRules;

        /// <summary>UC-T01-06/07: captures palette value rules into a private list before hashing and worker dispatch.</summary>
        /// <remarks>Main thread only. An absent or empty palette falls back to the node's serialized rules.</remarks>
        public override void PrepareMainThread()
        {
            base.PrepareMainThread();
            // UC-T01-06/07: rules are value structs; copying the list detaches in-place palette edits.
            paletteRules = (palette != null && palette.HasRules) ? new List<BiomeSplatRule>(palette.rules) : null;
        }

        [System.Serializable]
        private sealed class PreparedPaletteRules
        {
            public List<BiomeSplatRule> rules;
        }

        /// <summary>UC-T01-07: exposes copied palette assignments to dependency hashing.</summary>
        /// <returns>Plain prepared rules, or null when the node uses its serialized rules.</returns>
        /// <remarks>Main thread after preparation; does not read the live palette.</remarks>
        public object GetPreparedInputsForHash()
        {
            return paletteRules != null ? new PreparedPaletteRules { rules = paletteRules } : null;
        }

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

            EnsureLayerTextureSlots();
            MarkDirty();
        }

        /// <summary>
        /// Seeds one labeled, empty-but-tinted slot per reserved ground channel, so the
        /// user sees named slots (Grass, Rock, ...) to drop textures into instead of
        /// guessing an index, and an unfilled channel paints its plausible flat colour
        /// rather than the brown autofill stand-in. Snow (4) keeps its dedicated fields.
        /// Only runs when the list is empty, so it never clobbers user entries. No path,
        /// filename, or format is assumed - albedo stays null until the buyer assigns one.
        /// </summary>
        public void EnsureLayerTextureSlots()
        {
            if (layerTextures == null) layerTextures = new List<SplatLayerTexture>();
            if (layerTextures.Count > 0) return;

            layerTextures.Add(Slot(0, "Rock",         new Color(0.46f, 0.45f, 0.43f), 0.30f));
            layerTextures.Add(Slot(1, "Cliff Rock",   new Color(0.38f, 0.37f, 0.36f), 0.35f));
            layerTextures.Add(Slot(2, "Mud / Silt",   new Color(0.34f, 0.27f, 0.20f), 0.20f));
            layerTextures.Add(Slot(3, "Sand",         new Color(0.72f, 0.65f, 0.48f), 0.26f));
            layerTextures.Add(Slot(5, "Road",         new Color(0.28f, 0.28f, 0.29f), 0.35f));
            layerTextures.Add(Slot(6, "Grass",        new Color(0.32f, 0.44f, 0.20f), 0.30f));
            layerTextures.Add(Slot(7, "Forest Floor", new Color(0.22f, 0.28f, 0.16f), 0.25f));
        }

        private static SplatLayerTexture Slot(int layer, string label, Color tint, float smoothness)
        {
            return new SplatLayerTexture
            {
                layer = layer,
                label = label,
                tint = tint,
                tileSizeMetres = new Vector2(8f, 8f),
                smoothness = smoothness,
                normalScale = 1f
            };
        }

        /// <summary>
        /// Whether the author deliberately wired a BED ALBEDO into the water-bare channel.
        /// Only an albedo map counts: the channel is seeded with a flat tint by
        /// <see cref="EnsureLayerTextureSlots"/> like every ground slot, and a bed must
        /// read as the UNTEXTURED terrain default (author spec), not as that tint. So a
        /// tint-only bare slot is treated as "empty" here and suppressed at emit time,
        /// leaving the channel to mint the neutral stand-in; an albedo means the author
        /// chose a bed look and it is kept. Thread-safe null test per SplatLayerTexture.
        /// </summary>
        private bool BareLayerHasAuthoredBed(int bareChannel)
        {
            if (layerTextures == null) return false;
            for (int s = 0; s < layerTextures.Count; s++)
                if (layerTextures[s].layer == bareChannel &&
                    !ReferenceEquals(layerTextures[s].albedo, null))
                    return true;
            return false;
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
            // A node authored before the labeled slots existed still gets them, so the
            // ground channels the Texture Graph cannot reach (grass 6, forest 7, ...)
            // paint their tint instead of the brown autofill. No-op once seeded/edited.
            EnsureLayerTextureSlots();

            Matrix biome = GetInputMatrix(tile, 1, stop);
            Matrix wetness = GetInputMatrix(tile, 2, stop);
            Matrix overrideMask = GetInputMatrix(tile, 3, stop);
            Matrix blendField = GetInputMatrix(tile, 4, stop);   // BIO-8 graded ecotone

            tile.outputs.biomeClassification = biome; // UC-T07-03: preserve original categorical labels for immutable accepted-world sampling.

            if (stop != null && stop.StopRequested) return;

            // UC-T07-04: request true neighbours through the existing graph/halo owner before filtering categorical membership.
            TextureImageSampling.RequireFinite(biomeSoftening); TextureImageSampling.RequireFinite(biomeBlendNoise);
            TextureImageSampling.RequireFinite(biomeBlendNoiseScaleM);
            if (biomeSoftening < 0 || biomeSoftening > 8 || biomeBlendNoise < 0)
                throw new System.ArgumentException("Biome softening must be 0…8 samples and warp amplitude must be nonnegative metres.");
            Matrix filterLabels = biome;
            if (biome != null)
            {
                int margin = Mathf.CeilToInt(3f * biomeSoftening) + TileWindows.MarginForMetres(tile, biomeBlendNoise);
                if (margin > 256) throw new System.InvalidOperationException("UC-T07-04: biome halo exceeds 256 samples; reduce warp/softening explicitly.");
                TileData halo = TileWindows.Expand(tile, margin);
                if (halo != null)
                {
                    filterLabels = halo.PullInput(this, 1, stop) as Matrix;
                    blendField = halo.PullInput(this, 4, stop) as Matrix;
                    if (filterLabels == null || (stop != null && stop.StopRequested)) return;
                }
            }
            // UC-T07-04: filter membership of actual labels. Averaging enum IDs invents intervening biomes at a border.
            using BiomeMembershipField membership = filterLabels == null ? null : BiomeMembershipField.Build(filterLabels, biomeSoftening, tile, stop);
            if (biome != null && membership == null) return;

            Matrix slopes = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return;

            // UC-T01-06: chunk workers cannot reset a table another chunk is consuming.
            BiomeSplatRule[] ruleByBiome = BuildRuleTable();

            int layers = Mathf.Max(1, layerCount);
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            float[,,] splat = new float[h, w, layers];

            // One scratch row of weights, reused per pixel. Allocating per pixel on
            // a 1025 grid is a million throwaway arrays per chunk.
            float[] acc = new float[layers];

            float power = Finite(contrast) ? Mathf.Max(0.05f, contrast) : 1f; // UC biome repair: malformed serialized contrast must not produce NaN alphamaps.

            // THE BED TEST NEEDS THE HYDROLOGY FIELD, NOT A HEIGHT COMPARISON (author
            // report 2026-10-09, with a screenshot of a bare untextured pan: "it is treated
            // as a basin not a plain but no water there … that is why no matter what we do it
            // never moves up").
            //
            // The old bed test was `height <= seaLevel`. That is the real bug behind the
            // untextured flat area: a DRY plain sitting at sea level is not under water, yet
            // `height <= sea` flagged it as a bed and painted it bare, so it read as sea bed
            // with no sea on it. "At or below the waterline" is NOT the same question as "is
            // there water here" - a closed depression or a sea-level flat can be bone dry.
            //
            // So the bed is now defined by ACTUAL water membership: `DistanceToWaterM <= 0`,
            // i.e. inside the ocean or a lake on the SAME whole-world hydrology field the
            // terrain passes and ecology use (HydroField's distance is 0 inside a body,
            // positive on dry land). A dry plain is positive-distance and textures normally;
            // a true lake or sea bed is zero and stays bare. One definition of "water",
            // shared, so the texture can never again disagree with the terrain about it.
            //
            // Because the bed test now depends on the field, the field is built whenever the
            // feature is on - not only when a shore band is non-zero.
            HydroField hydro = null;
            float bareLake  = Mathf.Max(0f, lakeBareMetres);
            float bareOcean = Mathf.Max(0f, oceanBareMetres);
            float bareRiver = Mathf.Max(0f, riverBareMetres);
            int   bareIdx   = ResolveBareLayer(layers);
            float seaNormForBare = InterpolationMath.Clamp01(seaLevel);

            if (waterBareEnabled)
            {
                WorldDrainage bareWd = WorldDrainage.Solve(
                    this, 0, tile, SeaLevelFloor.DefaultResolution, 0f, false, stop);
                if (stop != null && stop.StopRequested) return;

                if (bareWd != null && bareWd.height != null)
                {
                    float baseY  = bareWd.height.worldPos.y;
                    float scaleY = bareWd.height.worldHeight;
                    hydro = HydroField.For(bareWd, baseY + seaNormForBare * scaleY);
                }
            }

            // Per-pixel world XZ, for the metre-accurate band lookup.
            float bareBaseX = src.worldPos.x;
            float bareBaseZ = src.worldPos.z;
            Vector2 barePix = src.PixelSize;

            // BIO-10: optional border-interlock warp. A tileable fBm displaces WHERE the biome
            // border is sampled, so adjacent biomes meet in organic fingers instead of a straight
            // blurred line. Null (amplitude 0) leaves the straight read untouched - zero regression.
            Noise biomeWarp = (biome != null && biomeBlendNoise > 0.001f) ? new Noise(917) : null;
            float warpSize = Mathf.Max(1f, biomeBlendNoiseScaleM);

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

                    // WATER-BARE: a bed or a shore strip takes NO land texture. Handing
                    // the whole pixel to the reserved bare layer (weight 1, everything
                    // else 0) is what makes it read as the untextured terrain default,
                    // and it skips the biome/wet/beach/snow logic below entirely so no
                    // grass, forest or rock weight can leak onto a lake bed or a beach
                    // strip. `continue` is deliberate: this is an override, not a blend.
                    //
                    // Bed test uses the node's own NORMALISED seaLevel (the same
                    // convention the beach band below uses) rather than HydroField's
                    // world-metre surface, so no unit conversion can drift. The shore
                    // test uses metres from HydroField, which is the author's spec.
                    //
                    // THE BED TEST IS NOT GATED ON THE HYDROLOGY FIELD. It used to live
                    // inside `if (hydro != null)`, and `hydro` was only built when a shore
                    // band was non-zero - so zeroing the shore offsets silently switched
                    // off the BED override too, and the sea bed fell through to the biome
                    // logic and came out grassy (author report, 2026-10-06). A bed is
                    // "everything at or below the waterline", a pure height comparison
                    // that needs no distance field and no band, so it now runs whenever
                    // the feature is enabled, whatever the offsets are set to.
                    if (waterBareEnabled && hydro != null)
                    {
                        float wx = bareBaseX + x * barePix.x;
                        float wz = bareBaseZ + z * barePix.y;

                        // Bed / depth: ACTUALLY inside a water body (ocean or lake), not
                        // merely at or below the waterline. HydroField's distance is 0
                        // inside a body and positive on dry land, so a dry sea-level plain
                        // reads positive and is NOT a bed - which is the whole fix. A river
                        // bed is a carved channel below its own water and is covered by the
                        // river carve's own texturing, not by this ocean/lake test.
                        float dW = hydro.DistanceToWaterM(wx, wz);
                        bool bare = dW <= 0f;

                        if (!bare)
                        {
                            // Shore: horizontal metres from the water edge, banded by which
                            // body is nearest. Ocean reaches furthest inland, a lake less,
                            // a river bank least - the author's three offsets, one each.
                            float band;
                            switch (hydro.NearestKind(wx, wz))
                            {
                                case WaterBodyKind.Ocean: band = bareOcean; break;
                                case WaterBodyKind.River: band = bareRiver; break;
                                default:                  band = bareLake;  break;
                            }

                            if (band > 0f) bare = dW <= band;
                        }

                        if (bare)
                        {
                            splat[z, x, bareIdx] = 1f;
                            continue;
                        }
                    }

                    // UC-T07-04: independent filtered memberships preserve the actual categories through diagonal/warped ecotones.
                    if (membership == null)
                    {
                        AddWeight(acc, layers, defaultLayer, 1f);
                    }
                    else
                    {
                        // UC-T07-04: warp absolute world metres, then sample independent nonnegative memberships.
                        float wx = bareBaseX + x * barePix.x, wz = bareBaseZ + z * barePix.y;
                        if (biomeWarp != null)
                        {
                            float ox = biomeWarp.Fractal3D(wx, 0f, wz, warpSize) * biomeBlendNoise;
                            float oz = biomeWarp.Fractal3D(wz, 53f, wx, warpSize) * biomeBlendNoise;
                            wx += ox; wz += oz;
                        }
                        float mix = 1f;
                        if (blendField != null)
                        {
                            Vector2 bp = blendField.WorldToPixel(wx, wz);
                            mix = blendField.GetInterpolated(bp.x, bp.y); TextureImageSampling.RequireFinite(mix); mix = Mathf.Clamp01(mix);
                        }
                        int nearest = membership.NearestCategory(wx, wz);
                        foreach (int category in membership.Categories)
                        {
                            float weight = Mathf.Lerp(category == nearest ? 1f : 0f, membership.Sample(category, wx, wz), mix);
                            if (weight > 0.000001f) ApplyRule(ruleByBiome, acc, layers, category, height, slope, weight);
                        }
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
                    {
                        // UC biome repair: preserve relative contrast while bounding powers before overflow.
                        // Ordinary weights (<=1) retain their original override-mask strength.
                        float scale = 1f;
                        for (int L = 0; L < layers; L++) if (Finite(acc[L])) scale = Mathf.Max(scale, acc[L]);
                        for (int L = 0; L < layers; L++)
                            if (acc[L] > 0f) acc[L] = Mathf.Pow(acc[L] / scale, power);
                    }

                    if (overrideMask != null)
                    {
                        float ov = InterpolationMath.Clamp01(overrideMask.arr[i]);
                        if (ov > 0.0001f)
                        {
                            for (int L = 0; L < layers; L++) acc[L] *= 1f - ov;
                            AddWeight(acc, layers, overrideLayer, ov);
                        }
                    }

                    // UC biome repair: finite individual weights can overflow a float sum;
                    // invalid contributions must never enter Unity's native alphamap upload.
                    double sum = 0;
                    for (int L = 0; L < layers; L++)
                    {
                        if (!Finite(acc[L]) || acc[L] < 0f) acc[L] = 0f;
                        sum += acc[L];
                    }

                    if (sum < 1e-6f)
                    {
                        // Nothing voted. Falling through to an all-zero pixel makes
                        // Unity render untextured grey, which reads as a bug rather
                        // than as an unpainted spot.
                        splat[z, x, Mathf.Clamp(defaultLayer, 0, layers - 1)] = 1f;
                        continue;
                    }

                    double inv = 1.0 / sum;
                    for (int L = 0; L < layers; L++) splat[z, x, L] = (float)(acc[L] * inv);
                }
            }

            tile.outputs.splatmap = splat;
            EmitLayerTextures(tile);
        }

        /// <summary>
        /// Hands the applier the textures for any channel this node reserves but the
        /// world has not authored, so TerrainBuilder mints a real layer instead of the
        /// neutral grey stand-in. Snow's own fields win over any hand-added entry that
        /// also targets the snow layer. Emits null when nothing carries content, so a
        /// graph that assigns no textures behaves exactly as before.
        /// </summary>
        private void EmitLayerTextures(TileData tile)
        {
            List<SplatLayerTexture> emit = null;

            // The water-bare channel must mint as the UNTEXTURED terrain default so a bed
            // / shore reads as plain ground (author spec), UNLESS the author wired a real
            // bed albedo into it. The slot is tint-seeded like every ground channel, so a
            // tint-only bare slot is suppressed below to leave the channel neutral.
            // MUST resolve exactly as WriteOutput does. WriteOutput paints the bed's weight
            // at ResolveBareLayer(layers); if this used the raw authored `bareLayer` instead,
            // then whenever the index was re-homed (because the authored one is painted by
            // something else) the suppression below would protect a layer nobody paints while
            // the layer actually carrying the bed got dressed with a texture - and the sea bed
            // would come out coloured again, which is the whole defect being fixed. One
            // resolution, called from both places.
            int bareChannel = ResolveBareLayer(Mathf.Max(1, layerCount));
            bool suppressBareTint = waterBareEnabled && !BareLayerHasAuthoredBed(bareChannel);

            if (layerTextures != null)
            {
                for (int i = 0; i < layerTextures.Count; i++)
                {
                    SplatLayerTexture e = layerTextures[i];
                    // Skip a hand-added snow-layer entry; the Snow fields below own it.
                    if (snowEnabled && e.layer == snowLayer) continue;
                    // Keep the water-bare channel neutral unless an albedo dresses it.
                    if (suppressBareTint && e.layer == bareChannel &&
                        ReferenceEquals(e.albedo, null)) continue;
                    if (!e.HasContent) continue;
                    (emit ?? (emit = new List<SplatLayerTexture>())).Add(e);
                }
            }

            if (snowEnabled)
            {
                SplatLayerTexture snow = new SplatLayerTexture
                {
                    layer = snowLayer,
                    albedo = snowAlbedo,
                    normal = snowNormal,
                    ao = snowAO,
                    tint = snowTint,
                    tileSizeMetres = snowTileSize,
                    smoothness = snowSmoothness,
                    normalScale = 1f
                };
                // Always emit the snow layer while snow is on: even with no albedo the
                // flat snow-white tint must replace the brown/grey native stand-in the
                // user reported. HasContent is true because snowTint is non-black.
                (emit ?? (emit = new List<SplatLayerTexture>())).Add(snow);
            }

            tile.outputs.splatLayerTextures = emit;
        }

        private void ApplyRule(BiomeSplatRule[] ruleByBiome, float[] acc, int layers, int biomeIndex,
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

        /// <summary>
        /// Picks the terrain layer water beds and shore strips are painted onto, and
        /// guarantees it is not a layer something else already paints.
        ///
        /// The bed override hands a pixel weight 1 on this layer and nothing else, so the
        /// result reads as the untextured terrain default ONLY while the layer is genuinely
        /// unused. When the index collides with a painted layer the override still fires but
        /// paints that layer's texture instead - which is exactly how an ocean bed came out
        /// grassy for an author who had left the bed slot empty: the shipped default put the
        /// bare layer at index 0, the same index as <c>defaultLayer</c>.
        ///
        /// So: use the authored index when it is free, otherwise re-home to the highest free
        /// index. Scanning downward keeps the bed off the low indices authors fill first.
        /// Deterministic, allocation-free, and it never invents a texture or a path - it only
        /// chooses which EMPTY slot the bed is handed to.
        /// </summary>
        private int ResolveBareLayer(int layers)
        {
            if (layers <= 1) return 0;

            int authored = Mathf.Clamp(bareLayer, 0, layers - 1);
            if (!IsLayerClaimed(authored, layers)) return authored;

            for (int L = layers - 1; L >= 0; L--)
                if (!IsLayerClaimed(L, layers)) return L;

            // Every layer is painted by something. Nothing here can conjure a free slot, so
            // keep the authored index: the bed then shares a texture, which is visible and
            // therefore reportable, rather than silently landing on an arbitrary layer.
            return authored;
        }

        /// <summary>
        /// True when <paramref name="layer"/> is painted by any other part of this node -
        /// the default/wet/beach/snow/override slots, or any enabled biome rule's ground,
        /// slope or high layer. Used only to keep the water-bare layer clear of them.
        /// </summary>
        private bool IsLayerClaimed(int layer, int layers)
        {
            if (layer < 0 || layer >= layers) return true;

            if (layer == Mathf.Clamp(defaultLayer, 0, layers - 1)) return true;
            if (wetStrength  > 0f && layer == Mathf.Clamp(wetLayer,   0, layers - 1)) return true;
            if (beachEnabled && layer == Mathf.Clamp(beachLayer, 0, layers - 1)) return true;
            if (snowEnabled  && layer == Mathf.Clamp(snowLayer,  0, layers - 1)) return true;

            // The override layer has no enable flag - it is driven by an input mask that may
            // be wired at any time (hand-painted regions, road and path masks), so it counts
            // as claimed unconditionally rather than only when a mask happens to be present.
            if (layer == Mathf.Clamp(overrideLayer, 0, layers - 1)) return true;

            if (rules != null)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    BiomeSplatRule r = rules[i];
                    if (r.weight <= 0f) continue;              // disabled rule paints nothing
                    if (layer == Mathf.Clamp(r.groundLayer, 0, layers - 1)) return true;
                    if (layer == Mathf.Clamp(r.slopeLayer,  0, layers - 1)) return true;
                    if (layer == Mathf.Clamp(r.highLayer,   0, layers - 1)) return true;
                }
            }

            return false;
        }

        private static void AddWeight(float[] acc, int layers, int layer, float amount)
        {
            if (amount <= 0f || !Finite(amount)) return;
            if (layer < 0 || layer >= layers) return;
            acc[layer] = (float)System.Math.Min(float.MaxValue, (double)acc[layer] + amount);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // UC biome repair: normalize a worker-local copy, retaining disabled/reversed blends and saved authoring.
        private BiomeSplatRule ValidatedRule(BiomeSplatRule rule)
        {
            var fallback = BiomeSplatRule.Default(rule.biome, defaultLayer);
            int last = Mathf.Max(1, layerCount) - 1;
            rule.groundLayer = Mathf.Clamp(rule.groundLayer, 0, last);
            rule.slopeLayer = Mathf.Clamp(rule.slopeLayer, 0, last);
            rule.highLayer = Mathf.Clamp(rule.highLayer, 0, last);
            rule.slopeStart = Finite(rule.slopeStart) ? Mathf.Clamp(rule.slopeStart, 0f, 90f) : fallback.slopeStart;
            rule.slopeEnd = Finite(rule.slopeEnd) ? Mathf.Clamp(rule.slopeEnd, 0f, 90f) : fallback.slopeEnd;
            rule.highStart = Finite(rule.highStart) ? Mathf.Clamp01(rule.highStart) : fallback.highStart;
            rule.highEnd = Finite(rule.highEnd) ? Mathf.Clamp01(rule.highEnd) : fallback.highEnd;
            rule.weight = Finite(rule.weight) ? Mathf.Max(0f, rule.weight) : fallback.weight;
            return rule;
        }

        private BiomeSplatRule[] BuildRuleTable()
        {
            int count = BiomeClassifierNode.BiomeCount;

            BiomeSplatRule[] ruleByBiome = new BiomeSplatRule[count];

            for (int i = 0; i < count; i++)
                ruleByBiome[i] = ValidatedRule(BiomeSplatRule.Default((BiomeType)i, defaultLayer));

            // BIO-5: a populated palette (snapshotted on the main thread) overrides the inline
            // rules; an empty/absent one falls back to them, so nothing changes until the buyer
            // fills a palette.
            List<BiomeSplatRule> src = ChooseRules(paletteRules, rules);
            if (src == null) return ruleByBiome;

            for (int i = 0; i < src.Count; i++)
            {
                int index = (int)src[i].biome;
                if (index >= 0 && index < count) ruleByBiome[index] = ValidatedRule(src[i]);
            }
            return ruleByBiome;
        }

        /// <summary>
        /// BIO-5: picks the rule source - a populated palette snapshot when present, else the node's
        /// own inline rules. Pure/self-testable; the "shipped empty palette = node defaults" contract.
        /// </summary>
        internal static List<BiomeSplatRule> ChooseRules(List<BiomeSplatRule> paletteRules,
                                                         List<BiomeSplatRule> inlineRules)
        {
            return (paletteRules != null && paletteRules.Count > 0) ? paletteRules : inlineRules;
        }
    }
}
