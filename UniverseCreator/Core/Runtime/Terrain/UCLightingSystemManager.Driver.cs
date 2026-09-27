// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCLightingSystemManager.Driver.cs  (partial of UCLightingSystemManager)
//  Purpose: The RUNTIME LIGHTING DRIVER. The lighting math (AtmosphereState, SkyModel,
//           ExposureModel, SunMoonRig, AmbientLight, MoonLight) is genuinely physical,
//           but until this file nothing pushed it to the live scene - the sun was never
//           coloured or oriented, the sky LUT was never filled, the air was lossless and
//           ambient fell back to the engine default. This reads the world's clock and
//           weather once per event (edit mode) or per frame (play mode) and drives the
//           scene: sun/moon orientation and physical colour, one shared photographic
//           exposure, sky-derived ambient, the sky LUT, and the shader globals the sky,
//           fog and reflection paths read. (§33)
//
//  IT OWNS NO LIGHT MATH. Every value here is read from AtmosphereState.From; this file
//  only applies it. One exposure multiplier scales the sun, the moon, the ambient probe
//  and the sky table together, so nothing in the frame is lit on a different stop (S-11).
//
//  EDIT MODE IS EVENT-DRIVEN. The heavy work runs from OnEnable and OnValidate (and the
//  Apply Lighting Now menu), never a per-frame ExecuteAlways Update, so the scene view
//  shows the lit world after a change without keeping the editor loop hot. Play mode runs
//  it per frame for the day cycle and eye adaptation.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Weather;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCLightingSystemManager
    {
        // Shader globals this driver publishes (the sky/fog/reflection HLSL reads them).
        private static readonly int s_SkyLutId       = Shader.PropertyToID("_SkyLUT");
        private static readonly int s_SkyExposureId  = Shader.PropertyToID("_SkyExposure");
        private static readonly int s_SunDirId       = Shader.PropertyToID("_UCSunDir");
        private static readonly int s_BeamExtinctionId = Shader.PropertyToID("_UCBeamExtinction");

        private const string MoonChildName = "UC Moon Light";
        private const string SunChildName  = "UC Sun Light";

        // Non-serialized runtime state: the created moon light, the baked sky table, the
        // last-baked sky (so a bake only happens on a meaningful change), and the adapted
        // exposure the eye is currently at.
        [System.NonSerialized] private Light _moon;
        [System.NonSerialized] private Texture2D _skyLut;
        [System.NonSerialized] private bool _hasBaked;
        [System.NonSerialized] private float _bakedElevationDeg;
        [System.NonSerialized] private float _bakedTurbidity;
        [System.NonSerialized] private float _bakedCloud;
        [System.NonSerialized] private float _adaptedEv;
        [System.NonSerialized] private bool _hasAdapted;

        // A neutral mid-grey ground reflectance for the ambient bounce until per-point
        // terrain albedo is fed in (a documented Step 33 follow-up, honest scope). Earthy,
        // so the sky ambient is physically grounded without a snowfield over-brightening it.
        private static readonly Color GroundAlbedoDefault = new Color(0.20f, 0.20f, 0.18f, 1f);

        /// <summary>
        /// Read the world's clock and weather once and push a physically-based sky to the
        /// live scene. Owns no light math - every value is read from AtmosphereState.From.
        /// <paramref name="allowCreate"/> is false from OnValidate (Unity forbids creating
        /// GameObjects there); <paramref name="animate"/> is true only in play mode, where
        /// the eye adaptation advances by Time.deltaTime.
        /// </summary>
        private void ApplyLighting(bool allowCreate, bool animate)
        {
            if (!lightingEnabled) return;

            // Same clock and weather owner the rest of the world reads (never re-derived here).
            ClockState clock = Clock;
            WeatherState weather = UCWeatherSystemManager.ResolveWeatherFor(transform);

            // settings.turbidity is an absolute value; AtmosphereState wants a bias off the
            // 2.2 default, so the authored default resolves to a zero offset.
            float turbidityBias = settings.turbidity - LightingConstants.TurbidityDefault;
            AtmosphereState atmo = AtmosphereState.From(weather, clock, GroundAlbedoDefault, turbidityBias);

            // Without a directional sun there is nothing to drive; bail rather than invent one.
            Light sunLight = ResolveSunLight(allowCreate);
            if (sunLight == null) return;
            Light moonLight = ResolveMoonLight(allowCreate);

            // Orientation: the rig points the transforms; it owns no colour or intensity.
            new SunMoonRig(sunLight, moonLight).Orient(clock);

            // One exposure for the whole frame (S-11). Adaptive only advances when animating.
            float ev = atmo.exposureEv;
            if (settings.exposureMode == ExposureMode.Adaptive && animate)
            {
                if (!_hasAdapted) { _adaptedEv = ev; _hasAdapted = true; }
                else _adaptedEv = ExposureModel.AdaptEv100(_adaptedEv, ev, Time.deltaTime);
                ev = _adaptedEv;
            }
            float exposureMul = ExposureModel.ExposureMultiplier(ev);

            // Sun: physical colour, illuminance scaled to a viewable stop, faded through twilight.
            float sunWeight = SunMoonRig.SunTwilightWeight01(clock.elevationDeg);
            sunLight.color = atmo.sunColor;
            sunLight.intensity = atmo.sunIlluminanceLux * exposureMul * sunWeight;

            // Moon: physical colour and intensity, on the same stop, only where the sun has left.
            if (moonLight != null)
            {
                moonLight.color = atmo.moonColor;
                float moonInt = MoonLight.Intensity(clock.lunarElevationDeg, clock.lunarIlluminatedFraction, settings.moonIntensity);
                moonLight.intensity = moonInt * exposureMul * (1f - sunWeight);
                moonLight.enabled = moonEnabled;
            }

            // Ambient: sky-derived SH, scaled onto the same stop, assigned as the custom probe.
            SphericalHarmonicsL2 probe = AmbientLight.BuildAmbientProbe(atmo.sunDir, atmo.turbidity, atmo.cloudCoverCombined01, GroundAlbedoDefault);
            probe *= exposureMul;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = probe;

            // Shader globals the sky/fog/reflection HLSL reads.
            Shader.SetGlobalVector(s_SunDirId, new Vector4(atmo.sunDir.x, atmo.sunDir.y, atmo.sunDir.z, 0f));
            Shader.SetGlobalFloat(s_SkyExposureId, exposureMul);
            Shader.SetGlobalVector(s_BeamExtinctionId, LightingConstants.BeamExtinction);

            // Sky LUT: rebuilt only on a meaningful change, then bound for the sky sampler.
            EnsureSkyLutBaked(atmo);
            if (_skyLut != null) Shader.SetGlobalTexture(s_SkyLutId, _skyLut);
        }

        /// <summary>
        /// The directional light this driver colours and orients: the authored <c>sun</c>
        /// field, else the scene's RenderSettings sun, else a child named "UC Sun Light"
        /// (found, or created when <paramref name="allowCreate"/> allows it). Returns null
        /// when none exists and creation is not permitted, so the caller drives nothing.
        /// </summary>
        private Light ResolveSunLight(bool allowCreate)
        {
            if (sun != null) return sun;
            if (RenderSettings.sun != null) { sun = RenderSettings.sun; return sun; }

            Transform existing = transform.Find(SunChildName);
            if (existing != null)
            {
                sun = existing.GetComponent<Light>();
                if (sun != null) { RenderSettings.sun = sun; return sun; }
            }
            if (!allowCreate) return null;

            GameObject go = new GameObject(SunChildName);
            go.transform.SetParent(transform, false);
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            RenderSettings.sun = sun;
            return sun;
        }

        /// <summary>
        /// The moon's directional light: a cached reference, else a child named "UC Moon
        /// Light" (found, or created when <paramref name="allowCreate"/> allows it). May
        /// return null - the moon is optional and the sun path never depends on it.
        /// </summary>
        private Light ResolveMoonLight(bool allowCreate)
        {
            if (_moon != null) return _moon;

            Transform existing = transform.Find(MoonChildName);
            if (existing != null) _moon = existing.GetComponent<Light>();
            if (_moon != null || !allowCreate) return _moon;

            GameObject go = new GameObject(MoonChildName);
            go.transform.SetParent(transform, false);
            _moon = go.AddComponent<Light>();
            _moon.type = LightType.Directional;
            return _moon;
        }

        /// <summary>Editor entry point: light the scene view now, from the current world state.</summary>
        [ContextMenu("Apply Lighting Now")]
        private void ApplyLightingNow() => ApplyLighting(allowCreate: true, animate: false);

        /// <summary>
        /// Rebuild the sky table only when the sky has meaningfully changed (sun elevation,
        /// turbidity or cloud, per SkyLut.ShouldRebuild), and create the RGBAHalf texture
        /// lazily. Keeps the day cycle cheap - a static noon does not re-bake every frame.
        /// </summary>
        private void EnsureSkyLutBaked(in AtmosphereState atmo)
        {
            float elevDeg = Mathf.Asin(Mathf.Clamp(atmo.sunDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            if (_skyLut == null)
            {
                _skyLut = new Texture2D(SkyLut.Width, SkyLut.Height, TextureFormat.RGBAHalf, false, true)
                {
                    name = "_UCSkyLUT",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                _hasBaked = false;
            }

            bool need = !_hasBaked || SkyLut.ShouldRebuild(
                _bakedElevationDeg, elevDeg,
                _bakedTurbidity, atmo.turbidity,
                _bakedCloud, atmo.cloudCoverCombined01);
            if (!need) return;

            BakeSkyLut(atmo.sunDir, atmo.turbidity);
            _hasBaked = true;
            _bakedElevationDeg = elevDeg;
            _bakedTurbidity = atmo.turbidity;
            _bakedCloud = atmo.cloudCoverCombined01;
        }

        /// <summary>
        /// Fill the sky table by inverting UC_Sky.hlsl's UCSkyUV: texel (u,v) maps to an
        /// azimuth-from-sun in [0,PI] and an elevation in [0,PI/2], the view direction that
        /// samples it. Radiance is stored raw (cd/m^2); the shader applies _SkyExposure, so
        /// the whole sky rides the same photographic stop as the sun and ambient.
        /// </summary>
        private void BakeSkyLut(Vector3 sunDir, float turbidity)
        {
            Vector3 sunHoriz = new Vector3(sunDir.x, 0f, sunDir.z);
            sunHoriz = sunHoriz.sqrMagnitude < 1e-6f ? Vector3.forward : sunHoriz.normalized;

            for (int y = 0; y < SkyLut.Height; y++)
            {
                float v01 = (y + 0.5f) / SkyLut.Height;
                float el = v01 * (0.5f * Mathf.PI);
                float cosEl = Mathf.Cos(el);
                float sinEl = Mathf.Sin(el);

                for (int x = 0; x < SkyLut.Width; x++)
                {
                    float u01 = (x + 0.5f) / SkyLut.Width;
                    float azFromSun = u01 * Mathf.PI;
                    Vector3 viewHoriz = Quaternion.AngleAxis(azFromSun * Mathf.Rad2Deg, Vector3.up) * sunHoriz;
                    Vector3 viewDir = (viewHoriz * cosEl + Vector3.up * sinEl).normalized;

                    Color rad = SkyModel.SkyRadiance(viewDir, sunDir, turbidity);
                    _skyLut.SetPixel(x, y, rad);
                }
            }
            _skyLut.Apply(false, false);
        }
    }
}
