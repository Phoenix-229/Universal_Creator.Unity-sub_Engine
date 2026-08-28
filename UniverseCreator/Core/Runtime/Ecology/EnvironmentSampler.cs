// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    EnvironmentSampler.cs
//  Purpose: Turns the graph's own surface products into the environment record
//           the ecology layer reads, for one point on the ground.
//
//  The rule this file exists to enforce: vegetation reads what the terrain
//  already decided, and never re-runs noise of its own. There is no `new Noise(`
//  anywhere below, and there must never be one. A second noise field would drift
//  from the first the moment either was retuned, and the forest would stop
//  agreeing with the ground it stands on.
//
//  Two corrections to the plan this task was written from, both found by reading
//  the shipped nodes rather than trusting the description:
//
//    1. The temperature channel is NOT an ecology input. TemperatureMapNode
//       documents its output as "normalised 0-1, not degrees", with its own lapse
//       rate baked in as a fraction of that range. Ecology needs real degrees -
//       the treeline gate is 6.4 degC of warmest month, growing degree days
//       accumulate above 5 degC - and a 0-1 mask cannot be fed to either. Worse,
//       adding a mask that already contains elevation on top of a lapse-adjusted
//       temperature would count altitude twice. So the thermal fields come from
//       physics: sea-level mean, the real lapse rate in degC/km, the annual
//       swing, and the aspect bias. The mask stays what it was built to be, a
//       selector for splatmaps.
//
//    2. The height channel arrives as a Matrix, not as OutputBag.heightmap. The
//       bag's copy is the normalised form written to TerrainData and carries no
//       world placement; a Matrix carries worldPos, worldSize and worldHeight, so
//       it can answer a question asked in absolute world metres - which is what
//       determinism requires the question to be asked in.
//
//  Every field of the record is filled from a read, a derivation, or a stated
//  default. None is left at zero by omission, because a zero that means "no data"
//  is indistinguishable from a zero that means "sea level, no slope, no salt" -
//  and that is how a forest ends up covering an ocean.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Which inputs the sampler was given, and which it had to do without.
    ///
    /// Reported rather than silently absorbed: the vegetation inspector shows a
    /// warning row for every bit set here, so a graph wired without a heightmap
    /// says so instead of quietly growing a flat meadow at sea level.
    /// </summary>
    [System.Flags]
    public enum EnvironmentChannel
    {
        None = 0,

        /// <summary>The surface itself. Without it there is no altitude, slope or aspect.</summary>
        Height = 1 << 0,

        /// <summary>The 0-1 moisture mask from the shipped climate nodes.</summary>
        Moisture = 1 << 1,

        /// <summary>Biome classification. Absent means every species is judged on climate alone.</summary>
        Biome = 1 << 2,

        /// <summary>Water bodies. Absent means no shore, no salt and no water table.</summary>
        Water = 1 << 3,

        /// <summary>
        /// The per-chunk derived fields - flow accumulation, wetness index and
        /// topographic exposure. Absent means each falls back to the local
        /// estimate the 3x3 stencil alone can support.
        /// </summary>
        Derivatives = 1 << 4,
    }

    /// <summary>
    /// Fills one <see cref="EnvironmentSample"/> per point.
    ///
    /// Bind once per chunk on the main thread, then Sample as often as needed
    /// from anywhere: after binding, the sampler holds nothing but plain arrays
    /// and value types, so the scatter pass may drive it from a worker thread.
    /// It keeps no state that a Sample call mutates, so two threads may sample
    /// the same bound sampler at once.
    /// </summary>
    public sealed class EnvironmentSampler
    {
        // MoistureMapNode.baseMoisture is 0.5, so a mask sitting at 0.5 means
        // "the world's average", not "half as wet as possible". Precipitation is
        // anchored to that: the mask scales the world's annual figure about its
        // midpoint, so 0.5 reproduces it, 1.0 doubles it and 0 is desert. This is
        // an interface fact about that node, not a constant of physics, which is
        // why it lives here and not in EcoConstants.
        private const float MoistureMaskMidpoint = 0.5f;

        // Fire needs fuel that dries out. Past this aridity the site is wet
        // enough that a fire will not carry, and the return interval is reported
        // as effectively never rather than as an ever-larger number.
        private const float FireNeverReturnsYears = 100000f;

        // How the biome-boundary probe is shaped. Eight compass directions on two
        // rings is sixteen nearest-cell reads of an array that is already in memory,
        // which is cheap enough to run per instance with no gate. The count also sets
        // how finely the transition is graded - sixteen probes give seventeen
        // distinguishable shares across the band, about a metre apart at the shipped
        // 12 m width, and the per-position hash in the weighted draw dithers the rest.
        private const int BiomeProbeDirections = 8;
        private const int BiomeProbeRings = 2;

        // The eight directions as unit vectors, built once. Cheaper than sixteen
        // sin/cos pairs per instance, and the diagonals are normalised so every probe
        // on a ring sits the same distance out - an unnormalised diagonal would reach
        // 1.41 times further and widen the band along the diagonals only.
        private static readonly Vector2[] BiomeProbeDirs = BuildProbeDirs();

        private static Vector2[] BuildProbeDirs()
        {
            Vector2[] dirs = new Vector2[BiomeProbeDirections];

            for (int i = 0; i < BiomeProbeDirections; i++)
            {
                float angle = i * (Mathf.PI * 2f / BiomeProbeDirections);
                dirs[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }

            return dirs;
        }

        private EcoSampleSettings settings;

        private Matrix height;
        private Matrix moisture;
        private Matrix biome;

        private WaterProbe[] water;
        private int waterCount;

        private TerrainDerivatives derivatives;

        // Attached rather than bound, and deliberately not a parameter of Bind: the
        // grid is filled progressively as stems are placed, long after the height
        // field it was bound against. It is a live object shared with the placement
        // pass, which is why sampling the same point twice during a bake can legally
        // return different light - the wood grew in between.
        private CanopyLightGrid canopy;

        // The chunk's climate normals (W-09), read from the graph's OutputBag. Like
        // the canopy grid it is attached rather than a Bind parameter and null is the
        // benign default: no weather branch produced a year, so the resolved settings
        // stand in exactly as they did before the weather module existed. When it IS
        // present the annual precipitation baseline is taken from it, so the forest
        // reads the same annual total the weather physics derived rather than the
        // manager's flat authored figure.
        private UniverseCreator.Weather.ClimateNormals climate;

        private float stepM;
        private EnvironmentChannel missing;
        private bool bound;

        /// <summary>Inputs the current binding lacks. Empty when everything was supplied.</summary>
        public EnvironmentChannel MissingOutputs { get { return missing; } }

        /// <summary>True once Bind has been called. Sampling before that returns a stated-default record.</summary>
        public bool IsBound { get { return bound; } }

        /// <summary>
        /// Points the sampler at the chunk's canopy grid (V-16). Passing null leaves
        /// every site in full daylight, which is what a graph with no vegetation
        /// manager should see.
        /// </summary>
        public void AttachCanopy(CanopyLightGrid grid)
        {
            canopy = grid;
        }

        /// <summary>
        /// Points the sampler at the chunk's climate normals from the graph's
        /// OutputBag (W-09/W-58). Passing null leaves the sampler on the resolved
        /// settings, which is what a graph with no weather branch produces - so a
        /// world that never fills the normals behaves exactly as it did before.
        /// </summary>
        public void AttachClimate(UniverseCreator.Weather.ClimateNormals chunkClimate)
        {
            climate = chunkClimate;
        }

        /// <summary>The chunk climate the sampler is reading, or null when it is on the resolved settings.</summary>
        public UniverseCreator.Weather.ClimateNormals ChunkClimate { get { return climate; } }

        /// <summary>
        /// Points the sampler at one chunk's worth of graph products.
        ///
        /// Call on the main thread. Any of the products may be null, and each
        /// null sets its bit in <see cref="MissingOutputs"/> rather than throwing
        /// - a half-wired graph should report what it is missing and carry on,
        /// because a generation path that throws leaves the terrain in whatever
        /// half-built state it had reached.
        /// </summary>
        public void Bind(
            in EcoSampleSettings sampleSettings,
            Matrix heightMatrix,
            Matrix moistureMatrix,
            Matrix biomeMatrix,
            WaterProbe[] waterProbes,
            int waterProbeCount,
            TerrainDerivatives chunkDerivatives)
        {
            settings = sampleSettings;
            height = heightMatrix;
            moisture = moistureMatrix;
            biome = biomeMatrix;
            water = waterProbes;
            waterCount = waterProbes != null ? Mathf.Min(waterProbeCount, waterProbes.Length) : 0;
            derivatives = chunkDerivatives;

            missing = MissingFrom(height, moisture, biome, waterCount, derivatives);

            // The stencil steps one heightmap cell, so slope is measured at the
            // resolution the ground was actually built at. Measuring finer would
            // read the bilinear filter's own facets; measuring coarser would
            // smooth away the break of slope that decides a treeline.
            stepM = 1f;
            if (height != null)
            {
                Vector2 pixel = height.PixelSize;
                stepM = Mathf.Max(pixel.x, Mathf.Epsilon);
            }

            bound = true;
        }

        /// <summary>
        /// Which of a bake's inputs are absent, from the products themselves.
        ///
        /// Extracted from <see cref="Bind"/> so the pre-build validation panel (V-51)
        /// can ask exactly the question a bake asks, from the shared context, without
        /// running a bake to find out. One rule, two callers: if the two ever disagreed
        /// the inspector would clear a graph that the sampler then treats as half-wired,
        /// which is the silent empty world V-51 exists to prevent.
        ///
        /// The height matrix is per chunk and does not exist until a cell is dispatched,
        /// so a caller with no heightfield yet passes null and reads the Height bit as
        /// "not answerable here" rather than as a defect.
        /// </summary>
        public static EnvironmentChannel MissingFrom(
            Matrix heightMatrix,
            Matrix moistureMatrix,
            Matrix biomeMatrix,
            int waterProbeCount,
            TerrainDerivatives chunkDerivatives)
        {
            EnvironmentChannel absent = EnvironmentChannel.None;

            if (heightMatrix == null) absent |= EnvironmentChannel.Height;
            if (moistureMatrix == null) absent |= EnvironmentChannel.Moisture;
            if (biomeMatrix == null) absent |= EnvironmentChannel.Biome;
            if (waterProbeCount <= 0) absent |= EnvironmentChannel.Water;

            if (chunkDerivatives == null || !chunkDerivatives.IsBuilt)
                absent |= EnvironmentChannel.Derivatives;

            return absent;
        }

        /// <summary>Releases the bound products so a finished chunk's arrays can be collected.</summary>
        public void Unbind()
        {
            height = null;
            moisture = null;
            biome = null;
            water = null;
            waterCount = 0;
            derivatives = null;
            canopy = null;
            climate = null;
            bound = false;
        }

        /// <summary>
        /// One point's environment, in absolute world coordinates.
        ///
        /// Absolute on purpose. A chunk-local position would make the answer
        /// depend on which chunk asked, and the same tree would then appear in
        /// one chunk and not in its neighbour along the seam.
        /// </summary>
        public EnvironmentSample Sample(float worldX, float worldZ)
        {
            EnvironmentSample s = new EnvironmentSample();

            ReadStencil(worldX, worldZ, out HeightStencil stencil, out float groundY);

            s.worldPos = new Vector3(worldX, groundY, worldZ);
            s.altitudeM = groundY - settings.seaLevelY;
            s.latitudeDeg = settings.latitudeDeg;

            TopographyMath.SlopeAspect(in stencil, out s.slopeDeg, out s.aspectDeg);
            s.curvature = TopographyMath.Convexity(in stencil);
            s.normal = NormalFrom(in stencil);

            FillWater(worldX, worldZ, groundY, ref s);
            FillThermal(ref s);
            FillWaterBalance(worldX, worldZ, ref s);
            FillSoil(ref s);
            FillDisturbance(ref s);
            FillBiome(worldX, worldZ, ref s);
            FillLight(worldX, worldZ, ref s);

            return s;
        }

        /// <summary>
        /// The three light figures, from whatever leaf area has been deposited so far
        /// (V-16). No grid means nothing has been placed above this point yet, and
        /// full daylight is then the truthful answer rather than a placeholder.
        /// </summary>
        private void FillLight(float worldX, float worldZ, ref EnvironmentSample s)
        {
            if (canopy == null || !canopy.IsBound)
            {
                s.subcanopyLightFraction = 1f;
                s.canopyLightFraction = 1f;
                s.floorLightFraction = 1f;
                s.canopyLai = 0f;
                return;
            }

            s.subcanopyLightFraction = canopy.LightReaching(worldX, worldZ, CanopyLayer.Canopy);
            s.canopyLightFraction = canopy.LightReaching(worldX, worldZ, CanopyLayer.Understory);
            s.floorLightFraction = canopy.LightReaching(worldX, worldZ, CanopyLayer.GroundCover);
            s.canopyLai = canopy.TotalLai(worldX, worldZ);
        }

        /// <summary>
        /// One line per missing input, for the inspector's warning rows.
        /// Empty string when nothing is missing.
        /// </summary>
        public string DescribeMissing()
        {
            if (missing == EnvironmentChannel.None) return string.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            if ((missing & EnvironmentChannel.Height) != 0)
                sb.AppendLine("No heightmap: ground reads flat at sea level, so slope and aspect tests cannot bite.");

            if ((missing & EnvironmentChannel.Moisture) != 0)
                sb.AppendLine("No moisture mask: soil moisture falls back to the aridity index, so rain shadow is not seen.");

            if ((missing & EnvironmentChannel.Biome) != 0)
                sb.AppendLine("No biome classification: species are judged on climate alone, with no biome affinity.");

            if ((missing & EnvironmentChannel.Water) != 0)
                sb.AppendLine("No water bodies: no shoreline, no salt spray and no water table within reach.");

            if ((missing & EnvironmentChannel.Derivatives) != 0)
                sb.AppendLine("No chunk derivatives: wetness and wind exposure use the local 3x3 estimate, not flow accumulation.");

            return sb.ToString();
        }

        private void ReadStencil(float worldX, float worldZ, out HeightStencil h, out float groundY)
        {
            h = new HeightStencil();
            h.stepM = stepM;

            if (height == null)
            {
                // Stated default: a level plain at the sea surface. Every entry
                // equal means zero gradient, so slope is 0 and aspect reports the
                // flat sentinel rather than an invented bearing.
                groundY = settings.seaLevelY;
                h.nw = h.n = h.ne = h.w = h.c = h.e = h.sw = h.s = h.se = groundY;
                return;
            }

            Vector2 p = height.WorldToPixel(worldX, worldZ);
            float baseY = height.worldPos.y;
            float scale = height.worldHeight;

            h.nw = baseY + height.GetInterpolated(p.x - 1f, p.y + 1f) * scale;
            h.n = baseY + height.GetInterpolated(p.x, p.y + 1f) * scale;
            h.ne = baseY + height.GetInterpolated(p.x + 1f, p.y + 1f) * scale;
            h.w = baseY + height.GetInterpolated(p.x - 1f, p.y) * scale;
            h.c = baseY + height.GetInterpolated(p.x, p.y) * scale;
            h.e = baseY + height.GetInterpolated(p.x + 1f, p.y) * scale;
            h.sw = baseY + height.GetInterpolated(p.x - 1f, p.y - 1f) * scale;
            h.s = baseY + height.GetInterpolated(p.x, p.y - 1f) * scale;
            h.se = baseY + height.GetInterpolated(p.x + 1f, p.y - 1f) * scale;

            groundY = h.c;
        }

        private static Vector3 NormalFrom(in HeightStencil h)
        {
            // The surface normal of a heightfield is (-dz/dx, 1, -dz/dz), taken
            // from the same Horn gradient the slope uses so the two can never
            // disagree about which way the ground tilts.
            float denom = 8f * Mathf.Max(h.stepM, Mathf.Epsilon);
            float dzdx = ((h.ne + 2f * h.e + h.se) - (h.nw + 2f * h.w + h.sw)) / denom;
            float dzdz = ((h.nw + 2f * h.n + h.ne) - (h.sw + 2f * h.s + h.se)) / denom;

            return new Vector3(-dzdx, 1f, -dzdz).normalized;
        }

        private void FillWater(float worldX, float worldZ, float groundY, ref EnvironmentSample s)
        {
            float radius = Mathf.Max(settings.waterSearchRadiusM, 0f);

            if (WaterProximity.Nearest(water, waterCount, worldX, worldZ, radius, out float d, out int index))
            {
                s.hasWaterInRange = true;
                s.distanceToWaterM = d;
                s.nearestWaterKind = water[index].kind;

                float surfaceY = water[index].surfaceY;
                s.submergedDepthM = d <= 0f ? Mathf.Max(0f, surfaceY - groundY) : 0f;

                // The water table is taken as level with the nearest body's
                // surface, so a point standing a metre above a lake bank has it a
                // metre down. Beyond reach of any body it is reported at the
                // unreachable depth, not at zero, which would read as a marsh.
                s.waterTableDepthM = Mathf.Max(0f, groundY - surfaceY);
            }
            else
            {
                // No body in range. The kind still has to hold a value, and Lake is the
                // least harmful one - but hasWaterInRange is what a rule must actually
                // test, because "the nearest water is a lake" is false here and any
                // rule that believes it plants a lake margin in a desert.
                s.hasWaterInRange = false;
                s.distanceToWaterM = radius;
                s.nearestWaterKind = WaterBodyKind.Lake;
                s.submergedDepthM = 0f;
                s.waterTableDepthM = EcoConstants.WaterTableUnreachableDepthMetres;
            }

            // Salinity has two physically different sources and blending them is
            // wrong. Below a surface it is the salinity of the body you are standing
            // in; on dry ground it is airborne spray blown inland from the nearest
            // SALT body, decaying with distance.
            //
            // Taking max(body, spray) would not be conservative, it would be false: a
            // freshwater lake inside the 120 m spray decay would read as seawater, so
            // kelp (saltTolerance 1) would pass in it while freshwater weed
            // (saltTolerance ~0.05) would be rejected from it - exactly inverted.
            // Submergence therefore wins outright where it applies.
            if (s.submergedDepthM > 0f)
            {
                s.saltFraction = EcoMath.BodySalinityFraction(s.nearestWaterKind);
            }
            else
            {
                float saltDistance = WaterProximity.DistanceToSaltM(
                    water, waterCount, worldX, worldZ, radius);

                s.saltFraction = EcoMath.SaltSprayFraction(saltDistance, settings.saltSprayDecayM);
            }
        }

        private void FillThermal(ref EnvironmentSample s)
        {
            float aspectBias = TopographyMath.AspectWarmingC(
                s.aspectDeg, settings.warmAspectDeg, settings.aspectWarmingC);

            s.meanAnnualTempC = settings.MeanTempAtC(s.altitudeM) + aspectBias;

            // A coast has a damped annual swing and an interior an extreme one,
            // because water stores heat and rock does not. Distance to salt water
            // as a fraction of the search radius is the measure of how maritime
            // the site is; the settings own how strongly that matters.
            float radius = Mathf.Max(settings.waterSearchRadiusM, Mathf.Epsilon);
            float shoreFraction = Mathf.Clamp01(s.distanceToWaterM / radius);
            float halfAmplitude = settings.LocalTempHalfAmplitudeC(shoreFraction);

            s.warmestMonthTempC = s.meanAnnualTempC + halfAmplitude;
            s.coldestMonthTempC = s.meanAnnualTempC - halfAmplitude;

            s.frostFreeFraction = EcoMath.FrostFreeFractionFromExtremes(
                s.coldestMonthTempC, s.warmestMonthTempC);

            // Warmth accumulates only while the ground is unfrozen, so the season
            // length is the frost-free share of the year rather than the whole of
            // it. A site frozen ten months over accumulates almost nothing even
            // if its two warm months are hot.
            float seasonDays = s.frostFreeFraction * EcoConstants.DaysPerTropicalYear;
            s.gdd = EcoMath.GrowingDegreeDays(
                s.meanAnnualTempC, EcoConstants.GddBaseTemperateC, seasonDays);
        }

        private void FillWaterBalance(float worldX, float worldZ, ref EnvironmentSample s)
        {
            float mask = MoistureMaskMidpoint;
            if (moisture != null)
            {
                Vector2 p = moisture.WorldToPixel(worldX, worldZ);
                mask = Mathf.Clamp01(moisture.GetInterpolated(p.x, p.y));
            }

            // The annual total is a chunk-scale fact. When a weather branch produced
            // the chunk's normals the sampler reads that total from the OutputBag,
            // so the forest and the weather agree on how much rain the year brings;
            // with no normals the resolved settings figure stands in as before. The
            // per-point moisture mask supplies the spatial variation about the
            // world's midpoint either way, so altitude and rain shadow are unchanged.
            float baselinePrecipMm = climate != null ? climate.annualPrecipMm : settings.annualPrecipMm;
            s.annualPrecipMm = baselinePrecipMm * (mask / MoistureMaskMidpoint);

            float halfDiurnal = settings.diurnalTempRangeC * 0.5f;
            float petPerDay = EcoMath.HargreavesPetMmPerDay(
                s.meanAnnualTempC - halfDiurnal,
                s.meanAnnualTempC + halfDiurnal,
                s.meanAnnualTempC,
                settings.radiationMjPerM2Day);

            s.petMm = petPerDay * EcoConstants.DaysPerTropicalYear;
            s.aridity = EcoMath.AridityIndex(s.annualPrecipMm, s.petMm);

            // With a mask the moisture is measured; without one it is inferred
            // from the water balance, which gets the deserts right and the
            // drainage hollows wrong. The missing-channel bit says which happened.
            s.soilMoisture = moisture != null ? mask : Mathf.Clamp01(s.aridity);

            float slopeRad = s.slopeDeg * Mathf.Deg2Rad;

            if (derivatives != null && derivatives.IsBuilt)
            {
                s.twi = derivatives.WetnessIndexAt(worldX, worldZ);
            }
            else
            {
                // No flow accumulation to draw on, so the contributing area is
                // the one cell that is certainly there: the point's own. That
                // makes ridges and hollows read alike, which is exactly what the
                // Derivatives warning row is telling the author.
                s.twi = EcoMath.TopographicWetnessIndex(EcoConstants.TwiMinCatchmentArea, slopeRad);
            }
        }

        private void FillSoil(ref EnvironmentSample s)
        {
            s.soilDepthM = TopographyMath.SoilDepthM(s.slopeDeg, s.curvature, settings.soilDepthMaxM);
            s.rockiness = TopographyMath.Rockiness(s.slopeDeg, s.soilDepthM, settings.soilAngleOfReposeDeg);

            // Fertility follows the fines and the organic matter, and both end up
            // where the slopes above shed them: deep ground is fertile, hollows
            // gain what ridges lose, and bare stone holds nothing whatever the
            // other two terms say - hence the rockiness factor multiplying rather
            // than averaging.
            float depthFraction = settings.soilDepthMaxM > Mathf.Epsilon
                ? Mathf.Clamp01(s.soilDepthM / settings.soilDepthMaxM)
                : 0f;

            float concavity = Mathf.Clamp01(
                -s.curvature / EcoConstants.SoilDepthCurvatureSpanPerMetre);

            float fertility = depthFraction + settings.fertilityFromCurvature * concavity;
            s.soilFertility = Mathf.Clamp01(fertility) * (1f - s.rockiness);
        }

        private void FillDisturbance(ref EnvironmentSample s)
        {
            if (derivatives != null && derivatives.IsBuilt)
            {
                s.windExposure = derivatives.WindExposureAt(s.worldPos.x, s.worldPos.z);
            }
            else
            {
                // Convexity alone is a real if short-sighted exposure measure: a
                // nose is exposed and a hollow is sheltered, both true without
                // looking past the neighbouring cells. What it cannot see is the
                // ridge half a kilometre upwind, which is what the derivative
                // pass adds.
                float convexNorm = Mathf.Clamp(
                    s.curvature / EcoConstants.SoilDepthCurvatureSpanPerMetre, -1f, 1f);
                s.windExposure = Mathf.Clamp01(0.5f + 0.5f * convexNorm);
            }

            if (settings.snowLoad)
            {
                // A millimetre of water is a kilogram per square metre, so
                // precipitation falling while the ground is frozen converts to
                // load directly with no coefficient to invent. The treeline
                // figure caps it, since past that the wind strips the snow off
                // faster than it lands.
                float frozenShare = 1f - s.frostFreeFraction;
                float load = s.annualPrecipMm * frozenShare;
                s.snowLoadKgPerM2 = Mathf.Min(load, settings.snowLoadAtTreelineKgPerM2);
            }
            else
            {
                s.snowLoadKgPerM2 = 0f;
            }

            if (settings.fire)
            {
                // Fire needs fuel and a dry spell to carry it. Aridity is the
                // ratio of supply to demand, so the shortfall below one is the
                // dryness that shortens the return interval; a site wetter than
                // its own demand never carries a fire at all.
                float dryness = Mathf.Clamp01(1f - s.aridity);
                s.fireReturnYears = dryness > Mathf.Epsilon
                    ? Mathf.Min(settings.fireReturnBaseYears / dryness, FireNeverReturnsYears)
                    : FireNeverReturnsYears;
            }
            else
            {
                s.fireReturnYears = FireNeverReturnsYears;
            }

            if (settings.browse)
            {
                // Grazers follow forage, and forage follows warmth and water, so
                // pressure is highest where growth is and falls away on frozen or
                // arid ground rather than being spread evenly over the map.
                float productivity = Mathf.Clamp01(s.aridity) * s.frostFreeFraction;
                s.browsePressure = Mathf.Clamp01(settings.browsePressureBase * productivity);
            }
            else
            {
                s.browsePressure = 0f;
            }
        }

        // =====================================================================
        //  BIOME
        // =====================================================================

        /// <summary>
        /// Which biome this point is in, which other biome it lies next to, and how
        /// firmly it belongs to the first.
        ///
        /// The classification is read as a class, never interpolated. Bilinear
        /// filtering an index field averages the *numbers*, and the numbers are
        /// arbitrary: tundra is 0 and desert is 5, so a filtered reading across that
        /// boundary lands on 2.5 and decodes to temperate grassland - a biome not
        /// present in either cell. Every read here is therefore nearest-cell, and
        /// the transition is built from real neighbouring classes instead.
        /// </summary>
        private void FillBiome(float worldX, float worldZ, ref EnvironmentSample s)
        {
            // Negative means unclassified, which every biome affinity test reads
            // as "no opinion" rather than as biome zero.
            s.biomeIndex = -1;
            s.biomeIndexAlt = -1;
            s.biomeBlend = 0f;

            if (biome == null) return;

            s.biomeIndex = (sbyte)BiomeAt(worldX, worldZ);

            // A zero band is an author saying they want the classifier's edges
            // exactly as baked. Full confidence everywhere, no probe, no cost.
            if (settings.biomeBlendM <= 0f)
            {
                s.biomeBlend = 1f;
                return;
            }

            // Half, because the band is centred on the break: a site one radius
            // inside sees no foreign ground, so the softened strip runs one radius
            // either side and biomeBlendM is its whole width.
            float radiusM = settings.biomeBlendM * 0.5f;

            int primaryHits = 0;
            int alt = -1;

            for (int ring = 0; ring < BiomeProbeRings; ring++)
            {
                // Inner ring at half radius. Two rings rather than one because a
                // single ring steps over a biome narrower than the band and reports
                // the far side as if the near side were not there.
                float r = radiusM * (ring + 1) / BiomeProbeRings;

                for (int dir = 0; dir < BiomeProbeDirections; dir++)
                {
                    Vector2 d = BiomeProbeDirs[dir];
                    int found = BiomeAt(worldX + d.x * r, worldZ + d.y * r);

                    if (found == s.biomeIndex) primaryHits++;

                    // First foreign class wins, and the inner ring is walked first, so
                    // the neighbour recorded is the nearer one. At a three-biome
                    // junction that makes the choice arbitrary but repeatable, and the
                    // third class is excluded rather than blended in - which is the
                    // point of naming one neighbour instead of relaxing the test.
                    else if (alt < 0) alt = found;
                }
            }

            float share = (float)primaryHits / (BiomeProbeRings * BiomeProbeDirections);

            // Remapped so a half-and-half neighbourhood - the break itself - reads as
            // zero confidence, and anything more foreign than half cannot push it
            // below zero. Below half would mean the classifier disagrees with itself
            // about which biome is primary, and the answer to that is still "both",
            // not "the other one".
            s.biomeBlend = Mathf.Clamp01(2f * share - 1f);
            s.biomeIndexAlt = (sbyte)alt;
        }

        // The biome class in the cell containing a world position. Nearest, clamped
        // to the bound matrix: a probe that reaches past the chunk reads the edge
        // class, which is the last thing actually known in that direction.
        private int BiomeAt(float worldX, float worldZ)
        {
            Vector2 p = biome.WorldToPixel(worldX, worldZ);

            return WhittakerBiome.Decode(
                biome.GetClamped(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)));
        }
    }
}
