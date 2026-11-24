// UC-T08-07: real shared samples, light/material/fog recipients, nested worlds and native retirement.
using System;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC Scene/Play: exercise the production cache with a deterministic real-time input, without sleeping or another clock.
        private static void TestContinuousSkyLighting()
        {
            var root = new GameObject("UC continuous sky fixture"); root.SetActive(false);
            var cache = new UCLightingSystemManager.LightingSampleCache();
            var update = typeof(UCLightingSystemManager).GetMethod("EnsureSkyLutBaked", BindingFlags.Instance | BindingFlags.NonPublic);
            var release = typeof(UCLightingSystemManager).GetMethod("ReleaseSampleCache", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                Check(update != null && release != null, "UC sky continuity resolves existing bake/retirement owners.");
                if (update == null || release == null) return;
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                weather.latitudeDeg = 0; weather.longitudeDeg = weather.standardMeridianDeg = 0;
                var lighting = root.AddComponent<UCLightingSystemManager>(); lighting.enabled = false;
                weather.SetCalendarTime(3000, 80, 12);
                var noon = weather.SampleEnvironmentAt(Vector3.zero); var noonAtmo = lighting.AtmosphereForSample(noon, out _);
                update.Invoke(lighting, new object[] { noonAtmo, noon, cache, 10d });
                var first = cache.SkyLut; var buffer = cache.SkyPixels; Color noonPixel = first.GetPixel(4, 4);
                var noonAmbient = UCLightingSystemManager.SampleSkyAmbient(cache);
                Check(cache.SkyBlend == 1 && cache.PreviousSkyLut == null, "UC sky initial bake publishes immediately with one endpoint.");
                update.Invoke(lighting, new object[] { noonAtmo, noon, cache, 10.01d });
                Check(cache.SkyBlend == 1 && cache.PreviousSkyLut == null && cache.SkyLut == first,
                    "UC sky a steady first view never starts a transition without a previous endpoint.");
                weather.SetCalendarTime(3000, 80, 0);
                var night = weather.SampleEnvironmentAt(Vector3.zero); var nightAtmo = lighting.AtmosphereForSample(night, out _);
                update.Invoke(lighting, new object[] { nightAtmo, night, cache, 11d });
                var second = cache.SkyLut;
                Check(first != second && cache.PreviousSkyLut == first && first.GetPixel(4, 4) == noonPixel && cache.SkyBlend == 0,
                    "UC sky threshold crossing retains the displayed endpoint instead of overwriting it.");
                Check(UCLightingSystemManager.SampleSkyAmbient(cache)[0, 0] == noonAmbient[0, 0],
                    "UC sky transition begins with the exact previous ambient, without a brightness jump.");
                update.Invoke(lighting, new object[] { nightAtmo, night, cache, 11.125d });
                CheckClose(cache.SkyBlend, 0.5f, "UC sky transition reaches its midpoint at 0.125 real seconds.");
                var mid = UCLightingSystemManager.SampleSkyAmbient(cache);
                for (int channel = 0; channel < 3; channel++)
                    for (int coefficient = 0; coefficient < 9; coefficient++)
                        CheckClose(mid[channel, coefficient], (cache.PreviousAmbient[channel, coefficient] + cache.Ambient[channel, coefficient]) * 0.5f,
                            "UC sky and all ambient coefficients share the midpoint weight.");
                update.Invoke(lighting, new object[] { noonAtmo, noon, cache, 11.2d });
                Check(cache.SkyLut == second && cache.PreviousSkyLut == first && first.GetPixel(4, 4) == noonPixel,
                    "UC sky a new request cannot overwrite a partly displayed endpoint.");
                update.Invoke(lighting, new object[] { nightAtmo, night, cache, 11.25d });
                Check(cache.SkyBlend == 1 && UCLightingSystemManager.SampleSkyAmbient(cache)[0, 0] < noonAmbient[0, 0],
                    "UC sky finishes its transition to darker physical midnight ambient.");
                update.Invoke(lighting, new object[] { noonAtmo, noon, cache, 12d });
                Check(cache.SkyLut == first && cache.PreviousSkyLut == second && ReferenceEquals(cache.SkyPixels, buffer),
                    "UC sky repeated transitions reuse two native textures and the one CPU bake buffer.");
                cache.HasBaked = false; // Existing seek/settings invalidation deliberately bypasses animated interpolation.
                update.Invoke(lighting, new object[] { nightAtmo, night, cache, 12.01d });
                Check(cache.SkyBlend == 1 && cache.SkyLut == first && cache.PreviousSkyLut == second,
                    "UC sky explicit invalidation immediately publishes the requested sky without allocating another endpoint.");
                update.Invoke(lighting, new object[] { nightAtmo, night, cache, 12.02d });
                Check(cache.SkyBlend == 1 && UCLightingSystemManager.SampleSkyAmbient(cache)[0, 0] == cache.Ambient[0, 0],
                    "UC sky a completed explicit seek cannot resume blending stale light on the next view.");
                release.Invoke(null, new object[] { cache });
                Check(first == null && second == null && cache.SkyPixels == null,
                    "UC sky retirement frees both native endpoints and its retained CPU buffer.");

                double radius = 0.53 * Math.PI / 360;
                float expectedRadiance = (float)(100000 / (2 * Math.PI * (1 - Math.Cos(radius))));
                Check(Math.Abs(SunMoonRig.DiscRadiance(100000, 0.53f) - expectedRadiance) < expectedRadiance * 0.00001,
                    "UC celestial discs convert lux using angular solid angle rather than dim ground-light units.");
                Check(SunMoonRig.DiscRadiance(0, 0.53f) == 0 && SunMoonRig.DiscRadiance(1, 0) == 0,
                    "UC disabled discs have zero radiance without division by zero.");
                float phase0 = ClockMath.LunarPhase01(80, 3000, AtmoConstants.LunarEpochAbsoluteDay, 0);
                float phase12 = ClockMath.LunarPhase01(80, 3000, AtmoConstants.LunarEpochAbsoluteDay, 43200);
                CheckWithin(Mathf.Repeat(phase12 - phase0, 1), (float)(0.5 / AtmoConstants.SynodicMonthDays), 0.000001f,
                    "UC lunar phase advances within a game day even at large calendar years.");
                CheckClose(ClockMath.LunarPhase01(80, 3000, AtmoConstants.LunarEpochAbsoluteDay, 86400),
                    ClockMath.LunarPhase01(81, 3000), "UC lunar phase is continuous across midnight.");
                CheckClose(ClockMath.LunarEclipticLatitudeDeg(80, 3000, 86400), ClockMath.LunarEclipticLatitudeDeg(81, 3000),
                    "UC lunar orbital latitude is continuous across midnight.");
                weather.SetCalendarTime(3000, 80, 12);
                CheckClose(weather.ResolveClock().lunarPhase01, phase12, "UC live clock publishes fractional-day lunar phase to every consumer.");
                CheckWithin(ClockMath.LunarIlluminatedFraction(0), 0, 0.00001f, "UC new moon is unlit.");
                CheckWithin(ClockMath.LunarIlluminatedFraction(0.25f), 0.5f, 0.00001f, "UC quarter moon is half lit.");
                CheckWithin(ClockMath.LunarIlluminatedFraction(0.5f), 1, 0.00001f, "UC full moon is fully lit.");
            }
            finally
            {
                release?.Invoke(null, new object[] { cache });
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // UC midday repair: independent cosine integration and gray-card bounds catch photometric errors that finiteness tests miss.
        private static void TestMeteredDaylightAndSeasons()
        {
            // Unity's peak-normalized directional-probe API differs from the analytical L2 radiance kernel.
            var impulse = new UnityEngine.Rendering.SphericalHarmonicsL2();
            impulse.AddDirectionalLight(Vector3.up, Color.white, 1);
            var impulseValue = new Color[1]; impulse.Evaluate(new[] { Vector3.up }, impulseValue);
            CheckWithin(impulseValue[0].r, 2, 0.0001f, "UC installed Unity directional-probe kernel matches the normalization used by the ambient integrator: actual=" + impulseValue[0].r);
            CheckWithin(MoonLight.HorizonVisibility01(-0.265f, 0.53f), 0, 0.00001f, "UC lunar beam is absent with the whole disc below the horizon.");
            CheckWithin(MoonLight.HorizonVisibility01(0, 0.53f), 0.5f, 0.00001f, "UC half-risen lunar disc publishes half its beam area.");
            CheckWithin(MoonLight.HorizonVisibility01(0.265f, 0.53f), 1, 0.00001f, "UC fully risen lunar disc publishes its complete beam area.");
            Check(MoonLight.HorizonVisibility01(0.0001f, 0.53f) - MoonLight.HorizonVisibility01(-0.0001f, 0.53f) < 0.001f,
                "UC moonrise crosses zero altitude continuously without a full-beam jump.");
            Color lowMoon = MoonLight.ColorLinear(2, 2.5f, out float lowMoonBeam);
            Color highMoon = MoonLight.ColorLinear(90, 2.5f, out float highMoonBeam);
            CheckWithin(highMoonBeam, 1, 0.000001f, "UC lunar atmospheric path preserves the zenith full-moon anchor.");
            Check(lowMoonBeam > 0 && lowMoonBeam < highMoonBeam && lowMoon.r / Mathf.Max(lowMoon.b, 1e-8f) > highMoon.r / Mathf.Max(highMoon.b, 1e-8f),
                "UC low lunar disc and beam use shared atmospheric dimming and reddening.");
            var normals = new[] { Vector3.up, new Vector3(1, 1, 0).normalized, Vector3.forward };
            foreach (float elevation in new[] { 15f, 45f, 85f })
            {
                Vector3 sun = SunMoonRig.DirectionToBody(elevation, 120);
                var probe = AmbientLight.BuildAmbientProbe(sun, 2.5f, 0);
                var evaluated = new Color[normals.Length]; probe.Evaluate(normals, evaluated);
                for (int n = 0; n < normals.Length; n++)
                {
                    Color integrated = Color.black;
                    const int thetaCount = 48, phiCount = 96;
                    double dTheta = Math.PI / (2 * thetaCount), dPhi = 2 * Math.PI / phiCount;
                    for (int t = 0; t < thetaCount; t++)
                    {
                        double theta = (t + 0.5) * dTheta;
                        for (int p = 0; p < phiCount; p++)
                        {
                            double phi = (p + 0.5) * dPhi;
                            var direction = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)),
                                (float)Math.Cos(theta), (float)(Math.Sin(theta) * Math.Sin(phi)));
                            float weight = (float)(Math.Sin(theta) * dTheta * dPhi / Math.PI) * Mathf.Max(0, Vector3.Dot(normals[n], direction));
                            integrated += SkyModel.SkyRadiance(direction, sun, 2.5f) * weight;
                        }
                    }
                    for (int c = 0; c < 3; c++)
                        Check(Mathf.Abs(evaluated[n][c] - integrated[c]) <= Mathf.Max(0.001f, integrated[c] * 0.12f),
                            "UC ambient Unity diffuse evaluation matches independent sky integral: elevation/normal/channel " +
                            elevation + "/" + n + "/" + c + " actual=" + evaluated[n][c] + " expected=" + integrated[c]);
                }
                float airMass = ClockMath.AirMass(elevation);
                float horizontalLux = SkyModel.SunIlluminanceLux(elevation, airMass, 2.5f, 0);
                float exposure = ExposureModel.ExposureMultiplierFromLux(horizontalLux);
                Color spectrum = ExposureModel.UnitLuminanceColor(SkyModel.SunColorLinear(elevation, airMass, 2.5f));
                float normalLux = SkyModel.SunDirectNormalIlluminanceLux(airMass, 2.5f, 0);
                Color direct = spectrum * (ExposureModel.UrpLightIntensity(SkyLightSplit.DirectionalLux(normalLux, 0), exposure) * sun.y);
                float directY = 0.2126f * direct.r + 0.7152f * direct.g + 0.0722f * direct.b;
                float expectedY = horizontalLux * SkyLightSplit.DirectBeamFraction01(0) * exposure / Mathf.PI;
                Check(Mathf.Abs(directY - expectedY) <= expectedY * 0.001f,
                    "UC daylight applies atmospheric brightness, incidence and Lambert normalization once at " + elevation);
                Color gray = (direct + evaluated[0] * exposure) * 0.18f;
                Check(gray.r > 0.02f && gray.g > 0.02f && gray.b > 0.02f && gray.maxColorComponent < 0.5f,
                    "UC physically metered 18-percent gray ground stays readable without clipping at " + elevation + ": " + gray);
            }
            Color warm = ExposureModel.UnitLuminanceColor(SkyModel.SunColorLinear(5, ClockMath.AirMass(5), 2.5f));
            Color noon = ExposureModel.UnitLuminanceColor(SkyModel.SunColorLinear(85, ClockMath.AirMass(85), 2.5f));
            Check(warm.r / Mathf.Max(warm.b, 1e-6f) > noon.r / Mathf.Max(noon.b, 1e-6f),
                "UC low sun is warmer than noon without replacing surface albedo.");
            foreach (int day in new[] { 80, 172, 355, 365 })
            {
                int next = day == 365 ? 1 : day + 1;
                CheckWithin(Mathf.Abs(Mathf.DeltaAngle(ClockMath.WinterPhase01(day, 86400) * 360,
                    ClockMath.WinterPhase01(next, 0) * 360)) / 360,
                    0, 0.000001f, "UC solar seasonal phase has no midnight/year-rollover step on day " + day);
                CheckClose(ClockMath.EquationOfTimeMinutes(day, 86400), ClockMath.EquationOfTimeMinutes(next, 0),
                    "UC apparent solar time has no midnight/year-rollover step on day " + day);
            }
            var root = new GameObject("UC continuous solar publication"); root.SetActive(false);
            try
            {
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                weather.latitudeDeg = 45; weather.longitudeDeg = weather.standardMeridianDeg = 0;
                foreach (int day in new[] { 80, 172, 355, 365 })
                    foreach (float hour in new[] { 5.9f, 12f, 18f, 23.9999f })
                    {
                        weather.SetCalendarTime(3000, day, hour); var a = weather.ResolveClock();
                        weather.SeekWorldInstant(weather.WorldInstantSeconds + 1); var b = weather.ResolveClock();
                        // Vector3.Angle loses precision for sub-degree steps; chord length has the same angular bound.
                        const float maxChord = 0.000174533f; // 2 sin(0.01 degrees / 2).
                        Check((SunMoonRig.DirectionToBody(a.elevationDeg, a.azimuthDeg) -
                            SunMoonRig.DirectionToBody(b.elevationDeg, b.azimuthDeg)).magnitude < maxChord &&
                            (SunMoonRig.DirectionToBody(a.lunarElevationDeg, a.lunarAzimuthDeg) -
                            SunMoonRig.DirectionToBody(b.lunarElevationDeg, b.lunarAzimuthDeg)).magnitude < maxChord,
                            "UC published solar/lunar bearings move continuously per game second at " + day + "/" + hour);
                    }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            var terrainShader = Shader.Find("UniverseCreator/TerrainLit"); var ssrShader = Shader.Find("Hidden/UniverseCreator/SSR");
            Check(terrainShader != null && !UnityEditor.ShaderUtil.ShaderHasError(terrainShader) &&
                ssrShader != null && !UnityEditor.ShaderUtil.ShaderHasError(ssrShader), "UC material-aware reflection shaders import without errors.");
            // UC midday repair: retain the actual importer diagnostics when the aggregate shader check fails.
            foreach (var shader in new[] { terrainShader, ssrShader })
                if (shader != null)
                    foreach (var message in UnityEditor.ShaderUtil.GetShaderMessages(shader))
                        if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                            Check(false, "UC reflection shader compiler: " + shader.name + " " + message.file + ":" + message.line + " " + message.message);
            if (terrainShader != null)
            {
                var material = new Material(terrainShader);
                try { Check(material.FindPass("UCReflectionMask") >= 0, "UC terrain exposes the actual material eligibility pass for SSR."); }
                finally { UnityEngine.Object.DestroyImmediate(material); }
            }
        }

        private static void TestEnvironmentLightingSamples()
        {
            var root = new GameObject("UC sampled lighting world"); root.SetActive(false);
            var otherRoot = new GameObject("UC foreign lighting world"); otherRoot.SetActive(false);
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater);
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.LightingClimateWeather);
            var context = new SnapshotRetirementContext(); var previousContext = SynchronizationContext.Current;
            Material incomingSky = RenderSettings.skybox; Light incomingSun = RenderSettings.sun;
            var incomingAmbient = RenderSettings.ambientProbe; var incomingMode = RenderSettings.ambientMode;
            var incomingLut = Shader.GetGlobalTexture("_SkyLUT"); float incomingExposure = Shader.GetGlobalFloat("_SkyExposure");
            var incomingPreviousLut = Shader.GetGlobalTexture("_UCPreviousSkyLUT"); float incomingBlend = Shader.GetGlobalFloat("_UCSkyBlend");
            UCWeatherSystemManager weather = null, otherWeather = null;
            UCLightingSystemManager lighting = null, otherLighting = null;
            Camera camera = null, otherCamera = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.graph = main; world.autoRegenerate = false;
                world.worldSizeKm = 0.032f; world.chunkCountPerSide = 1;
                var otherWorld = otherRoot.AddComponent<UniverseCreatorObject>(); otherWorld.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = graph;
                var weatherObject = new GameObject("Weather sibling"); weatherObject.transform.SetParent(root.transform, false);
                weather = weatherObject.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                weather.latitudeDeg = 45; weather.longitudeDeg = weather.standardMeridianDeg = 0; weather.rainIntensity = 0.5f;
                var lightingObject = new GameObject("Lighting sibling"); lightingObject.transform.SetParent(root.transform, false);
                lighting = lightingObject.AddComponent<UCLightingSystemManager>(); lighting.enabled = false;
                var sunObject = new GameObject("Authored sun"); sunObject.transform.SetParent(root.transform, false);
                lighting.sun = sunObject.AddComponent<Light>(); lighting.sun.type = LightType.Directional;
                var vegetation = root.AddComponent<UCVegetationSystemManager>(); vegetation.enabled = false;
                var run = world.GenerationScheduler.CurrentRun; long placement = vegetation.PlacementRevision;
                var ev = new ConstantNode { id = "sample-ev", value = 2 };
                var output = new LightingOutputNode { id = "sample-light-output" };
                graph.nodes.Add(ev); graph.nodes.Add(output); graph.connections.Add(new Connection(ev.id, 0, output.id, 4));
                Check(WaitVegetationRun(bindings.RunLightingAsync(false), context), "UC-T08-07 lighting sample fixture accepts the actual dynamic graph.");
                var cameraObject = new GameObject("Lighting camera"); cameraObject.transform.SetParent(root.transform, false);
                camera = cameraObject.AddComponent<Camera>();
                weather.SetCalendarTime(3, 172, 12);
                EnvironmentSampleProduct retained = weather.SampleEnvironmentAt(camera.transform.position);
                float retainedElevation = retained.Clock.elevationDeg;
                weather.SetCalendarTime(4, 355, 0); ev.value = -3;
                Check(WaitVegetationRun(bindings.RunLightingAsync(false), context), "UC-T08-07 replacement lighting is explicitly accepted.");
                Check(retained.Clock.elevationDeg == retainedElevation && retained.ExposureBiasEv == 2 &&
                    retained.WorldInstantSeconds != weather.WorldInstantSeconds && retained.EnvironmentRevision < bindings.LightingRevision,
                    "UC-T08-07 retained sample preserves its clock/bias/revision after backwards/forwards seeks and graph replacement.");
                lighting.AtmosphereForSample(retained, out float retainedEv);
                CheckClose(retainedEv, AtmosphereState.From(retained.Weather, retained.Clock, new Color(0.20f, 0.20f, 0.18f),
                    lighting.settings.turbidity - LightingConstants.TurbidityDefault, lighting.settings.nightFloorLux).exposureEv + 2,
                    "UC-T08-07 retained astronomy, weather and EV reach the existing physical exposure math.");
                lighting.settings.exposureMode = ExposureMode.Manual; lighting.settings.manualExposureEv = 9;
                lighting.AtmosphereForSample(retained, out float manualEv);
                CheckClose(manualEv, 11, "UC-T08-07 Manual mode applies the saved fixed EV and captured graph bias.");
                lighting.settings.exposureMode = ExposureMode.Physical;
                bool testedBrightRadiance = false;
                foreach (int day in new[] { 80, 172, 355 })
                    foreach (float hour in new[] { 0f, 6f, 12f, 18f })
                    {
                        weather.SetCalendarTime(4, day, hour);
                        var sample = weather.SampleEnvironmentAt(camera.transform.position);
                        var atmosphere = lighting.AtmosphereForSample(sample, out float targetEv);
                        bool admitted = weather.BeginEnvironmentView(camera);
                        Check(admitted, "UC-T08-07 sibling lighting/weather owners admit the same camera at day/hour " + day + "/" + hour);
                        if (!admitted) continue;
                        float exposure = Shader.GetGlobalFloat("_SkyExposure"); var sky = RenderSettings.skybox;
                        if (sample.Clock.elevationDeg > 10)
                        {
                            Color linearLight = UnityEngine.Rendering.GraphicsSettings.lightsUseLinearIntensity ? lighting.sun.color.linear : lighting.sun.color;
                            float luminance = 0.2126f * linearLight.r + 0.7152f * linearLight.g + 0.0722f * linearLight.b;
                            float actual = lighting.sun.intensity * luminance * atmosphere.sunDir.y;
                            float expected = atmosphere.sunIlluminanceLux * SkyLightSplit.DirectBeamFraction01(atmosphere.cloudCoverCombined01) * exposure / Mathf.PI;
                            Check(Mathf.Abs(actual - expected) <= Mathf.Max(1e-6f, expected * 0.003f),
                                "UC actual directional-light publication preserves horizontal metered lux without double colour/incidence attenuation.");
                        }
                        CheckClose(exposure, ExposureModel.ExposureMultiplier(targetEv), "UC-T08-07 camera lighting uses the captured exposure stop.");
                        var expectedFog = lighting.SkyRadianceForSample(camera.transform.forward, atmosphere, sample) * exposure;
                        Check(Vector4.Distance(RenderSettings.fogColor, expectedFog) < 0.001f &&
                            Vector3.Distance(-lighting.sun.transform.forward, atmosphere.sunDir) < 0.0001f,
                            "UC-T08-07 actual fog and directional sun consume the same sample at seasonal/day-night boundaries.");
                        Check(sky != null && sky.shader.name == "UniverseCreator/Skybox" && sky.GetTexture("_SkyLUT") == Shader.GetGlobalTexture("_SkyLUT") &&
                            sky.GetFloat("_SkyExposure") == exposure && Shader.GetGlobalVector("_UCSkyDiscParams").x > 0 &&
                            sky.GetTexture("_UCPreviousSkyLUT") == Shader.GetGlobalTexture("_UCPreviousSkyLUT") &&
                            sky.GetFloat("_UCSkyBlend") == Shader.GetGlobalFloat("_UCSkyBlend"),
                            "UC-T08-07 actual sky material properties and sun/moon disc globals are connected.");
                        CheckClose(Shader.GetGlobalVector("_UCMoonColor").w, sample.Clock.lunarIlluminatedFraction,
                            "UC sky phase mask receives the actual authoritative lunar lit fraction.");
                        var moonSpectrum = ExposureModel.UnitLuminanceColor(atmosphere.moonColor);
                        var publishedMoon = Shader.GetGlobalVector("_UCMoonColor");
                        if (publishedMoon.y > 0)
                            CheckWithin(publishedMoon.x / publishedMoon.y, moonSpectrum.r / moonSpectrum.g, 0.0001f,
                                "UC actual moon disc publication retains its atmospheric spectral colour.");
                        var lut = Shader.GetGlobalTexture("_SkyLUT") as Texture2D;
                        Check(lut != null && lut.width == SkyLut.Width && lut.height == SkyLut.Height && IsFinite(lut.GetPixel(0, 0).r) &&
                            IsFinite(RenderSettings.ambientProbe[0, 0]), "UC-T08-07 bounded LUT and night ambient remain finite: " + day + "/" + hour +
                            " LUT red=" + (lut != null ? lut.GetPixel(0, 0).r.ToString() : "missing") + " ambient=" + RenderSettings.ambientProbe[0, 0]);
                        if (lut != null)
                        {
                            Color first = lut.GetPixel(0, 0);
                            bool finitePixels = true;
                            foreach (Color pixel in lut.GetPixels())
                                finitePixels &= IsFinite(pixel.r) && IsFinite(pixel.g) && IsFinite(pixel.b) && IsFinite(pixel.a) && pixel.a == first.a;
                            Check(finitePixels, "UC-T08-14 every native sky texel is finite and shares one interpolation-safe exponent.");
                            Vector3 horizontal = new Vector3(atmosphere.sunDir.x, 0, atmosphere.sunDir.z).normalized;
                            if (horizontal.sqrMagnitude < 1e-6f) horizontal = Vector3.forward;
                            float elevation = Mathf.PI / (4 * SkyLut.Height);
                            Vector3 view = Quaternion.AngleAxis(90f / SkyLut.Width, Vector3.up) * horizontal * Mathf.Cos(elevation) + Vector3.up * Mathf.Sin(elevation);
                            float expectedRed = lighting.SkyRadianceForSample(view.normalized, atmosphere, sample).r;
                            float decodedRed = first.r * Mathf.Pow(2, first.a);
                            Check(IsFinite(decodedRed) && Mathf.Abs(decodedRed - expectedRed) <= Mathf.Max(1e-8f, expectedRed * 0.002f),
                                "UC-T08-14 native half storage decodes to the physical sky radiance without clipping.");
                            testedBrightRadiance |= expectedRed > 65504 && decodedRed > 65504;
                        }
                        Check(!weather.BeginEnvironmentView(camera) && RenderSettings.skybox == sky,
                            "UC-T08-07 recursive reuse cannot overwrite an active camera's LUT.");
                        weather.EndEnvironmentView(camera);
                    }
                Check(testedBrightRadiance, "UC-T08-14 seasonal views exercise radiance beyond half-float range and retain it after decoding.");
                Check(!UnityEditor.ShaderUtil.ShaderHasError(Shader.Find("UniverseCreator/Skybox")),
                    "UC-T08-14 shared sky storage decoder imports without shader errors.");
                // UC midday repair: exercise the actual camera recipients and owned Volume lifetime, not just their helpers.
                weather.SetCalendarTime(4, 172, 12); weather.rainIntensity = weather.fogDensity = 0; weather.cloudCoverage = 0;
                lighting.settings.cloudSoftensShadows = true;
                Check(weather.BeginEnvironmentView(camera), "UC daylight clear-weather publication is admitted.");
                float clearBeam = lighting.sun.intensity;
                Check(clearBeam > 0 && !RenderSettings.fog, "UC clear daylight has a direct beam without precipitation fog.");
                weather.EndEnvironmentView(camera);
                bool incomingFog = RenderSettings.fog; var incomingFogMode = RenderSettings.fogMode;
                Color incomingFogColor = RenderSettings.fogColor; float incomingFogDensity = RenderSettings.fogDensity;
                weather.cloudCoverage = 1; weather.fogDensity = 0.7f;
                var fogSample = weather.SampleEnvironmentAt(camera.transform.position);
                Check(weather.BeginEnvironmentView(camera), "UC daylight overcast/fog publication is admitted.");
                Check(lighting.sun.intensity < clearBeam * 0.01f && lighting.sun.shadowStrength < 0.01f,
                    "UC complete cloud cover removes the clear-sky directional beam and hard shadow strength.");
                Check(RenderSettings.fog && RenderSettings.fogMode == FogMode.Exponential &&
                    Mathf.Abs(RenderSettings.fogDensity - 3 / Mathf.Max(1, fogSample.Weather.visibility)) < 0.000001f,
                    "UC fog uses the sampled viewing distance rather than replacing terrain albedo.");
                weather.EndEnvironmentView(camera);
                Check(RenderSettings.fog == incomingFog && RenderSettings.fogMode == incomingFogMode &&
                    RenderSettings.fogColor == incomingFogColor && RenderSettings.fogDensity == incomingFogDensity,
                    "UC ending the weather view restores the caller's exact fog state.");
                weather.cloudCoverage = 0; weather.fogDensity = 0;
                Vector3 savedCameraPosition = camera.transform.position;
                float savedSunDiameter = lighting.settings.sunDiscSizeDeg, savedMoonDiameter = lighting.settings.moonDiscSizeDeg;
                lighting.settings.sunDiscSizeDeg = 1; lighting.settings.moonDiscSizeDeg = 1.2f;
                Check(weather.BeginEnvironmentView(camera), "UC authored angular-disc settings publication is admitted.");
                Vector4 localDisc = Shader.GetGlobalVector("_UCSkyDiscParams"); weather.EndEnvironmentView(camera);
                camera.transform.position += new Vector3(100000, 0, 100000);
                Check(weather.BeginEnvironmentView(camera), "UC distant camera celestial publication is admitted.");
                Check(Shader.GetGlobalVector("_UCSkyDiscParams") == localDisc &&
                    Mathf.Abs(localDisc.x - 0.5f * Mathf.Deg2Rad) < 0.000001f &&
                    Mathf.Abs(localDisc.z - 0.6f * Mathf.Deg2Rad) < 0.000001f,
                    "UC angular-size controls reach the actual sky and remain independent of camera/world distance.");
                weather.EndEnvironmentView(camera);
                camera.transform.position = savedCameraPosition;
                lighting.settings.sunDiscSizeDeg = savedSunDiameter; lighting.settings.moonDiscSizeDeg = savedMoonDiameter;
#if UC_URP
                var authoredVolumeObject = new GameObject("UC Tonemapping Volume");
                authoredVolumeObject.transform.SetParent(lighting.transform, false);
                var authoredVolume = authoredVolumeObject.AddComponent<UnityEngine.Rendering.Volume>();
                lighting.settings.enableModuleTonemapping = true;
                Check(weather.BeginEnvironmentView(camera), "UC daylight tonemapping opt-in publication is admitted.");
                var volumeField = typeof(UCLightingSystemManager).GetField("_tonemapVolume", BindingFlags.Instance | BindingFlags.NonPublic);
                var ownedVolume = volumeField?.GetValue(lighting) as UnityEngine.Rendering.Volume;
                var ownedProfile = ownedVolume != null ? ownedVolume.sharedProfile : null;
                Check(ownedVolume != null && ownedVolume != authoredVolume && ownedProfile != null && authoredVolume.sharedProfile == null,
                    "UC tonemapping creates its own Volume without adopting a similarly named authored object.");
                weather.EndEnvironmentView(camera);
                lighting.settings.enableModuleTonemapping = false;
                Check(weather.BeginEnvironmentView(camera), "UC daylight tonemapping opt-out publication is admitted.");
                Check(ownedVolume == null && ownedProfile == null && authoredVolume != null && authoredVolume.sharedProfile == null,
                    "UC tonemapping off retires its owned Volume/profile while preserving the authored Volume.");
                weather.EndEnvironmentView(camera);
#endif
                weather.SetCalendarTime(4, 172, 12); var noon = weather.SampleEnvironmentAt(Vector3.zero);
                var noonAtmo = lighting.AtmosphereForSample(noon, out _);
                weather.SetCalendarTime(4, 172, 0); var night = weather.SampleEnvironmentAt(Vector3.zero);
                var nightAtmo = lighting.AtmosphereForSample(night, out _);
                Check(lighting.SkyRadianceForSample(Vector3.up, nightAtmo, night).maxColorComponent <
                    lighting.SkyRadianceForSample(Vector3.up, noonAtmo, noon).maxColorComponent,
                    "UC-T08-07 the below-horizon daytime Perez model does not leave midnight brighter than noon before exposure.");
                Check(world.GenerationScheduler.CurrentRun.Equals(run) && vegetation.PlacementRevision == placement,
                    "UC-T08-07 time/bias/render edits leave terrain and stable vegetation placement revisions unchanged.");

                otherWeather = otherRoot.AddComponent<UCWeatherSystemManager>(); otherWeather.enabled = false; otherWeather.SetCalendarTime(8, 172, 12);
                otherLighting = otherRoot.AddComponent<UCLightingSystemManager>(); otherLighting.enabled = false;
                otherLighting.sun = lighting.sun; // An explicit foreign-world light is invalid and cannot become a second authority.
                var otherCameraObject = new GameObject("Foreign lighting camera"); otherCameraObject.transform.SetParent(otherRoot.transform, false);
                otherCamera = otherCameraObject.AddComponent<Camera>();
                Check(weather.BeginEnvironmentView(camera), "UC-T08-07 first world begins nested lighting fixture.");
                var firstSky = RenderSettings.skybox; var firstLut = Shader.GetGlobalTexture("_SkyLUT") as Texture2D;
                var firstPreviousLut = Shader.GetGlobalTexture("_UCPreviousSkyLUT"); float firstBlend = Shader.GetGlobalFloat("_UCSkyBlend");
                Color firstPixel = firstLut.GetPixel(0, 0); Quaternion firstRotation = lighting.sun.transform.rotation;
                Check(!weather.BeginEnvironmentView(otherCamera) && otherWeather.BeginEnvironmentView(otherCamera) &&
                    RenderSettings.skybox != firstSky && otherLighting.sun != lighting.sun && lighting.sun.transform.rotation == firstRotation,
                    "UC-T08-07 another world gets an independent sky/light and cannot borrow the foreign authored sun.");
                otherWeather.EndEnvironmentView(otherCamera);
                Check(RenderSettings.skybox == firstSky && Shader.GetGlobalTexture("_SkyLUT") == firstLut && firstLut.GetPixel(0, 0) == firstPixel &&
                    Shader.GetGlobalTexture("_UCPreviousSkyLUT") == firstPreviousLut && Shader.GetGlobalFloat("_UCSkyBlend") == firstBlend,
                    "UC-T08-07 nested world restoration retains the first camera's unmodified native sky texture.");
                weather.EndEnvironmentView(camera);
                var disable = typeof(UCLightingSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(disable != null, "UC-T08-07 lifetime fixture resolves the existing disable callback.");
                disable?.Invoke(lighting, null); disable?.Invoke(otherLighting, null);
                Check(firstLut == null && firstSky == null && lighting.sun != null,
                    "UC-T08-07 disable retires native LUT/material resources while retaining the authored light object.");
            }
            finally
            {
                weather?.EndEnvironmentView(camera); otherWeather?.EndEnvironmentView(otherCamera);
                var disable = typeof(UCLightingSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
                if (lighting != null) disable?.Invoke(lighting, null);
                if (otherLighting != null) disable?.Invoke(otherLighting, null);
                var disableWeather = typeof(UCWeatherSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
                if (weather != null) disableWeather?.Invoke(weather, null);
                if (otherWeather != null) disableWeather?.Invoke(otherWeather, null);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(otherRoot);
                UnityEngine.Object.DestroyImmediate(main); UnityEngine.Object.DestroyImmediate(graph);
                context.Drain(); SynchronizationContext.SetSynchronizationContext(previousContext);
                RenderSettings.skybox = incomingSky; RenderSettings.sun = incomingSun;
                RenderSettings.ambientMode = incomingMode; RenderSettings.ambientProbe = incomingAmbient;
                Shader.SetGlobalTexture("_SkyLUT", incomingLut); Shader.SetGlobalFloat("_SkyExposure", incomingExposure);
                Shader.SetGlobalTexture("_UCPreviousSkyLUT", incomingPreviousLut); Shader.SetGlobalFloat("_UCSkyBlend", incomingBlend);
            }
        }
    }
}
