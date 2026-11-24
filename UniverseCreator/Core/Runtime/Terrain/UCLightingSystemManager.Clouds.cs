// UC-T08-08: the existing sky owner draws bounded moving cloud sheets; worker baking never creates Unity objects.
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Weather;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCLightingSystemManager
    {
        private static readonly int CloudNoiseId = Shader.PropertyToID("_UCCloudNoise");
        private static readonly int CloudScaleId = Shader.PropertyToID("_UCCloudMetresPerUv");
        private static readonly int CloudQualityId = Shader.PropertyToID("_UCCloudQuality");
        private static readonly int CloudAmbientId = Shader.PropertyToID("_UCCloudAmbient");
        private static readonly int CloudLitId = Shader.PropertyToID("_UCCloudLit");
        private static readonly int CloudWindId = Shader.PropertyToID("_UCCloudWindAxis");
        private static readonly int CloudStormId = Shader.PropertyToID("_UCCloudStorm");
        private static readonly int CloudStormParamsId = Shader.PropertyToID("_UCCloudStormParams");
        private static readonly int[] CloudShapeIds = { Shader.PropertyToID("_UCCloudShape0"), Shader.PropertyToID("_UCCloudShape1"), Shader.PropertyToID("_UCCloudShape2") };
        private static readonly int[] CloudDriftIds = { Shader.PropertyToID("_UCCloudDrift0"), Shader.PropertyToID("_UCCloudDrift1"), Shader.PropertyToID("_UCCloudDrift2") };
        private readonly Texture2D[] cloudNoiseTiers = new Texture2D[3]; // 64/128/256; immutable variants avoid destroying a nested view's incoming texture.
        private readonly Color32[][] cloudNoisePixels = new Color32[3][];
        private Task<Color32[]> cloudNoiseBake;
        private CancellationTokenSource cloudNoiseCancellation;
        private int cloudBakeResolution, cloudFailedResolution;

        /// <summary>Current cloud draw/bake diagnostic; GPU cloud/cookie cost and visual quality require separate rendered acceptance.</summary>
        public string CloudRenderingDiagnostic { get; private set; } = "Cloud noise has not been requested.";

        internal struct CloudViewState
        {
            internal Texture Noise;
            internal float Scale;
            internal Vector4 Shape0, Shape1, Shape2, Drift0, Drift1, Drift2, Quality, Ambient, Lit, Wind, Storm, StormParams;
        }

        private static CloudViewState CaptureCloudView() => new CloudViewState {
            Noise = Shader.GetGlobalTexture(CloudNoiseId), Scale = Shader.GetGlobalFloat(CloudScaleId),
            Shape0 = Shader.GetGlobalVector(CloudShapeIds[0]), Shape1 = Shader.GetGlobalVector(CloudShapeIds[1]), Shape2 = Shader.GetGlobalVector(CloudShapeIds[2]),
            Drift0 = Shader.GetGlobalVector(CloudDriftIds[0]), Drift1 = Shader.GetGlobalVector(CloudDriftIds[1]), Drift2 = Shader.GetGlobalVector(CloudDriftIds[2]),
            Quality = Shader.GetGlobalVector(CloudQualityId), Ambient = Shader.GetGlobalVector(CloudAmbientId), Lit = Shader.GetGlobalVector(CloudLitId),
            Wind = Shader.GetGlobalVector(CloudWindId), Storm = Shader.GetGlobalVector(CloudStormId), StormParams = Shader.GetGlobalVector(CloudStormParamsId)
        };

        private static void RestoreCloudView(in CloudViewState previous)
        {
            Shader.SetGlobalTexture(CloudNoiseId, previous.Noise); Shader.SetGlobalFloat(CloudScaleId, previous.Scale);
            Shader.SetGlobalVector(CloudShapeIds[0], previous.Shape0); Shader.SetGlobalVector(CloudShapeIds[1], previous.Shape1); Shader.SetGlobalVector(CloudShapeIds[2], previous.Shape2);
            Shader.SetGlobalVector(CloudDriftIds[0], previous.Drift0); Shader.SetGlobalVector(CloudDriftIds[1], previous.Drift1); Shader.SetGlobalVector(CloudDriftIds[2], previous.Drift2);
            Shader.SetGlobalVector(CloudQualityId, previous.Quality); Shader.SetGlobalVector(CloudAmbientId, previous.Ambient); Shader.SetGlobalVector(CloudLitId, previous.Lit);
            Shader.SetGlobalVector(CloudWindId, previous.Wind); Shader.SetGlobalVector(CloudStormId, previous.Storm); Shader.SetGlobalVector(CloudStormParamsId, previous.StormParams);
        }

        private Texture2D ResolveCloudNoise(int resolution, bool allowCreate)
        {
            int index = resolution == 64 ? 0 : resolution == 128 ? 1 : 2;
            if (cloudNoiseBake != null && cloudNoiseBake.IsCompleted)
            {
                if (cloudNoiseBake.IsFaulted)
                { CloudRenderingDiagnostic = "Cloud noise failed: " + cloudNoiseBake.Exception.GetBaseException().Message; cloudFailedResolution = cloudBakeResolution; }
                else if (!cloudNoiseBake.IsCanceled)
                {
                    Color32[] pixels = cloudNoiseBake.GetAwaiter().GetResult(); // Completed worker only; no wait on the rendering thread.
                    int readyIndex = cloudBakeResolution == 64 ? 0 : cloudBakeResolution == 128 ? 1 : 2;
                    var texture = new Texture2D(cloudBakeResolution, cloudBakeResolution, TextureFormat.RGBA32, false, true) {
                        name = "UC shared cloud noise " + cloudBakeResolution, filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.DontSave
                    };
                    texture.SetPixels32(pixels); texture.Apply(false, true); cloudNoiseTiers[readyIndex] = texture; cloudNoisePixels[readyIndex] = pixels;
                }
                cloudNoiseBake = null; cloudNoiseCancellation.Dispose(); cloudNoiseCancellation = null;
            }
            if (cloudNoiseTiers[index] != null) return cloudNoiseTiers[index];
            if (cloudNoiseBake != null)
            {
                if (cloudBakeResolution != resolution) cloudNoiseCancellation.Cancel();
                return null; // At most one worker per world, including tier changes/cancellation.
            }
            if (!allowCreate || cloudFailedResolution == resolution) return null;
            int seed = environmentWeather != null ? environmentWeather.regionalWeatherSeed : 2024;
            cloudBakeResolution = resolution; cloudNoiseCancellation = new CancellationTokenSource();
            CancellationToken cancellation = cloudNoiseCancellation.Token;
            cloudNoiseBake = Task.Run(() => CloudNoiseBaker.BuildPixels(seed, resolution, cancellation), cancellation);
            CloudRenderingDiagnostic = "Bounded cloud noise is baking; sky remains usable. Low tiers use optional cover-softened shadows; high tiers can add planar cloud cookies.";
            return null;
        }

        private void PublishSampleClouds(in EnvironmentSampleProduct sample, in AtmosphereState atmosphere, LightingSampleCache cache, float exposure, bool allowCreate)
        {
            LightingTierSettings tier = ResolvedTierSettings;
            Texture2D noise = ResolveCloudNoise(tier.cloudNoiseResolution, allowCreate);
            Shader.SetGlobalTexture(CloudNoiseId, noise);
            if (cache.SkyMaterial != null) cache.SkyMaterial.SetTexture(CloudNoiseId, noise);
            ApplyCloudCookie(sample, cache, tier, noiseAvailable: noise != null);
            Shader.SetGlobalFloat(CloudScaleId, noise != null ? LightingConstants.CloudAdvectionMetresPerUv : 0);
            Shader.SetGlobalVector(CloudQualityId, new Vector4(tier.cloudLayerCount, tier.cloudSelfShadows ? 1 : 0, 0, 0));
            float angle = sample.Weather.windDirectionDeg * Mathf.Deg2Rad;
            Vector2 windAxis = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)), wind = windAxis * sample.Weather.windSpeed;
            Shader.SetGlobalVector(CloudWindId, new Vector4(windAxis.x, windAxis.y, 0, 0));
            Vector2 scroll = CloudRenderBinding.AdvectionUv(wind, sample.WorldInstantSeconds);
            PublishCloudDeck(0, atmosphere.cloudGenusLow, sample.Weather.cloudCoverLow, sample.Weather.cloudBaseM, wind, scroll);
            PublishCloudDeck(1, atmosphere.cloudGenusMid, sample.Weather.cloudCoverMid, sample.Weather.cloudBaseMidM, wind, scroll);
            PublishCloudDeck(2, atmosphere.cloudGenusHigh, sample.Weather.cloudCoverHigh, sample.Weather.cloudBaseHighM, wind, scroll);
            Color fill = SkyRadianceForSample(Vector3.up, atmosphere, sample) * exposure;
            Color lit = fill + atmosphere.sunColor * (atmosphere.sunIlluminanceLux * exposure * DaySkyWeight(sample.Clock.elevationDeg) / Mathf.PI);
            Shader.SetGlobalVector(CloudAmbientId, fill); Shader.SetGlobalVector(CloudLitId, lit);
            Shader.SetGlobalVector(CloudStormId, new Vector4(atmosphere.cloudGenusLow == CloudGenus.Cumulonimbus ? 1 : 0,
                atmosphere.cloudGenusMid == CloudGenus.Cumulonimbus ? 1 : 0, atmosphere.cloudGenusHigh == CloudGenus.Cumulonimbus ? 1 : 0, 0));
            Shader.SetGlobalVector(CloudStormParamsId, new Vector4(1, 0.5f, 0, 0)); // Explicit artistic sheet contrast, not a volumetric cloud claim.
            if (noise != null) CloudRenderingDiagnostic = "Cloud sheets active within tier limits; cover-softened shadows are optional, with bounded planar sun cookies on higher tiers. Volumetric cloud shadows require rendered acceptance.";
        }

        private void ApplyCloudCookie(in EnvironmentSampleProduct sample, LightingSampleCache cache, LightingTierSettings tier, bool noiseAvailable)
        {
            if (sun == null) return;
            bool ownCookie = OwnsCloudCookie(sun.cookie);
            // Authored cookies retain priority; low/medium use the existing cheaper cover-softening instead of allocating a cookie atlas input.
            if (!settings.cloudSoftensShadows || !tier.cloudSelfShadows || !noiseAvailable || sample.Weather.cloudCoverLow <= 0)
            { if (ownCookie) sun.cookie = null; return; }
            if (sun.cookie != null && !ownCookie) return;
            int sourceIndex = tier.cloudNoiseResolution == 64 ? 0 : tier.cloudNoiseResolution == 128 ? 1 : 2;
            Color32[] source = cloudNoisePixels[sourceIndex]; if (source == null) return;
            float cover = sample.Weather.cloudCoverLow, scale = settings.cloudShadowScale;
            if (cache.CloudCookie == null)
            {
                cache.CookiePixels = new Color32[64 * 64];
                cache.CloudCookie = new Texture2D(64, 64, TextureFormat.RGBA32, false, true) {
                    name = "UC planar cloud shadow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.DontSave
                };
            }
            // Five simulated seconds between 64² updates; explicit seeks/cover/scale changes refresh directly without a catch-up loop.
            if (double.IsNaN(cache.CookieInstant) || sample.WorldInstantSeconds < cache.CookieInstant ||
                sample.WorldInstantSeconds - cache.CookieInstant >= 5 || Mathf.Abs(cover - cache.CookieCover) >= 0.1f || scale != cache.CookieScale)
            {
                float angle = sample.Weather.windDirectionDeg * Mathf.Deg2Rad;
                Vector2 wind = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * sample.Weather.windSpeed;
                Vector2 phase = CloudRenderBinding.AdvectionUv(wind * (scale * LightingConstants.CloudAdvectionMetresPerUv), sample.WorldInstantSeconds);
                int edge = tier.cloudNoiseResolution;
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                {
                    int sx = ((int)((x / 64f + phase.x) * edge)) % edge, sy = ((int)((y / 64f + phase.y) * edge)) % edge;
                    float density = Mathf.Clamp01((source[sy * edge + sx].r / 255f - (1 - cover)) * 4);
                    byte transmittance = (byte)(255 * (1 - density * CloudShadows.ShadowContrast01(cover)));
                    cache.CookiePixels[y * 64 + x] = new Color32(transmittance, transmittance, transmittance, 255);
                }
                cache.CloudCookie.SetPixels32(cache.CookiePixels); cache.CloudCookie.Apply(false, false);
                cache.CookieInstant = sample.WorldInstantSeconds; cache.CookieCover = cover; cache.CookieScale = scale;
            }
            // UC-T08-08: retain square cloud projection through Unity's two-dimensional cookie API.
            sun.cookie = cache.CloudCookie; sun.cookieSize2D = Vector2.one / Mathf.Max(0.00005f, scale);
        }

        private bool OwnsCloudCookie(Texture cookie)
        {
            if (cookie == null) return false;
            if (cookie == observerLighting.CloudCookie) return true;
            for (int i = 0; i < cameraLighting.Length; i++) if (cameraLighting[i] != null && cookie == cameraLighting[i].CloudCookie) return true;
            return false;
        }

        private static void PublishCloudDeck(int index, CloudGenus genus, float coverage, float altitude, Vector2 wind, Vector2 scroll)
        {
            CloudSheetParams sheet = CloudRenderBinding.Resolve(genus, coverage, altitude, wind, 0f);
            Shader.SetGlobalVector(CloudShapeIds[index], new Vector4(sheet.coverage01, sheet.edgeSharpness, sheet.flatten01, sheet.stretch));
            Shader.SetGlobalVector(CloudDriftIds[index], new Vector4(scroll.x, scroll.y, sheet.baseAltitudeM, sheet.selfShadowDepth));
        }

        private void ReleaseCloudResources()
        {
            if (cloudNoiseCancellation != null) { cloudNoiseCancellation.Cancel(); cloudNoiseCancellation.Dispose(); cloudNoiseCancellation = null; }
            if (cloudNoiseBake != null && cloudNoiseBake.IsFaulted) _ = cloudNoiseBake.Exception;
            cloudNoiseBake = null;
            for (int i = 0; i < cloudNoiseTiers.Length; i++)
            { if (cloudNoiseTiers[i] != null) ReleaseLightingObject(cloudNoiseTiers[i]); cloudNoiseTiers[i] = null; cloudNoisePixels[i] = null; }
            cloudFailedResolution = 0;
            CloudRenderingDiagnostic = "Cloud resources retired.";
        }
    }
}
