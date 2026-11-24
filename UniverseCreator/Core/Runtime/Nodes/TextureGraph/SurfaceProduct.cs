// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    SurfaceProduct.cs
//  Purpose: The accumulator every texture-graph feature writes into, and the
//           layer/splat/gradient products that feed it.
//
//  WHY A PROPERTY BAG AND NOT A TYPED STRUCT
//  -----------------------------------------
//  SurfaceProduct stores shader property names as strings. That looks like the
//  weaker choice until you consider what the alternative costs: a typed field per
//  shader uniform means every new shader feature touches this file, the node that
//  sets it, and the applier that writes it - three edits to add one slider, with
//  two of them pure transcription.
//
//  The risk of a string key is a typo that fails silently. That is handled
//  directly rather than designed around: UCSelfTest validates every key any node
//  writes against the shader's own property list, so a typo is a red test in
//  batch mode rather than a control that does nothing in the editor. A name that
//  the shader does not have is a build failure, which is the only property of the
//  typed version anyone actually wanted.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    // -------------------------------------------------------------------------
    //  Layer
    // -------------------------------------------------------------------------

    /// <summary>
    /// One terrain material layer. Maps one-to-one onto Unity's TerrainLayer, so
    /// what the graph describes is exactly what the terrain engine paints with -
    /// and the stock Paint Terrain tools keep working on the result.
    /// </summary>
    public class LayerProduct
    {
        public string name = "Layer";

        public TextureRef albedo;
        public TextureRef normal;
        public TextureRef mask;          // R metallic, G occlusion, B height, A smoothness

        public UVTransform uv = new UVTransform();

        public Color tint = Color.white;
        public float normalStrength = 1f;
        public float metallic;
        public float smoothness = 0.35f;
        /// <summary>UC-T07-06: finite occlusion multiplier, 0…1; one preserves the packed mask's occlusion.</summary>
        public float occlusion = 1;
        /// <summary>UC-T07-06: linear HDR emission colour; nonnegative RGB, with no baked-in scene lighting.</summary>
        public Color emission = Color.black;
        /// <summary>UC-T07-06: finite material opacity, 0…1; TerrainLit supports cutoff clipping, not transparent terrain.</summary>
        public float opacity = 1;
        /// <summary>UC-T07-06: explicitly assigns albedo alpha to opacity; smoothness then uses mask alpha or its scalar default.</summary>
        public bool albedoAlphaIsOpacity;

        /// <summary>
        /// Height offset applied to this layer's mask B channel during height
        /// blending. Lifting a layer makes it read through its neighbours where
        /// they overlap - the difference between gravel scattered ON sand and
        /// gravel cross-faded INTO it.
        /// </summary>
        public float heightOffset;

        /// <summary>
        /// Requests world-space triplanar for this layer on steep ground. The
        /// per-layer flag is advisory: the uber-shader has one cliff channel, so
        /// the first layer asking for it wins and the rest are reported. Honest
        /// about the limit rather than quietly ignoring three of four requests.
        /// </summary>
        public bool triplanarOnSlopes;
        /// <summary>UC-T07-11: finite world-axis blend exponent, 1…8, shared by cliff and snow projection.</summary>
        public float triplanarSharpness = 4;

        /// <summary>UC-T07-02: validates declared colour/normal/PBR mask meanings without guessing legacy import semantics.</summary>
        /// <exception cref="System.ArgumentException">An explicitly described image is wired into an incompatible material channel.</exception>
        /// <remarks>Albedo accepts Colour, normals accept TangentNormal, and masks accept TerrainMask.
        /// Imported legacy references with unspecified metadata remain supported; no texture readback or allocation occurs.</remarks>
        public void ValidateTextureChannels()
        {
            ValidateChannel(albedo, TextureImageSemantic.Colour, "base colour");
            ValidateChannel(normal, TextureImageSemantic.TangentNormal, "tangent normal");
            ValidateChannel(mask, TextureImageSemantic.TerrainMask, "terrain PBR mask");
            TextureImageSampling.RequireFinite(tint); TextureImageSampling.RequireFinite(emission);
            TextureImageSampling.RequireFinite(normalStrength); TextureImageSampling.RequireFinite(heightOffset);
            TextureImageSampling.RequireFinite(triplanarSharpness);
            if (triplanarSharpness < 1 || triplanarSharpness > 8) throw new System.ArgumentException("Triplanar sharpness requires a finite exponent 1…8.");
            ValidateUnit(metallic); ValidateUnit(smoothness); ValidateUnit(occlusion); ValidateUnit(opacity);
            if (normalStrength < 0 || normalStrength > 4 || emission.r < 0 || emission.g < 0 || emission.b < 0)
                throw new System.ArgumentException("UC-T07-06: layer normals require strength 0…4 and emission requires nonnegative linear RGB.");
        }

        private static void ValidateUnit(float value)
        { TextureImageSampling.RequireFinite(value); if (value < 0 || value > 1) throw new System.ArgumentException("UC-T07-06: material channels require values 0…1."); }

        internal static void ValidateChannel(TextureRef reference, TextureImageSemantic expected, string channel)
        {
            if (reference?.ImageDescription == null) return;
            TextureImageDescriptor descriptor = reference.ImageDescription.Value; descriptor.Validate();
            if (descriptor.Semantic != expected)
                throw new System.ArgumentException("UC-T07-02: " + channel + " requires explicit channel conversion from " + descriptor.Semantic + ".");
        }

        /// <summary>UC-T01-12: copies editable layer/UV/sampling metadata while borrowing native texture handles.</summary>
        /// <returns>Independent metadata; absent descriptors remain absent and no Unity resource is created or released.</returns>
        public LayerProduct Copy()
        {
            return new LayerProduct
            {
                name = name,
                albedo = albedo != null ? albedo.Copy() : null,
                normal = normal != null ? normal.Copy() : null,
                mask = mask != null ? mask.Copy() : null,
                uv = uv != null ? uv.Copy() : new UVTransform(),
                tint = tint,
                normalStrength = normalStrength,
                metallic = metallic,
                smoothness = smoothness,
                occlusion = occlusion, emission = emission, opacity = opacity, albedoAlphaIsOpacity = albedoAlphaIsOpacity,
                heightOffset = heightOffset,
                triplanarOnSlopes = triplanarOnSlopes, triplanarSharpness = triplanarSharpness
            };
        }
    }

    // -------------------------------------------------------------------------
    //  Splat
    // -------------------------------------------------------------------------

    /// <summary>
    /// Up to four layers, in control-map channel order: index 0 is the R channel,
    /// 1 is G, 2 is B, 3 is A.
    ///
    /// Four and not more, because the uber-shader samples one RGBA control map.
    /// A fifth layer needs a second control map and a second set of four samplers,
    /// which is a different shader - not a bigger array here.
    /// </summary>
    public class SplatProduct
    {
        public readonly LayerProduct[] layers = new LayerProduct[4];
        /// <summary>UC-T07-11: optional independent continuous membership maps in world XZ metres; null slots mean zero when any coverage is authored.</summary>
        /// <remarks>Borrowed immutable after publication. Apply resamples weights, never category IDs; all-null retains the world's existing control maps.</remarks>
        public readonly Matrix[] coverage = new Matrix[4];

        /// <summary>Whether height blending is requested for this splat set.</summary>
        public bool heightBlend;

        /// <summary>0 is a hard cut between layers, 1 a soft cross-fade.</summary>
        public float heightTransition = 0.2f;

        /// <summary>UC-T01-12: copies four ordered layers and their editable descriptors without duplicating textures.</summary>
        /// <returns>Independent splat metadata preserving null layer slots and blend settings.</returns>
        public SplatProduct Copy()
        {
            var copy = new SplatProduct { heightBlend = heightBlend, heightTransition = heightTransition };
            for (int i = 0; i < layers.Length; i++) { copy.layers[i] = layers[i] != null ? layers[i].Copy() : null; copy.coverage[i] = coverage[i]; }
            return copy;
        }

        /// <summary>Number of leading non-null layers. Trailing gaps are not counted.</summary>
        public int Count
        {
            get
            {
                int n = 0;
                for (int i = 0; i < layers.Length; i++) if (layers[i] != null) n = i + 1;
                return n;
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Gradient
    // -------------------------------------------------------------------------

    /// <summary>One stop on a colour ramp. Inline struct so the node drawer lists it.</summary>
    [System.Serializable]
    public struct GradientStop
    {
        [Tooltip("Position along the ramp, 0 to 1.")]
        public float position;

        public Color color;

        public GradientStop(float position, Color color)
        {
            this.position = position;
            this.color = color;
        }
    }

    /// <summary>
    /// A multi-stop colour ramp, evaluated on the CPU and baked to a 256x1 LUT.
    ///
    /// 256 because that is the width at which a smooth ramp across a full
    /// mountain stops banding, and the cost is one row of texels either way.
    /// </summary>
    public class GradientProduct
    {
        public readonly List<GradientStop> stops = new List<GradientStop>();

        /// <summary>UC-T01-12: copies ordered value-type stops before a branch edits a ramp.</summary>
        /// <returns>Independent stop list, including an empty list; no sorting or native resource allocation occurs.</returns>
        public GradientProduct Copy()
        {
            var copy = new GradientProduct(); copy.stops.AddRange(stops); return copy;
        }

        /// <summary>Sorted-by-position copy of the stops. Sorting is done here so
        /// nodes may append in any order and users may drag a stop past another.</summary>
        public List<GradientStop> Sorted()
        {
            var copy = new List<GradientStop>(stops);
            copy.Sort((a, b) => a.position.CompareTo(b.position));
            return copy;
        }

        /// <summary>
        /// Colour at t in 0..1. Clamps outside the outermost stops rather than
        /// extrapolating - extrapolating a colour ramp produces out-of-gamut
        /// values that only show up once something bright lights the terrain.
        /// </summary>
        public Color Evaluate(float t)
        {
            List<GradientStop> s = Sorted();
            if (s.Count == 0) return Color.white;
            if (s.Count == 1) return s[0].color;

            t = Mathf.Clamp01(t);
            if (t <= s[0].position) return s[0].color;
            if (t >= s[s.Count - 1].position) return s[s.Count - 1].color;

            for (int i = 0; i < s.Count - 1; i++)
            {
                float a = s[i].position;
                float b = s[i + 1].position;
                if (t < a || t > b) continue;

                float span = b - a;
                float k = span > 1e-6f ? (t - a) / span : 0f;
                return Color.Lerp(s[i].color, s[i + 1].color, k);
            }
            return s[s.Count - 1].color;
        }
    }

    // -------------------------------------------------------------------------
    //  Water
    // -------------------------------------------------------------------------

    /// <summary>
    /// A water surface description. Kept separate from SurfaceProduct because
    /// water is a different material on different geometry - the terrain shader
    /// has no transparency path, and giving it one would cost every opaque
    /// terrain pixel a blend it never uses.
    ///
    /// Serializable so a hand-placed UCWaterBody can hold one in the inspector and
    /// hand the same object to the same applier the graph uses. One description,
    /// one code path, whichever end it came from.
    /// </summary>
    [System.Serializable]
    public class WaterProduct
    {
        [Tooltip("Colour where the water is shallow. This is what the shoreline reads as.")]
        public Color shallowColor = new Color(0.29f, 0.61f, 0.66f, 0.72f);

        [Tooltip("Colour at full depth. The distance between the two colours is the " +
                 "single strongest cue that water has volume.")]
        public Color deepColor = new Color(0.03f, 0.13f, 0.20f, 0.95f);

        [Tooltip("Depth in metres over which shallow fades to deep. Small numbers " +
                 "give a tropical shelf, large ones an ocean that stays readable.")]
        public float depthFadeMetres = 6f;

        [Tooltip("How much of the scene behind the water shows through, 0 to 1.")]
        public float transparency = 0.75f;

        [Tooltip("How far the refracted background is bent, in screen units. Above " +
                 "about 0.08 the bend pulls in pixels from outside the surface and " +
                 "the shoreline smears.")]
        public float refraction = 0.03f;

        [Tooltip("Fresnel exponent. Higher makes reflection appear only at grazing " +
                 "angles, which is what real water does; 5 is physical.")]
        public float fresnelPower = 5f;

        [Tooltip("Specular smoothness. Water is near-mirror, so this belongs high.")]
        public float smoothness = 0.92f;

        [Tooltip("Normal map for the wave detail. This, not the mesh, is where the " +
                 "small waves come from - which is why a 64-vertex lattice is enough " +
                 "on a phone.")]
        public TextureRef normalMap;

        [Tooltip("Wave scroll in world metres per second. On a river the vertex flow " +
                 "stream overrides this per-vertex; on a lake it is the whole motion.")]
        public Vector2 flowMetresPerSecond = new Vector2(0.06f, 0.03f);

        [Tooltip("Strength of the wave normal map.")]
        public float normalScale = 1f;

        [Tooltip("World size of one wave-normal tile, in metres. Metres and not a " +
                 "repeat count, so the waves stay the same size when the world does not.")]
        public float waveTileMetres = 12f;

        // Foam

        [Tooltip("Draw foam at the shoreline and wherever the flow goes fast enough " +
                 "to break.")]
        public bool foamEnabled;

        [Tooltip("Foam texture. Leave empty for flat-coloured foam.")]
        public TextureRef foamTexture;

        [Tooltip("Foam tint. Real foam is not pure white - it carries the water's colour.")]
        public Color foamColor = Color.white;

        [Tooltip("Foam opacity.")]
        public float foamIntensity = 1f;

        [Tooltip("How far the shoreline foam band reaches out from the bank, in metres.")]
        public float foamEdgeWidthMetres = 1.2f;

        [Tooltip("Foam scroll speed relative to the water.")]
        public float foamScrollSpeed = 0.15f;

        [Tooltip("World size of one foam tile, in metres.")]
        public float foamTileMetres = 4f;

        // Caustics

        [Tooltip("Project light patterns onto the bed under the water.")]
        public bool causticsEnabled;

        [Tooltip("Caustics texture. A tiling caustic pattern; leave empty for none.")]
        public TextureRef causticsTexture;

        [Tooltip("Caustics tint.")]
        public Color causticsColor = Color.white;

        [Tooltip("Caustics brightness.")]
        public float causticsIntensity = 0.6f;

        [Tooltip("World size of one caustics tile, in metres.")]
        public float causticsTileMetres = 3f;

        [Tooltip("Caustics animation speed.")]
        public float causticsSpeed = 0.05f;

        [Tooltip("Depth in metres at which caustics have faded out. Real caustics " +
                 "do not reach the bottom of a deep lake.")]
        public float causticsDepthFadeMetres = 8f;

        // Interactive ripples.
        //
        // How strongly this body responds to the scene's ripple simulation. Per
        // body and not global, because a puddle and an ocean want the same
        // simulation to read very differently on them - a footstep that is a
        // dramatic event in a puddle is invisible in a swell.

        [Tooltip("How strongly this body responds to the scene ripple simulation. A " +
                 "footstep is a dramatic event in a puddle and invisible in a swell, " +
                 "so this is per body rather than global.")]
        public float rippleStrength = 1f;

        [Tooltip("How much foam a ripple leaves behind it.")]
        public float rippleFoam = 1f;

        // Vertex wave motion. These mirror _WaveHeight/_WaveLength/_WaveSpeed and
        // must stay in step with the WaterSurfaceParams the physics reads, or a
        // boat floats off the visible surface.

        [Tooltip("Vertical swell amplitude in metres. The physics reads the same " +
                 "number, so a boat sits on the wave you can see.")]
        public float waveHeightMetres = 0.12f;

        [Tooltip("Distance between swell crests in metres.")]
        public float waveLengthMetres = 26f;

        [Tooltip("Swell travel speed.")]
        public float waveSpeed = 0.6f;

        [Tooltip("Distance in metres over which the surface fades out at the bank, " +
                 "so the water does not end in a hard line across the ground.")]
        public float edgeSoftenMetres = 0.6f;

        // Shoreline / bank band.
        //
        // Where water meets land the ground is not the grass or rock that sits
        // inland - it is a band of wet, water-worked material: sand on an ocean
        // beach, gravel or mud on a lake margin, scoured sand on a river bank. The
        // terrain's own beach band (the _UC_BEACH_ON shader feature) draws this at
        // the GLOBAL sea level for the ocean. A lake or a river sits at its own
        // altitude, so the band it wants is described here PER BODY and applied
        // around that body rather than at one world height. Reading grass straight
        // into the waterline - the "grass standing in the water" artefact - is what
        // these fields exist to prevent.

        [Tooltip("Draw a wet shoreline / bank band where this body meets the land, " +
                 "instead of letting the inland grass or rock run into the water.")]
        public bool shorelineEnabled;

        [Tooltip("Colour of the wet shoreline band - beach sand, lake gravel or river " +
                 "bank. Read at the waterline; it dries toward the inland surface " +
                 "across the band width.")]
        public Color shorelineColor = new Color(0.62f, 0.57f, 0.44f, 1f);

        [Tooltip("How far the band reaches inland from the waterline, in metres. A surf " +
                 "beach is tens of metres; a lake margin or a river bank a few.")]
        public float shorelineWidthMetres = 4f;

        [Tooltip("How much darker the band is right at the waterline, 0 to 1. Wet sand " +
                 "is markedly darker than dry; about 0.3 is a convincing tide line.")]
        public float shorelineWetDarkening = 0.3f;

        [Tooltip("Specular smoothness of the wet band. Water-saturated sand is glossy at " +
                 "the edge and matte where it dries, so this is the wet-end value.")]
        public float shorelineSmoothness = 0.45f;

        [Tooltip("Optional sand / gravel / bank texture for the band. Empty gives a " +
                 "flat-coloured band.")]
        public TextureRef shorelineTexture;

        [Tooltip("River only: derive the bank from the dug trench walls rather than an " +
                 "altitude band, so the sand follows the channel it was cut into and " +
                 "grass never ends up standing in the water.")]
        public bool bankReadsTrench;

        [Tooltip("Slope in degrees below which the bank band is allowed. Above it the " +
                 "wall is too steep to hold the worked material and the inland surface " +
                 "shows through, which is what a real cut bank does.")]
        public float bankSlopeLimitDegrees = 55f;

        /// <summary>
        /// Copy, for the same reason SurfaceProduct has one: the water chain is
        /// usually linear, but nothing stops a user fanning one Water Surface into
        /// a foamy shoreline branch and a still-lake branch. Mutating in place
        /// would let the second branch inherit the first one's foam, and the
        /// symptom - foam on a lake that has no foam node - would send them
        /// looking at the wrong node entirely.
        /// </summary>
        /// <returns>UC-T01-12: independent water/sampling metadata; native textures remain borrowed from their original owner.</returns>
        public WaterProduct Copy()
        {
            var copy = (WaterProduct)MemberwiseClone();
            copy.normalMap = normalMap != null ? normalMap.Copy() : null;
            copy.foamTexture = foamTexture != null ? foamTexture.Copy() : null;
            copy.causticsTexture = causticsTexture != null ? causticsTexture.Copy() : null;
            copy.shorelineTexture = shorelineTexture != null ? shorelineTexture.Copy() : null;
            return copy;
        }
    }

    // -------------------------------------------------------------------------
    //  Surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// The accumulated material description. Every surface feature node takes one
    /// of these, adds its own contribution, and passes it on, so the chain in the
    /// graph reads in the same order the shader applies the features.
    /// </summary>
    public class SurfaceProduct
    {
        /// <summary>UC-T07-02: optional immutable supported shader-output contract; absent before a terminal output selects its target.</summary>
        public ShaderOutputDescriptor ShaderOutput { get; internal set; }
        public readonly Dictionary<string, float> floats = new Dictionary<string, float>();
        public readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
        /// <summary>UC-T07-06: finite four-component shader parameters; distinct from colour encoding and image pixels.</summary>
        public readonly Dictionary<string, Vector4> vectors = new Dictionary<string, Vector4>();
        public readonly Dictionary<string, TextureRef> textures = new Dictionary<string, TextureRef>();
        public readonly HashSet<string> keywords = new HashSet<string>();

        /// <summary>The splat set this surface paints with, if one was supplied.</summary>
        public SplatProduct splat;

        /// <summary>
        /// Water description, if a water chain was joined in. Applied to the water
        /// material rather than the terrain one.
        /// </summary>
        public WaterProduct water;

        // ---- typed extras --------------------------------------------------
        //
        //  Not everything the graph decides is a shader uniform. Grass tint lands
        //  on DetailPrototype.healthyColor, layer count lands on the TerrainLayer
        //  array. Those get real fields rather than dictionary keys, because the
        //  self-test validates every key against the shader's property list and a
        //  key that is deliberately not a shader property would fail it - which
        //  would be a true report of a false problem.

        /// <summary>Season-blended ground-cover tint, if a Vegetation Colour node ran.</summary>
        public Color vegetationTint = Color.white;
        public bool vegetationTintSet;

        /// <summary>Dry-season variant of the same tint.</summary>
        public Color vegetationDryTint = new Color(0.7f, 0.65f, 0.4f, 1f);

        /// <summary>
        /// Layer budget from Terrain Blend Final. Layers past this are dropped
        /// before the splat reaches the terrain, so a mobile target can share one
        /// graph with a desktop one.
        /// </summary>
        public int maxLayers = 4;

        /// <summary>Quality tier chosen in Terrain Blend Final. 0 low, 1 medium, 2 high.</summary>
        public int qualityLevel = 2;

        /// <summary>
        /// CPU-baked maps to be turned into Texture2D on the main thread, keyed by
        /// the shader property they will land in. The occlusion raymarch and the
        /// noise bake fill these; nothing else should.
        /// </summary>
        public readonly Dictionary<string, Matrix> bakedMaps = new Dictionary<string, Matrix>();

        /// <summary>
        /// Gradient ramps to bake to 256x1 LUTs, keyed by shader property.
        /// </summary>
        public readonly Dictionary<string, GradientProduct> bakedGradients =
            new Dictionary<string, GradientProduct>();

        /// <summary>
        /// Notes the graph wants surfaced to the user - a request the shader could
        /// not honour, a value that was clamped. Shader Output logs these once,
        /// as a single grouped message rather than one warning per node.
        /// </summary>
        public readonly List<string> notices = new List<string>();

        // ---- writers -------------------------------------------------------

        public void SetFloat(string name, float value) { floats[name] = value; }
        public void SetColor(string name, Color value) { colors[name] = value; }
        /// <summary>UC-T07-06: stores a finite shader vector without touching textures or rebuilding terrain.</summary>
        /// <param name="name">Nonempty shader property name.</param>
        /// <param name="value">Four finite scalar components, in the target property's declared units.</param>
        /// <exception cref="System.ArgumentException">Name is empty or a component is nonfinite.</exception>
        public void SetVector(string name, Vector4 value)
        {
            if (string.IsNullOrEmpty(name)) throw new System.ArgumentException("Shader vector needs a property name.");
            TextureImageSampling.RequireFinite(new Color(value.x, value.y, value.z, value.w)); vectors[name] = value;
        }
        public void SetKeyword(string name) { keywords.Add(name); }

        public void SetTexture(string name, TextureRef value)
        {
            if (value == null || value.texture == null) textures.Remove(name);
            else textures[name] = value;
        }

        public void SetBakedMap(string name, Matrix map)
        {
            if (map == null) bakedMaps.Remove(name);
            else bakedMaps[name] = map;
        }

        public void SetGradient(string name, GradientProduct gradient)
        {
            if (gradient == null) bakedGradients.Remove(name);
            else bakedGradients[name] = gradient;
        }

        public void Notice(string message)
        {
            if (!string.IsNullOrEmpty(message) && !notices.Contains(message))
                notices.Add(message);
        }

        /// <summary>
        /// Enables a feature: sets its _Enable float to 1 and, when the feature is
        /// keyword-gated, its keyword too. Nodes call this rather than doing both
        /// by hand, because doing only the first is the exact desync that makes a
        /// tick box look broken.
        /// </summary>
        public void EnableFeature(string enableProperty, string keyword = null)
        {
            SetFloat(enableProperty, 1f);
            if (!string.IsNullOrEmpty(keyword)) SetKeyword(keyword);
        }

        /// <summary>
        /// UC-T01-12: copies editable surface/material descriptors so sibling feature chains remain independent.
        /// </summary>
        /// <returns>Independent dictionaries, stops, splat/water and sampling metadata. Native textures and large baked matrices stay borrowed.</returns>
        /// <remarks>Baked matrices are immutable after publication; a writer must copy a map before editing its samples.
        /// Sharing those arrays prevents every scalar material feature from cloning full-resolution baked maps.</remarks>
        public SurfaceProduct Copy()
        {
            var c = new SurfaceProduct { ShaderOutput = ShaderOutput }; // UC-T07-02: terminal metadata is immutable across sibling consumers.
            foreach (var kv in floats) c.floats[kv.Key] = kv.Value;
            foreach (var kv in colors) c.colors[kv.Key] = kv.Value;
            foreach (var kv in vectors) c.vectors[kv.Key] = kv.Value;
            foreach (var kv in textures) c.textures[kv.Key] = kv.Value != null ? kv.Value.Copy() : null;
            foreach (var kv in bakedMaps) c.bakedMaps[kv.Key] = kv.Value;
            foreach (var kv in bakedGradients) c.bakedGradients[kv.Key] = kv.Value != null ? kv.Value.Copy() : null;
            foreach (string k in keywords) c.keywords.Add(k);
            for (int i = 0; i < notices.Count; i++) c.notices.Add(notices[i]);
            c.splat = splat != null ? splat.Copy() : null;
            c.water = water != null ? water.Copy() : null;
            c.vegetationTint = vegetationTint;
            c.vegetationTintSet = vegetationTintSet;
            c.vegetationDryTint = vegetationDryTint;
            c.maxLayers = maxLayers;
            c.qualityLevel = qualityLevel;
            return c;
        }

        /// <summary>Validates declared layer and water-normal channel semantics before a material consumer mutates assets.</summary>
        /// <exception cref="System.ArgumentException">Explicit metadata describes incompatible channel data.</exception>
        public void ValidateTextureChannels()
        {
            if (splat != null) foreach (LayerProduct layer in splat.layers) layer?.ValidateTextureChannels();
            LayerProduct.ValidateChannel(water?.normalMap, TextureImageSemantic.TangentNormal, "water normal");
        }

        /// <summary>UC-T07-06: publishes ordered reusable layer appearance parameters to TerrainLit without baking image data.</summary>
        /// <remarks>Absent slots retain opacity/occlusion one and emission zero. Authored control-map order is preserved.</remarks>
        public void PublishLayerParameters()
        {
            Vector4 opacity = Vector4.one, alpha = Vector4.zero, occlusion = Vector4.one;
            for (int i = 0; i < 4; i++)
            {
                LayerProduct layer = splat?.layers[i]; layer?.ValidateTextureChannels();
                // UC-T07-11: the first layer projection reaches the existing cliff channel; explicit Cliff Surface authoring retains priority.
                if (layer != null && layer.triplanarOnSlopes && !floats.ContainsKey(UCShaderProps.EnableSlopeBlend))
                {
                    EnableFeature(UCShaderProps.EnableSlopeBlend, UCShaderProps.KeywordSlopeBlend);
                    if (layer.albedo != null) SetTexture(UCShaderProps.SlopeTex, layer.albedo);
                    if (layer.normal != null) SetTexture(UCShaderProps.SlopeNormal, layer.normal);
                    SetColor(UCShaderProps.SlopeColor, layer.tint);
                    SetFloat(UCShaderProps.SlopeSmoothness, layer.smoothness); SetFloat(UCShaderProps.SlopeMetallic, layer.metallic);
                    SetFloat(UCShaderProps.SlopeNormalStrength, layer.normalStrength);
                    SetFloat(UCShaderProps.SlopeTriplanarScale, layer.uv != null ? layer.uv.SafeTileSize.x : 8);
                    SetFloat(UCShaderProps.TriplanarSharpness, layer.triplanarSharpness);
                }
                opacity[i] = layer?.opacity ?? 1; alpha[i] = layer != null && layer.albedoAlphaIsOpacity ? 1 : 0;
                occlusion[i] = layer?.occlusion ?? 1;
                SetColor(UCShaderProps.LayerEmissionPrefix + i, layer?.emission ?? Color.black);
            }
            SetVector(UCShaderProps.LayerOpacity, opacity); SetVector(UCShaderProps.LayerAlphaOpacity, alpha);
            SetVector(UCShaderProps.LayerOcclusion, occlusion);
        }
    }
}
