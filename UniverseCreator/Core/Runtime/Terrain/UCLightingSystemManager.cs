// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCLightingSystemManager.cs
//  Purpose: The LightingSystem child's control surface. Time of Day is live and read
//           from the weather clock; the render dials live in an authored settings block,
//           the performance envelope in a resolved product tier, and the physics owns
//           every value neither of those sets.
//
//  WHAT THIS COMPONENT OWNS, AND WHAT IT DOES NOT
//  ----------------------------------------------
//  Time of Day is read, never authored here. It owns the day half of the world clock
//  and the site geometry - latitude, north bearing, axial tilt - that solar position
//  follows from. The accessors at the bottom of this file turn those three authored
//  numbers into solar declination, day length and the daily radiation total, and the
//  ecology layer derives the world's climate from them. That is the point: climate is
//  computed from astronomy, not authored twice.
//
//  The render look is a LightingSettings block of biases and trims - the numbers an
//  author is allowed to turn. The performance envelope (cascades, shadow distance,
//  screen-space budgets, probe spacing scale) is a LightingTierSettings resolved from
//  the one product tier, not a second set of dials. Everything else - sun intensity and
//  colour across the day, ambient, exposure - is computed by the atmosphere physics from
//  the sun's position and the air, so it is not restated as a field here. The fields the
//  retired build declared for those were placeholders; they are gone, not migrated,
//  because a dial that shadows a physical value is the lie that costs someone an
//  afternoon.
//
//  THE CONSTRAINTS THE RENDERING PASS INHERITS
//  -------------------------------------------
//   - URP only. Every shader and every render feature the toolkit ships targets
//     the Universal Render Pipeline. A lighting pass must not reach for built-in
//     or HDRP-only API.
//   - The sun is one directional light, owned here. Water, vegetation and terrain
//     all read the same one; a second sun would double-light the world.
//   - Time of day drives the sun rather than the other way round, so a saved world
//     reopened at the same time of day looks the same.
//   - Weather owns cloud and fog. Lighting reads them. Overcast dimming belongs to
//     whichever of the two runs later, not to both.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level lighting and time-of-day control. Time of Day is live and owns
    /// the world's site geometry; the rendering groups are declared for a later
    /// module.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Lighting System Manager")]
    [DisallowMultipleComponent]
    public class UCLightingSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  TIME OF DAY  -  RESOLVED, NOT AUTHORED  (W-04)
        //
        //  This group used to author the day: hour, day length, latitude, north
        //  bearing and axial tilt all lived here, on the reasoning that the sun
        //  rig is what consumes them. That was the wrong owner, and it is worth
        //  recording why so it is not put back.
        //
        //  Latitude is not a lighting property. It sets the Coriolis sign, the
        //  temperature gradient toward the pole, and which way a warm hillside
        //  faces. Two copies of it give a world with a northern-hemisphere sun
        //  over southern-hemisphere forest, and nothing in the scene says why.
        //  The same goes for the hour: two components advancing time means two
        //  clocks, and the moment they differ by a frame the sky and the
        //  phenology are in different hours.
        //
        //  So UCWeatherSystemManager owns the clock and the site, and this
        //  component reads it. The properties below are get-only on purpose: an
        //  author edits them on the WeatherSystem child, and there is no second
        //  place where a stale value can hide.
        // =====================================================================

        [Header("Time of Day  (read from the weather system's clock)")]

        [Tooltip("Master switch for the lighting pass.")]
        public bool lightingEnabled = true;

        /// <summary>
        /// The world clock. The weather manager's if this world has one, otherwise
        /// the documented default of latitude 45, longitude 0, midsummer noon - and
        /// the inspector says which. (W-04)
        /// </summary>
        public ClockState Clock
        {
            get { return UCWeatherSystemManager.ResolveClockFor(transform); }
        }

        /// <summary>
        /// True when this world has a weather system to read the clock from. False
        /// means every value below is the documented fallback.
        /// </summary>
        public bool HasClockSource
        {
            get { return UCWeatherSystemManager.FindForWorld(transform) != null; }
        }

        /// <summary>h. Clock hour, 0 to 24. Resolved, not authored.</summary>
        public float TimeOfDayHours
        {
            get { return Clock.localHour; }
        }

        /// <summary>
        /// deg. Latitude of this world, negative south. Resolved from the weather
        /// system, which is its only owner.
        /// </summary>
        public float LatitudeDegrees
        {
            get { return Clock.latitudeDeg; }
        }

        /// <summary>deg. Axial tilt of this world. Resolved, not authored.</summary>
        public float AxialTiltDegrees
        {
            get { return UCWeatherSystemManager.ResolveAxialTiltDeg(transform); }
        }

        /// <summary>deg. Compass bearing of world north. Resolved, not authored.</summary>
        public float NorthBearingDegrees
        {
            get { return UCWeatherSystemManager.ResolveNorthBearingDeg(transform); }
        }

        // =====================================================================
        //  SUN AND MOON
        //
        //  The one directional light, and whether the moon lights the night. The
        //  sun's intensity and colour across the day are not fields: they are the
        //  atmosphere physics reading the sun's own position and the air, so a value
        //  here would only ever disagree with the sky. The moon's authored trim
        //  lives on the settings block below, with the rest of the render dials.
        // =====================================================================

        [Header("Sun and Moon")]

        [Tooltip("The one directional light this world is lit by. Left empty, the module " +
                 "adopts the scene's sun rather than adding a second one.")]
        public Light sun;

        [Tooltip("The moon lights the world after sunset. Its brightness is the moonIntensity " +
                 "trim on the lighting settings below, on the physical full-moon reference.")]
        public bool moonEnabled = true;

        // =====================================================================
        //  LIGHTING SETTINGS  -  THE AUTHORED DIALS
        //
        //  Every render value an author is allowed to turn, in one serialised block
        //  whose defaults track the physical constants. The driver reads it and pushes
        //  each dial to the sky model, the probe grid and the screen-space features.
        // =====================================================================

        [Header("Lighting Settings")]

        [Tooltip("The authored render dials - sky, moon trim, irradiance grid, screen-space, " +
                 "clouds, discs, night floor and exposure mode. Each is a bias or trim on a " +
                 "value the physics already computed, never a replacement for it.")]
        public Lighting.LightingSettings settings = new Lighting.LightingSettings();

        // =====================================================================
        //  TIER AND PERFORMANCE
        //
        //  The performance envelope is one product tier, not a second set of dials.
        //  Follow the world's AdaptiveQuality so lighting scales with everything
        //  else, or pin a tier for a fixed target. LightingTier resolves either to
        //  the cascades, shadow distance, screen-space budgets and probe spacing the
        //  frame is actually built with.
        // =====================================================================

        [Header("Tier and Performance")]

        [Tooltip("Resolve the lighting tier from the world's AdaptiveQuality, so lighting " +
                 "scales with vegetation, props and effects. Off pins the override below.")]
        public bool followAdaptiveQuality = true;

        [Tooltip("The tier used when not following AdaptiveQuality. Ultra is the richest, " +
                 "Low the cheapest.")]
        public AdaptiveQuality.QualityTier lightingTierOverride = AdaptiveQuality.QualityTier.High;

        // =====================================================================
        //  DEBUG VIEWS  (S-53)
        // =====================================================================

        [Header("Debug Views")]

        [Tooltip("Off ships the lit world. The other modes visualise one term - probe " +
                 "positions or colours, ambient, direct, AO, SSGI, cloud shadow, exposure, or " +
                 "the tier in force - for diagnosis, never for a shipped frame.")]
        public LightingDebug.DebugMode debugView = LightingDebug.DebugMode.Off;

        /// <summary>The world this lighting belongs to, or null when loose in a scene.</summary>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        // =====================================================================
        //  RESOLVED TIER AND IRRADIANCE COST
        //
        //  Read-only, deterministic, and derived on demand - no cached state, so the
        //  inspector shows what the frame would actually build.
        // =====================================================================

        /// <summary>
        /// The product tier lighting resolves to right now: the world's AdaptiveQuality
        /// tier when following it, otherwise the pinned override. Falls back to the
        /// override when no AdaptiveQuality is present, so a loose scene still resolves.
        /// </summary>
        public AdaptiveQuality.QualityTier ResolvedTier
        {
            get
            {
                if (followAdaptiveQuality)
                {
                    AdaptiveQuality aq = GetComponentInParent<AdaptiveQuality>();
                    if (aq != null) return aq.CurrentTier;
                }
                return lightingTierOverride;
            }
        }

        /// <summary>The full tier envelope for <see cref="ResolvedTier"/> - cascades,
        /// shadow distance, screen-space budgets and probe spacing scale.</summary>
        public LightingTierSettings ResolvedTierSettings
        {
            get { return LightingTier.Resolve(ResolvedTier); }
        }

        /// <summary>
        /// Probes the irradiance grid would allocate over this world, at the authored
        /// horizontal spacing scaled by the resolved tier, times the fixed vertical
        /// levels. A pure function of the world size and those two numbers - no bake,
        /// no random - so the count is exact before a probe is filled. Zero when the
        /// component is loose in a scene with no world to size against. (§8.8)
        /// </summary>
        public long EstimatedProbeCount
        {
            get
            {
                UniverseCreatorObject w = World;
                if (w == null) return 0L;

                Vector3 size = w.GetWorldSize();
                float spacing = settings.probeSpacing * ResolvedTierSettings.probeSpacingScale;

                long across = IrradianceGrid.ProbesAcross(size.x, spacing);
                long deep   = IrradianceGrid.ProbesAcross(size.z, spacing);
                long levels = Mathf.Max(1, LightingConstants.IrradianceVerticalLevels);
                return across * deep * levels;
            }
        }

        /// <summary>
        /// GPU bytes the irradiance grid would upload for this world - <see
        /// cref="EstimatedProbeCount"/> priced by the upload format's own per-probe cost,
        /// so the driver's packing and this preview can never disagree on the figure.
        /// </summary>
        public long EstimatedIrradianceBytes
        {
            get { return IrradianceUpload.GpuBytes(EstimatedProbeCount); }
        }

        private void OnValidate()
        {
            // The render dials clamp themselves to the ranges their tooltips promise.
            settings.ClampToValidRanges();

            // Latitude, tilt and the hour are clamped where they are authored, on
            // the weather system. Clamping a copy of them here is what a second
            // owner looks like before anyone notices.

            // Vegetation resolves latitude, tilt, day length and radiation from
            // here and then caches the answer. OnValidate is the only signal that
            // any of them moved, so it is where the cache is dropped.
            UCVegetationSystemManager.InvalidateForWorld(transform);

            // Republish the one shader global on an inspector change. Update() no longer
            // ticks it every frame in edit mode (§36), so OnEnable + OnValidate are what
            // keep _UCEnvReflectionStrength correct in the scene view when the tier moves.
            if (isActiveAndEnabled) PublishEnvReflectionStrength();
        }

        // =====================================================================
        //  THE ONE SHADER GLOBAL THIS MANAGER PUBLISHES  (S-82)
        //
        //  The quality ladder resolves how strongly the ray-free wet-sky reflection
        //  (S-79/S-81) contributes, and the shaders read it from _UCEnvReflectionStrength.
        //  This component owns the tier, so it is the single publisher of that value: 0
        //  on the T0/T1 rung (Low / Mobile Low) leaves the term off, and the T1.5 rung and
        //  up (Mobile High and up) scale it in. Reset to 0 when the manager is disabled so
        //  a torn-down world never leaves a stale reflection lit on the next scene.
        // =====================================================================

        private static readonly int s_EnvReflectionStrengthId =
            Shader.PropertyToID("_UCEnvReflectionStrength");

        private void OnEnable()  { PublishEnvReflectionStrength(); }

        // Play mode only. This is [ExecuteAlways], so an ungated Update() ran on every
        // scene-view repaint in edit mode - forbidden by the author ("in sceneview which
        // is the edit mode never run execute always"). The value only changes when the
        // tier changes, so OnEnable/OnValidate publish it once and it stays correct
        // across a domain reload without a per-frame heartbeat keeping the editor update
        // loop hot. (§36)
        private void Update()
        {
            if (!Application.isPlaying) return;
            PublishEnvReflectionStrength();
        }

        private void OnDisable() { Shader.SetGlobalFloat(s_EnvReflectionStrengthId, 0f); }

        /// <summary>
        /// Push the resolved tier's reflection strength to the shader global, or 0 when the
        /// lighting pass is switched off. Reads the tier through <see cref="ResolvedTierSettings"/>,
        /// so it can never disagree with what the inspector and the self-test report.
        /// </summary>
        private void PublishEnvReflectionStrength()
        {
            float strength = lightingEnabled ? ResolvedTierSettings.envReflectionStrength : 0f;
            Shader.SetGlobalFloat(s_EnvReflectionStrengthId, strength);
        }

        // =====================================================================
        //  RESOLVED SOLAR GEOMETRY
        //
        //  Pure functions of latitude, axial tilt and the point in the year. No
        //  frame time, no random source, no cached state - so a world's climate,
        //  and the vegetation derived from it, comes out identical on every
        //  machine and after every reload.
        //
        //  The season phase argument is the weather manager's, on the same
        //  convention: 0 is northern midwinter, 0.5 northern midsummer.
        // =====================================================================

        /// <summary>
        /// deg. Solar declination - the latitude the sun stands directly over at
        /// this point in the year. Runs from minus the axial tilt at midwinter to
        /// plus it at midsummer, which is the entire mechanism of the seasons. A
        /// world with no tilt holds this at zero and has none. (L-01)
        ///
        /// The formula lives in ClockMath (W-02), not here. It used to be written out
        /// twice - once in this file and once in the weather layer - and two copies
        /// of one formula is not redundancy: one of them eventually gets a fix the
        /// other does not, and then the sky and the vegetation disagree about what
        /// season it is. This forwards.
        /// </summary>
        public float SolarDeclinationDegrees(float seasonPhase)
        {
            return ClockMath.DeclinationDegFromWinterPhase01(seasonPhase, AxialTiltDegrees);
        }

        /// <summary>
        /// h. Hours between sunrise and sunset at this latitude and season. Returns a
        /// full 24 in polar day and 0 in polar night rather than failing, because
        /// beyond the circles the sun genuinely does not set or rise. (L-03)
        ///
        /// Forwards to ClockMath for the same single-owner reason as the declination
        /// above, and gains its polar-latitude tan guard by doing so.
        /// </summary>
        public float DayLengthHours(float seasonPhase)
        {
            return ClockMath.DayLengthHours(LatitudeDegrees, SolarDeclinationDegrees(seasonPhase));
        }

        /// <summary>
        /// MJ/m2/day arriving at the top of the atmosphere. The evapotranspiration
        /// law reads this, which is what lets a world's water balance follow from
        /// its astronomy instead of from an authored guess: the same annual
        /// rainfall is a desert at one latitude and a wetland at another because
        /// this number differs. (L-13)
        ///
        /// The FAO-56 formula lives in ClockMath (W-05), not here, for the same
        /// single-owner reason as the declination and day length above - and because
        /// a world that spans latitude re-derives this on a worker thread, which
        /// cannot reach a component at all. This forwards.
        /// </summary>
        public float ExtraterrestrialRadiationMjPerM2Day(float seasonPhase)
        {
            return ClockMath.ExtraterrestrialRadiationMjPerM2Day(
                LatitudeDegrees,
                SolarDeclinationDegrees(seasonPhase),
                seasonPhase);
        }

        /// <summary>
        /// deg. Compass azimuth of the warm slope here. The warm slope is the one
        /// facing the equator, so it is south-facing in the northern hemisphere
        /// and north-facing in the southern. Reading it from latitude rather than
        /// hard-coding south is what keeps every hillside in a southern world from
        /// coming out inverted.
        /// </summary>
        public float WarmAspectDegrees
        {
            get { return TopographyMath.WarmAspectDegFor(LatitudeDegrees); }
        }
    }
}
