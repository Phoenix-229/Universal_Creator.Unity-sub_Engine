// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWeatherSystemManager.cs
//  Purpose: The WeatherSystem child's control surface. Wind is real and shipping;
//           everything else here is a declared placeholder for a later module.
//
//  WIND IS THE PART THAT WORKS
//  ---------------------------
//  UCWindManager already drives grass, shrubs, trees, water and the sway maps from
//  one source. This component owns it: it makes sure exactly one exists, keeps it
//  on this object, and is where the wind settings are edited from.
//
//  Keeping it on this object is not cosmetic. A WindZone blows along its own
//  forward axis, so UCWindManager rotates the transform it sits on. On the world
//  root that would rotate the root - and every terrain cell parented under it -
//  every time the wind direction changed. On the WeatherSystem child it rotates a
//  child that owns nothing but weather.
//
//  WIND AND CLIMATE ARE LIVE; THE REST IS STILL DECLARED
//  -----------------------------------------------------
//  Climate and Season is now read. The ecology layer resolves temperature, the
//  water balance and the treeline from it through the accessors at the bottom of
//  this file, so there is one climate reference for the world rather than one per
//  consumer.
//
//  Precipitation, cloud, fog and storms are still named-only: they carry the
//  ranges and units they will use, and nothing reads them yet. They keep saying
//  PLACEHOLDER because they are one, and a tooltip that claimed otherwise would
//  be the lie that costs someone an afternoon. They exist so the module that
//  implements them has a shape to fill rather than a blank file to invent from -
//  which is how a later pass ends up with its own parallel weather system that
//  does not agree with this one about which way the wind is blowing.
//
//  THE WHOLE CLOCK IS OWNED HERE  (W-03, W-04)
//  -------------------------------------------
//  This used to be split: the year lived here and the day - time of day, day
//  length, latitude, north bearing - lived on the lighting manager, on the
//  reasoning that the sun rig is what consumes them. That was wrong, and the way
//  it was wrong is worth recording so it is not reintroduced.
//
//  Latitude is not a lighting property. It sets the Coriolis sign (W-19), the
//  meridional temperature gradient (W-11), and which way a warm hillside faces
//  (V-09) - so a second copy anywhere produces a world with a northern sun over
//  southern forest, and nothing in the scene says why. A split clock also means
//  two things advance time, and the moment they disagree by one frame the sky and
//  the phenology are in different hours.
//
//  So: this component owns the clock outright - time scale, start day, start
//  hour, latitude, longitude, standard meridian, pause. It resolves all of it
//  into one immutable ClockState. The lighting manager, the vegetation manager
//  and everything downstream read that struct and declare none of it themselves.
//  When this component is absent they fall back to a documented default
//  (latitude 45, longitude 0) and say so in the inspector rather than guessing
//  quietly.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level weather control. Wind is live; the remaining sections are
    /// declared for a later module and are not read by anything yet.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Weather System Manager")]
    [DisallowMultipleComponent]
    public partial class UCWeatherSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  WIND  -  IMPLEMENTED
        // =====================================================================

        [Header("Wind")]

        [Tooltip("Wind is on. Off removes the wind manager's effect: grass, shrubs, " +
                 "trees and water all go still, and the sway maps stop being uploaded.")]
        public bool windEnabled = true;

        [Tooltip("Off (the default) leaves the wind exactly as the author set it. On, the weather " +
                 "layer steers the wind: the same manager still owns direction, strength and the " +
                 "shader upload, but its bearing and speed follow the pressure field instead of the " +
                 "authored dials, so grass and trees lean into the weather as it passes.")]
        public bool weatherDrivesWind = false;

        // Serialized so the link survives a domain reload. Found or created by
        // EnsureWind rather than assigned by hand.
        [SerializeField, HideInInspector] private UCWindManager wind;

        /// <summary>
        /// The wind manager for this world, creating it on this object if there is
        /// none. Never returns null while this component is alive.
        /// </summary>
        public UCWindManager Wind
        {
            get { return EnsureWind(); }
        }

        /// <summary>The wind manager if one already exists, without creating it.</summary>
        public UCWindManager WindOrNull
        {
            get
            {
                if (wind != null) return wind;

                // A world built before this component existed has its wind manager
                // somewhere else in the hierarchy. Adopt it rather than adding a
                // second one that would fight it for the same shader globals.
                UniverseCreatorObject world = World;
                Transform search = world != null ? world.transform : transform;
                wind = search.GetComponentInChildren<UCWindManager>(true);
                return wind;
            }
        }

        /// <summary>The world this weather belongs to, or null when loose in a scene.</summary>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        /// <summary>
        /// Guarantees one wind manager, on this object.
        ///
        /// Moving an adopted manager is not attempted: a component cannot change
        /// GameObject, and destroying and recreating it would drop the composited
        /// sway map the graph has already baked into it. So an older world keeps
        /// its manager where it is and this component drives it from here.
        /// </summary>
        public UCWindManager EnsureWind()
        {
            UCWindManager existing = WindOrNull;
            if (existing != null) return existing;

            wind = gameObject.AddComponent<UCWindManager>();
            return wind;
        }

        // =====================================================================
        //  PRECIPITATION  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Precipitation  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Rain intensity, 0 dry to 1 downpour.")]
        [Range(0f, 1f)]
        public float rainIntensity;

        [Tooltip("PLACEHOLDER. Snow intensity, 0 clear to 1 blizzard.")]
        [Range(0f, 1f)]
        public float snowIntensity;

        [Tooltip("PLACEHOLDER. Metres above which precipitation falls as snow rather " +
                 "than rain.")]
        public float snowLineMetres = 1200f;

        [Tooltip("PLACEHOLDER. How wet surfaces read, 0 dry to 1 streaming.")]
        [Range(0f, 1f)]
        public float surfaceWetness;

        [Tooltip("PLACEHOLDER. Seconds a surface takes to dry once precipitation stops.")]
        public float dryingSeconds = 90f;

        // =====================================================================
        //  CLOUD AND SKY  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Cloud and Sky  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Sky coverage, 0 clear to 1 overcast.")]
        [Range(0f, 1f)]
        public float cloudCoverage = 0.35f;

        [Tooltip("PLACEHOLDER. Metres above sea level the cloud base sits at.")]
        public float cloudBaseMetres = 900f;

        [Tooltip("PLACEHOLDER. Cloud thickness in metres. Drives how dark an " +
                 "overcast sky reads.")]
        public float cloudThicknessMetres = 400f;

        [Tooltip("PLACEHOLDER. Clouds drift with the wind rather than on their own " +
                 "vector, so a storm front and the grass agree on the direction.")]
        public bool cloudsFollowWind = true;

        // =====================================================================
        //  FOG AND VISIBILITY  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Fog and Visibility  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Fog density, 0 clear to 1 whiteout.")]
        [Range(0f, 1f)]
        public float fogDensity;

        [Tooltip("PLACEHOLDER. Metres at which visibility falls to nothing.")]
        public float visibilityMetres = 3000f;

        [Tooltip("PLACEHOLDER. Fog pools in low ground rather than filling the world " +
                 "uniformly - the difference between valley mist and a grey filter.")]
        public bool groundFog = true;

        [Tooltip("PLACEHOLDER. Metres above the terrain that ground fog reaches.")]
        public float groundFogHeightMetres = 30f;

        // =====================================================================
        //  STORMS  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Storms  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Storms can build and pass on their own.")]
        public bool stormsEnabled;

        [Tooltip("PLACEHOLDER. Average minutes between storms.")]
        public float stormIntervalMinutes = 45f;

        [Tooltip("PLACEHOLDER. Minutes one storm takes to build, hold and clear.")]
        public float stormDurationMinutes = 8f;

        [Tooltip("PLACEHOLDER. Wind multiplier at the height of a storm.")]
        [Range(1f, 5f)]
        public float stormWindMultiplier = 2.5f;

        [Tooltip("PLACEHOLDER. Lightning strikes per minute at the height of a storm.")]
        public float lightningPerMinute = 4f;

        // =====================================================================
        //  WORLD CLOCK  -  LIVE  (W-03, W-04)
        //
        //  The one clock. Nothing else in the toolkit advances time, and nothing
        //  else declares latitude.
        //
        //  It is a clock and not a frame counter, and that distinction is the
        //  whole task. A frame counter gives a different world on a different
        //  machine, and it cannot be scrubbed: an author who drags the hour back
        //  gets a different answer than they had a moment ago, because the state
        //  that produced the first answer was consumed by advancing it. Here the
        //  authored start point plus one elapsed accumulator is the entire state,
        //  so the same authored numbers always resolve to the same ClockState no
        //  matter how many frames have gone past.
        //
        //  The accumulator is a double on purpose. Held as a float, a day's worth
        //  of seconds reaches 86 400, where float steps by about 1/128 of a
        //  second - coarser than one frame at 60 Hz. Time would then quantise and
        //  drift against the day in a way that only shows up as the sun arriving
        //  at the wrong hour after a long session.
        // =====================================================================

        [Header("World Clock")]

        [Tooltip("Time is frozen. On by default so that no existing world starts " +
                 "moving underneath its author. While this is on the sun, sky, weather " +
                 "and phenology are all held at the start day and hour below - " +
                 "together, because they all read this one clock.")]
        public bool pauseTime = true;

        [Tooltip("Simulated seconds per real second. 1 is real time, so a full " +
                 "in-world day takes 24 real hours; 1200 puts a day in about 72 " +
                 "seconds. This is the only rate knob in the toolkit - day length and " +
                 "year length are reported from it rather than authored a second time, " +
                 "which is what stops the sky and the seasons running at rates that " +
                 "disagree.")]
        public float timeScale = 1200f;

        [Tooltip("Day of the year the world starts on, 1 to 365. 172 is northern " +
                 "midsummer, 355 northern midwinter. Drag it while time is paused to " +
                 "scrub the year.")]
        [Range(1, 365)]
        public int startDayOfYear = 172;

        [Tooltip("Hour of the day the world starts at, 0 to 24. This is clock time, " +
                 "not solar time: the sun reaches its highest point at whatever hour " +
                 "the longitude and the equation of time work out to, which is why " +
                 "noon shadows do not point exactly north.")]
        [Range(0f, 24f)]
        public float startHourOfDay = 12f;

        [Tooltip("Latitude in degrees, negative south. Owned here and nowhere else. " +
                 "It decides how high the sun climbs and how long twilight lasts, the " +
                 "sign of the Coriolis deflection, the temperature gradient toward the " +
                 "pole, and which way a warm hillside faces - so a second copy of this " +
                 "number anywhere gives a world whose sun and whose forest are in " +
                 "different hemispheres, with nothing in the scene to say why.")]
        [Range(-90f, 90f)]
        public float latitudeDeg = 45f;

        [Tooltip("Degrees of latitude the world spans from its south edge to its " +
                 "north edge. 0 - the default - gives one latitude everywhere, which " +
                 "is right for anything up to a few tens of kilometres and keeps " +
                 "existing worlds identical.\n\n" +
                 "Above 0 the latitude above becomes the world's centre and the edges " +
                 "run half the span either side of it, so a 30 degree span at latitude " +
                 "45 runs from 30 to 60 - temperate at the bottom, near-boreal at the " +
                 "top, from one graph. Sampled per chunk, so a chunk has one latitude " +
                 "and the trees inside it do not each get their own.")]
        [Range(0f, 180f)]
        public float latitudeSpanDeg;

        [Tooltip("Longitude in degrees, positive east. This only shifts the clock " +
                 "against the sun: it moves solar noon away from 12:00 and does not " +
                 "change how much sun the world receives.")]
        [Range(-180f, 180f)]
        public float longitudeDeg;

        [Tooltip("Centre longitude of the world's time zone, in degrees, positive " +
                 "east. Set it equal to the longitude above and clock noon is solar " +
                 "noon, give or take the equation of time. A real time zone is a " +
                 "multiple of 15.")]
        [Range(-180f, 180f)]
        public float standardMeridianDeg;

        [Tooltip("Axial tilt in degrees. Earth is 23.44. This one number is the whole " +
                 "reason seasons exist: set it to 0 and the world has none - every day " +
                 "the same length at every latitude, and no summer to grow in. Past 40 " +
                 "the temperate zones get violent seasons and midnight sun reaches far " +
                 "further down. It lives beside latitude because it is the same kind " +
                 "of value: a property of the planet, not of the sun rig.")]
        [Range(0f, 90f)]
        public float axialTiltDeg = AtmoConstants.EarthAxialTiltDegrees;

        [Tooltip("Compass bearing of world north, in degrees, so the sun rises where " +
                 "the world says east is.")]
        [Range(0f, 360f)]
        public float northBearingDeg;

        // Simulated seconds since the authored start point. Serialized so a world
        // reopened mid-session is where it was left; hidden because it is not a
        // setting - it is the clock's hand, and the way to move it is the start
        // fields above, or SetTime.
        [SerializeField, HideInInspector] private double elapsedSimSeconds;

        /// <summary>
        /// Simulated seconds since the authored start point. Read for diagnostics;
        /// move the clock with <see cref="SetTime"/> rather than by writing here.
        /// </summary>
        public double ElapsedSimSeconds
        {
            get { return elapsedSimSeconds; }
        }

        /// <summary>
        /// Absolute simulated second, counting from the start of day 1 of year 1.
        /// The authored start point and the elapsed accumulator meet here and only
        /// here, so day, hour and year cannot end up rolled over inconsistently
        /// with one another - which is the failure mode of keeping three mutable
        /// counters and incrementing them separately.
        /// </summary>
        private double AbsoluteSeconds
        {
            get
            {
                double startSeconds =
                    (startDayOfYear - 1) * (double)ClockState.SecondsPerDay +
                    startHourOfDay * (double)ClockState.SecondsPerHour;

                return startSeconds + elapsedSimSeconds;
            }
        }

        /// <summary>Whole simulated days since the start of year 1.</summary>
        private double AbsoluteDays
        {
            get { return System.Math.Floor(AbsoluteSeconds / ClockState.SecondsPerDay); }
        }

        /// <summary>Calendar year, starting at 1. Rolls over past day 365.</summary>
        public int Year
        {
            get
            {
                return 1 + (int)System.Math.Floor(
                    AbsoluteDays / AtmoConstants.DaysPerCalendarYear);
            }
        }

        /// <summary>Day of the year, 1 to 365.</summary>
        public int DayOfYear
        {
            get
            {
                double days = AbsoluteDays;
                double yearsWhole = System.Math.Floor(days / AtmoConstants.DaysPerCalendarYear);
                return 1 + (int)(days - yearsWhole * AtmoConstants.DaysPerCalendarYear);
            }
        }

        /// <summary>Seconds since local midnight, 0 to 86400.</summary>
        public float SecondsOfDay
        {
            get { return (float)(AbsoluteSeconds - AbsoluteDays * ClockState.SecondsPerDay); }
        }

        /// <summary>Clock hour, 0 to 24.</summary>
        public float LocalHour
        {
            get { return ClockMath.LocalHourFromSeconds(SecondsOfDay); }
        }

        /// <summary>
        /// Real seconds one in-world day takes at the current time scale. Reported
        /// rather than authored, so it cannot disagree with the rate above.
        /// </summary>
        public float RealSecondsPerDay
        {
            get { return ClockState.SecondsPerDay / Mathf.Max(0.0001f, timeScale); }
        }

        /// <summary>
        /// Real minutes one in-world year takes at the current time scale. Same
        /// reason: derived, never a second knob.
        /// </summary>
        public float RealMinutesPerYear
        {
            get
            {
                return RealSecondsPerDay * AtmoConstants.DaysPerCalendarYear /
                       AtmoConstants.SecondsPerMinute;
            }
        }

        /// <summary>
        /// True while the one clock is actually advancing: enabled, not paused, and a
        /// positive time scale. The single owner of that predicate - Update gates on it
        /// so the manager cannot disagree with itself, and the edit-mode driver reads it
        /// to decide whether the Scene view needs pumping rather than duplicating the
        /// pause-and-scale test where the two could drift apart.
        /// </summary>
        public bool ClockIsRunning
        {
            get { return isActiveAndEnabled && !pauseTime && timeScale > 0f; }
        }

        /// <summary>
        /// Moves the clock to a day and hour and clears the elapsed accumulator, so
        /// the world sits exactly at the stated time rather than at the stated time
        /// plus however long the session has been open. Two calls with the same
        /// arguments, any number of frames apart, leave an identical clock. (W-03)
        /// </summary>
        public void SetTime(int dayOfYear, float hourOfDay)
        {
            startDayOfYear = Mathf.Clamp(dayOfYear, 1, (int)AtmoConstants.DaysPerCalendarYear);
            startHourOfDay = Mathf.Repeat(hourOfDay, ClockState.HoursPerDay);
            elapsedSimSeconds = 0.0;
        }

        /// <summary>
        /// Moves the clock forward (or back, for a negative value) by a number of
        /// simulated seconds, independently of frame time and of timeScale. This is
        /// how a caller scrubs: it adds to the same accumulator Update writes, so a
        /// scrubbed clock and a run clock are the same clock and cannot disagree.
        ///
        /// It does not clamp to the current year. Rolling past day 365 advances the
        /// year, which is the whole reason the accumulator is one monotone double
        /// rather than a set of wrapping counters. (W-03)
        /// </summary>
        public void Advance(double simSeconds)
        {
            elapsedSimSeconds += simSeconds;

            // Guard against running the clock behind its own authored start, where
            // the date arithmetic would go negative and the year would count down.
            double floor = -((startDayOfYear - 1) * (double)ClockState.SecondsPerDay +
                             startHourOfDay * (double)ClockState.SecondsPerHour);

            if (elapsedSimSeconds < floor) elapsedSimSeconds = floor;
        }

        /// <summary>
        /// The world clock, resolved. One immutable struct computed from the
        /// authored fields above and nothing else - no frame time, no cached state,
        /// no random source - so it is identical on every machine, after every
        /// reload, and between two calls in the same frame.
        ///
        /// This is what every consumer reads. A consumer that reaches for
        /// Time.time or a frame count instead is the defect W-03 exists to
        /// prevent: it would run at the machine's frame rate rather than at the
        /// world's clock, and stop agreeing with the sky the moment the frame rate
        /// changed.
        /// </summary>
        public ClockState ResolveClock()
        {
            int year = Year;
            int day = DayOfYear;
            float secondsOfDay = SecondsOfDay;

            float winterPhase01 = ClockMath.WinterPhase01(day);
            float declinationDeg =
                ClockMath.DeclinationDegFromWinterPhase01(winterPhase01, axialTiltDeg);

            float eotMin = ClockMath.EquationOfTimeMinutes(day);
            float hourAngleDeg = ClockMath.SolarHourAngleDeg(
                secondsOfDay, longitudeDeg, eotMin, standardMeridianDeg);

            float elevationDeg = ClockMath.ElevationDeg(latitudeDeg, declinationDeg, hourAngleDeg);
            float azimuthDeg = ClockMath.AzimuthDeg(
                latitudeDeg, declinationDeg, hourAngleDeg, elevationDeg);

            float lunarPhase01 = ClockMath.LunarPhase01(day, year);

            // W-07. The moon is placed the same way the sun above it was: a
            // declination and an hour angle, fed to the same two functions. Its hour
            // angle is derived FROM the sun's, so the longitude and equation-of-time
            // corrections already in that number are inherited rather than restated.
            float lunarEclipticLatDeg = ClockMath.LunarEclipticLatitudeDeg(day, year);

            float lunarDeclinationDeg = ClockMath.LunarDeclinationDeg(
                winterPhase01, lunarPhase01, lunarEclipticLatDeg, axialTiltDeg);

            float lunarHourAngleDeg = ClockMath.LunarHourAngleDeg(
                hourAngleDeg, winterPhase01, lunarPhase01,
                lunarEclipticLatDeg, axialTiltDeg);

            float lunarElevationDeg = ClockMath.ElevationDeg(
                latitudeDeg, lunarDeclinationDeg, lunarHourAngleDeg);

            return new ClockState(
                year,
                day,
                secondsOfDay,
                ClockMath.LocalHourFromSeconds(secondsOfDay),
                latitudeDeg,
                longitudeDeg,
                elevationDeg,
                azimuthDeg,
                declinationDeg,
                hourAngleDeg,
                ClockMath.SolarNoonHour(longitudeDeg, eotMin, standardMeridianDeg),
                ClockMath.DayLengthHours(latitudeDeg, declinationDeg),
                eotMin,
                lunarPhase01,
                lunarElevationDeg,
                ClockMath.AzimuthDeg(latitudeDeg, lunarDeclinationDeg,
                                     lunarHourAngleDeg, lunarElevationDeg),
                lunarDeclinationDeg,
                ClockMath.LunarIlluminatedFraction(lunarPhase01),
                ClockMath.Season01(winterPhase01, latitudeDeg),
                AtmoConstants.SolarConstantWm2,
                ClockMath.AirMass(elevationDeg));
        }

        // =====================================================================
        //  THE FALLBACK WHEN THERE IS NO WEATHER SYSTEM  (W-04)
        //
        //  A world can legitimately have no WeatherSystem child - deleted, or
        //  never added, or a lighting rig dropped loose into a scene to look at
        //  one mesh. The consumers still need a latitude, and the one thing they
        //  must not do is each invent their own, because that is the two-owner
        //  defect arriving by the back door.
        //
        //  So the default lives here, once, next to the real thing: latitude 45,
        //  longitude 0, midsummer noon. Consumers resolve through the statics
        //  below and never construct a clock themselves, and the inspector says
        //  out loud when the default is what is in use.
        // =====================================================================

        /// <summary>Latitude used when a world has no weather system. (W-04)</summary>
        public const float DefaultLatitudeDeg = 45f;

        /// <summary>Longitude used when a world has no weather system. (W-04)</summary>
        public const float DefaultLongitudeDeg = 0f;

        /// <summary>Day of year used when a world has no weather system: midsummer.</summary>
        public const int DefaultDayOfYear = 172;

        /// <summary>Hour used when a world has no weather system: noon.</summary>
        public const float DefaultHourOfDay = 12f;

        /// <summary>
        /// The weather manager governing a transform's world, or null. Searches the
        /// world root rather than the transform's own children, so a lighting or
        /// vegetation manager sitting in a sibling child still finds it.
        /// </summary>
        public static UCWeatherSystemManager FindForWorld(Transform context)
        {
            if (context == null) return null;

            UniverseCreatorObject world = context.GetComponentInParent<UniverseCreatorObject>();
            Transform search = world != null ? world.transform : context;
            return search.GetComponentInChildren<UCWeatherSystemManager>(true);
        }

        /// <summary>
        /// The clock governing a transform's world: the weather manager's if there
        /// is one, otherwise the documented default. This is what
        /// <c>UCLightingSystemManager</c> and <c>UCVegetationSystemManager</c> read,
        /// so neither declares a latitude of its own. (W-04)
        /// </summary>
        public static ClockState ResolveClockFor(Transform context)
        {
            UCWeatherSystemManager weather = FindForWorld(context);
            return weather != null ? weather.ResolveClock() : DefaultClock();
        }

        /// <summary>
        /// The documented fallback clock, built by the same path as the real one so
        /// it cannot drift away from it: a throwaway component would be cleaner to
        /// read but would allocate a GameObject per call, and this runs from
        /// inspector repaints.
        /// </summary>
        public static ClockState DefaultClock()
        {
            float secondsOfDay = DefaultHourOfDay * ClockState.SecondsPerHour;

            float winterPhase01 = ClockMath.WinterPhase01(DefaultDayOfYear);
            float declinationDeg = ClockMath.DeclinationDegFromWinterPhase01(
                winterPhase01, AtmoConstants.EarthAxialTiltDegrees);

            float eotMin = ClockMath.EquationOfTimeMinutes(DefaultDayOfYear);
            float hourAngleDeg = ClockMath.SolarHourAngleDeg(
                secondsOfDay, DefaultLongitudeDeg, eotMin, DefaultLongitudeDeg);

            float elevationDeg = ClockMath.ElevationDeg(
                DefaultLatitudeDeg, declinationDeg, hourAngleDeg);
            float azimuthDeg = ClockMath.AzimuthDeg(
                DefaultLatitudeDeg, declinationDeg, hourAngleDeg, elevationDeg);

            float lunarPhase01 = ClockMath.LunarPhase01(DefaultDayOfYear, 1);

            // The same reduction as ResolveClock, against Earth's tilt. Repeated
            // rather than shared because this path has no component to read fields
            // from - every input here is a constant - and the arithmetic lives in
            // ClockMath either way, so there is one copy of the formulae and two
            // sets of inputs.
            float lunarEclipticLatDeg = ClockMath.LunarEclipticLatitudeDeg(DefaultDayOfYear, 1);

            float lunarDeclinationDeg = ClockMath.LunarDeclinationDeg(
                winterPhase01, lunarPhase01, lunarEclipticLatDeg,
                AtmoConstants.EarthAxialTiltDegrees);

            float lunarHourAngleDeg = ClockMath.LunarHourAngleDeg(
                hourAngleDeg, winterPhase01, lunarPhase01, lunarEclipticLatDeg,
                AtmoConstants.EarthAxialTiltDegrees);

            float lunarElevationDeg = ClockMath.ElevationDeg(
                DefaultLatitudeDeg, lunarDeclinationDeg, lunarHourAngleDeg);

            return new ClockState(
                1,
                DefaultDayOfYear,
                secondsOfDay,
                DefaultHourOfDay,
                DefaultLatitudeDeg,
                DefaultLongitudeDeg,
                elevationDeg,
                azimuthDeg,
                declinationDeg,
                hourAngleDeg,
                ClockMath.SolarNoonHour(DefaultLongitudeDeg, eotMin, DefaultLongitudeDeg),
                ClockMath.DayLengthHours(DefaultLatitudeDeg, declinationDeg),
                eotMin,
                lunarPhase01,
                lunarElevationDeg,
                ClockMath.AzimuthDeg(DefaultLatitudeDeg, lunarDeclinationDeg,
                                     lunarHourAngleDeg, lunarElevationDeg),
                lunarDeclinationDeg,
                ClockMath.LunarIlluminatedFraction(lunarPhase01),
                ClockMath.Season01(winterPhase01, DefaultLatitudeDeg),
                AtmoConstants.SolarConstantWm2,
                ClockMath.AirMass(elevationDeg));
        }

        /// <summary>
        /// Axial tilt governing a transform's world, or Earth's when there is no
        /// weather system. Separate from the clock because tilt is not a field of
        /// <c>ClockState</c> - the clock reports the declination the tilt produced,
        /// which is what consumers of a resolved moment need.
        /// </summary>
        public static float ResolveAxialTiltDeg(Transform context)
        {
            UCWeatherSystemManager weather = FindForWorld(context);
            return weather != null ? weather.axialTiltDeg : AtmoConstants.EarthAxialTiltDegrees;
        }

        /// <summary>
        /// Compass bearing of world north, or 0 when there is no weather system.
        /// </summary>
        public static float ResolveNorthBearingDeg(Transform context)
        {
            UCWeatherSystemManager weather = FindForWorld(context);
            return weather != null ? weather.northBearingDeg : 0f;
        }

        /// <summary>
        /// W-05. Degrees of latitude a transform's world spans, or 0 when there is no
        /// weather system. Zero is the honest answer for an absent manager as well as
        /// the default for a present one: no manager means no authored gradient, and
        /// a world then has the one latitude the clock's default reports rather than
        /// an invented spread.
        ///
        /// Separate from the clock for the same reason as the axial tilt above: a
        /// span is a property of the world's extent, not of a resolved moment, so it
        /// is not a field of <c>ClockState</c>.
        /// </summary>
        public static float ResolveLatitudeSpanDeg(Transform context)
        {
            UCWeatherSystemManager weather = FindForWorld(context);
            return weather != null ? weather.latitudeSpanDeg : 0f;
        }

        // =====================================================================
        //  CLIMATE AND SEASON  -  LIVE
        //
        //  This group is the world's climate reference, and it is read: the
        //  ecology layer resolves temperature, water balance and the treeline
        //  from these fields rather than declaring a second set of its own.
        //
        //  Season phase sits in the World Clock group above now, because the year
        //  is part of the clock rather than a climate setting. What is left here
        //  is climate proper: how warm, how wet, how continental.
        // =====================================================================

        [Header("Climate and Season")]

        [Tooltip("Annual mean air temperature at sea level, degrees Celsius. Every " +
                 "temperature in the world is derived from this one by the lapse rate " +
                 "below, so it is the single number that decides whether the world is " +
                 "arctic, temperate or tropical.")]
        public float temperatureCelsius = 15f;

        [Tooltip("Derive the temperature above from the world's latitude instead of " +
                 "typing it in, using 0.7 degrees of cooling per degree poleward. " +
                 "Off by default so no existing world's climate changes underneath it. " +
                 "On, latitude becomes the single control: move the world north and " +
                 "the treeline comes down by itself, which is what makes altitude and " +
                 "latitude interchangeable the way they are in the world (V-26).")]
        public bool deriveTemperatureFromLatitude = false;

        [Tooltip("Annual mean sea-level temperature at the equator, used only when " +
                 "the temperature is derived from latitude. 27 C is Earth's.")]
        public float equatorTemperatureCelsius = 27f;

        [Tooltip("Degrees Celsius lost per kilometre of altitude. 6.5 is the standard " +
                 "atmospheric lapse rate; 9.8 is dry adiabatic and 5.0 saturated. This " +
                 "is what makes a mountain cold, and the ecology layer honours the same " +
                 "value so terrain and vegetation cannot disagree about it.")]
        public float lapseRatePerKm = 6.5f;

        [Tooltip("Relative humidity, 0 arid to 1 saturated.")]
        [Range(0f, 1f)]
        public float humidity = 0.5f;

        /// <summary>
        /// Position in the year, 0 to 1, on the winter-zero convention: 0 is
        /// midwinter in the northern hemisphere, 0.5 northern midsummer. The
        /// southern hemisphere is derived from the sign of latitude rather than
        /// authored separately.
        ///
        /// Derived from the clock's day of year, not stored. It used to be a
        /// serialized field that the vegetation manager advanced from its own tick
        /// accumulator, which meant the year ran at the ecology tick rate while the
        /// day ran at the frame rate - two clocks, guaranteed to disagree, and
        /// neither reproducible on a second machine. Scrub startDayOfYear to move
        /// it. (W-03)
        ///
        /// Note this is not <c>ClockState.season01</c>, which is zero at the vernal
        /// equinox. The two differ by a quarter turn; ResolveClock converts.
        /// </summary>
        public float seasonPhase
        {
            get { return ClockMath.WinterPhase01(DayOfYear); }
        }

        [Tooltip("Degrees Celsius. Half the swing between the coldest and warmest " +
                 "month, so the warmest month reads as the annual mean plus this. It " +
                 "is what places the treeline: trees fail where the warmest month " +
                 "falls below 6.4 degC, and that threshold moves with this number, " +
                 "not with the annual mean. Maritime climates run 5 to 8, mid-latitude " +
                 "continental 14 to 20, Siberia past 25, the deep tropics under 3.")]
        public float annualTempHalfAmplitudeCelsius = 12f;

        [Tooltip("Degrees Celsius between the daily minimum and maximum. Required by " +
                 "the potential-evapotranspiration law, which reads the daily range as " +
                 "its proxy for how much sun actually reaches the ground: a wide range " +
                 "means clear skies and heavy evaporation. Humid tropics run 6 to 10, " +
                 "deserts past 20.")]
        public float diurnalTempRangeCelsius = 10f;

        [Tooltip("0 fully maritime, 1 fully continental. Scales the annual swing with " +
                 "distance from the sea, so a coast stays mild while the interior at " +
                 "the same latitude freezes and bakes. This is why two places on one " +
                 "line of latitude grow different forests.")]
        [Range(0f, 1f)]
        public float continentality = 0.5f;

        [Tooltip("Millimetres of precipitation per year at the reference site. The " +
                 "graph's orographic nodes raise it on windward slopes and drop it in " +
                 "rain shadows; this is the baseline they work from. Desert is under " +
                 "250, temperate forest 700 to 1500, rainforest past 2000.")]
        public float annualPrecipitationMm = 800f;

        [Tooltip("0 spreads rain evenly through the year, 1 concentrates it all in one " +
                 "wet season. A monsoon and an oceanic climate can have identical " +
                 "annual totals and grow completely different vegetation, and this is " +
                 "the field that separates them.")]
        [Range(0f, 1f)]
        public float precipitationSeasonality = 0.3f;

        [Tooltip("Position in the year, 0 to 1, at which the wet season peaks. 0.5 is " +
                 "midsummer rain, 0 midwinter rain - the difference between a monsoon " +
                 "and a Mediterranean climate.")]
        [Range(0f, 1f)]
        public float wettestSeasonPhase = 0.5f;

        // =====================================================================
        //  LIFECYCLE
        // =====================================================================

        private void OnEnable()
        {
            ApplyWindEnabled();
        }

        /// <summary>
        /// Advances the one clock. This is the only place in the toolkit where time
        /// moves. (W-03)
        ///
        /// Runs in edit mode as well as play mode, which is what lets an author
        /// watch a day pass without entering play. The scene is not marked dirty by
        /// it: the accumulator is what changed, and dirtying the scene every frame
        /// while time runs would leave an author unable to tell an edit from a tick.
        /// </summary>
        private void Update()
        {
            if (!ClockIsRunning) return;

            float delta = Application.isPlaying ? Time.deltaTime : Time.unscaledDeltaTime;
            if (delta <= 0f) return;

            elapsedSimSeconds += (double)delta * timeScale;

            // Vegetation caches the resolved season. Without this the standing
            // forest would hold whatever season it was built in while the sky moved
            // on, which reads as a bug in the phenology rather than in the clock.
            if (SeasonMovedEnoughToInvalidate())
            {
                UCVegetationSystemManager.InvalidateForWorld(transform);
            }
        }

        // Day of year at the last cache drop. -1 means "no cache to drop yet".
        private int lastInvalidatedDay = -1;

        /// <summary>
        /// True once the clock has crossed into a new day since the last cache
        /// drop. Measured in days rather than in frames so the same threshold
        /// behaves the same way at any time scale, and so a fast clock does not
        /// invalidate the vegetation cache every frame.
        /// </summary>
        private bool SeasonMovedEnoughToInvalidate()
        {
            int day = DayOfYear;
            if (day == lastInvalidatedDay) return false;

            bool hadCache = lastInvalidatedDay >= 0;
            lastInvalidatedDay = day;
            return hadCache;
        }

        private void OnValidate()
        {
            snowLineMetres = Mathf.Max(0f, snowLineMetres);
            dryingSeconds = Mathf.Max(0f, dryingSeconds);
            cloudBaseMetres = Mathf.Max(0f, cloudBaseMetres);
            cloudThicknessMetres = Mathf.Max(0f, cloudThicknessMetres);
            visibilityMetres = Mathf.Max(1f, visibilityMetres);
            groundFogHeightMetres = Mathf.Max(0f, groundFogHeightMetres);
            stormIntervalMinutes = Mathf.Max(0.1f, stormIntervalMinutes);
            stormDurationMinutes = Mathf.Max(0.1f, stormDurationMinutes);
            lightningPerMinute = Mathf.Max(0f, lightningPerMinute);

            // The Range attributes only guard the inspector; these are the same
            // limits applied to values arriving from script. Latitude past 90 and
            // tilt past 90 both put the sun somewhere it cannot be, and a negative
            // time scale would run the world backwards through a clock whose
            // accumulator only counts up.
            latitudeDeg = Mathf.Clamp(latitudeDeg, -90f, 90f);
            latitudeSpanDeg = Mathf.Clamp(latitudeSpanDeg, 0f, 180f);
            longitudeDeg = Mathf.Clamp(longitudeDeg, -180f, 180f);
            standardMeridianDeg = Mathf.Clamp(standardMeridianDeg, -180f, 180f);
            axialTiltDeg = Mathf.Clamp(axialTiltDeg, 0f, 90f);
            northBearingDeg = Mathf.Repeat(northBearingDeg, AtmoConstants.DegreesPerCircle);
            timeScale = Mathf.Max(0f, timeScale);
            startDayOfYear = Mathf.Clamp(startDayOfYear, 1, (int)AtmoConstants.DaysPerCalendarYear);
            startHourOfDay = Mathf.Clamp(startHourOfDay, 0f, ClockState.HoursPerDay);

            // A negative swing would put the warmest month below the coldest and
            // invert every seasonal term downstream of it.
            annualTempHalfAmplitudeCelsius = Mathf.Max(0f, annualTempHalfAmplitudeCelsius);
            diurnalTempRangeCelsius = Mathf.Max(0f, diurnalTempRangeCelsius);
            annualPrecipitationMm = Mathf.Max(0f, annualPrecipitationMm);

            if (isActiveAndEnabled) ApplyWindEnabled();

            // Vegetation resolves this world's temperature, lapse rate, rainfall
            // and season from here and then caches the answer. OnValidate is the
            // only signal that any of them moved, so it is where the cache is
            // dropped - otherwise a lapse rate edited here would leave the treeline
            // where the old one put it.
            UCVegetationSystemManager.InvalidateForWorld(transform);
        }

        /// <summary>
        /// Matches the wind manager's enabled state to the toggle above. Disabling
        /// the component rather than destroying it keeps its settings and its baked
        /// sway map, so turning wind back on restores the world it had.
        /// </summary>
        public void ApplyWindEnabled()
        {
            // A wind manager is added only in a live, loaded scene. OnEnable and
            // OnValidate both reach here, and both fire during asset import and while
            // a prefab asset is open, where a structural change (AddComponent) from a
            // lifecycle callback is unsafe and would run weather code where none should.
            // In those contexts an existing manager is still adopted and toggled, but a
            // missing one is left uncreated - creation waits for a live scene: an author
            // toggling wind, the inspector's own button, or the first chunk whose graph
            // needs it.
            bool mayCreate = windEnabled && SafeForStructuralChange();
            UCWindManager w = mayCreate ? EnsureWind() : WindOrNull;
            if (w == null) return;
            if (w.enabled != windEnabled) w.enabled = windEnabled;
        }

        /// <summary>
        /// True only when this component sits in a real, loaded scene, which is the one
        /// context where adding or removing a component from a lifecycle callback is
        /// safe. A prefab asset being edited and an object mid-import both report an
        /// invalid or unloaded scene, so this returns false and no structural change is
        /// attempted there.
        /// </summary>
        private bool SafeForStructuralChange()
        {
            return gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        // =====================================================================
        //  RESOLVED CLIMATE
        //
        //  What every other system reads, so that "how warm is it here" has one
        //  answer. These are pure functions of the authored fields above: no
        //  frame time, no random source, no cached state. That is what lets
        //  vegetation placed from them come out identical on every machine and
        //  after every reload, which is the determinism requirement rather than
        //  a nicety.
        // =====================================================================

        /// <summary>One full turn of a cycle, in radians. A year and a day both use it.</summary>
        private const float Tau = Mathf.PI * 2f;

        /// <summary>degC. Annual mean at this altitude, cooled by the authored lapse rate. (L-04)</summary>
        public float MeanTempAtAltitudeC(float altitudeMetres)
        {
            return EcoMath.LapseAdjustedTempC(temperatureCelsius, altitudeMetres, lapseRatePerKm);
        }

        /// <summary>
        /// degC. Mean of the warmest month at this altitude. The treeline reads
        /// this and not the annual mean, which is why a cold-winter continental
        /// climate still grows forest while a cool maritime one does not. (L-22)
        /// </summary>
        public float WarmestMonthTempC(float altitudeMetres)
        {
            return MeanTempAtAltitudeC(altitudeMetres) + annualTempHalfAmplitudeCelsius;
        }

        /// <summary>degC. Mean of the coldest month at this altitude. Gates evergreen broadleaf and palms.</summary>
        public float ColdestMonthTempC(float altitudeMetres)
        {
            return MeanTempAtAltitudeC(altitudeMetres) - annualTempHalfAmplitudeCelsius;
        }

        /// <summary>
        /// degC at this altitude and point in the year. Phase 0 is midwinter, so
        /// the swing is subtracted at phase 0 rather than added.
        /// </summary>
        public float TempAtSeasonC(float altitudeMetres, float phase)
        {
            float swing = annualTempHalfAmplitudeCelsius * Mathf.Cos(phase * Tau);
            return MeanTempAtAltitudeC(altitudeMetres) - swing;
        }

        /// <summary>degC. Daily minimum, for the evapotranspiration law. (L-13)</summary>
        public float DailyMinTempC(float altitudeMetres, float phase)
        {
            return TempAtSeasonC(altitudeMetres, phase) - diurnalTempRangeCelsius * 0.5f;
        }

        /// <summary>degC. Daily maximum, for the evapotranspiration law. (L-13)</summary>
        public float DailyMaxTempC(float altitudeMetres, float phase)
        {
            return TempAtSeasonC(altitudeMetres, phase) + diurnalTempRangeCelsius * 0.5f;
        }

        /// <summary>0-1 share of the year above freezing at this altitude. (L-26)</summary>
        public float FrostFreeFractionAt(float altitudeMetres)
        {
            return EcoMath.FrostFreeFraction(MeanTempAtAltitudeC(altitudeMetres),
                                             annualTempHalfAmplitudeCelsius);
        }

        /// <summary>
        /// mm/day at this point in the year. The seasonal weighting is a cosine
        /// centred on the wet season whose mean over a full year is exactly 1, so
        /// the annual total is preserved whatever the seasonality is set to -
        /// a monsoon redistributes the year's rain, it does not add any.
        /// </summary>
        public float PrecipitationMmPerDay(float phase)
        {
            float dailyMean = annualPrecipitationMm / EcoConstants.DaysPerTropicalYear;
            float weight = 1f + precipitationSeasonality * Mathf.Cos((phase - wettestSeasonPhase) * Tau);
            return weight > 0f ? dailyMean * weight : 0f;
        }

        // =====================================================================
        //  THE CLIMATE NORMALS REQUEST
        // =====================================================================

        /// <summary>
        /// The authored dials on this component, expressed as the request the
        /// physical climate model takes.
        ///
        /// WHY THE CONVERSION LIVES HERE AND NOT AT THE CALL SITE
        /// Two callers need it - the diagnostic panel now, and the sampler that
        /// will read these normals per chunk later - and if each built its own
        /// request they would eventually disagree about what the continentality
        /// dial means. This component owns the dials, so it owns the translation.
        ///
        /// WHAT THE MODEL DOES NOT TAKE FROM HERE
        /// The mean temperature and the annual swing are absent on purpose. The
        /// model derives both from latitude, altitude and distance from the sea,
        /// and handing it the authored numbers as well would let a world assert
        /// 12 degC of swing at the equator and have the model agree. Where the two
        /// disagree, that disagreement is information: it is the whole reason the
        /// diagnostic exists, and feeding the answer in would waste it.
        ///
        /// Aspect is left at zero. A component describes a world, not a slope; the
        /// per-site aspect warming is applied by whatever samples a position.
        ///
        /// NOTHING IN THE ECOLOGY PATH READS THIS YET. Temperature, rainfall and
        /// radiation still reach the vegetation layer through the resolved
        /// snapshot. Two live sources for one field would be a world whose plants
        /// and whose panel describe different climates.
        /// </summary>
        public ClimateRequest BuildClimateRequest(float altitudeMetres)
        {
            ClimateRequest r = ClimateRequest.Default(latitudeDeg, altitudeMetres,
                                                      annualPrecipitationMm,
                                                      EcoConstants.GddBaseTemperateC);

            r.distanceToCoastM =
                ClimateNormals.CoastDistanceMFromContinentality01(continentality);

            // Humidity is a live authored field and is read. Cloud cover is not:
            // cloudCoverage is still a declared placeholder that nothing renders,
            // and reading it here would make one greyed-out slider quietly change
            // the panel's numbers while its neighbours do nothing. The model's own
            // default stands in until the synoptic layer produces real cloud.
            r.humidity01 = Mathf.Clamp01(humidity);
            r.lapseRateCPerKm = lapseRatePerKm;

            return r;
        }

        /// <summary>
        /// Writes an archetype's geography onto this component - latitude, the maritime
        /// axis, the annual rainfall total and humidity - then lets latitude drive
        /// temperature so the climate follows from the place rather than from a hand-set
        /// mood. Only geography is touched: the author keeps tuning from here and the
        /// existing climate path derives the year. Turbidity and cloud have no live field
        /// yet, so the preset carries them for the sky without asserting them here.
        /// </summary>
        public void ApplyPreset(in WeatherPreset preset)
        {
            latitudeDeg = Mathf.Clamp(preset.latitudeDeg, -90f, 90f);
            continentality = Mathf.Clamp01(preset.continentality01);
            annualPrecipitationMm = Mathf.Max(0f, preset.annualPrecipMm);
            humidity = Mathf.Clamp01(preset.humidity01);
            deriveTemperatureFromLatitude = true;
        }

        /// <summary>Applies a preset chosen by kind. The inspector's preset row calls this.</summary>
        public void ApplyPreset(WeatherPresetKind kind)
        {
            ApplyPreset(WeatherPreset.For(kind));
        }

        /// <summary>
        /// The twelve-month normals for a point at this altitude, built fresh.
        ///
        /// Diagnostic use only for now, and it allocates: the panel that calls it
        /// runs on repaint, not per frame per chunk. When the sampler starts
        /// reading normals it will hold a pooled instance and call Rebuild on it
        /// rather than this.
        /// </summary>
        public ClimateNormals BuildClimateNormals(float altitudeMetres)
        {
            return ClimateNormals.Build(BuildClimateRequest(altitudeMetres));
        }
    }
}
