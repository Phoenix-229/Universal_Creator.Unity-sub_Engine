// UC-T08-07: bounded camera caches and reversible rendering on the existing lighting owner.
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Weather;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCLightingSystemManager
    {
        private const int MaximumLightingViews = 8;
        private readonly Camera[] lightingCameras = new Camera[MaximumLightingViews];
        private readonly LightingSampleCache[] cameraLighting = new LightingSampleCache[MaximumLightingViews];
        private Shader sampleSkyShader;
        private LightingViewState initialLighting;
        private bool initialLightingCaptured;

        private void CaptureInitialLighting()
        {
            if (initialLightingCaptured) return;
            initialLighting = CaptureLightingState(); initialLightingCaptured = true;
        }

        internal sealed class LightingSampleCache
        {
            internal Texture2D SkyLut;
            internal Texture2D PreviousSkyLut; // UC Scene/Play: two reusable endpoints; decode each exponent before blending.
            internal Color[] SkyPixels; // UC-T08-14: one bounded reusable bake buffer; all texels share one exponent for linear interpolation.
            internal Material SkyMaterial;
            internal bool HasBaked, HasAdapted;
            internal float ElevationDeg, Turbidity, Cloud, SkyTint, AdaptedEv;
            internal int AdaptedFrame = -1;
            internal int ActiveViews;
            internal SphericalHarmonicsL2 Ambient;
            internal SphericalHarmonicsL2 PreviousAmbient;
            internal float SkyBlend = 1;
            internal double SkyTransitionStarted;
            internal Texture2D CloudCookie;
            internal Color32[] CookiePixels; // Only high-tier shadow users allocate the bounded 64² CPU buffer.
            internal double CookieInstant = double.NaN;
            internal float CookieCover, CookieScale;
        }

        internal struct LightingViewState
        {
            internal UCLightingSystemManager Owner;
            internal Material Skybox;
            internal Light RenderSun;
            internal AmbientMode AmbientMode;
            internal SphericalHarmonicsL2 Ambient;
            internal LightState Sun, Moon;
            internal Texture SkyLut, PreviousSkyLut, IrrA, IrrB, IrrBase, Stars;
            internal Matrix4x4 StarRotation;
            internal Vector4 StarParams;
            internal Vector4 SunDir, MoonDir, SunColor, MoonColor, Disc, Extinction, ShaftColor, IrrOrigin, IrrDims;
            internal float Exposure, SkyBlend, Shafts, Reflection;
            internal bool IrrAtlas;
            internal LightingSampleCache Cache;
            internal CloudViewState Clouds;
        }

        internal struct LightState
        {
            internal Light Light;
            internal Quaternion Rotation;
            internal Color Color;
            internal float Intensity, Shadows;
            internal bool Enabled;
            internal Texture Cookie;
            internal Vector2 CookieSize; // UC-T08-08: restore both authored dimensions after a camera-local cloud projection.
            internal static LightState Capture(Light light) => light == null ? default : new LightState {
                Light = light, Rotation = light.transform.rotation, Color = light.color, Intensity = light.intensity,
                Shadows = light.shadowStrength, Enabled = light.enabled, Cookie = light.cookie, CookieSize = light.cookieSize2D
            };
            internal void Restore()
            {
                if (Light == null) return;
                Light.transform.rotation = Rotation; Light.color = Color; Light.intensity = Intensity;
                Light.shadowStrength = Shadows; Light.enabled = Enabled;
                Light.cookie = Cookie; Light.cookieSize2D = CookieSize;
            }
        }

        private bool IsLocalSun(Light candidate, bool explicitlyAssigned)
        {
            var world = candidate.GetComponentInParent<UniverseCreatorObject>(true);
            return world != null ? ReferenceEquals(world, World) : explicitlyAssigned || candidate.transform.IsChildOf(transform);
        }

        /// <summary>Resets bounded camera/observer adaptation and sky caches on an explicit clock seek, settings edit or accepted graph replacement.</summary>
        /// <remarks>Main thread; retains native textures for reuse and never advances a second clock.</remarks>
        internal void ResetEnvironmentLighting()
        {
            irradianceInputsValid = false; // UC-T08-12: explicit seek/settings/graph acceptance dirties the existing grid.
            observerLighting.HasAdapted = observerLighting.HasBaked = false;
            observerLighting.CookieInstant = double.NaN;
            for (int i = 0; i < cameraLighting.Length; i++)
                if (cameraLighting[i] != null)
                { cameraLighting[i].HasAdapted = cameraLighting[i].HasBaked = false; cameraLighting[i].CookieInstant = double.NaN; }
        }

        /// <summary>Applies one owned camera's captured environment and returns a reversible rendering state plus its effective exposure.</summary>
        /// <param name="camera">Admitted camera from the existing weather view adapter.</param>
        /// <param name="sample">Immutable environment from this world's shared clock and accepted graph.</param>
        /// <param name="previous">Incoming sky, ambient, light and shader bindings; restore at the matching view end.</param>
        /// <param name="exposure">Actual multiplier shared by lights, sky and fog; adaptation steps at most once per camera per rendered frame.</param>
        /// <returns>False when lighting is disabled or all eight camera slots are in use; incoming rendering is retained.</returns>
        internal bool BeginSampleLighting(Camera camera, in EnvironmentSampleProduct sample, out LightingViewState previous, out float exposure)
        {
            previous = default;
            AtmosphereForSample(sample, out float targetEv);
            exposure = ExposureModel.ExposureMultiplier(targetEv);
            if (!lightingEnabled || camera == null) return false;
            if (environmentWeather == null) environmentWeather = UCWeatherSystemManager.FindForWorld(transform);
            int index = -1;
            for (int i = 0; i < lightingCameras.Length; i++)
                if (lightingCameras[i] == camera) { index = i; break; }
            if (index < 0)
                for (int i = 0; i < lightingCameras.Length; i++)
                    if ((lightingCameras[i] == null || !lightingCameras[i].isActiveAndEnabled) &&
                        (cameraLighting[i] == null || cameraLighting[i].ActiveViews == 0))
                    {
                        ReleaseSampleCache(cameraLighting[i]);
                        lightingCameras[i] = camera; cameraLighting[i] = new LightingSampleCache(); index = i; break;
                    }
            if (index < 0) return false;
            // Creation changes no globals and new lights start disabled, so their pre-view state is also reversible.
            ResolveSunLight(allowCreate: true); ResolveMoonLight(allowCreate: true);
            previous = CaptureLightingState();
            previous.Owner = this;
            previous.Cache = cameraLighting[index]; previous.Cache.ActiveViews++;
            try
            {
                exposure = ApplySampleLighting(sample, cameraLighting[index], allowCreate: true, animate: Application.isPlaying, refreshIrradiance: false);
            }
            catch
            {
                RestoreSampleLighting(previous); previous = default;
                throw; // Native publication failed: restore the incoming view without hiding the error.
            }
            return true;
        }

        private LightingViewState CaptureLightingState() => new LightingViewState {
            Skybox = RenderSettings.skybox, RenderSun = RenderSettings.sun,
            AmbientMode = RenderSettings.ambientMode, Ambient = RenderSettings.ambientProbe,
            Sun = LightState.Capture(sun != null && IsLocalSun(sun, explicitlyAssigned: true) ? sun : null), Moon = LightState.Capture(_moon),
            SkyLut = Shader.GetGlobalTexture(s_SkyLutId), Exposure = Shader.GetGlobalFloat(s_SkyExposureId),
            PreviousSkyLut = Shader.GetGlobalTexture(s_PreviousSkyLutId), SkyBlend = Shader.GetGlobalFloat(s_SkyBlendId),
            Stars = Shader.GetGlobalTexture(s_StarsId), StarRotation = Shader.GetGlobalMatrix(s_StarRotationId),
            StarParams = Shader.GetGlobalVector(s_StarParamsId),
            SunDir = Shader.GetGlobalVector(s_SunDirId), MoonDir = Shader.GetGlobalVector(s_MoonDirId),
            SunColor = Shader.GetGlobalVector(s_SunColorId), MoonColor = Shader.GetGlobalVector(s_MoonColorId),
            Disc = Shader.GetGlobalVector(s_DiscParamsId), Extinction = Shader.GetGlobalVector(s_BeamExtinctionId),
            Shafts = Shader.GetGlobalFloat(s_ShaftIntensityId), ShaftColor = Shader.GetGlobalVector(s_ShaftColorId),
            Reflection = Shader.GetGlobalFloat(s_EnvReflectionStrengthId),
            IrrOrigin = Shader.GetGlobalVector(s_IrrGridOriginId), IrrDims = Shader.GetGlobalVector(s_IrrGridDimsId),
            IrrA = Shader.GetGlobalTexture(s_IrrAId), IrrB = Shader.GetGlobalTexture(s_IrrBId), IrrBase = Shader.GetGlobalTexture(s_IrrBaseYId),
            IrrAtlas = Shader.IsKeywordEnabled(IrradianceUpload.KeywordAtlas), Clouds = CaptureCloudView()
        };

        /// <summary>Restores the incoming camera bindings and lights before any cached resource can be reused or released.</summary>
        /// <param name="previous">State returned by one successful BeginSampleLighting; restore once at its matching view end, on the main thread.</param>
        internal static void RestoreSampleLighting(in LightingViewState previous)
        {
            if (previous.Cache != null) previous.Cache.ActiveViews--;
            RestoreCloudView(previous.Clouds);
            previous.Sun.Restore(); previous.Moon.Restore();
            RenderSettings.skybox = previous.Skybox; RenderSettings.sun = previous.RenderSun;
            RenderSettings.ambientMode = previous.AmbientMode; RenderSettings.ambientProbe = previous.Ambient;
            Shader.SetGlobalTexture(s_SkyLutId, previous.SkyLut); Shader.SetGlobalFloat(s_SkyExposureId, previous.Exposure);
            Shader.SetGlobalTexture(s_PreviousSkyLutId, previous.PreviousSkyLut); Shader.SetGlobalFloat(s_SkyBlendId, previous.SkyBlend);
            Shader.SetGlobalTexture(s_StarsId, previous.Stars); Shader.SetGlobalMatrix(s_StarRotationId, previous.StarRotation);
            Shader.SetGlobalVector(s_StarParamsId, previous.StarParams);
            Shader.SetGlobalVector(s_SunDirId, previous.SunDir); Shader.SetGlobalVector(s_MoonDirId, previous.MoonDir);
            Shader.SetGlobalVector(s_SunColorId, previous.SunColor); Shader.SetGlobalVector(s_MoonColorId, previous.MoonColor);
            Shader.SetGlobalVector(s_DiscParamsId, previous.Disc); Shader.SetGlobalVector(s_BeamExtinctionId, previous.Extinction);
            Shader.SetGlobalFloat(s_ShaftIntensityId, previous.Shafts); Shader.SetGlobalVector(s_ShaftColorId, previous.ShaftColor);
            Shader.SetGlobalFloat(s_EnvReflectionStrengthId, previous.Reflection);
            Shader.SetGlobalVector(s_IrrGridOriginId, previous.IrrOrigin); Shader.SetGlobalVector(s_IrrGridDimsId, previous.IrrDims);
            Shader.SetGlobalTexture(s_IrrAId, previous.IrrA); Shader.SetGlobalTexture(s_IrrBId, previous.IrrB); Shader.SetGlobalTexture(s_IrrBaseYId, previous.IrrBase);
            if (previous.IrrAtlas) Shader.EnableKeyword(IrradianceUpload.KeywordAtlas); else Shader.DisableKeyword(IrradianceUpload.KeywordAtlas);
        }

        // Below-horizon daylight is outside the retained Perez model's domain. Smoothly retire it through civil twilight.
        private static float DaySkyWeight(float elevationDeg) => Mathf.SmoothStep(0, 1, SunMoonRig.SunTwilightWeight01(elevationDeg));

        /// <summary>Returns linear sky radiance for the captured clock/weather, including twilight and the explicit artistic night floor.</summary>
        /// <param name="direction">World-space view direction; +Y is up.</param>
        /// <param name="atmosphere">Atmosphere derived from the same sample and lighting settings.</param>
        /// <param name="sample">Captured sun geometry, cloud state and artistic tint.</param>
        /// <returns>Nonnegative linear radiance in cd/m², before the shared exposure; used by the LUT and camera fog.</returns>
        internal Color SkyRadianceForSample(Vector3 direction, in AtmosphereState atmosphere, in EnvironmentSampleProduct sample)
        {
            float day = DaySkyWeight(sample.Clock.elevationDeg), cover = atmosphere.cloudCoverCombined01;
            Color radiance = SkyModel.SkyRadiance(direction, atmosphere.sunDir, atmosphere.turbidity) * day +
                SkyModel.TwilightRadiance(direction, atmosphere.sunDir, atmosphere.turbidity);
            float floor = NightFloor.Apply(LightingConstants.StarlightLux, settings.nightFloorLux) * (1 - day) / Mathf.PI;
            radiance += new Color(floor, floor, floor, 0);
            float luminance = 0.2126f * radiance.r + 0.7152f * radiance.g + 0.0722f * radiance.b;
            radiance = Color.Lerp(radiance, new Color(luminance, luminance, luminance, 1), cover) *
                Mathf.Lerp(1, LightingConstants.OvercastDayLux / LightingConstants.NoonClearSkyLux, cover);
            Color tint = sample.SkyTint >= 0 ? new Color(1, 0.85f, 0.65f) : new Color(0.65f, 0.85f, 1);
            radiance *= Color.Lerp(Color.white, tint, Mathf.Clamp01(Mathf.Abs(sample.SkyTint)));
            radiance.a = 1;
            return radiance;
        }

        private void BindSampleSkybox(LightingSampleCache cache, float exposure, bool allowCreate)
        {
            if (cache.SkyMaterial == null && allowCreate)
            {
                if (sampleSkyShader == null) sampleSkyShader = Shader.Find("UniverseCreator/Skybox");
                if (sampleSkyShader != null) cache.SkyMaterial = new Material(sampleSkyShader) { name = "UC sampled sky", hideFlags = HideFlags.DontSave };
            }
            if (cache.SkyMaterial == null) return;
            // Material properties shadow shader globals: both recipients must receive the identical LUT and exposure.
            cache.SkyMaterial.SetTexture(s_SkyLutId, cache.SkyLut); cache.SkyMaterial.SetFloat(s_SkyExposureId, exposure);
            cache.SkyMaterial.SetTexture(s_PreviousSkyLutId, cache.PreviousSkyLut != null ? cache.PreviousSkyLut : cache.SkyLut);
            cache.SkyMaterial.SetFloat(s_SkyBlendId, cache.SkyBlend);
            RenderSettings.skybox = cache.SkyMaterial;
        }

        private void BindViewIrradiance()
        {
            if (!settings.enableIrradianceGI || _irrGrid == null || _irrVolA == null) { UnbindIrradianceGrid(); return; }
            Shader.DisableKeyword(IrradianceUpload.KeywordAtlas);
            Shader.SetGlobalVector(s_IrrGridOriginId, new Vector4(_irrGrid.originXZ.x, _irrGrid.originXZ.y, _irrGrid.spacingM, _irrGrid.verticalSpacingM));
            Shader.SetGlobalVector(s_IrrGridDimsId, new Vector4(_irrGrid.dimX, _irrGrid.dimY, _irrGrid.dimZ, 0));
            Shader.SetGlobalTexture(s_IrrAId, _irrVolA); Shader.SetGlobalTexture(s_IrrBId, _irrVolB); Shader.SetGlobalTexture(s_IrrBaseYId, _irrBaseY);
            _irrBound = true; // UC-T08-12: retirement tracks view bindings as well as upload bindings.
        }

        private static void ReleaseSampleCache(LightingSampleCache cache)
        {
            if (cache == null) return;
            if (cache.SkyMaterial != null) ReleaseLightingObject(cache.SkyMaterial);
            if (cache.SkyLut != null) ReleaseLightingObject(cache.SkyLut);
            if (cache.PreviousSkyLut != null) ReleaseLightingObject(cache.PreviousSkyLut);
            if (cache.CloudCookie != null) ReleaseLightingObject(cache.CloudCookie);
            cache.SkyMaterial = null; cache.SkyLut = cache.PreviousSkyLut = null; cache.HasBaked = cache.HasAdapted = false;
            cache.SkyPixels = null;
            cache.CloudCookie = null; cache.CookieInstant = double.NaN;
            cache.CookiePixels = null;
        }

        /// <summary>Retires one camera's bounded sky/cloud cache after its final environment view has restored incoming bindings.</summary>
        /// <param name="camera">Camera no longer borrowed by the editor preview; null and unknown cameras are harmless.</param>
        /// <remarks>Main thread. An active nested view retains its resources until it ends; other cameras and the observer cache are untouched.</remarks>
        internal void RetireSampleCamera(Camera camera)
        {
            if (camera == null) return;
            for (int i = 0; i < lightingCameras.Length; i++)
            {
                if (lightingCameras[i] != camera) continue;
                if (cameraLighting[i] != null && cameraLighting[i].ActiveViews > 0) return;
                // UC-T08-15: stopped previews must not occupy the existing eight-camera budget.
                ReleaseSampleCache(cameraLighting[i]); cameraLighting[i] = null; lightingCameras[i] = null;
                // UC midday repair: the last retired edit preview must not leave a global tone Volume behind.
                if (!Application.isPlaying && observerLighting.SkyLut == null)
                {
                    bool retainedView = false;
                    for (int j = 0; j < cameraLighting.Length; j++) retainedView |= cameraLighting[j] != null;
                    if (!retainedView) ReleaseOwnedTonemapping();
                }
                return;
            }
        }

        private static void ReleaseLightingObject(Object value)
        {
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        private void ReleaseEnvironmentLighting()
        {
            environmentWeather?.RetireLightingViews(this);
            // Restore only when this owner's observer bindings are still current; a foreign world retains its state.
            if (observerLighting.SkyLut != null && Shader.GetGlobalTexture(s_SkyLutId) == observerLighting.SkyLut) RestoreSampleLighting(initialLighting);
            ReleaseSampleCache(observerLighting);
            for (int i = 0; i < cameraLighting.Length; i++)
            { ReleaseSampleCache(cameraLighting[i]); cameraLighting[i] = null; lightingCameras[i] = null; }
            RetireIrradianceGrid(); // UC-T08-12: retire cached delegates as well as textures and queued work.
            if (createdSun != null) { ReleaseLightingObject(createdSun.gameObject); createdSun = null; sun = null; }
            if (createdMoon != null) { ReleaseLightingObject(createdMoon.gameObject); createdMoon = null; _moon = null; }
            ReleaseOwnedTonemapping();
            ReleaseCloudResources();
            // UC celestial repair: restore observer/view bindings before destroying the catalog texture.
            if (starSky != null) ReleaseLightingObject(starSky.Texture);
            starSky = null; starCatalogAttempted = false;
            initialLightingCaptured = false;
        }
    }
}
