// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TextureOutputNodes.cs
//  Purpose: Texture Graph T22-T24. Where the chains converge and the material is
//           actually written.
//
//  THE SHAPE OF THE END OF THE GRAPH
//  ---------------------------------
//    T22  Vegetation Colour  - a season-blended tint, on the surface chain
//    T23  Terrain Blend Final - joins Surface + Splat + Water, applies budgets
//    T24  Shader Output       - the IOutputNode terminal; writes the material
//
//  T23 is where three currencies meet, and it is a separate node from T24 for a
//  reason worth stating: budget decisions (how many layers, what quality tier)
//  are authoring decisions that a user changes often while iterating, and output
//  decisions (which material asset, where it lands) are ones they set once. Two
//  nodes keeps the thing you touch weekly away from the thing you touch once.
//
//  Only T24 implements IOutputNode, so it is the single node GraphEvaluator pulls
//  from. Everything upstream is reached by the pull, which is why a texture graph
//  with no Shader Output evaluates to nothing at all rather than to a partial
//  material - the same contract the height graph has.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  T22 - Vegetation Colour
    // -------------------------------------------------------------------------

    /// <summary>
    /// A ground-cover tint that moves through the four seasons.
    ///
    /// WHERE IT LANDS
    /// --------------
    /// Not on the terrain shader. It lands on DetailPrototype.healthyColor and
    /// dryColor, which is what Unity's grass and detail renderer actually reads,
    /// and that is why it is a typed field on SurfaceProduct rather than a
    /// dictionary key - the self-test validates every key against the shader's
    /// property list, and a key that is deliberately not a shader property would
    /// fail a test that was working correctly.
    ///
    /// THE SEASON CURVE
    /// ----------------
    /// Season is one scalar in 0..1 covering a full year: 0 is the spring
    /// equinox, 0.25 midsummer, 0.5 the autumn equinox, 0.75 midwinter. The four
    /// colours are the values AT those points, and the blend between them is a
    /// cosine interpolation rather than a linear one:
    ///
    ///     w = (1 - cos(pi * t)) / 2
    ///
    /// because a linear blend has a discontinuous derivative at each keyframe, and
    /// a year that visibly jerks four times is worse than one that does not change
    /// at all. The cosine form has zero slope at every key, so the transitions are
    /// smooth through the equinoxes.
    ///
    /// Transition Speed multiplies how fast the season scalar advances relative to
    /// real time, for anything that drives this from a clock rather than by hand.
    /// </summary>
    [System.Serializable]
    public class VegetationColorNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Vegetation Colour"; } }
        public override string Tooltip
        {
            get { return "Season-blended tint for grass and ground cover. Cosine-interpolated so the year does not jerk."; }
        }

        [Tooltip("New growth. Yellow-green and bright; spring foliage has little chlorophyll depth yet.")]
        public Color springColor = new Color(0.45f, 0.62f, 0.24f, 1f);

        [Tooltip("Full growth. The deepest, most saturated green of the year.")]
        public Color summerColor = new Color(0.28f, 0.49f, 0.18f, 1f);

        [Tooltip("Senescence. Chlorophyll breaks down first, revealing the carotenoids underneath.")]
        public Color autumnColor = new Color(0.62f, 0.47f, 0.19f, 1f);

        [Tooltip("Dormancy. Desaturated straw, not brown - dead grass is pale, not dark.")]
        public Color winterColor = new Color(0.52f, 0.48f, 0.38f, 1f);

        [Tooltip("Where in the year, 0 to 1. 0 spring, 0.25 summer, 0.5 autumn, 0.75 winter.")]
        public float season;

        [Tooltip("Multiplier on how fast the season advances for anything driving this from a clock.")]
        public float transitionSpeed = 1f;

        [Tooltip("Dry-season variant, used by DetailPrototype.dryColor. Blended by the same curve.")]
        public float dryDesaturation = 0.4f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            Color tint = EvaluateSeason(Mathf.Repeat(season, 1f));

            surface.vegetationTint = tint;
            surface.vegetationTintSet = true;

            // The dry variant is the same hue with the life taken out of it, not a
            // second hand-picked colour. Deriving it means the two can never drift
            // apart when someone retunes the season palette.
            surface.vegetationDryTint = Desaturate(tint, Mathf.Clamp01(dryDesaturation));
        }

        /// <summary>
        /// Cosine blend between the two bracketing season colours.
        /// </summary>
        public Color EvaluateSeason(float t)
        {
            Color[] keys = { springColor, summerColor, autumnColor, winterColor };

            float scaled = t * 4f;
            int from = Mathf.FloorToInt(scaled) % 4;
            int to = (from + 1) % 4;
            float k = scaled - Mathf.Floor(scaled);

            // Zero slope at both ends, so the year is smooth through every key.
            float w = (1f - Mathf.Cos(Mathf.PI * k)) * 0.5f;

            return Color.Lerp(keys[from], keys[to], w);
        }

        /// <summary>
        /// Rec. 709 luma desaturation. Equal channel weights would darken the
        /// greens and lighten the blues, because the eye is about seven times
        /// more sensitive to green than to blue.
        /// </summary>
        private static Color Desaturate(Color c, float amount)
        {
            float luma = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            return new Color(
                InterpolationMath.Lerp(c.r, luma, amount),
                InterpolationMath.Lerp(c.g, luma, amount),
                InterpolationMath.Lerp(c.b, luma, amount),
                c.a);
        }

        /// <summary>Season scalar advanced by dt seconds at the configured speed.</summary>
        public float AdvanceSeason(float current, float deltaSeconds, float secondsPerYear)
        {
            if (secondsPerYear < 1e-4f) return current;
            return Mathf.Repeat(current + deltaSeconds * transitionSpeed / secondsPerYear, 1f);
        }
    }

    // -------------------------------------------------------------------------
    //  T23 - Terrain Blend Final
    // -------------------------------------------------------------------------

    public enum TerrainQualityLevel
    {
        /// <summary>Mobile. Two layers, no height blend, no cliff triplanar, no detail.</summary>
        Low = 0,
        /// <summary>Console and mid PC. Four layers, height blend, cliff, no detail overlay.</summary>
        Medium = 1,
        /// <summary>Desktop. Everything the graph asked for.</summary>
        High = 2
    }

    /// <summary>
    /// Joins the surface chain, the splat set and the water chain into one
    /// description, and applies the platform budget.
    ///
    /// WHY THE BUDGET IS ENFORCED HERE AND NOT IN THE SHADER
    /// -----------------------------------------------------
    /// A shader can branch on quality, but every branch it carries is a variant it
    /// must compile and ship. Deciding on the CPU which features the material asks
    /// for means the low tier simply never sets the keyword, so the variant is
    /// never compiled and never loaded. On mobile that is the difference between
    /// a shader that warms up in a second and one that hitches on first sight of
    /// terrain.
    ///
    /// WHAT EACH TIER ACTUALLY DROPS
    /// -----------------------------
    ///   Low     - clears cliff blend, snow, detail overlay, mud and macro
    ///             variation; caps layers at 2; forces distance fade on
    ///   Medium  - clears the detail overlay and macro variation; caps at 4
    ///   High    - changes nothing
    ///
    /// The choices are by cost, not by taste. Cliff blend and snow are the two
    /// keyword features at six texture fetches each. Detail and macro variation
    /// are cheap per pixel but pointless on a screen where the terrain is already
    /// under-sampled. Everything dropped is reported through the notices, because
    /// a user who set snow and got none deserves to know a budget removed it
    /// rather than that their node is broken.
    /// </summary>
    [System.Serializable]
    public class TerrainBlendFinalNode : TextureNode
    {
        public override string NodeName { get { return "Terrain Blend Final"; } }
        public override NodeCategory Category { get { return NodeCategory.Surface; } }
        public override string Tooltip
        {
            get { return "Joins surface, splat and water, and applies the platform layer and quality budget."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input),
            new Port("Splat",   PortType.Splat,   PortDirection.Input),
            new Port("Water",   PortType.Water,   PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.SurfaceOut; }

        [Tooltip("How many terrain layers to keep. The shader's hard ceiling is 4.")]
        public int maxLayers = 4;

        [Tooltip("Platform tier. Low and Medium drop features by cost, and say which in the console.")]
        public TerrainQualityLevel qualityLevel = TerrainQualityLevel.High;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            SurfaceProduct upstream = GetInputSurface(tile, 0, stop);
            SurfaceProduct surface = upstream != null ? upstream.Copy() : new SurfaceProduct();

            SplatProduct splat = GetInputSplat(tile, 1, stop);
            WaterProduct water = GetInputWater(tile, 2, stop);

            int cap = Mathf.Clamp(maxLayers, 1, 4);
            if (maxLayers > 4)
                surface.Notice("Max Layers above 4 is not possible: the control map has four channels.");

            surface.qualityLevel = (int)qualityLevel;
            surface.water = water;

            ApplyQuality(surface, ref cap);

            surface.maxLayers = cap;

            if (splat != null)
            {
                SplatProduct trimmed = new SplatProduct();
                trimmed.heightBlend = splat.heightBlend;
                trimmed.heightTransition = splat.heightTransition;

                int dropped = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (splat.layers[i] == null) continue;
                    if (i < cap) trimmed.layers[i] = splat.layers[i];
                    else dropped++;
                }

                if (dropped > 0)
                    surface.Notice("Max Layers dropped " + dropped + " layer(s) from the splat set.");

                if (qualityLevel == TerrainQualityLevel.Low && trimmed.heightBlend)
                {
                    trimmed.heightBlend = false;
                    surface.Notice("Low quality: height blending disabled.");
                }

                surface.splat = trimmed;
                surface.SetFloat(UCShaderProps.EnableHeightBlend, trimmed.heightBlend ? 1f : 0f);
                surface.SetFloat(UCShaderProps.HeightTransition, trimmed.heightTransition);
            }
            else
            {
                surface.Notice("Terrain Blend Final has no Splat input; the terrain keeps its existing layers.");
            }

            return surface;
        }

        /// <summary>
        /// Strips what the tier cannot afford. Clearing the enable float AND the
        /// keyword, because leaving the keyword set would still compile and load
        /// the expensive variant even though the branch inside it never runs -
        /// which is all of the cost and none of the effect.
        /// </summary>
        private void ApplyQuality(SurfaceProduct surface, ref int cap)
        {
            if (qualityLevel == TerrainQualityLevel.High) return;

            bool low = qualityLevel == TerrainQualityLevel.Low;

            if (low)
            {
                if (cap > 2) cap = 2;

                Disable(surface, UCShaderProps.EnableSlopeBlend, UCShaderProps.KeywordSlopeBlend,
                        "Low quality: cliff blend disabled.");
                Disable(surface, UCShaderProps.EnableSnow, UCShaderProps.KeywordSnow,
                        "Low quality: snow disabled.");
                Disable(surface, UCShaderProps.EnableMud, null,
                        "Low quality: mud disabled.");

                // Forced on rather than merely allowed: the whole point of the low
                // tier is that distant terrain stops paying for per-pixel work.
                surface.SetFloat(UCShaderProps.EnableDistanceFade, 1f);
                if (!surface.floats.ContainsKey(UCShaderProps.DistanceFadeStart))
                {
                    surface.SetFloat(UCShaderProps.DistanceFadeStart, 60f);
                    surface.SetFloat(UCShaderProps.DistanceFadeEnd, 180f);
                }
            }

            Disable(surface, UCShaderProps.EnableDetail, null,
                    low ? "Low quality: detail overlay disabled."
                        : "Medium quality: detail overlay disabled.");

            Disable(surface, UCShaderProps.EnableMacroVariation, null,
                    low ? "Low quality: macro variation disabled."
                        : "Medium quality: macro variation disabled.");
        }

        private static void Disable(SurfaceProduct surface, string enableProperty,
                                    string keyword, string message)
        {
            float existing;
            bool wasOn = surface.floats.TryGetValue(enableProperty, out existing) && existing > 0.5f;

            surface.SetFloat(enableProperty, 0f);
            if (!string.IsNullOrEmpty(keyword)) surface.keywords.Remove(keyword);

            if (wasOn) surface.Notice(message);
        }
    }

    // -------------------------------------------------------------------------
    //  T24 - Shader Output
    // -------------------------------------------------------------------------

    public enum ShaderTarget
    {
        /// <summary>UniverseCreator/TerrainLit. The uber-shader this graph was built for.</summary>
        UniverseCreatorTerrainLit = 0,

        /// <summary>
        /// Whatever material is already on the terrain. Sets only the properties
        /// that material happens to have, and reports the ones it does not.
        /// </summary>
        ExistingMaterial = 1
    }

    /// <summary>
    /// The terminal node. Collects the finished surface description and stores it
    /// in the tile's output bag for the applier to write onto a real Material and
    /// a real TerrainLayer array.
    ///
    /// WHY THIS NODE DOES NOT TOUCH UNITY OBJECTS ITSELF
    /// -------------------------------------------------
    /// It could - the texture graph runs on the main thread, so calling
    /// Material.SetFloat here would be legal. It does not, for two reasons.
    ///
    /// First, evaluation can be cancelled. A node that has already written half a
    /// material when a stop token trips leaves the terrain in a state that is
    /// neither the old material nor the new one, and the user's next action is to
    /// wonder why the ground is half snow.
    ///
    /// Second, a graph can be evaluated for a preview - a thumbnail, a self-test,
    /// a validation pass - and none of those should mutate a project asset. Making
    /// the node produce a description and the applier perform the write means
    /// preview and apply share every line of logic except the one that commits.
    ///
    /// So: this node's whole job is to hand a finished SurfaceProduct to
    /// OutputBag, and UCTextureGraphApplier decides when that becomes an asset.
    /// </summary>
    [System.Serializable]
    public class ShaderOutputNode : SurfaceOutputNode
    {
        public override string NodeName { get { return "Shader Output"; } }
        public override string Tooltip
        {
            get { return "The end of the texture graph. Hands the finished material description to the applier."; }
        }

        [Tooltip("Which shader the material targets. TerrainLit is the one this graph was built for.")]
        public ShaderTarget target = ShaderTarget.UniverseCreatorTerrainLit;

        [Tooltip("Name for the generated material asset.")]
        public string materialName = "UC_TerrainMaterial";

        [Tooltip("Also write the generated TerrainLayer array onto the terrain. Off leaves layers alone.")]
        public bool writeTerrainLayers = true;

        [Tooltip("Folder under Assets for generated materials, layers and baked maps.")]
        public string outputFolder = DefaultOutputFolder;

        protected override void WriteSurface(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            tile.outputs.surface = surface;
            tile.outputs.surfaceShaderTarget = (int)target;
            tile.outputs.surfaceMaterialName = string.IsNullOrEmpty(materialName)
                ? "UC_TerrainMaterial" : materialName;
            tile.outputs.surfaceWriteLayers = writeTerrainLayers;
            tile.outputs.surfaceOutputFolder = string.IsNullOrEmpty(outputFolder)
                ? DefaultOutputFolder : SanitiseFolder(outputFolder);
        }

        /// <summary>
        /// Where generated assets land when the field is left empty.
        ///
        /// A project-relative constant, never a machine path. This asset is sold and
        /// will be opened on machines whose project lives nowhere near this one, so
        /// anything anchored outside Assets/ is a defect that only shows up on
        /// someone else's computer.
        /// </summary>
        public const string DefaultOutputFolder = "Assets/UniverseCreator/Generated";

        /// <summary>
        /// Forces the folder to be inside Assets and forward-slashed.
        ///
        /// Unity's AssetDatabase only speaks project-relative paths with forward
        /// slashes; a backslash from a Windows user pasting a path, or an absolute
        /// drive path, produces a create that fails with a message about the folder
        /// not existing rather than about the path being wrong.
        /// </summary>
        public static string SanitiseFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return DefaultOutputFolder;

            string s = folder.Replace('\\', '/').Trim();
            while (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);

            if (s.Length == 0) return DefaultOutputFolder;
            if (s != "Assets" && !s.StartsWith("Assets/")) return DefaultOutputFolder;

            return s;
        }
    }
}
