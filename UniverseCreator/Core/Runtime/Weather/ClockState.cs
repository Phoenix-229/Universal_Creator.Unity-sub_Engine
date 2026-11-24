// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    ClockState.cs
//  Purpose: One immutable snapshot of where the sun and moon are, and when it is.
//
//  WHY A STRUCT AND NOT A COMPONENT
//  --------------------------------
//  Every consumer in the weather and sky layers - the sun rig, the sky model, the
//  cloud field, phenology, snow melt - needs the same twenty numbers every frame.
//  Passing this by value means none of them can reach back and change the clock
//  they were handed, so a consumer cannot make the world's time depend on the
//  order the consumers happened to run in. That ordering dependency is the exact
//  failure the determinism rule (C-06) exists to prevent: a world that looks
//  different on the second visit because the sky asked what time it was before
//  the weather did.
//
//  WHY THE FIELD NAMES ARE FIXED
//  -----------------------------
//  The first seven fields carry the names the retired module document used for its
//  ClockTime type, so anything already written against those names keeps compiling
//  and keeps meaning the same thing. The rest are appended, never inserted, for
//  the same reason.
//
//  THIS FILE COMPUTES NOTHING.
//  All the astronomy lives in ClockMath, which is a pure static class. This is the
//  result, not the calculation - so there is exactly one place where a solar
//  formula can be wrong, and it is not here.
//
//  NO REFERENCE FIELDS, NO SETTERS, NO UNITY TYPES.
//  A reference field would let two snapshots share mutable state and would put
//  this on the garbage collector's books once per frame. A setter would make it a
//  mutable struct passed by value, which is the classic silent bug: the caller
//  changes a copy and wonders why the world ignored it.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Weather
{
    /// <summary>
    /// An immutable reading of the world clock and the sky geometry it implies.
    /// Produced by <see cref="ClockMath"/>, consumed by value everywhere else.
    /// </summary>
    public readonly struct ClockState
    {
        // ---------------------------------------------------------------------
        //  CALENDAR. The two integers sit together so the struct packs without
        //  padding between them.
        // ---------------------------------------------------------------------

        /// <summary>Year number. Advances when dayOfYear rolls past the last day.</summary>
        public readonly int year;

        /// <summary>Day of the year, 1 to 365. One-based because that is how a calendar reads.</summary>
        public readonly int dayOfYear;

        // ---------------------------------------------------------------------
        //  TIME OF DAY
        // ---------------------------------------------------------------------

        /// <summary>s. Seconds since local midnight, 0 to 86400.</summary>
        public readonly float secondsOfDay;

        /// <summary>h. The same instant as a decimal hour, 0 to 24. Stored rather than derived because every readout and gradient lookup wants it.</summary>
        public readonly float localHour;

        // ---------------------------------------------------------------------
        //  WHERE ON THE WORLD. Carried in the snapshot so a consumer never has to
        //  find the manager to know which hemisphere it is in.
        // ---------------------------------------------------------------------

        /// <summary>deg. Latitude, positive north. Owned by UCWeatherSystemManager (W-04); this is the resolved copy.</summary>
        public readonly float latitudeDeg;

        /// <summary>deg. Longitude, positive east.</summary>
        public readonly float longitudeDeg;

        // ---------------------------------------------------------------------
        //  SUN. Elevation and azimuth are what the sun light actually points
        //  along; the three angles below them are the working that produced it,
        //  kept because the inspector and the self-test both assert on them.
        // ---------------------------------------------------------------------

        /// <summary>deg. Sun altitude above the horizon. Negative at night; below about -6 is past civil twilight.</summary>
        public readonly float elevationDeg;

        /// <summary>deg. Sun compass bearing, 0 = north, sweeping clockwise through a full 360 over the day.</summary>
        public readonly float azimuthDeg;

        /// <summary>deg. Solar declination: the latitude the sun stands over today. The whole mechanism of the seasons. (L-01)</summary>
        public readonly float declinationDeg;

        /// <summary>deg. Solar hour angle, 0 at solar noon, 15 deg per hour, negative before noon.</summary>
        public readonly float hourAngleDeg;

        /// <summary>h. Local clock hour at which the sun is highest. Not 12:00 except by coincidence of longitude and date.</summary>
        public readonly float solarNoonHour;

        /// <summary>h. Hours between sunrise and sunset. Exactly 0 in polar night and exactly 24 under midnight sun. (L-03)</summary>
        public readonly float dayLengthHours;

        /// <summary>min. Equation of time. Positive means the true sun runs ahead of clock time.</summary>
        public readonly float equationOfTimeMin;

        // ---------------------------------------------------------------------
        //  MOON. Night lighting reads all of these (section 8.9): the lit fraction
        //  for how bright, the elevation for whether there is any moonlight at all,
        //  and the bearing for which way the light comes from.
        // ---------------------------------------------------------------------

        /// <summary>Fraction 0 to 1 of the synodic month. 0 is new moon, 0.5 is full. (L-30)</summary>
        public readonly float lunarPhase01;

        /// <summary>deg. Moon altitude above the horizon, on the same convention as the sun's. (W-07)</summary>
        public readonly float lunarElevationDeg;

        /// <summary>
        /// deg. Moon compass bearing, 0 = north, clockwise, on the same convention as
        /// the sun's. (W-07)
        ///
        /// Carried rather than left to the consumer because a moon light needs a
        /// direction as much as the sun light does, and the only way to get one
        /// outside this struct would be a second copy of the hour-angle reduction.
        /// </summary>
        public readonly float lunarAzimuthDeg;

        /// <summary>deg. The latitude the moon stands over. Near the sun's at new moon and opposite it at full, which is why a full moon rides high in winter. (W-07)</summary>
        public readonly float lunarDeclinationDeg;

        /// <summary>Fraction of the lunar disc lit, (1 - cos(2 pi phase)) / 2. This is the number moonlight scales with, not the phase itself.</summary>
        public readonly float lunarIlluminatedFraction;

        // ---------------------------------------------------------------------
        //  DERIVED SEASON AND RADIATION
        // ---------------------------------------------------------------------

        /// <summary>
        /// Season as a fraction, 0 at the vernal equinox, wrapping at 1. A number
        /// and not an enum on purpose (W-06): a fraction inverts correctly across
        /// the equator and compresses toward the tropics on its own, where a named
        /// season would have to be special-cased in both places and would still be
        /// wrong for a wet season that is not warm.
        /// </summary>
        public readonly float season01;

        /// <summary>W/m2. Solar irradiance at the top of the atmosphere now, including the orbital-distance correction.</summary>
        public readonly float solarConstantWm2;

        /// <summary>
        /// Relative air mass along the path to the sun. 1 at the zenith, about 38
        /// at the horizon. Held finite by a Kasten-style guard, because the naive
        /// 1/sin form goes to infinity at sunset and takes the sky colour with it.
        /// </summary>
        public readonly float airMass;

        // =====================================================================
        //  CONSTRUCTION
        //
        //  One constructor, every field, positional. Long, and deliberately so:
        //  a partial constructor would let a caller leave the moon or the season
        //  at zero and ship a world where the moon never rises, which is a defect
        //  no compiler would report.
        // =====================================================================

        public ClockState(
            int year,
            int dayOfYear,
            float secondsOfDay,
            float localHour,
            float latitudeDeg,
            float longitudeDeg,
            float elevationDeg,
            float azimuthDeg,
            float declinationDeg,
            float hourAngleDeg,
            float solarNoonHour,
            float dayLengthHours,
            float equationOfTimeMin,
            float lunarPhase01,
            float lunarElevationDeg,
            float lunarAzimuthDeg,
            float lunarDeclinationDeg,
            float lunarIlluminatedFraction,
            float season01,
            float solarConstantWm2,
            float airMass)
        {
            this.year = year;
            this.dayOfYear = dayOfYear;
            this.secondsOfDay = secondsOfDay;
            this.localHour = localHour;
            this.latitudeDeg = latitudeDeg;
            this.longitudeDeg = longitudeDeg;
            this.elevationDeg = elevationDeg;
            this.azimuthDeg = azimuthDeg;
            this.declinationDeg = declinationDeg;
            this.hourAngleDeg = hourAngleDeg;
            this.solarNoonHour = solarNoonHour;
            this.dayLengthHours = dayLengthHours;
            this.equationOfTimeMin = equationOfTimeMin;
            this.lunarPhase01 = lunarPhase01;
            this.lunarElevationDeg = lunarElevationDeg;
            this.lunarAzimuthDeg = lunarAzimuthDeg;
            this.lunarDeclinationDeg = lunarDeclinationDeg;
            this.lunarIlluminatedFraction = lunarIlluminatedFraction;
            this.season01 = season01;
            this.solarConstantWm2 = solarConstantWm2;
            this.airMass = airMass;
        }

        // =====================================================================
        //  READ-ONLY CONVENIENCES
        //
        //  Get-only, computed from the fields above, no state. These exist so that
        //  the same question is not answered two different ways in two consumers:
        //  "is the sun up" must mean one thing across the sky, the weather and the
        //  vegetation, or a shadow will disagree with a temperature.
        // =====================================================================

        /// <summary>True when the sun's centre is above the geometric horizon.</summary>
        public bool SunIsUp
        {
            get { return elevationDeg > 0f; }
        }

        /// <summary>True when the moon's centre is above the horizon, whether or not it is lit.</summary>
        public bool MoonIsUp
        {
            get { return lunarElevationDeg > 0f; }
        }

        /// <summary>
        /// True on a day when the sun does not rise at all. Reported from the day
        /// length rather than from the current elevation, so it is the same answer
        /// at every hour of that day.
        /// </summary>
        public bool IsPolarNight
        {
            get { return dayLengthHours <= 0f; }
        }

        /// <summary>True on a day when the sun does not set at all.</summary>
        public bool IsPolarDay
        {
            get { return dayLengthHours >= HoursPerDay; }
        }

        /// <summary>True in the southern hemisphere, where every season is inverted.</summary>
        public bool IsSouthernHemisphere
        {
            get { return latitudeDeg < 0f; }
        }

        // ---------------------------------------------------------------------
        //  WHEN THE SUN CROSSES THE HORIZON
        //
        //  Derived rather than stored, because they are the solar noon and the day
        //  length already in this struct read a second way, and a stored copy would
        //  be two numbers that could disagree.
        //
        //  A CLOCK TIME, NOT A ROUND HOUR. Half the day length either side of solar
        //  noon, and solar noon already carries the longitude offset and the equation
        //  of time - so on the same date at the same latitude two worlds a few
        //  degrees of longitude apart get different sunrise clock times, which is the
        //  truth. Anything that jumps the clock to 06:00 instead is wrong on every
        //  day of the year but the equinoxes, and at every latitude but the equator,
        //  and the sun visibly sits at the wrong height when it lands.
        //
        //  Outside 0..24 is legitimate: a large longitude offset can put sunrise
        //  before local midnight. The hour angle is periodic in a day, so wrapping
        //  the value into a clock day lands the sun at the same height - callers may
        //  wrap freely and none of them needs to know why that is safe.
        // ---------------------------------------------------------------------

        /// <summary>
        /// h. Local clock hour at which the sun's centre crosses the horizon rising.
        /// Meaningless when <see cref="HasSunriseToday"/> is false - test that first.
        /// </summary>
        public float SunriseHour
        {
            get { return solarNoonHour - dayLengthHours * HalfOfADayLength; }
        }

        /// <summary>
        /// h. Local clock hour at which the sun's centre crosses the horizon setting.
        /// Meaningless when <see cref="HasSunriseToday"/> is false.
        /// </summary>
        public float SunsetHour
        {
            get { return solarNoonHour + dayLengthHours * HalfOfADayLength; }
        }

        /// <summary>
        /// Whether the sun crosses the horizon at all today. False on both polar
        /// extremes, where there is genuinely no crossing to report: inside a polar
        /// night the sun never comes up, and under a midnight sun it never goes down.
        /// The two are opposite conditions with the same answer to this question, and
        /// a consumer that only guarded against the first would offer a sunrise
        /// halfway through a polar summer.
        /// </summary>
        public bool HasSunriseToday
        {
            get { return !IsPolarNight && !IsPolarDay; }
        }

        /// <summary>h. Local midnight, named so a caller jumping the clock to it is not writing a bare zero.</summary>
        public const float MidnightHour = 0f;

        // =====================================================================
        //  UNIT DIVISORS
        //
        //  Two exact definitions, not measurements, so they are allowed to live
        //  here rather than in AtmoConstants: a struct that cannot express its own
        //  hour without reaching into the physics constants would make the clock
        //  depend on the atmosphere, which is backwards.
        // =====================================================================

        /// <summary>h per day. Exact by definition.</summary>
        public const float HoursPerDay = 24f;

        /// <summary>s per hour. Exact by definition.</summary>
        public const float SecondsPerHour = 3600f;

        /// <summary>s per day. Exact by definition.</summary>
        public const float SecondsPerDay = HoursPerDay * SecondsPerHour;

        /// <summary>
        /// The sun crosses the horizon twice a day, so half a day length is the
        /// distance from either crossing to solar noon. Named rather than written as
        /// 0.5 at the two places above, because a 0.5 next to a day length reads as a
        /// fudge factor and this one is a count.
        /// </summary>
        private const float HalfOfADayLength = 0.5f;

        /// <summary>
        /// A short human-readable stamp, for log lines and the inspector. Not a
        /// serialisation format: nothing parses this back.
        /// </summary>
        public override string ToString()
        {
            int hour = (int)localHour;
            int minute = (int)((localHour - hour) * AtmoConstants.MinutesPerHour);

            return "Y" + year
                   + " D" + dayOfYear
                   + " " + hour.ToString("00") + ":" + minute.ToString("00")
                   + " sun " + elevationDeg.ToString("0.0") + "deg";
        }
    }
}
