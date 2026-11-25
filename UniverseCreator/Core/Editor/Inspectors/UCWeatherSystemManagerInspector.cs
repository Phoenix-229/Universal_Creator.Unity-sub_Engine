// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWeatherSystemManagerInspector.cs
//  Purpose: Draws the weather manager, separating the part that works from the
//           part that does not.
//
//  WHY THE SPLIT IS DRAWN SO LOUDLY
//  --------------------------------
//  UC-T08-05: wind, climate and supported regional environment controls are live.
//  Remaining unsupported cloud/accumulation/storm controls stay disabled with
//  explicit diagnostics until their later Topic 8 task connects them.
//
//  So the placeholder sections are collapsed by default, drawn disabled, and say
//  what they are. The wind section is open, live, and links straight to the
//  component that actually does the work.
//
//  CLIMATE MOVED OUT OF THE PLANNED BLOCK
//  --------------------------------------
//  Climate and Season used to fall into the disabled block by default, because
//  that block is drawn with DrawPropertiesExcluding and anything not named in the
//  exclusion list lands in it. The ecology layer reads those twelve fields now, so
//  leaving them greyed out would lock an author out of the numbers that decide
//  where their treeline lands. They are named in the exclusion list and drawn in
//  their own live section, with a readout of what they come to.
//
//  Any field appended to this component in future lands in the planned block by
//  default, which is the safe direction to fail: greyed and visible, never hidden.
//
//  NO DIALOGS.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(UCWeatherSystemManager))]
    public class UCWeatherSystemManagerInspector : UnityEditor.Editor
    {
        private static bool windFoldout = true;
        private static bool climateFoldout = true;
        private static bool presetsFoldout;
        private static bool plannedFoldout;
        private static bool environmentFoldout = true; // UC-T08-05: supported runtime controls are no longer trapped in the disabled planned block.

        /// <summary>The archetype the presets row currently offers to apply.</summary>
        private static WeatherPresetKind presetChoice = WeatherPresetKind.TemperateOceanic;

        /// <summary>
        /// The fields drawn live above, and so excluded from the planned block.
        /// Kept as one list because the two places that need it must not drift:
        /// a name here but not in the live section vanishes from the inspector.
        /// </summary>
        private static readonly string[] LiveFields =
        {
            "m_Script", "windEnabled", "wind",
            "temperatureCelsius", "lapseRatePerKm", "humidity",
            "annualTempHalfAmplitudeCelsius", "diurnalTempRangeCelsius",
            "continentality", "annualPrecipitationMm",
            "precipitationSeasonality", "wettestSeasonPhase",
            "deriveTemperatureFromLatitude", "equatorTemperatureCelsius",
            "pauseTime", "timeScale", "startDayOfYear", "startHourOfDay",
            "latitudeDeg", "latitudeSpanDeg", "longitudeDeg", "standardMeridianDeg",
            "clockReferenceMeridianDeg", "longitudeSpanDeg",
            "axialTiltDeg", "northBearingDeg",
            "enableRegionalWeather", "regionalWeatherSeed", "regionalSampleIntervalSeconds", "regionalSampleSpacingMetres", "enableLiveWeatherSim", "weatherDrivesWind",
            "cloudCoverage", "rainIntensity", "snowIntensity", "surfaceWetness", "fogDensity", "visibilityMetres",
            "simulateSurfaceResponse", "surfaceExposure01", "dryingSeconds"
        };

        private UnityEditor.Editor windEditor;

        private UCWeatherSystemManager Manager { get { return (UCWeatherSystemManager)target; } }

        private void OnDisable()
        {
            if (Utilities.WeatherEditModeDriver.PreviewOwner == Manager) Utilities.WeatherEditModeDriver.StopPreview(); // UC-T08-15: closing this inspector retires its temporary preview.
            // The nested editor is a real Object; leaking one per selection change
            // shows up as a slow drip of leaked editors in the profiler.
            if (windEditor != null) DestroyImmediate(windEditor);
            windEditor = null;
        }

        /// <summary>Shows graph assignment for an unconfigured owning world; otherwise exposes the saved weather and clock controls.</summary>
        public override void OnInspectorGUI()
        {
            if (UCInspectorUI.DrawMissingTerrainGraph(targets)) return; // UC repair 31: attached worlds require a terrain graph.
            UCWeatherSystemManager manager = Manager;
            if (manager == null) return;

            UCInspectorUI.HeaderBar("Weather System", "d_CloudConnect",
                                    UCInspectorUI.HeaderTeal, StatusText(manager));

            EditorGUILayout.Space(4);

            serializedObject.Update();

            windFoldout = Group(windFoldout, "Wind  (live)", UCInspectorUI.HeaderGreen);
            if (windFoldout) DrawWind(manager);

            EditorGUILayout.Space(4);

            climateFoldout = Group(climateFoldout, "Climate and Season  (live)",
                                   UCInspectorUI.HeaderGreen);
            if (climateFoldout) DrawClimate(manager);

            EditorGUILayout.Space(4);

            presetsFoldout = Group(presetsFoldout, "Presets  (geography)",
                                   UCInspectorUI.HeaderGreen);
            if (presetsFoldout) DrawPresets(manager);

            EditorGUILayout.Space(4);
            environmentFoldout = Group(environmentFoldout, "Runtime Environment", UCInspectorUI.HeaderGreen);
            if (environmentFoldout)
            {
                foreach (string name in new[] { "enableRegionalWeather", "regionalWeatherSeed", "regionalSampleIntervalSeconds", "regionalSampleSpacingMetres", "enableLiveWeatherSim", "weatherDrivesWind",
                    "cloudCoverage", "rainIntensity", "snowIntensity", "surfaceWetness", "fogDensity", "visibilityMetres",
                    "simulateSurfaceResponse", "surfaceExposure01", "dryingSeconds" })
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(name));
                EditorGUILayout.HelpBox("Lighting Graph fields apply explicitly. Camera-near emitters read their bound world's rain/snow. Cameras inside this world, or the camera assigned as the World Graph Bindings observer, receive local visibility, wind and wetness; up to eight camera bindings are supported. Live weather edits refresh appearance without rebaking placement.", MessageType.Info);
                if (!string.IsNullOrEmpty(manager.EnvironmentViewDiagnostic)) EditorGUILayout.HelpBox(manager.EnvironmentViewDiagnostic, MessageType.Warning);
                EditorGUILayout.LabelField("Regional endpoint evaluations", manager.RegionalEndpointSamples.ToString()); // UC-T08-06: bounded cache diagnostics, never a claim of measured frame cost.
                EditorGUILayout.LabelField("Stored surface cells (max 128)", manager.CachedSurfaceCells.ToString()); // UC-T08-09: sparse local storage, not a continent archive.
                if (manager.simulateSurfaceResponse) EditorGUILayout.HelpBox("Surface stores use simulated seconds and reset on seeks or observation gaps over one hour. Exposure is authored; no automatic shelter geometry. Accepted river containment excludes submerged snow. Terrain materials need their existing Snow option enabled.", MessageType.Info);
                var windOwner = manager.WindOrNull;
                if (windOwner != null && windOwner.driveWindZone)
                    EditorGUILayout.HelpBox("Unity's native tree wind affects the whole scene. Independent tree winds across different worlds require UC vegetation shaders with this native integration disabled.", MessageType.Info);
            }

            EditorGUILayout.Space(4);

            plannedFoldout = Group(plannedFoldout, "Planned  (not implemented)",
                                   UCInspectorUI.HeaderGrey);
            if (plannedFoldout) DrawPlanned();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawWind(UCWeatherSystemManager manager)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("windEnabled"));

            UCWindManager wind = manager.WindOrNull;

            if (wind == null)
            {
                EditorGUILayout.HelpBox(
                    "No wind manager yet. It is created the moment wind is needed - " +
                    "either by enabling it here, or by the first chunk whose graph has " +
                    "a Wind Output node.", MessageType.Info);

                if (ActionButton("Create Wind Manager", UCInspectorUI.ButtonGreen))
                {
                    Undo.RegisterCompleteObjectUndo(manager.gameObject, "Create wind manager");
                    manager.EnsureWind();
                    EditorUtility.SetDirty(manager.gameObject);
                }
                return;
            }

            if (wind.gameObject != manager.gameObject)
                EditorGUILayout.HelpBox(
                    "The wind manager lives on '" + wind.gameObject.name + "' rather than " +
                    "here, because this world was built before the weather system existed. " +
                    "It still works and it is still edited from here; moving it would " +
                    "discard the sway map the graph has already baked into it.",
                    MessageType.Info);

            if (!manager.windEnabled)
                EditorGUILayout.HelpBox(
                    "Wind is off, so grass, trees and water are still. The settings below " +
                    "are kept - turning wind back on restores them.", MessageType.None);

            EditorGUILayout.Space(4);

            // The wind manager's own inspector, drawn inline. Better than a list of
            // proxy fields: whatever the wind system gains later shows up here with
            // no change to this file, and there is exactly one place the values live.
            CreateCachedEditor(wind, null, ref windEditor);
            if (windEditor != null)
            {
                using (new EditorGUI.DisabledScope(!manager.windEnabled))
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    windEditor.OnInspectorGUI();
                    EditorGUILayout.EndVertical();
                }
            }
        }

        /// <summary>
        /// The world's climate reference, and what it comes to. The readout is the
        /// half that matters: the authored numbers are inputs, and the treeline
        /// altitude is the thing the author is actually trying to place.
        /// </summary>
        private void DrawClimate(UCWeatherSystemManager manager)
        {
            EditorGUILayout.HelpBox(
                "Read by the ecology layer. These decide the world's temperature, its " +
                "water balance and where the treeline falls, and they are the only " +
                "copy - vegetation reads them from here rather than declaring its own.",
                MessageType.None);

            EditorGUILayout.LabelField("Temperature", EditorStyles.boldLabel);
            Field("temperatureCelsius");

            // Live since V-26, but it was not in the drawn list, so it had been
            // appearing under "planned" with the placeholders - which reads as a
            // promise that it does nothing yet, when in fact it takes over the
            // temperature above.
            Field("deriveTemperatureFromLatitude");
            Field("equatorTemperatureCelsius");

            Field("lapseRatePerKm");
            Field("annualTempHalfAmplitudeCelsius");
            Field("diurnalTempRangeCelsius");
            Field("continentality");

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("Water", EditorStyles.boldLabel);
            Field("humidity");
            Field("annualPrecipitationMm");
            Field("precipitationSeasonality");
            Field("wettestSeasonPhase");

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("World clock", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "The only clock and the only latitude in the world. Lighting and " +
                "vegetation read these rather than carrying their own, which is what " +
                "stops a world ending up with its sun in one hemisphere and its " +
                "forest in the other.",
                MessageType.None);

            // Resolved before the sliders rather than after them, because the scrub
            // row below cannot offer a sunrise without first knowing where the sun
            // is today. One resolve feeds the sliders, the scrub row and the
            // readouts further down, so the three cannot disagree within a frame.
            ClockState clock = manager.ResolveClock();

            Field("pauseTime");
            Field("timeScale");

            // UC-T08-15: day duration edits the existing rate, rather than storing a competing day-length authority.
            float dayMinutes = 1440f / Mathf.Max(0.0001f, serializedObject.FindProperty("timeScale").floatValue);
            float requestedMinutes = EditorGUILayout.DelayedFloatField(new GUIContent("Game day (real minutes)",
                "Duration of 24 in-game hours in real minutes. Sets Time Scale = 1440 / minutes; independent of your computer's clock."), dayMinutes);
            if (requestedMinutes != dayMinutes && requestedMinutes > 0 && !float.IsNaN(requestedMinutes) && !float.IsInfinity(requestedMinutes))
            {
                float rate = 1440f / requestedMinutes;
                if (!float.IsInfinity(rate) && rate > 0) serializedObject.FindProperty("timeScale").floatValue = rate;
            }
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                bool previewing = Utilities.WeatherEditModeDriver.PreviewOwner == manager;
                if (GUILayout.Button(previewing ? "Stop Scene Day/Night Preview (restore time)" : "Start Scene Day/Night Preview"))
                {
                    serializedObject.ApplyModifiedProperties();
                    if (previewing) Utilities.WeatherEditModeDriver.StopPreview();
                    else Utilities.WeatherEditModeDriver.StartPreview(manager);
                    serializedObject.Update();
                }
            }
            EditorGUILayout.HelpBox("Scene preview uses the world's Lighting System, temporarily enables Scene lighting and skybox, and restores those settings and its starting time when stopped, saved or entering Play mode. In Play mode, Pause Time and Time Scale control gameplay. A separate always-on Directional Light can keep nights bright; assign your intended daylight source in LightingSystem > Time and Sun > Sun.", MessageType.Info);

            Field("startDayOfYear");

            // UC-T08-02: calendar seeks use the same serialized owner and preserve the year during sunrise/noon scrubbing.
            int calendarYear = EditorGUILayout.DelayedIntField(new GUIContent("Calendar year", "365-day model calendar, years 1…9999; civil display and solar geography share one instant."), clock.year);
            if (calendarYear != clock.year && calendarYear >= 1 && calendarYear <= UCWeatherSystemManager.MaximumCalendarYear)
            {
                serializedObject.ApplyModifiedProperties(); Undo.RecordObject(manager, "Set world calendar year");
                manager.SetCalendarTime(calendarYear, manager.DayOfYear, manager.LocalHour); EditorUtility.SetDirty(manager); serializedObject.Update();
            }

            // The phase is the number the runtime reads; the name is here because
            // nobody can tell at a glance that 0.62 is autumn. The hemisphere is
            // named alongside it for the one case that reads as a bug and is not:
            // at latitude -45 day 172 is midwinter, and an author who does not see
            // "southern" there goes looking for a fault that is not present.
            Readout("Season", clock.season01.ToString("0.00") + "  -  " +
                    SeasonLabelOf(clock.season01) +
                    (clock.IsSouthernHemisphere ? "  (southern hemisphere)" : ""));

            Field("startHourOfDay");

            DrawClockScrubRow(manager, clock);

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("Site", EditorStyles.boldLabel);
            Field("latitudeDeg");
            Field("latitudeSpanDeg");
            Field("longitudeSpanDeg"); // UC-T08-10: explicit planar camera geography, zero spans retain a single-site sky.
            Field("clockReferenceMeridianDeg");
            Readout("Civil display hour", manager.CivilDisplayHour.ToString("0.00"));

            // W-05. Reported because the span is clamped at the poles: an author who
            // asks for 120 degrees around latitude 60 gets an arctic north edge and no
            // warning otherwise, and the two numbers that actually reach the ecology
            // are these. Shown only when the gradient is on, so a world using one
            // latitude keeps an uncluttered panel.
            if (manager.latitudeSpanDeg > 0f)
            {
                float south = ClockMath.LatitudeAt(manager.latitudeDeg,
                                                   manager.latitudeSpanDeg, 0f);
                float north = ClockMath.LatitudeAt(manager.latitudeDeg,
                                                   manager.latitudeSpanDeg, 1f);

                Readout("Latitude at edges",
                        south.ToString("0.0") + " deg south edge  ->  " +
                        north.ToString("0.0") + " deg north edge");
            }

            Field("longitudeDeg");
            Field("standardMeridianDeg");
            Field("axialTiltDeg");
            Field("northBearingDeg");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Resolved clock", EditorStyles.boldLabel);

            Readout("Date", "year " + clock.year + ", day " + clock.dayOfYear);
            Readout("Local time", HourMinute(clock.localHour));
            Readout("Solar noon", HourMinute(clock.solarNoonHour));
            Readout("Sun elevation / azimuth",
                    clock.elevationDeg.ToString("0.0") + " / " +
                    clock.azimuthDeg.ToString("0.0") + " deg" +
                    (clock.SunIsUp ? "" : "  (below the horizon)"));
            Readout("Declination", clock.declinationDeg.ToString("0.0") + " deg");
            Readout("Equation of time", clock.equationOfTimeMin.ToString("0.0") + " min");
            Readout("Moon", (clock.lunarIlluminatedFraction * 100f).ToString("0") +
                    " % lit, phase " + clock.lunarPhase01.ToString("0.00") +
                    "  (" + MoonPhaseLabelOf(clock.lunarPhase01) + ")");

            // W-07. Where it is, not just how lit it is. Both are needed to read a
            // night: a full moon below the horizon lights nothing, and the pair of
            // rows together is what tells an author whether a dark night is the
            // moon's phase or the moon's absence.
            Readout("Moon position", clock.MoonIsUp
                ? clock.lunarElevationDeg.ToString("0.0") + " deg up, bearing "
                  + clock.lunarAzimuthDeg.ToString("0") + " deg"
                : "below the horizon (" +
                  clock.lunarElevationDeg.ToString("0.0") + " deg)");

            // Reported rather than authored: at one time scale a day and a year are
            // both fixed, so a second knob for either could only disagree with this.
            Readout("A day takes",
                    (manager.RealSecondsPerDay / 60f).ToString("0.0") + " real minutes");
            Readout("A year takes",
                    manager.RealMinutesPerYear.ToString("0") + " real minutes");

            if (manager.pauseTime)
            {
                EditorGUILayout.HelpBox(
                    "Time is paused, so the sun, sky, weather and phenology are all " +
                    "held at the start day and hour. Drag them to scrub.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Resolved at sea level", EditorStyles.boldLabel);

            Readout("Warmest month", manager.WarmestMonthTempC(0f).ToString("0.0") + " C");
            Readout("Coldest month", manager.ColdestMonthTempC(0f).ToString("0.0") + " C");
            Readout("Frost-free share of year",
                    (manager.FrostFreeFractionAt(0f) * 100f).ToString("0") + " %");
            Readout("Rain today",
                    manager.PrecipitationMmPerDay(manager.seasonPhase).ToString("0.0") + " mm/day");

            // Where the warmest month falls to the treeline threshold. Solved for
            // rather than searched, since the lapse rate is linear in altitude.
            if (manager.lapseRatePerKm > 0f)
            {
                float headroomC = manager.temperatureCelsius
                                  + manager.annualTempHalfAmplitudeCelsius
                                  - EcoConstants.TreelineWarmestMonthC;
                float treelineM = headroomC * EcoConstants.MetresPerKilometre
                                  / manager.lapseRatePerKm;

                Readout("Treeline", treelineM <= 0f
                    ? "at sea level - too cold for trees anywhere"
                    : treelineM.ToString("0") + " m above sea level");
            }

            DrawKoeppenCrossCheck(manager);
        }

        /// <summary>
        /// The climate this world's latitude, altitude, rainfall and distance from
        /// the sea actually describe, as a Koeppen code - and next to it, the
        /// numbers the sliders above assert.
        ///
        /// THIS PANEL IS AN INSTRUMENT, NOT A CONTROL. Nothing in the runtime reads
        /// the code. Its whole job is to be disagreed with: an author who has built
        /// a spruce forest and is reading "Af" here has a world whose numbers do not
        /// mean what they think, and finding that out from one line of text is
        /// cheaper than finding it out from a hillside of dead trees.
        ///
        /// The modelled mean is shown beside the authored one for the same reason.
        /// They are allowed to differ - a world may be deliberately warmer than
        /// Earth at its latitude - but a difference of twenty degrees is somebody
        /// having typed a temperature and forgotten the latitude, and there is
        /// otherwise nowhere in the product that would say so.
        /// </summary>
        private void DrawKoeppenCrossCheck(UCWeatherSystemManager manager)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Climate cross-check  (read-only)",
                                       EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Derived from latitude, altitude, rainfall and distance from the " +
                "sea by the physical model, not from the sliders above. Nothing in " +
                "the world reads it. It is here to be compared with what you " +
                "intended - if the code and your planting disagree, the numbers " +
                "above are wrong.",
                MessageType.None);

            ClimateNormals normals = manager.BuildClimateNormals(0f);
            KoeppenResult koeppen = KoeppenCode.Classify(normals);

            Readout("Koeppen", koeppen.code + "  -  " + koeppen.name);
            Readout("Derived rain regime", normals.regime.ToString());

            float authored = manager.MeanTempAtAltitudeC(0f);
            float delta = normals.meanAnnualTempC - authored;

            Readout("Mean, modelled / authored",
                    normals.meanAnnualTempC.ToString("0.0") + " / " +
                    authored.ToString("0.0") + " C" +
                    (Mathf.Abs(delta) < KoeppenAgreementC
                        ? "  (agree)"
                        : "  (" + (delta > 0f ? "+" : "") + delta.ToString("0.0") +
                          " C - latitude and the typed temperature disagree)"));

            Readout("Modelled warmest / coldest month",
                    normals.warmestMonthTempC.ToString("0.0") + " / " +
                    normals.coldestMonthTempC.ToString("0.0") + " C" +
                    "  (swing " + normals.AnnualRangeC.ToString("0.0") + " C)");

            Readout("Modelled PET / aridity",
                    normals.annualPetMm.ToString("0") + " mm/yr  -  P/PET " +
                    normals.aridity.ToString("0.00"));

            // The dry threshold is the one number that turns rainfall into a class,
            // and it moves with temperature and with WHEN the rain falls - so a site
            // can be shown how far it is from being a steppe without the author
            // having to know the rule.
            Readout("Dry-climate threshold",
                    koeppen.aridityThresholdMm.ToString("0") + " mm/yr  -  this world has " +
                    normals.annualPrecipMm.ToString("0") +
                    (normals.annualPrecipMm < koeppen.aridityThresholdMm
                        ? "  (below: dry climate)" : "  (above: not dry)"));

            Readout("Wettest / driest month",
                    koeppen.wettestMonthMm.ToString("0") + " / " +
                    koeppen.driestMonthMm.ToString("0") + " mm  -  " +
                    (koeppen.warmHalfPrecipShare01 * 100f).ToString("0") +
                    " % of the year's rain in its warm half");
        }

        /// <summary>
        /// degC. Below this the modelled and authored means are reported as
        /// agreeing. Half a degree: tighter and rounding alone would light it up.
        /// </summary>
        private const float KoeppenAgreementC = 0.5f;

        /// <summary>
        /// Where today's sun crossings fall, and the four buttons that jump the clock
        /// to them.
        ///
        /// EVERY HOUR HERE IS COMPUTED, NONE IS TYPED. Sunrise is solar noon minus
        /// half the day length, and solar noon already carries the longitude offset
        /// and the equation of time - so the hour a jump lands on moves with the date,
        /// the latitude and the distance from the standard meridian. A button that
        /// jumped to a flat 06:00 would be correct on an equinox at the equator and
        /// visibly wrong on every other day at every other latitude, and it would
        /// read as a broken sun rather than as a broken button.
        ///
        /// Inside a polar night, and equally under a midnight sun, there is no
        /// crossing to jump to. Those two buttons are disabled and the reason is
        /// stated, because a button that silently does nothing reads as a dead panel.
        /// Solar noon and midnight stay live: both are defined every day of the year,
        /// including the days the sun never moves across the horizon.
        /// </summary>
        private void DrawClockScrubRow(UCWeatherSystemManager manager, ClockState clock)
        {
            bool hasCrossings = clock.HasSunriseToday;

            Readout("Sunrise / sunset", hasCrossings
                ? HourMinute(clock.SunriseHour) + "  ->  " + HourMinute(clock.SunsetHour)
                : clock.IsPolarNight ? "no sunrise today - the sun stays down"
                                     : "no sunset today - the sun stays up");

            Readout("Day length", clock.dayLengthHours.ToString("0.0") + " h" +
                    (clock.IsPolarDay ? "  (polar day)" :
                     clock.IsPolarNight ? "  (polar night)" : ""));

            // Live, and kept up here rather than only in the resolved block, because
            // this is the one number an author watches while dragging the hour.
            Readout("Sun elevation now",
                    clock.elevationDeg.ToString("0.0") + " deg " +
                    (clock.SunIsUp ? "above the horizon" : "below the horizon"));

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(!hasCrossings))
            {
                if (ActionButton("Sunrise", UCInspectorUI.ButtonAmber, 20f))
                {
                    ScrubTo(manager, clock.dayOfYear, clock.SunriseHour, "sunrise");
                }
            }

            if (ActionButton("Solar noon", UCInspectorUI.ButtonBlue, 20f))
            {
                ScrubTo(manager, clock.dayOfYear, clock.solarNoonHour, "solar noon");
            }

            using (new EditorGUI.DisabledScope(!hasCrossings))
            {
                if (ActionButton("Sunset", UCInspectorUI.ButtonAmber, 20f))
                {
                    ScrubTo(manager, clock.dayOfYear, clock.SunsetHour, "sunset");
                }
            }

            if (ActionButton("Midnight", UCInspectorUI.ButtonBlue, 20f))
            {
                ScrubTo(manager, clock.dayOfYear, ClockState.MidnightHour, "midnight");
            }

            EditorGUILayout.EndHorizontal();

            if (!hasCrossings)
            {
                EditorGUILayout.HelpBox(clock.IsPolarNight
                    ? "No sunrise on day " + clock.dayOfYear + " at latitude " +
                      clock.latitudeDeg.ToString("0.0") +
                      ": this is inside the polar night, so the sun does not cross " +
                      "the horizon at any hour. Solar noon still works - it is the " +
                      "brightest moment of a dark day, and the sun is highest there " +
                      "even while it stays below the horizon."
                    : "No sunset on day " + clock.dayOfYear + " at latitude " +
                      clock.latitudeDeg.ToString("0.0") +
                      ": this is under the midnight sun, so the sun does not cross " +
                      "the horizon at any hour. Midnight still works - it is where " +
                      "the sun sits lowest without setting.",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// Moves the world clock and leaves the inspector's two views of it in step.
        ///
        /// The pending slider edits are pushed to the object first, then the clock is
        /// moved through the manager's own SetCalendarTime, then the serialized copy is
        /// re-read so the sliders show where the clock actually went. Writing the two
        /// backing fields directly instead would leave the elapsed accumulator
        /// standing, and a world that had been open for a while would land at the
        /// requested time plus however long it had been running.
        /// </summary>
        private void ScrubTo(UCWeatherSystemManager manager, int dayOfYear,
                             float hourOfDay, string what)
        {
            serializedObject.ApplyModifiedProperties();

            Undo.RecordObject(manager, "Set world clock to " + what);
            manager.SetCalendarTime(manager.Year, dayOfYear, Mathf.Repeat(hourOfDay, ClockState.HoursPerDay)); // UC-T08-02: do not reset an advanced calendar to year 1.
            EditorUtility.SetDirty(manager);

            serializedObject.Update();
        }

        /// <summary>
        /// A decimal hour as a clock time. 13.5 reads as 13:30, which is what an
        /// author is checking against when they ask where the sun is - "0.5" of an
        /// hour is a number, "30" past the hour is a time.
        /// </summary>
        private static string HourMinute(float hours)
        {
            float wrapped = Mathf.Repeat(hours, ClockState.HoursPerDay);

            int h = (int)wrapped;
            int m = Mathf.RoundToInt((wrapped - h) * 60f);

            // Rounding 13.999 gives 60 minutes, which must carry rather than print.
            if (m >= 60) { m -= 60; h += 1; }
            if (h >= (int)ClockState.HoursPerDay) h -= (int)ClockState.HoursPerDay;

            return h.ToString("00") + ":" + m.ToString("00");
        }

        /// <summary>
        /// Repaints while the clock is running, so the readouts above move with the
        /// world instead of freezing until the next mouse-over. Off while paused,
        /// because a constantly repainting inspector costs editor frame time for
        /// nothing when nothing is changing.
        /// </summary>
        public override bool RequiresConstantRepaint()
        {
            UCWeatherSystemManager m = Manager;
            return m != null && (m.ClockIsRunning || Utilities.WeatherEditModeDriver.PreviewOwner == m);
        }

        private void Field(string propertyName)
        {
            SerializedProperty p = serializedObject.FindProperty(propertyName);
            if (p == null)
            {
                // Loud on purpose: a silent skip is how a renamed field disappears
                // from the inspector for a whole release.
                EditorGUILayout.HelpBox(
                    "No serialized field named \"" + propertyName + "\". This inspector " +
                    "is out of date with the component.", MessageType.Error);
                return;
            }

            EditorGUILayout.PropertyField(p, true);
        }

        /// <summary>A computed line: label left, derived value right.</summary>
        private static void Readout(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            GUIStyle right = new GUIStyle(EditorStyles.miniLabel);
            right.alignment = TextAnchor.MiddleRight;
            EditorGUILayout.LabelField(value, right);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// W-06. A name for a season phase, FOR DISPLAY ONLY.
        ///
        /// It lives in the editor assembly deliberately. Put a season name in the
        /// runtime and something eventually branches on it, and the moment it does the
        /// southern hemisphere breaks: the phase already carries the hemisphere
        /// inversion, a name does not. Runtime code reads <c>season01</c>. This exists
        /// so a human reading the inspector knows which end of the year they are
        /// looking at, and for nothing else - which is why it returns a string rather
        /// than an enum, so it cannot be switched on even by accident.
        ///
        /// The bands are centred on the astronomical markers rather than starting at
        /// them: season01 is 0 AT the vernal equinox, so equinox day should read
        /// "spring" and not sit on the boundary between two names. Spring's band
        /// therefore wraps across 0, which is why it is tested at both ends.
        /// </summary>
        private static string SeasonLabelOf(float season01)
        {
            float phase = season01 - Mathf.Floor(season01);

            // Eighths of a year: each name owns the quarter centred on its marker,
            // spring on 0, summer on 0.25, autumn on 0.5, winter on 0.75.
            if (phase < 0.125f || phase >= 0.875f) return "spring";
            if (phase < 0.375f) return "summer";
            if (phase < 0.625f) return "autumn";
            return "winter";
        }

        /// <summary>
        /// W-07. A name for a lunar phase, FOR DISPLAY ONLY, and in the editor
        /// assembly for the same reason the season label is: runtime code that
        /// branched on "full moon" would be branching on a band boundary rather than
        /// on the lit fraction it actually wants.
        ///
        /// Eighths of the month, each name owning the eighth centred on its marker -
        /// so "full" is the days around 0.5 rather than the single instant of it, and
        /// "new" wraps across 0. Waxing is the first half by definition: the phase
        /// counts forward from new moon.
        /// </summary>
        private static string MoonPhaseLabelOf(float lunarPhase01)
        {
            float phase = lunarPhase01 - Mathf.Floor(lunarPhase01);

            if (phase < 0.0625f || phase >= 0.9375f) return "new";
            if (phase < 0.1875f) return "waxing crescent";
            if (phase < 0.3125f) return "first quarter";
            if (phase < 0.4375f) return "waxing gibbous";
            if (phase < 0.5625f) return "full";
            if (phase < 0.6875f) return "waning gibbous";
            if (phase < 0.8125f) return "last quarter";
            return "waning crescent";
        }

        /// <summary>
        /// The nine archetypes, and one button that stamps the chosen one's geography
        /// onto the world.
        ///
        /// A preset sets a PLACE, not a mood: latitude, the maritime axis, the annual
        /// rainfall and humidity, after which the climate above is derived and the
        /// author tunes from there. It is deliberately not automatic - applying rewrites
        /// live fields the author may have set, so it waits for the button and records
        /// one undo step; the readouts above move to the new climate the instant it lands.
        /// </summary>
        private void DrawPresets(UCWeatherSystemManager manager)
        {
            EditorGUILayout.HelpBox(
                "A preset fills the world's geography - latitude, how continental it is, " +
                "the yearly rainfall and humidity - and lets the climate follow from the " +
                "physics. It does not author a weather mood. Applying overwrites those " +
                "fields; everything else you have set is kept.",
                MessageType.None);

            presetChoice = (WeatherPresetKind)EditorGUILayout.EnumPopup("Archetype", presetChoice);

            WeatherPreset preview = WeatherPreset.For(presetChoice);
            Readout("Latitude", preview.latitudeDeg.ToString("0.0") + " deg");
            Readout("Continentality", preview.continentality01.ToString("0.00") +
                    (preview.continentality01 < 0.5f ? "  (maritime)" : "  (continental)"));
            Readout("Annual rainfall", preview.annualPrecipMm.ToString("0") + " mm/yr");
            Readout("Rain regime", preview.regime.ToString());

            EditorGUILayout.Space(3);

            if (ActionButton("Apply preset", UCInspectorUI.ButtonGreen))
            {
                serializedObject.ApplyModifiedProperties();

                Undo.RecordObject(manager, "Apply weather preset");
                manager.ApplyPreset(presetChoice);
                EditorUtility.SetDirty(manager);

                serializedObject.Update();
            }
        }

        private void DrawPlanned()
        {
            EditorGUILayout.HelpBox(
                "These remaining controls are retained for later environment features; their consumers are not connected yet. Working climate, wind and runtime environment controls appear above.",
                MessageType.Warning);

            // Disabled rather than hidden. Hiding them would leave a later reader
            // wondering whether weather exists at all; showing them greyed says
            // "planned, and this is the shape it will take".
            using (new EditorGUI.DisabledScope(true))
            {
                DrawPropertiesExcluding(serializedObject, LiveFields);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Surface traction  (published grip)",
                                       EditorStyles.boldLabel);

            // The one grip number other systems read, shown for the four surface
            // states so an author can see the ordering the model guarantees. Live
            // scene conditions are not wired yet - the driver assembles the snapshot
            // this comes from - so these are the model's fixed points, not the
            // ground under the camera, which is why they are in the planned block.
            EditorGUILayout.HelpBox(
                "The 0..1 grip the weather module hands to vehicle and character " +
                "systems - it never applies a force itself. Dry grips fully, a wet " +
                "film less, snow less again, and glaze ice least of all.",
                MessageType.None);

            Readout("Dry", WetnessSimulation.SurfaceFriction01(0f, 0f, WarmSampleC)
                    .ToString("0.00"));
            Readout("Wet", WetnessSimulation.SurfaceFriction01(1f, 0f, WarmSampleC)
                    .ToString("0.00"));
            Readout("Snow", WetnessSimulation.SurfaceFriction01(
                    0f, AtmoConstants.SnowFrictionFullDepthM, FrozenSampleC).ToString("0.00"));
            Readout("Ice", WetnessSimulation.SurfaceFriction01(1f, 0f, FrozenSampleC)
                    .ToString("0.00"));
        }

        /// <summary>degC. A representative above-freezing surface for the traction readout.</summary>
        private const float WarmSampleC = 15f;

        /// <summary>degC. A representative below-freezing surface for the traction readout.</summary>
        private const float FrozenSampleC = -5f;

        private static string StatusText(UCWeatherSystemManager manager)
        {
            if (!manager.windEnabled) return "wind off";
            return manager.WindOrNull != null ? "wind on" : "no wind yet";
        }

        private static bool Group(bool state, string title, Color accent)
        {
            bool before = GUI.changed;
            bool next = UCInspectorUI.Section(state, title, accent);
            GUI.changed = before;
            return next;
        }

        private static bool ActionButton(string label, Color tint, float height = 22f)
        {
            bool before = GUI.changed;
            bool clicked = UCInspectorUI.TintedButton(label, tint, height);
            if (clicked) GUI.changed = before;
            return clicked;
        }
    }
}
