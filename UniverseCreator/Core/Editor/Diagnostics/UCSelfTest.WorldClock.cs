// UC-T08-02: production clock/calendar/seek tests registered in the package; no second time owner.
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWorldClockContinuation()
        {
            var root = new GameObject("UC world clock continuation fixture");
            try
            {
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.timeScale = 1200; weather.pauseTime = false;
                weather.SetCalendarTime(2, 365, 23.5f); double before = weather.WorldInstantSeconds;
                weather.Advance(1800); var midnight = weather.ResolveClock();
                Check(midnight.year == 3 && midnight.dayOfYear == 1 && midnight.secondsOfDay == 0,
                    "UC-T08-02 the existing clock advances across the model year boundary exactly once.");
                weather.Advance(-1800); var returned = weather.ResolveClock();
                Check(weather.WorldInstantSeconds == before && returned.year == 2 && returned.dayOfYear == 365 && returned.localHour == 23.5f,
                    "UC-T08-02 backwards seeks reproduce the same absolute instant, calendar and sky inputs.");
                weather.pauseTime = true; weather.TickWorldClock(10);
                Check(weather.WorldInstantSeconds == before, "UC-T08-02 paused regular ticks retain the shared world instant.");
                weather.pauseTime = false; weather.SetCalendarTime(7, 172, 12); double start = weather.WorldInstantSeconds;
                weather.TickWorldClock(1); double whole = weather.WorldInstantSeconds;
                weather.SeekWorldInstant(start); for (int i = 0; i < 1000; i++) weather.TickWorldClock(0.001);
                Check(Math.Abs(weather.WorldInstantSeconds - whole) < 0.00001 && weather.Year == 7,
                    "UC-T08-02 compensated regular stepping preserves the same instant across different frame partitions without resetting the year.");
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    before = weather.WorldInstantSeconds; int refused = 0;
                    try { weather.Advance(invalid); } catch (ArgumentOutOfRangeException) { refused++; }
                    try { weather.SeekWorldInstant(invalid); } catch (ArgumentOutOfRangeException) { refused++; }
                    try { weather.TickWorldClock(invalid); } catch (ArgumentOutOfRangeException) { refused++; }
                    Check(refused == 3 && weather.WorldInstantSeconds == before,
                        "UC-T08-02 nonfinite tick/advance/seek inputs fail before changing the serialized clock: " + invalid + ".");
                }
                before = weather.WorldInstantSeconds; bool invalidHour = false;
                try { weather.SetTime(1, float.NaN); } catch (ArgumentOutOfRangeException) { invalidHour = true; }
                Check(invalidHour && weather.WorldInstantSeconds == before, "UC-T08-02 legacy SetTime rejects a nonfinite authored hour atomically.");
                weather.SetTime(172, float.MaxValue);
                Check(weather.LocalHour >= 0 && weather.LocalHour < 24 && !float.IsNaN(weather.LocalHour),
                    "UC-T08-02 finite extreme legacy hours wrap without overflowing the clock.");
                weather.Advance(double.MaxValue);
                Check(weather.Year == UCWeatherSystemManager.MaximumCalendarYear && !weather.ClockIsRunning,
                    "UC-T08-02 calendar exhaustion is finite, bounded and stops regular advancement.");
                weather.Advance(-double.MaxValue);
                Check(weather.Year == 1 && weather.DayOfYear == 1 && weather.SecondsOfDay == 0,
                    "UC-T08-02 a backwards advance saturates at the first model instant.");
                weather.SetCalendarTime(2, 172, 12); weather.enableLiveWeatherSim = true; weather.TickLiveWeather(0);
                weather.temperatureCelsius = -5; weather.SetCalendarTime(2, 355, 0);
                Check(weather.ResolveWeather().temperature == -5,
                    "UC-T08-02 an explicit time seek cannot retain the previous weather smoother state.");
                foreach (float latitude in new[] { -90f, 0f, 90f })
                {
                    weather.latitudeDeg = latitude; ClockState clock = weather.ResolveClock();
                    Check(!float.IsNaN(clock.elevationDeg) && clock.dayLengthHours >= 0 && clock.dayLengthHours <= 24,
                        "UC-T08-02 the retained solar mathematics stays finite at latitude " + latitude + ".");
                }
                // UC-T08-15: exercise the actual editor preview against the existing clock, including retirement and world isolation.
                weather.timeScale = 48; weather.pauseTime = true; weather.SetCalendarTime(2, 172, 12);
                before = weather.WorldInstantSeconds;
                Check(Utilities.WeatherEditModeDriver.StartPreview(weather), "UC-T08-15 Scene preview admits an enabled scene clock.");
                Utilities.WeatherEditModeDriver.StepPreview(900);
                Check(weather.WorldInstantSeconds == before + 43200 && weather.DayOfYear == 173 && weather.LocalHour == 0 && weather.pauseTime && weather.timeScale == 48,
                    "UC-T08-15 a 30-real-minute game day previews midnight in 15 minutes without changing authored pause/rate.");
                double previewInstant = weather.WorldInstantSeconds; bool invalidPreview = false;
                try { Utilities.WeatherEditModeDriver.StepPreview(double.NaN); } catch (ArgumentOutOfRangeException) { invalidPreview = true; }
                Check(invalidPreview && weather.pauseTime && weather.WorldInstantSeconds == previewInstant,
                    "UC-T08-15 invalid preview elapsed time retains instant and restores the pause gate.");
                Utilities.WeatherEditModeDriver.StopPreview(); Utilities.WeatherEditModeDriver.StopPreview();
                Check(weather.WorldInstantSeconds == before && weather.pauseTime && weather.timeScale == 48 && Utilities.WeatherEditModeDriver.PreviewOwner == null,
                    "UC-T08-15 idempotent Scene preview stop restores the exact start and retires its owner.");
                weather.timeScale = float.NaN;
                Check(!Utilities.WeatherEditModeDriver.StartPreview(weather) && weather.WorldInstantSeconds == before,
                    "UC-T08-15 invalid Scene preview rate is rejected without changing the existing clock.");
            }
            finally { Utilities.WeatherEditModeDriver.StopPreview(); UnityEngine.Object.DestroyImmediate(root); }
            TestScenePreviewLighting();
        }

        // UC-T08-15: reproduce the missing Scene camera binding using native editor cameras and actual light/sky recipients.
        private static void TestScenePreviewLighting()
        {
            var root = new GameObject("UC rendered Scene preview fixture"); root.SetActive(false);
            var foreign = new GameObject("UC nonpreview weather fixture"); foreign.SetActive(false);
            SceneView view = null;
            Material incomingSky = RenderSettings.skybox; Light incomingSun = RenderSettings.sun;
            var incomingAmbient = RenderSettings.ambientProbe; var incomingAmbientMode = RenderSettings.ambientMode;
            bool incomingFog = RenderSettings.fog; Color incomingFogColor = RenderSettings.fogColor;
            var incomingFogMode = RenderSettings.fogMode; float incomingFogDensity = RenderSettings.fogDensity;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.worldSizeKm = 0.032f; world.chunkCountPerSide = 1;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.pauseTime = true; weather.timeScale = 48;
                weather.latitudeDeg = weather.longitudeDeg = weather.standardMeridianDeg = 0;
                weather.latitudeSpanDeg = weather.longitudeSpanDeg = 0;
                weather.SetCalendarTime(3, 80, 12);
                var lighting = root.AddComponent<UCLightingSystemManager>(); lighting.enabled = false;
                lighting.settings.exposureMode = UniverseCreator.TerrainSystem.Lighting.ExposureMode.Manual;
                lighting.settings.manualExposureEv = 12; lighting.settings.nightFloorLux = 0;
                var otherWeather = foreign.AddComponent<UCWeatherSystemManager>();
                double otherInstant = otherWeather.WorldInstantSeconds;
                root.SetActive(true);
                view = ScriptableObject.CreateInstance<SceneView>();
                view.sceneLighting = false; view.sceneViewState.showSkybox = false;
                Camera camera = view.camera;
                Check(camera != null && camera.cameraType == CameraType.SceneView,
                    "UC-T08-15 preview fixture uses a native unparented Scene camera.");
                if (camera == null) return;
                Check(!weather.OwnsEnvironmentView(camera), "UC-T08-15 an idle world does not acquire the editor camera.");
                double start = weather.WorldInstantSeconds; var run = world.GenerationScheduler.CurrentRun;
                Check(Utilities.WeatherEditModeDriver.StartPreview(weather) && Utilities.WeatherEditModeDriver.PreparePreviewView(view) &&
                    weather.ScenePreviewCamera == camera && view.sceneLighting && view.sceneViewState.showSkybox,
                    "UC-T08-15 explicit preview connects the Scene camera and enables its light/sky toggles.");
                Check(!otherWeather.OwnsEnvironmentView(camera), "UC-T08-15 Scene preview does not admit the same camera to another weather owner.");
                bool admitted = weather.BeginEnvironmentView(camera);
                Check(admitted, "UC-T08-15 the production environment renderer admits the preview camera at noon.");
                if (!admitted) return;
                Light sun = lighting.sun;
                Check(sun != null && sun.type == LightType.Directional && sun.enabled && sun.intensity > 0 && RenderSettings.sun == sun,
                    "UC-T08-15 the generated directional sun actually lights the noon preview.");
                float noonSun = sun != null ? sun.intensity : 0;
                Vector3 noonSunDirection = sun != null ? sun.transform.forward : Vector3.zero;
                float cameraHeight = camera.transform.position.y;
                float noonAmbient = RenderSettings.ambientProbe[0, 0];
                Material sky = RenderSettings.skybox; var lut = Shader.GetGlobalTexture("_SkyLUT") as Texture2D;
                Check(sky != null && sky.shader.name == "UniverseCreator/Skybox" && lut != null && sky.GetTexture("_SkyLUT") == lut,
                    "UC-T08-15 Scene preview binds the actual UC sky material and generated LUT.");
                Vector4 noonPixel = lut != null ? lut.GetPixel(4, 4) : Vector4.zero;
                Vector4 discs = Shader.GetGlobalVector("_UCSkyDiscParams");
                CheckClose(discs.x, lighting.settings.sunDiscSizeDeg * 0.5f * Mathf.Deg2Rad,
                    "UC-T08-15 visible sun size comes from angular diameter rather than the Light object's scale.");
                CheckClose(discs.z, lighting.settings.moonDiscSizeDeg * 0.5f * Mathf.Deg2Rad,
                    "UC-T08-15 visible moon size uses the same angular sky convention.");
                weather.EndEnvironmentView(camera);
                Check(RenderSettings.skybox == incomingSky && RenderSettings.sun == incomingSun &&
                    RenderSettings.ambientProbe[0, 0] == incomingAmbient[0, 0] && sun != null && !sun.enabled,
                    "UC-T08-15 Scene camera completion restores incoming sky, ambient and generated-light state.");
                Utilities.WeatherEditModeDriver.StepPreview(900);
                admitted = weather.BeginEnvironmentView(camera);
                Check(admitted, "UC-T08-15 the same Scene camera receives the advanced midnight instant.");
                if (!admitted) return;
                Check(sun != null && sun.intensity == 0 && noonSun > 0,
                    "UC-T08-15 advancing the actual preview clock retires directional daylight at midnight.");
                var nightLut = Shader.GetGlobalTexture("_SkyLUT") as Texture2D;
                Check(lut != null && nightLut != null && Vector4.Distance(noonPixel, nightLut.GetPixel(4, 4)) > 0.00001f,
                    "UC-T08-15 the midnight endpoint changes without overwriting the displayed noon LUT.");
                weather.EndEnvironmentView(camera);
                var caches = (UCLightingSystemManager.LightingSampleCache[])typeof(UCLightingSystemManager)
                    .GetField("cameraLighting", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(lighting);
                foreach (var cache in caches)
                    if (cache != null) cache.SkyTransitionStarted = Time.realtimeSinceStartupAsDouble - 1;
                Check(weather.BeginEnvironmentView(camera) && RenderSettings.ambientProbe[0, 0] < noonAmbient,
                    "UC-T08-15 the actual preview publishes darker midnight ambient after its bounded transition finishes.");
                var clock = weather.ResolveClockAt(camera.transform.position);
                Check(Vector3.Distance(Shader.GetGlobalVector("_UCMoonDir"),
                    UniverseCreator.TerrainSystem.Lighting.SunMoonRig.DirectionToBody(clock.lunarElevationDeg, clock.lunarAzimuthDeg)) < 0.0001f,
                    "UC-T08-15 the sky moon direction follows the same advanced world clock.");
                // Stop during an admitted view: restore bindings before releasing its native resources.
                Utilities.WeatherEditModeDriver.StopPreview();
                Check(weather.WorldInstantSeconds == start && weather.pauseTime && weather.timeScale == 48 &&
                    weather.ScenePreviewCamera == null && !view.sceneLighting && !view.sceneViewState.showSkybox,
                    "UC-T08-15 stopping preview restores the exact time, authored clock settings and Scene toggles.");
                Check(RenderSettings.skybox == incomingSky && RenderSettings.sun == incomingSun && RenderSettings.fog == incomingFog &&
                    RenderSettings.fogColor == incomingFogColor && RenderSettings.fogMode == incomingFogMode && RenderSettings.fogDensity == incomingFogDensity,
                    "UC-T08-15 preview retirement restores the incoming lighting and fog before discarding camera state.");
                Check(lut == null && nightLut == null && sky == null && !weather.OwnsEnvironmentView(camera) && otherWeather.WorldInstantSeconds == otherInstant,
                    "UC-T08-15 stop frees preview sky resources and leaves other world clocks unchanged.");
                // UC Scene/Play repair: changing altitude changes atmospheric extinction; test translation at the same height.
                world.worldSizeKm = 1024; camera.transform.position = new Vector3(1000000, cameraHeight, -1000000);
                if (sun != null) { sun.transform.position = new Vector3(123, 456, 789); sun.transform.localScale = Vector3.one * 0.01f; }
                Check(Utilities.WeatherEditModeDriver.StartPreview(weather) && Utilities.WeatherEditModeDriver.PreparePreviewView(view) &&
                    weather.BeginEnvironmentView(camera), "UC-T08-15 a retired preview can restart without consuming another camera slot.");
                Check(Shader.GetGlobalVector("_UCSkyDiscParams") == discs && sun != null && sun.type == LightType.Directional &&
                    sun.enabled && sun.intensity > 0 && IsFinite(sun.intensity) && Vector3.Distance(sun.transform.forward, noonSunDirection) < 0.0001f &&
                    world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T08-15 large-world camera/light positions preserve directional sunlight and angular discs without rebuilding terrain.");
                Utilities.WeatherEditModeDriver.StopPreview();
                Check(Utilities.WeatherEditModeDriver.StartPreview(weather) && Utilities.WeatherEditModeDriver.PreparePreviewView(view),
                    "UC-T08-15 preview can be restarted for owner-retirement acceptance.");
                Check(weather.BeginEnvironmentView(camera), "UC Scene/Play repair: owner retirement exercises an active native preview view.");
                Material retiringSky = RenderSettings.skybox; Texture retiringLut = Shader.GetGlobalTexture("_SkyLUT");
                UnityEngine.Object.DestroyImmediate(weather);
                typeof(Utilities.WeatherEditModeDriver).GetMethod("OnEditorUpdate", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                Check(Utilities.WeatherEditModeDriver.PreviewOwner == null && !view.sceneLighting && !view.sceneViewState.showSkybox,
                    "UC-T08-15 destroying the preview owner restores editor toggles on the next driver tick.");
                Check(retiringSky == null && retiringLut == null && RenderSettings.skybox == incomingSky,
                    "UC Scene/Play repair: weather destruction restores and retires its borrowed native sky without destroying the lighting manager.");
            }
            finally
            {
                Utilities.WeatherEditModeDriver.StopPreview();
                if (view != null) UnityEngine.Object.DestroyImmediate(view);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(foreign);
                RenderSettings.skybox = incomingSky; RenderSettings.sun = incomingSun;
                RenderSettings.ambientMode = incomingAmbientMode; RenderSettings.ambientProbe = incomingAmbient;
                RenderSettings.fog = incomingFog; RenderSettings.fogColor = incomingFogColor;
                RenderSettings.fogMode = incomingFogMode; RenderSettings.fogDensity = incomingFogDensity;
            }
        }
    }
}
