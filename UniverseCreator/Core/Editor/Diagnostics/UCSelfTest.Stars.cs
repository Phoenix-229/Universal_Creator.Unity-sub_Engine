// UC celestial repair: real catalog, coordinate/season limits and existing camera/owner publication.
using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Weather;
using LightingSettings = UniverseCreator.TerrainSystem.Lighting.LightingSettings; // UC celestial repair: distinguish the saved UC model from Unity's lightmapping asset.

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Compiles the celestial sky's rendered passes on the active Editor graphics backend and exits zero only on success.</summary>
        /// <remarks>Main-thread batch entry; requires graphics, not -nographics. Checks compilation, not rendered appearance or device performance.</remarks>
        public static void RunCelestialShaderBatch()
        {
            checks = 0; failures.Clear(); Material material = null;
            try
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    throw new InvalidOperationException("The celestial shader gate requires an active graphics backend.");
                Shader shader = Shader.Find("UniverseCreator/Skybox");
                if (shader == null) throw new InvalidOperationException("The UC celestial sky shader is missing.");
                material = new Material(shader);
                Check(material.passCount > 0 && material.passCount <= 8, "UC celestial sky has a bounded rendered pass count.");
                for (int pass = 0; pass < Math.Min(material.passCount, 8); pass++)
                {
                    ShaderUtil.CompilePass(material, pass, true);
                    Check(ShaderUtil.IsPassCompiled(material, pass) && !ShaderUtil.ShaderHasError(shader),
                        "UC celestial sky pass " + pass + " compiles on " + SystemInfo.graphicsDeviceType + ".");
                }
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    Check(message.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error &&
                        message.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Warning,
                        "UC celestial shader compiler: " + message.message);
            }
            catch (Exception error) { Check(false, "UC celestial graphics compiler exception: " + error); }
            finally { if (material != null) UnityEngine.Object.DestroyImmediate(material); }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        private static void TestCatalogStars()
        {
            Check(!StarSky.TryRead(null, out _, out _, out _) && !StarSky.TryRead("invalid", out _, out _, out _),
                "UC stars reject missing/malformed catalog lines.");
            var source = Resources.Load<TextAsset>(StarSky.ResourcePath);
            Check(source != null, "UC stars original catalog is available through the runtime resource path.");
            if (source == null) return;
            Vector3 polaris = Vector3.zero; bool found = false;
            using (var reader = new System.IO.StringReader(source.text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.StartsWith(" 424") && StarSky.TryRead(line, out float ra, out float dec, out float mag))
                    {
                        float r = ra * Mathf.Deg2Rad, d = dec * Mathf.Deg2Rad;
                        polaris = new Vector3(Mathf.Cos(d) * Mathf.Cos(r), Mathf.Sin(d), Mathf.Cos(d) * Mathf.Sin(r));
                        found = Math.Abs(ra - 37.95f) < 0.1 && dec > 89 && mag < 3;
                    }
            }
            Check(found, "UC stars Polaris HR 424 uses its retained J2000 coordinates, not a random northern point.");
            Resources.UnloadAsset(source);
            Color low = SkyModel.SunColorLinear(2, 10, 2.2f), high = SkyModel.SunColorLinear(80, 10, 2.2f);
            Check(low == high, "UC sun spectrum changes through supplied atmospheric path, without a second altitude-temperature ramp.");
            Check(StarSky.Visibility(-18) == 1 && StarSky.Visibility(-6) == 0 &&
                StarSky.Visibility(-12.001f) > StarSky.Visibility(-12) &&
                StarSky.Visibility(-12) - StarSky.Visibility(-11.999f) < 0.001,
                "UC stars fade continuously through astronomical/civil twilight without time-band snapping.");
            var root = new GameObject("UC catalog stars acceptance"); root.SetActive(false);
            Texture nativeStars = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                weather.longitudeDeg = weather.standardMeridianDeg = 0;
                var lighting = root.AddComponent<UCLightingSystemManager>(); lighting.enabled = false;
                var camera = root.AddComponent<Camera>(); camera.enabled = false;
                // UC celestial repair: Unity dispatches OnDestroy only for previously active GameObjects.
                // Keep drivers disabled while exercising the native owner-destruction path.
                root.SetActive(true);
                var so = new SerializedObject(lighting);
                Check(so.FindProperty("settings.enableStars") != null && so.FindProperty("settings.starBrightness") != null,
                    "UC stars expose real saved Enable Stars and Star Brightness controls.");
                lighting.settings.enableStars = false;
                lighting.settings.starBrightness = 1.5f;
                var saved = JsonUtility.FromJson<LightingSettings>(JsonUtility.ToJson(lighting.settings));
                Check(!saved.enableStars && saved.starBrightness == 1.5f,
                    "UC star checkbox and brightness survive supported settings serialization.");
                int darkestDay = 1, moonlitDay = 1; float minimumMoon = float.MaxValue, maximumMoon = 0;
                for (int day = 1; day <= 30; day++)
                {
                    weather.latitudeDeg = 0; weather.SetCalendarTime(3000, day, 0);
                    ClockState clock = weather.SampleEnvironmentAt(Vector3.zero).Clock;
                    float moon = ExposureModel.MoonIlluminanceLux(clock.lunarElevationDeg, clock.lunarIlluminatedFraction);
                    if (moon < minimumMoon) { minimumMoon = moon; darkestDay = day; }
                    if (moon > maximumMoon) { maximumMoon = moon; moonlitDay = day; }
                }
                Check(maximumMoon > 0.1f && minimumMoon < 0.001f, "UC star fixture finds genuine moonless and near-full moon nights from the existing clock.");
                foreach (float latitude in new[] { -45f, 0f, 45f })
                {
                    weather.latitudeDeg = latitude; weather.SetCalendarTime(3000, 80, 0);
                    ClockState clock = weather.SampleEnvironmentAt(Vector3.zero).Clock;
                    Matrix4x4 rotation = StarSky.WorldToEquatorial(clock);
                    Vector3 local = rotation.transpose.MultiplyVector(polaris);
                    Check(Mathf.Abs(local.magnitude - 1) < 0.0001f &&
                        (latitude < 0 ? local.y < -0.69f : latitude > 0 ? local.y > 0.69f && local.z > 0.69f : Math.Abs(local.y) < 0.02),
                        "UC Polaris latitude/horizon and orthonormal rotation at latitude " + latitude);
                }
                weather.latitudeDeg = 0; weather.SetCalendarTime(3000, 80, 0);
                var midnight = weather.SampleEnvironmentAt(Vector3.zero);
                Matrix4x4 before = StarSky.WorldToEquatorial(midnight.Clock);
                weather.SetCalendarTime(3000, 80, 0.0001f);
                Matrix4x4 after = StarSky.WorldToEquatorial(weather.SampleEnvironmentAt(Vector3.zero).Clock);
                Check((before.MultiplyVector(Vector3.forward) - after.MultiplyVector(Vector3.forward)).sqrMagnitude < 1e-6f,
                    "UC star orientation advances continuously with fractional game time.");
                float darkStrength = 0;
                foreach (int mode in new[] { 0, 1, 2, 3 })
                {
                    lighting.settings.enableStars = mode != 1;
                    lighting.settings.starBrightness = mode == 2 ? 2 : 1;
                    weather.SetCalendarTime(3000, mode == 2 ? moonlitDay : mode == 3 ? 80 : darkestDay, mode == 3 ? 12 : 0);
                    var sample = weather.SampleEnvironmentAt(camera.transform.position);
                    Texture incoming = Shader.GetGlobalTexture("_UCStars");
                    Matrix4x4 incomingRotation = Shader.GetGlobalMatrix("_UCWorldToEquatorial");
                    Vector4 incomingParams = Shader.GetGlobalVector("_UCStarParams");
                    bool admitted = lighting.BeginSampleLighting(camera, sample, out var previous, out float exposure);
                    Check(admitted, "UC catalog stars use the existing lighting view owner, mode " + mode);
                    if (!admitted) continue;
                    try
                    {
                        nativeStars = Shader.GetGlobalTexture("_UCStars");
                        Check(nativeStars is Texture2D atlas && atlas.width == StarSky.Width && atlas.height == StarSky.Height && !atlas.isReadable,
                            "UC stars bounded native texture releases its CPU copy, mode " + mode);
                        float strength = Shader.GetGlobalVector("_UCStarParams").x;
                        if (mode == 0) darkStrength = strength;
                        if (mode == 2) Check(strength < darkStrength * 0.1f,
                            "UC full-moon exposure suppresses faint stars even at twice the authored brightness.");
                        Check(mode == 1 || mode == 3 ? strength == 0 : strength > 0 && !float.IsInfinity(strength),
                            "UC stars enabled/night versus disabled/day publication, mode " + mode);
                        var data = (StarSky)typeof(UCLightingSystemManager).GetField("starSky",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(lighting);
                        Check(data.Count > 9000 && Math.Abs(strength - (mode == 1 || mode == 3 ? 0 : data.RadianceScale * exposure * lighting.settings.starBrightness)) < 0.0001f,
                            "UC star magnitudes/shared exposure consume the saved brightness, mode " + mode);
                    }
                    finally { UCLightingSystemManager.RestoreSampleLighting(previous); }
                    Check(Shader.GetGlobalTexture("_UCStars") == incoming && Shader.GetGlobalMatrix("_UCWorldToEquatorial") == incomingRotation &&
                        Shader.GetGlobalVector("_UCStarParams") == incomingParams,
                        "UC stars restore incoming camera texture, rotation and visibility, mode " + mode);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            Check(nativeStars == null, "UC stars owner teardown destroys its native catalog texture after views restore.");
        }
    }
}
