// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    ClockMath.cs
//  Purpose: All of the module's astronomy, in one place, as pure functions.
//           (W-02; laws L-01, L-02, L-03, L-27, L-30)
//
//  WHY EVERYTHING IS STATIC AND TAKES ITS INPUTS AS ARGUMENTS
//  ---------------------------------------------------------
//  There is no instance, no cached field and no frame time in this file. Given
//  the same day, second, latitude and longitude it returns the same numbers on
//  every machine, in every build, in any calling order. That is what makes a
//  saved world reopen looking like itself (C-06), and it is why the self-test can
//  assert on the equinoxes without constructing a scene.
//
//  THIS IS THE ONLY PLACE A SOLAR FORMULA MAY LIVE
//  -----------------------------------------------
//  UCLightingSystemManager used to carry its own copy of declination and day
//  length. Two copies of one formula is not redundancy, it is a future bug: one
//  gets a polar-circle guard and the other does not, and then the sky disagrees
//  with the vegetation about whether the sun rose. Those methods now forward
//  here. If a solar formula is wrong, it is wrong once, in this file.
//
//  TWO PLACES WHERE THIS FILE DELIBERATELY DEPARTS FROM THE TASK DOCUMENT
//  ---------------------------------------------------------------------
//  Both are recorded at the method that does it, and both are sign or definition
//  problems rather than differences of opinion:
//
//   1. The hour-angle time correction. The document writes the longitude term as
//      4 min/deg * (L_standard - L_local), which is the west-positive longitude
//      convention. ClockState documents longitude as positive EAST. Combining the
//      two as written puts local noon off by twice the offset, in the wrong
//      direction. This file uses (L_local - L_standard) with east-positive
//      longitude. Identical physics, stated once, consistently.
//
//   2. Air mass. The document gives 1/(sin a + 0.15), which returns 0.87 at the
//      zenith - and air mass is 1.0 at the zenith by definition, because it is a
//      multiple of the vertical path. ClockState's own documentation says "1 at
//      the zenith, about 38 at the horizon", which only true Kasten-Young
//      satisfies. This file uses Kasten-Young. See AtmoConstants for the working.
//
//  NO BARE PHYSICAL LITERALS
//  -------------------------
//  Every measured quantity comes from AtmoConstants. The only bare numbers here
//  are exact counts and halvings - a 2 that means "the sun crosses that hour
//  angle twice, once rising and once setting", a 0.5 that means "half" - and each
//  one is named in a comment where it appears.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Pure static solar and lunar geometry. No Unity types beyond <see cref="Mathf"/>,
    /// no state, no time source. Produces the numbers <see cref="ClockState"/> carries.
    /// </summary>
    public static class ClockMath
    {
        /// <summary>One full turn of a cycle, in radians.</summary>
        private const float Tau = Mathf.PI * 2f;

        /// <summary>
        /// Half. Used where a formula averages two bounds, never as a tuning value.
        /// </summary>
        private const float Half = 0.5f;

        /// <summary>
        /// The sun passes any given hour angle twice a day, once climbing and once
        /// descending, so a half-day arc doubles into a day length. A count, not a
        /// measurement.
        /// </summary>
        private const float RisingAndSettingCrossings = 2f;

        // =====================================================================
        //  SECTION 1  -  WHERE WE ARE IN THE YEAR
        // =====================================================================

        /// <summary>
        /// Fraction 0 to 1 of the year on the declination cosine's own phase, where
        /// 0 is northern midwinter and 0.5 northern midsummer.
        ///
        /// This is NOT <see cref="ClockState.season01"/>, which is zeroed at the
        /// vernal equinox instead. They differ by a quarter turn and W-06 owns the
        /// conversion between them. Two names because conflating them silently
        /// inverts every season, which is the kind of bug that looks like an art
        /// problem for a week.
        /// </summary>
        public static float WinterPhase01(int dayOfYear)
        {
            return (dayOfYear + AtmoConstants.DeclinationDayOffset)
                   / AtmoConstants.DaysPerCalendarYear;
        }

        // =====================================================================
        //  SECTION 2  -  SOLAR DECLINATION  (L-01)
        //
        //  The latitude the sun stands directly over today. The whole mechanism of
        //  the seasons: a world whose axial tilt is zero holds this at zero and has
        //  no seasons at all, at any latitude.
        // =====================================================================

        /// <summary>
        /// Season as a 0..1 phase on the astronomical convention: 0 at the vernal
        /// equinox, 0.25 at the summer solstice, 0.5 autumn, 0.75 winter. This is
        /// <c>ClockState.season01</c>. (W-06)
        ///
        /// Two things happen here, and both are the reason there is no
        /// <c>enum Season</c> anywhere in the toolkit.
        ///
        /// First, the quarter turn. The winter-zero phase the climate maths uses is
        /// not this; they differ by exactly a quarter of a year, and converting in
        /// one place is what stops a consumer picking the wrong one and running its
        /// summer three months early.
        ///
        /// Second, the hemisphere. Southern latitudes are half a year out of step,
        /// so their warm season falls in January. An enum cannot express that: a
        /// value of <c>Summer</c> would have to mean two opposite months depending
        /// on the sign of a latitude it does not carry. A phase can, and does, by
        /// adding half a turn below the equator.
        ///
        /// Exactly on the equator this returns the northern reading. There is no
        /// correct answer there - the equator has no thermal seasons to invert, only
        /// a wet one and a dry one - so it takes the branch that keeps the function
        /// continuous rather than pretending to a precision it has not got.
        /// </summary>
        public static float Season01(float winterPhase01, float latitudeDeg)
        {
            float season01 = winterPhase01 - VernalEquinoxOffsetOfWinterPhase;

            if (latitudeDeg < 0f) season01 += Half;

            return Frac01(season01);
        }

        /// <summary>
        /// The winter-zero phase runs a quarter of a year ahead of the equinox-zero
        /// phase, because midwinter is a quarter turn before the vernal equinox.
        /// </summary>
        private const float VernalEquinoxOffsetOfWinterPhase = 0.25f;

        /// <summary>
        /// deg. The sun's ecliptic longitude at midwinter, where the winter-zero
        /// phase is 0. A quarter turn short of the equinox, which is the same
        /// quarter turn <see cref="VernalEquinoxOffsetOfWinterPhase"/> expresses as
        /// a fraction - stated in both units because one is a phase and one is an
        /// angle, and converting at the point of use is how a sign gets lost. (W-07)
        /// </summary>
        private const float SolarLongitudeAtMidwinterDeg = 270f;

        /// <summary>
        /// W-05. The latitude at a position in a world that spans latitude.
        /// <paramref name="northFraction01"/> is 0 at the world's south edge and 1 at
        /// its north edge, so the world's authored latitude lands at its centre and a
        /// span of zero returns that latitude everywhere.
        ///
        /// Clamped to the poles rather than wrapped. A world whose span runs off the
        /// top of the globe is an authoring mistake, and clamping makes the north edge
        /// arctic - which is what the author was reaching for - while wrapping would
        /// put a second equator at the top of the map and hide the mistake behind
        /// plausible-looking forest.
        /// </summary>
        public static float LatitudeAt(float centreLatitudeDeg, float spanDeg,
                                       float northFraction01)
        {
            float offset = (northFraction01 - 0.5f) * spanDeg;
            return Mathf.Clamp(centreLatitudeDeg + offset, -PoleLatitudeDeg, PoleLatitudeDeg);
        }

        /// <summary>deg. The latitude of a pole, and so the limit of any latitude.</summary>
        public const float PoleLatitudeDeg = 90f;

        /// <summary>
        /// MJ/m2/day arriving at the top of the atmosphere, FAO-56 equation 21.
        ///
        /// It lives here rather than on a manager because it is a pure function of
        /// latitude and the point in the year, and because W-05 re-derives it on a
        /// worker thread for a chunk's own latitude - which cannot reach a
        /// MonoBehaviour at all. UCLightingSystemManager forwards to it for the same
        /// single-owner reason the declination and day length above already do.
        ///
        /// The eccentricity term is not a tuning factor: the planet is nearest the
        /// sun close to northern midwinter, so with this phase convention - 0 is
        /// northern midwinter - the correction is largest at phase 0, which is why
        /// the cosine takes the phase unshifted.
        ///
        /// Latitude is clamped off the poles first, for the reason DayLengthHours
        /// states: at exactly 90 deg tan is infinite, and on the two days a year when
        /// declination is exactly zero the product is inf * 0, which is NaN. NaN
        /// passes straight through the -1..+1 clamp below and then poisons the
        /// evapotranspiration law, so it is stopped here.
        /// </summary>
        public static float ExtraterrestrialRadiationMjPerM2Day(float latDeg,
                                                                float decDeg,
                                                                float winterPhase01)
        {
            float safeLat = Mathf.Clamp(
                latDeg,
                -AtmoConstants.LatitudeTanGuardDeg,
                AtmoConstants.LatitudeTanGuardDeg);

            float phi = safeLat * Mathf.Deg2Rad;
            float delta = decDeg * Mathf.Deg2Rad;

            float omega = Mathf.Acos(Mathf.Clamp(-Mathf.Tan(phi) * Mathf.Tan(delta), -1f, 1f));

            float dr = 1f + AtmoConstants.OrbitEccentricityFactor
                            * Mathf.Cos(winterPhase01 * Tau);

            float ra = (AtmoConstants.MinutesPerDay / Mathf.PI)
                       * AtmoConstants.SolarConstantMjPerM2PerMin
                       * dr
                       * (omega * Mathf.Sin(phi) * Mathf.Sin(delta)
                          + Mathf.Cos(phi) * Mathf.Cos(delta) * Mathf.Sin(omega));

            return ra > 0f ? ra : 0f;
        }

        /// <summary>Wraps a phase into 0..1, for negatives as well as overshoots.</summary>
        private static float Frac01(float phase01)
        {
            return phase01 - Mathf.Floor(phase01);
        }

        /// <summary>
        /// deg. Solar declination for a day of an Earth-like year, from
        /// d = -23.44 * cos(360 * (N + 10) / 365).
        ///
        /// The offset of 10 days is what places midwinter on 21 December rather than
        /// on 1 January: the calendar year does not start at a solstice.
        /// </summary>
        public static float DeclinationDeg(int dayOfYear)
        {
            return DeclinationDegFromWinterPhase01(
                WinterPhase01(dayOfYear),
                AtmoConstants.EarthAxialTiltDegrees);
        }

        /// <summary>
        /// deg. Declination for an arbitrary axial tilt, from a phase already on the
        /// midwinter-zero convention.
        ///
        /// This overload exists because a world may author its own tilt, and because
        /// UCLightingSystemManager already thinks in a season phase rather than in a
        /// day number. Both callers reach the same cosine rather than each keeping
        /// one.
        /// </summary>
        public static float DeclinationDegFromWinterPhase01(float winterPhase01, float axialTiltDeg)
        {
            return -axialTiltDeg * Mathf.Cos(winterPhase01 * Tau);
        }

        // =====================================================================
        //  SECTION 3  -  EQUATION OF TIME
        //
        //  SIGN CONVENTION, STATED ONCE AND USED EVERYWHERE:
        //  POSITIVE MEANS THE TRUE SUN IS AHEAD OF CLOCK TIME - a sundial reads
        //  later than the clock. Get this backwards and sunrise sits about fifteen
        //  minutes off in spring and autumn, correct at the solstices, and nothing
        //  in the world will point at the cause.
        //
        //  A check to hold on to: at day 81 this returns -7.53 min. Around
        //  22 March the sundial genuinely does run about seven and a half minutes
        //  behind the clock, so the negative sign is right.
        // =====================================================================

        /// <summary>
        /// min. Equation of time: how far the true sun leads (positive) or trails
        /// (negative) clock time today. From
        /// EoT = 9.87 sin 2B - 7.53 cos B - 1.5 sin B, B = 360 (N - 81) / 364.
        /// </summary>
        public static float EquationOfTimeMinutes(int dayOfYear)
        {
            float bDeg = AtmoConstants.DegreesPerCircle
                         * (dayOfYear - AtmoConstants.EquationOfTimeDayOffset)
                         / AtmoConstants.EquationOfTimeDayDivisor;

            float b = bDeg * Mathf.Deg2Rad;

            return AtmoConstants.EquationOfTimeSin2BMinutes * Mathf.Sin(b * RisingAndSettingCrossings)
                   - AtmoConstants.EquationOfTimeCosBMinutes * Mathf.Cos(b)
                   - AtmoConstants.EquationOfTimeSinBMinutes * Mathf.Sin(b);
        }

        // =====================================================================
        //  SECTION 4  -  CLOCK TIME TO SOLAR TIME  (L-02)
        // =====================================================================

        /// <summary>
        /// min. How far local true solar time runs ahead of the standard clock here.
        /// Two independent effects added: the site's displacement from its time-zone
        /// meridian, and the equation of time.
        ///
        /// LONGITUDE IS POSITIVE EAST. East of your standard meridian the sun reaches
        /// you first, so solar time is AHEAD and this is positive - hence
        /// (local - standard), not the reverse. See the file header: the task
        /// document writes this term in the opposite, west-positive convention.
        /// </summary>
        public static float TimeCorrectionMinutes(float longitudeDeg, float eotMin, float standardMeridianDeg)
        {
            return AtmoConstants.MinutesPerDegreeLongitude * (longitudeDeg - standardMeridianDeg)
                   + eotMin;
        }

        /// <summary>h. Seconds since local midnight as a decimal hour.</summary>
        public static float LocalHourFromSeconds(float secondsOfDay)
        {
            return secondsOfDay / ClockState.SecondsPerHour;
        }

        /// <summary>
        /// deg. Solar hour angle: 0 at solar noon, negative before it, 15 deg per
        /// hour. Wrapped into -180..+180 so that "before noon" stays negative even
        /// when the time correction pushes solar time past a day boundary - an
        /// unwrapped value would make the azimuth reflection below pick the wrong
        /// half of the sky for an hour either side of midnight.
        /// </summary>
        public static float SolarHourAngleDeg(
            float secondsOfDay,
            float longitudeDeg,
            float eotMin,
            float standardMeridianDeg)
        {
            float solarHour = LocalHourFromSeconds(secondsOfDay)
                              + TimeCorrectionMinutes(longitudeDeg, eotMin, standardMeridianDeg)
                                / AtmoConstants.MinutesPerHour;

            float hourAngle = AtmoConstants.DegreesPerHourOfHourAngle
                              * (solarHour - AtmoConstants.NoonHour);

            return WrapSignedDegrees(hourAngle);
        }

        /// <summary>
        /// h. The local clock hour at which the sun is highest. Equal to 12:00 only
        /// by coincidence of longitude and date; this is the inverse of the hour
        /// angle above, sharing its one time correction rather than restating it.
        /// </summary>
        public static float SolarNoonHour(float longitudeDeg, float eotMin, float standardMeridianDeg)
        {
            return AtmoConstants.NoonHour
                   - TimeCorrectionMinutes(longitudeDeg, eotMin, standardMeridianDeg)
                     / AtmoConstants.MinutesPerHour;
        }

        // =====================================================================
        //  SECTION 5  -  SUN POSITION  (L-02)
        // =====================================================================

        /// <summary>
        /// deg. Sun altitude above the geometric horizon, from
        /// sin a = sin phi sin d + cos phi cos d cos h. Negative at night.
        ///
        /// The asin argument is clamped: it is a sum of products of sines and
        /// cosines that should never leave -1..+1, and in float arithmetic
        /// occasionally does by a few parts in ten million. An unclamped asin
        /// answers NaN, and a NaN elevation propagates into temperature, then
        /// moisture, then every species suitability, with no error anywhere.
        /// </summary>
        public static float ElevationDeg(float latDeg, float decDeg, float hourAngleDeg)
        {
            float phi = latDeg * Mathf.Deg2Rad;
            float delta = decDeg * Mathf.Deg2Rad;
            float h = hourAngleDeg * Mathf.Deg2Rad;

            float sinAlpha = Mathf.Sin(phi) * Mathf.Sin(delta)
                             + Mathf.Cos(phi) * Mathf.Cos(delta) * Mathf.Cos(h);

            return Mathf.Asin(Mathf.Clamp(sinAlpha, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 0-1 surface insolation, lagged. The sine of the sun's height - the share of
        /// a horizontal surface's radiation it delivers - read at an hour angle shifted
        /// back by the surface-heating lag, so the peak lands in the early afternoon
        /// rather than at solar noon and the couple of hours after sunrise still read
        /// as night. Zero whenever the lagged sun is at or below the horizon. This is
        /// the one diurnal forcing the stability lapse and the local sea and slope
        /// winds all share, so they cannot peak at different times of day.
        /// </summary>
        public static float LaggedInsolation01(float latDeg, float decDeg,
                                               float hourAngleDeg)
        {
            float laggedHourAngleDeg = hourAngleDeg
                - AtmoConstants.StabilityHeatingLagHours
                  * AtmoConstants.DegreesPerHourOfHourAngle;

            float elevDeg = ElevationDeg(latDeg, decDeg, laggedHourAngleDeg);
            float sinElev = Mathf.Sin(elevDeg * Mathf.Deg2Rad);

            return sinElev > 0f ? sinElev : 0f;
        }

        /// <summary>
        /// deg. Sun compass bearing, 0 = north, increasing clockwise through a full
        /// 0..360 over the day, from cos A = (sin d - sin a sin phi) / (cos a cos phi).
        ///
        /// WHY THE REFLECTION. acos only ever answers 0..180, so on its own the sun
        /// would sweep east to south and then fold back the way it came instead of
        /// continuing into the west. Past solar noon - a positive hour angle - the
        /// true bearing is 360 minus the acos. Without this a shadow at 16:00 points
        /// the same way as at 08:00.
        ///
        /// THE DEGENERATE CASE. The denominator collapses at the poles, where cos phi
        /// is zero, and at a sun exactly overhead, where cos a is. In both the bearing
        /// is genuinely undefined - standing on the pole, every direction is south.
        /// Rather than divide by zero, fall back to the bearing implied by the hour
        /// angle alone, which is continuous with the general case and still turns
        /// through a full circle over the day.
        /// </summary>
        public static float AzimuthDeg(float latDeg, float decDeg, float hourAngleDeg, float elevDeg)
        {
            float phi = latDeg * Mathf.Deg2Rad;
            float delta = decDeg * Mathf.Deg2Rad;
            float alpha = elevDeg * Mathf.Deg2Rad;

            float denominator = Mathf.Cos(alpha) * Mathf.Cos(phi);

            if (Mathf.Abs(denominator) < AtmoConstants.AzimuthDenominatorFloor)
            {
                // Bearing from the hour angle: due south (180) at solar noon in the
                // northern sense, advancing 15 deg per hour with the sun.
                return WrapUnsignedDegrees(AtmoConstants.DegreesHalfCircle + hourAngleDeg);
            }

            float cosAzimuth = (Mathf.Sin(delta) - Mathf.Sin(alpha) * Mathf.Sin(phi)) / denominator;

            float azimuth = Mathf.Acos(Mathf.Clamp(cosAzimuth, -1f, 1f)) * Mathf.Rad2Deg;

            // Afternoon: the sun is west of the meridian.
            if (hourAngleDeg > 0f)
            {
                azimuth = AtmoConstants.DegreesPerCircle - azimuth;
            }

            return WrapUnsignedDegrees(azimuth);
        }

        // =====================================================================
        //  SECTION 6  -  DAY LENGTH  (L-03)
        // =====================================================================

        /// <summary>
        /// h. Hours between sunrise and sunset, from the sunrise hour angle
        /// acos(-tan phi tan d), doubled because the sun crosses it twice.
        ///
        /// POLAR NIGHT AND MIDNIGHT SUN ARE THE CORRECT ANSWERS, NOT ERRORS.
        /// Inside the polar circles the acos argument leaves -1..+1, because there
        /// genuinely is no hour at which the sun crosses the horizon. Clamped, that
        /// falls out as exactly 24 h or exactly 0 h. Left unclamped, acos answers
        /// NaN, and a NaN day length silently poisons temperature, then moisture,
        /// then every species suitability in section 6 - a world that generates
        /// empty with nothing in the console.
        ///
        /// Latitude is clamped off the poles first: at exactly 90 deg tan is
        /// infinite, and on the one day a year when declination is also exactly zero
        /// the product would be inf * 0, which is NaN and would pass straight
        /// through the clamp below.
        /// </summary>
        public static float DayLengthHours(float latDeg, float decDeg)
        {
            float safeLat = Mathf.Clamp(
                latDeg,
                -AtmoConstants.LatitudeTanGuardDeg,
                AtmoConstants.LatitudeTanGuardDeg);

            float phi = safeLat * Mathf.Deg2Rad;
            float delta = decDeg * Mathf.Deg2Rad;

            float cosOmega = -Mathf.Tan(phi) * Mathf.Tan(delta);

            if (cosOmega <= -1f) return ClockState.HoursPerDay;   // the sun never sets
            if (cosOmega >= 1f) return 0f;                        // the sun never rises

            float omegaDeg = Mathf.Acos(cosOmega) * Mathf.Rad2Deg;

            return RisingAndSettingCrossings * omegaDeg / AtmoConstants.DegreesPerHourOfHourAngle;
        }

        // =====================================================================
        //  SECTION 7  -  THE MOON  (L-30)
        // =====================================================================

        /// <summary>
        /// Fraction 0 to 1 of the synodic month: 0 is new moon, 0.5 is full.
        ///
        /// COMPUTED IN DOUBLE ON PURPOSE. The absolute day number is year * 365 plus
        /// the day, so by year 3000 it is past a million. A float carries about seven
        /// significant digits, which at a million leaves steps of roughly a
        /// sixteenth of a day - and dividing that by a 29.53 day month quantises the
        /// phase into visible jumps, so the moon would step rather than wax. The
        /// result is returned as a float because nothing downstream needs more.
        /// </summary>
        public static float LunarPhase01(int dayOfYear, int year, float epochOffsetDays)
        {
            double absoluteDay = (double)year * AtmoConstants.DaysPerCalendarYear + dayOfYear;
            double sinceEpoch = absoluteDay - epochOffsetDays;

            double phase = sinceEpoch / AtmoConstants.SynodicMonthDays;
            phase -= System.Math.Floor(phase);

            return (float)phase;
        }

        /// <summary>
        /// Fraction 0 to 1 of the synodic month, against the module's own epoch.
        /// </summary>
        public static float LunarPhase01(int dayOfYear, int year)
        {
            return LunarPhase01(dayOfYear, year, AtmoConstants.LunarEpochAbsoluteDay);
        }

        /// <summary>
        /// Fraction of the lunar disc lit, (1 - cos(2 pi phase)) / 2.
        ///
        /// MOONLIGHT SCALES WITH THIS, NOT WITH THE PHASE. The phase is a position in
        /// the cycle and is linear in time; the lit fraction is a cosine of it. A
        /// quarter moon stands a quarter of the way through the month but is half
        /// lit, and gives about a tenth of full-moon light. Driving a light off the
        /// phase directly makes the moon brighten at a constant rate, which reads as
        /// a dimmer switch rather than a moon.
        ///
        /// Owned here rather than in W-07, so that moonrise work has one definition
        /// of "how lit" to read instead of introducing a second.
        /// </summary>
        public static float LunarIlluminatedFraction(float lunarPhase01)
        {
            return (1f - Mathf.Cos(lunarPhase01 * Tau)) * Half;
        }

        // ---------------------------------------------------------------------
        //  W-07  WHERE THE MOON ACTUALLY IS
        //
        //  The phase above says how lit the moon is. It says nothing about whether
        //  it is in the sky, and those are different questions: a full moon is
        //  invisible at noon and a new moon rises with the sun. Night lighting that
        //  reads only the lit fraction produces a world where every full moon is
        //  bright all night and every new moon is dark all night, when the truth is
        //  that the full moon IS the night and the new moon is the day.
        //
        //  THE REDUCTION, IN ONE PLACE. The moon is placed the way any body off the
        //  ecliptic is placed: ecliptic longitude and latitude, rotated by the axial
        //  tilt into declination and right ascension, then read against the sun's
        //  own hour angle. Nothing here is a second copy of the sun's machinery -
        //  ElevationDeg and AzimuthDeg in section 5 take a declination and an hour
        //  angle and do not care which body produced them, which is why the moon
        //  needs no elevation function of its own.
        //
        //  WHAT IS APPROXIMATED, STATED PLAINLY. The moon's longitude is taken as
        //  the sun's plus the synodic phase, which ignores the ellipticity of the
        //  lunar orbit (the evection and annual equation terms, together about six
        //  degrees). That is a few degrees of bearing and up to about twenty minutes
        //  of moonrise time. For a light in a sky it is invisible; for an almanac it
        //  would not be, and this is not an almanac. The terms that ARE kept are the
        //  ones with visible consequences: the elongation, because it sets when the
        //  moon is up at all, and the orbital inclination, because without it the
        //  moon would ride the sun's exact path and the two would never separate in
        //  the sky.
        // ---------------------------------------------------------------------

        /// <summary>
        /// deg. The sun's ecliptic longitude: 0 at the vernal equinox, advancing a
        /// full turn over the year.
        ///
        /// Derived from the midwinter-zero phase rather than from the day number, so
        /// there is one definition of where the year has got to. The offset is the
        /// bridge between the two conventions: at midwinter the sun stands at 270 deg
        /// of longitude, a quarter turn short of the equinox.
        /// </summary>
        public static float SolarEclipticLongitudeDeg(float winterPhase01)
        {
            return WrapUnsignedDegrees(
                AtmoConstants.DegreesPerCircle * winterPhase01
                + SolarLongitudeAtMidwinterDeg);
        }

        /// <summary>
        /// deg. The moon's ecliptic latitude: how far it rides off the sun's path,
        /// swinging between plus and minus the orbital inclination over a draconic
        /// month.
        ///
        /// COMPUTED IN DOUBLE for the same reason the phase is: the absolute day
        /// number is large enough by year 3000 that float steps would quantise a
        /// 27 day cycle into visible jumps.
        /// </summary>
        public static float LunarEclipticLatitudeDeg(int dayOfYear, int year)
        {
            double absoluteDay = (double)year * AtmoConstants.DaysPerCalendarYear + dayOfYear;
            double sinceNode = absoluteDay - AtmoConstants.LunarNodeEpochAbsoluteDay;

            double nodePhase = sinceNode / AtmoConstants.DraconicMonthDays;
            nodePhase -= System.Math.Floor(nodePhase);

            return AtmoConstants.LunarOrbitInclinationDeg
                   * Mathf.Sin((float)nodePhase * Tau);
        }

        /// <summary>
        /// deg. The moon's declination: the latitude it stands over, exactly as the
        /// sun's declination is. This is what carries the moon high in a winter sky
        /// and low in a summer one - the opposite of the sun, because a full moon
        /// sits where the sun will be in six months.
        ///
        /// From sin d = sin b cos e + cos b sin e sin l, the standard rotation of
        /// ecliptic coordinates into equatorial ones. With b at zero it collapses to
        /// the sun's own declination, which is the check that the two agree.
        ///
        /// NOT THE SAME REDUCTION AS THE SUN'S, AND DELIBERATELY SO. The sun's
        /// declination above uses the cosine form the whole solar layer is built on,
        /// which is a small-angle approximation; this is the exact rotation, because
        /// the moon's ecliptic latitude gives it a term the cosine form has no place
        /// to put. The two agree exactly at the solstices and equinoxes and differ by
        /// up to about a quarter of a degree between them. Rewriting the sun to match
        /// would be a change to every shadow in every existing world for a quarter of
        /// a degree of accuracy, so the difference is documented and pinned in the
        /// self-test instead of quietly removed.
        /// </summary>
        public static float LunarDeclinationDeg(float winterPhase01,
                                                float lunarPhase01,
                                                float eclipticLatitudeDeg,
                                                float axialTiltDeg)
        {
            float lambda = LunarEclipticLongitudeDeg(winterPhase01, lunarPhase01) * Mathf.Deg2Rad;
            float beta = eclipticLatitudeDeg * Mathf.Deg2Rad;
            float epsilon = axialTiltDeg * Mathf.Deg2Rad;

            float sinDec = Mathf.Sin(beta) * Mathf.Cos(epsilon)
                           + Mathf.Cos(beta) * Mathf.Sin(epsilon) * Mathf.Sin(lambda);

            return Mathf.Asin(Mathf.Clamp(sinDec, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// deg. The moon's ecliptic longitude: the sun's, plus a full turn of
        /// elongation over the synodic month. New moon shares the sun's longitude,
        /// full moon stands opposite it.
        /// </summary>
        public static float LunarEclipticLongitudeDeg(float winterPhase01, float lunarPhase01)
        {
            return WrapUnsignedDegrees(
                SolarEclipticLongitudeDeg(winterPhase01)
                + AtmoConstants.DegreesPerCircle * lunarPhase01);
        }

        /// <summary>
        /// deg. The moon's hour angle, on the same convention as the sun's: 0 at its
        /// transit, negative before it.
        ///
        /// WHY IT IS THE SUN'S MINUS A DIFFERENCE OF RIGHT ASCENSIONS. An hour angle
        /// is local sidereal time minus right ascension, so two bodies at the same
        /// instant differ by exactly the difference of their right ascensions - and
        /// taking the sun's hour angle as the reference means the moon inherits the
        /// longitude and equation-of-time corrections already applied to it rather
        /// than repeating them. The moon lies east of the sun by its elongation, and
        /// a body further east transits later, so its hour angle is the smaller one.
        ///
        /// The difference goes through the right ascensions and not through the
        /// elongation directly, because the two are not equal off the equinoxes: the
        /// tilt compresses ecliptic longitude into right ascension by up to two and a
        /// half degrees, and using the raw elongation would put the transit ten
        /// minutes out at the solstices while being exact four times a year - which
        /// looks like an intermittent bug rather than a missing rotation.
        /// </summary>
        public static float LunarHourAngleDeg(float solarHourAngleDeg,
                                              float winterPhase01,
                                              float lunarPhase01,
                                              float eclipticLatitudeDeg,
                                              float axialTiltDeg)
        {
            float sunRaDeg = RightAscensionDeg(
                SolarEclipticLongitudeDeg(winterPhase01), 0f, axialTiltDeg);

            float moonRaDeg = RightAscensionDeg(
                LunarEclipticLongitudeDeg(winterPhase01, lunarPhase01),
                eclipticLatitudeDeg, axialTiltDeg);

            float elongationRaDeg = WrapSignedDegrees(moonRaDeg - sunRaDeg);

            return WrapSignedDegrees(solarHourAngleDeg - elongationRaDeg);
        }

        /// <summary>
        /// deg. Right ascension from ecliptic coordinates,
        /// tan a = (sin l cos e - tan b sin e) / cos l.
        ///
        /// Atan2 rather than Atan, because Atan collapses the four quadrants into
        /// two and would fold the second half of the year onto the first. Tan of the
        /// ecliptic latitude is safe here: the moon's is bounded by its orbital
        /// inclination, five degrees, nowhere near the pole where the tangent runs
        /// away.
        /// </summary>
        private static float RightAscensionDeg(float eclipticLongitudeDeg,
                                               float eclipticLatitudeDeg,
                                               float axialTiltDeg)
        {
            float lambda = eclipticLongitudeDeg * Mathf.Deg2Rad;
            float beta = eclipticLatitudeDeg * Mathf.Deg2Rad;
            float epsilon = axialTiltDeg * Mathf.Deg2Rad;

            float y = Mathf.Sin(lambda) * Mathf.Cos(epsilon)
                      - Mathf.Tan(beta) * Mathf.Sin(epsilon);

            return WrapUnsignedDegrees(Mathf.Atan2(y, Mathf.Cos(lambda)) * Mathf.Rad2Deg);
        }

        // =====================================================================
        //  SECTION 8  -  ATMOSPHERIC PATH  (L-27)
        // =====================================================================

        /// <summary>
        /// Relative air mass: how many vertical atmospheres the direct beam crosses.
        /// 1.0 straight up, about 37.9 at the horizon, by Kasten-Young (1989).
        ///
        /// Elevation is clamped at the horizon before evaluation. That is not a
        /// convenience: below zero the offset term's base drops under 1, the
        /// negative exponent inverts it, and the formula would report an air mass
        /// below 1 for a sun that is down - shorter than looking straight up.
        /// Holding the horizon value is also the physically right answer, since the
        /// horizon path is the longest one there is, and consumers gate on
        /// <see cref="ClockState.SunIsUp"/> for whether there is a beam at all.
        /// </summary>
        public static float AirMass(float elevDeg)
        {
            float alphaDeg = Mathf.Max(elevDeg, 0f);

            float refraction = AtmoConstants.KastenYoungCoefficient
                               * Mathf.Pow(
                                     alphaDeg + AtmoConstants.KastenYoungOffsetDeg,
                                     AtmoConstants.KastenYoungExponent);

            float denominator = Mathf.Sin(alphaDeg * Mathf.Deg2Rad) + refraction;

            return 1f / Mathf.Max(AtmoConstants.AirMassDenominatorFloor, denominator);
        }

        // =====================================================================
        //  SECTION 9  -  ANGLE WRAPPING
        //
        //  Private, because every public method above returns an angle already in
        //  its documented range. A caller that needs to wrap an angle is a caller
        //  that has been handed one from somewhere it should not have been.
        // =====================================================================

        /// <summary>deg. Wraps into -180..+180, keeping "before noon is negative" true.</summary>
        private static float WrapSignedDegrees(float degrees)
        {
            return Mathf.Repeat(degrees + AtmoConstants.DegreesHalfCircle, AtmoConstants.DegreesPerCircle)
                   - AtmoConstants.DegreesHalfCircle;
        }

        /// <summary>deg. Wraps into 0..360, the convention for a compass bearing.</summary>
        private static float WrapUnsignedDegrees(float degrees)
        {
            return Mathf.Repeat(degrees, AtmoConstants.DegreesPerCircle);
        }
    }
}
