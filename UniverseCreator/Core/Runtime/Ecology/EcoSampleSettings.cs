// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    EcoSampleSettings.cs
//  Purpose: Everything the ecology layer needs from the world, resolved once per
//           cell into one flat value type.
//
//  WHY THIS EXISTS AT ALL
//  ----------------------
//  Not tidiness. The sampler and the suitability scorer run on worker threads,
//  and touching a MonoBehaviour's fields from a worker thread is illegal in Unity
//  - it throws if you are lucky and returns garbage if you are not. So the three
//  manager components are read once, on the main thread, into this block, and the
//  workers see only this.
//
//  It also means the ecology layer has no reference to the TerrainSystem
//  namespace: the managers depend on ecology, not the other way round, so the
//  laws can be tested without a scene.
//
//  WHERE EACH FIELD COMES FROM
//  ---------------------------
//  The world facts are resolved from whichever component owns them, never
//  duplicated:
//
//    latitude, longitude, axial tilt, day length    <- the world clock, which is
//    season, declination                               UCWeatherSystemManager's
//    temperature, lapse rate, precipitation         <- UCWeatherSystemManager
//    radiation                                      <- UCLightingSystemManager,
//                                                      until W-10 moves it
//    sea level                                      <- the ocean UCWaterVolume
//    everything else                                <- UCVegetationSystemManager
//
//  Latitude moved to the clock with W-04: a world can have no lighting rig and
//  still have a hemisphere.
//
//  Resolved once per cell, then passed by "in". Never mutated by a worker -
//  RebasedToLatitude below returns a copy for exactly that reason.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// The resolved, thread-safe snapshot of a world's ecology settings. Produced
    /// by UCVegetationSystemManager.ResolveSettings on the main thread.
    /// </summary>
    public struct EcoSampleSettings
    {
        // ---- Determinism -------------------------------------------------------

        /// <summary>World seed combined with the vegetation seed offset. The only source of variation.</summary>
        public int seed;

        // ---- World facts, resolved from their owners ---------------------------

        /// <summary>deg, negative south. The world's authored latitude, from the world clock.</summary>
        public float latitudeDeg;

        /// <summary>
        /// deg of latitude the world spans south edge to north edge, 0 for one
        /// latitude everywhere. From the weather manager. Carried in the snapshot,
        /// rather than read per chunk from the component, because the rebase happens
        /// on a worker thread where no component may be touched. (W-05)
        /// </summary>
        public float latitudeSpanDeg;

        /// <summary>
        /// deg. The tilt the clock was built with. Carried for the same reason as
        /// the span: RebasedToLatitude re-derives declination off-thread, and
        /// declination is a function of the tilt. (W-05)
        /// </summary>
        public float axialTiltDeg;

        /// <summary>
        /// degC annual mean at sea level on the equator. Carried so that a spanning
        /// world can re-derive its baseline temperature per chunk under V-26 without
        /// reading the weather component off-thread. Only consulted when
        /// <see cref="deriveTemperatureFromLatitude"/> is set. (W-05)
        /// </summary>
        public float equatorMeanTempC;

        /// <summary>
        /// V-26. Baseline temperature follows latitude rather than an authored
        /// number. Carried into the snapshot because it decides whether a per-chunk
        /// latitude may move the temperature with it. (W-05)
        /// </summary>
        public bool deriveTemperatureFromLatitude;

        /// <summary>deg azimuth of the equator-facing slope. Derived from the sign of latitude.</summary>
        public float warmAspectDeg;

        /// <summary>
        /// degC. How much warmer a fully equator-facing slope runs than a level
        /// site, and how much colder the pole-facing one. Two to three degrees is
        /// typical in mid latitudes, and it is enough to decide which side of a
        /// valley carries forest and which carries scrub.
        /// </summary>
        public float aspectWarmingC;

        /// <summary>World Y of the sea surface. From the ocean water volume, or 0 in a world with no sea.</summary>
        public float seaLevelY;

        /// <summary>degC/km. From the weather manager, so terrain and ecology agree on how cold a mountain is.</summary>
        public float lapseRateCPerKm;

        /// <summary>0-1 position in the year, 0 northern midwinter. From the weather manager or authored.</summary>
        public float seasonPhase;

        /// <summary>degC annual mean at sea level. From the weather manager.</summary>
        public float seaLevelMeanTempC;

        /// <summary>degC. Half the swing between coldest and warmest month.</summary>
        public float annualTempHalfAmplitudeC;

        /// <summary>degC between daily minimum and maximum. Feeds the PET law.</summary>
        public float diurnalTempRangeC;

        /// <summary>0-1 maritime to continental. Scales the annual swing with distance from the sea.</summary>
        public float continentality;

        /// <summary>mm/yr baseline precipitation, before the graph's orographic modulation.</summary>
        public float annualPrecipMm;

        /// <summary>MJ/m2/day at the top of the atmosphere for this season and latitude. Feeds the PET law.</summary>
        public float radiationMjPerM2Day;

        /// <summary>h between sunrise and sunset for this season and latitude. Drives senescence.</summary>
        public float dayLengthH;

        // ---- Suitability -------------------------------------------------------

        /// <summary>0-1 score below which a species is absent rather than sparse.</summary>
        public float suitabilityThreshold;

        /// <summary>Exponent on the combined score. Above 1 crisps boundaries.</summary>
        public float suitabilitySharpness;

        /// <summary>Runners-up may also appear at a site, not only the winner.</summary>
        public bool allowMixedStands;

        /// <summary>Species that may share one site when mixing is allowed.</summary>
        public int maxSpeciesPerSite;

        // ---- Environment sampling ---------------------------------------------

        /// <summary>m. Search radius for water. Also the distance reported when nothing is found.</summary>
        public float waterSearchRadiusM;

        /// <summary>m of soil on level ground, before slope and curvature reduce it.</summary>
        public float soilDepthMaxM;

        /// <summary>deg. Slope at which soil depth reaches zero and the site is bare rock. (L-23)</summary>
        public float soilAngleOfReposeDeg;

        /// <summary>0-1 how strongly hollows are richer than ridges.</summary>
        public float fertilityFromCurvature;

        /// <summary>m upslope radius used to estimate contributing area for wetness. (L-12)</summary>
        public float wetnessCatchmentRadiusM;

        /// <summary>m e-folding distance for salt spray inland. (L-25)</summary>
        public float saltSprayDecayM;

        // ---- Competition and canopy -------------------------------------------

        /// <summary>Taller species shade shorter ones. (L-17)</summary>
        public bool canopyCompetition;

        /// <summary>Vertical layers resolved per site, tallest first.</summary>
        public int canopyLayerPasses;

        /// <summary>0-1 light the ground layer is guaranteed regardless of canopy.</summary>
        public float understoryLightFloor;

        /// <summary>Stand density is capped by Reineke's law. (L-18)</summary>
        public bool selfThinning;

        /// <summary>Spacing follows crown radius from trunk diameter. (L-19)</summary>
        public bool crownSpacing;

        // ---- Dispersal ---------------------------------------------------------

        /// <summary>Sites near an existing stand of the same species are favoured. (L-21)</summary>
        public bool dispersalClustering;

        /// <summary>Parent sites per hectare seeding the dispersal field.</summary>
        public float dispersalSeedsPerHa;

        /// <summary>0-1 how strongly dispersal biases placement.</summary>
        public float clusterContrast;

        // ---- Disturbance -------------------------------------------------------

        /// <summary>Fire history shapes which species are present.</summary>
        public bool fire;

        /// <summary>yr between fires on typical ground.</summary>
        public float fireReturnBaseYears;

        /// <summary>Grazing pressure suppresses intolerant species.</summary>
        public bool browse;

        /// <summary>0-1 baseline grazing pressure.</summary>
        public float browsePressureBase;

        /// <summary>Snow load limits which crowns survive.</summary>
        public bool snowLoad;

        /// <summary>kg/m2 peak load at the treeline.</summary>
        public float snowLoadAtTreelineKgPerM2;

        /// <summary>Steep chutes are scoured clear.</summary>
        public bool avalanche;

        /// <summary>deg. Slope above which scour is possible.</summary>
        public float avalancheMinSlopeDeg;

        /// <summary>Wind-exposed sites dwarf a species rather than removing it.</summary>
        public bool krummholz;

        // ---- Hard gates from the shipped placement rules -----------------------

        /// <summary>deg. Steepest ground vegetation stands on.</summary>
        public float maxSlopeDeg;

        /// <summary>m above sea level below which nothing grows.</summary>
        public float minAltitudeM;

        /// <summary>m above sea level backstop. The real treeline comes from temperature.</summary>
        public float maxAltitudeM;

        /// <summary>m kept clear of water edges, roads and cut regions.</summary>
        public float clearanceM;

        /// <summary>Keep out of water bodies.</summary>
        public bool avoidWater;

        /// <summary>Keep out of regions the road bridge has cut.</summary>
        public bool avoidCutRegions;

        /// <summary>Read density from the graph's surface masks.</summary>
        public bool useGraphMasks;

        /// <summary>0-1 mask value below which nothing is placed.</summary>
        public float maskThreshold;

        /// <summary>m over which a biome boundary blends.</summary>
        public float biomeBlendM;

        // ---- Density and budget ------------------------------------------------

        /// <summary>stems/m2 ceiling for trees, already scaled by the global density dial.</summary>
        public float treeDensityPerM2;

        /// <summary>instances/m2 for detail, already scaled by the global density dial.</summary>
        public float grassDensityPerM2;

        /// <summary>
        /// The global density dial itself, 0-2. Already folded into the two figures
        /// above, and carried separately only for the channels the vegetation
        /// manager has no per-hectare field for - shrubs, objects, rock, debris -
        /// so that one dial still thins them. Anything reading a *PerM2 field above
        /// must not multiply by this as well.
        /// </summary>
        public float densityScale;

        /// <summary>m minimum spacing between trees, before crown geometry widens it.</summary>
        public float treeSpacingM;

        /// <summary>Candidate sites evaluated per square metre.</summary>
        public float candidatesPerM2;

        /// <summary>Ceiling on instances in one cell. -1 for none.</summary>
        public int maxInstancesPerCell;

        /// <summary>m beyond which vegetation stops casting shadows.</summary>
        public float shadowCasterDistanceM;

        // ---- Season ------------------------------------------------------------

        /// <summary>Foliage colour, leaf cover and grass height follow the season.</summary>
        public bool seasonalPhenology;

        /// <summary>
        /// degC annual mean at an altitude, using this snapshot's lapse rate. The
        /// one place altitude becomes temperature, so nothing downstream has to
        /// remember which lapse rate the world is on. (L-04)
        /// </summary>
        public float MeanTempAtC(float altitudeM)
        {
            return EcoMath.LapseAdjustedTempC(seaLevelMeanTempC, altitudeM, lapseRateCPerKm);
        }

        /// <summary>
        /// degC. The local annual swing, widened inland. Two places on one line of
        /// latitude grow different forests because the coast is milder, and this is
        /// where that happens: distanceToShoreFraction is 0 at the water and 1 well
        /// inland, so a coastal site keeps a maritime swing and the interior gets
        /// the full authored one.
        /// </summary>
        public float LocalTempHalfAmplitudeC(float distanceToShoreFraction)
        {
            if (distanceToShoreFraction < 0f) distanceToShoreFraction = 0f;
            if (distanceToShoreFraction > 1f) distanceToShoreFraction = 1f;

            // Maritime end of the range. Continentality decides how much of the
            // authored swing a fully inland site actually gets.
            float maritime = annualTempHalfAmplitudeC * (1f - continentality);
            return maritime + (annualTempHalfAmplitudeC - maritime) * distanceToShoreFraction;
        }

        /// <summary>
        /// W-05. A copy of this snapshot rebased onto the latitude of one chunk, for a
        /// world that spans latitude. <paramref name="northFraction01"/> is 0 at the
        /// world's south edge and 1 at its north edge.
        ///
        /// WHY THIS IS ONE METHOD AND NOT AN ASSIGNMENT TO latitudeDeg
        /// -----------------------------------------------------------
        /// Latitude does not act alone. Four other fields in this block are derived
        /// from it, and moving latitude without them is worse than not moving it at
        /// all - a chunk would report 60 deg while keeping the day length, the
        /// radiation and the warm slope of 45 deg, and the vegetation would come out
        /// confidently wrong with nothing to point at. So the whole derivation chain
        /// is re-run here, in one place, and the fields move together:
        ///
        ///   latitudeDeg          the new latitude itself
        ///   warmAspectDeg        which way the warm slope faces - it inverts if the
        ///                        span crosses the equator
        ///   dayLengthH           from the new latitude and the same declination
        ///   radiationMjPerM2Day  FAO-56 Ra at the new latitude
        ///   seaLevelMeanTempC    only under V-26, where temperature follows latitude
        ///
        /// Season phase and declination are deliberately NOT rebased: the sun stands
        /// over one declination for the whole planet at a given moment, so they are
        /// world facts rather than chunk facts.
        ///
        /// A span of zero returns this snapshot untouched, by value and without
        /// arithmetic, so worlds that do not use the gradient stay bit-identical.
        ///
        /// Pure and thread-safe. It reads no component and mutates nothing - it
        /// returns a copy, because a worker holding this struct by "in" must not be
        /// able to alter the world's snapshot.
        /// </summary>
        public EcoSampleSettings RebasedToLatitude(float northFraction01)
        {
            if (latitudeSpanDeg <= 0f) return this;

            EcoSampleSettings s = this;

            s.latitudeDeg = UniverseCreator.Weather.ClockMath.LatitudeAt(
                latitudeDeg, latitudeSpanDeg, northFraction01);

            s.warmAspectDeg = TopographyMath.WarmAspectDegFor(s.latitudeDeg);

            // One declination for both, so day length and radiation cannot disagree
            // about what season it is.
            float declinationDeg =
                UniverseCreator.Weather.ClockMath.DeclinationDegFromWinterPhase01(
                    seasonPhase, axialTiltDeg);

            s.dayLengthH = UniverseCreator.Weather.ClockMath.DayLengthHours(
                s.latitudeDeg, declinationDeg);

            s.radiationMjPerM2Day =
                UniverseCreator.Weather.ClockMath.ExtraterrestrialRadiationMjPerM2Day(
                    s.latitudeDeg, declinationDeg, seasonPhase);

            // V-26 only. With it off the author typed a sea-level temperature and
            // means it everywhere; overriding that from latitude here would quietly
            // discard a deliberate number.
            if (deriveTemperatureFromLatitude)
                s.seaLevelMeanTempC = ThermalEquivalence.SeaLevelMeanTempC(
                    s.latitudeDeg, equatorMeanTempC);

            return s;
        }
    }
}
