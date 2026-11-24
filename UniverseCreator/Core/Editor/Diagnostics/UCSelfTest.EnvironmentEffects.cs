// UC-T08-08: bounded worker noise, real tier/sky/cookie/particle recipients and camera-pool retirement.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestBoundedEnvironmentEffects()
        {
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false; try { CloudNoiseBaker.BuildPixels(17, 64, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            cancelled.Dispose();
            bool invalid = false; try { CloudNoiseBaker.BuildPixels(17, 65); } catch (ArgumentOutOfRangeException) { invalid = true; }
            Check(stopped && invalid, "UC-T08-08 noise workers reject invalid dimensions and pre-allocation cancellation.");
            Vector2 wind = new Vector2(12, -3); double distant = 9998d * 365 * ClockState.SecondsPerDay;
            Vector2 phase = CloudRenderBinding.AdvectionUv(wind, distant), next = CloudRenderBinding.AdvectionUv(wind, distant + 0.5);
            Check(phase.x >= 0 && phase.x < 1 && phase.y >= 0 && phase.y < 1 && phase != next && phase == CloudRenderBinding.AdvectionUv(wind, distant),
                "UC-T08-08 repeating drift retains subsecond motion and deterministic seeks near year 9999.");
            int previousParticles = 0;
            foreach (AdaptiveQuality.QualityTier quality in new[] { AdaptiveQuality.QualityTier.Low, AdaptiveQuality.QualityTier.Medium, AdaptiveQuality.QualityTier.High, AdaptiveQuality.QualityTier.Ultra })
            {
                var tier = LightingTier.Resolve(quality);
                Check(tier.cloudLayerCount >= 1 && tier.cloudLayerCount <= 3 && tier.cloudNoiseResolution >= 64 && tier.cloudNoiseResolution <= 256 &&
                    tier.precipitationParticleLimit > previousParticles && tier.precipitationParticleLimit <= 4096,
                    "UC-T08-08 resolved tier bounds cloud sheets/noise and native camera particles: " + quality);
                previousParticles = tier.precipitationParticleLimit;
            }
            var root = new GameObject("UC bounded environment effects"); root.SetActive(false);
            UCWeatherSystemManager weather = null; UCLightingSystemManager lighting = null;
            Camera[] cameras = new Camera[9]; Texture cloudTexture = null, cookie = null; Material particleMaterial = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                weather.temperatureCelsius = 20; weather.rainIntensity = 1; weather.cloudCoverage = 0.5f; weather.SetCalendarTime(3, 172, 12);
                lighting = root.AddComponent<UCLightingSystemManager>(); lighting.enabled = false;
                lighting.followAdaptiveQuality = false; lighting.lightingTierOverride = AdaptiveQuality.QualityTier.Low;
                lighting.settings.cloudSoftensShadows = true;
                for (int i = 0; i < cameras.Length; i++)
                {
                    var go = new GameObject("UC pooled camera " + i); go.transform.SetParent(root.transform, false);
                    cameras[i] = go.AddComponent<Camera>();
                }
                root.SetActive(true); // Disabled generators/clock/lighting remain disabled; camera identities are live for the bounded registry.
                for (int i = 0; i < 8; i++)
                {
                    Check(weather.BeginEnvironmentView(cameras[i]), "UC-T08-08 pooled camera receives a bounded native effect " + i);
                    var emitter = cameras[i].GetComponentInChildren<PrecipitationEmitter>();
                    var particles = emitter != null ? emitter.GetComponent<ParticleSystem>() : null;
                    Check(particles != null && particles.main.maxParticles == 512 && particles.shape.scale == new Vector3(32, 10, 32) &&
                        particles.emission.rateOverTime.constant > 0 && particles.main.simulationSpace == ParticleSystemSimulationSpace.World,
                        "UC-T08-08 real phone particles stay inside a camera-near box and hard native cap.");
                    if (emitter != null) particleMaterial = emitter.GetComponent<ParticleSystemRenderer>().sharedMaterial;
                    weather.EndEnvironmentView(cameras[i]);
                }
                Check(weather.CachedPrecipitationViews == 8 && !weather.BeginEnvironmentView(cameras[8]),
                    "UC-T08-08 a ninth live camera cannot grow the bounded effect pool.");
                var beginRender = typeof(UCWeatherSystemManager).GetMethod("BeginEnvironmentCamera", BindingFlags.Instance | BindingFlags.NonPublic);
                var endRender = typeof(UCWeatherSystemManager).GetMethod("EndEnvironmentCamera", BindingFlags.Instance | BindingFlags.NonPublic);
                var context = default(UnityEngine.Rendering.ScriptableRenderContext);
                var firstRenderer = cameras[0].GetComponentInChildren<ParticleSystemRenderer>();
                beginRender.Invoke(weather, new object[] { context, cameras[0] });
                Check(firstRenderer.enabled && !cameras[1].GetComponentInChildren<ParticleSystemRenderer>().enabled,
                    "UC-T08-08 one rendered camera excludes sibling pooled precipitation.");
                var foreignRoot = new GameObject("UC foreign precipitation view");
                try
                {
                    var foreignCamera = foreignRoot.AddComponent<Camera>();
                    beginRender.Invoke(weather, new object[] { context, foreignCamera });
                    Check(!firstRenderer.enabled, "UC-T08-08 foreign nested views cannot render this world's generated particles.");
                    endRender.Invoke(weather, new object[] { context, foreignCamera });
                    Check(firstRenderer.enabled, "UC-T08-08 ending a foreign nested view restores the incoming emitter visibility.");
                }
                finally { UnityEngine.Object.DestroyImmediate(foreignRoot); }
                endRender.Invoke(weather, new object[] { context, cameras[0] });
                Check(!firstRenderer.enabled, "UC-T08-08 generated renderers remain hidden outside their camera pass.");
                cameras[7].enabled = false;
                typeof(UCWeatherSystemManager).GetMethod("RetireInactivePrecipitation", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(weather, null);
                Check(weather.CachedPrecipitationViews == 7 && cameras[7].GetComponentInChildren<PrecipitationEmitter>() == null,
                    "UC-T08-08 disabling a camera retires its obsolete native pool entry without another camera allocation.");
                var firstEmitter = cameras[0].GetComponentInChildren<PrecipitationEmitter>(); var firstSystem = firstEmitter.GetComponent<ParticleSystem>();
                firstSystem.Emit(8); cameras[0].transform.position = new Vector3(1000, 200, 0);
                Check(weather.BeginEnvironmentView(cameras[0]) && firstSystem.particleCount == 0 && weather.CachedPrecipitationViews == 7 &&
                    cameras[0].GetComponentInChildren<PrecipitationEmitter>() == firstEmitter,
                    "UC-T08-08 teleport clears obsolete world-space particles while reusing the existing emitter.");
                weather.EndEnvironmentView(cameras[0]);
                float cloudBase = weather.SampleEnvironmentAt(Vector3.zero).Weather.cloudBaseM;
                Check(cloudBase > 0 && cloudBase == weather.SampleEnvironmentAt(new Vector3(0, 200, 0)).Weather.cloudBaseM,
                    "UC-T08-08 LCL cloud bases use a fixed world datum and do not follow a climbing camera.");
                weather.temperatureCelsius = -15; weather.BeginEnvironmentView(cameras[0]);
                Check(firstEmitter.GetComponent<ParticleSystemRenderer>().renderMode == ParticleSystemRenderMode.Billboard && firstSystem.velocityOverLifetime.y.constant < 0,
                    "UC-T08-08 freezing switches the existing effect to snow fall/billboard recipients.");
                weather.EndEnvironmentView(cameras[0]);
                firstEmitter.SetQuality(0); weather.BeginEnvironmentView(cameras[0]);
                Check(firstSystem.emission.rateOverTime.constant == 0 && firstSystem.particleCount == 0,
                    "UC-T08-08 disabled effects cannot be restarted by a later view sample.");
                weather.EndEnvironmentView(cameras[0]); firstEmitter.SetQuality(1);
                weather.temperatureCelsius = 20; lighting.lightingTierOverride = AdaptiveQuality.QualityTier.High;
                // UC-T08-08: a generated square cookie must not discard the incoming light's rectangular projection.
                Vector2 incomingCookieSize = new Vector2(384, 768); lighting.sun.cookieSize2D = incomingCookieSize;
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 15000 && cloudTexture == null)
                {
                    weather.BeginEnvironmentView(cameras[0]);
                    cloudTexture = Shader.GetGlobalTexture("_UCCloudNoise"); cookie = lighting.sun.cookie;
                    if (cloudTexture != null)
                        Check(RenderSettings.skybox.GetTexture("_UCCloudNoise") == cloudTexture && Shader.GetGlobalVector("_UCCloudQuality").x == 3 &&
                            Shader.GetGlobalFloat("_UCCloudMetresPerUv") > 0 && cookie != null && cookie.width == 64 &&
                            Mathf.Approximately(lighting.sun.cookieSize2D.x, 1 / lighting.settings.cloudShadowScale) &&
                            Mathf.Approximately(lighting.sun.cookieSize2D.y, 1 / lighting.settings.cloudShadowScale),
                            "UC-T08-08 completed worker pixels reach the actual sky material, three-sheet shader and bounded sun cookie.");
                    weather.EndEnvironmentView(cameras[0]);
                    if (cloudTexture == null) Thread.Sleep(10); // Test-only bounded wait; runtime never waits for a worker.
                }
                Check(cloudTexture != null && firstSystem.main.maxParticles == 2048, "UC-T08-08 higher-tier worker/camera budgets complete within the bounded fixture wait.");
                Check(lighting.sun.cookieSize2D == incomingCookieSize,
                    "UC-T08-08 ending a cloud-shadow view restores both incoming rectangular cookie dimensions.");
                var disableWeather = typeof(UCWeatherSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
                var disableLighting = typeof(UCLightingSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
                disableWeather?.Invoke(weather, null); disableLighting?.Invoke(lighting, null);
                Check(weather.CachedPrecipitationViews == 0 && firstEmitter == null && particleMaterial == null && cloudTexture == null && cookie == null,
                    "UC-T08-08 owner retirement releases pooled effects/materials and all owned cloud/cookie textures.");
            }
            finally
            {
                if (weather != null)
                {
                    for (int i = cameras.Length - 1; i >= 0; i--) weather.EndEnvironmentView(cameras[i]);
                    typeof(UCWeatherSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(weather, null);
                }
                if (lighting != null) typeof(UCLightingSystemManager).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(lighting, null);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
