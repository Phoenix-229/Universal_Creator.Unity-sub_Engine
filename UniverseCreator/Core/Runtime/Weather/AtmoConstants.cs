// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    AtmoConstants.cs
//  Purpose: Every atmospheric and astronomical constant the weather layer uses,
//           named, with its unit and the law or source it serves.
//
//  WHY THIS FILE EXISTS
//  --------------------
//  A bare 0.0065f in a method body is a defect even when the value is right,
//  because the next reader cannot tell a measured constant from a fudge factor and
//  cannot tell metres from kilometres. Every number below is either a published
//  measurement, an exact unit conversion, or a coefficient of a named empirical
//  fit - and in the last case the fit is named so a reviewer can look it up rather
//  than trust it.
//
//  THE RULE THIS FILE ENFORCES (C-16)
//  No other file in Weather/ may contain a float literal other than 0, 1, 0.5 and
//  loop bounds. If a number is needed there, it is added here first.
//
//  WHY THIS FILE, AND NOT Ecology/EcoConstants.cs
//  ----------------------------------------------
//  Several of these constants are also ecological inputs, and EcoConstants
//  declared them first because the ecology sampler had to work before a weather
//  machine existed. They cannot simply be shared in that direction: the
//  cross-module dependency rule is that no type in Weather/ references Ecology/,
//  because the atmosphere must not depend on the biology standing in it.
//
//  So the ownership is resolved the other way round. This file is the single
//  owner of the physical constants, and EcoConstants forwards the ones it
//  published to these, keeping its own field names alive for everything already
//  written against them. One value, one owner, no second copy to drift.
//
//  Law IDs (L-nn) refer to the law catalogue in the Module 3 task list, section 1.1.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Atmospheric, thermodynamic and astronomical constants. Static, immutable,
    /// thread-safe: the climate-normals builder reads these from worker threads.
    /// </summary>
    public static class AtmoConstants
    {
        // =====================================================================
        //  UNIT CONVERSIONS AND GEOMETRY
        //  Exact by definition, not measured. Declared because a bare 1000f in a
        //  lapse-rate line is the difference between metres and kilometres, and
        //  that mistake is a factor of a thousand in a temperature.
        // =====================================================================

        /// <summary>m per km. Lapse rates are published per kilometre; terrain is in metres.</summary>
        public const float MetresPerKilometre = 1000f;

        /// <summary>mm per m. Rain rates are published in millimetres; puddle depth is in metres.</summary>
        public const float MillimetresPerMetre = 1000f;

        /// <summary>deg. One full turn of compass azimuth.</summary>
        public const float DegreesPerCircle = 360f;

        /// <summary>deg. Half a turn.</summary>
        public const float DegreesHalfCircle = 180f;

        /// <summary>min per day. Converts a per-minute solar rate into a daily total.</summary>
        public const float MinutesPerDay = 1440f;

        /// <summary>min per hour.</summary>
        public const float MinutesPerHour = 60f;

        /// <summary>s per minute.</summary>
        public const float SecondsPerMinute = 60f;

        /// <summary>s per hour. A rain rate in mm/h becomes a depth rate per second over this.</summary>
        public const float SecondsPerHour = MinutesPerHour * SecondsPerMinute;

        /// <summary>s per day. A PET in mm/day becomes an evaporation depth rate per second over this.</summary>
        public const float SecondsPerDay = MinutesPerDay * SecondsPerMinute;

        /// <summary>h. Local clock hour of mean midday, the reference solar noon is measured against.</summary>
        public const float NoonHour = 12f;

        /// <summary>K at 0 degC. Absolute-temperature offset, exact by definition since 1954.</summary>
        public const float KelvinAtZeroCelsius = 273.15f;

        /// <summary>degC. Freezing point of water at one atmosphere.</summary>
        public const float WaterFreezingPointC = 0f;

        // =====================================================================
        //  L-01, L-02, L-03  SOLAR GEOMETRY
        //  The astronomy every season, day length and radiation term derives from.
        //  These depend on axial tilt and the day of the year and on nothing else,
        //  which is why a world's climate can be derived rather than authored.
        // =====================================================================

        /// <summary>deg. Earth's obliquity. The entire mechanism of the seasons: a world with no tilt has none.</summary>
        public const float EarthAxialTiltDegrees = 23.44f;

        /// <summary>days. Length of the calendar year the declination formula is fitted against.</summary>
        public const float DaysPerCalendarYear = 365f;

        /// <summary>days. Days in a full circle of the year, for phase normalisation. Tropical year.</summary>
        public const float DaysPerTropicalYear = 365.2422f;

        /// <summary>
        /// days. Offset in the declination cosine, N + 10. It is the gap between the
        /// December solstice and 1 January: the formula is a cosine that must peak
        /// at the solstice, not at new year. (L-01)
        /// </summary>
        public const float DeclinationDayOffset = 10f;

        /// <summary>deg per hour. Earth turns 360 deg in 24 h, so the sun's hour angle moves 15 deg each hour.</summary>
        public const float DegreesPerHourOfHourAngle = 15f;

        /// <summary>
        /// deg. Latitude is clamped to this before any tan() of it is taken.
        ///
        /// WHY THIS EXISTS. Day length needs acos(-tan(lat) * tan(dec)). At exactly
        /// +/-90 deg tan is infinite, and on the one day of the year when declination
        /// is also exactly zero the product is inf * 0, which is NaN - not a large
        /// number, not a clamped number, NaN. That NaN would pass straight through
        /// the acos clamp, into temperature, then moisture, then every species
        /// suitability, and the first visible symptom would be an empty world with
        /// nothing in the console.
        ///
        /// In float arithmetic Deg2Rad * 90 is not exactly pi/2, so today the product
        /// happens to come out finite. Relying on that is relying on a rounding
        /// accident. A tenth of a degree of latitude is roughly 11 km, which is below
        /// the resolution of anything in this module, so the clamp costs nothing real.
        /// </summary>
        public const float LatitudeTanGuardDeg = 89.9f;

        /// <summary>
        /// min per degree of longitude. 1440 min / 360 deg. Converts the gap between
        /// a site and its time-zone meridian into the clock offset of its true noon.
        /// </summary>
        public const float MinutesPerDegreeLongitude = 4f;

        /// <summary>Dimensionless. Amplitude of the orbital-eccentricity correction to solar distance, FAO-56.</summary>
        public const float OrbitEccentricityFactor = 0.033f;        /// <summary>MJ/m2/min. Solar constant as FAO-56 publishes it, for the Ra formula.</summary>
        public const float SolarConstantMjPerM2PerMin = 0.0820f;

        /// <summary>W/m2. Solar constant in SI, for radiative terms. WMO accepted value.</summary>
        public const float SolarConstantWm2 = 1361f;

        // ---------------------------------------------------------------------
        //  EQUATION OF TIME (Spencer / NOAA short form)
        //  EoT = 9.87 sin 2B - 7.53 cos B - 1.5 sin B,  B = 360(N - 81)/364
        //
        //  SIGN CONVENTION, stated once and used everywhere: POSITIVE means the
        //  true sun runs AHEAD of clock time, so solar noon falls earlier than
        //  12:00. Getting this backwards puts sunrise about a quarter of an hour
        //  out in spring and autumn and nothing in the world points at the cause.
        // ---------------------------------------------------------------------

        /// <summary>min. Coefficient of the sin(2B) term.</summary>
        public const float EquationOfTimeSin2BMinutes = 9.87f;

        /// <summary>min. Coefficient of the cos(B) term.</summary>
        public const float EquationOfTimeCosBMinutes = 7.53f;

        /// <summary>min. Coefficient of the sin(B) term.</summary>
        public const float EquationOfTimeSinBMinutes = 1.5f;

        /// <summary>days. Day number the B angle is measured from, near the vernal equinox.</summary>
        public const float EquationOfTimeDayOffset = 81f;

        /// <summary>days. Divisor of the B angle. 364, not 365 - it is part of the published fit, not a typo.</summary>
        public const float EquationOfTimeDayDivisor = 364f;

        // ---------------------------------------------------------------------
        //  L-27  RELATIVE AIR MASS (Kasten-style guard)
        //  m = 1 / max(floor, sin(max(alpha, horizonFloor)) + offset)
        //
        //  The naive 1/sin(alpha) goes to infinity as the sun touches the horizon
        //  and takes every extinction and sky-colour term with it. These three
        //  numbers are what keep a sunset finite.
        // ---------------------------------------------------------------------

        /// <summary>
        /// deg. Elevation at which civil twilight ends. NOT an input to the air-mass
        /// formula - see the Kasten-Young note below for why it cannot be one. This is
        /// the "the sun is effectively down" threshold that sky and lighting gate on.
        /// </summary>
        public const float AirMassHorizonFloorDeg = -6f;

        /// <summary>
        /// Dimensionless. Floor under the air-mass denominator. A backstop only, and it
        /// MUST stay below the horizon value the formula itself produces: at a
        /// zero-elevation sun the denominator is sin(0) + 0.50572·6.07995^-1.6364 ≈
        /// 0.0264, which is the air mass of 37.9 the horizon is supposed to give. A
        /// floor at or above that would clip the sunset - a floor of 0.05, for one,
        /// caps air mass at 20 and flattens the low-sun curve. This value sits under
        /// 0.0264 so it never shapes a valid (alpha ≥ 0) call and only catches a
        /// degenerate argument.
        /// </summary>
        public const float AirMassDenominatorFloor = 0.02f;

        /// <summary>
        /// Dimensionless. Near-zero threshold for the azimuth denominator cos(a)·cos(phi),
        /// below which the bearing is degenerate (at the poles cos phi vanishes, at the
        /// zenith cos a does) and the hour-angle fallback is used instead. Its own
        /// constant: it guards a different quantity from the air-mass floor and the two
        /// must not track each other.
        /// </summary>
        public const float AzimuthDenominatorFloor = 0.05f;

        // ---------------------------------------------------------------------
        //  KASTEN-YOUNG (1989) RELATIVE AIR MASS
        //
        //      m = 1 / ( sin(a) + 0.50572 * (a_deg + 6.07995) ^ -1.6364 )
        //
        //  WHY THIS AND NOT THE SIMPLER GUARDED 1/sin FORM.
        //  The cheap form 1/(sin a + 0.15) returns 0.87 at the zenith and 6.7 at the
        //  horizon. Air mass is BY DEFINITION 1.0 looking straight up - it is a
        //  multiple of the vertical path - so 0.87 is not an approximation, it is a
        //  13 % error in the number that scales atmospheric extinction. It would show
        //  up as a noon sky that is permanently slightly hazy, everywhere, forever,
        //  with nothing to point at. Kasten-Young costs one pow() and gives 0.9997 at
        //  the zenith and 37.9 at the horizon, which is the real figure.
        //
        //  VALID FOR a >= 0 ONLY. Below the horizon the offset term's base goes under
        //  1, the negative exponent inverts it, and the formula returns an air mass
        //  BELOW 1 for a sun that is down - the opposite of the truth. Callers must
        //  clamp elevation at 0 before evaluating, which is also the physically right
        //  answer: the horizon path is the longest one there is.
        // ---------------------------------------------------------------------

        /// <summary>Dimensionless. Kasten-Young leading coefficient on the low-sun refraction term.</summary>
        public const float KastenYoungCoefficient = 0.50572f;

        /// <summary>deg. Kasten-Young elevation offset, keeping the base positive at a zero-elevation sun.</summary>
        public const float KastenYoungOffsetDeg = 6.07995f;

        /// <summary>Dimensionless. Kasten-Young exponent. Negative: the term grows as the sun drops.</summary>
        public const float KastenYoungExponent = -1.6364f;

        // =====================================================================
        //  L-30  LUNAR CYCLE
        // =====================================================================

        /// <summary>days. Synodic month, new moon to new moon.</summary>
        public const float SynodicMonthDays = 29.53059f;

        /// <summary>
        /// days. Fixed epoch: the absolute day number on which the phase is taken
        /// to be new. Any constant works as long as it never changes, because the
        /// phase is a fraction measured from it - but it must be a named constant
        /// and not an inline number, or the moon's calendar will differ between two
        /// files and the night will not match the sky.
        /// </summary>
        public const float LunarEpochAbsoluteDay = 6f;

        /// <summary>deg. Inclination of the lunar orbit to the ecliptic. Why the moon does not track the sun's path exactly.</summary>
        public const float LunarOrbitInclinationDeg = 5.145f;

        /// <summary>days. Sidereal month, for the moon's own progress against the stars.</summary>
        public const float SiderealMonthDays = 27.32166f;

        /// <summary>
        /// days. Draconic (nodal) month: the moon's return to the same node of its
        /// orbit, and therefore the period of its ecliptic LATITUDE - the number that
        /// decides how far north or south of the sun's path it rides. (W-07)
        ///
        /// SHORTER THAN THE SIDEREAL MONTH ON PURPOSE, AND NOT A TYPO. The nodes
        /// regress westward about a degree and a half a month, so the moon meets the
        /// node before it finishes a lap of the stars. Driving latitude off
        /// <see cref="SiderealMonthDays"/> instead would be within a tenth of a day
        /// per month and look right for a year, then drift a whole cycle out of step
        /// - the sort of error that only shows up as "the moon's height stopped
        /// matching its phase" long after anyone would connect the two.
        /// </summary>
        public const float DraconicMonthDays = 27.21222f;

        /// <summary>
        /// days. Fixed epoch: the absolute day number on which the moon is taken to
        /// be at its ascending node, so its ecliptic latitude is zero and rising.
        ///
        /// Separate from <see cref="LunarEpochAbsoluteDay"/> because the two cycles
        /// are independent - phase runs on the synodic month, latitude on the
        /// draconic one, and their beat is what makes eclipse seasons drift through
        /// the year. Sharing one epoch would lock them together and give a world a
        /// moon that was always full at the same height.
        /// </summary>
        public const float LunarNodeEpochAbsoluteDay = 0f;

        // =====================================================================
        //  L-04  LAPSE RATES
        //  T(h) = T0 - rate * h. The environmental rate is what a standing
        //  atmosphere shows; the adiabatic pair bracket it and serve the cloud-base
        //  and stability terms rather than terrain temperature.
        //
        //  M2's shipped TemperatureMapNode already applies the environmental rate
        //  to terrain. This is the same number, declared once - it must not become
        //  a second, independently authored lapse rate.
        // =====================================================================

        /// <summary>degC/km. ICAO standard atmosphere environmental lapse rate.</summary>
        public const float LapseRateCPerKm = 6.5f;

        /// <summary>degC/km. Dry adiabatic: unsaturated air lifted with no heat exchange.</summary>
        public const float DryAdiabaticCPerKm = 9.8f;

        /// <summary>degC/km. Saturated adiabatic, mid-latitude mean. Latent heat release halves the cooling.</summary>
        public const float SaturatedAdiabaticCPerKm = 5.0f;

        // =====================================================================
        //  L-10  PRESSURE AND DENSITY
        // =====================================================================

        /// <summary>hPa. ICAO standard sea-level pressure.</summary>
        public const float SeaLevelPressureHpa = 1013.25f;

        /// <summary>Dimensionless. Exponent of the hypsometric (barometric) formula.</summary>
        public const float HypsometricExponent = 5.25588f;

        /// <summary>1/m. Coefficient of the hypsometric formula's altitude term.</summary>
        public const float HypsometricCoefficient = 2.25577e-5f;

        /// <summary>J/kg/K. Specific gas constant for dry air, for the ideal-gas density.</summary>
        public const float DryAirGasConstantJPerKgK = 287.05f;

        /// <summary>Pa per hPa. The gas law wants pascals; meteorology publishes hectopascals.</summary>
        public const float PascalsPerHectopascal = 100f;

        /// <summary>hPa. Reference pressure of potential temperature, by convention 1000 hPa.</summary>
        public const float PotentialTemperatureReferenceHpa = 1000f;

        /// <summary>Dimensionless. Poisson exponent R_d/c_p for dry air, used by potential temperature (W-23).</summary>
        public const float PoissonExponentDryAir = 0.2857f;

        // =====================================================================
        //  L-05, L-06  SATURATION VAPOUR PRESSURE (Tetens) AND DEW POINT
        //  e_s = 0.6108 * exp(17.27 T / (T + 237.3))  kPa
        // =====================================================================

        /// <summary>kPa. Saturation vapour pressure at 0 degC: the Tetens leading coefficient.</summary>
        public const float TetensCoefficientKPa = 0.6108f;

        /// <summary>Dimensionless. Tetens numerator coefficient.</summary>
        public const float TetensA = 17.27f;

        /// <summary>degC. Tetens denominator offset.</summary>
        public const float TetensB = 237.3f;

        /// <summary>
        /// Dimensionless. The smallest denominator the inverted Tetens curve is
        /// allowed to divide by. Reached only above about 260 degC, so it never
        /// bends a real answer - it exists so a bad input cannot return infinity.
        /// </summary>
        public const float TetensMinDenominator = 1e-6f;

        // =====================================================================
        //  L-07  LIFTING CONDENSATION LEVEL (Espy)
        //  h_base = 125 m per degC of dew-point depression. The one number that
        //  decides how high the cloud base sits, and therefore whether a mountain
        //  is in cloud or under it.
        // =====================================================================

        /// <summary>m per degC of dew-point depression. Espy's rule for the LCL.</summary>
        public const float LclMetresPerCelsius = 125f;

        // =====================================================================
        //  L-08  WET-BULB TEMPERATURE (Stull 2011 single-pass fit)
        //  Valid roughly -20 degC to +50 degC and RH above about 5 %. Outside that
        //  the polynomial diverges, so AtmoMath clamps to these bounds and says so.
        // =====================================================================

        /// <summary>Coefficient of the first arctangent's radicand scale.</summary>
        public const float StullA = 0.151977f;

        /// <summary>Offset inside the first arctangent's radicand.</summary>
        public const float StullB = 8.313659f;

        /// <summary>Offset of the third arctangent term.</summary>
        public const float StullC = 1.676331f;

        /// <summary>Coefficient of the RH^1.5 term.</summary>
        public const float StullD = 0.00391838f;

        /// <summary>Scale inside the fourth arctangent.</summary>
        public const float StullE = 0.023101f;

        /// <summary>Constant offset of the fit, in degC.</summary>
        public const float StullF = 4.686035f;

        /// <summary>Exponent of the RH term in Stull's fit.</summary>
        public const float StullRhExponent = 1.5f;

        /// <summary>degC. Lower bound of Stull's validity.</summary>
        public const float WetBulbValidMinC = -20f;

        /// <summary>degC. Upper bound of Stull's validity.</summary>
        public const float WetBulbValidMaxC = 50f;

        /// <summary>Percent. Lower bound of Stull's validity in relative humidity, as the fit's own percent units.</summary>
        public const float WetBulbValidMinRhPercent = 5f;

        /// <summary>Percent per unit fraction. Stull's fit takes RH in percent; the codebase carries it as 0-1.</summary>
        public const float PercentPerFraction = 100f;

        // =====================================================================
        //  L-09  PRECIPITATION TYPE BY WET BULB
        //  Wet bulb, not air temperature, is what decides rain from snow: a
        //  snowflake falling through dry air is cooled by its own evaporation, so
        //  snow reaches the ground at air temperatures well above freezing. That
        //  is a real and common event and this pair of thresholds is what
        //  reproduces it.
        // =====================================================================

        /// <summary>degC wet bulb. At or below this, precipitation falls as snow.</summary>
        public const float SnowWetBulbThresholdC = 0.5f;

        /// <summary>degC wet bulb. Above this it is entirely rain; between the two it is sleet.</summary>
        public const float SleetUpperWetBulbC = 1.5f;

        // =====================================================================
        //  SNOW ACCUMULATION AND MELT (section 7.8)
        // =====================================================================

        /// <summary>mm water equivalent per degC per day. Degree-day melt factor, temperate mid-latitude mean.</summary>
        public const float DegreeDayMeltMmPerCPerDay = 2.7f;

        /// <summary>kg/m3. Density of liquid water, exact by definition; converts stored SWE into physical snow depth.</summary>
        public const float WaterDensityKgPerM3 = 1000f;

        /// <summary>kg/m3. Fresh fallen snow, full of trapped air, so 1 mm SWE stands ~10 mm deep.</summary>
        public const float SnowFreshDensityKgPerM3 = 100f;

        /// <summary>kg/m3. Snow that has settled and compacted over days under its own weight.</summary>
        public const float SnowSettledDensityKgPerM3 = 300f;

        /// <summary>kg/m3. Spring corn snow, melt-frozen and dense, the end state of an old pack.</summary>
        public const float SnowCornDensityKgPerM3 = 500f;

        /// <summary>days. Time constant for a fresh pack's density to relax toward the settled value.</summary>
        public const float SnowSettleTimeConstantDays = 3f;

        /// <summary>MJ per kg. Latent heat of fusion: the energy to melt one kilogram (one mm SWE) of ice.</summary>
        public const float SnowLatentHeatFusionMjPerKg = 0.334f;

        /// <summary>Dimensionless. Fraction of absorbed shortwave that goes to melt rather than longwave and turbulent loss.</summary>
        public const float RadiationMeltEfficiency01 = 0.35f;

        /// <summary>degC. Below this effective temperature the sun cannot bring the surface to melting, so radiation melt is off.</summary>
        public const float SnowMeltGateLowerC = -2f;

        /// <summary>degC. Width of the ramp over which radiation melt fades in as the surface nears melting.</summary>
        public const float SnowMeltGateWidthC = 3f;

        /// <summary>Dimensionless. Albedo of fresh snow: it reflects most sunlight, which is why it survives a sunny day.</summary>
        public const float SnowFreshAlbedo01 = 0.8f;

        /// <summary>Dimensionless. Albedo of old, dirty snow: it absorbs far more, which is why spring snow melts out.</summary>
        public const float SnowOldAlbedo01 = 0.4f;

        /// <summary>days. Time constant for snow albedo to decay from fresh toward old as the surface ages and soils.</summary>
        public const float SnowAlbedoDecayTimeConstantDays = 7f;

        /// <summary>deg. Slope at which snow begins to shed; below this it lies fully.</summary>
        public const float SnowSlopeShedStartDeg = 40f;

        /// <summary>deg. Slope at which snow sheds completely, so a cliff reads as bare rock.</summary>
        public const float SnowSlopeShedFullDeg = 60f;

        /// <summary>Multiplier. Deepest lee accumulation relative to open ground - what builds a cornice.</summary>
        public const float SnowLeeAccumulationMax = 3f;

        /// <summary>Multiplier. Snow left on a fully wind-scoured windward ridge; most of it is blown away.</summary>
        public const float SnowWindwardScourFloor = 0.1f;

        /// <summary>degC. Ground temperature at which fresh snow melts on contact and none accumulates.</summary>
        public const float SnowGroundMeltUpperC = 2f;

        /// <summary>Dimensionless. Reflectance of bare, dark soil - roughly a fifth of fresh snow, which is why a snowfall brightens a world so strongly.</summary>
        public const float SoilAlbedo01 = 0.15f;

        /// <summary>m. Depth over which lying snow buries and hides low grass and detail; a blade taller than this still shows through.</summary>
        public const float SnowGrassBuryDepthM = 0.2f;

        /// <summary>m. E-folding snow depth for thermal insulation: a pack this deep decouples the ground from the air by about two thirds.</summary>
        public const float SnowInsulationDepthScaleM = 0.1f;

        /// <summary>m/s. Terminal velocity of a raindrop of a few millimetres.</summary>
        public const float RainTerminalVelocityMPerS = 9.0f;

        /// <summary>m/s. Terminal velocity of a snowflake. Nine times slower than rain, which is why snow drifts and rain does not.</summary>
        public const float SnowTerminalVelocityMPerS = 1.0f;

        /// <summary>1/m3/mm. Marshall-Palmer intercept of the raindrop size distribution.</summary>
        public const float MarshallPalmerN0 = 8000f;

        // =====================================================================
        //  PRECIPITATION RENDERING (section 7.6)
        //  The visible drops and flakes, not the rain rate. Fall speed grows with
        //  drop size (Atlas-Ulbrich), the size mix follows Marshall-Palmer so hard
        //  rain is bigger drops and drizzle is a mist of small ones, the streak a
        //  drop draws is its fall speed across one shutter, and snow drifts far
        //  more than rain in the same wind.
        // =====================================================================

        /// <summary>m/s. Atlas-Ulbrich asymptote: the speed an arbitrarily large drop approaches.</summary>
        public const float RainFallSpeedAsymptoteMPerS = 9.65f;

        /// <summary>m/s. Atlas-Ulbrich span between a vanishing drop and the asymptote.</summary>
        public const float RainFallSpeedRangeMPerS = 10.3f;

        /// <summary>1/mm. Atlas-Ulbrich decay rate: how fast fall speed climbs with drop diameter.</summary>
        public const float RainFallSpeedDecayPerMm = 0.6f;

        /// <summary>Coefficient of the Marshall-Palmer slope Lambda = coeff * R^exponent.</summary>
        public const float MarshallPalmerSlopeCoeff = 4.1f;

        /// <summary>Exponent of rain rate in the Marshall-Palmer slope: heavier rain, gentler slope, bigger drops.</summary>
        public const float MarshallPalmerSlopeExponent = -0.21f;

        /// <summary>Numerator of the median-volume diameter D0 = factor / Lambda for an exponential drop spectrum.</summary>
        public const float MarshallPalmerMedianFactor = 3.67f;

        /// <summary>s. Exposure time a streak is drawn across, tying its length to a ~1/48 s shutter.</summary>
        public const float PrecipExposureSeconds = 1f / 48f;

        /// <summary>Fractional spread of snowflake fall speed, so flakes do not fall in lockstep.</summary>
        public const float SnowFallSpeedVariance01 = 0.4f;

        /// <summary>How much more than rain a snowflake advects with the wind, being light and slow.</summary>
        public const float SnowWindAdvectionGain = 3f;

        // =====================================================================
        //  SURFACE WETNESS (section 7.6)
        //  A surface wets fast when it rains and dries at a rate set by potential
        //  evapotranspiration, not by a constant. That one substitution is why the
        //  same shower dries in minutes on a hot afternoon and lingers a day in a
        //  cold damp December - the season lives entirely in the PET term, so no
        //  seasonal constant appears anywhere in the wetness code.
        // =====================================================================

        /// <summary>mm/h. Rain rate at which wetting proceeds at one reference unit per wetting time.</summary>
        public const float WetnessReferenceRainMmPerH = 10f;

        /// <summary>s. Seconds to fully wet a dry surface under the reference rain rate. Wetting is fast.</summary>
        public const float WetnessWetTimeSeconds = 60f;

        /// <summary>mm/day. PET at which drying proceeds at one reference unit per drying time.</summary>
        public const float WetnessReferencePetMmPerDay = 5f;

        /// <summary>s. Seconds to fully dry a wet surface under the reference PET. Drying is slow, and scales with PET.</summary>
        public const float WetnessDryTimeSeconds = 1800f;

        /// <summary>1/s. Ceiling on how fast wetness may change, so it can never jump in a single frame.</summary>
        public const float WetnessMaxRatePerSecond = 0.2f;

        // =====================================================================
        //  PUDDLING BY TOPOGRAPHY (section 7.6)
        //  Standing water collects where water collects: the flats and the
        //  hollows, never the slopes. Slope decides whether a puddle can sit at
        //  all - past a few degrees the water simply runs off - while the
        //  topographic wetness index (L-12) and concave curvature decide how much
        //  runoff converges on a cell from the ground above it, which is why a
        //  hollow pools far deeper than a flat under the same rain. Depth then
        //  drains into the soil at a rate set by its permeability and evaporates
        //  at the potential-evapotranspiration rate, the same PET that dries the
        //  surface wetness above. Every input is a deterministic function of the
        //  terrain, so the same hollows fill on every rebuild.
        // =====================================================================

        /// <summary>deg. Slope past which standing water sheds instead of pooling; a puddle needs near-flat ground.</summary>
        public const float PuddleHoldAngleDegrees = 5f;

        /// <summary>TWI below which a cell gathers no extra runoff: a ridge that sheds in every direction.</summary>
        public const float PuddleTwiGatherMin = 4f;

        /// <summary>TWI at which runoff convergence is full: a valley floor drawing a large upslope area.</summary>
        public const float PuddleTwiGatherFull = 10f;

        /// <summary>1/m. Concave curvature at which a hollow gathers fully; mirrors the ecology curvature span.</summary>
        public const float PuddleConcavityFullPerM = 0.02f;

        /// <summary>Extra multiple of the local rain a fully gathering hollow receives from upslope.</summary>
        public const float PuddleCatchmentMaxGain = 6f;

        /// <summary>m/s. Drain rate into fully permeable soil; scaled down by a cell's permeability toward zero on rock or clay.</summary>
        public const float PuddleInfiltrationRateMetresPerSecond = 1e-5f;

        /// <summary>m. Depth past which pooled water is a pond for the hydrology system, not a transient puddle.</summary>
        public const float PuddleMaxDepthMetres = 0.15f;

        // =====================================================================
        //  SURFACE TRACTION (section 7.6)
        //  Grip is a published number, not a force this module ever applies: it
        //  hands a vehicle or a character system a single 0..1 friction and lets
        //  that system decide what to do with it. Dry ground grips fully; a wet
        //  film gives some up; snow cover gives up more; and glaze ice - liquid
        //  water caught below freezing - is the slipperiest of all, well past
        //  snow, which is the one ordering a traction model has to get right.
        // =====================================================================

        /// <summary>Friction of fully wet ground: a rain-slick surface, grippier than snow but short of dry.</summary>
        public const float WetFrictionFloor = 0.7f;

        /// <summary>Friction of snow-covered ground: less grip than a wet road, far more than ice.</summary>
        public const float SnowFrictionFloor = 0.45f;

        /// <summary>m. Snow depth at which the ground reads as fully snow-covered for traction; a dusting counts for little.</summary>
        public const float SnowFrictionFullDepthM = 0.05f;

        /// <summary>Friction of glaze ice: the slipperiest surface, well below snow.</summary>
        public const float IceFriction01 = 0.1f;

        /// <summary>degC. Band below freezing over which liquid water glazes to ice, so grip does not snap at exactly zero.</summary>
        public const float IceFreezeBandC = 1f;

        // =====================================================================
        //  FOG BY FORMATION MECHANISM (section 7.7)
        //  Fog is not one density; it is four phenomena that form under different
        //  conditions and sit in different places. Each mechanism resolves to a
        //  0..1 surface density and the strongest wins - a valley can be under
        //  radiation fog or sea fog, not a sum of both. These are the bands each
        //  gate opens over.
        // =====================================================================

        /// <summary>degC. Dew-point depression within which the air counts as saturated enough to fog.</summary>
        public const float FogDewpointDepressionBandC = 2f;

        /// <summary>m/s. Above this the night air mixes and radiation fog cannot settle.</summary>
        public const float FogCalmWindMps = 3f;

        /// <summary>degrees. Sun elevation at which the morning burns radiation fog off.</summary>
        public const float FogBurnoffSunElevationDeg = 10f;

        /// <summary>Wetness index at which valley pooling of radiation fog begins.</summary>
        public const float FogValleyTwiMin = 6f;

        /// <summary>Wetness index at which valley pooling of radiation fog is full.</summary>
        public const float FogValleyTwiFull = 12f;

        /// <summary>0..1. Radiation fog an open flat still gets before any valley pooling is added.</summary>
        public const float FogValleyBase01 = 0.3f;

        /// <summary>degC. Air-warmer-than-surface spread over which advection fog reaches full strength.</summary>
        public const float FogAdvectionTempSpreadC = 3f;

        /// <summary>0..1. Relative humidity above which advection fog can form.</summary>
        public const float FogHumidityThreshold01 = 0.9f;

        /// <summary>m. Height above the cloud base over which upslope fog thickens to full.</summary>
        public const float FogUpslopeBandM = 200f;

        /// <summary>degC. Water-warmer-than-air spread over which steam fog reaches full strength.</summary>
        public const float FogSteamTempSpreadC = 5f;

        /// <summary>m. Vertical scale height of the fog layer: density falls by 1/e over this rise.</summary>
        public const float FogScaleHeightM = 60f;

        // =====================================================================
        //  VISIBILITY (section 7.7)
        //  Visibility distance in metres, set by fog and by precipitation and
        //  combined by adding their extinctions (never multiplying), so heavy
        //  snow is the murkiest condition and light rain barely changes the view.
        // =====================================================================

        /// <summary>m. Clear-air ceiling: the furthest visibility ever reported, with no fog or precipitation.</summary>
        public const float VisibilityClearMetres = 40000f;

        /// <summary>m. Visibility inside the thickest fog (density 1); thinner fog scales up from here.</summary>
        public const float FogVisibilityAtFullDensityMetres = 40f;

        /// <summary>mm/h. The heavy-precipitation rate the reference visibilities below are quoted at.</summary>
        public const float PrecipVisibilityReferenceMmPerH = 25f;

        /// <summary>m. Visibility in heavy rain at the reference rate: a downpour still leaves a couple of km.</summary>
        public const float RainVisibilityAtReferenceMetres = 2000f;

        /// <summary>m. Visibility in heavy snow at the reference rate: snow scatters far harder than rain.</summary>
        public const float SnowVisibilityAtReferenceMetres = 250f;

        /// <summary>Falloff exponent of visibility with precipitation rate (V scales as rate^-this).</summary>
        public const float PrecipVisibilityRateExponent = 0.5f;

        // =====================================================================
        //  PRECIPITATION INTENSITY (section 7.4)
        //  Three mechanisms set the rain rate - orographic lift, frontal ascent
        //  and convective instability. They combine as the strongest plus a
        //  small share of the rest, never a sum: three light showers do not add
        //  to a deluge. These are the reference rates each mechanism reaches.
        // =====================================================================

        /// <summary>mm/h. A warm front is wide and steady: gentle rain over many hours.</summary>
        public const float WarmFrontRainMmPerH = 2f;

        /// <summary>mm/h. A cold front is narrow and hard: a short, intense band.</summary>
        public const float ColdFrontRainMmPerH = 15f;

        /// <summary>mm/h. An occlusion, between the two in both width and rate.</summary>
        public const float OccludedFrontRainMmPerH = 4f;

        /// <summary>hPa. Depth of low at which the frontal rain rate saturates.</summary>
        public const float FrontRainReferenceDepthHpa = 15f;

        /// <summary>mm/h. A fully unstable, moist afternoon cell at its convective peak.</summary>
        public const float ConvectiveRainReferenceMmPerH = 30f;

        /// <summary>Fraction of the weaker mechanisms added onto the strongest, not summed.</summary>
        public const float PrecipBlendFraction = 0.15f;

        // =====================================================================
        //  WEATHER STATE MACHINE (section 7.5)
        //  The instantaneous physics is classified into one of four states, then
        //  the visible fields are eased between states so the sky never snaps.
        //  A minimum dwell holds a state briefly so a value sitting on a
        //  threshold cannot flicker between two states frame to frame.
        // =====================================================================

        /// <summary>mm/h. Below this the sky counts as dry: the threshold that separates rain and snow states from overcast and clear.</summary>
        public const float PrecipDetectableMmPerH = 0.1f;

        /// <summary>Cloud cover above which a dry sky reads Overcast rather than Clear.</summary>
        public const float OvercastCloudCover01 = 0.65f;

        /// <summary>s of simulated time. How long the visible fields take to ease from one weather state to the next.</summary>
        public const float WeatherTransitionSeconds = 20f;

        /// <summary>s of simulated time. The shortest a state may hold before it may change again, so a threshold case cannot flicker.</summary>
        public const float WeatherMinDwellSeconds = 30f;

        // =====================================================================
        //  STORM ACTIVITY AND LIGHTNING (section 7.5)
        //  A storm needs convective instability and moisture (that is the
        //  convective cover) and is deepened by a synoptic low. Lightning is a
        //  rate proportional to that activity; thunder trails the flash by the
        //  time sound takes to cross the distance.
        // =====================================================================

        /// <summary>hPa. The low depth at which the dynamic storm boost is full - a genuinely deep system.</summary>
        public const float StormReferenceDepthHpa = 20f;

        /// <summary>Extra storm intensity a full-depth low adds on top of the convective cover, as a fraction of it.</summary>
        public const float StormLowDepthGain = 1f;

        /// <summary>Storm activity below which no lightning fires at all - a stratus deck never flashes.</summary>
        public const float StormActivityThreshold01 = 0.15f;

        /// <summary>strikes/min. Expected strike rate at full storm activity, the ceiling the Poisson rate scales to.</summary>
        public const float LightningMaxStrikesPerMinute = 40f;

        /// <summary>m/s. Speed of sound in air, so thunder trails the flash by distance / this.</summary>
        public const float SpeedOfSoundMPerS = 343f;

        // =====================================================================
        //  AIR MASS IDENTITY (section 7.5)
        //  A system is tagged by where its air came from: the origin latitude
        //  sets the thermal class (tropical, polar, arctic) and the origin
        //  surface sets the moisture source (maritime or continental). The
        //  anomalies below are what that identity carries with it, so a front
        //  swaps in genuinely different air rather than only starting rain.
        // =====================================================================

        /// <summary>deg. Poleward of the Arctic Circle a system's air is Arctic, whatever the surface beneath it.</summary>
        public const float ArcticAirMassLatitudeDeg = 66.5f;

        /// <summary>degC. How much warmer tropical-origin air is than the local climatology it moves into.</summary>
        public const float TropicalAirTempAnomalyC = 8f;

        /// <summary>degC. How much colder polar-origin air is than the local climatology.</summary>
        public const float PolarAirTempAnomalyC = -8f;

        /// <summary>degC. Arctic air is colder still - the coldest air mass in the set.</summary>
        public const float ArcticAirTempAnomalyC = -16f;

        /// <summary>degC of dew-point rise. Maritime air carries the moisture of the sea it left.</summary>
        public const float MaritimeAirDewAnomalyC = 6f;

        /// <summary>degC of dew-point fall. Continental air left its moisture over land.</summary>
        public const float ContinentalAirDewAnomalyC = -6f;

        /// <summary>degC of dew-point fall. Arctic air is bone dry - cold air holds almost no moisture.</summary>
        public const float ArcticAirDewAnomalyC = -14f;

        // =====================================================================
        //  W-22  CLOUD FRACTION (Sundqvist)
        //  Cloud does not appear at 100 % relative humidity, because a grid cell
        //  averages over air that is not uniformly humid. It appears above a
        //  critical value, which is what this is.
        // =====================================================================

        /// <summary>Fraction. Relative humidity above which cloud begins to form in a cell.</summary>
        public const float SundqvistCriticalRh = 0.80f;

        /// <summary>m. Altitude of the low cloud deck - the cumulus and stratus base.</summary>
        public const float CloudLowLayerAltitudeM = 1500f;

        /// <summary>m. Altitude of the mid cloud deck - the altostratus and altocumulus.</summary>
        public const float CloudMidLayerAltitudeM = 5000f;

        /// <summary>m. Altitude of the high cloud deck - the cirrus and the anvil tops.</summary>
        public const float CloudHighLayerAltitudeM = 9000f;

        /// <summary>
        /// Fraction. Column relative humidity in an ascent regime - the ITCZ trough
        /// and the storm-track low, where convergence lifts moist air. Set so a deep
        /// column combines across the three decks to well above the wet-season target.
        /// </summary>
        public const float CloudWetColumnHumidity01 = 0.90f;

        /// <summary>
        /// Fraction. Column relative humidity in a descent regime - the subtropical
        /// ridge, where subsidence dries the whole column. Below the critical value on
        /// purpose, so a desert latitude carries no cloud at all.
        /// </summary>
        public const float CloudDryColumnHumidity01 = 0.30f;

        /// <summary>
        /// m. Scale height of water vapour in an ascent column: moisture is carried
        /// deep, so the mid and high decks can still saturate as the air cools.
        /// </summary>
        public const float CloudMoistureScaleHeightWetM = 3000f;

        /// <summary>
        /// m. Scale height of water vapour in a descent column: subsidence caps the
        /// moisture near the ground, so the air aloft is dry however cold it gets -
        /// which is what keeps the high deck off a desert instead of freezing it to
        /// saturation on the far-too-cold upper troposphere.
        /// </summary>
        public const float CloudMoistureScaleHeightDryM = 1200f;

        /// <summary>
        /// Fraction per hPa. How much the column humidity rises for each hPa the
        /// synoptic anomaly sits below the mean, so cover thickens smoothly as a low
        /// approaches rather than switching on when it arrives.
        /// </summary>
        public const float CloudAnomalyHumidityPerHpa = 0.01f;

        /// <summary>
        /// km. Range at which an approaching warm/occluded front first hangs high cloud:
        /// beyond this the front is too far to organise the sky, within it the high deck
        /// reads cirrus. The outermost band of the frontal cloud sequence.
        /// </summary>
        public const float CloudFrontCirrusRangeKm = 800f;

        /// <summary>
        /// km. Range within which the frontal cloud has thickened past thin cirrus into a
        /// milky veil: the high deck reads cirrostratus and the mid deck altostratus.
        /// </summary>
        public const float CloudFrontVeilRangeKm = 400f;

        /// <summary>
        /// km. Range within which the front is close enough for its deep nimbostratus and
        /// steady rain: the low deck reads nimbostratus rather than a fair-weather form.
        /// </summary>
        public const float CloudFrontRainRangeKm = 150f;

        /// <summary>Fraction. Column RH at or above which a stable low deck is a solid stratus sheet.</summary>
        public const float CloudStratusMinRh01 = 0.90f;

        /// <summary>m/s. Surface wind at or below which a stable, humid low deck settles into flat stratus rather than breaking into stratocumulus.</summary>
        public const float CloudStratusMaxWindMPerS = 4f;

        /// <summary>
        /// C/km. Lapse at or above which conditional instability is deep enough to grow a
        /// fair-weather cumulus into a towering congestus; between the saturated adiabat
        /// and this it stays humilis. Sits midway to the dry adiabat.
        /// </summary>
        public const float CloudCongestusMinLapseCPerKm = 7.5f;

        // =====================================================================
        //  W-18  GENERAL CIRCULATION
        //  The three latitude bands that decide where deserts and westerlies are.
        // =====================================================================

        /// <summary>deg. Amplitude of the ITCZ's seasonal migration either side of the equator.</summary>
        public const float ItczAmplitudeDeg = 8.0f;

        /// <summary>deg. Latitude of the subtropical high-pressure belt - the world's desert latitude.</summary>
        public const float SubtropicalHighLatitudeDeg = 30.0f;

        /// <summary>deg. Latitude of the subpolar low - the storm track.</summary>
        public const float SubpolarLowLatitudeDeg = 60.0f;

        /// <summary>
        /// Fraction. How far the ITCZ follows the sun, as a share of the solar
        /// declination. Derived rather than authored, which matters: a fixed
        /// migration in degrees would march the ITCZ up and down a world with no
        /// axial tilt, where there is no season for it to follow. Earth's observed
        /// migration at Earth's tilt is the one authored number, and the ratio is
        /// what carries to a world with a different tilt.
        /// </summary>
        public const float ItczMigrationFractionOfDeclination =
            ItczAmplitudeDeg / EarthAxialTiltDegrees;

        // ---------------------------------------------------------------------
        //  The zonal mean pressure profile, as offsets from the standard sea-level
        //  pressure rather than four absolute numbers. Offsets because the profile
        //  is a shape: an author who retunes the reference should move the whole
        //  atmosphere with it, not open a gap between the reference and the bands.
        //
        //  Zonal annual means, rounded: the equatorial trough near 1010, the
        //  subtropical ridge near 1019, the subpolar trough near 1001 (deeper than
        //  that over the southern ocean, shallower over the northern basins) and
        //  the polar cap near 1017.
        // ---------------------------------------------------------------------

        /// <summary>hPa. Offset at the ITCZ - convergence, so a trough.</summary>
        public const float ItczPressureOffsetHpa = -3f;

        /// <summary>hPa. Offset at the subtropical ridge - descent, so a high, and
        /// the reason the world's deserts sit where they do.</summary>
        public const float SubtropicalHighPressureOffsetHpa = 6f;

        /// <summary>hPa. Offset at the subpolar low - the storm track.</summary>
        public const float SubpolarLowPressureOffsetHpa = -12f;

        /// <summary>hPa. Offset at the poles - cold, dense, stable air.</summary>
        public const float PolarHighPressureOffsetHpa = 4f;

        // =====================================================================
        //  W-19  CORIOLIS
        // =====================================================================

        /// <summary>rad/s. Earth's angular velocity, for the Coriolis parameter f = 2 Omega sin(phi).</summary>
        public const float EarthAngularVelocityRadPerS = 7.2921e-5f;

        /// <summary>
        /// km. Half-width of the central difference the wind is taken from. An
        /// eighth of SynopticRadiusMinKm, so the tightest system the field can draw
        /// still spans sixteen probes across its diameter; and wide enough that the
        /// difference of two pressures a few hundredths of a hectopascal apart keeps
        /// its significant digits in a float.
        /// </summary>
        public const float GeostrophicProbeKm = 25f;

        /// <summary>
        /// deg. Latitude below which geostrophic balance stops describing the flow.
        /// The Coriolis parameter goes to zero at the equator, so the balanced speed
        /// goes to infinity there; inside this latitude the wind is blended toward
        /// straight down-gradient flow, which is what the tropics actually do. A
        /// latitude rather than a floor on f, because the latitude is the number with
        /// a meaning and the floor is derived from it and the world's rotation.
        /// </summary>
        public const float GeostrophicFloorLatitudeDeg = 10f;

        /// <summary>
        /// deg. How far friction turns the surface wind across the isobars toward
        /// low pressure. Observed at twenty over open water and thirty over rough
        /// land; one number until a surface-roughness field exists to split them.
        /// </summary>
        public const float SurfaceFrictionTurnDeg = 25f;

        /// <summary>
        /// Fraction. How much of the balanced speed survives at the surface. The
        /// drag that turns the wind also slows it, so this and the turn above are two
        /// halves of one effect and should be retuned together.
        /// </summary>
        public const float SurfaceFrictionSpeedFactor = 0.65f;

        /// <summary>
        /// m/s. Ceiling on the balanced speed, before friction takes its share - so
        /// the surface figure an author reads is this times the friction factor. Not a
        /// safety net: amplitude and radius are drawn from independent streams, so the
        /// field can legitimately draw a thirty hectopascal system at a two hundred
        /// kilometre radius - a gradient no synoptic chart shows - and this is where
        /// that pairing stops producing a wind no anemometer has ever read.
        /// </summary>
        public const float BalancedWindMaxMPerS = 50f;

        // =====================================================================
        //  FRONTS - THE PASSAGE SEQUENCE
        //
        //  A front is read from the field's own time series at a point rather than
        //  drawn as a separate object: as a system passes, the barometer falls,
        //  bottoms out and rises, and the wind turns one way or the other. That is
        //  exactly what a surface observer classifies a front by, so classifying it
        //  the same way keeps one source of truth - the pressure field - instead of
        //  authoring a second layer that could disagree with it.
        // =====================================================================

        /// <summary>
        /// days. Half-window for the time derivatives a front is read from - about an
        /// hour and a quarter. Small against the four-day synoptic epoch, so it sees
        /// the passage and not the season; wide enough that the difference of two
        /// pressures a few hundredths of a hectopascal apart keeps its digits.
        /// </summary>
        public const float FrontProbeDays = 0.05f;

        /// <summary>
        /// hPa. How far below the mean the anomaly must sit for the point to be
        /// inside a front at all. A front is the edge of a low, not a fair-weather
        /// ridge, so a positive or shallow anomaly reports no front however fast it
        /// is changing.
        /// </summary>
        public const float FrontDepthThresholdHpa = 2f;

        /// <summary>
        /// hPa. Depth past which the observer is close enough to the low's centre
        /// that the warm and cold sectors have wrapped together - an occlusion. Set
        /// well inside the deepest amplitude the field can draw, so only a near-direct
        /// hit reports occluded.
        /// </summary>
        public const float FrontOccludedDepthHpa = 15f;

        /// <summary>
        /// hPa per day. Barometer rate above which a front is actively approaching
        /// (falling) or departing (rising). Below it in magnitude the barometer is
        /// steady, which over a lasting trough is the mark of a stationary front.
        /// </summary>
        public const float FrontActiveTendencyHpaPerDay = 1f;

        /// <summary>
        /// m/s. Zonal steering speed below which the systems barely advect, so a
        /// trough sits over one place for days - a stationary front rather than a
        /// passing one. The steering vanishes at the circulation boundaries, so this
        /// is what makes the quasi-stationary frontal zones fall near them.
        /// </summary>
        public const float FrontStationarySpeedMPerS = 3f;

        // =====================================================================
        //  W-23  STABILITY - THE ONE TEST BEHIND CUMULUS vs STRATUS
        //
        //  Compare the observed near-surface lapse against the adiabatic pair
        //  (L-04). The standing atmosphere sits at the environmental rate, between
        //  the two, so it is conditionally unstable by default; surface heating is
        //  what tips it either way over a day. A hot afternoon steepens the lapse
        //  past the dry adiabat and the sky goes convective; a clear night cools
        //  the ground below the air and lays an inversion, which is fog weather.
        //  That diurnal swing is why the tropics storm in the afternoon and a
        //  winter morning is flat grey - one test, driven by the sun.
        // =====================================================================

        /// <summary>
        /// hours. Lag of peak surface temperature behind solar noon. The ground
        /// keeps warming while it gains more than it loses, so the hottest hour and
        /// the steepest lapse fall in the early afternoon - and, read the other way,
        /// the morning is still stably stratified from the night hours after sunrise.
        /// </summary>
        public const float StabilityHeatingLagHours = 2.5f;

        /// <summary>
        /// degC/km added to the environmental lapse at peak insolation - enough that
        /// a sun near overhead carries the near-surface lapse past the dry adiabat
        /// into absolute instability, which is what builds the afternoon cumulonimbus.
        /// </summary>
        public const float StabilityMaxHeatingBoostCPerKm = 5f;

        /// <summary>
        /// degC/km subtracted from the environmental lapse while the sun is down, so
        /// overnight radiative cooling of the ground lays a surface inversion - the
        /// negative lapse that reads as very stable and traps fog and haze.
        /// </summary>
        public const float StabilityNocturnalInversionCPerKm = 8f;

        // =====================================================================
        //  W-24  LOCAL CIRCULATIONS - THE WINDS THE GROUND MAKES
        //
        //  Three closed-form vectors added to the synoptic surface wind, each
        //  keyed off geography the world already owns. All three reverse (or, for
        //  the gap, merely strengthen) on the same diurnal solar forcing the
        //  stability lapse rides, so the afternoon sea breeze and the afternoon
        //  storm keep the same clock.
        // =====================================================================

        /// <summary>
        /// m. Reach of the sea breeze inland. Beyond it the coast no longer drives
        /// the wind; within it the effect falls off linearly to full strength at the
        /// shoreline.
        /// </summary>
        public const float SeaBreezeRangeM = 20000f;

        /// <summary>
        /// m/s per degC. Onshore breeze speed at the shore, at peak heating, per
        /// degree of land-sea temperature contrast - so a ten-degree afternoon
        /// contrast drives a fresh four-metre breeze and a mild one barely stirs.
        /// </summary>
        public const float SeaBreezeSpeedMPerSPerC = 0.4f;

        /// <summary>
        /// m/s. Slope wind at full daytime forcing on the steepest ground: upslope by
        /// day, and the same magnitude reversed to downslope drainage by night, scaled
        /// down by the weaker nocturnal forcing.
        /// </summary>
        public const float SlopeWindReferenceMPerS = 5f;

        /// <summary>
        /// Dimensionless. Fraction by which a fully constricted valley speeds the
        /// along-valley flow through it - a venturi, so a wind squeezed through a
        /// narrowing gap comes out faster on the far side.
        /// </summary>
        public const float GapWindGain = 1f;

        /// <summary>
        /// Dimensionless. Magnitude of the nocturnal solar forcing relative to the
        /// daytime peak, so the land breeze and the katabatic drainage are the real,
        /// gentler mirror of the afternoon sea breeze and upslope wind.
        /// </summary>
        public const float NocturnalForcingFraction = 0.4f;

        // =====================================================================
        //  W-25  HUMIDITY - A PROPERTY OF THE AIR MASS, NOT THE GROUND
        //
        //  Moisture is carried in by the air, so it is characterised by the dew
        //  point of the air mass - high for maritime air, low for continental -
        //  which is near enough conserved through a day while the temperature
        //  swings. Relative humidity is then e_s(dew)/e_s(T), so it climbs to a
        //  dawn maximum and falls to an afternoon minimum on its own, with no
        //  moisture added and no authored daily curve.
        // =====================================================================

        /// <summary>
        /// degC. Dew point of maritime air - a moist tropical-maritime source, so a
        /// coast breathes near-saturated air at dawn and cloud forms readily.
        /// </summary>
        public const float MaritimeAirDewPointC = 16f;

        /// <summary>
        /// degC. Dew point of deep-continental air - a dry source, so the interior of
        /// a landmass runs a wide humidity range and a low dawn maximum.
        /// </summary>
        public const float ContinentalAirDewPointC = 0f;

        /// <summary>
        /// degC per hPa. How much an approaching low raises the air-mass dew point per
        /// hPa it sits below the mean, as its circulation advects maritime air ahead
        /// of the front - which is what lifts humidity before the front arrives.
        /// </summary>
        public const float FrontMoistureBoostCPerHpa = 0.4f;

        /// <summary>
        /// degC. Ceiling on that frontal moisture boost, so even a direct hit by the
        /// deepest low cannot drive the dew point to an unphysical value.
        /// </summary>
        public const float FrontMoistureMaxBoostC = 8f;

        // =====================================================================
        //  W-11, W-12  TEMPERATURE NORMALS
        // =====================================================================

        /// <summary>degC per degree of latitude. Observed meridional temperature gradient in the mid-latitudes.</summary>
        public const float LatitudeGradientCPerDeg = 0.7f;

        /// <summary>m. e-folding distance of marine moderation inland. Why coasts have mild winters and interiors do not.</summary>
        public const float MarineModerationScaleM = 150000f;

        /// <summary>days. Lag of peak temperature behind the solstice: thermal inertia, and the reason July is hotter than June.</summary>
        public const float SeasonalPeakLagDays = 30f;

        /// <summary>Dimensionless. Conrad continentality multiplier.</summary>
        public const float ConradMultiplier = 1.7f;

        /// <summary>deg. Conrad latitude offset, inside sin(phi + 10 deg).</summary>
        public const float ConradLatitudeOffsetDeg = 10f;

        /// <summary>Dimensionless. Conrad additive term.</summary>
        public const float ConradOffset = 14f;

        /// <summary>
        /// Dimensionless. Conrad index of a deep continental interior, which is what
        /// the marine-moderation kernel saturates to. Verkhoyansk reaches about 100;
        /// 65 is a large landmass rather than the world record, because a generated
        /// world reading as Siberia everywhere is worse than one reading as Eurasia.
        /// </summary>
        public const float ConradMaxIndex = 65f;

        /// <summary>
        /// degC. Observed global mean sea-level air temperature: the T0 term of the
        /// zonal profile below.
        /// </summary>
        public const float GlobalMeanSeaLevelTempC = 14.9f;

        /// <summary>
        /// Dimensionless. dP2/dphi of the second Legendre polynomial at 45 deg, which
        /// is 3*sin(45)*cos(45) = 1.5.
        ///
        /// WHY THE PROFILE IS A COSINE AND NOT A STRAIGHT LINE. The observed
        /// meridional gradient above is a MID-LATITUDE figure. Run it linearly from
        /// the equator and 45 deg comes out near -4 degC against an observed +11, and
        /// 70 deg near -22 against an observed -8: the tropics are far flatter than
        /// the mid-latitudes and the linear form cannot express that. The zonal mean
        /// T0 + T2*P2(sin phi) can, and its slope at 45 deg is exactly T2 * this
        /// factor - so the gradient above stays the single owner of the number and
        /// the amplitude is derived from it rather than authored beside it.
        /// </summary>
        public const float LegendreP2MidLatitudeSlope = 1.5f;

        /// <summary>
        /// degC. Clear, dry, continental day-night temperature range. Cloud and
        /// humidity only ever reduce it, so this is the ceiling - and it is why a
        /// desert swings about 20 degC and a maritime winter barely 5.
        /// </summary>
        public const float DiurnalBaseRangeC = 20f;

        /// <summary>Dimensionless. Share of the diurnal range that complete overcast removes.</summary>
        public const float DiurnalCloudFactor = 0.5f;

        /// <summary>Dimensionless. Share of the diurnal range that saturated air removes.</summary>
        public const float DiurnalHumidityFactor = 0.4f;

        /// <summary>Months in a year. Named because it is an array length, a divisor and a loop bound in the same expressions.</summary>
        public const int MonthsPerYear = 12;

        // =====================================================================
        //  W-10  SURFACE RADIATION (Angstrom-Prescott)
        //  Rs = Ra * (a + b * sunshineFraction)
        // =====================================================================

        /// <summary>Dimensionless. Angstrom a: the fraction of Ra reaching the ground under complete overcast.</summary>
        public const float AngstromA = 0.25f;

        /// <summary>Dimensionless. Angstrom b: the additional fraction under clear sky, so a + b is the clear-sky total.</summary>
        public const float AngstromB = 0.50f;

        // =====================================================================
        //  W-13  PRECIPITATION REGIME SHAPES
        //
        //  Each regime is one harmonic on a flat base of 1: the annual harmonic
        //  moves the wet season to one end of the year, the semiannual one splits
        //  it in two. The amplitudes below are what separates a monsoon from an
        //  oceanic winter maximum, and an amplitude above 1 is deliberate - it
        //  drives the dry half of the year to zero, which is the entire point of
        //  a dry season.
        //
        //  The weights are normalised to sum to 1 before use, so these numbers
        //  set the SHAPE of the year and never its total. The total comes from the
        //  shipped orographic precipitation field, which is not recomputed here.
        // =====================================================================

        /// <summary>Semiannual amplitude of the equatorial double maximum, one peak per ITCZ passage.</summary>
        public const float EquatorialSemiannualAmplitude = 0.35f;

        /// <summary>Annual amplitude of a monsoon. Above 1 so the winter half clamps to dry.</summary>
        public const float MonsoonAnnualAmplitude = 1.25f;

        /// <summary>Annual amplitude of a Mediterranean winter maximum: summer drought without a wholly rainless year.</summary>
        public const float MediterraneanAnnualAmplitude = 0.95f;

        /// <summary>Annual amplitude of a temperate oceanic year: rain in every month, slightly more in winter.</summary>
        public const float OceanicAnnualAmplitude = 0.20f;

        /// <summary>Annual amplitude of a continental year: a summer convective maximum.</summary>
        public const float ContinentalAnnualAmplitude = 0.45f;

        /// <summary>Annual amplitude of a polar or desert year: low, and almost flat.</summary>
        public const float FlatRegimeAmplitude = 0.10f;

        /// <summary>deg. Poleward limit of the equatorial double-peak band.</summary>
        public const float EquatorialRegimeLatitudeDeg = 10f;

        /// <summary>deg. Poleward limit of the monsoon band.</summary>
        public const float MonsoonRegimeLatitudeDeg = 25f;

        /// <summary>
        /// deg. Equatorward edge of the Mediterranean band. Forwarded from the
        /// subtropical high rather than authored again: summer drought at this
        /// latitude IS that high sitting overhead, so if the circulation belt is
        /// ever moved for a world the regime band has to move with it.
        /// </summary>
        public const float MediterraneanRegimeMinLatitudeDeg = SubtropicalHighLatitudeDeg;

        /// <summary>deg. Poleward edge of the Mediterranean band.</summary>
        public const float MediterraneanRegimeMaxLatitudeDeg = 45f;

        /// <summary>deg. Equatorward edge of the polar band, forwarded from the storm-track latitude for the same reason.</summary>
        public const float PolarRegimeLatitudeDeg = SubpolarLowLatitudeDeg;

        /// <summary>Conrad index above which a site is continental enough to take a summer convective maximum instead of an oceanic one.</summary>
        public const float ContinentalRegimeMinConradIndex = 35f;

        /// <summary>P/PET below which a site is a desert and its regime goes flat whatever its latitude says.</summary>
        public const float DesertAridityThreshold = 0.2f;

        // =====================================================================
        //  EVAPORATIVE DEMAND
        //
        //  These arrived in EcoConstants because evapotranspiration was first
        //  needed by the vegetation layer, but they measure the atmosphere and
        //  not the plants: how much water a given energy flux can lift, and how
        //  the Hargreaves fit converts temperature into radiation. They own the
        //  values here now and EcoConstants forwards, so the two layers cannot
        //  drift apart, and so the climate normals - which live in this folder
        //  and may not read the ecology folder - can reach them at all.
        // =====================================================================

        /// <summary>MJ/kg. Latent heat of vaporisation of water near 20 degC. The exchange rate between an energy flux and a depth of water.</summary>
        public const float LatentHeatVaporisationMjPerKg = 2.45f;

        /// <summary>Dimensionless. Hargreaves-Samani fitted coefficient (FAO-56 eq. 52).</summary>
        public const float HargreavesCoefficient = 0.0023f;

        /// <summary>degC. Hargreaves-Samani temperature offset (FAO-56 eq. 52).</summary>
        public const float HargreavesTempOffsetC = 17.8f;

        /// <summary>mm/yr. Floor on annual PET before dividing by it, so a frozen world reports a large aridity index rather than an infinite one.</summary>
        public const float AridityMinPetMmPerYear = 1f;

        /// <summary>Dimensionless. Ceiling on P/PET. Beyond about 20 the distinction between wet and wetter has no ecological consequence.</summary>
        public const float AridityMaxIndex = 20f;

        // =====================================================================
        //  W-17  THE PRESSURE LATTICE
        // =====================================================================

        /// <summary>km. Spacing of the synoptic pressure lattice. The observed scale of a mid-latitude high or low.</summary>
        public const float WeatherCellSpacingKm = 600f;

        /// <summary>m per km. Kept beside the spacing so the lattice can convert without reaching for a bare 1000.</summary>
        public const float MetresPerKilometreLattice = MetresPerKilometre;

        /// <summary>days. How long one generation of pressure centres lives, birth to death. Real mid-latitude systems run three to five days.</summary>
        public const float SynopticEpochDays = 4f;

        /// <summary>hPa. Shallowest pressure centre worth placing - below this a system is not weather, it is noise.</summary>
        public const float SynopticAmplitudeMinHpa = 4f;

        /// <summary>hPa. Deepest centre. A 30 hPa anomaly on a 1013 base is a severe storm or a blocking high, which is the intended ceiling.</summary>
        public const float SynopticAmplitudeMaxHpa = 30f;

        /// <summary>
        /// Exponent that biases the amplitude draw towards shallow systems, since
        /// deep ones are rare: amplitude = min + (max - min) * u^exponent. At 3 the
        /// chance of clearing 20 hPa is about one centre in seven.
        /// </summary>
        public const float SynopticAmplitudeRarityExponent = 3f;

        /// <summary>km. Smallest centre radius - a tight, fast-moving low.</summary>
        public const float SynopticRadiusMinKm = 200f;

        /// <summary>km. Largest centre radius. The neighbourhood search below is sized from this, so raising it without raising the search truncates the field.</summary>
        public const float SynopticRadiusMaxKm = 900f;

        /// <summary>Fraction of centres that are lows rather than highs. Half, because the mean has to come out at the mean.</summary>
        public const float SynopticLowFraction01 = 0.5f;

        /// <summary>
        /// Half-width in cells of the lattice search. A centre in a cell this many
        /// steps away can still be inside its own radius of the sample point; one
        /// step further away cannot, for any jitter. The invariant the self-test
        /// pins is SynopticSearchCells * WeatherCellSpacingKm >= SynopticRadiusMaxKm.
        /// </summary>
        public const int SynopticSearchCells = 2;

        /// <summary>
        /// m/s. Mean speed of the mid-latitude westerlies, which is the speed weather
        /// arrives at across most of an inhabited world: about a thousand kilometres a day.
        /// </summary>
        public const float WesterlySteeringMPerS = 12f;

        /// <summary>m/s. Mean speed of the trade winds. Easterly, so tropical weather arrives from the other side.</summary>
        public const float TradeWindSteeringMPerS = 8f;

        /// <summary>m/s. Mean speed of the polar easterlies. Weakest of the three cells.</summary>
        public const float PolarEasterlySteeringMPerS = 5f;

        /// <summary>
        /// Fraction of the zonal steering speed that carries a system polewards.
        /// Storm tracks tilt towards the pole as they cross an ocean rather than
        /// running along a parallel, and a quarter is the observed slope.
        /// </summary>
        public const float SynopticTrackTiltFraction = 0.25f;

        // =====================================================================
        //  W-14  KOEPPEN-GEIGER THRESHOLDS
        //
        //  The published decision tree, as numbers. They live here rather than in
        //  the classifier for the same reason every other measured value does: a
        //  threshold inlined in a switch cannot be found by the grep that proves
        //  this folder holds no bare literals, and cannot be moved for a world
        //  whose year or freezing point is not Earth's.
        //
        //  Several of these coincide in value with thresholds elsewhere in this
        //  file and are still declared separately, because they are separate
        //  measurements that happen to agree: 18 degC as the tropical winter floor
        //  is Koeppen's, and it is not the same claim as 18 degC being warm. Where
        //  one value genuinely IS the other, it is forwarded rather than retyped.
        // =====================================================================

        /// <summary>degC. Warmest month below this is polar, class E.</summary>
        public const float KoeppenPolarWarmestMonthC = 10f;

        /// <summary>degC. Warmest month below this is perpetual frost, EF rather than ET. The freezing point, forwarded.</summary>
        public const float KoeppenFrostWarmestMonthC = WaterFreezingPointC;

        /// <summary>degC. Coldest month at or above this is tropical, class A.</summary>
        public const float KoeppenTropicalColdestMonthC = 18f;

        /// <summary>degC. Coldest month at or above this is temperate (C); below it is continental (D). The 0 degC convention rather than the older -3.</summary>
        public const float KoeppenTemperateColdestMonthC = WaterFreezingPointC;

        /// <summary>mm per degC. Slope of the aridity threshold: warmer air demands more rain before a place stops being a desert.</summary>
        public const float KoeppenAridityTempFactor = 20f;

        /// <summary>mm. Aridity offset when rain falls mostly in the warm half of the year, where it evaporates hardest.</summary>
        public const float KoeppenAridityOffsetSummerMm = 280f;

        /// <summary>mm. Aridity offset for a year with no strong seasonal preference.</summary>
        public const float KoeppenAridityOffsetEvenMm = 140f;

        /// <summary>mm. Aridity offset when rain falls mostly in the cool half, where little of it is lost.</summary>
        public const float KoeppenAridityOffsetWinterMm = 0f;

        /// <summary>0-1 share of annual rain in the warm half at or above which the summer offset applies.</summary>
        public const float KoeppenSummerShareHigh = 0.7f;

        /// <summary>0-1 share below which the winter offset applies.</summary>
        public const float KoeppenSummerShareLow = 0.3f;

        /// <summary>Fraction of the aridity threshold below which a dry climate is desert (BW) rather than steppe (BS).</summary>
        public const float KoeppenDesertFraction = 0.5f;

        /// <summary>degC. Annual mean at or above this makes a dry climate hot (h) rather than cold (k).</summary>
        public const float KoeppenHotAnnualMeanC = 18f;

        /// <summary>mm. Driest month at or above this in a tropical climate is rainforest, Af.</summary>
        public const float KoeppenTropicalDriestMonthMm = 60f;

        /// <summary>mm. Intercept of the monsoon line separating Am from Aw.</summary>
        public const float KoeppenMonsoonInterceptMm = 100f;

        /// <summary>Divisor of the annual total in the monsoon line: Am holds where the driest month clears 100 - P/25.</summary>
        public const float KoeppenMonsoonDivisor = 25f;

        /// <summary>mm. Driest warm-half month below this is a candidate dry summer, the s of Csa.</summary>
        public const float KoeppenSummerDryMonthMm = 40f;

        /// <summary>Wettest cool-half month must exceed the driest warm-half month by this factor for a dry summer.</summary>
        public const float KoeppenDrySummerRatio = 3f;

        /// <summary>Wettest warm-half month must exceed the driest cool-half month by this factor for a dry winter.</summary>
        public const float KoeppenDryWinterRatio = 10f;

        /// <summary>degC. Warmest month at or above this is a hot summer, the a of Cfa.</summary>
        public const float KoeppenHotSummerMonthC = 22f;

        /// <summary>Months at or above the polar threshold needed for a warm summer (b) rather than a cool one (c).</summary>
        public const int KoeppenWarmSummerMonthCount = 4;

        /// <summary>degC. Coldest month below this is the severely continental d subtype.</summary>
        public const float KoeppenSevereWinterC = -38f;

        /// <summary>Length in months of the warm half of the year, used for the seasonal rain tests.</summary>
        public const int KoeppenHalfYearMonths = MonthsPerYear / 2;

        /// <summary>
        /// Floor on the remaining maritime share when inverting the continentality
        /// dial. 1e-4 puts the ceiling at about nine scale lengths - 1380 km, deeper
        /// inland than any point on Earth - and keeps the logarithm finite.
        /// </summary>
        public const float ContinentalitySaturationFloor = 1e-4f;
    }
}
