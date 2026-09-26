// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TexturingNodes.cs
//  Purpose: Texture Graph T1-T7. The layer-building half of the graph: what the
//           four painted terrain layers are made of, and how they meet.
//
//  READING ORDER
//  -------------
//  T1 makes a layer. T2 gives it a normal map. T4 asks for triplanar on steep
//  ground. T6 collects four of them into a splat set. T3 decides how they meet.
//  T5 and T7 are surface features that colour and detail whatever T6 produced.
//
//  Every tiling value in this file is a world size in METRES, never a repeat
//  count. A repeat count silently rescales every material when the world size
//  changes; a metre value survives it, which is the whole point of authoring
//  against a world rather than against a mesh.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  T1 - Albedo Layer
    // -------------------------------------------------------------------------

    /// <summary>
    /// One terrain material layer: base colour, tint, tiling, and the PBR
    /// constants that go with it. This is the atom of the texture graph - every
    /// painted surface on the terrain starts as one of these.
    /// </summary>
    [System.Serializable]
    public class AlbedoLayerNode : LayerNode
    {
        public override string NodeName { get { return "Albedo Layer"; } }
        public override string Tooltip
        {
            get { return "One terrain layer: albedo, tint, tiling in metres, and its PBR constants."; }
        }

        // Both inputs are optional overrides for the inline fields below. Having
        // them as ports as well as fields is what makes the utility nodes worth
        // having: Texture Sampler can set the import intent once and feed six
        // layers, and UV Scaler can retile a whole material from one place.
        private static readonly Port[] Ins =
        {
            new Port("Texture", PortType.Texture, PortDirection.Input),
            new Port("UV",      PortType.UV,      PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }

        [Tooltip("Name shown on the generated TerrainLayer asset.")]
        public string layerName = "Layer";

        [Tooltip("Albedo / Base Color texture. Leave empty for a flat tinted layer.")]
        public Texture2D albedoTexture;

        [Tooltip("Multiplied over the albedo. White leaves the texture untouched.")]
        public Color tint = Color.white;

        [Tooltip("World size of one texture tile, in metres. 8 means the texture repeats every 8 m.")]
        public Vector2 tileSizeMetres = new Vector2(8f, 8f);

        [Tooltip("Shifts the tiling origin, in metres. Use to stop two layers lining up.")]
        public Vector2 offsetMetres = Vector2.zero;

        [Tooltip("Rotates the tiling, in degrees. Breaks the grid look on large flat ground.")]
        public float rotationDegrees;

        [Tooltip("0 for everything except bare metal ore. Terrain is almost never metallic.")]
        public float metallic;

        [Tooltip("Smoothness (A) = 1 - roughness. 0 is chalk, 1 is glass. Dry soil sits near 0.2, wet rock near 0.6.")]
        public float smoothness = 0.35f;

        [Tooltip("Raises this layer in the height blend so it reads through its neighbours.")]
        public float heightOffset;

        protected override LayerProduct BuildLayer(TileData tile, StopToken stop)
        {
            LayerProduct layer = new LayerProduct();
            layer.name = string.IsNullOrEmpty(layerName) ? "Layer" : layerName;

            // A wired Texture wins over the inline slot. The wire is the more
            // deliberate act of the two, and the inline slot stays visible so the
            // user can see what they overrode rather than wondering where it went.
            TextureRef wired = GetInputTexture(tile, 0, stop);
            if (wired != null && wired.texture != null) layer.albedo = wired;
            else if (albedoTexture != null) layer.albedo = new TextureRef(albedoTexture);

            layer.tint = tint;
            layer.metallic = Mathf.Clamp01(metallic);
            layer.smoothness = Mathf.Clamp01(smoothness);
            layer.heightOffset = heightOffset;

            UVTransform wiredUV = GetInputUV(tile, 1, stop);
            layer.uv = wiredUV != null
                ? wiredUV.Copy()
                : new UVTransform
                {
                    tileSizeMetres = tileSizeMetres,
                    offset = offsetMetres,
                    rotationDegrees = rotationDegrees
                };

            return layer;
        }
    }

    // -------------------------------------------------------------------------
    //  T2 - Normal Map Layer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attaches a normal map and a mask map to an existing layer.
    ///
    /// Separate from T1 rather than folded into it because normal and mask maps
    /// are the two slots a user most often leaves empty, and a node that is
    /// half-empty on every use teaches people to ignore its fields.
    ///
    /// The strength value here reaches the shader as _NormalScaleN, which URP
    /// applies inside UnpackNormalScale - so it scales the tangent-space XY
    /// before renormalising, rather than lerping toward flat afterwards. Past
    /// about 2 the result stops being a plausible surface, which is why the
    /// inspector range stops there.
    /// </summary>
    [System.Serializable]
    public class NormalMapLayerNode : TextureNode
    {
        public override string NodeName { get { return "Normal Map Layer"; } }
        public override string Tooltip
        {
            get { return "Adds a normal map and mask map (R metallic, G occlusion, B height, A smoothness) to a layer."; }
        }

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.LayerOut; }

        private static readonly Port[] Ins =
        {
            new Port("Layer",  PortType.Layer,   PortDirection.Input),
            new Port("Normal", PortType.Texture, PortDirection.Input),
            new Port("Mask",   PortType.Texture, PortDirection.Input)
        };

        [Tooltip("Tangent-space normal map. Must be imported as Normal Map or it will read as colour.")]
        public Texture2D normalTexture;

        [Tooltip("Depth of the normal map. 1 is as authored.")]
        public float strength = 1f;

        [Tooltip("Pre-packed mask map (R metallic, G occlusion, B height, A smoothness). " +
                 "Optional - if you have the four channels as separate maps, leave this empty and " +
                 "drop them into the Metallic / AO / Height / Roughness slots below instead.")]
        public Texture2D maskTexture;

        // Individual PBR channel slots. A downloaded set ships these as four separate
        // greyscale PNGs; wiring them here packs them into the mask URP wants, using
        // the SAME packer as the Mask Map Packer node. Named with their channel and
        // abbreviation so there is no guessing what each slot is for.
        [Tooltip("Metallic map -> packed into mask R. Terrain is almost never metallic; leave empty for ground.")]
        public Texture2D metallicMap;

        [Tooltip("Ambient Occlusion (AO) map -> packed into mask G. Bright = fully lit, dark = shadowed crevices.")]
        public Texture2D ambientOcclusionMap;

        [Tooltip("Height / Displacement map -> packed into mask B. Drives Height Blend so tall detail punches through.")]
        public Texture2D heightDisplacementMap;

        [Tooltip("Roughness map -> inverted into mask A as smoothness (smoothness = 1 - roughness).")]
        public Texture2D roughnessMap;

        [Tooltip("Tick if the map in the Roughness slot is ALREADY smoothness, not roughness. Then A is used as-is.")]
        public bool roughnessIsSmoothness;

        [Tooltip("Packed mask resolution in pixels, rounded to a power of two. Used only when packing the slots above.")]
        public int maskResolution = 1024;

        [Tooltip("Override the layer tiling. Leave at zero to inherit the layer's own.")]
        public Vector2 tileSizeMetresOverride = Vector2.zero;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            LayerProduct upstream = GetInputLayer(tile, 0, stop);

            // An unconnected Layer input still produces a layer rather than null.
            // A node that vanishes when half-wired is indistinguishable from a
            // node that is broken.
            LayerProduct layer = upstream != null ? upstream.Copy() : new LayerProduct();

            TextureRef wiredNormal = GetInputTexture(tile, 1, stop);
            if (wiredNormal != null && wiredNormal.texture != null) layer.normal = wiredNormal;
            else if (normalTexture != null) layer.normal = new TextureRef(normalTexture);

            TextureRef wiredMask = GetInputTexture(tile, 2, stop);
            if (wiredMask != null && wiredMask.texture != null)
            {
                layer.mask = wiredMask;
            }
            else if (maskTexture != null)
            {
                layer.mask = new TextureRef(maskTexture);
            }
            else
            {
                // No pre-packed mask wired or dropped in: pack whatever individual
                // PBR channels the user supplied, through the SAME packer the Mask
                // Map Packer node uses, so channel order and the 1 - roughness
                // conversion can never diverge between the two paths. Returns null
                // when no channel is set, leaving the layer's own mask untouched.
                TextureRef packed = MaskMapPackerNode.PackMask(
                    metallicMap, ambientOcclusionMap, heightDisplacementMap, roughnessMap,
                    0f, 1f, 0.5f, 0.5f,
                    roughnessIsSmoothness, maskResolution, stop);
                if (packed != null) layer.mask = packed;
            }

            layer.normalStrength = Mathf.Clamp(strength, 0f, 4f);

            if (tileSizeMetresOverride.sqrMagnitude > 1e-6f)
            {
                if (layer.uv == null) layer.uv = new UVTransform();
                layer.uv.tileSizeMetres = tileSizeMetresOverride;
            }

            return layer;
        }
    }

    // -------------------------------------------------------------------------
    //  T3 - Height Blend
    // -------------------------------------------------------------------------

    /// <summary>
    /// Turns the boundary between layers from a cross-fade into a real material
    /// transition.
    ///
    /// WHAT THE SHADER ACTUALLY DOES
    /// -----------------------------
    /// A plain splat blend takes a weighted average of two albedos, which at a
    /// 50/50 pixel gives a colour that belongs to neither material - gravel over
    /// sand looks like beige mud along every edge. Height blending instead reads
    /// each layer's mask B channel as a physical height, adds the layer's own
    /// offset, and lets the taller one win within a transition band:
    ///
    ///     h_i     = mask_i.b + offset_i
    ///     peak    = max(h_i * w_i)
    ///     w'_i    = max(0, h_i * w_i - (peak - transition))
    ///
    /// The result is gravel stones sitting IN the sand with sand visible between
    /// them, because the tall parts of the gravel punch through while the low
    /// parts do not.
    ///
    /// A transition of 0 is a hard cut - correct for tiled stone against mortar,
    /// wrong for sand against soil. The default of 0.2 suits natural ground.
    /// </summary>
    [System.Serializable]
    public class HeightBlendNode : TextureNode
    {
        public override string NodeName { get { return "Height Blend"; } }
        public override string Tooltip
        {
            get { return "Layers meet by height rather than cross-fading. Needs mask maps with a height channel."; }
        }

        private static readonly Port[] SplatIn =
        {
            new Port("Splat", PortType.Splat, PortDirection.Input)
        };

        public override Port[] GetInputs() { return SplatIn; }
        public override Port[] GetOutputs() { return TexturePorts.SplatOut; }

        [Tooltip("Width of the band where two layers compete. 0 is a hard cut, 1 a soft fade.")]
        public float blendSharpness = 0.2f;

        [Tooltip("Height bias per layer, in mask-map units. Positive lifts the layer over its neighbours.")]
        public float heightOffset0;
        public float heightOffset1;
        public float heightOffset2;
        public float heightOffset3;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            SplatProduct upstream = GetInputSplat(tile, 0, stop);
            SplatProduct splat = new SplatProduct();

            float[] offsets = { heightOffset0, heightOffset1, heightOffset2, heightOffset3 };

            if (upstream != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (upstream.layers[i] == null) continue;

                    LayerProduct copy = upstream.layers[i].Copy();
                    copy.heightOffset += offsets[i];
                    splat.layers[i] = copy;
                }
            }

            splat.heightBlend = true;

            // Clamped, not just assigned. A negative transition makes the max()
            // in the shader select nothing and the terrain goes black - a failure
            // whose cause is nowhere near where it shows.
            splat.heightTransition = Mathf.Clamp(blendSharpness, 0f, 1f);

            return splat;
        }
    }

    // -------------------------------------------------------------------------
    //  T4 - Triplanar Mapping
    // -------------------------------------------------------------------------

    /// <summary>
    /// Marks a layer to be projected from three world axes instead of from the
    /// terrain's top-down UV.
    ///
    /// WHY IT IS NEEDED
    /// ----------------
    /// Terrain UVs are a flat top-down projection. On a 70-degree cliff, one
    /// texel of that projection covers roughly 1/cos(70) - about three times -
    /// more surface than on flat ground, so the texture smears into vertical
    /// streaks. Triplanar samples the same texture three times, along X, Y and Z,
    /// and weights each by how much the surface faces that axis:
    ///
    ///     w = |n|^sharpness, normalised so w.x + w.y + w.z = 1
    ///
    /// The exponent controls how quickly one projection takes over from another.
    /// Below about 2 the three samples visibly ghost over each other; above 8 the
    /// switch becomes a hard seam along the 45-degree line. 4 is the usual
    /// compromise and is what the shader uses.
    ///
    /// COST, STATED PLAINLY
    /// --------------------
    /// Three samples instead of one, per map. The uber-shader therefore carries a
    /// single triplanar cliff channel rather than four, and this node's request is
    /// honoured for the first layer that asks. The rest are reported through the
    /// surface's notices rather than silently dropped.
    /// </summary>
    [System.Serializable]
    public class TriplanarMappingNode : TextureNode
    {
        public override string NodeName { get { return "Triplanar Mapping"; } }
        public override string Tooltip
        {
            get { return "Projects a layer from three world axes so steep ground stops smearing."; }
        }

        public override Port[] GetInputs() { return TexturePorts.LayerIn; }
        public override Port[] GetOutputs() { return TexturePorts.LayerOut; }

        [Tooltip("World size of one projected tile, in metres.")]
        public float projectionScaleMetres = 6f;

        [Tooltip("How sharply one axis takes over from another. 4 is the usual value.")]
        public float sharpness = 4f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            LayerProduct upstream = GetInputLayer(tile, 0, stop);
            LayerProduct layer = upstream != null ? upstream.Copy() : new LayerProduct();

            layer.triplanarOnSlopes = true;

            // Triplanar reads world position directly, so the layer's own UV
            // tiling no longer applies to it. Recording the projection scale in
            // the UV keeps one number in one place for the applier to find.
            if (layer.uv == null) layer.uv = new UVTransform();
            float s = Mathf.Max(0.01f, projectionScaleMetres);
            layer.uv.tileSizeMetres = new Vector2(s, s);

            return layer;
        }

        /// <summary>Exponent, clamped to the range where the blend still reads as a surface.</summary>
        public float SafeSharpness { get { return Mathf.Clamp(sharpness, 1f, 8f); } }
    }

    // -------------------------------------------------------------------------
    //  T5 - Colour Gradient
    // -------------------------------------------------------------------------

    public enum GradientDriver
    {
        /// <summary>Ramp indexed by world height, between Min and Max metres.</summary>
        Altitude = 0,
        /// <summary>Ramp indexed by slope angle, between Min and Max degrees.</summary>
        Slope = 1
    }

    public enum GradientBlend
    {
        /// <summary>Darkens; keeps the ground texture's detail. The safe default.</summary>
        Multiply = 0,
        /// <summary>Multiplies in shadow, screens in highlight. Keeps contrast.</summary>
        Overlay = 1,
        /// <summary>Ignores what is underneath. Use for stylised terrain.</summary>
        Replace = 2
    }

    /// <summary>
    /// A multi-stop colour ramp driven by altitude or slope, applied over the
    /// painted layers.
    ///
    /// This is the cheapest way to make a terrain look like it has a climate: the
    /// same rock texture reads as sun-bleached at 2000 m and as damp at 200 m
    /// without a second material. The ramp is baked to a 256x1 LUT so the shader
    /// costs one clamped fetch, not a chain of lerps.
    ///
    /// Two outputs: the surface it just modified, and the ramp itself, so a
    /// vegetation node downstream can be tinted by the same climate curve rather
    /// than by a second hand-matched one.
    /// </summary>
    [System.Serializable]
    public class ColorGradientNode : TextureNode
    {
        public override string NodeName { get { return "Colour Gradient"; } }
        public override NodeCategory Category { get { return NodeCategory.Surface; } }
        public override string Tooltip
        {
            get { return "Colours the terrain by altitude or slope from a multi-stop ramp."; }
        }

        private static readonly Port[] Outs =
        {
            new Port("Surface",  PortType.Surface,  PortDirection.Output, true),
            new Port("Gradient", PortType.Gradient, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return TexturePorts.SurfaceChainIn; }
        public override Port[] GetOutputs() { return Outs; }

        [Tooltip("What indexes the ramp: world height in metres, or slope angle in degrees.")]
        public GradientDriver mode = GradientDriver.Altitude;

        [Tooltip("How the ramp colour meets the painted ground.")]
        public GradientBlend blendMode = GradientBlend.Multiply;

        [Tooltip("Value that maps to the left end of the ramp. Metres, or degrees in Slope mode.")]
        public float rangeMin;

        [Tooltip("Value that maps to the right end of the ramp.")]
        public float rangeMax = 1000f;

        [Tooltip("0 leaves the ground untouched, 1 applies the ramp in full.")]
        public float strength = 1f;

        [Tooltip("Ramp stops. Position runs 0 to 1 across the range above; order does not matter.")]
        public List<GradientStop> stops = new List<GradientStop>
        {
            new GradientStop(0f,    new Color(0.38f, 0.42f, 0.26f)),
            new GradientStop(0.55f, new Color(0.55f, 0.52f, 0.44f)),
            new GradientStop(1f,    new Color(0.86f, 0.87f, 0.90f))
        };

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GradientProduct gradient = BuildGradient();

            // Port 1 is the ramp on its own, for anything downstream that wants
            // to share this climate curve.
            if (outputIndex == 1) return gradient;

            SurfaceProduct upstream = GetInputSurface(tile, 0, stop);
            SurfaceProduct surface = upstream != null ? upstream.Copy() : new SurfaceProduct();

            surface.EnableFeature(UCShaderProps.EnableGradient);
            surface.SetGradient(UCShaderProps.GradientTex, gradient);
            surface.SetFloat(UCShaderProps.GradientMode, (float)(int)mode);
            surface.SetFloat(UCShaderProps.GradientBlendMode, (float)(int)blendMode);
            surface.SetFloat(UCShaderProps.GradientStrength, Mathf.Clamp01(strength));

            // Guard the degenerate range here rather than in the shader. An
            // inverted or zero-width range divides by zero per pixel; a swap and
            // a minimum width costs nothing once, on the CPU, at author time.
            float lo = Mathf.Min(rangeMin, rangeMax);
            float hi = Mathf.Max(rangeMin, rangeMax);
            if (hi - lo < 0.001f) hi = lo + 0.001f;

            surface.SetFloat(UCShaderProps.GradientMin, lo);
            surface.SetFloat(UCShaderProps.GradientMax, hi);

            if (stops == null || stops.Count == 0)
                surface.Notice("Colour Gradient has no stops; the ramp resolves to white.");

            return surface;
        }

        private GradientProduct BuildGradient()
        {
            GradientProduct g = new GradientProduct();
            if (stops == null) return g;

            for (int i = 0; i < stops.Count; i++)
            {
                GradientStop s = stops[i];
                s.position = Mathf.Clamp01(s.position);
                g.stops.Add(s);
            }
            return g;
        }
    }

    // -------------------------------------------------------------------------
    //  T6 - Texture Splat
    // -------------------------------------------------------------------------

    /// <summary>
    /// Collects up to four layers into the set the control map indexes: input 0
    /// is the control map's red channel, 1 green, 2 blue, 3 alpha.
    ///
    /// Four is not a soft limit. The uber-shader samples one RGBA control map, so
    /// a fifth layer needs a second control texture and four more sampler slots -
    /// a different shader, not a longer array. The node reports the excess rather
    /// than accepting a wire that could never render.
    ///
    /// This is also the seam between the two graphs: the height graph's Texture
    /// Output paints WHERE each channel goes, and this node decides WHAT each
    /// channel is made of.
    /// </summary>
    [System.Serializable]
    public class TextureSplatNode : TextureNode
    {
        public override string NodeName { get { return "Texture Splat"; } }
        public override string Tooltip
        {
            get { return "Four layers in control-map channel order: R, G, B, A."; }
        }

        public override Port[] GetInputs() { return TexturePorts.FourLayerIn; }
        public override Port[] GetOutputs() { return TexturePorts.SplatOut; }

        [Tooltip("Height blending between these layers. Needs mask maps with a height channel.")]
        public bool heightBlend;

        [Tooltip("Width of the height-blend transition band. Ignored unless Height Blend is on.")]
        public float heightTransition = 0.2f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            SplatProduct splat = new SplatProduct();

            for (int i = 0; i < 4; i++)
            {
                LayerProduct layer = GetInputLayer(tile, i, stop);
                if (layer != null) splat.layers[i] = layer;
            }

            splat.heightBlend = heightBlend;
            splat.heightTransition = Mathf.Clamp(heightTransition, 0f, 1f);

            return splat;
        }
    }

    // -------------------------------------------------------------------------
    //  T7 - Detail Texture
    // -------------------------------------------------------------------------

    public enum DetailBlendMode
    {
        /// <summary>Multiplies in shadow, screens in highlight. Keeps the base contrast.</summary>
        Overlay = 0,
        /// <summary>Darkens only. Good for grime and soot.</summary>
        Multiply = 1,
        /// <summary>Brightens only. Good for dust and frost.</summary>
        Add = 2
    }

    /// <summary>
    /// A fine tiling pattern laid over everything, fading out with distance.
    ///
    /// WHAT PROBLEM IT SOLVES
    /// ----------------------
    /// A ground texture tiled every 8 m is convincing at 3 m and obviously
    /// repeating at 40 m, because the eye picks up the lattice long before it
    /// picks up the texel. A second pattern at a different scale breaks that
    /// lattice, since two periods that are not integer multiples of each other
    /// only realign at their least common multiple.
    ///
    /// The distance fade is not an optimisation, it is the point. Detail at
    /// 200 m is smaller than a pixel, so keeping it there produces shimmer, not
    /// detail - the sampler picks a different texel every frame as the camera
    /// moves. Fading it out between Start and End metres lets the mip chain take
    /// over cleanly.
    /// </summary>
    [System.Serializable]
    public class DetailTextureNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Detail Texture"; } }
        public override string Tooltip
        {
            get { return "Fine overlay pattern that breaks up tiling up close and fades out with distance."; }
        }

        [Tooltip("Greyscale or low-saturation pattern. Noise, grain, fine gravel.")]
        public Texture2D detailTexture;

        [Tooltip("World size of one detail tile, in metres. Keep well under the layer tiling.")]
        public float detailScaleMetres = 1.5f;

        [Tooltip("How strongly the pattern shows. Past about 0.5 it reads as dirt rather than detail.")]
        public float strength = 0.35f;

        [Tooltip("How the pattern meets the ground colour.")]
        public DetailBlendMode blendMode = DetailBlendMode.Overlay;

        [Tooltip("Metres from camera where the detail starts fading out.")]
        public float fadeStartMetres = 25f;

        [Tooltip("Metres from camera where it is gone. Must exceed Fade Start.")]
        public float fadeEndMetres = 90f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            if (detailTexture == null)
            {
                surface.Notice("Detail Texture has no texture assigned; the feature stays off.");
                return;
            }

            surface.EnableFeature(UCShaderProps.EnableDetail);
            surface.SetTexture(UCShaderProps.DetailTex, new TextureRef(detailTexture));
            surface.SetFloat(UCShaderProps.DetailScale, Mathf.Max(0.01f, detailScaleMetres));
            surface.SetFloat(UCShaderProps.DetailStrength, Mathf.Clamp01(strength));
            surface.SetFloat(UCShaderProps.DetailBlendMode, (float)(int)blendMode);

            float start = Mathf.Max(0f, fadeStartMetres);
            float end = Mathf.Max(start + 1f, fadeEndMetres);
            surface.SetFloat(UCShaderProps.DetailFadeStart, start);
            surface.SetFloat(UCShaderProps.DetailFadeEnd, end);
        }
    }
}
