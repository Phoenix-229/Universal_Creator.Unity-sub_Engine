// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    PbrNodes.cs
//  Purpose: Texture Graph T12-T14, plus the four surface features the uber-shader
//           already implements and nothing else in the graph could reach:
//           Cliff Surface, Macro Variation, Distance Fade and Colour Grade.
//
//  WHY FOUR NODES THAT ARE NOT IN THE T1-T24 LIST
//  ----------------------------------------------
//  UC_TerrainLit already carries _EnableSlopeBlend, _EnableMacroVariation,
//  _EnableDistanceFade and the _Saturation / _Brightness / _Contrast / _TintColor
//  block. Every one of those costs shader instructions on every terrain pixel
//  whether or not anything switches it on. Shipping them with no node to drive
//  them would mean paying for a feature no user can find - and _EnableSlopeBlend
//  in particular is one of only two real shader keywords in the whole material,
//  so it is the single most expensive thing in there to leave unreachable.
//
//  These are additions to the specified set, not substitutions for it. T1-T24 are
//  all present.
//
//  ONE CONVENTION THROUGHOUT
//  -------------------------
//  Smoothness is authored, roughness is talked about. Artists think in roughness,
//  URP's Lit BRDF wants smoothness, and the conversion is exactly 1 - x. The
//  Roughness node therefore shows roughness in its fields and writes smoothness
//  to the shader, doing the flip in one place so nobody has to remember which
//  end of the slider is shiny.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  T12 - Roughness Map
    // -------------------------------------------------------------------------

    /// <summary>
    /// Remaps every layer's smoothness into a chosen band, and breaks the result
    /// up with a large-scale noise so the ground is not uniformly glossy.
    ///
    /// WHY A REMAP AND NOT A VALUE
    /// ---------------------------
    /// The mask maps already carry per-texel smoothness authored by whoever made
    /// them, and throwing that away for one global number flattens the material.
    /// Remapping keeps the texture's variation and moves the band it occupies -
    /// the same operation URP's own HDRP-style mask workflow uses, and the reason
    /// a wet-looking and a dry-looking version of one terrain can share textures.
    ///
    ///     smoothness = lerp(min, max, mask.a)
    ///
    /// A min above the max inverts the map, which is legal and occasionally what
    /// someone wants, so it is not clamped into order.
    ///
    /// THE VARIATION TERM
    /// ------------------
    /// Real ground is not equally weathered everywhere. A low-frequency noise -
    /// tens of metres per period, well above the texture tiling - shifts the band
    /// slightly from place to place. Without it a large flat area catches the sun
    /// in one uniform sheet, which is the single most reliable tell that a terrain
    /// is procedural.
    /// </summary>
    [System.Serializable]
    public class RoughnessMapNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Roughness Map"; } }
        public override string Tooltip
        {
            get { return "Remaps layer smoothness into a band and varies it over large distances."; }
        }

        [Tooltip("Roughness where the mask map reads 0. 1 is fully matte.")]
        public float roughnessMax = 0.9f;

        [Tooltip("Roughness where the mask map reads 1. 0 is mirror-smooth.")]
        public float roughnessMin = 0.4f;

        [Tooltip("World size of one variation period, in metres. Keep far above the texture tiling.")]
        public float variationScaleMetres = 60f;

        [Tooltip("How far the variation shifts roughness. 0.1 is plenty; 0.3 reads as patchy.")]
        public float variationAmount = 0.12f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            // Authored as roughness, delivered as smoothness. The flip lives here
            // and nowhere else, so the shader never has to know the field names.
            float smoothAtZero = 1f - Mathf.Clamp01(roughnessMax);
            float smoothAtOne = 1f - Mathf.Clamp01(roughnessMin);

            surface.SetFloat(UCShaderProps.SmoothnessRemapMin, smoothAtZero);
            surface.SetFloat(UCShaderProps.SmoothnessRemapMax, smoothAtOne);
            surface.SetFloat(UCShaderProps.RoughnessVariationScale, Mathf.Max(1f, variationScaleMetres));
            surface.SetFloat(UCShaderProps.RoughnessVariationAmount, Mathf.Clamp01(variationAmount));
        }
    }

    // -------------------------------------------------------------------------
    //  T13 - Metallic Map
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scales metallic across the material, with a separate value for the cliff
    /// channel.
    ///
    /// THE PHYSICS, WHICH IS UNUSUALLY BLUNT HERE
    /// ------------------------------------------
    /// Metallic is not a style dial. It selects which of two BRDFs a pixel uses:
    /// at 0 the surface has a coloured diffuse response and a white specular
    /// highlight; at 1 it has no diffuse at all and its specular takes the albedo
    /// colour. There is no material in between - the gradient exists only to blend
    /// across a boundary within one texel.
    ///
    /// Almost nothing on a terrain is metal. Soil, sand, rock, snow, grass, mud:
    /// all dielectric, all 0. The defaults are therefore 0, and a non-zero value
    /// is for exposed ore, iron-bearing rock, or a stylised look chosen on purpose.
    /// Raising it "to make the rock shinier" is the classic mistake, and produces
    /// terrain that goes black in ambient light - the Roughness node is what
    /// shininess actually lives on.
    /// </summary>
    [System.Serializable]
    public class MetallicMapNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Metallic Map"; } }
        public override string Tooltip
        {
            get { return "Metallic scale for the painted layers and the cliff channel. Terrain is almost always 0."; }
        }

        [Tooltip("Multiplies every layer's metallic. 0 for all natural ground.")]
        public float metallicScale;

        [Tooltip("Metallic for the cliff / rock channel only. Raise for ore-bearing stone.")]
        public float rockMetallic;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.SetFloat(UCShaderProps.MetallicScale, Mathf.Clamp01(metallicScale));
            surface.SetFloat(UCShaderProps.SlopeMetallic, Mathf.Clamp01(rockMetallic));

            if (metallicScale > 0.5f)
                surface.Notice("Metallic Scale is above 0.5. Natural ground is dielectric; expect it to read as dark metal.");
        }
    }

    // -------------------------------------------------------------------------
    //  T14 - Emission
    // -------------------------------------------------------------------------

    /// <summary>
    /// Light emitted by the ground itself: lava, bioluminescence, glowing veins.
    ///
    /// Intensity is a linear multiplier on the colour, so above 1 the result is
    /// out of the 0-1 range and only means something with HDR enabled on the
    /// camera and a bloom pass to catch it. That is the intended use - an emissive
    /// terrain without bloom just looks like a flat bright patch.
    ///
    /// The map's RGB is multiplied by the colour rather than replacing it, so one
    /// greyscale crack pattern can drive orange lava on one world and cyan spores
    /// on another.
    /// </summary>
    [System.Serializable]
    public class EmissionNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Emission"; } }
        public override string Tooltip
        {
            get { return "Ground that emits light. Needs HDR and bloom to read as glow rather than as a bright patch."; }
        }

        [Tooltip("Where the ground glows. Greyscale is enough; the colour below tints it.")]
        public Texture2D emissionMap;

        [Tooltip("Emitted colour. Multiplied by the map.")]
        public Color emissionColor = new Color(1f, 0.45f, 0.12f, 1f);

        [Tooltip("Linear multiplier. Above 1 needs an HDR camera and a bloom pass to show.")]
        public float intensity = 1f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.EnableFeature(UCShaderProps.EnableEmission);

            if (emissionMap != null)
                surface.SetTexture(UCShaderProps.EmissionMap, new TextureRef(emissionMap));
            else
                surface.Notice("Emission has no map; the whole terrain emits evenly.");

            surface.SetColor(UCShaderProps.EmissionColor, emissionColor);
            surface.SetFloat(UCShaderProps.EmissionIntensity, Mathf.Max(0f, intensity));
        }
    }

    // -------------------------------------------------------------------------
    //  Cliff Surface - drives the shader's slope-blend channel
    // -------------------------------------------------------------------------

    /// <summary>
    /// The rock that shows through wherever the ground is too steep for anything
    /// to sit on it.
    ///
    /// WHY THIS IS NOT JUST A FIFTH LAYER
    /// ----------------------------------
    /// A splat layer is placed by the control map, which is painted in the height
    /// graph from a top-down projection. Cliffs are exactly where a top-down
    /// projection has the least information - a vertical face occupies almost no
    /// area from above, so there are almost no control-map texels to paint into.
    /// The cliff channel is therefore chosen by the surface normal in the fragment
    /// shader, at full screen resolution, and projected triplanar so it does not
    /// smear. That is a different mechanism from splatting, not a fifth slot in it.
    ///
    /// THE ANGLES
    /// ----------
    /// The two angles are the real repose limits of loose material. Dry sand comes
    /// to rest at 30-35 degrees, soil with roots holds to about 45, and past
    /// roughly 55 nothing unconsolidated stays put at all - which is why bare rock
    /// appears on real slopes at about that angle and why the defaults are 35 and
    /// 60 rather than a round 45.
    ///
    /// COST
    /// ----
    /// This is one of two keyword-gated features. Off, the branch and its six
    /// texture fetches are compiled out entirely.
    /// </summary>
    [System.Serializable]
    public class CliffSurfaceNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Cliff Surface"; } }
        public override string Tooltip
        {
            get { return "Triplanar rock on steep ground, chosen by surface angle rather than by the control map."; }
        }

        [Tooltip("Rock texture, projected from three world axes. Leave empty for flat colour.")]
        public Texture2D rockTexture;

        [Tooltip("Normal map for the rock. Optional.")]
        public Texture2D rockNormal;

        [Tooltip("Multiplied over the rock albedo.")]
        public Color rockColor = new Color(0.52f, 0.50f, 0.47f, 1f);

        [Tooltip("Slope in degrees where rock starts showing. 35 is the repose angle of dry soil.")]
        public float angleStartDegrees = 35f;

        [Tooltip("Slope in degrees where it is fully rock. Past 55-60 nothing loose holds.")]
        public float angleEndDegrees = 60f;

        [Tooltip("Rock gloss. Dry stone is around 0.25; wet or polished rock higher.")]
        public float rockSmoothness = 0.25f;

        [Tooltip("Depth of the rock normal map.")]
        public float normalStrength = 1f;

        [Tooltip("World size of one projected rock tile, in metres.")]
        public float triplanarScaleMetres = 6f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.EnableFeature(UCShaderProps.EnableSlopeBlend, UCShaderProps.KeywordSlopeBlend);

            if (rockTexture != null)
                surface.SetTexture(UCShaderProps.SlopeTex, new TextureRef(rockTexture));
            if (rockNormal != null)
                surface.SetTexture(UCShaderProps.SlopeNormal, new TextureRef(rockNormal));

            surface.SetColor(UCShaderProps.SlopeColor, rockColor);

            // Ordered, not clamped into order silently: an end below the start
            // makes the shader's smoothstep run backwards, so rock would appear on
            // FLAT ground and grass on the cliffs. Swapping and saying so is the
            // only reading of that input which is not a bug report waiting to
            // happen.
            float lo = Mathf.Clamp(Mathf.Min(angleStartDegrees, angleEndDegrees), 0f, 90f);
            float hi = Mathf.Clamp(Mathf.Max(angleStartDegrees, angleEndDegrees), 0f, 90f);
            if (hi - lo < 0.5f) hi = Mathf.Min(90f, lo + 0.5f);

            if (angleEndDegrees < angleStartDegrees)
                surface.Notice("Cliff Surface angles were inverted and have been swapped.");

            surface.SetFloat(UCShaderProps.SlopeAngleStart, lo);
            surface.SetFloat(UCShaderProps.SlopeAngleEnd, hi);
            surface.SetFloat(UCShaderProps.SlopeNormalStrength, Mathf.Clamp(normalStrength, 0f, 4f));
            surface.SetFloat(UCShaderProps.SlopeTriplanarScale, Mathf.Max(0.01f, triplanarScaleMetres));

            // _SlopeSmoothness is the rock's PBR smoothness, not a transition
            // width - the shader already smoothsteps the angle mask itself, so
            // there is no second softness to expose. _SlopeMetallic is left to
            // the Metallic Map node rather than written here, because two nodes
            // writing one key means the answer depends on chain order, which is
            // not something a user should have to reason about.
            surface.SetFloat(UCShaderProps.SlopeSmoothness, Mathf.Clamp01(rockSmoothness));
        }
    }

    // -------------------------------------------------------------------------
    //  Macro Variation - kills the tiling lattice at distance
    // -------------------------------------------------------------------------

    /// <summary>
    /// A very low-frequency colour drift over the whole terrain.
    ///
    /// Detail Texture (T7) breaks up tiling within a few metres. This breaks it up
    /// across hundreds. They are the same problem at two scales and both are
    /// needed: at 300 m the 8 m texture tile is sub-pixel and invisible, but the
    /// uniform average colour of a whole hillside is not, and a hillside that is
    /// one flat colour reads as painted cardboard.
    ///
    /// Costs no texture fetch - it is procedural value noise on world XZ evaluated
    /// in the fragment shader, which is a handful of ALU. That is why it is a
    /// uniform-branch feature and not a keyword.
    /// </summary>
    [System.Serializable]
    public class MacroVariationNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Macro Variation"; } }
        public override string Tooltip
        {
            get { return "Large-scale colour drift so distant hillsides are not one flat colour."; }
        }

        [Tooltip("World size of one variation period, in metres. 150-400 suits a landscape.")]
        public float scaleMetres = 220f;

        [Tooltip("How far the colour drifts. 0.15 is visible; 0.4 reads as blotchy.")]
        public float amount = 0.15f;

        [Tooltip("Colour the variation drifts toward. A desaturated warm or cool cast, not a hue.")]
        public Color variationColor = new Color(0.85f, 0.82f, 0.72f, 1f);

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.EnableFeature(UCShaderProps.EnableMacroVariation);
            surface.SetFloat(UCShaderProps.MacroVariationScale, Mathf.Max(1f, scaleMetres));
            surface.SetFloat(UCShaderProps.MacroVariationAmount, Mathf.Clamp01(amount));
            surface.SetColor(UCShaderProps.MacroVariationColor, variationColor);
        }
    }

    // -------------------------------------------------------------------------
    //  Distance Fade - what the terrain becomes far away
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fades the expensive per-pixel work out past a distance, leaving the plain
    /// splat blend.
    ///
    /// This is a performance control with a visual justification, which is rare
    /// enough to say out loud. Past a few hundred metres a terrain pixel covers
    /// several square metres of ground; height blending, cliff triplanar and
    /// detail overlay are all computing information finer than that pixel can
    /// hold, so switching them off costs nothing visible and returns the fetches.
    /// Keeping them on does not make the distance look better - it makes it
    /// shimmer, because sub-pixel detail aliases.
    ///
    /// Start and End are metres from the camera, and End must exceed Start or the
    /// fade is a step.
    /// </summary>
    [System.Serializable]
    public class DistanceFadeNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Distance Fade"; } }
        public override string Tooltip
        {
            get { return "Drops the per-pixel features past a distance, where they alias rather than help."; }
        }

        [Tooltip("Metres from camera where the expensive features start fading out.")]
        public float fadeStartMetres = 120f;

        [Tooltip("Metres from camera where only the plain splat blend remains.")]
        public float fadeEndMetres = 400f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.EnableFeature(UCShaderProps.EnableDistanceFade);

            float start = Mathf.Max(0f, fadeStartMetres);
            float end = Mathf.Max(start + 1f, fadeEndMetres);

            surface.SetFloat(UCShaderProps.DistanceFadeStart, start);
            surface.SetFloat(UCShaderProps.DistanceFadeEnd, end);
        }
    }

    // -------------------------------------------------------------------------
    //  Colour Grade - the last word before the material is written
    // -------------------------------------------------------------------------

    /// <summary>
    /// Saturation, brightness, contrast and an overall tint, applied to the final
    /// albedo.
    ///
    /// WHY GRADE HERE RATHER THAN IN POST
    /// ----------------------------------
    /// Post-processing grades the whole frame - sky, characters, UI overlays and
    /// terrain together. This grades the terrain alone, which is what you want
    /// when the ground reads too green against art-directed foliage, or when one
    /// biome's material set needs to sit beside another's without re-authoring
    /// every texture.
    ///
    /// Saturation uses the Rec. 709 luma weights (0.2126, 0.7152, 0.0722) rather
    /// than a flat third each, because the eye is roughly seven times more
    /// sensitive to green than to blue. Desaturating with equal weights visibly
    /// darkens greens and lightens blues, which is why hand-rolled greyscale so
    /// often looks wrong.
    ///
    /// Contrast pivots at 0.5 in linear space. Pivoting at 0 would scale toward
    /// black and change exposure rather than contrast.
    /// </summary>
    [System.Serializable]
    public class ColorGradeNode : SurfaceFeatureNode
    {
        // UC-T07-03: reuse the real shader-grading owner for cheap scalar parameters; preserve original Surface port index zero.
        private static readonly Port[] ParameterInputs =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input),
            new Port("Brightness", PortType.Scalar, PortDirection.Input),
            new Port("Contrast", PortType.Scalar, PortDirection.Input),
            new Port("Saturation", PortType.Scalar, PortDirection.Input)
        };
        public override Port[] GetInputs() => ParameterInputs;
        public override string NodeName { get { return "Colour Grade"; } }
        public override string Tooltip
        {
            get { return "Saturation, brightness, contrast and tint on the terrain alone, not the whole frame."; }
        }

        [Tooltip("0 is greyscale by Rec. 709 luma, 1 is untouched, above 1 oversaturates.")]
        public float saturation = 1f;

        [Tooltip("Linear multiplier on the final albedo. 1 is untouched.")]
        public float brightness = 1f;

        [Tooltip("Contrast about a 0.5 pivot. 1 is untouched.")]
        public float contrast = 1f;

        [Tooltip("Multiplied over everything last. White is untouched.")]
        public Color tint = Color.white;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            float s = GetInputScalarOr(tile, 3, stop, saturation), b = GetInputScalarOr(tile, 1, stop, brightness),
                c = GetInputScalarOr(tile, 2, stop, contrast);
            TextureImageSampling.RequireFinite(s); TextureImageSampling.RequireFinite(b); TextureImageSampling.RequireFinite(c);
            surface.SetFloat(UCShaderProps.Saturation, Mathf.Max(0f, s));
            surface.SetFloat(UCShaderProps.Brightness, Mathf.Max(0f, b));
            surface.SetFloat(UCShaderProps.Contrast, Mathf.Max(0f, c));
            surface.SetColor(UCShaderProps.TintColor, tint);
        }
    }
}
