// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  Module 3: Living World
//
//  File:    UCSelfTest.Weather.cs
//  Purpose: The weather half of the self test. Asserts that the astronomy in
//           ClockMath still implements the laws it claims, and that ClockState
//           has not grown into something too expensive to pass by value.
//
//  WHY A THIRD FILE
//  ----------------
//  Same reason as UCSelfTest.Ecology.cs: a partial class shares the counter and
//  the Check helper, so these land in the same tally without growing a file that
//  is already past two thousand lines.
//
//  WHAT THESE CHECKS ARE FOR
//  -------------------------
//  Solar geometry is the worst kind of code to get subtly wrong. Every formula
//  here is a handful of sines that compiles perfectly whatever the signs are, and
//  a wrong sign does not crash - it produces a world where the seasons are
//  inverted, or the sun sets in the east, or the shadows at four o'clock point
//  the same way as at eight. None of that raises an error. So the values are
//  pinned here against the law that fixes them.
//
//  The tolerances are the ones the task document names: half a degree on the
//  declination and elevation checks, a fifth of an hour on day length. Loose
//  enough that a differently-arranged form of the same formula passes, tight
//  enough that a different formula does not.
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>deg. The document's tolerance for declination and elevation.</summary>
        private const float SolarAngleToleranceDeg = 0.5f;

        /// <summary>h. The document's tolerance for equatorial day length.</summary>
        private const float DayLengthToleranceHours = 0.2f;

        private static void CheckWithin(float actual, float expected, float tolerance, string message)
        {
            Check(Mathf.Abs(actual - expected) <= tolerance,
                  message + " (expected " + expected + " +/- " + tolerance + ", got " + actual + ")");
        }

        // ---------------------------------------------------------------------
        //  W-01  THE CLOCK SNAPSHOT MUST STAY CHEAP
        // ---------------------------------------------------------------------

        /// <summary>
        /// ClockState is passed by value to every consumer in the weather and sky
        /// layers, several times a frame. That is only the right design while it
        /// stays small; a struct that grows past a cache line's worth of fields
        /// quietly turns every one of those hand-offs into a memcpy. The ceiling is
        /// the task document's 96 bytes.
        /// </summary>
        private static void TestClockStateStaysCheap()
        {
            int size = Marshal.SizeOf(typeof(ClockState));

            Check(size <= 96,
                  "W-01 ClockState must stay at or under 96 bytes to be worth passing "
                  + "by value (measured " + size + " bytes)");
        }

        // ---------------------------------------------------------------------
        //  W-02  DECLINATION  (L-01)
        // ---------------------------------------------------------------------

        /// <summary>
        /// The seasons, pinned at their four corners.
        ///
        /// A NOTE ON WHICH DAYS. The equinoxes are asserted at days 81 and 263, not
        /// at the calendar's 20 March and 22 September. This cosine model puts its
        /// zero where its argument reaches 90 deg, which is day 81.25 - the model's
        /// own equinox, about a day off the real one. Asserting the calendar date
        /// instead would be asserting the error rather than the law. (The equation
        /// of time uses the same day 81 for the same reason.)
        /// </summary>
        private static void TestSolarDeclination()
        {
            CheckWithin(ClockMath.DeclinationDeg(81), 0f, SolarAngleToleranceDeg,
                        "L-01 declination must cross zero at the vernal equinox");

            CheckWithin(ClockMath.DeclinationDeg(263), 0f, SolarAngleToleranceDeg,
                        "L-01 declination must cross zero at the autumnal equinox");

            CheckWithin(ClockMath.DeclinationDeg(172), AtmoConstants.EarthAxialTiltDegrees,
                        SolarAngleToleranceDeg,
                        "L-01 declination must reach +axial tilt at the June solstice");

            CheckWithin(ClockMath.DeclinationDeg(355), -AtmoConstants.EarthAxialTiltDegrees,
                        SolarAngleToleranceDeg,
                        "L-01 declination must reach -axial tilt at the December solstice");

            // A world with no tilt has no seasons. This is the check that catches a
            // declination formula that picked up a constant term somewhere.
            for (int day = 1; day <= 365; day += 30)
            {
                CheckWithin(ClockMath.DeclinationDegFromWinterPhase01(ClockMath.WinterPhase01(day), 0f),
                            0f, 1e-4f,
                            "L-01 a world with zero axial tilt must have zero declination all year");
            }
        }

        // ---------------------------------------------------------------------
        //  W-02  DAY LENGTH  (L-03)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Day length at the equator, in the polar circles, and the NaN guard.
        ///
        /// The polar cases are the point of this test. acos(-tan phi tan d) leaves
        /// its domain inside the polar circles, and unclamped it answers NaN - which
        /// does not throw, does not log, and propagates through temperature into
        /// moisture into every species suitability, producing an empty world with a
        /// clean console. So this walks every day of the year at 70 and at 90 deg and
        /// insists on a real number.
        /// </summary>
        private static void TestDayLength()
        {
            // The equator gets twelve hours every day of the year, at any tilt.
            for (int day = 1; day <= 365; day++)
            {
                float hours = ClockMath.DayLengthHours(0f, ClockMath.DeclinationDeg(day));

                CheckWithin(hours, ClockState.HoursPerDay * 0.5f, DayLengthToleranceHours,
                            "L-03 equatorial day length must stay near 12 h on day " + day);
            }

            // Inside the Arctic circle the sun must actually stop rising and setting.
            CheckWithin(ClockMath.DayLengthHours(70f, ClockMath.DeclinationDeg(172)),
                        ClockState.HoursPerDay, 0f,
                        "L-03 70 deg N at the June solstice must be exactly 24 h of daylight");

            CheckWithin(ClockMath.DayLengthHours(70f, ClockMath.DeclinationDeg(355)),
                        0f, 0f,
                        "L-03 70 deg N at the December solstice must be exactly 0 h of daylight");

            // The NaN sweep, including the pole itself where tan is infinite.
            float[] latitudes = { 0f, 45f, 66.5f, 70f, 89.9f, 90f, -90f };

            for (int i = 0; i < latitudes.Length; i++)
            {
                for (int day = 1; day <= 365; day++)
                {
                    float hours = ClockMath.DayLengthHours(latitudes[i], ClockMath.DeclinationDeg(day));

                    Check(!float.IsNaN(hours) && !float.IsInfinity(hours),
                          "L-03 day length must never be NaN or infinite - latitude "
                          + latitudes[i] + ", day " + day);
                }
            }

            // The hemispheres must be opposites, not copies. This catches a latitude
            // sign dropped by an abs() somewhere.
            float northMidsummer = ClockMath.DayLengthHours(45f, ClockMath.DeclinationDeg(172));
            float southMidsummer = ClockMath.DayLengthHours(-45f, ClockMath.DeclinationDeg(172));

            Check(northMidsummer > ClockState.HoursPerDay * 0.5f
                  && southMidsummer < ClockState.HoursPerDay * 0.5f,
                  "L-03 the June solstice must be the long day in the north and the "
                  + "short day in the south (got " + northMidsummer + " h and "
                  + southMidsummer + " h)");
        }

        // ---------------------------------------------------------------------
        //  W-02  SUN POSITION  (L-02)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Noon elevation, and the azimuth reflection.
        ///
        /// A NOTE ON THE LATITUDES CHOSEN. Noon elevation is asserted as
        /// 90 - phi + 23.44, which only holds where the noon sun is south of the
        /// zenith, that is for latitudes at or above the tropic. At the equator on
        /// the June solstice the sun stands 23.44 deg NORTH of overhead and the true
        /// elevation is 66.56, not the 113.44 the formula would give - an elevation
        /// above 90 being the tell that the formula has been taken outside its range.
        /// So this tests from 30 deg poleward and states why.
        /// </summary>
        private static void TestSunPosition()
        {
            float juneDeclination = ClockMath.DeclinationDeg(172);
            float[] latitudes = { 30f, 45f, 60f, 80f };

            for (int i = 0; i < latitudes.Length; i++)
            {
                float noonElevation = ClockMath.ElevationDeg(latitudes[i], juneDeclination, 0f);

                CheckWithin(noonElevation,
                            90f - latitudes[i] + AtmoConstants.EarthAxialTiltDegrees,
                            SolarAngleToleranceDeg,
                            "L-02 June solstice noon elevation at " + latitudes[i] + " deg");
            }

            // THE FOLD-BACK CHECK. acos only answers 0..180, so without the
            // afternoon reflection the sun would retrace its morning path westward
            // and a 16:00 shadow would point where an 08:00 one does.
            float morning = ClockMath.AzimuthDeg(45f, 0f, -45f,
                                                 ClockMath.ElevationDeg(45f, 0f, -45f));
            float afternoon = ClockMath.AzimuthDeg(45f, 0f, 45f,
                                                   ClockMath.ElevationDeg(45f, 0f, 45f));

            Check(morning > 90f && morning < AtmoConstants.DegreesHalfCircle,
                  "L-02 the morning sun must bear east of south in the northern "
                  + "hemisphere (got " + morning + " deg)");

            Check(afternoon > AtmoConstants.DegreesHalfCircle,
                  "L-02 the afternoon sun must bear west of south, not fold back east "
                  + "(got " + afternoon + " deg)");

            // At noon, due south in the north and due north in the south.
            CheckWithin(ClockMath.AzimuthDeg(45f, 0f, 0f, ClockMath.ElevationDeg(45f, 0f, 0f)),
                        AtmoConstants.DegreesHalfCircle, SolarAngleToleranceDeg,
                        "L-02 the northern-hemisphere noon sun must bear due south");

            float southernNoon = ClockMath.AzimuthDeg(-45f, 0f, 0f,
                                                      ClockMath.ElevationDeg(-45f, 0f, 0f));

            Check(southernNoon < SolarAngleToleranceDeg
                  || southernNoon > AtmoConstants.DegreesPerCircle - SolarAngleToleranceDeg,
                  "L-02 the southern-hemisphere noon sun must bear due north, not "
                  + "south (got " + southernNoon + " deg)");
        }

        // ---------------------------------------------------------------------
        //  W-02  TIME CORRECTION AND ITS SIGN
        // ---------------------------------------------------------------------

        /// <summary>
        /// The equation of time's sign, and the round trip between solar noon and a
        /// zero hour angle.
        ///
        /// The sign is the whole test. Around 22 March a sundial runs about seven and
        /// a half minutes BEHIND the clock, so the equation of time there is negative.
        /// Flip it and sunrise sits a quarter of an hour off in spring and autumn,
        /// exact at the solstices, with nothing to point at.
        /// </summary>
        private static void TestSolarTimeCorrection()
        {
            CheckWithin(ClockMath.EquationOfTimeMinutes(81),
                        -AtmoConstants.EquationOfTimeCosBMinutes, 0.01f,
                        "W-02 the equation of time must be negative in late March - a "
                        + "sundial there reads behind the clock");

            // Solar noon and a zero hour angle are the same instant by definition, so
            // feeding one into the other must come back to zero. This catches a sign
            // or a unit slip in the longitude term, which is otherwise invisible: the
            // world still has a day, it just has it at the wrong time.
            float eot = ClockMath.EquationOfTimeMinutes(100);
            float longitude = 12.5f;
            float standardMeridian = 15f;

            float noonHour = ClockMath.SolarNoonHour(longitude, eot, standardMeridian);

            float hourAngleAtNoon = ClockMath.SolarHourAngleDeg(
                noonHour * ClockState.SecondsPerHour, longitude, eot, standardMeridian);

            CheckWithin(hourAngleAtNoon, 0f, 0.01f,
                        "W-02 the hour angle at the reported solar noon must be zero");

            // East of the standard meridian the sun arrives early, so solar noon
            // falls before 12:00 on the clock once the equation of time is taken out.
            float eastCorrection = ClockMath.TimeCorrectionMinutes(20f, 0f, 15f);
            float westCorrection = ClockMath.TimeCorrectionMinutes(10f, 0f, 15f);

            Check(eastCorrection > 0f && westCorrection < 0f,
                  "W-02 with longitude positive east, a site east of its standard "
                  + "meridian must run ahead of the clock (got " + eastCorrection
                  + " min east, " + westCorrection + " min west)");
        }

        // ---------------------------------------------------------------------
        //  W-02  AIR MASS  (L-27)  AND THE MOON  (L-30)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Air mass is a MULTIPLE of the vertical path, so it is exactly 1 looking
        /// straight up - that is the definition, not an approximation. This is the
        /// check that would have caught the simpler 1/(sin a + 0.15) form, which
        /// answers 0.87 at the zenith and would have made every noon sky in every
        /// world permanently slightly hazy.
        /// </summary>
        private static void TestAirMassAndMoon()
        {
            CheckWithin(ClockMath.AirMass(90f), 1f, 0.01f,
                        "L-27 air mass must be 1 at the zenith by definition");

            CheckWithin(ClockMath.AirMass(0f), 37.9f, 1f,
                        "L-27 Kasten-Young air mass at the horizon is about 37.9");

            CheckWithin(ClockMath.AirMass(30f), 2f, 0.05f,
                        "L-27 air mass at 30 deg elevation is about 2");

            // Below the horizon the formula is out of range and must hold, not invert.
            CheckWithin(ClockMath.AirMass(-10f), ClockMath.AirMass(0f), 0f,
                        "L-27 a sun below the horizon must hold the horizon air mass "
                        + "rather than report a shorter path than straight up");

            // Monotone: the path can only get longer as the sun drops.
            float previous = ClockMath.AirMass(90f);
            for (float elevation = 85f; elevation >= 0f; elevation -= 5f)
            {
                float current = ClockMath.AirMass(elevation);

                Check(current >= previous,
                      "L-27 air mass must increase as the sun drops (" + elevation
                      + " deg gave " + current + " after " + previous + ")");

                previous = current;
            }

            // The moon. New is dark, full is lit, quarter is half lit - and the phase
            // itself is not the brightness, which is the mistake this pins.
            CheckWithin(ClockMath.LunarIlluminatedFraction(0f), 0f, 1e-4f,
                        "L-30 a new moon must be unlit");

            CheckWithin(ClockMath.LunarIlluminatedFraction(0.5f), 1f, 1e-4f,
                        "L-30 a full moon must be fully lit");

            CheckWithin(ClockMath.LunarIlluminatedFraction(0.25f), 0.5f, 1e-4f,
                        "L-30 a quarter moon stands a quarter through the month and is "
                        + "half lit");

            // The phase must actually advance day to day, and must stay inside 0..1.
            float first = ClockMath.LunarPhase01(1, 0);
            float second = ClockMath.LunarPhase01(2, 0);

            Check(first >= 0f && first < 1f && second >= 0f && second < 1f,
                  "L-30 lunar phase must stay in 0..1");

            Check(!Mathf.Approximately(first, second),
                  "L-30 lunar phase must advance from one day to the next");

            // A full synodic month later the moon must be back where it started. This
            // is the check that catches the phase being computed in float at a large
            // day number, where the precision loss quantises it into steps.
            float atEpoch = ClockMath.LunarPhase01(1, 2000);
            float aMonthLater = ClockMath.LunarPhase01(1 + 30, 2000);

            Check(Mathf.Abs(aMonthLater - atEpoch) < 0.06f
                  || Mathf.Abs(aMonthLater - atEpoch) > 0.94f,
                  "L-30 the moon must return to nearly the same phase one synodic "
                  + "month later, even a thousand years in (got " + atEpoch + " then "
                  + aMonthLater + ")");
        }

        // ---------------------------------------------------------------------
        //  W-02, W-04  SINGLE OWNERSHIP OF THE SOLAR FORMULAE AND OF LATITUDE
        // ---------------------------------------------------------------------

        /// <summary>
        /// UCLightingSystemManager used to carry its own declination and day-length
        /// formulae, and its own latitude and axial tilt. The formulae now forward to
        /// ClockMath and the site values are read from the weather manager. This
        /// asserts both, which is what makes reintroducing a second copy fail here
        /// rather than three months later when one of the two gets a fix the other
        /// does not.
        /// </summary>
        private static void TestSolarFormulaeHaveOneOwner()
        {
            const float ProbeLatitudeDeg = 52f;

            // The lighting manager resolves the clock by searching its world root,
            // so the two components go on one object under one parent - the shape a
            // real world has.
            var world = new GameObject("UC_SelfTest_World");

            try
            {
                var weather = world.AddComponent<UniverseCreator.TerrainSystem.UCWeatherSystemManager>();
                var lighting = world.AddComponent<UniverseCreator.TerrainSystem.UCLightingSystemManager>();

                weather.latitudeDeg = ProbeLatitudeDeg;
                weather.axialTiltDeg = AtmoConstants.EarthAxialTiltDegrees;

                // W-04. The value the lighting manager reports must be the one the
                // weather manager was given, with nothing of its own in between.
                CheckWithin(lighting.LatitudeDegrees, ProbeLatitudeDeg, 1e-4f,
                            "W-04 the lighting manager must report the weather "
                            + "manager's latitude, not one of its own");

                CheckWithin(lighting.AxialTiltDegrees, AtmoConstants.EarthAxialTiltDegrees,
                            1e-4f,
                            "W-04 the lighting manager must report the weather "
                            + "manager's axial tilt, not one of its own");

                for (int day = 1; day <= 365; day += 60)
                {
                    float phase = ClockMath.WinterPhase01(day);

                    CheckWithin(lighting.SolarDeclinationDegrees(phase),
                                ClockMath.DeclinationDeg(day), 1e-3f,
                                "W-02 the lighting manager's declination must be "
                                + "ClockMath's, not a second copy (day " + day + ")");

                    CheckWithin(lighting.DayLengthHours(phase),
                                ClockMath.DayLengthHours(
                                    ProbeLatitudeDeg, ClockMath.DeclinationDeg(day)),
                                1e-3f,
                                "W-02 the lighting manager's day length must be "
                                + "ClockMath's, not a second copy (day " + day + ")");
                }

                // W-04. Deleting the weather system must leave lighting working at
                // the stated default rather than at zero or at a stale copy.
                Object.DestroyImmediate(weather);

                Check(!lighting.HasClockSource,
                      "W-04 with no weather system the lighting manager must report "
                      + "that it has no clock source, so the inspector can say so");

                CheckWithin(lighting.LatitudeDegrees,
                            UniverseCreator.TerrainSystem.UCWeatherSystemManager.DefaultLatitudeDeg,
                            1e-4f,
                            "W-04 with no weather system the lighting manager must "
                            + "fall back to the documented default latitude");
            }
            finally
            {
                Object.DestroyImmediate(world);
            }
        }

        // ---------------------------------------------------------------------
        //  W-03  THE CLOCK IS A CLOCK, NOT A FRAME COUNTER
        // ---------------------------------------------------------------------

        /// <summary>
        /// W-03's two clauses. First: a paused clock holds everything together, so
        /// the sun, the season and the phenology cannot drift apart. Second: the
        /// same authored day and hour resolve to an identical ClockState however
        /// many frames have gone past - which is what a frame counter cannot do,
        /// and the reason this task exists.
        /// </summary>
        private static void TestClockIsDeterministic()
        {
            var world = new GameObject("UC_SelfTest_Clock");

            try
            {
                var weather = world.AddComponent<UniverseCreator.TerrainSystem.UCWeatherSystemManager>();

                weather.latitudeDeg = 45f;
                weather.longitudeDeg = 0f;
                weather.standardMeridianDeg = 0f;
                weather.axialTiltDeg = AtmoConstants.EarthAxialTiltDegrees;

                weather.SetTime(172, 12f);
                ClockState first = weather.ResolveClock();

                // Stand in for "from a different frame count". SetTime clears the
                // accumulator, so arriving at day 172 hour 12 by way of two other
                // times must give the same answer as arriving there directly.
                weather.SetTime(1, 3.25f);
                weather.ResolveClock();
                weather.SetTime(300, 21.75f);
                weather.ResolveClock();
                weather.SetTime(172, 12f);

                ClockState second = weather.ResolveClock();

                Check(first.dayOfYear == second.dayOfYear && first.year == second.year,
                      "W-03 day 172 set twice must give the same date (got day "
                      + first.dayOfYear + " then " + second.dayOfYear + ")");

                CheckWithin(second.secondsOfDay, first.secondsOfDay, 1e-3f,
                            "W-03 hour 12 set twice must give the same second of day");
                CheckWithin(second.elevationDeg, first.elevationDeg, 1e-4f,
                            "W-03 the same authored time must give the same sun "
                            + "elevation regardless of how it was reached");
                CheckWithin(second.azimuthDeg, first.azimuthDeg, 1e-4f,
                            "W-03 the same authored time must give the same sun azimuth");
                CheckWithin(second.declinationDeg, first.declinationDeg, 1e-4f,
                            "W-03 the same authored time must give the same declination");
                CheckWithin(second.season01, first.season01, 1e-4f,
                            "W-03 the same authored time must give the same season");
                CheckWithin(second.lunarPhase01, first.lunarPhase01, 1e-4f,
                            "W-03 the same authored time must give the same lunar phase");

                // Resolving twice in a row without touching anything must also be
                // identical: a cached or accumulated term would show up here.
                ClockState again = weather.ResolveClock();
                CheckWithin(again.elevationDeg, second.elevationDeg, 0f,
                            "W-03 two resolves with no change between them must be "
                            + "bit-identical, so nothing is accumulating inside");

                // Paused, the clock must not move for any elapsed time at all.
                weather.pauseTime = true;
                weather.timeScale = 100000f;
                ClockState paused = weather.ResolveClock();

                Check(weather.ElapsedSimSeconds == 0.0,
                      "W-03 a paused clock must not accumulate elapsed time");
                CheckWithin(paused.localHour, 12f, 1e-3f,
                            "W-03 a paused clock must hold the authored hour");

                // The roll-over. 200 days on from day 172 is day 7 of the next year,
                // and the year must have advanced with it rather than the day
                // wrapping on its own.
                weather.SetTime(172, 12f);
                weather.pauseTime = false;
                weather.Advance(200.0 * ClockState.SecondsPerDay);

                ClockState rolled = weather.ResolveClock();

                Check(rolled.dayOfYear == 7,
                      "W-03 200 days on from day 172 must be day 7 (got day "
                      + rolled.dayOfYear + ")");
                Check(rolled.year == 2,
                      "W-03 rolling past day 365 must advance the year (got year "
                      + rolled.year + ")");
                CheckWithin(rolled.localHour, 12f, 1e-2f,
                            "W-03 a whole number of days on must land at the same hour");

                // W-03. The season must be derived from the date, not stored beside
                // it. This is the check that fails if a second season accumulator is
                // ever reintroduced: the old code advanced seasonPhase from the
                // vegetation tick, so after a roll-over like the one above the year
                // and the season disagreed and nothing reported it.
                CheckWithin(weather.seasonPhase,
                            ClockMath.WinterPhase01(rolled.dayOfYear, rolled.secondsOfDay), 1e-4f,
                            "W-03 the season must be derived from the clock's day, "
                            + "not accumulated separately");

                // W-06. The southern hemisphere's warm season must fall in January,
                // which is what makes an enum unusable for seasons.
                weather.SetTime(15, 12f);

                weather.latitudeDeg = 45f;
                float northSeason = weather.ResolveClock().season01;

                weather.latitudeDeg = -45f;
                float southSeason = weather.ResolveClock().season01;

                CheckWithin(Mathf.Abs(Mathf.DeltaAngle(
                                northSeason * 360f, southSeason * 360f)), 180f, 1f,
                            "W-06 the same day must be opposite seasons north and "
                            + "south of the equator");
            }
            finally
            {
                Object.DestroyImmediate(world);
            }
        }

        /// <summary>
        /// W-05. The latitude gradient. Two clauses matter and both are checked here.
        ///
        /// FIRST, span 0 must change nothing - bit-identical, not merely close. That is
        /// the compatibility promise every existing world depends on, and a tolerance
        /// would let a drift of a thousandth of a degree through, which is enough to
        /// move a species boundary by one site and make a bake unreproducible.
        ///
        /// SECOND, and this is the bug this test exists to catch: latitude must never
        /// move alone. Four fields are derived from it, and a rebase that updated only
        /// latitudeDeg would leave a chunk at 60 deg holding the day length, radiation
        /// and warm slope of 45 - a world that is confidently wrong with nothing in the
        /// console. So each derived field is compared against the same formula called
        /// directly, rather than against a recorded number.
        /// </summary>
        private static void TestLatitudeGradient()
        {
            const float Centre = 45f;
            const float Span = 30f;
            const float Phase = 0.6f;
            const float Tilt = 23.44f;

            // ---- Span 0 changes nothing -------------------------------------

            EcoSampleSettings flat = new EcoSampleSettings();
            flat.latitudeDeg = Centre;
            flat.latitudeSpanDeg = 0f;
            flat.axialTiltDeg = Tilt;
            flat.seasonPhase = Phase;
            flat.warmAspectDeg = 180f;
            flat.dayLengthH = 14.5f;
            flat.radiationMjPerM2Day = 37f;
            flat.seaLevelMeanTempC = 11f;

            // Both edges, because a mistake that rebased only one direction would pass
            // a single-sample check.
            for (int i = 0; i <= 2; i++)
            {
                float fraction = i * 0.5f;
                EcoSampleSettings same = flat.RebasedToLatitude(fraction);

                CheckWithin(same.latitudeDeg, flat.latitudeDeg, 0f,
                            "W-05 span 0 must leave latitude bit-identical");
                CheckWithin(same.dayLengthH, flat.dayLengthH, 0f,
                            "W-05 span 0 must leave day length bit-identical");
                CheckWithin(same.radiationMjPerM2Day, flat.radiationMjPerM2Day, 0f,
                            "W-05 span 0 must leave radiation bit-identical");
                CheckWithin(same.warmAspectDeg, flat.warmAspectDeg, 0f,
                            "W-05 span 0 must leave the warm aspect bit-identical");
                CheckWithin(same.seaLevelMeanTempC, flat.seaLevelMeanTempC, 0f,
                            "W-05 span 0 must leave sea level temperature bit-identical");
            }

            // ---- A span puts the edges where the arithmetic says -------------

            EcoSampleSettings spanning = flat;
            spanning.latitudeSpanDeg = Span;

            EcoSampleSettings south = spanning.RebasedToLatitude(0f);
            EcoSampleSettings middle = spanning.RebasedToLatitude(0.5f);
            EcoSampleSettings north = spanning.RebasedToLatitude(1f);

            CheckWithin(south.latitudeDeg, Centre - Span * 0.5f, 1e-4f,
                        "W-05 the south edge sits half a span below the authored latitude");
            CheckWithin(north.latitudeDeg, Centre + Span * 0.5f, 1e-4f,
                        "W-05 the north edge sits half a span above the authored latitude");
            CheckWithin(middle.latitudeDeg, Centre, 1e-4f,
                        "W-05 the authored latitude belongs at the world's centre");

            // ---- Every derived field moved with it ---------------------------

            float declination = ClockMath.DeclinationDegFromWinterPhase01(Phase, Tilt);

            CheckWithin(north.dayLengthH,
                        ClockMath.DayLengthHours(north.latitudeDeg, declination), 1e-4f,
                        "W-05 day length must be re-derived from the chunk's latitude");
            CheckWithin(south.dayLengthH,
                        ClockMath.DayLengthHours(south.latitudeDeg, declination), 1e-4f,
                        "W-05 day length must be re-derived from the chunk's latitude");

            CheckWithin(north.radiationMjPerM2Day,
                        ClockMath.ExtraterrestrialRadiationMjPerM2Day(
                            north.latitudeDeg, declination, Phase), 1e-4f,
                        "W-05 radiation must be re-derived from the chunk's latitude");

            Check(north.dayLengthH != south.dayLengthH,
                  "W-05 a 30 degree span must give the two edges different day lengths, "
                  + "or the gradient reaches the ecology in name only");
            Check(north.radiationMjPerM2Day != south.radiationMjPerM2Day,
                  "W-05 a 30 degree span must give the two edges different radiation");

            // ---- The warm slope inverts across the equator -------------------
            //
            // The case a single-hemisphere test would miss: a world centred on the
            // equator with a wide span has hillsides facing opposite ways at its two
            // ends, and getting this wrong inverts every slope in half the world.

            EcoSampleSettings straddling = flat;
            straddling.latitudeDeg = 0f;
            straddling.latitudeSpanDeg = 40f;

            CheckWithin(straddling.RebasedToLatitude(1f).warmAspectDeg,
                        TopographyMath.WarmAspectDegFor(20f), 1e-4f,
                        "W-05 the warm slope faces the equator in the northern half");
            CheckWithin(straddling.RebasedToLatitude(0f).warmAspectDeg,
                        TopographyMath.WarmAspectDegFor(-20f), 1e-4f,
                        "W-05 the warm slope must invert in the southern half of a "
                        + "world that straddles the equator");

            // ---- V-26 moves the baseline temperature, and only then ----------

            EcoSampleSettings derived = spanning;
            derived.deriveTemperatureFromLatitude = true;
            derived.equatorMeanTempC = ThermalEquivalence.EquatorSeaLevelMeanTempC;

            CheckWithin(derived.RebasedToLatitude(1f).seaLevelMeanTempC,
                        ThermalEquivalence.SeaLevelMeanTempC(
                            north.latitudeDeg,
                            ThermalEquivalence.EquatorSeaLevelMeanTempC), 1e-4f,
                        "W-05 under V-26 the baseline temperature follows the chunk's "
                        + "latitude");

            Check(derived.RebasedToLatitude(1f).seaLevelMeanTempC
                  < derived.RebasedToLatitude(0f).seaLevelMeanTempC,
                  "W-05 the north edge of a northern-hemisphere world must be the "
                  + "colder one");

            // Without V-26 the author typed a temperature and means it everywhere.
            CheckWithin(north.seaLevelMeanTempC, flat.seaLevelMeanTempC, 0f,
                        "W-05 with V-26 off, a latitude span must not overwrite the "
                        + "authored sea level temperature");

            // ---- The clamp stops at the pole, it does not wrap ----------------

            EcoSampleSettings absurd = flat;
            absurd.latitudeDeg = 60f;
            absurd.latitudeSpanDeg = 120f;

            CheckWithin(absurd.RebasedToLatitude(1f).latitudeDeg,
                        ClockMath.PoleLatitudeDeg, 1e-4f,
                        "W-05 an over-large span must clamp at the pole rather than "
                        + "wrapping into a second equator");

            // ---- The lighting manager and ClockMath agree on Ra ---------------
            //
            // The Ra body moved into ClockMath so a worker could reach it. This proves
            // the manager forwards rather than keeping a second copy that will one day
            // receive a fix this one does not.

            var world = new GameObject("UC_SelfTest_Radiation");
            try
            {
                var weather = world.AddComponent<
                    UniverseCreator.TerrainSystem.UCWeatherSystemManager>();
                var lighting = world.AddComponent<
                    UniverseCreator.TerrainSystem.UCLightingSystemManager>();

                weather.latitudeDeg = Centre;
                weather.axialTiltDeg = Tilt;

                CheckWithin(lighting.ExtraterrestrialRadiationMjPerM2Day(Phase),
                            ClockMath.ExtraterrestrialRadiationMjPerM2Day(
                                Centre,
                                ClockMath.DeclinationDegFromWinterPhase01(Phase, Tilt),
                                Phase), 1e-3f,
                            "W-05 the lighting manager must forward to ClockMath for "
                            + "radiation, not keep its own copy of FAO-56");
            }
            finally
            {
                Object.DestroyImmediate(world);
            }
        }

        // ---------------------------------------------------------------------
        //  W-07  THE MOON HAS A POSITION, NOT JUST A PHASE
        //
        //  The placeholder this replaces reported the moon at -90 deg for ever, so
        //  MoonIsUp was always false. That passes any test that only asks "is the new
        //  moon dark" - which is why the checks below are written in pairs: every
        //  clause that must be dark is matched by one that must be lit, and a moon
        //  nailed under the horizon fails the second half of each pair.
        // ---------------------------------------------------------------------

        private static void TestMoonPosition()
        {
            const float Tilt = AtmoConstants.EarthAxialTiltDegrees;

            // FIRST: the ecliptic latitude. Bounded by the orbital inclination, and it
            // must actually swing - an inclination that reached the moon as a constant
            // would leave it riding the sun's exact path for ever.
            float minLat = float.MaxValue;
            float maxLat = float.MinValue;

            for (int day = 1; day <= 28; day++)
            {
                float beta = ClockMath.LunarEclipticLatitudeDeg(day, 0);

                Check(Mathf.Abs(beta) <= AtmoConstants.LunarOrbitInclinationDeg + 1e-3f,
                      "W-07 the moon's ecliptic latitude cannot exceed its orbital "
                      + "inclination (day " + day + " gave " + beta + " deg)");

                if (beta < minLat) minLat = beta;
                if (beta > maxLat) maxLat = beta;
            }

            Check(maxLat > AtmoConstants.LunarOrbitInclinationDeg * 0.9f
                  && minLat < -AtmoConstants.LunarOrbitInclinationDeg * 0.9f,
                  "W-07 the moon must ride to both sides of the ecliptic within a "
                  + "draconic month (got " + minLat + " to " + maxLat + " deg)");

            // SECOND: with the moon on the ecliptic and at zero elongation it IS the
            // sun, so its declination must collapse onto the sun's. This is the check
            // that the rotation from ecliptic to equatorial coordinates is the right
            // one rather than merely a plausible one.
            //
            // TWO TOLERANCES, AND THE REASON MATTERS. The two are not the same
            // reduction: the moon's path goes through asin(sin e sin l), which is
            // exact, while the sun's declination uses the cosine form the whole solar
            // layer is built on, which is a small-angle approximation. They agree
            // exactly at the solstices and equinoxes and part company by up to about
            // a quarter of a degree in between - a real and known difference, not a
            // fault, and one worth pinning at both scales so that neither a broken
            // rotation nor a drift past that quarter degree can pass.
            float[] quarterPoints = { 0f, 0.25f, 0.5f, 0.75f };

            for (int i = 0; i < quarterPoints.Length; i++)
            {
                CheckWithin(
                    ClockMath.LunarDeclinationDeg(quarterPoints[i], 0f, 0f, Tilt),
                    ClockMath.DeclinationDegFromWinterPhase01(quarterPoints[i], Tilt),
                    1e-2f,
                    "W-07 at a solstice or equinox the two declination reductions "
                    + "must agree exactly (winter phase " + quarterPoints[i] + ")");
            }

            for (float winterPhase = 0f; winterPhase < 1f; winterPhase += 0.0625f)
            {
                CheckWithin(
                    ClockMath.LunarDeclinationDeg(winterPhase, 0f, 0f, Tilt),
                    ClockMath.DeclinationDegFromWinterPhase01(winterPhase, Tilt),
                    0.4f,
                    "W-07 a body on the ecliptic at the sun's longitude must share "
                    + "the sun's declination to within the difference between the two "
                    + "reductions (winter phase " + winterPhase + ")");
            }

            // THIRD: a full moon stands opposite the sun, so its declination is the
            // sun's negated - which is the whole reason a full moon rides high on a
            // winter night and skims the horizon in summer.
            CheckWithin(
                ClockMath.LunarDeclinationDeg(0f, 0.5f, 0f, Tilt),
                -ClockMath.DeclinationDegFromWinterPhase01(0f, Tilt),
                1e-2f,
                "W-07 a full moon must stand over the opposite latitude to the sun");

            // FOURTH: the hour angle. At new moon the moon transits with the sun; at
            // full moon it transits half a turn away. Zero elongation must leave the
            // sun's hour angle untouched, or the moon has picked up an offset of its
            // own somewhere.
            CheckWithin(ClockMath.LunarHourAngleDeg(0f, 0.3f, 0f, 0f, Tilt), 0f, 1e-2f,
                        "W-07 a new moon must transit with the sun");

            CheckWithin(Mathf.Abs(ClockMath.LunarHourAngleDeg(0f, 0.3f, 0.5f, 0f, Tilt)),
                        AtmoConstants.DegreesHalfCircle, 1f,
                        "W-07 a full moon must transit half a turn from the sun");

            TestMoonAgainstTheClock();
        }

        /// <summary>
        /// The same clauses through a live manager, which is where the wiring can be
        /// wrong even when every formula is right.
        /// </summary>
        private static void TestMoonAgainstTheClock()
        {
            var world = new GameObject("UC_SelfTest_Moon");

            try
            {
                var weather = world.AddComponent<
                    UniverseCreator.TerrainSystem.UCWeatherSystemManager>();

                weather.latitudeDeg = 45f;
                weather.longitudeDeg = 0f;
                weather.standardMeridianDeg = 0f;
                weather.axialTiltDeg = AtmoConstants.EarthAxialTiltDegrees;

                // A 30 day scrub at a fixed hour. Over slightly more than one synodic
                // month the lit fraction must reach both ends of its range: this is
                // W-07's "a 29.5-day scrub shows a full cycle" clause, and it is the
                // check that fails if the phase is ever quantised or frozen.
                float minLit = float.MaxValue;
                float maxLit = float.MinValue;

                int newMoonDay = -1;
                int fullMoonDay = -1;
                float nearestToNew = float.MaxValue;
                float nearestToFull = float.MaxValue;

                for (int day = 1; day <= 31; day++)
                {
                    weather.SetTime(day, 12f);
                    ClockState clock = weather.ResolveClock();

                    if (clock.lunarIlluminatedFraction < minLit)
                        minLit = clock.lunarIlluminatedFraction;

                    if (clock.lunarIlluminatedFraction > maxLit)
                        maxLit = clock.lunarIlluminatedFraction;

                    // Distance to new measured on the wrapped phase, since new moon
                    // sits at both ends of the range.
                    float toNew = Mathf.Min(clock.lunarPhase01, 1f - clock.lunarPhase01);
                    if (toNew < nearestToNew) { nearestToNew = toNew; newMoonDay = day; }

                    float toFull = Mathf.Abs(clock.lunarPhase01 - 0.5f);
                    if (toFull < nearestToFull) { nearestToFull = toFull; fullMoonDay = day; }

                    CheckWithin(clock.lunarIlluminatedFraction,
                                ClockMath.LunarIlluminatedFraction(clock.lunarPhase01),
                                1e-4f,
                                "W-07 the clock's lit fraction must be the one "
                                + "ClockMath derives from its own phase (day " + day + ")");
                }

                Check(minLit < 0.02f && maxLit > 0.98f,
                      "W-07 a 30 day scrub must show a full cycle of illuminated "
                      + "fraction (got " + minLit + " to " + maxLit + ")");

                Check(newMoonDay > 0 && fullMoonDay > 0,
                      "W-07 a 30 day scrub must contain both a new and a full moon");

                // A GENUINELY DARK NIGHT. At the new moon the moon keeps the sun's
                // company, so at local midnight it is under the horizon with it.
                weather.SetTime(newMoonDay, 0f);
                ClockState newMoonMidnight = weather.ResolveClock();

                Check(!newMoonMidnight.SunIsUp,
                      "W-07 the midnight used for the dark-night check must itself be "
                      + "dark (sun at " + newMoonMidnight.elevationDeg + " deg)");

                Check(!newMoonMidnight.MoonIsUp,
                      "W-07 a new moon must be below the horizon at midnight, so the "
                      + "night is genuinely dark (moon at "
                      + newMoonMidnight.lunarElevationDeg + " deg)");

                Check(newMoonMidnight.lunarIlluminatedFraction < 0.05f,
                      "W-07 the new moon must also be unlit, not merely absent");

                // THE OTHER HALF OF THE PAIR. A full moon at midnight must be up and
                // lit. A placeholder elevation passes the clause above and fails here,
                // which is the point of testing both.
                weather.SetTime(fullMoonDay, 0f);
                ClockState fullMoonMidnight = weather.ResolveClock();

                Check(fullMoonMidnight.MoonIsUp,
                      "W-07 a full moon must be above the horizon at midnight (got "
                      + fullMoonMidnight.lunarElevationDeg + " deg)");

                Check(fullMoonMidnight.lunarIlluminatedFraction > 0.95f,
                      "W-07 the full moon must be lit as well as up");

                // And it must be DOWN in the middle of the day, which is the clause a
                // moon pinned above the horizon would fail.
                weather.SetTime(fullMoonDay, 12f);

                Check(!weather.ResolveClock().MoonIsUp,
                      "W-07 a full moon must be below the horizon at midday");

                // The moon must move. Six hours of the world's rotation is 90 deg of
                // hour angle, so no honest elevation survives it unchanged.
                weather.SetTime(fullMoonDay, 0f);
                float atMidnight = weather.ResolveClock().lunarElevationDeg;

                weather.SetTime(fullMoonDay, 6f);
                float sixHoursOn = weather.ResolveClock().lunarElevationDeg;

                Check(Mathf.Abs(atMidnight - sixHoursOn) > 5f,
                      "W-07 the moon's elevation must change through the night rather "
                      + "than being reported from a placeholder (got " + atMidnight
                      + " then " + sixHoursOn + " deg)");

                // The bearing must be a real compass bearing, and the same one the
                // shared azimuth function gives for the moon's own declination and
                // hour angle - not a second reduction living in the manager.
                ClockState sample = weather.ResolveClock();

                Check(sample.lunarAzimuthDeg >= 0f
                      && sample.lunarAzimuthDeg <= AtmoConstants.DegreesPerCircle,
                      "W-07 the moon's bearing must be a 0..360 compass bearing (got "
                      + sample.lunarAzimuthDeg + ")");

                CheckWithin(sample.lunarElevationDeg,
                            ClockMath.ElevationDeg(
                                weather.latitudeDeg,
                                sample.lunarDeclinationDeg,
                                ClockMath.LunarHourAngleDeg(
                                    sample.hourAngleDeg,
                                    ClockMath.WinterPhase01(sample.dayOfYear, sample.secondsOfDay),
                                    sample.lunarPhase01,
                                    ClockMath.LunarEclipticLatitudeDeg(
                                        // UC Scene/Play: verify the same fractional-day instant as the clock.
                                        sample.dayOfYear, sample.year, sample.secondsOfDay),
                                    weather.axialTiltDeg)),
                            1e-3f,
                            "W-07 the clock's moon elevation must be the shared "
                            + "elevation function applied to the moon's own "
                            + "declination and hour angle");

                // DETERMINISM, W-07's third clause. Two arrivals at the same instant
                // by different routes must agree bit for bit: the phase runs off an
                // absolute day number in double, so there is no accumulated state to
                // drift and nothing here may introduce one.
                weather.SetTime(200, 3f);
                ClockState firstVisit = weather.ResolveClock();

                weather.SetTime(17, 22.5f);
                weather.ResolveClock();
                weather.SetTime(200, 3f);
                ClockState secondVisit = weather.ResolveClock();

                CheckWithin(secondVisit.lunarPhase01, firstVisit.lunarPhase01, 0f,
                            "W-07 the lunar phase must be identical on a second "
                            + "arrival at the same instant");

                CheckWithin(secondVisit.lunarElevationDeg, firstVisit.lunarElevationDeg,
                            0f,
                            "W-07 the moon's position must be identical on a second "
                            + "arrival at the same instant");
            }
            finally
            {
                Object.DestroyImmediate(world);
            }
        }

        // =====================================================================
        //  THE CLOCK SCRUB TARGETS
        //
        //  The inspector offers four jumps - sunrise, solar noon, sunset, midnight -
        //  and every one of them is computed from the resolved clock rather than
        //  typed. This is the check that the computation is the right one.
        //
        //  WHY IT IS TESTED THROUGH THE MANAGER AND NOT THE GUI. A button does two
        //  things: it works out an hour, and it calls SetTime. Only the first can be
        //  wrong in an interesting way, and it is reachable without a window - so the
        //  test asks the clock for its own sunrise hour, moves the clock there and
        //  re-resolves. If the arithmetic drifts, the sun lands at the wrong height
        //  and the failure names the latitude and the day.
        //
        //  A LOOSE TOLERANCE WOULD PASS A BROKEN BUTTON. One degree is the promise,
        //  and a flat 06:00 jump misses by twenty to forty degrees at these
        //  latitudes, so the margin is not a matter of taste.
        // =====================================================================

        private static void TestClockScrubTargets()
        {
            // Pure arithmetic first, with no scene involved: the two crossings must
            // sit symmetrically about solar noon and span exactly the day length.
            // Checked before any sun is placed, so a failure here is unambiguous.
            float noonHour = ClockMath.SolarNoonHour(0f, 0f, 0f);
            float dayLength = ClockMath.DayLengthHours(45f, ClockMath.DeclinationDeg(172));

            ClockState reference = MakeClockForCrossings(noonHour, dayLength, 45f);

            CheckWithin(reference.SunsetHour - reference.SunriseHour, dayLength, 1e-3f,
                        "sunset minus sunrise must equal the day length exactly");

            CheckWithin((reference.SunriseHour + reference.SunsetHour) * 0.5f,
                        noonHour, 1e-3f,
                        "the two crossings must straddle solar noon");

            Check(ClockState.MidnightHour == 0f,
                  "the named midnight hour must be 0, not a rounded value");

            var world = new GameObject("UC_SelfTest_Scrub");

            try
            {
                var weather = world.AddComponent<
                    UniverseCreator.TerrainSystem.UCWeatherSystemManager>();

                weather.axialTiltDeg = AtmoConstants.EarthAxialTiltDegrees;
                weather.longitudeDeg = 0f;
                weather.standardMeridianDeg = 0f;
                weather.latitudeSpanDeg = 0f;

                // Latitudes spanning both hemispheres, the tropics, the temperate band
                // and just inside the arctic circle; days at both solstices, both
                // equinoxes and four dates between. The arctic entry is the one a
                // sweep of mid latitudes would miss, and it is where a day length
                // close to 0 or 24 makes the crossing arithmetic most fragile.
                float[] latitudes = { -60f, -35f, -10f, 0f, 23.5f, 45f, 60f, 66f };
                int[] days = { 1, 46, 80, 172, 213, 264, 300, 355 };

                int crossingsTested = 0;

                for (int li = 0; li < latitudes.Length; li++)
                {
                    weather.latitudeDeg = latitudes[li];

                    for (int di = 0; di < days.Length; di++)
                    {
                        int day = days[di];

                        weather.SetTime(day, ClockState.MidnightHour);
                        ClockState before = weather.ResolveClock();

                        if (!before.HasSunriseToday) continue;

                        crossingsTested++;
                        CheckOneDaysCrossings(weather, before, day, latitudes[li]);
                    }
                }

                Check(crossingsTested >= 50,
                      "the scrub sweep must actually reach most of its cases, reached "
                      + crossingsTested);

                CheckScrubFarFromMeridian(weather);
                CheckScrubRefusesInsidePolarDays(weather);
            }
            finally
            {
                Object.DestroyImmediate(world);
            }
        }

        /// <summary>
        /// One latitude on one day: each of the four jumps must land where its label
        /// says. Split out of the sweep so the sweep reads as the grid it is.
        /// </summary>
        private static void CheckOneDaysCrossings(
            UniverseCreator.TerrainSystem.UCWeatherSystemManager weather,
            ClockState before, int day, float latitudeDeg)
        {
            string where = " (latitude " + latitudeDeg.ToString("0.0")
                           + ", day " + day + ")";

            // The promise the inspector makes: the sunrise jump puts the sun on the
            // horizon. Wrapping is expected and safe - a large longitude offset can
            // put sunrise before local midnight, and SetTime folds that back into the
            // day at the same sun height because the hour angle is periodic in a day.
            weather.SetTime(day, before.SunriseHour);
            ClockState atSunrise = weather.ResolveClock();

            Check(Mathf.Abs(atSunrise.elevationDeg) <= 1f,
                  "the sunrise jump must land the sun within 1 deg of the horizon, got "
                  + atSunrise.elevationDeg.ToString("0.00") + " deg" + where);

            weather.SetTime(day, before.SunsetHour);
            ClockState atSunset = weather.ResolveClock();

            Check(Mathf.Abs(atSunset.elevationDeg) <= 1f,
                  "the sunset jump must land the sun within 1 deg of the horizon, got "
                  + atSunset.elevationDeg.ToString("0.00") + " deg" + where);

            // Solar noon must be the highest the sun gets that day or the label is a
            // lie. Compared against both crossings and against midnight, the only
            // other computed jump, so a swapped pair of hours cannot pass.
            weather.SetTime(day, before.solarNoonHour);
            ClockState atNoon = weather.ResolveClock();

            Check(atNoon.elevationDeg >= atSunrise.elevationDeg - 1e-3f
                  && atNoon.elevationDeg >= atSunset.elevationDeg - 1e-3f,
                  "solar noon must put the sun higher than either crossing" + where);

            Check(atNoon.SunIsUp,
                  "the sun must be up at solar noon on a day it rises" + where);

            weather.SetTime(day, ClockState.MidnightHour);
            ClockState atMidnight = weather.ResolveClock();

            CheckWithin(atMidnight.localHour, 0f, 1e-3f,
                        "the midnight jump must land the clock at 00:00" + where);

            Check(atMidnight.elevationDeg <= atNoon.elevationDeg,
                  "midnight cannot be brighter than solar noon" + where);
        }

        /// <summary>
        /// A world far east of its standard meridian, where the computed sunrise hour
        /// goes negative. Asserted on its own rather than left to the sweep, because
        /// this is the exact case a clamp into 0..24 would break while every mid
        /// meridian world kept working.
        /// </summary>
        private static void CheckScrubFarFromMeridian(
            UniverseCreator.TerrainSystem.UCWeatherSystemManager weather)
        {
            weather.latitudeDeg = 45f;
            weather.longitudeDeg = 170f;
            weather.standardMeridianDeg = 0f;

            weather.SetTime(172, ClockState.MidnightHour);
            ClockState farEast = weather.ResolveClock();

            Check(farEast.SunriseHour < 0f,
                  "170 deg east of its standard meridian must put sunrise before local "
                  + "midnight, got " + farEast.SunriseHour.ToString("0.00"));

            weather.SetTime(172, farEast.SunriseHour);
            ClockState wrapped = weather.ResolveClock();

            Check(Mathf.Abs(wrapped.elevationDeg) <= 1f,
                  "a wrapped negative sunrise hour must still land the sun on the "
                  + "horizon, got " + wrapped.elevationDeg.ToString("0.00") + " deg");

            weather.longitudeDeg = 0f;
        }

        /// <summary>
        /// Both polar extremes must report that there is no crossing today. A guard
        /// that only caught the dark half would offer a sunrise in the middle of a
        /// polar summer, and the inspector disables its two crossing buttons off
        /// exactly this flag. Solar noon and midnight must stay meaningful on both
        /// days, which is why those two buttons are left enabled there.
        /// </summary>
        private static void CheckScrubRefusesInsidePolarDays(
            UniverseCreator.TerrainSystem.UCWeatherSystemManager weather)
        {
            weather.latitudeDeg = 80f;

            weather.SetTime(355, ClockState.MidnightHour);
            ClockState polarNight = weather.ResolveClock();

            Check(polarNight.IsPolarNight && !polarNight.HasSunriseToday,
                  "latitude 80 at midwinter must report a polar night and no sunrise, "
                  + "day length " + polarNight.dayLengthHours.ToString("0.0"));

            Check(!polarNight.SunIsUp,
                  "the sun must be below the horizon at midnight in a polar night");

            weather.SetTime(172, ClockState.MidnightHour);
            ClockState polarDay = weather.ResolveClock();

            Check(polarDay.IsPolarDay && !polarDay.HasSunriseToday,
                  "latitude 80 at midsummer must report a polar day and no sunrise, "
                  + "day length " + polarDay.dayLengthHours.ToString("0.0"));

            Check(polarDay.SunIsUp,
                  "the sun must be above the horizon at midnight under a midnight sun");

            weather.SetTime(355, polarNight.solarNoonHour);
            ClockState polarNoon = weather.ResolveClock();

            Check(polarNoon.elevationDeg > polarNight.elevationDeg,
                  "solar noon inside a polar night must still be the brightest moment "
                  + "of that day");
        }

        /// <summary>
        /// A clock carrying only the three numbers the crossing arithmetic reads, so
        /// the symmetry of sunrise and sunset can be checked without a scene. Every
        /// other field is zero and none of them is consulted by what that proves.
        /// </summary>
        private static ClockState MakeClockForCrossings(float solarNoonHour,
                                                        float dayLengthHours,
                                                        float latitudeDeg)
        {
            return new ClockState(
                1, 172, 0f, 0f,
                latitudeDeg, 0f,
                0f, 0f, 0f, 0f,
                solarNoonHour, dayLengthHours, 0f,
                0f, 0f, 0f, 0f, 0f,
                0f, 0f, 1f);
        }

        // =====================================================================
        //  MOIST-AIR THERMODYNAMICS
        //
        //  Every value below is a textbook number for air at 20 degC and half
        //  humidity, which is the one condition anyone can check by hand. The point
        //  is not that the arithmetic runs - it is that a transposed Tetens exponent
        //  or a degrees-for-radians slip in Stull's fit shows up as a wet bulb above
        //  the air temperature, which is thermodynamically impossible and would
        //  otherwise reach the player as snow falling in summer.
        // =====================================================================

        private static void TestAtmosphericThermodynamics()
        {
            CheckWithin(AtmoMath.SaturationVapourPressureKPa(20f), 2.34f, 0.05f,
                        "saturation vapour pressure at 20 degC must be near 2.34 kPa");

            CheckWithin(AtmoMath.VapourPressureKPa(20f, 0.5f), 1.17f, 0.03f,
                        "half-saturated air at 20 degC must carry about 1.17 kPa");

            float dewPoint = AtmoMath.DewPointC(20f, 0.5f);

            CheckWithin(dewPoint, 9.3f, 0.5f,
                        "the dew point at 20 degC and 50 percent humidity must be near 9.3 degC");

            // Round trip: the inverse must return the humidity it was given, or one
            // of the two curves is not the other's inverse.
            CheckWithin(AtmoMath.RelativeHumidity01(20f, dewPoint), 0.5f, 1e-2f,
                        "relative humidity from a dew point must invert the dew point");

            CheckWithin(AtmoMath.LiftingCondensationLevelM(20f, dewPoint), 1340f, 100f,
                        "the cloud base for that spread must sit near 1340 m");

            Check(AtmoMath.LiftingCondensationLevelM(12f, 15f) == 0f,
                  "already-saturated air must put the cloud base on the ground, not below it");

            CheckWithin(AtmoMath.WetBulbC(20f, 0.5f), 13.7f, 0.5f,
                        "the wet bulb at 20 degC and 50 percent humidity must be near 13.7 degC");

            CheckWithin(AtmoMath.PressureHpa(AtmoConstants.SeaLevelPressureHpa, 5000f),
                        540f, 10f,
                        "pressure at 5000 m must fall to about 540 hPa");

            CheckWithin(AtmoMath.AirDensityKgPerM3(20f, AtmoConstants.SeaLevelPressureHpa),
                        1.204f, 0.01f,
                        "sea-level air at 20 degC must weigh about 1.204 kg/m3");

            // Potential temperature is defined as the temperature air would have at
            // the reference level, so at the reference level it must change nothing.
            // An exponent applied to the wrong ratio still returns a plausible
            // number everywhere else, and only this case catches it.
            float refK = 293.15f;
            CheckWithin(AtmoMath.PotentialTemperatureK(refK,
                            AtmoConstants.PotentialTemperatureReferenceHpa),
                        refK, 1e-2f,
                        "potential temperature at the reference pressure must be an identity");

            Check(AtmoMath.PotentialTemperatureK(refK, 500f) > refK,
                  "lifting air to half pressure must raise its potential temperature");

            // Sweep the whole plausible envelope for the two impossibilities that
            // would reach a player: a wet bulb above the air temperature, and a dew
            // point above it. Both are what a diverged fit produces.
            for (int t = -30; t <= 55; t += 5)
            {
                for (int rh = 0; rh <= 100; rh += 10)
                {
                    float tempC = t;
                    float rh01 = rh * 0.01f;

                    float wet = AtmoMath.WetBulbC(tempC, rh01);
                    float dew = AtmoMath.DewPointC(tempC, rh01);

                    Check(!float.IsNaN(wet) && !float.IsInfinity(wet),
                          "the wet bulb must stay finite at " + t + " degC / " + rh + " percent");
                    Check(!float.IsNaN(dew) && !float.IsInfinity(dew),
                          "the dew point must stay finite at " + t + " degC / " + rh + " percent");

                    // Inside the fit's valid band the ordering is physical law. Outside
                    // it the inputs are clamped, so the answer belongs to the boundary
                    // temperature and may legitimately sit above this one.
                    //
                    // The allowance widens below -10 degC on purpose. Stull's fit is a
                    // polynomial, not a solver, and its published error is worst at the
                    // cold dry corner - it reads about 0.9 degC above the dry bulb at
                    // -20 degC and 10 percent humidity. That is a known property of the
                    // chosen fit, so the test pins it to a bound instead of pretending
                    // it does not happen; a real sign or radian fault breaks the warm
                    // half of the band by tens of degrees and is caught either way.
                    if (tempC >= AtmoConstants.WetBulbValidMinC &&
                        tempC <= AtmoConstants.WetBulbValidMaxC && rh > 0)
                    {
                        float allowance = tempC >= -10f ? 0.5f : 1.5f;

                        Check(wet <= tempC + allowance,
                              "the wet bulb must not exceed the air temperature at "
                              + t + " degC / " + rh + " percent, got " + wet);
                        Check(dew <= tempC + 0.01f,
                              "the dew point must not exceed the air temperature at "
                              + t + " degC / " + rh + " percent, got " + dew);
                    }

                    if (rh >= 100)
                        CheckWithin(dew, tempC, 0.05f,
                                    "saturated air must have its dew point at the air temperature");
                }
            }

            Check(AtmoMath.PressureHpa(AtmoConstants.SeaLevelPressureHpa, 0f)
                  == AtmoConstants.SeaLevelPressureHpa,
                  "pressure at sea level must be the sea-level pressure exactly");

            Check(AtmoMath.PressureHpa(AtmoConstants.SeaLevelPressureHpa, 1e6f) >= 0f,
                  "an absurd altitude must clamp to zero pressure, never go complex");

            Check(AtmoMath.AirDensityKgPerM3(-AtmoConstants.KelvinAtZeroCelsius - 10f,
                                            AtmoConstants.SeaLevelPressureHpa) == 0f,
                  "below absolute zero the density must be refused, not negative");

            CheckAtmoForwardersAgree();
        }

        // =====================================================================
        //  ONE OWNER PER FORMULA
        //
        //  Six climate laws moved from EcoMath into AtmoMath, and EcoMath now
        //  forwards. Forwarding is only worth anything while it is actually
        //  forwarding: the day someone "fixes" one of the ecology methods by
        //  pasting a body back into it, both layers still compile and both still
        //  look right, and the two answers drift apart silently for a release.
        //  These checks are what makes that loud.
        // =====================================================================

        private static void CheckAtmoForwardersAgree()
        {
            CheckClose(EcoMath.LapseAdjustedTempC(15f, 2000f),
                       AtmoMath.LapseAdjustedTempC(15f, 2000f),
                       "the lapse forwarder must agree with its owner");

            CheckClose(EcoMath.LapseAdjustedTempC(15f, 2000f, 5.5f),
                       AtmoMath.LapseAdjustedTempC(15f, 2000f, 5.5f),
                       "the authored-rate lapse forwarder must agree with its owner");

            CheckClose(EcoMath.HargreavesPetMmPerDay(12f, 28f, 20f, 30f),
                       AtmoMath.HargreavesPetMmPerDay(12f, 28f, 20f, 30f),
                       "the PET forwarder must agree with its owner");

            CheckClose(EcoMath.AridityIndex(800f, 1200f),
                       AtmoMath.AridityIndex(800f, 1200f),
                       "the aridity forwarder must agree with its owner");

            CheckClose(EcoMath.GrowingDegreeDays(18f, 5f, 30f),
                       AtmoMath.GrowingDegreeDays(18f, 5f, 30f),
                       "the growing-degree-day forwarder must agree with its owner");

            CheckClose(EcoMath.FrostFreeFraction(8f, 12f),
                       AtmoMath.FrostFreeFraction(8f, 12f),
                       "the frost-free forwarder must agree with its owner");

            CheckClose(EcoMath.FrostFreeFractionFromExtremes(-4f, 20f),
                       AtmoMath.FrostFreeFractionFromExtremes(-4f, 20f),
                       "the frost-free-from-extremes forwarder must agree with its owner");

            // The constants forward too, and a forwarded constant that stops
            // forwarding is the same failure one level down.
            Check(EcoConstants.HargreavesCoefficient == AtmoConstants.HargreavesCoefficient,
                  "the Hargreaves coefficient must have one owner");
            Check(EcoConstants.HargreavesTempOffsetC == AtmoConstants.HargreavesTempOffsetC,
                  "the Hargreaves temperature offset must have one owner");
            Check(EcoConstants.LatentHeatVaporisationMjPerKg
                  == AtmoConstants.LatentHeatVaporisationMjPerKg,
                  "the latent heat of vaporisation must have one owner");
            Check(EcoConstants.AridityMinPetMmPerYear == AtmoConstants.AridityMinPetMmPerYear,
                  "the aridity PET floor must have one owner");
            Check(EcoConstants.AridityMaxIndex == AtmoConstants.AridityMaxIndex,
                  "the aridity ceiling must have one owner");
            Check(EcoConstants.FreezingPointC == AtmoConstants.WaterFreezingPointC,
                  "the freezing point must have one owner");
            Check(EcoConstants.LapseRateCPerKm == AtmoConstants.LapseRateCPerKm,
                  "the lapse rate must have one owner");

            // Behaviour of the moved bodies, checked once here rather than in the
            // ecology suite, because this is where they live now.
            CheckWithin(AtmoMath.LapseAdjustedTempC(15f, 1000f), 8.5f, 0.2f,
                        "a kilometre of altitude must cost about 6.5 degC");

            CheckWithin(AtmoMath.AridityIndex(0f, 1000f), 0f, 1e-4f,
                        "no rain must read as absolute desert, not as a divide by zero");
            CheckWithin(AtmoMath.AridityIndex(1000f, 0f), AtmoConstants.AridityMaxIndex, 1e-3f,
                        "no evaporative demand must saturate at the ceiling, not run to infinity");

            Check(AtmoMath.GrowingDegreeDays(3f, 5f, 30f) == 0f,
                  "a month below the base temperature must accumulate no warmth");

            CheckWithin(AtmoMath.FrostFreeFraction(0f, 10f), 0.5f, 1e-3f,
                        "a year averaging freezing must be frost free for exactly half of itself");
            Check(AtmoMath.FrostFreeFraction(20f, 0f) == 1f,
                  "a seasonless warm world must be frost free all year");
            Check(AtmoMath.FrostFreeFraction(-20f, 0f) == 0f,
                  "a seasonless frozen world must never thaw");
        }

        // =====================================================================
        //  CLIMATE NORMALS
        //
        //  These are the checks that would have caught the two mistakes actually
        //  made while building the normals. The first: a straight-line latitude
        //  profile, which compiles and looks plausible and puts 45 degrees at
        //  minus four Celsius. The second: shaping the year's rain by scaling each
        //  month independently, which quietly changes the annual total that the
        //  precipitation field already decided. Both are silent at runtime and
        //  both are one assertion away from being loud.
        // =====================================================================

        private static void TestClimateNormals()
        {
            TestZonalTemperatureProfile();
            TestContinentality();
            TestClimateNormalsYear();
            TestPrecipitationRegimes();
            TestClimateNormalsCost();
        }

        /// <summary>
        /// The zonal profile against the three latitudes the specification names.
        /// Real annual means are about 26, 11 and -8 degC; a few degrees is the
        /// honest tolerance for a two-term profile, and the point of the test is
        /// that the tropics come out flat and the mid-latitudes steep rather than
        /// that any one number is exact.
        /// </summary>
        private static void TestZonalTemperatureProfile()
        {
            CheckWithin(ClimateNormals.ZonalMeanSeaLevelTempC(0f), 26f, 3f,
                        "the equator must average near 26 degC at sea level");
            CheckWithin(ClimateNormals.ZonalMeanSeaLevelTempC(45f), 11f, 3f,
                        "45 degrees must average near 11 degC, not near freezing");
            CheckWithin(ClimateNormals.ZonalMeanSeaLevelTempC(70f), -8f, 3f,
                        "70 degrees must average near -8 degC");

            Check(ClimateNormals.ZonalMeanSeaLevelTempC(-45f)
                  == ClimateNormals.ZonalMeanSeaLevelTempC(45f),
                  "the zonal profile must be symmetric about the equator");

            // Monotonic poleward. A profile that warms up again somewhere would
            // put a warm band inside the ice cap.
            float previous = ClimateNormals.ZonalMeanSeaLevelTempC(0f);
            for (int lat = 5; lat <= 90; lat += 5)
            {
                float here = ClimateNormals.ZonalMeanSeaLevelTempC(lat);
                Check(here <= previous + EcoEpsilon,
                      "the zonal profile must never warm as it goes poleward");
                previous = here;
            }

            // The latitude-altitude equivalence the vegetation layer relies on:
            // climbing a kilometre must cost the same warmth as moving poleward by
            // the distance the meridional gradient prices that kilometre at. It
            // only holds in the mid-latitudes, which is where it is asserted.
            float latitudeStepDeg = AtmoConstants.LapseRateCPerKm
                                    / AtmoConstants.LatitudeGradientCPerDeg;

            float byLatitude = ClimateNormals.ZonalMeanSeaLevelTempC(45f)
                             - ClimateNormals.ZonalMeanSeaLevelTempC(45f + latitudeStepDeg);

            float byAltitude = AtmoConstants.LapseRateCPerKm;

            CheckWithin(byLatitude, byAltitude, 1.5f,
                        "a kilometre of climb and its equivalent in latitude must cost the same degrees");
        }

        /// <summary>
        /// Continentality, which is the whole of W-12: an island world and a
        /// continental interior have to come out of the same code from nothing but
        /// their distance to water.
        /// </summary>
        private static void TestContinentality()
        {
            Check(ClimateNormals.ConradIndexFromCoastDistance(0f) == 0f,
                  "a shoreline must have no continentality at all");

            float island = ClimateNormals.ConradIndexFromCoastDistance(0f);
            float nearCoast = ClimateNormals.ConradIndexFromCoastDistance(20000f);
            float oneScale = ClimateNormals.ConradIndexFromCoastDistance(
                                 AtmoConstants.MarineModerationScaleM);
            float deepInterior = ClimateNormals.ConradIndexFromCoastDistance(2000000f);

            Check(nearCoast > island && oneScale > nearCoast && deepInterior > oneScale,
                  "the Conrad index must rise monotonically with distance inland");
            Check(deepInterior <= AtmoConstants.ConradMaxIndex,
                  "the Conrad index must saturate at its ceiling, not run away inland");

            // One e-folding is 63 % of the way to the ceiling by construction.
            CheckWithin(oneScale, AtmoConstants.ConradMaxIndex * 0.632f, 1f,
                        "one marine-moderation scale length must reach 63 percent of the ceiling");

            // The two headline figures. Same latitude, same function, only the
            // distance to water differs.
            float islandRangeC = ClimateNormals.AnnualRangeFromConradIndex(island, 45f);
            float interiorRangeC = ClimateNormals.AnnualRangeFromConradIndex(deepInterior, 45f);

            Check(islandRangeC < 10f,
                  "an island at 45 degrees must have an annual range under about 10 degC");
            Check(interiorRangeC > 30f,
                  "a continental interior at 45 degrees must have an annual range over about 30 degC");

            // Conrad forward against Conrad inverted. An inverse nobody checks is
            // an inverse that drifts, and this pair is the one place a sign error
            // would produce a plausible-looking wrong amplitude everywhere.
            for (int lat = 0; lat <= 80; lat += 10)
            {
                for (float k = 0f; k <= AtmoConstants.ConradMaxIndex; k += 15f)
                {
                    float rangeC = ClimateNormals.AnnualRangeFromConradIndex(k, lat);
                    float roundTrip = ClimateNormals.ConradIndexFromAnnualRange(rangeC, lat);

                    CheckWithin(roundTrip, k, 1e-2f,
                                "Conrad inverted then applied forward must return the index it started from");
                }
            }

            // The equator is where the forward form divides by the latitude weight,
            // so it is where a missing offset would divide by zero.
            float equatorRange = ClimateNormals.AnnualRangeFromConradIndex(30f, 0f);
            Check(!float.IsNaN(equatorRange) && !float.IsInfinity(equatorRange) && equatorRange > 0f,
                  "the annual range at the equator must be finite and positive");
        }

        /// <summary>
        /// A built year, end to end: the shape of the seasons, the radiation that
        /// drives them, and the evaporative demand that comes out the far side.
        /// </summary>
        private static void TestClimateNormalsYear()
        {
            // ---- the shape of the year ------------------------------------------
            ClimateRequest temperate = ClimateRequest.Default(45f, 0f, 800f,
                                                              EcoConstants.GddBaseTemperateC);
            temperate.distanceToCoastM = 400000f;

            ClimateNormals north = ClimateNormals.Build(temperate);

            CheckWithin(north.meanAnnualTempC,
                        ClimateNormals.ZonalMeanSeaLevelTempC(45f), 1f,
                        "the twelve monthly means must average back to the annual mean");

            Check(north.warmestMonthTempC > north.coldestMonthTempC,
                  "the warmest month must be warmer than the coldest one");
            Check(north.AnnualRangeC > 0f,
                  "a mid-latitude continental year must have a seasonal range");

            // Northern high summer is month 6, and the lag is why it is July rather
            // than the June solstice. A hemisphere sign error puts it in January and
            // nothing else in the build would notice.
            int warmestMonth = 0;
            int coldestMonth = 0;
            for (int month = 1; month < AtmoConstants.MonthsPerYear; month++)
            {
                if (north.monthlyMeanTempC[month] > north.monthlyMeanTempC[warmestMonth])
                    warmestMonth = month;
                if (north.monthlyMeanTempC[month] < north.monthlyMeanTempC[coldestMonth])
                    coldestMonth = month;
            }

            Check(warmestMonth >= 5 && warmestMonth <= 7,
                  "the northern warmest month must fall in the northern summer");
            Check(coldestMonth <= 1 || coldestMonth >= 11,
                  "the northern coldest month must fall in the northern winter");

            // The same geography below the equator. Not a separate code path - the
            // sign of the latitude is the only input that changes.
            ClimateRequest southernRequest = temperate;
            southernRequest.latitudeDeg = -45f;

            ClimateNormals south = ClimateNormals.Build(southernRequest);

            Check(south.IsSouthernHemisphere && !north.IsSouthernHemisphere,
                  "the hemisphere must be read from the sign of the latitude");
            CheckWithin(south.meanAnnualTempC, north.meanAnnualTempC, EcoEpsilon,
                        "mirrored latitudes must share an annual mean");
            Check(south.monthlyMeanTempC[6] < south.monthlyMeanTempC[0],
                  "July must be the cold half of a southern year");
            CheckWithin(south.monthlyMeanTempC[6], north.monthlyMeanTempC[0], 0.5f,
                        "a southern July must match a northern January at the mirrored latitude");

            // ---- radiation, and the demand it drives ----------------------------
            ClimateRequest equatorRequest = ClimateRequest.Default(0f, 0f, 2000f,
                                                EcoConstants.GddBaseTemperateC);
            equatorRequest.distanceToCoastM = 0f;

            ClimateNormals equator = ClimateNormals.Build(equatorRequest);

            // The equinox months bracket the spec's 36 MJ figure at the equator.
            CheckWithin(equator.monthlyRadiationMj[2], 36f, 3f,
                        "equatorial radiation at the March equinox must be near 36 MJ/m2/day");
            CheckWithin(equator.monthlyRadiationMj[8], 36f, 3f,
                        "equatorial radiation at the September equinox must be near 36 MJ/m2/day");

            ClimateRequest polarRequest = ClimateRequest.Default(60f, 0f, 600f,
                                              EcoConstants.GddBaseTemperateC);
            ClimateNormals polar = ClimateNormals.Build(polarRequest);

            Check(polar.monthlyRadiationMj[11] < 5f,
                  "December radiation at 60 degrees north must fall almost to nothing");
            Check(polar.monthlyRadiationMj[6] > polar.monthlyRadiationMj[11] * 4f,
                  "the polar summer must receive many times the polar winter");

            // Surface radiation is the top-of-atmosphere value after cloud, so it is
            // always the smaller of the two. Keeping them apart is what stops a
            // cloudy world evaporating like a clear one.
            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                Check(polar.monthlySurfaceRadiationMj[month] <= polar.monthlyRadiationMj[month] + EcoEpsilon,
                      "surface radiation can never exceed what arrived at the top of the atmosphere");
                Check(polar.monthlyRadiationMj[month] >= 0f,
                      "radiation must never go negative, even in the polar night");
            }

            // Hot desert against cold coast, the two ends of the PET range.
            ClimateRequest desert = ClimateRequest.Default(25f, 0f, 50f,
                                        EcoConstants.GddBaseTemperateC);
            desert.distanceToCoastM = 1000000f;
            desert.cloudCover01 = 0.05f;
            desert.humidity01 = 0.15f;

            ClimateNormals desertNormals = ClimateNormals.Build(desert);

            Check(desertNormals.annualPetMm > 1500f,
                  "a hot desert must demand more than 1500 mm of evaporation a year");
            Check(desertNormals.aridity < AtmoConstants.DesertAridityThreshold,
                  "50 mm of rain against that demand must classify as desert");

            ClimateRequest coldCoast = ClimateRequest.Default(60f, 0f, 900f,
                                           EcoConstants.GddBaseTemperateC);
            coldCoast.distanceToCoastM = 0f;
            coldCoast.cloudCover01 = 0.8f;
            coldCoast.humidity01 = 0.85f;

            ClimateNormals coldCoastNormals = ClimateNormals.Build(coldCoast);

            Check(coldCoastNormals.annualPetMm < 500f,
                  "a cold cloudy coast must demand less than 500 mm a year");
            Check(coldCoastNormals.aridity > AtmoConstants.DesertAridityThreshold,
                  "900 mm against that small demand must not read as a desert");

            // ---- continuity across a chunk edge ---------------------------------
            // Two chunks meeting at a seam sample positions a metre apart. Because
            // the normals are a pure function of the position, that has to produce
            // the same year on both sides - anything stateful in the build shows up
            // here as a visible line in the vegetation along every chunk boundary.
            ClimateRequest westEdge = ClimateRequest.Default(45f, 500f, 800f,
                                          EcoConstants.GddBaseTemperateC);
            westEdge.distanceToCoastM = 300000f;

            ClimateRequest eastEdge = westEdge;
            eastEdge.latitudeDeg = 45.00001f;
            eastEdge.altitudeM = 500.01f;
            eastEdge.distanceToCoastM = 300001f;

            ClimateNormals west = ClimateNormals.Build(westEdge);
            ClimateNormals east = ClimateNormals.Build(eastEdge);

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                CheckWithin(east.monthlyMeanTempC[month], west.monthlyMeanTempC[month], 0.01f,
                            "monthly means must be continuous across a chunk seam");
                CheckWithin(east.monthlyPrecipMm[month], west.monthlyPrecipMm[month], 0.01f,
                            "monthly precipitation must be continuous across a chunk seam");
            }

            CheckWithin(east.annualPetMm, west.annualPetMm, 0.5f,
                        "annual PET must be continuous across a chunk seam");
            Check(east.regime == west.regime,
                  "the regime must not flip between two positions a metre apart");

            // Rebuild has to overwrite the whole year. If it left a single month
            // behind, a pooled instance would carry one month of the previous
            // chunk's climate into this one - and that is exactly the kind of bug
            // that only appears once the pool starts being reused under load.
            ClimateNormals reused = ClimateNormals.Build(desert);
            reused.Rebuild(coldCoast);

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                CheckWithin(reused.monthlyMeanTempC[month],
                            coldCoastNormals.monthlyMeanTempC[month], EcoEpsilon,
                            "a rebuilt instance must match a freshly built one, month for month");
                CheckWithin(reused.monthlyPrecipMm[month],
                            coldCoastNormals.monthlyPrecipMm[month], EcoEpsilon,
                            "a rebuilt instance must not keep a month of the previous position's rain");
            }

            CheckWithin(reused.annualPetMm, coldCoastNormals.annualPetMm, EcoEpsilon,
                        "a rebuilt instance must not accumulate the previous position's PET");
            CheckWithin(reused.gddAnnual, coldCoastNormals.gddAnnual, EcoEpsilon,
                        "a rebuilt instance must not accumulate the previous position's degree days");
            Check(reused.regime == coldCoastNormals.regime,
                  "a rebuilt instance must adopt the new position's regime");

            // ---- the aggregates the ecology layer reads --------------------------
            Check(north.frostFreeFraction >= 0f && north.frostFreeFraction <= 1f,
                  "the frost-free fraction must be a fraction");
            Check(desertNormals.frostFreeFraction > north.frostFreeFraction,
                  "a subtropical desert must be frost free for more of the year than a continental mid-latitude");
            Check(desertNormals.gddAnnual > north.gddAnnual,
                  "a hot year must accumulate more degree days than a cool one");
            Check(coldCoastNormals.gddAnnual < north.gddAnnual,
                  "a cold coast must accumulate fewer degree days than a warm continental site");
            // The reported continentality is Conrad run forward on the range the
            // twelve months actually realised, so it lands just short of the index
            // the coast distance asked for - the monthly midpoints do not sample the
            // exact peak of the harmonic. A degree of slack covers that sampling gap
            // without covering a sign error, which would be out by tens.
            CheckWithin(north.continentality,
                        ClimateNormals.ConradIndexFromCoastDistance(400000f), 1f,
                        "the reported continentality must be the index the coast distance implied");
        }

        /// <summary>
        /// Precipitation regimes. Two things are being defended here. First, that
        /// the regime a place gets is decided by its geography and nobody has to
        /// select it. Second, and this is the one that would have been a real bug:
        /// that shaping the year never changes the year's total, because the annual
        /// figure arrives from the precipitation field and this code is only allowed
        /// to redistribute it.
        /// </summary>
        private static void TestPrecipitationRegimes()
        {
            // Derivation from geography alone, band by band.
            Check(ClimateNormals.DeriveRegime(5f, 20f, 1.2f) == PrecipitationRegime.Equatorial,
                  "the deep tropics must derive the equatorial double peak");
            Check(ClimateNormals.DeriveRegime(20f, 40f, 0.9f) == PrecipitationRegime.Monsoon,
                  "the outer tropics must derive a monsoon");
            Check(ClimateNormals.DeriveRegime(38f, 18f, 0.6f) == PrecipitationRegime.Mediterranean,
                  "a maritime site under the subtropical high must derive a Mediterranean year");
            Check(ClimateNormals.DeriveRegime(38f, 50f, 0.6f) == PrecipitationRegime.Continental,
                  "the same latitude far inland must derive a continental year instead");
            Check(ClimateNormals.DeriveRegime(50f, 10f, 0.8f) == PrecipitationRegime.TemperateOceanic,
                  "a maritime temperate site must derive rain in every month");
            Check(ClimateNormals.DeriveRegime(70f, 50f, 1.5f) == PrecipitationRegime.PolarOrDesert,
                  "high latitudes must derive the flat regime");
            Check(ClimateNormals.DeriveRegime(20f, 60f, 0.05f) == PrecipitationRegime.PolarOrDesert,
                  "aridity must beat latitude: a desert at a monsoon latitude is still a desert");

            // A Mediterranean latitude, with nobody selecting anything.
            ClimateRequest mediterranean = ClimateRequest.Default(38f, 0f, 700f,
                                               EcoConstants.GddBaseTemperateC);
            mediterranean.distanceToCoastM = 50000f;
            mediterranean.cloudCover01 = 0.4f;
            mediterranean.humidity01 = 0.6f;

            ClimateNormals med = ClimateNormals.Build(mediterranean);

            Check(med.regime == PrecipitationRegime.Mediterranean,
                  "a Mediterranean latitude near the coast must choose that regime by itself");

            int july = 6;
            int january = 0;

            Check(med.monthlyPrecipMm[july] < med.monthlyPrecipMm[january],
                  "a Mediterranean year must be drier in July than in January");
            Check(med.monthlyPrecipMm[july] < med.annualPrecipMm
                                             / AtmoConstants.MonthsPerYear * 0.25f,
                  "the Mediterranean summer must be a drought, not merely a dip");

            // The seasonal aridity index. This is the number that makes the summer
            // drought visible to the vegetation layer: the year as a whole is humid
            // and August on its own is a desert.
            Check(med.aridity > AtmoConstants.DesertAridityThreshold,
                  "the Mediterranean year as a whole must not read as a desert");
            Check(med.MonthlyAridity(july) < AtmoConstants.DesertAridityThreshold,
                  "the Mediterranean summer month must read as a desert on its own");
            Check(med.MonthlyAridity(january) > med.MonthlyAridity(july),
                  "seasonal aridity must vary across the year, not sit at the annual value");
            Check(med.DriestMonthIndex() >= 4 && med.DriestMonthIndex() <= 8,
                  "the driest month of a Mediterranean year must fall in its summer");

            // The conservation law. Every regime, both hemispheres: the twelve months
            // have to add back to the total the precipitation field handed in. This
            // is the check that would have caught scaling each month on its own.
            for (int r = 0; r <= (int)PrecipitationRegime.PolarOrDesert; r++)
            {
                for (int hemisphere = 0; hemisphere < 2; hemisphere++)
                {
                    ClimateRequest authored = ClimateRequest.Default(
                                                  hemisphere == 0 ? 30f : -30f,
                                                  0f, 1200f,
                                                  EcoConstants.GddBaseTemperateC);
                    authored.regime = (PrecipitationRegime)r;
                    authored.regimeIsAuthored = true;

                    ClimateNormals shaped = ClimateNormals.Build(authored);

                    Check(shaped.regime == (PrecipitationRegime)r,
                          "an authored regime must survive the build unchanged");

                    float total = 0f;
                    for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
                    {
                        Check(shaped.monthlyPrecipMm[month] >= 0f,
                              "no month may be given a negative amount of rain");
                        total += shaped.monthlyPrecipMm[month];
                    }

                    CheckWithin(total, shaped.annualPrecipMm, 0.1f,
                                "the twelve months must add back to the annual total, whatever the regime");
                }
            }

            // A dry year has to stay dry rather than divide by its own total.
            ClimateRequest bone = ClimateRequest.Default(25f, 0f, 0f,
                                     EcoConstants.GddBaseTemperateC);
            ClimateNormals dry = ClimateNormals.Build(bone);

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
                Check(dry.monthlyPrecipMm[month] == 0f,
                      "a rainless year must give every month zero, not a NaN");

            // The equatorial double peak: two maxima a year, because the ITCZ passes
            // twice. One maximum would mean the semiannual harmonic became annual.
            ClimateRequest itcz = ClimateRequest.Default(5f, 0f, 2400f,
                                     EcoConstants.GddBaseTemperateC);
            itcz.regime = PrecipitationRegime.Equatorial;
            itcz.regimeIsAuthored = true;

            ClimateNormals equatorial = ClimateNormals.Build(itcz);

            int maximaCount = 0;
            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                int before = (month + AtmoConstants.MonthsPerYear - 1) % AtmoConstants.MonthsPerYear;
                int after = (month + 1) % AtmoConstants.MonthsPerYear;

                if (equatorial.monthlyPrecipMm[month] > equatorial.monthlyPrecipMm[before] &&
                    equatorial.monthlyPrecipMm[month] > equatorial.monthlyPrecipMm[after])
                    maximaCount++;
            }

            Check(maximaCount == 2,
                  "an equatorial year must have exactly two rainfall maxima, one per ITCZ passage");

            // A monsoon's dry season is genuinely dry, not merely quieter. That is
            // what an amplitude above one buys, and it is why the weights are
            // floored at zero before they are normalised.
            ClimateRequest monsoonRequest = ClimateRequest.Default(15f, 0f, 1800f,
                                               EcoConstants.GddBaseTemperateC);
            monsoonRequest.regime = PrecipitationRegime.Monsoon;
            monsoonRequest.regimeIsAuthored = true;

            ClimateNormals monsoon = ClimateNormals.Build(monsoonRequest);

            int boneDryMonths = 0;
            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
                if (monsoon.monthlyPrecipMm[month] == 0f) boneDryMonths++;

            Check(boneDryMonths >= 2,
                  "a monsoon must have a genuinely rainless season, not just a drier one");
            Check(monsoon.monthlyPrecipMm[6] > monsoon.monthlyPrecipMm[0],
                  "a northern monsoon must put its rain in the northern summer");

            // The same regime below the equator has to move its wet season, not keep
            // northern months. A hemisphere sign dropped here is invisible until
            // somebody looks at a southern continent.
            ClimateRequest southernMonsoon = monsoonRequest;
            southernMonsoon.latitudeDeg = -15f;

            ClimateNormals monsoonSouth = ClimateNormals.Build(southernMonsoon);

            Check(monsoonSouth.monthlyPrecipMm[0] > monsoonSouth.monthlyPrecipMm[6],
                  "a southern monsoon must put its rain in January, not July");

            // Close but not identical, and the gap is honest: the twelve month
            // midpoints are not perfectly symmetric about the seasonal lag, so the
            // clamped dry season removes a slightly different amount of area in each
            // hemisphere and the normalisation divides by a slightly different sum.
            // A few millimetres out of three hundred is that; a dropped hemisphere
            // sign would be three hundred out of three hundred.
            CheckWithin(monsoonSouth.monthlyPrecipMm[0], monsoon.monthlyPrecipMm[6], 5f,
                        "the two hemispheres must be the same shape, half a year apart");
        }

        /// <summary>
        /// The budget, and the reason the class is built per chunk rather than per
        /// candidate. Twelve months of trigonometry is nothing once and a measurable
        /// waste forty thousand times, so the cost is asserted rather than assumed.
        /// </summary>
        private static void TestClimateNormalsCost()
        {
            ClimateRequest request = ClimateRequest.Default(45f, 300f, 900f,
                                         EcoConstants.GddBaseTemperateC);
            request.distanceToCoastM = 250000f;

            ClimateNormals normals = ClimateNormals.Build(request);

            // Hold the array references from before the refill. Rebuild is only worth
            // having if it fills these in place; the moment it reallocates, a pooled
            // instance is just an allocation with extra steps.
            object meanArray = normals.monthlyMeanTempC;
            object precipArray = normals.monthlyPrecipMm;
            object radiationArray = normals.monthlyRadiationMj;

            const int iterations = 2000;

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                request.latitudeDeg = -60f + i * 0.06f;
                normals.Rebuild(request);
            }

            watch.Stop();

            float msPerBuild = (float)watch.Elapsed.TotalMilliseconds / iterations;

            Check(msPerBuild < 1f,
                  "a chunk's climate normals must build in under a millisecond, measured at "
                  + msPerBuild.ToString("F4") + " ms");

            Check(ReferenceEquals(meanArray, normals.monthlyMeanTempC) &&
                  ReferenceEquals(precipArray, normals.monthlyPrecipMm) &&
                  ReferenceEquals(radiationArray, normals.monthlyRadiationMj),
                  "Rebuild must refill the existing arrays rather than allocate new ones");

            // A sweep of the whole habitable envelope. Latitude to the poles, sea
            // level to the summit, dry to saturated - one NaN anywhere in here
            // propagates into PET, then aridity, then which species gets planted,
            // and by then it is a bald patch nobody can trace back.
            for (int lat = -90; lat <= 90; lat += 15)
            {
                for (int altitude = 0; altitude <= 8000; altitude += 2000)
                {
                    ClimateRequest sweep = ClimateRequest.Default(lat, altitude, 1000f,
                                               EcoConstants.GddBaseTemperateC);
                    sweep.distanceToCoastM = altitude * 100f;
                    sweep.cloudCover01 = altitude > 0 ? 1f : 0f;
                    sweep.humidity01 = lat < 0 ? 0f : 1f;

                    ClimateNormals swept = ClimateNormals.Build(sweep);

                    for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
                    {
                        Check(IsFinite(swept.monthlyMeanTempC[month]) &&
                              IsFinite(swept.monthlyMinTempC[month]) &&
                              IsFinite(swept.monthlyMaxTempC[month]),
                              "no monthly temperature may be NaN or infinite anywhere on a world");
                        Check(IsFinite(swept.monthlyPrecipMm[month]) &&
                              swept.monthlyPrecipMm[month] >= 0f,
                              "no monthly precipitation may be NaN, infinite or negative");
                        Check(IsFinite(swept.monthlyRadiationMj[month]) &&
                              swept.monthlyRadiationMj[month] >= 0f,
                              "no monthly radiation may be NaN, infinite or negative");
                        Check(swept.monthlyMinTempC[month] <= swept.monthlyMeanTempC[month] &&
                              swept.monthlyMeanTempC[month] <= swept.monthlyMaxTempC[month],
                              "the daily minimum, mean and maximum must stay in that order");
                    }

                    Check(IsFinite(swept.annualPetMm) && swept.annualPetMm >= 0f,
                          "annual PET must be finite and never negative");
                    Check(IsFinite(swept.aridity) && swept.aridity >= 0f,
                          "the aridity index must be finite and never negative");
                    Check(swept.frostFreeFraction >= 0f && swept.frostFreeFraction <= 1f,
                          "the frost-free fraction must stay a fraction everywhere");
                    Check(IsFinite(swept.gddAnnual) && swept.gddAnnual >= 0f,
                          "degree days must be finite and never negative");
                    Check(IsFinite(swept.continentality),
                          "the reported continentality must be finite even at the poles");
                }
            }
        }

        /// <summary>True for a real number: not NaN, not either infinity.</summary>
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
