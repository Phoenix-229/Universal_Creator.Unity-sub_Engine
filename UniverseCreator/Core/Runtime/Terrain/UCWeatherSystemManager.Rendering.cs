// UC-T08-05: view adapters on the existing weather owner; no scene-wide weather singleton or competing fog/wind manager.
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Weather;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        private const int MaximumEnvironmentViews = 8;
        private readonly ViewState[] environmentViews = new ViewState[MaximumEnvironmentViews];
        private int environmentViewDepth;
        private UniverseCreatorObject renderWorld;
        private UCLightingSystemManager viewLighting; // UC-T08-07: existing world owner, resolved once and reused by the camera adapter.
        private readonly Camera[] knownCameras = new Camera[MaximumEnvironmentViews];
        private readonly Transform[] knownParents = new Transform[MaximumEnvironmentViews];
        private readonly UniverseCreatorObject[] knownWorlds = new UniverseCreatorObject[MaximumEnvironmentViews];
        private static readonly int WindDirectionId = Shader.PropertyToID("_UCWindDirection");
        private static readonly int WindParamsId = Shader.PropertyToID("_UCWindParams");
        private static readonly int WindGustId = Shader.PropertyToID("_UCWindGust");
        private static readonly int WindResponseId = Shader.PropertyToID("_UCWindResponse");
        private static readonly int SwayTextureId = Shader.PropertyToID("_UCSwayMap");
        private static readonly int SwayWindowId = Shader.PropertyToID("_UCSwayWindow");
        private static readonly int SwayEnabledId = Shader.PropertyToID("_UCSwayMapEnabled");

        /// <summary>Diagnostic for bounded camera rendering; empty means the most recent owned view was admitted.</summary>
        public string EnvironmentViewDiagnostic { get; private set; } = "";

#if UNITY_EDITOR
        /// <summary>Scene camera explicitly borrowed by the reversible editor preview; null outside that preview.</summary>
        /// <remarks>Assigned by the editor driver before Scene rendering; never reparents the camera or admits a gameplay camera.</remarks>
        internal Camera ScenePreviewCamera { get; set; }

        /// <summary>Whether the cached world lighting owner requests module tonemapping for the current editor preview.</summary>
        internal bool ScenePreviewUsesModuleTonemapping => LightingOrNull != null && LightingOrNull.settings.enableModuleTonemapping;

        /// <summary>Restores and retires one preview camera's generated lighting before its editor binding is discarded.</summary>
        /// <param name="camera">Scene camera previously admitted by the preview, or null; call on the editor main thread after rendering.</param>
        internal void RetireScenePreviewCamera(Camera camera)
        {
            EndEnvironmentView(camera);
            LightingOrNull?.RetireSampleCamera(camera);
            for (int i = 0; i < knownCameras.Length; i++)
                if (knownCameras[i] == camera)
                { knownCameras[i] = null; knownParents[i] = null; knownWorlds[i] = null; }
        }
#endif

        private struct ViewState
        {
            internal Camera Camera;
            internal bool Fog;
            internal FogMode Mode;
            internal Color Color;
            internal float Density;
            internal Vector4 WindDirection, WindParams, WindGust, WindResponse, Wetness, SwayWindow;
            internal Texture SwayTexture;
            internal float SwayEnabled;
            internal Vector4 SurfaceSnow;
            internal UCLightingSystemManager.LightingViewState Lighting;
        }

        private void RegisterEnvironmentRendering()
        {
            renderWorld = GetComponentInParent<UniverseCreatorObject>(true);
            viewLighting = FindOwnedEnvironmentManager<UCLightingSystemManager>(renderWorld != null ? renderWorld.transform : transform);
            RenderPipelineManager.beginCameraRendering -= BeginEnvironmentCamera;
            RenderPipelineManager.beginCameraRendering += BeginEnvironmentCamera;
            RenderPipelineManager.endCameraRendering -= EndEnvironmentCamera;
            RenderPipelineManager.endCameraRendering += EndEnvironmentCamera;
        }

        private void UnregisterEnvironmentRendering()
        {
            RenderPipelineManager.beginCameraRendering -= BeginEnvironmentCamera;
            RenderPipelineManager.endCameraRendering -= EndEnvironmentCamera;
            while (environmentViewDepth > 0) { RestoreEnvironmentView(environmentViews[--environmentViewDepth]); environmentViews[environmentViewDepth] = default; }
#if UNITY_EDITOR
            // UC Scene/Play repair: destruction must retire borrowed sky caches before the editor loses the weather owner.
            for (int i = 0; i < knownCameras.Length; i++)
                if (knownCameras[i] != null && knownCameras[i].cameraType == CameraType.SceneView) RetireScenePreviewCamera(knownCameras[i]);
            ScenePreviewCamera = null;
#endif
            System.Array.Clear(knownCameras, 0, knownCameras.Length);
            System.Array.Clear(knownParents, 0, knownParents.Length);
            System.Array.Clear(knownWorlds, 0, knownWorlds.Length);
            ReleaseCameraPrecipitation(); // UC-T08-08: unregistering retires only this owner's generated camera effects.
        }

        private void BeginEnvironmentCamera(ScriptableRenderContext context, Camera camera)
        {
            BeginPrecipitationRendering(camera); // UC-T08-08: exclude this world's pooled particles from foreign views too.
            BeginEnvironmentView(camera);
        }
        private void EndEnvironmentCamera(ScriptableRenderContext context, Camera camera)
        {
            EndEnvironmentView(camera);
            EndPrecipitationRendering(camera);
        }

        internal bool OwnsEnvironmentView(Camera camera)
        {
            if (camera == null) return false;
            bool previewView = false;
#if UNITY_EDITOR
            // UC-T08-15: the editor camera is outside every world hierarchy; borrow only the explicitly previewed Scene view.
            previewView = !Application.isPlaying && camera == ScenePreviewCamera && camera.cameraType == CameraType.SceneView;
#endif
            // UC-T08-14: explicit view sampling can precede OnEnable; resolve the same hierarchy owner without registering render callbacks.
            if (graphBindings == null && renderWorld == null) renderWorld = World;
            UniverseCreatorObject world = graphBindings != null ? graphBindings.Owner : renderWorld;
            for (int i = 0; i < knownCameras.Length; i++)
            {
                if (knownCameras[i] != null && knownCameras[i] != camera && knownCameras[i].isActiveAndEnabled) continue;
                if (knownCameras[i] != camera || knownParents[i] != camera.transform.parent ||
                    knownWorlds[i] == null || !camera.transform.IsChildOf(knownWorlds[i].transform))
                {
                    knownCameras[i] = camera; knownParents[i] = camera.transform.parent;
                    knownWorlds[i] = camera.GetComponentInParent<UniverseCreatorObject>(true);
                }
                if (knownWorlds[i] != null && !ReferenceEquals(knownWorlds[i], world)) return false;
                Transform target = graphBindings != null ? graphBindings.EnvironmentSampleTarget : null;
                return previewView || target == camera.transform || world != null && ReferenceEquals(knownWorlds[i], world);
            }
            EnvironmentViewDiagnostic = "At most eight camera bindings are supported per weather owner; retire unused cameras before adding views.";
            return false;
        }

        internal bool BeginEnvironmentView(Camera camera)
        {
            if (!OwnsEnvironmentView(camera)) return false;
            if (environmentViewDepth >= environmentViews.Length)
            { EnvironmentViewDiagnostic = "Eight nested environment views are already active."; return false; }
            for (int i = 0; i < environmentViewDepth; i++)
                if (environmentViews[i].Camera == camera)
                { EnvironmentViewDiagnostic = "The same camera cannot recursively replace its active environment resources."; return false; }
            // Sample before touching render state: invalid authored inputs retain the caller's fog and shader values.
            EnvironmentSampleProduct sample = SampleEnvironmentAt(camera.transform.position);
            WeatherState local = sample.Weather;
            environmentViews[environmentViewDepth++] = new ViewState {
                Camera = camera, Fog = RenderSettings.fog, Mode = RenderSettings.fogMode, Color = RenderSettings.fogColor,
                Density = RenderSettings.fogDensity, WindDirection = Shader.GetGlobalVector(WindDirectionId),
                WindParams = Shader.GetGlobalVector(WindParamsId), WindGust = Shader.GetGlobalVector(WindGustId),
                WindResponse = Shader.GetGlobalVector(WindResponseId), Wetness = Shader.GetGlobalVector(WetnessCoupling.WetParamsId),
                SwayTexture = Shader.GetGlobalTexture(SwayTextureId), SwayWindow = Shader.GetGlobalVector(SwayWindowId),
                SwayEnabled = Shader.GetGlobalFloat(SwayEnabledId),
                SurfaceSnow = Shader.GetGlobalVector(WetnessCoupling.SurfaceSnowId) // UC-T08-09: restore foreign camera snow too.
            };
            var windOwner = WindOrNull;
            if (windOwner != null) windOwner.PublishEnvironmentWind(local, weatherDrivesWind, windEnabled);
            else UCWindManager.ClearEnvironmentWind();
            WetnessCoupling.Publish(local.wetness, local.precipitationType == WeatherPrecipType.PrecipKind.Snow ? 0 :
                local.precipitationMmPerHour / AtmoConstants.ConvectiveRainReferenceMmPerH, local.relativeHumidity01);
            WetnessCoupling.PublishSurfaceSnow(local.snowDepthM, simulateSurfaceResponse);
            // Built-in URP fog is the usable cheap visibility recipient; optional depth/height fog remains a later feature.
            var atmosphere = AtmosphereState.From(local, sample.Clock, new Color(0.20f, 0.20f, 0.18f), sample.TurbidityBias);
            float exposure = ExposureModel.ExposureMultiplier(atmosphere.exposureEv);
            Color horizon;
            if (viewLighting == null) viewLighting = LightingOrNull;
            if (viewLighting != null && viewLighting.lightingEnabled)
            {
                atmosphere = viewLighting.AtmosphereForSample(sample, out _);
                if (!viewLighting.BeginSampleLighting(camera, sample, out var lightingState, out exposure))
                {
                    EndEnvironmentView(camera);
                    EnvironmentViewDiagnostic = "Lighting's eight camera caches are occupied; retire an unused view before admitting this camera.";
                    return false;
                }
                environmentViews[environmentViewDepth - 1].Lighting = lightingState;
                horizon = viewLighting.SkyRadianceForSample(camera.transform.forward, atmosphere, sample);
            }
            else horizon = SkyModel.SkyRadiance(Vector3.forward, atmosphere.sunDir, atmosphere.turbidity) +
                SkyModel.TwilightRadiance(Vector3.forward, atmosphere.sunDir, atmosphere.turbidity);
            RenderSettings.fogColor = horizon * exposure;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 3 / Mathf.Max(1, local.visibility);
            RenderSettings.fog = local.fogDensity > 0 || local.precipitationMmPerHour > 0;
            EnvironmentViewDiagnostic = "";
            RefreshViewPrecipitation(camera, sample); // UC-T08-08: admitted camera sample drives its bounded native effect.
            return true;
        }

        internal void EndEnvironmentView(Camera camera)
        {
            if (environmentViewDepth == 0 || environmentViews[environmentViewDepth - 1].Camera != camera) return;
            RestoreEnvironmentView(environmentViews[--environmentViewDepth]);
            environmentViews[environmentViewDepth] = default;
            SetPrecipitationView(environmentViewDepth > 0 ? environmentViews[environmentViewDepth - 1].Camera : null);
        }

        private static void RestoreEnvironmentView(ViewState previous)
        {
            if (previous.Lighting.Owner != null) UCLightingSystemManager.RestoreSampleLighting(previous.Lighting);
            RenderSettings.fog = previous.Fog; RenderSettings.fogMode = previous.Mode;
            RenderSettings.fogColor = previous.Color; RenderSettings.fogDensity = previous.Density;
            Shader.SetGlobalVector(WindDirectionId, previous.WindDirection); Shader.SetGlobalVector(WindParamsId, previous.WindParams);
            Shader.SetGlobalVector(WindGustId, previous.WindGust); Shader.SetGlobalVector(WindResponseId, previous.WindResponse);
            Shader.SetGlobalVector(WetnessCoupling.WetParamsId, previous.Wetness);
            Shader.SetGlobalVector(WetnessCoupling.SurfaceSnowId, previous.SurfaceSnow);
            Shader.SetGlobalTexture(SwayTextureId, previous.SwayTexture); Shader.SetGlobalVector(SwayWindowId, previous.SwayWindow);
            Shader.SetGlobalFloat(SwayEnabledId, previous.SwayEnabled);
        }

        internal void RetireLightingViews(UCLightingSystemManager lighting)
        {
            // Disable/reload uses the same stack unwind as normal view end, before releasing any sky resource.
            while (environmentViewDepth > 0 && environmentViews[environmentViewDepth - 1].Lighting.Owner == lighting)
            { RestoreEnvironmentView(environmentViews[--environmentViewDepth]); environmentViews[environmentViewDepth] = default; }
        }

        private UCLightingSystemManager LightingOrNull
        {
            get
            {
                if (viewLighting != null) return viewLighting;
                var world = graphBindings != null ? graphBindings.Owner : GetComponentInParent<UniverseCreatorObject>(true);
                viewLighting = FindOwnedEnvironmentManager<UCLightingSystemManager>(world != null ? world.transform : transform);
                return viewLighting;
            }
        }
    }
}
