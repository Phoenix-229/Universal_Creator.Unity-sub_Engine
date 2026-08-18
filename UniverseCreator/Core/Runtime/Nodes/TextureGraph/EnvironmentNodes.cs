// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    EnvironmentNodes.cs
//  Purpose: Texture Graph T8-T11 plus the Terrain Height Source that T8 and T11
//           both read from. Weather and light acting on the ground, rather than
//           the ground itself.
//
//  REAL-WORLD MATH, NOT PLAUSIBLE-LOOKING MATH
//  -------------------------------------------
//  Each of these implements a measured physical relationship rather than a
//  hand-tuned curve, because the parameters then mean something a level designer
//  can look up:
//
//    Snow line       - the environmental lapse rate, 6.5 degC per 1000 m, is what
//                      sets the altitude where mean temperature crosses freezing.
//    Snow adhesion   - snow stops holding somewhere around 50-60 degrees of
//                      slope; above that it avalanches off within hours.
//    Wet darkening   - a water film raises the effective index of refraction at
//                      the surface, so more light that enters the substrate is
//                      internally reflected back into it and absorbed. Albedo
//                      drops and specular rises together; they are one effect.
//    Ambient occlusion - the fraction of the sky hemisphere a point can see,
//                      estimated by horizon raymarching, which is the same
//                      quantity a baked AO map stores.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  Terrain Height Source - the bridge between the two graphs
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the live terrain heightmap into a Matrix the texture graph can use.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// Two texture nodes need real elevation data rather than a shader uniform:
    /// the occlusion baker raymarches it, and the snow node bakes a traction mask
    /// from it. Neither can be done from inside the fragment shader, and the
    /// texture graph has no heightfield of its own.
    ///
    /// It reads a Terrain the user points it at rather than guessing from the
    /// scene, because a project may hold several worlds and picking "the first
    /// one found" produces a graph whose output depends on hierarchy order.
    ///
    /// MAIN THREAD ONLY. TerrainData.GetHeights is a Unity API call. That is
    /// legal here and nowhere in the height graph, which is precisely the
    /// difference between the two graphs.
    /// </summary>
    [System.Serializable]
    public class TerrainHeightSourceNode : TextureNode
    {
        public override string NodeName { get { return "Terrain Height Source"; } }
        public override string Tooltip
        {
            get { return "Reads a live Terrain's heightmap so occlusion and snow masks can be baked from it."; }
        }

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return Outs; }

        [Tooltip("The terrain to read. Drag one in from the hierarchy.")]
        public UnityEngine.Terrain terrain;

        [Tooltip("Downsample factor. 2 reads every second sample; 4 every fourth. Raise it for speed on huge terrains.")]
        public int downsample = 1;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (terrain == null || terrain.terrainData == null) return null;

            TerrainData data = terrain.terrainData;
            int step = Mathf.Clamp(downsample, 1, 16);

            int srcRes = data.heightmapResolution;
            int res = Mathf.Max(2, (srcRes - 1) / step + 1);

            // GetHeights returns [z, x] normalised 0-1, which is already the
            // Matrix convention. No rescaling, so no place for a factor to go
            // missing between the two representations.
            float[,] heights = data.GetHeights(0, 0, srcRes, srcRes);

            Matrix m = new Matrix(new CoordRect(new Coord(0, 0), new Coord(res, res)));
            m.worldPos = terrain.transform.position;
            m.worldSize = data.size;
            m.worldHeight = data.size.y;

            for (int z = 0; z < res; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                int sz = Mathf.Min(z * step, srcRes - 1);
                int row = z * res;

                for (int x = 0; x < res; x++)
                {
                    int sx = Mathf.Min(x * step, srcRes - 1);
                    m.arr[row + x] = heights[sz, sx];
                }
            }

            return m;
        }
    }

    // -------------------------------------------------------------------------
    //  T8 - Snow Cover
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lays snow above an altitude and below a slope angle, over ground and cliff
    /// alike.
    ///
    /// THE PHYSICS THE DEFAULTS COME FROM
    /// ----------------------------------
    /// The environmental lapse rate is about 6.5 degC per 1000 m of altitude. A
    /// permanent snow line therefore sits where sea-level mean temperature minus
    /// 6.5 * (h / 1000) crosses 0 degC. At a 15 degC sea-level mean that is
    /// roughly 2300 m, which is why the default altitude is 2000 m rather than a
    /// round fraction of the terrain height - it is a real number for a temperate
    /// world, and Snowline Solver in the climate set can drive it per latitude.
    ///
    /// The slope limit is not decoration either. Snow does not accumulate past
    /// roughly 50-60 degrees; beyond that the shear stress in the pack exceeds
    /// what the bond to the rock can hold and it sloughs off. That is why real
    /// mountains show bare rock stripes down their steepest faces, and why a
    /// uniformly white peak reads as fake immediately.
    ///
    /// SECOND OUTPUT
    /// -------------
    /// The snow mask is also exported as a Matrix so the physics layer can lower
    /// ground friction where snow lies. The shader cannot supply that - the CPU
    /// needs the value to answer a raycast - so it is baked here from the same
    /// altitude and slope rule the shader uses. Both sides then agree by
    /// construction rather than by two similar-looking formulas.
    /// </summary>
    [System.Serializable]
    public class SnowCoverNode : TextureNode
    {
        public override string NodeName { get { return "Snow Cover"; } }
        public override NodeCategory Category { get { return NodeCategory.Surface; } }
        public override string Tooltip
        {
            get { return "Snow above an altitude and below a slope angle. Also exports a traction mask for physics."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input),
            new Port("Height",  PortType.Matrix,  PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Surface",   PortType.Surface, PortDirection.Output, true),
            new Port("Snow Mask", PortType.Matrix,  PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        [Tooltip("Snow texture. Leave empty to use the snow colour alone.")]
        public Texture2D snowTexture;

        [Tooltip("Normal map for the snow surface. Optional.")]
        public Texture2D snowNormal;

        [Tooltip("Snow colour. Real snow is slightly blue in shadow, not pure white.")]
        public Color snowColor = new Color(0.92f, 0.94f, 0.98f, 1f);

        [Tooltip("World height in metres where snow begins. 2000 m is the temperate permanent snow line.")]
        public float snowLineAltitude = 2000f;

        [Tooltip("Metres over which snow fades in above the line. Wider reads as a gentler climate.")]
        public float falloffMetres = 150f;

        [Tooltip("Slope in degrees past which snow slides off. Real limit is 50-60.")]
        public float slopeLimitDegrees = 52f;

        [Tooltip("Degrees over which the slope cutoff softens. Avoids a hard stencil edge.")]
        public float slopeFalloffDegrees = 8f;

        [Tooltip("Overall amount. 1 is a full winter, 0.3 a light dusting.")]
        public float coverage = 1f;

        [Tooltip("Fresh snow is smooth; old compacted snow is not. 0.55 suits a settled pack.")]
        public float smoothness = 0.55f;

        [Tooltip("Depth of the snow normal map.")]
        public float normalStrength = 0.6f;

        [Tooltip("World size of one snow tile, in metres. Snow is projected triplanar so it holds on cliffs.")]
        public float triplanarScaleMetres = 6f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex == 1) return BuildTractionMask(tile, stop);

            SurfaceProduct upstream = GetInputSurface(tile, 0, stop);
            SurfaceProduct surface = upstream != null ? upstream.Copy() : new SurfaceProduct();

            // Snow is one of only two features expensive enough to earn a real
            // shader keyword - six extra texture fetches. EnableFeature sets the
            // uniform and the keyword together, which is the desync that
            // otherwise makes a tick box look broken.
            surface.EnableFeature(UCShaderProps.EnableSnow, UCShaderProps.KeywordSnow);

            if (snowTexture != null)
                surface.SetTexture(UCShaderProps.SnowTex, new TextureRef(snowTexture));
            if (snowNormal != null)
                surface.SetTexture(UCShaderProps.SnowNormal, new TextureRef(snowNormal));

            surface.SetColor(UCShaderProps.SnowColor, snowColor);
            surface.SetFloat(UCShaderProps.SnowAltitude, snowLineAltitude);
            surface.SetFloat(UCShaderProps.SnowFalloff, Mathf.Max(1f, falloffMetres));
            surface.SetFloat(UCShaderProps.SnowSlopeLimit, Mathf.Clamp(slopeLimitDegrees, 0f, 90f));
            surface.SetFloat(UCShaderProps.SnowSlopeFalloff, Mathf.Max(0.5f, slopeFalloffDegrees));
            surface.SetFloat(UCShaderProps.SnowCoverage, Mathf.Clamp01(coverage));
            surface.SetFloat(UCShaderProps.SnowSmoothness, Mathf.Clamp01(smoothness));
            surface.SetFloat(UCShaderProps.SnowNormalStrength, Mathf.Clamp(normalStrength, 0f, 4f));
            surface.SetFloat(UCShaderProps.SnowTriplanarScale, Mathf.Max(0.01f, triplanarScaleMetres));

            if (snowTexture == null)
                surface.Notice("Snow Cover has no texture; snow renders as flat colour.");

            return surface;
        }

        /// <summary>
        /// Bakes the same altitude and slope rule the shader runs, as a 0-1 mask
        /// the physics layer can sample. Returns null when no height is wired in -
        /// a null product is honest about there being nothing to compute from.
        /// </summary>
        private Matrix BuildTractionMask(TileData tile, StopToken stop)
        {
            Matrix height = GetInputMap(tile, 1, stop);
            if (height == null) return null;

            Matrix mask = height.Blank();

            int w = height.rect.size.x;
            int h = height.rect.size.z;

            float metresPerHeightUnit = height.worldHeight > 0f ? height.worldHeight : 1f;
            float mppX = height.PixelSize.x;
            float mppZ = height.PixelSize.y;
            float baseY = height.worldPos.y;

            float fall = Mathf.Max(1f, falloffMetres);
            float slopeLimit = Mathf.Clamp(slopeLimitDegrees, 0f, 90f);
            float slopeFall = Mathf.Max(0.5f, slopeFalloffDegrees);
            float cover = Mathf.Clamp01(coverage);

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                int row = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = row + x;

                    float worldY = baseY + height.arr[i] * metresPerHeightUnit;

                    // Central differences on the interior, one-sided at the edge.
                    // Clamping the index instead of skipping keeps the mask the
                    // same size as the heightmap, so the physics layer can index
                    // it without an offset table.
                    int xm = x > 0 ? x - 1 : x;
                    int xp = x < w - 1 ? x + 1 : x;
                    int zm = z > 0 ? z - 1 : z;
                    int zp = z < h - 1 ? z + 1 : z;

                    float dhx = (height.arr[row + xp] - height.arr[row + xm]) * metresPerHeightUnit;
                    float dhz = (height.arr[zp * w + x] - height.arr[zm * w + x]) * metresPerHeightUnit;

                    float runX = (xp - xm) * mppX;
                    float runZ = (zp - zm) * mppZ;
                    if (runX < 1e-6f) runX = 1e-6f;
                    if (runZ < 1e-6f) runZ = 1e-6f;

                    float gx = dhx / runX;
                    float gz = dhz / runZ;

                    // Slope of the steepest descent, in degrees. atan of the
                    // gradient magnitude - the same quantity the shader gets from
                    // the geometric normal, arrived at from the other side.
                    float slopeDeg = Mathf.Atan(Mathf.Sqrt(gx * gx + gz * gz)) * Mathf.Rad2Deg;

                    float alt = InterpolationMath.Clamp01(
                        (worldY - snowLineAltitude + fall) / (2f * fall));

                    float slopeTerm = 1f - InterpolationMath.SmoothStep(
                        slopeLimit - slopeFall, slopeLimit + slopeFall, slopeDeg);

                    mask.arr[i] = InterpolationMath.Clamp01(alt * slopeTerm * cover);
                }
            }

            return mask;
        }
    }

    // -------------------------------------------------------------------------
    //  T9 - Wetness
    // -------------------------------------------------------------------------

    /// <summary>
    /// Darkens and polishes the ground, and pools water in low flat hollows.
    ///
    /// WHY DARKER AND SHINIER ARE ONE SETTING
    /// --------------------------------------
    /// A wet surface is not a dry surface with a gloss layer painted on. Water
    /// fills the microscopic roughness, so light entering the substrate meets a
    /// water-air boundary on the way out instead of a rock-air one. More of it is
    /// totally internally reflected back into the material, gets another chance to
    /// be absorbed, and never reaches the eye. The surface darkens.
    ///
    /// The same film that causes the darkening is also flat, so the specular lobe
    /// tightens. Darkening and smoothness rise together because they are the same
    /// physical event, which is why this node exposes both and why turning one up
    /// without the other looks wrong.
    ///
    /// Puddles are separate: standing water needs somewhere to stand. The shader
    /// pools it where the ground is below the puddle level, is concave by the mask
    /// height channel, and is not steep - water does not sit on a slope.
    /// </summary>
    [System.Serializable]
    public class WetnessNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Wetness"; } }
        public override string Tooltip
        {
            get { return "Rain darkening and specular boost, with puddles in low flat hollows."; }
        }

        [Tooltip("Overall wetness, 0 dry to 1 soaked. Drive this from the weather rain mask.")]
        public float wetnessAmount = 0.5f;

        [Tooltip("How much the albedo drops when soaked. 0.3 to 0.4 matches measured wet soil.")]
        public float darkening = 0.35f;

        [Tooltip("Smoothness a fully wet surface reaches. Standing water is around 0.95.")]
        public float wetSmoothness = 0.8f;

        [Tooltip("Extra gloss on standing water only, over and above the wet smoothness.")]
        public float specularBoost = 0.15f;

        [Tooltip("How deep a hollow must be, in mask height units, before water collects.")]
        public float puddleThreshold = 0.35f;

        [Tooltip("World height in metres below which puddles may form. Set near the water table.")]
        public float puddleLevel;

        [Tooltip("Metres over which puddling fades out above the level.")]
        public float puddleFalloff = 2f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.EnableFeature(UCShaderProps.EnableWetness);
            surface.SetFloat(UCShaderProps.WetnessAmount, Mathf.Clamp01(wetnessAmount));
            surface.SetFloat(UCShaderProps.WetDarkening, Mathf.Clamp01(darkening));
            surface.SetFloat(UCShaderProps.WetSmoothness, Mathf.Clamp01(wetSmoothness));
            surface.SetFloat(UCShaderProps.WetSpecBoost, Mathf.Clamp01(specularBoost));
            surface.SetFloat(UCShaderProps.PuddleThreshold, Mathf.Clamp01(puddleThreshold));
            surface.SetFloat(UCShaderProps.PuddleLevel, puddleLevel);
            surface.SetFloat(UCShaderProps.PuddleFalloff, Mathf.Max(0.01f, puddleFalloff));
        }
    }

    // -------------------------------------------------------------------------
    //  T10 - Mud and Dirt
    // -------------------------------------------------------------------------

    /// <summary>
    /// Collects mud in low, flat, concave ground.
    ///
    /// Mud is a transport phenomenon: fine sediment washes downhill and settles
    /// where the water slows. That means the places mud belongs are exactly the
    /// places with low slope and negative curvature - the bottom of a hollow, not
    /// the side of it. The shader tests both, which is why this node has a slope
    /// limit at all rather than a plain height threshold.
    ///
    /// It runs BEFORE the cliff blend in the shader, deliberately. Running it
    /// after would let mud smear up vertical rock faces, where in the world it
    /// would simply have run off.
    /// </summary>
    [System.Serializable]
    public class MudDirtNode : SurfaceFeatureNode
    {
        public override string NodeName { get { return "Mud and Dirt"; } }
        public override string Tooltip
        {
            get { return "Mud gathering in low, flat, concave ground. Runs before the cliff blend so it cannot climb."; }
        }

        [Tooltip("Mud texture, projected triplanar. Leave empty for flat colour.")]
        public Texture2D mudTexture;

        [Tooltip("Mud colour. Wet earth is a desaturated warm brown, not orange.")]
        public Color mudColor = new Color(0.24f, 0.19f, 0.14f, 1f);

        [Tooltip("World size of one mud tile, in metres.")]
        public float scaleMetres = 3f;

        [Tooltip("How concave the ground must be before mud settles.")]
        public float threshold = 0.45f;

        [Tooltip("Softness of the mud edge. 0 is a stencil, 1 a wide smear.")]
        public float blend = 0.3f;

        [Tooltip("Wet mud is glossy. 0.5 to 0.7 reads correctly under a sky light.")]
        public float smoothness = 0.6f;

        [Tooltip("Slope in degrees past which mud cannot hold. Real mud stops well under 25.")]
        public float slopeLimitDegrees = 18f;

        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            surface.EnableFeature(UCShaderProps.EnableMud);

            if (mudTexture != null)
                surface.SetTexture(UCShaderProps.MudTex, new TextureRef(mudTexture));

            surface.SetColor(UCShaderProps.MudColor, mudColor);
            surface.SetFloat(UCShaderProps.MudScale, Mathf.Max(0.01f, scaleMetres));
            surface.SetFloat(UCShaderProps.MudThreshold, Mathf.Clamp01(threshold));
            surface.SetFloat(UCShaderProps.MudBlend, Mathf.Clamp01(blend));
            surface.SetFloat(UCShaderProps.MudSmoothness, Mathf.Clamp01(smoothness));
            surface.SetFloat(UCShaderProps.MudSlopeLimit, Mathf.Clamp(slopeLimitDegrees, 0f, 90f));
        }
    }

    // -------------------------------------------------------------------------
    //  T11 - Occlusion Map
    // -------------------------------------------------------------------------

    /// <summary>
    /// Bakes an ambient occlusion map by raymarching the heightfield.
    ///
    /// WHAT IS BEING COMPUTED
    /// ----------------------
    /// Ambient occlusion is the fraction of the sky hemisphere a point can see.
    /// For a heightfield that reduces to a horizon problem: march outward in a
    /// ring of directions, track the highest angle the terrain rises to along
    /// each, and the visible sky is what is left above those horizons.
    ///
    ///     for each direction d:
    ///         h_max(d) = max over steps of atan((height(p + t*d) - height(p)) / t)
    ///     AO = 1 - mean over d of sin(max(0, h_max(d)))
    ///
    /// sin of the horizon angle, not the angle itself, because the sky is a
    /// hemisphere and the solid angle a horizon of elevation a occludes is
    /// proportional to sin(a). Using the raw angle over-darkens shallow valleys
    /// and under-darkens deep ones - the classic "everything looks dusty" AO.
    ///
    /// COST AND WHY IT IS A BAKE
    /// -------------------------
    /// Directions x steps ray samples per pixel. At the defaults that is 8 x 12,
    /// so a 1025-square heightmap is about 100 million samples. That is fine once,
    /// on the CPU, at author time - and impossible per frame, which is why this
    /// is the only texture node that produces a real baked map rather than shader
    /// parameters.
    ///
    /// It runs on flat float[] through Matrix, the same managed path the height
    /// graph uses, and honours the stop token per row so a cancel is responsive.
    /// </summary>
    [System.Serializable]
    public class OcclusionMapNode : TextureNode
    {
        public override string NodeName { get { return "Occlusion Map"; } }
        public override NodeCategory Category { get { return NodeCategory.Surface; } }
        public override string Tooltip
        {
            get { return "Bakes an AO map by horizon-marching the heightfield. Wire a Terrain Height Source in."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input),
            new Port("Height",  PortType.Matrix,  PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.SurfaceOut; }

        [Tooltip("How far each ray searches, in metres. Larger radius finds valley-scale shadowing.")]
        public float radiusMetres = 40f;

        [Tooltip("Ray directions per pixel. 8 is smooth enough; 4 shows a faint star pattern.")]
        public int directions = 8;

        [Tooltip("Samples along each ray. More steps catch thin ridges that a coarse march walks over.")]
        public int steps = 12;

        [Tooltip("How dark the occlusion goes. 1 is the physical result; above that is stylised.")]
        public float intensity = 1f;

        [Tooltip("Strength of the baked map once in the shader, separate from the bake itself.")]
        public float mapStrength = 1f;

        [Tooltip("Strength of the per-layer mask AO. Independent of the baked map.")]
        public float layerOcclusionStrength = 1f;

        [Tooltip("Extra darkening in the crevices read from each layer's mask height channel.")]
        public float cavityStrength = 0.5f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            SurfaceProduct upstream = GetInputSurface(tile, 0, stop);
            SurfaceProduct surface = upstream != null ? upstream.Copy() : new SurfaceProduct();

            surface.SetFloat(UCShaderProps.OcclusionStrength, Mathf.Clamp01(layerOcclusionStrength));
            surface.SetFloat(UCShaderProps.CavityStrength, Mathf.Clamp01(cavityStrength));

            Matrix height = GetInputMap(tile, 1, stop);
            if (height == null)
            {
                // The layer and cavity terms above still work without a heightmap;
                // only the baked map needs one. Saying so beats silently doing
                // two thirds of the job.
                surface.Notice("Occlusion Map has no Height input; only layer AO and cavity are applied.");
                return surface;
            }

            Matrix ao = Bake(height, stop);
            if (ao == null) return surface;

            surface.EnableFeature(UCShaderProps.EnableAOMap);
            surface.SetBakedMap(UCShaderProps.AOMap, ao);
            surface.SetFloat(UCShaderProps.AOMapStrength, Mathf.Clamp01(mapStrength));

            return surface;
        }

        private Matrix Bake(Matrix height, StopToken stop)
        {
            int w = height.rect.size.x;
            int h = height.rect.size.z;
            if (w < 2 || h < 2) return null;

            Matrix ao = height.Blank();

            int dirCount = Mathf.Clamp(directions, 2, 32);
            int stepCount = Mathf.Clamp(steps, 2, 64);
            float radius = Mathf.Max(0.1f, radiusMetres);
            float power = Mathf.Max(0f, intensity);

            float mppX = height.PixelSize.x;
            float mppZ = height.PixelSize.y;
            float metresPerUnit = height.worldHeight > 0f ? height.worldHeight : 1f;

            // Precompute the ring once. Recomputing sin and cos inside the pixel
            // loop would be a few hundred million transcendentals for a result
            // that never changes.
            float[] dirX = new float[dirCount];
            float[] dirZ = new float[dirCount];
            for (int d = 0; d < dirCount; d++)
            {
                float a = (d / (float)dirCount) * Mathf.PI * 2f;
                dirX[d] = Mathf.Cos(a);
                dirZ[d] = Mathf.Sin(a);
            }

            float stepMetres = radius / stepCount;
            float invDirCount = 1f / dirCount;

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                int row = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    float originY = height.arr[i] * metresPerUnit;

                    float occlusion = 0f;

                    for (int d = 0; d < dirCount; d++)
                    {
                        float ddx = dirX[d];
                        float ddz = dirZ[d];

                        // Track the tangent of the horizon angle, not the angle.
                        // atan is monotonic, so the maximum is at the same place,
                        // and one atan at the end replaces stepCount of them.
                        float maxTan = 0f;

                        for (int s = 1; s <= stepCount; s++)
                        {
                            float dist = s * stepMetres;

                            float sx = x + ddx * dist / mppX;
                            float sz = z + ddz * dist / mppZ;

                            if (sx < 0f || sz < 0f || sx > w - 1 || sz > h - 1) break;

                            float sampleY = SampleBilinear(height, sx, sz, w, h) * metresPerUnit;

                            float rise = sampleY - originY;
                            if (rise <= 0f) continue;

                            float tan = rise / dist;
                            if (tan > maxTan) maxTan = tan;
                        }

                        if (maxTan <= 0f) continue;

                        // sin(atan(t)) = t / sqrt(1 + t^2). The identity avoids
                        // both transcendentals and is exact, not an approximation.
                        occlusion += maxTan / Mathf.Sqrt(1f + maxTan * maxTan);
                    }

                    float visibility = 1f - occlusion * invDirCount * power;
                    ao.arr[i] = InterpolationMath.Clamp01(visibility);
                }
            }

            return ao;
        }

        // Local bilinear rather than Matrix.GetInterpolated: the ray march works
        // in local pixel coordinates and already knows they are in range, so the
        // rect-offset arithmetic and bounds test would be paid a hundred million
        // times for nothing.
        private static float SampleBilinear(Matrix m, float fx, float fz, int w, int h)
        {
            int x0 = (int)fx;
            int z0 = (int)fz;
            if (x0 > w - 2) x0 = w - 2;
            if (z0 > h - 2) z0 = h - 2;
            if (x0 < 0) x0 = 0;
            if (z0 < 0) z0 = 0;

            float tx = fx - x0;
            float tz = fz - z0;

            int r0 = z0 * w;
            int r1 = r0 + w;

            float v00 = m.arr[r0 + x0];
            float v10 = m.arr[r0 + x0 + 1];
            float v01 = m.arr[r1 + x0];
            float v11 = m.arr[r1 + x0 + 1];

            return InterpolationMath.Bilinear(v00, v10, v01, v11, tx, tz);
        }
    }
}
