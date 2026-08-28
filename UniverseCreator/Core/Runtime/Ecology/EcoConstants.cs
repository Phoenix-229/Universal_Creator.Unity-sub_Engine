// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    EcoConstants.cs
//  Purpose: Every physical constant the ecology layer uses, named with its unit
//           and the law it serves.
//
//  WHY THIS FILE EXISTS
//  --------------------
//  A bare 0.0065f inline is a defect even when the value is right, because the
//  next reader cannot tell a measured constant from a fudge factor, and cannot
//  tell metres from kilometres. Every number below is a published measurement or
//  an exact unit conversion, and no other file in Ecology/ may contain a float
//  literal other than 0, 1, 0.5 and loop bounds.
//
//  Law IDs (L-nn) refer to the law catalogue in the Module 3 task list, section
//  1.1. Sources are named where the value is a measurement rather than a
//  definition, so a reviewer can check it rather than trust it.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Ecological and surface-process constants. Static, immutable, thread-safe.
    /// </summary>
    public static class EcoConstants
    {
        // ---------------------------------------------------------------------
        //  Unit conversions. Exact by definition, not measured.
        // ---------------------------------------------------------------------

        /// <summary>m per km. Lapse rates are published per kilometre; terrain is in metres.</summary>
        public const float MetresPerKilometre = 1000f;

        /// <summary>m² per hectare. Reineke's law (L-18) is published per hectare.</summary>
        public const float SquareMetresPerHectare = 10000f;

        /// <summary>Days in a full circle of the year, used to normalise day-of-year phases.</summary>
        public const float DaysPerTropicalYear = 365.2422f;

        // ---------------------------------------------------------------------
        //  L-04  Environmental lapse rate.
        //  T(h) = T0 - rate * h. The environmental rate is the one a standing
        //  atmosphere actually shows; the adiabatic pair bracket it and are used
        //  by the cloud-base and stability terms, not by terrain temperature.
        // ---------------------------------------------------------------------

        /// <summary>degC/km. ICAO standard atmosphere environmental lapse rate. Owned by AtmoConstants.</summary>
        public const float LapseRateCPerKm = UniverseCreator.Weather.AtmoConstants.LapseRateCPerKm;

        /// <summary>degC/km. Dry adiabatic: unsaturated air lifted with no heat exchange. Owned by AtmoConstants.</summary>
        public const float LapseRateDryAdiabaticCPerKm = UniverseCreator.Weather.AtmoConstants.DryAdiabaticCPerKm;

        /// <summary>degC/km. Saturated adiabatic, mid-latitude mean. Latent heat release halves the cooling. Owned by AtmoConstants.</summary>
        public const float LapseRateSaturatedCPerKm = UniverseCreator.Weather.AtmoConstants.SaturatedAdiabaticCPerKm;

        // ---------------------------------------------------------------------
        //  L-07  Cloud base / lifting condensation level (Espy).
        //  This name stays here because the ecology sampler needs a cloud base for
        //  its light budget, and everything already written against
        //  EcoConstants.LclMetresPerCelsius must keep compiling. The VALUE now lives
        //  in AtmoConstants and this forwards to it - see the ownership note at the
        //  head of the solar section below for why that direction and not the other.
        // ---------------------------------------------------------------------

        /// <summary>m per degC of dew-point depression. Espy's rule for the LCL. Owned by AtmoConstants.</summary>
        public const float LclMetresPerCelsius = UniverseCreator.Weather.AtmoConstants.LclMetresPerCelsius;

        // ---------------------------------------------------------------------
        //  L-22  Treeline (Koerner). The single most visible ecological threshold
        //  in a mountain world: trees fail where the warmest month mean falls
        //  below this, which is why a real mountain goes bare at an altitude
        //  rather than thinning out gradually.
        // ---------------------------------------------------------------------

        /// <summary>degC. Warmest-month mean below which no tree life form establishes.</summary>
        public const float TreelineWarmestMonthC = 6.4f;

        // ---------------------------------------------------------------------
        //  L-24  Growing degree days. Two bases, because a cool-season grass and
        //  a warm-season crop do not start growing at the same temperature.
        // ---------------------------------------------------------------------

        /// <summary>degC. GDD base for temperate and cool-season species.</summary>
        public const float GddBaseTemperateC = 5f;

        /// <summary>degC. GDD base for warm-season species.</summary>
        public const float GddBaseWarmSeasonC = 10f;

        // ---------------------------------------------------------------------
        //  L-26  Frost-free fraction. Water freezes at one temperature and the
        //  threshold is not authored, so it is named rather than written as 0.
        // ---------------------------------------------------------------------

        /// <summary>degC. Freezing point of water at one atmosphere.</summary>
        public const float FreezingPointC = UniverseCreator.Weather.AtmoConstants.WaterFreezingPointC;

        // ---------------------------------------------------------------------
        //  L-17  Beer-Lambert canopy extinction. I = I0 * exp(-k * LAI).
        // ---------------------------------------------------------------------

        /// <summary>Dimensionless. Extinction coefficient for a spherical (random) leaf angle distribution.</summary>
        public const float BeerLambertDefaultK = 0.5f;

        /// <summary>m2/m2. Upper clamp on leaf area index; denser than any measured closed canopy.</summary>
        public const float LeafAreaIndexMax = 12f;

        // ---------------------------------------------------------------------
        //  L-18  Reineke self-thinning. Nmax = k * meanDbh^exponent.
        //  The exponent is close to universal across species; k is the species
        //  dial and is authored per envelope.
        // ---------------------------------------------------------------------

        /// <summary>Dimensionless. Reineke (1933) self-thinning exponent.</summary>
        public const float ReinekeExponent = -1.605f;

        /// <summary>stems/ha at 1 m mean DBH. Default Reineke coefficient for a temperate stand.</summary>
        public const float ReinekeDefaultK = 25000f;

        /// <summary>m. Floor on mean DBH so the negative-exponent power stays finite.</summary>
        public const float ReinekeMinDbhM = 0.01f;

        // ---------------------------------------------------------------------
        //  L-19  Crown allometry. rCrown = a * DBH^b. Spacing follows crown
        //  geometry, so these two numbers decide how a forest reads at a glance.
        // ---------------------------------------------------------------------

        /// <summary>m per m^b. Broadleaf crown-radius coefficient.</summary>
        public const float CrownAllometryDefaultA = 1.9f;

        /// <summary>Dimensionless. Broadleaf crown-radius exponent.</summary>
        public const float CrownAllometryDefaultB = 0.6f;

        // ---------------------------------------------------------------------
        //  L-21  Seed dispersal kernel. p(d) proportional to exp(-d / lambda).
        //  Small lambda clumps, large lambda spreads: this is the field that
        //  makes a stand read as grown rather than sprinkled.
        // ---------------------------------------------------------------------

        /// <summary>m. Default dispersal kernel scale length.</summary>
        public const float DispersalDefaultLambdaM = 25f;

        /// <summary>Multiples of lambda. Beyond 5 lambda the kernel is under 0.7 percent; searching further buys nothing.</summary>
        public const float DispersalCutoffLambdaMultiple = 5f;

        // ---------------------------------------------------------------------
        //  L-25  Salt spray decay. s(d) = exp(-d / d0) from the shoreline.
        //  This one number produces the whole coastal band: salt scrub, then
        //  pines, then inland forest.
        // ---------------------------------------------------------------------

        /// <summary>m. Salt-spray e-folding distance inland from the shore.</summary>
        public const float SaltSprayDecayMetres = 120f;

        // ---------------------------------------------------------------------
        //  L-25b  Body salinity, which is NOT the same quantity as spray.
        //
        //  Spray is an above-water process: wind lifts droplets off a breaking sea
        //  and deposits salt on foliage and soil downwind, decaying with distance.
        //  Below a water surface, salinity is instead a property of the body you
        //  are standing in, and a freshwater lake 50 m behind a sea cliff is fresh
        //  no matter how much spray crosses it.
        //
        //  Measured reference figures: open seawater 35 g/kg, fresh water below
        //  0.5 g/kg, brackish between the two.
        // ---------------------------------------------------------------------

        /// <summary>g/kg. Mean salinity of open seawater.</summary>
        public const float SeawaterSalinityGramsPerKg = 35f;

        /// <summary>g/kg. Upper bound of the fresh class; above this a body is brackish.</summary>
        public const float FreshWaterSalinityGramsPerKg = 0.5f;

        /// <summary>
        /// Salinity of a fresh body as a fraction of seawater: 0.5 / 35. Reported at
        /// the top of the fresh class rather than at zero, because "fresh" means
        /// below 0.5 g/kg rather than salt-free, and distilled water is not a habitat.
        /// </summary>
        public const float FreshWaterSalinityFraction =
            FreshWaterSalinityGramsPerKg / SeawaterSalinityGramsPerKg;

        // ---------------------------------------------------------------------
        //  L-20  Poisson-disc spacing from density. r = sqrt(1 / (pi * rho)).
        // ---------------------------------------------------------------------

        /// <summary>stems/m2. Density floor, so an empty stand yields a large finite spacing rather than an infinity.</summary>
        public const float PoissonMinDensityPerSquareMetre = 1e-6f;

        // ---------------------------------------------------------------------
        //  L-23  Angle of repose. The slope above which loose material cannot
        //  rest, so above it the site is scree and holds stone, not stems.
        // ---------------------------------------------------------------------

        /// <summary>deg. Loose angular scree.</summary>
        public const float AngleOfReposeDegrees = 35f;

        /// <summary>deg. Rounded gravel; rounder grains interlock less and rest shallower.</summary>
        public const float AngleOfReposeRoundedGravelDegrees = 30f;

        /// <summary>deg. Wet sand; capillary suction between grains holds a steeper face than dry sand.</summary>
        public const float AngleOfReposeWetSandDegrees = 45f;

        // ---------------------------------------------------------------------
        //  Regolith thickness. Soil is produced from bedrock and then carried
        //  downhill, so its thickness is set by the balance of the two: steep
        //  ground loses it faster than weathering makes it, and convex ground
        //  sheds it in every direction at once while hollows collect what the
        //  slopes above shed. Hence a slope term and a curvature term, not one
        //  authored depth.
        // ---------------------------------------------------------------------

        /// <summary>Dimensionless tangent. Slope at which transport thins the regolith to 1/e of its flat-ground depth; tan 30 deg.</summary>
        public const float SoilDepthSlopeDecayTangent = 0.577f;

        /// <summary>1/m. Curvature treated as fully convex or fully concave; beyond this the terms saturate.</summary>
        public const float SoilDepthCurvatureSpanPerMetre = 0.02f;

        /// <summary>Dimensionless. Depth a fully convex site loses, and a fully concave one gains.</summary>
        public const float SoilDepthConvexityLoss = 0.6f;

        /// <summary>m. Depth floor. Bare rock still carries the dust in its cracks, and nothing above zero divides badly.</summary>
        public const float SoilDepthMinMetres = 0.01f;

        /// <summary>deg. How far below the angle of repose bare stone starts to show, so cover thins in over a band rather than at one contour.</summary>
        public const float RockinessOnsetBelowReposeDegrees = 10f;

        /// <summary>m. Regolith thickness that just buries the bedrock; thinner than this and stone is at the surface.</summary>
        public const float RockinessSoilCoverMetres = 0.1f;

        /// <summary>deg. Aspect reported on ground too flat to face anywhere. Negative so it cannot be mistaken for north, which is the GIS convention.</summary>
        public const float AspectFlatSentinelDegrees = -1f;

        /// <summary>Dimensionless tangent. Below this gradient the ground is flat and its aspect is the sentinel, not a direction read out of rounding error.</summary>
        public const float AspectFlatSlopeTangent = 1e-4f;

        /// <summary>m. Water-table depth reported where no body is in reach. Deeper than any root in the library, so it fails every water test without a special case.</summary>
        public const float WaterTableUnreachableDepthMetres = 100f;

        // ---------------------------------------------------------------------
        //  Topographic shelter. A ridge upwind of a site takes the force out of
        //  the wind that reaches it, which is why the lee of a crest carries
        //  timber and the crest itself carries none. Shelter is measured as the
        //  angle to the skyline: ground that rises steeply upwind blocks a lot,
        //  ground that rises gently blocks little, and level ground blocks none.
        // ---------------------------------------------------------------------

        /// <summary>Compass directions sampled around the upwind arc. Eight resolves a ridge's orientation without the cost of sixteen.</summary>
        public const int WindExposureDirectionCount = 8;

        /// <summary>m. How far upwind the skyline is looked for. Past half a kilometre a ridge no longer shelters ground at its foot.</summary>
        public const float WindExposureMaxDistanceMetres = 500f;

        /// <summary>Samples taken along each direction, spaced geometrically so near ground is examined finely and far ground coarsely.</summary>
        public const int WindExposureDistanceSamples = 6;

        /// <summary>deg. Skyline angle counted as full shelter. Ten degrees of rise upwind is already enough to break the wind.</summary>
        public const float WindExposureShelterAngleDegrees = 10f;

        /// <summary>deg. Half-width of the upwind arc: the windward half of the compass, centred on where the wind comes from.</summary>
        public const float WindExposureArcHalfWidthDegrees = 90f;

        // ---------------------------------------------------------------------
        //  The shore band. Two heights measured from the sea surface, which is
        //  what separates a mangrove from the dune grass behind it.
        // ---------------------------------------------------------------------

        /// <summary>m. Mean ocean tidal range. Ground within this of sea level is submerged and exposed twice a day.</summary>
        public const float TidalRangeMetres = 2f;

        /// <summary>m. Height above the tidal band still reached by spray and storm wash: salt-pruned scrub, no closed canopy.</summary>
        public const float SplashZoneHeightMetres = 5f;

        // ---------------------------------------------------------------------
        //  L-12  Topographic wetness index. TWI = ln(a / tan(beta)).
        //  Both guards are load-bearing: flat ground drives tan(beta) to zero and
        //  the index to infinity, and ln of a non-positive area is undefined.
        // ---------------------------------------------------------------------

        /// <summary>rad. Slope floor for TWI, about 0.057 deg.</summary>
        public const float TwiMinSlopeRadians = 0.001f;

        /// <summary>m2 per m of contour. Contributing-area floor: one cell contributes to itself.</summary>
        public const float TwiMinCatchmentArea = 1f;

        // ---------------------------------------------------------------------
        //  L-13  Potential evapotranspiration (Hargreaves).
        //  PET = 0.0023 * Ra * (Tmean + 17.8) * sqrt(Tmax - Tmin)  mm/day
        // ---------------------------------------------------------------------

        /// <summary>Dimensionless. Hargreaves-Samani coefficient.</summary>
        public const float HargreavesCoefficient = UniverseCreator.Weather.AtmoConstants.HargreavesCoefficient;

        /// <summary>degC. Hargreaves temperature offset.</summary>
        public const float HargreavesTempOffsetC = UniverseCreator.Weather.AtmoConstants.HargreavesTempOffsetC;

        /// <summary>MJ/kg at about 20 degC. Divides radiation in MJ/m2/day into an equivalent water depth, since 1 kg/m2 of water is 1 mm.</summary>
        public const float LatentHeatVaporisationMjPerKg = UniverseCreator.Weather.AtmoConstants.LatentHeatVaporisationMjPerKg;

        // ---------------------------------------------------------------------
        //  L-14  Aridity index. AI = P / PET, the one number that separates
        //  desert from steppe from forest.
        // ---------------------------------------------------------------------

        /// <summary>mm/yr. PET floor, so a frozen site reports a high index rather than a division by zero.</summary>
        public const float AridityMinPetMmPerYear = UniverseCreator.Weather.AtmoConstants.AridityMinPetMmPerYear;

        /// <summary>Dimensionless. Reported ceiling on the aridity index; above this the site is water-limited by nothing.</summary>
        public const float AridityMaxIndex = UniverseCreator.Weather.AtmoConstants.AridityMaxIndex;

        // ---------------------------------------------------------------------
        //  L-15  Gaussian niche tolerance curve.
        //  f(x) = exp(-(x - opt)^2 / (2 sigma^2)), sigma from the authored
        //  half-width. Halving is the convention: at x = opt +/- tolerance the
        //  curve reads about 0.135, which is the edge of the species' range
        //  rather than its extinction point.
        // ---------------------------------------------------------------------

        /// <summary>Dimensionless. sigma = tolerance * this.</summary>
        public const float ToleranceToSigma = 0.5f;

        /// <summary>Same unit as the tolerance. Sigma floor, so a zero-width envelope is a step, not a NaN.</summary>
        public const float ToleranceMinSigma = 1e-4f;

        // ---------------------------------------------------------------------
        //  Aspect geometry (section 6.3). Warm aspect is equator-facing, so the
        //  sign flips across the equator. Getting this wrong inverts every
        //  mountainside in the southern hemisphere.
        // ---------------------------------------------------------------------

        /// <summary>deg. Warm (equator-facing) aspect north of the equator: south-facing.</summary>
        public const float WarmAspectNorthDeg = 180f;

        /// <summary>deg. Warm (equator-facing) aspect south of the equator: north-facing.</summary>
        public const float WarmAspectSouthDeg = 0f;

        /// <summary>degC. Default slope-aspect temperature swing for a mid-latitude world, used until a world states its own.</summary>
        public const float AspectWarmingDefaultC = 2.5f;

        /// <summary>deg. One full turn of compass azimuth.</summary>
        public const float DegreesPerCircle = 360f;

        /// <summary>deg. Half a turn; the largest possible angular difference between two aspects.</summary>
        public const float DegreesHalfCircle = 180f;

        // ---------------------------------------------------------------------
        //  L-30, L-02  Astronomy.
        //
        //  OWNERSHIP, AND WHY IT RUNS THIS WAY ROUND.
        //  These names stay here because the treeline, phenology and day-length
        //  terms are ecological consumers and everything already written against
        //  EcoConstants.<name> must keep compiling. The VALUES, though, now live in
        //  AtmoConstants, and these forward to them.
        //
        //  The earlier note here said the opposite - that ClockMath should reference
        //  these. That could not stand, for two reasons. First, the verification
        //  rule in section 11.2 is testable and explicit: no type in Weather/ may
        //  reference Ecology/. A ClockMath that read EcoConstants would fail it.
        //  Second, W-15 names these same constants as members of AtmoConstants, so
        //  leaving both declared would have produced two values for one measured
        //  quantity - the exact duplication that eventually has one of the pair
        //  corrected and the other not.
        //
        //  Forwarding satisfies both: one value, one owner, every published name
        //  still alive, and the dependency arrow points Ecology -> Weather, which is
        //  the permitted direction.
        // ---------------------------------------------------------------------

        /// <summary>days. Synodic (new moon to new moon) month. Owned by AtmoConstants.</summary>
        public const float SynodicMonthDays = UniverseCreator.Weather.AtmoConstants.SynodicMonthDays;

        /// <summary>s. Sidereal day: one rotation relative to the stars, not to the sun.</summary>
        public const float SecondsPerSiderealDay = 86164.0905f;

        // ---------------------------------------------------------------------
        //  L-02  Solar geometry. Declination, day length and the extraterrestrial
        //  radiation the evapotranspiration law needs. All three are pure
        //  astronomy: they depend on latitude, axial tilt and the day of the year
        //  and on nothing else, which is why a world's climate can be derived
        //  rather than authored. The lighting manager owns the authored tilt and
        //  latitude; these are the constants it computes with.
        // ---------------------------------------------------------------------

        /// <summary>deg. Earth's obliquity. Authored per world, because a world with no tilt has no seasons. Owned by AtmoConstants.</summary>
        public const float EarthAxialTiltDegrees = UniverseCreator.Weather.AtmoConstants.EarthAxialTiltDegrees;

        /// <summary>MJ/m2/min. Solar constant: the radiation arriving at the top of the atmosphere. Owned by AtmoConstants.</summary>
        public const float SolarConstantMjPerM2Min = UniverseCreator.Weather.AtmoConstants.SolarConstantMjPerM2PerMin;

        /// <summary>Dimensionless. Amplitude of the orbital-eccentricity correction to solar distance. Owned by AtmoConstants.</summary>
        public const float OrbitEccentricityFactor = UniverseCreator.Weather.AtmoConstants.OrbitEccentricityFactor;

        /// <summary>min per day. Converts the solar constant's per-minute rate into a daily total. Owned by AtmoConstants.</summary>
        public const float MinutesPerDay = UniverseCreator.Weather.AtmoConstants.MinutesPerDay;

        /// <summary>h per day. Used to express the sunrise hour angle as a day length. Owned by ClockState.</summary>
        public const float HoursPerDay = UniverseCreator.Weather.ClockState.HoursPerDay;

        // ---------------------------------------------------------------------
        //  Stand structure (V-14 … V-18). The numbers that turn a species
        //  description into a stem with a measurable trunk, crown and height,
        //  and that decide how much room that stem takes from its neighbours.
        // ---------------------------------------------------------------------

        /// <summary>m. Cell size of the canopy LAI grid. 4 m is one crown across - fine enough that a clearing reads as a clearing, coarse enough that a 1 km chunk is a 250-square grid rather than a million cells.</summary>
        public const float CanopyGridCellMetres = 4f;

        /// <summary>Dimensionless. Exponent of the height-diameter curve, h proportional to DBH^0.66. Real stems put on girth faster than height as they mature, which is why a big tree is stout rather than a scaled-up sapling.</summary>
        public const float HeightDiameterExponent = 0.66f;

        /// <summary>Fraction. Crowns may overlap this much before one of the pair is dropped. Real canopies interlace at the edges; demanding zero overlap produces visible gaps between every tree.</summary>
        public const float CrownInterlaceFraction = 0.15f;

        /// <summary>m. Cell size of the deterministic parent lattice the dispersal kernel measures against. One cell per two mature crowns, so a grove has somewhere to start.</summary>
        public const float ParentLatticeCellMetres = 32f;

        /// <summary>Cells. Radius of the parent-lattice neighbourhood searched per candidate. Three covers a 200 m lambda without walking the whole chunk.</summary>
        public const int ParentLatticeSearchCells = 3;

        /// <summary>Fraction. Floor under the dispersal kernel - long-distance seeding by bird, flood and hoof. Without it a species is absolutely absent beyond a few lambda and groves acquire visible circular edges.</summary>
        public const float DispersalBaselineFraction = 0.03f;

        // ---------------------------------------------------------------------
        //  Loose material (V-19). Where debris comes to rest, and how it sorts
        //  itself once it gets there.
        // ---------------------------------------------------------------------

        /// <summary>m. Distance below a cliff base over which talus grades from fines to blocks. The largest blocks carry furthest, so the toe of a real scree cone is the coarsest part of it.</summary>
        public const float TalusGradingLengthMetres = 60f;

        /// <summary>Fraction of the repose angle at which loose material begins to be shed rather than resting. Below this it is stable; between here and repose it thins out.</summary>
        public const float ReposeStabilityOnsetFraction = 0.75f;

        /// <summary>m. Altitude above which erratics are considered glacially delivered. Authored per world in principle; this is the default for an Earth-like temperate latitude.</summary>
        public const float GlaciationAltitudeMetres = 900f;

        /// <summary>m. Width of the shingle band above the waterline that wave energy sorts. Beyond it the beach is no longer being reworked.</summary>
        public const float ShingleBandMetres = 12f;

        // ---------------------------------------------------------------------
        //  ECOTONE WIDTHS  (V-25)
        //
        //  Every transition in a landscape has a width, and the widths differ by two
        //  orders of magnitude between one boundary and another. A treeline resolves
        //  over about a hundred metres of altitude. A riparian edge resolves over a
        //  few metres, because that is how fast the water table drops away from a
        //  bank. A coastal salt gradient runs for a hundred metres or more inland. A
        //  boundary between two biomes on flat ground is a dozen metres of mixing.
        //
        //  WHY THEY ARE NOT ONE BLEND SLIDER. A single shared blend width is the
        //  reason generated worlds have a characteristic look: whatever value it is
        //  set to, three of the four transitions are wrong. Set it narrow and the
        //  treeline becomes a contour line drawn round the mountain. Set it wide and
        //  the river bank dissolves into a fifty-metre smear of half-willows. They
        //  are separate constants because they are separate measurements.
        // ---------------------------------------------------------------------

        /// <summary>m of altitude over which the treeline resolves. Krummholz occupies this band: stems present but dwarfed.</summary>
        public const float EcotoneTreelineMetres = 100f;

        /// <summary>
        /// degC half-width of the treeline band, computed from its altitude width and
        /// the lapse rate rather than authored. Two numbers for one boundary is two
        /// chances to disagree, and the temperature gate is what actually reads it.
        /// </summary>
        public const float EcotoneTreelineBandC =
            EcotoneTreelineMetres * LapseRateCPerKm / 1000f;

        /// <summary>m from a bank over which riparian conditions give out. Short, because the water table falls away steeply.</summary>
        public const float EcotoneRiparianMetres = 4f;

        /// <summary>m inland over which the coastal salt gradient resolves. The same distance the spray itself carries (L-25).</summary>
        public const float EcotoneCoastalMetres = SaltSprayDecayMetres;

        /// <summary>m over which two biomes interdigitate on level ground. Metres, not hundreds of metres - a real biome edge is sharp where relief does not soften it.</summary>
        public const float EcotoneBiomeMetres = 12f;

        /// <summary>
        /// degC of cooling per degree of latitude poleward, in the mid-latitudes.
        ///
        /// Paired with the lapse rate this is the whole of V-26's equivalence: 1000 m
        /// of altitude costs 6.5 degC, and 6.5 / 0.7 is about 9 degrees of latitude.
        /// Nothing in the system enforces that ratio - it falls out of these two
        /// numbers being used consistently, which is exactly why checking for it is a
        /// test of the thermal chain rather than a test of a formula.
        /// </summary>
        public const float MeridionalGradientCPerDegreeLatitude = 0.7f;
    }
}
