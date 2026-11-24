// UC-T08-08: eight camera-near adapters on the existing weather owner, reusing PrecipitationEmitter.
using UnityEngine;
using UniverseCreator.Weather;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        private readonly PrecipitationEmitter[] viewPrecipitation = new PrecipitationEmitter[MaximumEnvironmentViews];
        private readonly Camera[] precipitationCameras = new Camera[MaximumEnvironmentViews];
        private readonly Vector3[] precipitationPositions = new Vector3[MaximumEnvironmentViews];
        private readonly bool[] ownsPrecipitation = new bool[MaximumEnvironmentViews];
        private Material precipitationMaterial;
        private readonly ParticleSystemRenderer[] precipitationRenderers = new ParticleSystemRenderer[MaximumEnvironmentViews];
        private readonly Camera[] precipitationRenderViews = new Camera[MaximumEnvironmentViews];
        private readonly int[] precipitationRenderMasks = new int[MaximumEnvironmentViews];
        private int precipitationRenderDepth;

        /// <summary>Cached camera precipitation adapters, at most eight; native particles have separate lighting-tier caps.</summary>
        public int CachedPrecipitationViews
        {
            get { int count = 0; for (int i = 0; i < viewPrecipitation.Length; i++) if (viewPrecipitation[i] != null) count++; return count; }
        }

        private void RefreshViewPrecipitation(Camera camera, in EnvironmentSampleProduct sample)
        {
            int index = -1;
            for (int i = 0; i < precipitationCameras.Length; i++) if (precipitationCameras[i] == camera) { index = i; break; }
            if (index < 0)
                for (int i = 0; i < precipitationCameras.Length; i++)
                    if (precipitationCameras[i] == null || !precipitationCameras[i].isActiveAndEnabled)
                    { RetirePrecipitationSlot(i); index = i; break; }
            if (index < 0) return;
            if (viewPrecipitation[index] == null)
            {
                if (sample.Weather.precipitationMmPerHour < AtmoConstants.PrecipDetectableMmPerH) return;
                var existing = FindOwnedEnvironmentManager<PrecipitationEmitter>(camera.transform); // Reuse the existing traversal that excludes nested worlds.
                if (existing == null)
                {
                    if (precipitationMaterial == null)
                    {
                        Shader shader = Shader.Find("UniverseCreator/Precipitation");
                        if (shader == null) { EnvironmentViewDiagnostic = "UC precipitation shader unavailable; particles were not created."; return; }
                        precipitationMaterial = new Material(shader) { name = "UC pooled precipitation", hideFlags = HideFlags.DontSave };
                    }
                    var effect = new GameObject("UC camera precipitation"); effect.hideFlags = HideFlags.DontSave;
                    effect.layer = camera.gameObject.layer; effect.transform.SetParent(camera.transform, false);
                    existing = effect.AddComponent<PrecipitationEmitter>(); existing.ConfigureCameraVolume();
                    effect.GetComponent<ParticleSystemRenderer>().sharedMaterial = precipitationMaterial;
                    ownsPrecipitation[index] = true;
                }
                viewPrecipitation[index] = existing; precipitationCameras[index] = camera;
                precipitationRenderers[index] = existing.GetComponent<ParticleSystemRenderer>();
                precipitationPositions[index] = camera.transform.position; existing.BindWeatherSource(this);
            }
            var emitter = viewPrecipitation[index];
            if (ownsPrecipitation[index]) emitter.transform.rotation = Quaternion.identity; // Camera tilt must not rotate the horizontal spawn volume.
            if ((camera.transform.position - precipitationPositions[index]).sqrMagnitude > 32 * 32) emitter.ClearLocalParticles();
            precipitationPositions[index] = camera.transform.position;
            LightingTierSettings tier = viewLighting != null ? viewLighting.ResolvedTierSettings : LightingTier.Resolve(AdaptiveQuality.QualityTier.Low);
            emitter.SetRenderBudget(tier.precipitationParticleLimit);
            if (emitter.enabled) emitter.ApplyEnvironmentSample(sample);
            SetPrecipitationView(camera); // UC-T08-08: generated particles are visible only to their admitted camera.
        }

        // Every render callback participates, including foreign worlds, so nested views restore incoming visibility.
        private void BeginPrecipitationRendering(Camera camera)
        {
            if (precipitationRenderDepth >= precipitationRenderViews.Length) return;
            int mask = 0;
            for (int i = 0; i < precipitationRenderers.Length; i++)
                if (ownsPrecipitation[i] && precipitationRenderers[i] != null && precipitationRenderers[i].enabled) mask |= 1 << i;
            precipitationRenderViews[precipitationRenderDepth] = camera;
            precipitationRenderMasks[precipitationRenderDepth++] = mask;
            SetPrecipitationView(null);
        }

        private void EndPrecipitationRendering(Camera camera)
        {
            if (precipitationRenderDepth == 0 || precipitationRenderViews[precipitationRenderDepth - 1] != camera) return;
            int mask = precipitationRenderMasks[--precipitationRenderDepth];
            precipitationRenderViews[precipitationRenderDepth] = null;
            for (int i = 0; i < precipitationRenderers.Length; i++)
                if (ownsPrecipitation[i] && precipitationRenderers[i] != null) precipitationRenderers[i].enabled = (mask & (1 << i)) != 0;
        }

        private void SetPrecipitationView(Camera camera)
        {
            for (int i = 0; i < precipitationRenderers.Length; i++)
                if (ownsPrecipitation[i] && precipitationRenderers[i] != null)
                    precipitationRenderers[i].enabled = camera != null && precipitationCameras[i] == camera;
        }

        private void RetireInactivePrecipitation()
        {
            if (precipitationRenderDepth != 0 || environmentViewDepth != 0) return;
            for (int i = 0; i < viewPrecipitation.Length; i++)
                if (viewPrecipitation[i] != null && (precipitationCameras[i] == null || !precipitationCameras[i].isActiveAndEnabled)) RetirePrecipitationSlot(i);
        }

        private void RetirePrecipitationSlot(int index)
        {
            var emitter = viewPrecipitation[index];
            if (emitter != null)
            {
                emitter.BindWeatherSource(null);
                if (ownsPrecipitation[index]) { emitter.gameObject.SetActive(false); ReleasePrecipitationObject(emitter.gameObject); }
            }
            viewPrecipitation[index] = null; precipitationCameras[index] = null; ownsPrecipitation[index] = false;
            precipitationRenderers[index] = null;
        }

        private void ResetCameraPrecipitation()
        {
            for (int i = 0; i < viewPrecipitation.Length; i++) if (viewPrecipitation[i] != null) viewPrecipitation[i].ClearLocalParticles();
        }

        private void ReleaseCameraPrecipitation()
        {
            precipitationRenderDepth = 0;
            System.Array.Clear(precipitationRenderViews, 0, precipitationRenderViews.Length);
            for (int i = 0; i < viewPrecipitation.Length; i++) RetirePrecipitationSlot(i);
            if (precipitationMaterial != null) ReleasePrecipitationObject(precipitationMaterial);
            precipitationMaterial = null;
        }

        private static void ReleasePrecipitationObject(Object value)
        {
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
