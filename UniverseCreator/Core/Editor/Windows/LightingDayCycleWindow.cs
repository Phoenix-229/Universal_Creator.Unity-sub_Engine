// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    LightingDayCycleWindow.cs
//  Purpose: A day-cycle scrub. A 24-sample strip of sky and sun colour across the
//           world's current date and latitude, and one-click jumps to the times a
//           lighting author actually cares about - midnight, sunrise, solar noon,
//           sunset, and the live clock.
//
//  WHERE THE NUMBERS COME FROM, AND WHY THERE IS NO SECOND SOLAR FORMULA
//  --------------------------------------------------------------------
//  The strip reads one resolved clock - the weather system's if the world has one,
//  the documented default otherwise - and takes its declination and solar-noon hour
//  from it. An arbitrary hour's sun position is then the same hour-angle reduction
//  every consumer uses: 15 degrees per hour off solar noon, fed to ClockMath's one
//  elevation and azimuth. So the hour the live clock happens to sit at reproduces
//  the clock's own sun exactly, and no formula lives twice.
//
//  WHAT THE SWATCHES ARE, AND WHAT THEY ARE NOT
//  --------------------------------------------
//  The hue of each swatch is physical: the sun's reddening from SkyModel, the sky's
//  Perez chromaticity by day and its twilight glow near the horizon. The brightness
//  is a display envelope, not an exposure map - full when the sun is well up, a dim
//  floor at deep night, smooth across twilight - so the strip reads as a day cycle
//  on screen. The exposure-mapped frame is the driver's job, not this preview's.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Weather;
using UniverseCreator.Editor.Inspectors;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>
    /// A preview of the world's day, sampled every hour. Read-only: it scrubs a
    /// cursor across the strip and reports the sun's height and bearing at it. The
    /// clock itself is owned by the weather system and edited there.
    /// </summary>
    public class LightingDayCycleWindow : EditorWindow
    {
        // One sample per hour of the day. Enough for the eye to read the arc of
        // the sun without pretending to a resolution the strip cannot show.
        private const int Samples = 24;

        // Display envelope, not physics. The sky swatch is at full brightness once
        // the sun is this far up, at a dim floor below astronomical twilight.
        private const float DaySkyFullElevDeg = 10f;
        private const float NightSkyFloor = 0.04f;

        // A low view toward the sun for the twilight glow sample - the horizon band
        // is where the colour lives, not the zenith.
        private const float TwilightViewElevDeg = 3f;

        // The hour the cursor sits at, 0..24. Negative until the first draw seeds
        // it from the live clock, so the window opens on the world's own time.
        private float previewHour = -1f;

        /// <summary>The sun geometry and preview colours at one hour of the day.</summary>
        public struct SolarSample
        {
            public float elevationDeg;
            public float azimuthDeg;
            public float airMass;
            public Color sunColor;
            public Color skyColor;
        }

        // =====================================================================
        //  WINDOW LIFECYCLE
        // =====================================================================

        /// <summary>Opens (or focuses) the day-cycle window. Called from the menu.</summary>
        public static void OpenWindow()
        {
            LightingDayCycleWindow w = GetWindow<LightingDayCycleWindow>("Day Cycle");
            w.minSize = new Vector2(420f, 220f);
            w.Show();
        }

        // Repaint at the editor's idle cadence so a change to the weather clock's
        // date or latitude shows up in the strip without the user touching the
        // window - the strip is recomputed from the resolved clock every draw.
        private void OnInspectorUpdate()
        {
            Repaint();
        }

        // =====================================================================
        //  DRAW
        // =====================================================================

        private void OnGUI()
        {
            UCLightingSystemManager target = ResolveTarget();
            ClockState clock = ResolveClock(target);
            float turbidity = ResolveTurbidity(target);

            // Seed the cursor on the world's live hour the first time we draw.
            if (previewHour < 0f) previewHour = clock.localHour;

            UCInspectorUI.HeaderBar("Lighting Day Cycle", "d_Light Icon",
                UCInspectorUI.HeaderAmber, "sky and sun sampled by the hour");

            string where = target != null
                ? "World: " + target.gameObject.name
                : "No Lighting System selected - showing the default clock";
            EditorGUILayout.HelpBox(
                where + "\nLat " + clock.latitudeDeg.ToString("0.0") + "°" +
                "   Day " + clock.dayOfYear + "   Turbidity " + turbidity.ToString("0.0") +
                (target == null ? "\nSelect a Lighting System to preview its own world." : ""),
                MessageType.Info);

            Rect strip = GUILayoutUtility.GetRect(10f, 96f, GUILayout.ExpandWidth(true));
            strip = new Rect(strip.x + 6f, strip.y + 4f, strip.width - 12f, strip.height - 8f);
            DrawStrip(strip, clock, turbidity);
            DrawHourAxis(strip);

            EditorGUILayout.Space(4f);
            DrawJumpButtons(clock);
            EditorGUILayout.Space(2f);
            DrawReadout(clock, turbidity);
        }

        // =====================================================================
        //  WHAT WORLD, WHAT CLOCK, WHAT AIR
        // =====================================================================

        // The selected Lighting System if there is one, otherwise any in the scene,
        // otherwise null - in which case the default clock stands in.
        private static UCLightingSystemManager ResolveTarget()
        {
            if (Selection.activeGameObject != null)
            {
                UCLightingSystemManager sel =
                    Selection.activeGameObject.GetComponentInParent<UCLightingSystemManager>();
                if (sel != null) return sel;
            }
            return FindAnyObjectByType<UCLightingSystemManager>();
        }

        // The clock is read, never authored here: the weather system's if the world
        // has one, the documented default otherwise. One owner, no second copy.
        private static ClockState ResolveClock(UCLightingSystemManager target)
        {
            return target != null
                ? UCWeatherSystemManager.ResolveClockFor(target.transform)
                : UCWeatherSystemManager.DefaultClock();
        }

        private static float ResolveTurbidity(UCLightingSystemManager target)
        {
            return target != null ? target.settings.turbidity : LightingConstants.TurbidityDefault;
        }

        // =====================================================================
        //  THE DETERMINISTIC SAMPLE
        //
        //  Pure functions of the resolved clock and turbidity - no frame time, no
        //  random, no cached state - so the strip is identical on every machine and
        //  on every repaint of an unchanged clock.
        // =====================================================================

        /// <summary>
        /// deg. Solar hour angle at a local clock hour, taken off the clock's own
        /// solar noon: 15 degrees per hour, negative before noon, wrapped to
        /// -180..180 on the same convention as <see cref="ClockMath.SolarHourAngleDeg"/>.
        /// Reading solar noon from the clock inherits its longitude and equation-of-time
        /// corrections rather than restating them.
        /// </summary>
        public static float HourAngleAt(float localHour, float solarNoonHour)
        {
            float ha = AtmoConstants.DegreesPerHourOfHourAngle * (localHour - solarNoonHour);
            return Mathf.Repeat(ha + 180f, 360f) - 180f;
        }

        /// <summary>
        /// The sun's height, bearing, air mass and preview colours at one hour of the
        /// clock's day and latitude. The geometry forwards to ClockMath; the colours
        /// are physical hue on a display brightness envelope (see the file header).
        /// </summary>
        public static SolarSample SampleAt(in ClockState clock, float localHour, float turbidity)
        {
            float ha = HourAngleAt(localHour, clock.solarNoonHour);
            float elev = ClockMath.ElevationDeg(clock.latitudeDeg, clock.declinationDeg, ha);
            float az = ClockMath.AzimuthDeg(clock.latitudeDeg, clock.declinationDeg, ha, elev);
            float airMass = ClockMath.AirMass(elev);

            SolarSample s;
            s.elevationDeg = elev;
            s.azimuthDeg = az;
            s.airMass = airMass;
            ComputeColors(elev, az, airMass, turbidity, out s.sunColor, out s.skyColor);
            return s;
        }

        // The sun's own colour, faded through civil twilight and black once gone;
        // the sky's chromaticity on a night-to-day brightness envelope.
        private static void ComputeColors(float elevDeg, float azDeg, float airMass,
                                          float turbidity, out Color sun, out Color sky)
        {
            Vector3 sunDir = SunMoonRig.DirectionToBody(elevDeg, azDeg);

            float sunLit = SunMoonRig.SunTwilightWeight01(elevDeg);
            Color sunHue = SkyModel.SunColorLinear(
                Mathf.Max(elevDeg, LightingConstants.SunColorElevationFloorDeg), airMass, turbidity);
            sun = new Color(sunHue.r * sunLit, sunHue.g * sunLit, sunHue.b * sunLit, 1f);

            Color hue;
            if (elevDeg >= 0f)
            {
                hue = NormalizeHue(SkyModel.SkyRadiance(Vector3.up, sunDir, turbidity));
            }
            else if (elevDeg > LightingConstants.AstronomicalTwilightDeg)
            {
                Vector3 lowView = SunMoonRig.DirectionToBody(TwilightViewElevDeg, azDeg);
                Color glow = NormalizeHue(SkyModel.TwilightRadiance(lowView, sunDir, turbidity));
                hue = glow.maxColorComponent > 1e-4f
                    ? glow : NormalizeHue(LightingConstants.TwilightSkyColor);
            }
            else
            {
                hue = NormalizeHue(LightingConstants.TwilightSkyColor);
            }

            float t = Mathf.InverseLerp(LightingConstants.AstronomicalTwilightDeg,
                                        DaySkyFullElevDeg, elevDeg);
            t = t * t * (3f - 2f * t); // smoothstep, so twilight is a ramp not a step
            float bright = Mathf.Lerp(NightSkyFloor, 1f, t);
            sky = new Color(hue.r * bright, hue.g * bright, hue.b * bright, 1f);
        }

        // A colour reduced to its hue: peak channel scaled to 1, so the swatch
        // carries the sky's chromaticity and takes its brightness from the envelope.
        private static Color NormalizeHue(Color c)
        {
            float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (peak <= 1e-6f) return new Color(0f, 0f, 0f, 1f);
            return new Color(c.r / peak, c.g / peak, c.b / peak, 1f);
        }

        // =====================================================================
        //  DRAWING THE STRIP
        // =====================================================================

        // Twenty-four bars, each split sky over sun, with the cursor drawn on top.
        private void DrawStrip(Rect r, in ClockState clock, float turbidity)
        {
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 1f));

            float bw = r.width / Samples;
            float skyH = r.height * 0.62f;

            for (int i = 0; i < Samples; i++)
            {
                float hour = i + 0.5f; // the centre of the hour, not its edge
                SolarSample s = SampleAt(clock, hour, turbidity);

                float x = r.x + i * bw;
                float w = Mathf.Ceil(bw) + 1f; // overlap a pixel so no seam shows
                EditorGUI.DrawRect(new Rect(x, r.y, w, skyH), s.skyColor);
                EditorGUI.DrawRect(new Rect(x, r.y + skyH, w, r.height - skyH), s.sunColor);
            }

            // The cursor: a white line at the preview hour, click or drag to move it.
            float cx = r.x + Mathf.Clamp01(previewHour / ClockState.HoursPerDay) * r.width;
            EditorGUI.DrawRect(new Rect(cx - 1f, r.y, 2f, r.height), Color.white);

            Event e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) &&
                r.Contains(e.mousePosition))
            {
                previewHour = Mathf.Clamp01((e.mousePosition.x - r.x) / r.width)
                              * ClockState.HoursPerDay;
                e.Use();
                Repaint();
            }
        }

        // Hour marks under the strip at 00, 06, 12, 18, 24.
        private void DrawHourAxis(Rect strip)
        {
            Rect axis = GUILayoutUtility.GetRect(10f, 14f, GUILayout.ExpandWidth(true));
            GUIStyle tick = EditorStyles.miniLabel;
            for (int h = 0; h <= 24; h += 6)
            {
                float x = strip.x + (h / ClockState.HoursPerDay) * strip.width;
                Rect lr = new Rect(x - 14f, axis.y, 28f, axis.height);
                GUI.Label(lr, h.ToString("00") + ":00", tick);
            }
        }

        // One-click jumps to the times an author cares about. Sunrise and sunset are
        // disabled on the two polar extremes, where there is genuinely no crossing.
        private void DrawJumpButtons(ClockState clock)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Midnight")) previewHour = ClockState.MidnightHour;

            using (new EditorGUI.DisabledScope(!clock.HasSunriseToday))
            {
                if (GUILayout.Button("Sunrise"))
                    previewHour = Mathf.Repeat(clock.SunriseHour, ClockState.HoursPerDay);
            }

            if (GUILayout.Button("Solar Noon"))
                previewHour = Mathf.Repeat(clock.solarNoonHour, ClockState.HoursPerDay);

            using (new EditorGUI.DisabledScope(!clock.HasSunriseToday))
            {
                if (GUILayout.Button("Sunset"))
                    previewHour = Mathf.Repeat(clock.SunsetHour, ClockState.HoursPerDay);
            }

            if (GUILayout.Button("Now")) previewHour = clock.localHour;
            EditorGUILayout.EndHorizontal();
        }

        // The sun's state at the cursor, read from the same sampler the strip uses.
        private void DrawReadout(ClockState clock, float turbidity)
        {
            SolarSample s = SampleAt(clock, previewHour, turbidity);
            int hh = Mathf.FloorToInt(previewHour);
            int mm = Mathf.FloorToInt((previewHour - hh) * 60f);

            UCGroupedInspector.Readout("Preview time", hh.ToString("00") + ":" + mm.ToString("00"));
            UCGroupedInspector.Readout("Sun elevation", s.elevationDeg.ToString("0.0") + "°" +
                (s.elevationDeg > 0f ? "  (up)" : "  (down)"));
            UCGroupedInspector.Readout("Sun azimuth", s.azimuthDeg.ToString("0") + "°");
            UCGroupedInspector.Readout("Air mass", s.elevationDeg > 0f ? s.airMass.ToString("0.0") : "-");

            if (clock.IsPolarNight)
                EditorGUILayout.HelpBox("Polar night: the sun does not rise on this day.", MessageType.None);
            else if (clock.IsPolarDay)
                EditorGUILayout.HelpBox("Midnight sun: the sun does not set on this day.", MessageType.None);
        }
    }
}
