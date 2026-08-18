// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    WaterNodes.cs
//  Purpose: Texture Graph T19-T21. The water material, authored as its own chain
//           and joined to the terrain surface at the end.
//
//  WHY WATER IS A SEPARATE CHAIN
//  -----------------------------
//  Water is a different material on different geometry. The terrain shader is
//  opaque with no blend state and no depth read; giving it a transparency path so
//  a lake could share it would cost every opaque terrain pixel in the world a
//  blend it never uses, and would put the terrain in the transparent queue where
//  it sorts against foliage badly.
//
//  So the water chain builds a WaterProduct, Water Surface Output hands it to the
//  water material, and the two only meet in Terrain Blend Final - which carries
//  the water description along so one graph asset still describes one world.
//
//  THE PHYSICS EACH NODE IMPLEMENTS
//  --------------------------------
//    T19  Beer-Lambert absorption for depth colour, Schlick's approximation for
//         the Fresnel term, and index-of-refraction-driven UV displacement.
//    T20  Foam where the water meets geometry, found by depth difference, which
//         is where turbulence actually entrains air in the real thing.
//    T21  Caustics as two counter-scrolling layers, because a single scrolling
//         layer reads as a sliding texture and two reads as interference.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  T19 - Water Surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// The base water material: colour with depth, transparency, refraction and
    /// the Fresnel falloff that makes water look like water.
    ///
    /// DEPTH COLOUR IS ABSORPTION, NOT A LERP
    /// --------------------------------------
    /// Water absorbs light exponentially with path length - Beer-Lambert:
    ///
    ///     I(d) = I0 * exp(-k * d)
    ///
    /// and k is strongly wavelength-dependent. Red is absorbed roughly thirty
    /// times faster than blue, which is the entire reason deep water is blue and
    /// why a red object goes grey at ten metres down. The shader therefore fades
    /// shallow to deep on an exponential curve rather than linearly, and Depth
    /// Fade is the distance at which about 63% of the light is gone - one
    /// e-folding, not the distance to full deep colour.
    ///
    /// FRESNEL
    /// -------
    /// Reflectance rises steeply toward grazing angles. Schlick's approximation:
    ///
    ///     R(theta) = R0 + (1 - R0) * (1 - cos theta)^5
    ///
    /// with R0 = 0.02 for water against air. Straight down you see through it;
    /// at a shallow angle it is a mirror. Power 5 is the physical exponent, and
    /// the field is exposed because stylised water often wants 3 or 4 - but the
    /// default should be the real number.
    ///
    /// REFRACTION
    /// ----------
    /// Water's refractive index is 1.333, so a ray crossing the surface bends by
    /// Snell's law. The shader approximates this by offsetting the scene-colour
    /// UV along the surface normal, which is the standard screen-space cheat: it
    /// is wrong for anything above the water line and right for everything below,
    /// which is where anyone is looking.
    /// </summary>
    [System.Serializable]
    public class WaterSurfaceNode : TextureNode
    {
        public override string NodeName { get { return "Water Surface"; } }
        public override NodeCategory Category { get { return NodeCategory.Water; } }
        public override string Tooltip
        {
            get { return "Base water: depth absorption, Fresnel reflectance, refraction and wave normals."; }
        }

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return TexturePorts.WaterOut; }

        [Tooltip("Colour in shallow water, where little light has been absorbed.")]
        public Color shallowColor = new Color(0.29f, 0.61f, 0.66f, 0.72f);

        [Tooltip("Colour at depth. Real deep water is near-black blue, not saturated teal.")]
        public Color deepColor = new Color(0.03f, 0.13f, 0.20f, 0.95f);

        [Tooltip("Metres over which the colour fades from shallow to deep. One e-folding of absorption.")]
        public float depthFadeMetres = 6f;

        [Tooltip("How much of the scene behind shows through. 0 is opaque paint.")]
        public float transparency = 0.75f;

        [Tooltip("Strength of the UV bend. Water's real index is 1.333; 0.02-0.05 looks right.")]
        public float refraction = 0.03f;

        [Tooltip("Fresnel exponent. 5 is Schlick's physical value; 3-4 is a stylised look.")]
        public float fresnelPower = 5f;

        [Tooltip("Surface gloss. Still water is 0.95 and up; wind-chopped water lower.")]
        public float smoothness = 0.92f;

        [Tooltip("Wave normal map. Two layers of this are scrolled against each other.")]
        public Texture2D normalMap;

        [Tooltip("World size of one wave tile, in metres.")]
        public float waveTileMetres = 12f;

        [Tooltip("Depth of the wave normals.")]
        public float normalScale = 1f;

        [Tooltip("Surface drift in metres per second. Small numbers; 0.06 is a gentle current.")]
        public Vector2 flowMetresPerSecond = new Vector2(0.06f, 0.03f);

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            WaterProduct water = new WaterProduct();

            water.shallowColor = shallowColor;
            water.deepColor = deepColor;
            water.depthFadeMetres = Mathf.Max(0.01f, depthFadeMetres);
            water.transparency = Mathf.Clamp01(transparency);
            water.refraction = Mathf.Clamp(refraction, 0f, 0.5f);
            water.fresnelPower = Mathf.Clamp(fresnelPower, 1f, 10f);
            water.smoothness = Mathf.Clamp01(smoothness);

            if (normalMap != null)
                water.normalMap = new TextureRef(normalMap);

            water.waveTileMetres = Mathf.Max(0.1f, waveTileMetres);
            water.normalScale = Mathf.Clamp(normalScale, 0f, 4f);
            water.flowMetresPerSecond = flowMetresPerSecond;

            return water;
        }
    }

    // -------------------------------------------------------------------------
    //  T20 - Water Foam
    // -------------------------------------------------------------------------

    /// <summary>
    /// White water where the surface meets something solid.
    ///
    /// HOW THE EDGE IS FOUND
    /// ---------------------
    /// Compare the depth buffer's value at this pixel with the water surface's own
    /// depth. A small difference means the geometry behind is very close to the
    /// surface - a shoreline, a rock breaking through, a boat hull. That is
    /// exactly where real water entrains air and turns white, so the depth
    /// difference is not a proxy for the effect, it is the same quantity the
    /// effect depends on.
    ///
    /// Edge Width is in metres of depth difference, so it means the same thing on
    /// a lake and on an ocean, and does not change when the camera moves.
    ///
    /// NOTE ON THE DEPTH TEXTURE
    /// -------------------------
    /// This needs the URP camera depth texture. If it is off in the pipeline
    /// asset, foam has nothing to compare against - the node says so through the
    /// notices rather than rendering a uniform white sheet and leaving the user
    /// to work out why.
    /// </summary>
    [System.Serializable]
    public class WaterFoamNode : TextureNode
    {
        public override string NodeName { get { return "Water Foam"; } }
        public override NodeCategory Category { get { return NodeCategory.Water; } }
        public override string Tooltip
        {
            get { return "Foam where water meets geometry, found from depth difference. Needs the depth texture."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Water", PortType.Water, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.WaterOut; }

        [Tooltip("Foam pattern. Greyscale; the colour below tints it.")]
        public Texture2D foamTexture;

        [Tooltip("Foam colour. Slightly off-white; pure white blows out in sunlight.")]
        public Color foamColor = new Color(0.95f, 0.97f, 0.98f, 1f);

        [Tooltip("How strongly the foam shows.")]
        public float intensity = 1f;

        [Tooltip("Depth difference in metres over which foam appears. 1-2 m suits a shoreline.")]
        public float edgeWidthMetres = 1.2f;

        [Tooltip("How fast the pattern drifts, in metres per second.")]
        public float scrollSpeed = 0.15f;

        [Tooltip("World size of one foam tile, in metres.")]
        public float tileMetres = 4f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            WaterProduct upstream = GetInputWater(tile, 0, stop);

            // Same rule as the surface chain: an unwired input starts a fresh
            // product rather than returning null, so a half-built graph still
            // previews something. Copied rather than mutated, so a fan-out does
            // not push this node's foam back up into the other branch.
            WaterProduct water = upstream != null ? upstream.Copy() : new WaterProduct();

            water.foamEnabled = true;

            if (foamTexture != null)
                water.foamTexture = new TextureRef(foamTexture);

            water.foamColor = foamColor;
            water.foamIntensity = Mathf.Clamp(intensity, 0f, 4f);
            water.foamEdgeWidthMetres = Mathf.Max(0.01f, edgeWidthMetres);
            water.foamScrollSpeed = scrollSpeed;
            water.foamTileMetres = Mathf.Max(0.1f, tileMetres);

            return water;
        }
    }

    // -------------------------------------------------------------------------
    //  T21 - Caustics
    // -------------------------------------------------------------------------

    /// <summary>
    /// The moving net of light on a shallow lake bed.
    ///
    /// WHAT CAUSTICS ARE
    /// -----------------
    /// A wavy water surface acts as a field of weak lenses. Where the surface is
    /// locally convex it focuses sunlight into a bright line; where concave it
    /// spreads it. The bright lines are caustics, and their characteristic
    /// tangled-net look is the interference of many such lenses, not a single
    /// pattern sliding around.
    ///
    /// WHY TWO LAYERS
    /// --------------
    /// That is the whole reason this node scrolls two copies in opposing
    /// directions and multiplies them. One scrolling layer reads immediately as a
    /// texture being dragged - the eye locks onto a feature and tracks it. Two
    /// layers moving against each other produce a pattern whose features appear
    /// and vanish in place, which is what the real interference does. Cost is one
    /// extra texture fetch, and there is no cheaper way to get the effect.
    ///
    /// DEPTH FADE
    /// ----------
    /// Caustics only reach as far as the light does. Fading them out with depth on
    /// the same absorption curve as the water colour keeps a deep lake from having
    /// a glowing floor - and lets one setting serve both a metre-deep stream and a
    /// twenty-metre lake.
    /// </summary>
    [System.Serializable]
    public class CausticsNode : TextureNode
    {
        public override string NodeName { get { return "Caustics"; } }
        public override NodeCategory Category { get { return NodeCategory.Water; } }
        public override string Tooltip
        {
            get { return "Two counter-scrolling caustic layers on the lake bed, fading out with depth."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Water", PortType.Water, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.WaterOut; }

        [Tooltip("Caustic pattern. A Voronoi-edge or thin-line texture works best.")]
        public Texture2D causticsTexture;

        [Tooltip("Caustic colour. Usually a touch warmer than the sunlight.")]
        public Color causticsColor = new Color(1f, 0.98f, 0.90f, 1f);

        [Tooltip("Brightness. Additive, so above 1 needs HDR to show properly.")]
        public float intensity = 0.6f;

        [Tooltip("World size of one caustic tile, in metres. 2-4 m reads as a real scale.")]
        public float tileMetres = 3f;

        [Tooltip("Drift speed in metres per second. The two layers move in opposite directions.")]
        public float speed = 0.05f;

        [Tooltip("Depth in metres at which caustics have faded out.")]
        public float depthFadeMetres = 8f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            WaterProduct upstream = GetInputWater(tile, 0, stop);
            WaterProduct water = upstream != null ? upstream.Copy() : new WaterProduct();

            water.causticsEnabled = true;

            if (causticsTexture != null)
                water.causticsTexture = new TextureRef(causticsTexture);

            water.causticsColor = causticsColor;
            water.causticsIntensity = Mathf.Clamp(intensity, 0f, 4f);
            water.causticsTileMetres = Mathf.Max(0.1f, tileMetres);
            water.causticsSpeed = speed;
            water.causticsDepthFadeMetres = Mathf.Max(0.01f, depthFadeMetres);

            return water;
        }
    }
}
