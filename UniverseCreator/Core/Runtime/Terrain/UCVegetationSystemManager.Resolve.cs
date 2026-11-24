// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    UCVegetationSystemManager.Resolve.cs
//  Purpose: The read path. Turns the authored fields in
//           UCVegetationSystemManager.cs into the numbers placement actually
//           uses, and gathers the world facts this component does not own.
//
//  WHY THIS IS A SEPARATE FILE
//  ---------------------------
//  The declaration side is long because the inspector is meant to be. Bolting
//  the logic onto the end of it would give one file nobody can find anything in;
//  a partial class gives the author one inspector and the maintainer two short
//  files. Fields there, behaviour here.
//
//  WHY THE FACTS COME FROM ELSEWHERE
//  ---------------------------------
//  Latitude, sea level and climate are not vegetation's to own, and are not
//  re-declared here:
//
//    latitude, longitude, axial tilt, the clock  <- UCWeatherSystemManager
//    day length, declination, season             <- ClockMath, via that clock
//    temperature, lapse rate, precipitation      <- UCWeatherSystemManager
//    radiation (FAO-56 Ra)                       <- ClockMath, via that clock
//    sea level                                   <- the ocean UCWaterVolume
//
//  Latitude used to be read off the lighting manager. It moved with W-04: a world
//  can have no lighting rig and still have a hemisphere, and the sun and the
//  forest must agree about which one it is.
//
//  A second copy of a world fact is a second answer to the same question, and
//  the two copies drift the first time someone edits one of them. When a manager
//  is absent the fallback is a documented constant, never a guess dressed up as
//  a measurement.
//
//  MAIN THREAD ONLY
//  ----------------
//  ResolveSettings reads MonoBehaviour fields, so it must be called on the main
//  thread. Everything downstream of it reads the returned struct instead, which
//  is the whole reason the struct exists.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCVegetationSystemManager
    {
        // Found rather than authored, and cached because the resolve runs per
        // cell. A destroyed component compares equal to null, so the null check
        // re-finds it rather than handing out a dead reference.
        private UCWeatherSystemManager cachedWeather;
        private UCLightingSystemManager cachedLighting;

        // Resolve-once cache. ResolveSettings reads three components and does real
        // trigonometry, and the scatter pass asks for it once per cell, so a large
        // world would otherwise recompute one unchanging answer thousands of times.
        private EcoSampleSettings cachedSettings;
        private bool hasCachedSettings;
        private float cachedPhase;

        /// <summary>
        /// Throws away the resolved snapshot so the next request rebuilds it.
        ///
        /// Called from this component's own OnValidate, and from the weather and
        /// lighting managers' OnValidate, because a latitude or a lapse rate edited
        /// on another component changes this component's answer and nothing else
        /// would notice. The manager references are dropped too, so moving the
        /// lighting manager to a different child is picked up rather than cached
        /// against the old one.
        /// </summary>
        /// <remarks>UC-T06-09: advances the runtime placement-invalidation count and invalidates cached climate/season solves; it does not execute a graph.</remarks>
        /// <exception cref="System.OverflowException">The runtime placement revision counter is exhausted.</exception>
        public void MarkSettingsDirty()
        {
            placementRevision = checked(placementRevision + 1); // UC-T06-09: explicit placement invalidation stays independent of clock/tint refreshes.
            hasCachedSettings = false;
            cachedWeather = null;
            cachedLighting = null;
            seasonSolvesValid = false;
        }

        /// <summary>
        /// Invalidates the vegetation resolve for the world a component belongs to.
        ///
        /// The weather and lighting managers own facts vegetation reads, but they
        /// have no business knowing who reads them, and vegetation has no way to
        /// notice an edit on a sibling component. One call from their OnValidate
        /// closes that gap without either side polling the other.
        ///
        /// Static, and a world with no vegetation manager is a normal world rather
        /// than an error, so a null result is silent.
        /// </summary>
        public static void InvalidateForWorld(Transform member)
        {
            UCVegetationSystemManager v = ForWorld(member);
            if (v != null) v.MarkSettingsDirty();
        }

        /// <summary>
        /// The vegetation manager governing the world a component belongs to, or
        /// null. Factored out of InvalidateForWorld because the scatter applier
        /// needs the same walk to read the shadow budget, and two copies of a
        /// hierarchy search are two things to fix when the hierarchy changes.
        ///
        /// A world with no vegetation manager is a normal world, so null is silent.
        /// UC-T01-06/07: only managers whose nearest world is this owner are eligible, including inactive children.
        /// </summary>
        public static UCVegetationSystemManager ForWorld(Transform member)
        {
            if (member == null) return null;

            UniverseCreatorObject world = member.GetComponentInParent<UniverseCreatorObject>();
            Transform search = world != null ? world.transform : member;

            return FindOwnedManager<UCVegetationSystemManager>(search, world);
        }

        // UC-T01-06/07: resolve once per bake/cache refresh; never let nested worlds provide sibling inputs.
        private static T FindOwnedManager<T>(Transform search, UniverseCreatorObject world) where T : Component
        {
            T[] candidates = search.GetComponentsInChildren<T>(true);
            for (int i = 0; i < candidates.Length; i++)
                if (candidates[i].GetComponentInParent<UniverseCreatorObject>() == world) return candidates[i];
            return null;
        }

        // =====================================================================
        //  THE WORLD'S OTHER MANAGERS
        // =====================================================================

        /// <summary>
        /// The weather manager for this world, or null. Never created: weather is
        /// the WeatherSystem child's component, and adding one here would put a
        /// second wind manager in the world.
        /// </summary>
        public UCWeatherSystemManager Weather
        {
            get
            {
                if (cachedWeather != null && cachedWeather.GetComponentInParent<UniverseCreatorObject>() == World) return cachedWeather;

                UniverseCreatorObject world = World;
                Transform search = world != null ? world.transform : transform;
                cachedWeather = FindOwnedManager<UCWeatherSystemManager>(search, world);
                return cachedWeather;
            }
        }

        /// <summary>The lighting manager for this world, or null. Never created.</summary>
        public UCLightingSystemManager Lighting
        {
            get
            {
                if (cachedLighting != null && cachedLighting.GetComponentInParent<UniverseCreatorObject>() == World) return cachedLighting;

                UniverseCreatorObject world = World;
                Transform search = world != null ? world.transform : transform;
                cachedLighting = FindOwnedManager<UCLightingSystemManager>(search, world);
                return cachedLighting;
            }
        }

        // =====================================================================
        //  RESOLVED WORLD FACTS
        // =====================================================================

        /// <summary>
        /// The seed placement varies on: the world's master seed plus this
        /// component's offset. Nothing else varies, which is what makes two
        /// machines build the same forest.
        /// </summary>
        public int EffectiveSeed
        {
            get
            {
                UniverseCreatorObject world = World;
                int worldSeed = world != null ? world.seed : 0;
                return worldSeed + seedOffset;
            }
        }

        /// <summary>
        /// deg latitude, from the weather system's clock - its only owner. Falls
        /// back to the documented default of 45 when a world has no weather system,
        /// not to the equator: 0 looks like a safe neutral but it is a claim, and a
        /// wrong one, because it flattens the seasonal swing to nothing and turns
        /// every warm hillside into a coin toss. 45 is stated, visible in the
        /// inspector, and obviously an assumption. (W-04)
        /// </summary>
        public float LatitudeDegrees
        {
            get { return UCWeatherSystemManager.ResolveClockFor(transform).latitudeDeg; }
        }

        /// <summary>
        /// World Y of the sea surface, from the ocean water volume. A world with
        /// no ocean resolves to 0, which makes the altitude fields plain world Y
        /// rather than silently disabling them.
        ///
        /// UC-T01-06/07: only active oceans with this manager's nearest world owner
        /// are considered. The highest ocean in that world wins; standalone managers
        /// consider standalone volumes only.
        /// </summary>
        public float SeaLevelY
        {
            get
            {
                float level = 0f;
                bool found = false;
                UniverseCreatorObject world = World;

                System.Collections.Generic.IReadOnlyList<UCWaterVolume> all = UCWaterVolume.All;
                for (int i = 0; i < all.Count; i++)
                {
                    UCWaterVolume v = all[i];
                    if (v == null || v.kind != UCWaterVolume.BodyKind.Ocean) continue;
                    if (!v.isActiveAndEnabled || v.GetComponentInParent<UniverseCreatorObject>() != world) continue;

                    if (!found || v.surfaceLevel > level)
                    {
                        level = v.surfaceLevel;
                        found = true;
                    }
                }

                return level;
            }
        }

        /// <summary>
        /// 0-1 position in the year, on the winter-zero convention. Follows the
        /// world clock when the toggle is on, else the authored day of year, so a
        /// world can be previewed in high summer without moving the clock.
        ///
        /// Both paths convert through ClockMath. The manual one used to divide the
        /// day number by the year length directly, which put its zero on 1 January
        /// while the clock's zero was midwinter - so turning the toggle off shifted
        /// the world's season by ten days for no reason an author could see. (W-03)
        /// </summary>
        public float ResolvedSeasonPhase
        {
            get
            {
                if (seasonFollowsWeather)
                {
                    UCWeatherSystemManager w = Weather;
                    if (w != null) return Mathf.Repeat(w.seasonPhase, 1f);
                }

                return ClockMath.WinterPhase01(manualDayOfYear);
            }
        }

        /// <summary>
        /// stems/m2 for trees, from the authored per-hectare figure and the global
        /// density dial. The dial is the one place a whole world is thinned for a
        /// weaker device, so it multiplies here rather than at each call site.
        /// </summary>
        public float TreeDensityPerSquareMetre
        {
            get
            {
                return EcoMath.StemsPerHaToPerSquareMetre(treesPerHectare) * globalDensityScale;
            }
        }

        /// <summary>
        /// m. Minimum spacing between trees: whichever is larger of the authored
        /// spacing and the spacing that density alone implies. Taking the larger
        /// is deliberate - asking for 60 stems a hectare at 1 m spacing is not a
        /// dense forest, it is a contradiction, and the density is the physical
        /// half of it. (L-20)
        /// </summary>
        public float ResolvedTreeSpacingMetres
        {
            get
            {
                float fromDensity = EcoMath.PoissonRadiusFromDensity(TreeDensityPerSquareMetre);
                return Mathf.Max(treeSpacingMetres, fromDensity);
            }
        }

        // =====================================================================
        //  DETAIL-LAYER MEMORY (V-29)
        //
        //  TerrainData keeps detail densities as one int per cell per layer per
        //  chunk, allocated when the layer is written and held until the chunk is
        //  released. So the cost is res^2 x 4 B x layers x resident chunks, it is
        //  paid in RAM rather than in frame time, and it is paid whether or not a
        //  single blade is on screen. Nothing else in vegetation behaves that way,
        //  which is why it gets its own arithmetic and its own warning instead of
        //  being folded into the frame budget.
        //
        //  512 with eight species over a 9x9 world is 2.6 GB. That is not a
        //  slow frame, it is a failed allocation, and it reads as a harmless
        //  default until it happens - hence a figure in the inspector before the
        //  build rather than a crash after it.
        // =====================================================================

        /// <summary>B. TerrainData stores one 32-bit density per detail cell per layer.</summary>
        public const int BytesPerDetailCell = 4;

        /// <summary>
        /// px. Detail resolution a mid-range mobile device can afford: 256 is
        /// ~260 KB per layer per chunk against 1 MB at 512, a 4x saving for a
        /// placement precision of half a metre on a 128 m chunk - finer than the
        /// grass is visually resolvable at the distance detail stops drawing.
        /// </summary>
        public const int MobileDetailResolution = 256;

        /// <summary>
        /// MB held in detail layers. The one piece of arithmetic; every readout,
        /// preset and warning calls it rather than repeating it.
        /// </summary>
        public static float DetailMemoryMegabytes(int resolution, int layerCount, int residentChunks)
        {
            if (resolution <= 0 || layerCount <= 0 || residentChunks <= 0) return 0f;

            // double, not float: res^2 x layers x chunks passes 2^24 on a large
            // world, where float stops counting in single bytes.
            double cells = (double)resolution * resolution;
            double bytes = cells * BytesPerDetailCell * layerCount * residentChunks;
            return (float)(bytes / (1024.0 * 1024.0));
        }

        /// <summary>
        /// Detail layers this world actually writes. A prototype with neither a
        /// mesh nor a texture is skipped by ApplyDetailPrototypes, so it costs
        /// nothing and must not be counted.
        /// </summary>
        public int DetailLayerCount
        {
            get
            {
                UniverseCreatorObject world = World;
                if (world == null || world.scatterLibrary == null) return 0;

                int n = 0;
                System.Collections.Generic.List<DetailPrototypeDef> details =
                    world.scatterLibrary.details;

                for (int i = 0; i < details.Count; i++)
                {
                    DetailPrototypeDef d = details[i];
                    if (d != null && (d.prefabMesh != null || d.billboardTexture != null)) n++;
                }

                return n;
            }
        }

        /// <summary>
        /// Chunks holding detail data at once. In the editor every cell of the
        /// world is present, so the count is the whole grid; that is also the
        /// honest figure to warn against, because it is what a build allocates.
        /// </summary>
        public int ResidentChunkCount
        {
            get
            {
                UniverseCreatorObject world = World;
                if (world == null) return 0;

                int side = Mathf.Max(1, world.chunkCountPerSide);
                return side * side;
            }
        }

        /// <summary>MB this world's detail layers will hold, at the authored settings.</summary>
        public float ResolvedDetailMemoryMB
        {
            get
            {
                return DetailMemoryMegabytes(detailResolution, DetailLayerCount, ResidentChunkCount);
            }
        }

        /// <summary>Whether the authored settings exceed the configured ceiling.</summary>
        public bool DetailMemoryOverCeiling
        {
            get { return ResolvedDetailMemoryMB > detailMemoryCeilingMB; }
        }

        /// <summary>
        /// px. Largest resolution that keeps this world under its ceiling, rounded
        /// down to a power of two because that is what the resolution field wants.
        /// This is the fix the warning offers, so it is computed rather than
        /// suggested: telling an author they are over budget without telling them
        /// what would fit is a warning they cannot act on.
        /// </summary>
        public int DetailResolutionUnderCeiling
        {
            get
            {
                int layers = DetailLayerCount;
                int chunks = ResidentChunkCount;
                if (layers <= 0 || chunks <= 0) return detailResolution;

                double budgetBytes = (double)detailMemoryCeilingMB * 1024.0 * 1024.0;
                double cells = budgetBytes / (BytesPerDetailCell * (double)layers * chunks);
                if (cells < 1.0) return 0;

                int res = (int)System.Math.Sqrt(cells);
                if (res < 8) return 0;

                // Down to the power of two at or below it: 8 is Unity's floor and
                // 4096 its ceiling for a detail resolution.
                int pot = 8;
                while (pot * 2 <= res && pot < 4096) pot *= 2;
                return pot;
            }
        }

        // =====================================================================
        //  RESOLVE
        // =====================================================================

        /// <summary>
        /// Copies everything the ecology layer needs into one flat struct. Main
        /// thread only: it reads live components.
        ///
        /// Resolved once and then handed out unchanged until an edit invalidates it
        /// or the season moves. Cheap enough to call per cell for that reason, and
        /// the returned struct is a copy, so a caller cannot corrupt the cache.
        /// </summary>
        public EcoSampleSettings ResolveSettings()
        {
            float phase = ResolvedSeasonPhase;

            // The season is the one input that moves without anyone editing a
            // field - the weather manager's clock advances it - so it is compared
            // rather than assumed constant.
            if (hasCachedSettings && Mathf.Approximately(phase, cachedPhase))
                return cachedSettings;

            EcoSampleSettings s = new EcoSampleSettings();

            // ---- Determinism ------------------------------------------------

            s.seed = EffectiveSeed;

            // ---- From the world clock ---------------------------------------
            //
            // Latitude, the warm aspect that follows from its sign, and day length
            // all come from the clock, which is the weather system's and has one
            // owner. They used to be read off the lighting manager, with an
            // equatorial fallback when there was none - so a world with no lighting
            // rig silently grew equatorial forest at latitude 45, and nothing said
            // so. The clock's own documented default is used instead. (W-04)

            ClockState clock = UCWeatherSystemManager.ResolveClockFor(transform);
            float axialTiltDeg = UCWeatherSystemManager.ResolveAxialTiltDeg(transform);

            s.latitudeDeg = clock.latitudeDeg;
            s.warmAspectDeg = TopographyMath.WarmAspectDegFor(s.latitudeDeg);

            // W-05. The span the world covers, carried in the snapshot so a worker
            // can re-derive a chunk's own latitude without touching a component.
            // Zero - the default - means one latitude for the whole world.
            s.latitudeSpanDeg = UCWeatherSystemManager.ResolveLatitudeSpanDeg(transform);

            // Carried for the same reason: RebasedToLatitude re-derives day length and
            // radiation off-thread, and both need the tilt the clock was built with.
            s.axialTiltDeg = axialTiltDeg;

            // Asked for the passed phase rather than read off the clock, because a
            // caller may be resolving a season other than the current one. Same
            // formula either way: ClockMath owns it.
            //
            // Declination is taken once and used twice below. Day length and
            // radiation are two readings of one sun position, and computing it
            // twice is how they would eventually come to disagree.
            float declinationDeg =
                ClockMath.DeclinationDegFromWinterPhase01(phase, axialTiltDeg);

            s.dayLengthH = ClockMath.DayLengthHours(s.latitudeDeg, declinationDeg);

            // Radiation comes straight from the weather layer's astronomy now, not
            // through the lighting manager. It used to be read there because that
            // was where the term lived; it lives in ClockMath beside declination and
            // day length, so the hop through a rendering component - which a world
            // is allowed not to have at all - buys nothing and only adds a branch
            // whose two sides could drift apart.
            s.radiationMjPerM2Day = ClockMath.ExtraterrestrialRadiationMjPerM2Day(
                s.latitudeDeg, declinationDeg, phase);

            // ---- From the weather manager -----------------------------------

            UCWeatherSystemManager weather = Weather;
            if (weather != null)
            {
                s.seasonPhase = phase;
                s.seaLevelMeanTempC = weather.temperatureCelsius;
                s.lapseRateCPerKm = weather.lapseRatePerKm;

                // V-26. When the world asks for it, the baseline temperature comes from
                // where on the planet it is rather than from a typed number. This is the
                // connection that lets the altitude-for-latitude trade emerge: with it
                // off, a world can sit at 60 degrees north and 22 degC at once, and the
                // treeline will believe the number and ignore the latitude.
                //
                // Read from s.latitudeDeg, which the clock block above has already
                // filled, so there is one latitude in play and not two.
                if (weather.deriveTemperatureFromLatitude)
                    s.seaLevelMeanTempC = ThermalEquivalence.SeaLevelMeanTempC(
                        s.latitudeDeg, weather.equatorTemperatureCelsius);
                s.seaLevelMeanTempC += weather.BaselineTemperatureBiasC; // UC-T05-04: add early baseline once, after latitude resolution and outside dynamic weather.

                // W-05. Both inputs of the line above, carried so a chunk in a world
                // that spans latitude can re-run it for its own latitude on a worker
                // thread. Carried whether or not the flag is set, so that turning it
                // on does not need a different resolve path.
                s.deriveTemperatureFromLatitude = weather.deriveTemperatureFromLatitude;
                s.equatorMeanTempC = weather.equatorTemperatureCelsius + weather.BaselineTemperatureBiasC;

                s.annualTempHalfAmplitudeC = weather.annualTempHalfAmplitudeCelsius;
                s.diurnalTempRangeC = weather.diurnalTempRangeCelsius;
                s.continentality = weather.continentality;
                s.annualPrecipMm = weather.ResolvedAnnualPrecipitationMm;
            }
            else
            {
                // A temperate generalist climate, matching the envelope defaults,
                // so a world with no weather manager still grows something
                // recognisable rather than reading as bare rock.
                s.seasonPhase = phase;
                s.seaLevelMeanTempC = 15f;
                s.lapseRateCPerKm = EcoConstants.LapseRateCPerKm;
                s.annualTempHalfAmplitudeC = 12f;
                s.diurnalTempRangeC = 10f;
                s.continentality = 0.5f;
                s.annualPrecipMm = 800f;
            }

            // ---- From the ocean ---------------------------------------------

            s.seaLevelY = SeaLevelY;

            // ---- Suitability ------------------------------------------------

            s.suitabilityThreshold = suitabilityThreshold;
            s.suitabilitySharpness = suitabilitySharpness;
            s.allowMixedStands = allowMixedStands;
            s.maxSpeciesPerSite = allowMixedStands ? maxSpeciesPerSite : 1;

            // ---- Environment sampling ---------------------------------------

            s.waterSearchRadiusM = waterSearchRadiusMetres;
            s.soilDepthMaxM = soilDepthMaxMetres;
            s.soilAngleOfReposeDeg = soilAngleOfReposeDegrees;
            s.fertilityFromCurvature = fertilityFromCurvature;
            s.wetnessCatchmentRadiusM = wetnessCatchmentRadiusMetres;
            s.saltSprayDecayM = saltSprayDecayMetres;

            // ---- Competition ------------------------------------------------

            s.canopyCompetition = enableCanopyCompetition;
            s.canopyLayerPasses = enableCanopyCompetition ? canopyLayerPasses : 1;
            s.understoryLightFloor = understoryLightFloor;
            s.selfThinning = enableSelfThinning;
            s.crownSpacing = enableCrownSpacing;

            // ---- Dispersal --------------------------------------------------

            s.dispersalClustering = enableDispersalClustering;
            s.dispersalSeedsPerHa = dispersalSeedsPerHectare;
            s.clusterContrast = enableDispersalClustering ? clusterContrast : 0f;

            // ---- Disturbance ------------------------------------------------

            s.fire = enableFire;
            s.fireReturnBaseYears = fireReturnBaseYears;
            s.browse = enableBrowse;
            s.browsePressureBase = enableBrowse ? browsePressureBase : 0f;
            s.snowLoad = enableSnowLoad;
            s.snowLoadAtTreelineKgPerM2 = snowLoadAtTreelineKgPerM2;
            s.avalanche = enableAvalanche;
            s.avalancheMinSlopeDeg = avalancheMinSlopeDegrees;
            s.krummholz = enableKrummholz;
            s.ageSizeVariation = Mathf.Clamp01(ageSizeVariation);
            s.seedlingSizeFraction = Mathf.Clamp01(seedlingSizeFraction);
            s.antiRepeatRadiusM = Mathf.Max(0f, antiRepeatRadiusM);
            s.antiRepeatStrength = Mathf.Clamp01(antiRepeatStrength);
            s.assertBareGround = assertBareGround;

            // ---- Hard gates -------------------------------------------------

            s.maxSlopeDeg = maxSlopeDegrees;
            s.minAltitudeM = minAltitudeMetres;
            s.maxAltitudeM = maxAltitudeMetres;
            s.clearanceM = clearanceMetres;
            s.avoidWater = avoidWater;
            s.avoidCutRegions = avoidCutRegions;
            s.useGraphMasks = useGraphMasks;
            s.maskThreshold = maskThreshold;
            s.biomeBlendM = biomeBlendMetres;

            // ---- Density and budget -----------------------------------------

            s.treeDensityPerM2 = TreeDensityPerSquareMetre;
            s.grassDensityPerM2 = grassPerSquareMetre * globalDensityScale;
            s.densityScale = globalDensityScale;
            s.treeSpacingM = ResolvedTreeSpacingMetres;
            s.candidatesPerM2 = candidatesPerSquareMetre;
            s.maxInstancesPerCell = maxInstancesPerCell;

            // T7: a per-cell ceiling of 20000 - or -1 for "unlimited" - can put a phone
            // out of memory on a dense cell. On a mobile platform, cap the resolved
            // ceiling to the device floor so a buyer's authored value can never exceed
            // what the minimum target can hold; desktop keeps the authored value.
            if (Application.isMobilePlatform)
                s.maxInstancesPerCell = MobileCappedInstancesPerCell(s.maxInstancesPerCell);

            s.shadowCasterDistanceM = shadowCasterDistanceMetres;

            // ---- Season -----------------------------------------------------

            s.seasonalPhenology = enableSeasonalPhenology;

            cachedSettings = s;
            cachedPhase = phase;
            hasCachedSettings = true;

            return s;
        }

        // =====================================================================
        //  BILLBOARDS AND SHADOWS (L-49)
        //
        //  Draw distances are NOT owned here. The acuity model on
        //  UniverseCreatorObject is the single source of the four native terrain
        //  distances (basemap, detail, tree, tree-billboard); it derives them from
        //  one authored acuity budget through the same metres-per-pixel relation the
        //  density core uses, and TerrainBuilder.ConfigureTerrain applies them. A
        //  second authored answer here would be a second, competing model.
        //
        //  What is still per-vegetation is the one refinement the acuity value alone
        //  cannot express: the billboard swap can be pushed OUTWARD by a species that
        //  must stay a mesh longer. The native tree renderer has one
        //  treeBillboardDistance for every prototype on the terrain, not one each, so
        //  where species disagree the largest wins - billboarding a big conifer too
        //  early is the visible artefact, while keeping a small tree as a mesh
        //  slightly too long is only a cost.
        // =====================================================================

        /// <summary>
        /// m. Floor under the billboard crossfade. Zero pops, and a pop at the
        /// billboard transition is the most-reported artefact in any vegetation
        /// system - so zero is not on offer, whatever is authored.
        /// </summary>
        public const float MinCrossFadeMetres = 5f;

        /// <summary>
        /// m at which trees begin crossfading to billboards. The base is the world's
        /// acuity-derived treeBillboardDistance; a species that must stay a mesh longer
        /// pushes it outward (largest wins), then the whole value is clamped inside the
        /// acuity tree-cull distance less the fade, so a crossfade that started never
        /// runs past the cutoff - a fade that never finishes pops at the far edge.
        /// </summary>
        public float ResolvedBillboardStartM
        {
            get
            {
                UniverseCreatorObject world = World;
                if (world == null) return 0f;

                float start = Mathf.Max(0f, world.treeBillboardDistance);

                if (world.scatterLibrary != null)
                {
                    System.Collections.Generic.List<TreePrototypeDef> trees =
                        world.scatterLibrary.trees;

                    for (int i = 0; i < trees.Count; i++)
                    {
                        TreePrototypeDef t = trees[i];
                        if (t == null || t.prefab == null) continue;

                        float wanted = t.eco.ResolveBillboardStartM(-1f);
                        if (wanted > start) start = wanted;
                    }
                }

                float draw = Mathf.Max(0f, world.treeDistance);
                float fade = ResolvedBillboardFadeM;

                // The fade has to finish before the trees are cut off entirely.
                return Mathf.Clamp(start, 0f, Mathf.Max(0f, draw - fade));
            }
        }

        /// <summary>m over which the billboard crossfade runs. Never zero.</summary>
        public float ResolvedBillboardFadeM
        {
            get { return Mathf.Max(MinCrossFadeMetres, billboardFadeLengthMetres); }
        }

        /// <summary>
        /// Refreshes the acuity-derived draw distances on the world, then layers the
        /// one vegetation refinement on top: the billboard swap pushed outward by the
        /// largest species that wants it. Draw distances themselves are not authored
        /// here - RecomputeDerivedDistances is called first so the billboard base is
        /// the current acuity value and never a previously pushed one, which would
        /// otherwise ratchet outward and never come back. Only when vegetation is
        /// enabled: a disabled manager has no business touching the terrain's LOD.
        ///
        /// Returns true when something actually changed, so a caller does not dirty
        /// a world it did not alter.
        /// </summary>
        public bool ApplyLodToWorld()
        {
            if (!vegetationEnabled) return false;

            UniverseCreatorObject world = World;
            if (world == null) return false;

            // The acuity model owns the four native distances. Recompute the clean
            // base so the per-species billboard push below rides the current acuity
            // value, not a value this method pushed on a previous call.
            world.RecomputeDerivedDistances();

            float start = ResolvedBillboardStartM;
            float fade = ResolvedBillboardFadeM;

            bool changed = !Mathf.Approximately(world.treeBillboardDistance, start)
                        || !Mathf.Approximately(world.treeCrossFadeLength, fade);

            if (!changed) return false;

            world.treeBillboardDistance = start;
            world.treeCrossFadeLength = fade;
            return true;
        }

        // =====================================================================
        //  DEVICE PRESETS (V-33)
        //
        //  One action writes every cost dial for a device class, then stops
        //  interfering. The checksum is how the inspector knows the author has
        //  since moved one: a hash of the dials compared against the hash taken
        //  when the preset was applied costs one int of serialized state, where a
        //  shadow copy of every field would cost a second copy of all of them and
        //  drift the first time a dial is added.
        // =====================================================================

        /// <summary>
        /// The numbers for one device class. One switch rather than three
        /// serialized profiles, so adding a dial cannot leave a profile behind
        /// half-populated with a default nobody chose.
        /// </summary>
        public struct QualityDials
        {
            public int detailResolution;
            public float billboardFadeM;
            public float densityScale;
            public float shadowDistanceM;
            public float detailMemoryCeilingMB;

            // Library side, because a collider budget is a property of the
            // prototype set rather than of this component. Written only when a
            // world with a library is present.
            public int maxShrubsPerChunk;
            public int shrubColliderBudget;
            public int maxObjectsPerChunk;
            public int gpuInstancingThreshold;
        }

        public static QualityDials DialsFor(VegetationQuality quality)
        {
            QualityDials d = new QualityDials();

            switch (quality)
            {
                case VegetationQuality.MidRangeMobile:
                    // 256 is the 4x memory saving V-29 exists to make visible, and
                    // 0.25 m placement precision on a 128 m chunk is still finer
                    // than grass can be told apart at 60 m.
                    d.detailResolution = MobileDetailResolution;
                    d.billboardFadeM = 8f;
                    d.densityScale = 0.6f;
                    d.shadowDistanceM = 35f;
                    d.detailMemoryCeilingMB = 96f;
                    d.maxShrubsPerChunk = 250;
                    d.shrubColliderBudget = 60;
                    d.maxObjectsPerChunk = 2000;   // Appendix B's mobile budget
                    d.gpuInstancingThreshold = 600;
                    break;

                case VegetationQuality.FlagshipMobile:
                    d.detailResolution = 384;
                    d.billboardFadeM = 12f;
                    d.densityScale = 0.8f;
                    d.shadowDistanceM = 50f;
                    d.detailMemoryCeilingMB = 192f;
                    d.maxShrubsPerChunk = 400;
                    d.shrubColliderBudget = 120;
                    d.maxObjectsPerChunk = 3000;
                    d.gpuInstancingThreshold = 1000;
                    break;

                default:
                    // The shipped values, so applying Desktop to an existing world
                    // returns it to what it had rather than to something new.
                    d.detailResolution = 512;
                    d.billboardFadeM = 20f;
                    d.densityScale = 1f;
                    d.shadowDistanceM = 80f;
                    d.detailMemoryCeilingMB = 512f;
                    d.maxShrubsPerChunk = 600;
                    d.shrubColliderBudget = 250;
                    d.maxObjectsPerChunk = 4000;
                    d.gpuInstancingThreshold = 1500;
                    break;
            }

            return d;
        }

        /// <summary>
        /// Writes one device class's dials onto this component and, when a world is
        /// present, onto its prototype library. Records the checksum so divergence
        /// can be reported afterwards.
        /// </summary>
        public void ApplyQualityPreset(VegetationQuality quality)
        {
            QualityDials d = DialsFor(quality);

            qualityPreset = quality;

            detailResolution = d.detailResolution;
            billboardFadeLengthMetres = d.billboardFadeM;
            globalDensityScale = d.densityScale;
            shadowCasterDistanceMetres = d.shadowDistanceM;
            detailMemoryCeilingMB = d.detailMemoryCeilingMB;

            UniverseCreatorObject world = World;
            if (world != null && world.scatterLibrary != null)
            {
                ScatterPrototypeLibrary lib = world.scatterLibrary;
                lib.maxShrubsPerChunk = d.maxShrubsPerChunk;
                lib.shrubColliderBudget = d.shrubColliderBudget;
                lib.maxObjectsPerChunk = d.maxObjectsPerChunk;
                lib.gpuInstancingThreshold = d.gpuInstancingThreshold;
            }

            ApplyLodToWorld();

            appliedPresetChecksum = PresetDialChecksum;
            hasAppliedPreset = true;
            MarkSettingsDirty();
        }

        /// <summary>
        /// Hash of exactly the dials a preset writes. Floats go in by their bit
        /// pattern rather than through a tolerance, because "(modified)" should
        /// mean the author moved something, not that it drifted by an epsilon.
        /// </summary>
        public int PresetDialChecksum
        {
            get
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + detailResolution;
                    h = h * 31 + billboardFadeLengthMetres.GetHashCode();
                    h = h * 31 + globalDensityScale.GetHashCode();
                    h = h * 31 + shadowCasterDistanceMetres.GetHashCode();
                    h = h * 31 + detailMemoryCeilingMB.GetHashCode();

                    UniverseCreatorObject world = World;
                    if (world != null && world.scatterLibrary != null)
                    {
                        ScatterPrototypeLibrary lib = world.scatterLibrary;
                        h = h * 31 + lib.maxShrubsPerChunk;
                        h = h * 31 + lib.shrubColliderBudget;
                        h = h * 31 + lib.maxObjectsPerChunk;
                        h = h * 31 + lib.gpuInstancingThreshold;
                    }

                    return h;
                }
            }
        }

        /// <summary>
        /// Whether the dials have moved since the preset was applied. False before
        /// any preset has been applied: a fresh component has not diverged from a
        /// choice nobody made.
        /// </summary>
        public bool PresetModified
        {
            get
            {
                if (!hasAppliedPreset) return false;
                return PresetDialChecksum != appliedPresetChecksum;
            }
        }

        /// <summary>
        /// Whether this component's dials already match a device class exactly.
        /// Lets the inspector report the honest preset on a world that was authored
        /// before presets existed.
        /// </summary>
        public bool MatchesPreset(VegetationQuality quality)
        {
            QualityDials d = DialsFor(quality);

            return detailResolution == d.detailResolution
                && Mathf.Approximately(billboardFadeLengthMetres, d.billboardFadeM)
                && Mathf.Approximately(globalDensityScale, d.densityScale)
                && Mathf.Approximately(shadowCasterDistanceMetres, d.shadowDistanceM);
        }

        /// <summary>
        /// Whether this cell is worth scattering into at all. A cheap rejection
        /// before the expensive per-candidate work, using only the cell bounds.
        ///
        /// Every test here is conservative: it rejects only when no point in the
        /// bounds could pass, so a cell that straddles a limit is still built and
        /// filtered per candidate. Rejecting a straddling cell is what leaves a
        /// square bite out of a treeline.
        /// </summary>
        public bool ShouldBuild(Bounds cellBounds)
        {
            if (!vegetationEnabled) return false;
            if (globalDensityScale <= 0f) return false;
            if (grassPerSquareMetre <= 0f && treesPerHectare <= 0f) return false;

            float seaLevel = SeaLevelY;

            // The altitude fields are metres above sea level; the bounds are world
            // Y. Converting once here is what keeps a world whose ocean does not
            // sit at Y = 0 from placing its treeline at the wrong height.
            float lowestAltitude = cellBounds.min.y - seaLevel;
            float highestAltitude = cellBounds.max.y - seaLevel;

            if (highestAltitude < minAltitudeMetres) return false;
            if (lowestAltitude > maxAltitudeMetres) return false;

            return true;
        }
    }
}
