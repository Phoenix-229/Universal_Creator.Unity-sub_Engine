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
    // -------------------------------------------------------------------------
    //  T22-T24 - Shoreline / bank band
    // -------------------------------------------------------------------------
    //  Three sibling nodes for the three ways water meets land. They all write the
    //  same WaterProduct shoreline fields; they differ only in the real-world
    //  defaults, and the river one additionally reads the dug trench. Each also
    //  sets the depth-fade scale for its body, because an ocean, a lake and a
    //  stream absorb over very different distances - so dropping one of these after
    //  Water Surface gives a body both its edge band AND its depth scale in one
    //  place. Only the fade DISTANCE is set here; the shallow/deep colours stay
    //  whatever Water Surface authored, so this never silently repaints the water.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The ocean's edge: a wide sand beach at the waterline, and the depth scale of
    /// open water.
    ///
    /// The terrain draws the dry berm itself through the _UC_BEACH_ON feature at
    /// the global sea level; this node describes the WET band the water lays over
    /// that berm - the darker, glossier strip the tide keeps saturated - and sizes
    /// it to a surf beach (tens of metres), so the terrain sand and the water's
    /// shallow tint meet at the same line instead of one cutting across the other.
    /// </summary>
    [System.Serializable]
    public class OceanShorelineNode : TextureNode
    {
        public override string NodeName { get { return "Ocean Shoreline"; } }
        public override NodeCategory Category { get { return NodeCategory.Water; } }
        public override string Tooltip
        {
            get { return "Wide wet sand beach at the waterline plus the depth scale of open ocean."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Water", PortType.Water, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.WaterOut; }

        [Tooltip("Wet sand colour at the waterline. Dries toward the terrain's inland layer.")]
        public Color sandColor = new Color(0.78f, 0.72f, 0.56f, 1f);

        [Tooltip("Sand texture for the beach band. Empty gives flat-coloured sand.")]
        public Texture2D sandTexture;

        [Tooltip("How far the beach reaches inland from the waterline, in metres. Surf beaches are wide.")]
        public float beachWidthMetres = 18f;

        [Tooltip("How much darker the wet sand is at the waterline, 0 to 1.")]
        public float wetDarkening = 0.32f;

        [Tooltip("Wet-sand smoothness. Saturated sand is glossy; it dries to matte across the band.")]
        public float wetSmoothness = 0.5f;

        [Tooltip("Depth in metres over which open ocean fades shallow to deep. One e-folding.")]
        public float depthFadeMetres = 12f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            WaterProduct upstream = GetInputWater(tile, 0, stop);
            WaterProduct water = upstream != null ? upstream.Copy() : new WaterProduct();

            water.shorelineEnabled = true;
            water.shorelineColor = sandColor;
            if (sandTexture != null)
                water.shorelineTexture = new TextureRef(sandTexture);
            water.shorelineWidthMetres = Mathf.Max(0.1f, beachWidthMetres);
            water.shorelineWetDarkening = Mathf.Clamp01(wetDarkening);
            water.shorelineSmoothness = Mathf.Clamp01(wetSmoothness);
            water.bankReadsTrench = false;
            water.depthFadeMetres = Mathf.Max(0.01f, depthFadeMetres);

            return water;
        }
    }

    /// <summary>
    /// A lake's margin: a narrow ring of gravel or mud, and the moderate depth of
    /// still standing water.
    ///
    /// A lake sits at its own altitude, not the global sea level, so the terrain's
    /// beach feature cannot draw its edge - the band is described here and applied
    /// around the body. Still water works its shore far less than surf does, so the
    /// band is a few metres of gravel/mud rather than a wide sand berm, and it is
    /// less glossy because it is worked and settled rather than wave-washed.
    /// </summary>
    [System.Serializable]
    public class LakeShorelineNode : TextureNode
    {
        public override string NodeName { get { return "Lake Shoreline"; } }
        public override NodeCategory Category { get { return NodeCategory.Water; } }
        public override string Tooltip
        {
            get { return "Narrow gravel / mud margin around a lake plus the depth scale of still water."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Water", PortType.Water, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.WaterOut; }

        [Tooltip("Gravel / mud colour at the waterline. Dries toward the terrain's inland layer.")]
        public Color marginColor = new Color(0.50f, 0.47f, 0.40f, 1f);

        [Tooltip("Gravel / mud texture for the margin. Empty gives a flat-coloured band.")]
        public Texture2D marginTexture;

        [Tooltip("How far the margin reaches inland from the waterline, in metres. Lake margins are narrow.")]
        public float marginWidthMetres = 3f;

        [Tooltip("How much darker the wet margin is at the waterline, 0 to 1.")]
        public float wetDarkening = 0.28f;

        [Tooltip("Wet margin smoothness. Settled mud is fairly matte even when wet.")]
        public float wetSmoothness = 0.35f;

        [Tooltip("Depth in metres over which the lake fades shallow to deep. One e-folding.")]
        public float depthFadeMetres = 6f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            WaterProduct upstream = GetInputWater(tile, 0, stop);
            WaterProduct water = upstream != null ? upstream.Copy() : new WaterProduct();

            water.shorelineEnabled = true;
            water.shorelineColor = marginColor;
            if (marginTexture != null)
                water.shorelineTexture = new TextureRef(marginTexture);
            water.shorelineWidthMetres = Mathf.Max(0.1f, marginWidthMetres);
            water.shorelineWetDarkening = Mathf.Clamp01(wetDarkening);
            water.shorelineSmoothness = Mathf.Clamp01(wetSmoothness);
            water.bankReadsTrench = false;
            water.depthFadeMetres = Mathf.Max(0.01f, depthFadeMetres);

            return water;
        }
    }

    /// <summary>
    /// A river's bank: scoured sand that FOLLOWS THE DUG CHANNEL, and the shallow
    /// depth of running water.
    ///
    /// WHY THE BANK READS THE TRENCH
    /// -----------------------------
    /// A river is a channel cut into the ground, not a pond at some altitude. If the
    /// bank were an altitude band - "sand below height H" - it would either miss the
    /// water where the channel runs downhill or paint sand up the surrounding valley
    /// where there is none. The real bank is the wall of the cut itself, so this
    /// node keys the band to the trench the river dug (bankReadsTrench), which the
    /// applier reads from the same trench measurement the channel was carved from.
    /// The result is sand on the channel wall and grass right up to the lip above
    /// it, never grass standing in the water.
    ///
    /// The bank is allowed only where the wall is shallow enough to hold worked
    /// material; a steeper cut bank shows the inland surface, which is what an
    /// undercut river bank actually looks like.
    /// </summary>
    [System.Serializable]
    public class RiverBankNode : TextureNode
    {
        public override string NodeName { get { return "River Bank"; } }
        public override NodeCategory Category { get { return NodeCategory.Water; } }
        public override string Tooltip
        {
            get { return "Scoured sand bank that follows the dug channel, plus the shallow depth of a river."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Water", PortType.Water, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.WaterOut; }

        [Tooltip("Scoured sand colour at the waterline. Dries toward the terrain's inland layer.")]
        public Color bankColor = new Color(0.60f, 0.55f, 0.43f, 1f);

        [Tooltip("Sand / gravel texture for the bank. Empty gives a flat-coloured band.")]
        public Texture2D bankTexture;

        [Tooltip("How far the bank reaches up the channel wall from the waterline, in metres.")]
        public float bankWidthMetres = 2.5f;

        [Tooltip("How much darker the wet bank is at the waterline, 0 to 1.")]
        public float wetDarkening = 0.35f;

        [Tooltip("Wet bank smoothness. Water-scoured sand is glossy at the edge.")]
        public float wetSmoothness = 0.4f;

        [Tooltip("Slope in degrees below which the bank shows. Steeper cut banks reveal the inland surface.")]
        public float bankSlopeLimitDegrees = 60f;

        [Tooltip("Depth in metres over which the river fades shallow to deep. Rivers are shallow; the bed reads through.")]
        public float depthFadeMetres = 2f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            WaterProduct upstream = GetInputWater(tile, 0, stop);
            WaterProduct water = upstream != null ? upstream.Copy() : new WaterProduct();

            water.shorelineEnabled = true;
            water.shorelineColor = bankColor;
            if (bankTexture != null)
                water.shorelineTexture = new TextureRef(bankTexture);
            water.shorelineWidthMetres = Mathf.Max(0.1f, bankWidthMetres);
            water.shorelineWetDarkening = Mathf.Clamp01(wetDarkening);
            water.shorelineSmoothness = Mathf.Clamp01(wetSmoothness);
            water.bankSlopeLimitDegrees = Mathf.Clamp(bankSlopeLimitDegrees, 1f, 89f);
            water.bankReadsTrench = true;
            water.depthFadeMetres = Mathf.Max(0.01f, depthFadeMetres);

            return water;
        }
    }
}
