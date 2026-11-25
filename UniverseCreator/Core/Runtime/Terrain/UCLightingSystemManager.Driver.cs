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
        private static readonly int s_PreviousSkyLutId = Shader.PropertyToID("_UCPreviousSkyLUT");
        private static readonly int s_SkyBlendId = Shader.PropertyToID("_UCSkyBlend");
        private static readonly int s_SkyExposureId  = Shader.PropertyToID("_SkyExposure");
        private static readonly int s_SunDirId       = Shader.PropertyToID("_UCSunDir");
        private static readonly int s_BeamExtinctionId = Shader.PropertyToID("_UCBeamExtinction");
        private static readonly int s_ShaftIntensityId = Shader.PropertyToID("_UCShaftIntensity");
        private static readonly int s_ShaftColorId     = Shader.PropertyToID("_UCShaftColor");
        private static readonly int s_MoonDirId = Shader.PropertyToID("_UCMoonDir");
        private static readonly int s_SunColorId = Shader.PropertyToID("_UCSunColor");
        private static readonly int s_MoonColorId = Shader.PropertyToID("_UCMoonColor");
        private static readonly int s_DiscParamsId = Shader.PropertyToID("_UCSkyDiscParams");
        private static readonly int s_StarsId = Shader.PropertyToID("_UCStars");
        private static readonly int s_StarRotationId = Shader.PropertyToID("_UCWorldToEquatorial");
        private static readonly int s_StarParamsId = Shader.PropertyToID("_UCStarParams");
        private StarSky starSky;
        private bool starCatalogAttempted;

        private const string MoonChildName = "UC Moon Light";
        private const string SunChildName  = "UC Sun Light";

        // Non-serialized runtime state: the created moon light, the baked sky table, the
        // last-baked sky (so a bake only happens on a meaningful change), and the adapted
        // exposure the eye is currently at.
        [System.NonSerialized] private Light _moon;
        private Light createdSun, createdMoon; // UC-T08-07: retire only generated lights; authored light objects are never destroyed.
        private readonly LightingSampleCache observerLighting = new LightingSampleCache(); // UC-T08-07: observer and camera resources have separate bounded owners.
        private UCWorldGraphBindings graphBindings; // UC-T05-04: cached world binding; accepted fields are sampled without per-frame discovery or allocations.
        private UCWeatherSystemManager environmentWeather; // UC-T08-07: the existing sampled clock/weather owner, not a second authority.

        internal void BindEnvironmentGraph(UCWorldGraphBindings source)
        {
            if (source != null && !ReferenceEquals(source.Owner, GetComponentInParent<UniverseCreatorObject>(true)))
                throw new System.ArgumentException("Environment graph belongs to another UC world.", nameof(source));
            graphBindings = source;
            environmentWeather = UCWeatherSystemManager.FindForWorld(transform);
        }

        internal void RefreshAcceptedGraphEnvironment()
        {
            ResetEnvironmentLighting();
            ApplyLighting(allowCreate: false, animate: false);
        }

        // A neutral mid-grey ground reflectance for the ambient bounce until per-point
        // terrain albedo is fed in (a documented Step 33 follow-up, honest scope). Earthy,
        // so the sky ambient is physically grounded without a snowfield over-brightening it.
        private static readonly Color GroundAlbedoDefault = new Color(0.20f, 0.20f, 0.18f, 1f);

        /// <summary>Derives atmosphere and target EV from one captured local environment using this owner's existing lighting settings.</summary>
        /// <param name="sample">Immutable weather/clock/bias value from this world at a declared position, instant and accepted revision.</param>
        /// <param name="targetEv">Target photographic EV100 including the captured -16..16 stop bias, before eye adaptation.</param>
        /// <returns>Physical atmosphere using captured weather and astronomy; this call neither advances time nor publishes render state.</returns>
        internal AtmosphereState AtmosphereForSample(in EnvironmentSampleProduct sample, out float targetEv)
        {
            float turbidityBias = settings.turbidity - LightingConstants.TurbidityDefault + sample.TurbidityBias;
            AtmosphereState atmosphere = AtmosphereState.From(sample.Weather, sample.Clock, GroundAlbedoDefault, turbidityBias, settings.nightFloorLux);
            targetEv = (settings.exposureMode == ExposureMode.Manual ? settings.manualExposureEv : atmosphere.exposureEv) +
                Mathf.Clamp(sample.ExposureBiasEv, -16, 16);
            return atmosphere;
        }

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
            CaptureInitialLighting(); // UC-T08-07: explicit acceptance may publish before an inactive fixture's OnEnable.

            // UC-T08-07: lighting consumes the same immutable clock/weather/accepted-bias sample as observer effects.
            if (environmentWeather == null) environmentWeather = UCWeatherSystemManager.FindForWorld(transform);
            EnvironmentSampleProduct sample;
            if (environmentWeather != null)
            {
                Vector3 position = graphBindings != null && graphBindings.EnvironmentSampleTarget != null ?
                    graphBindings.EnvironmentSampleTarget.position : environmentWeather.transform.position;
                sample = environmentWeather.SampleEnvironmentAt(position);
            }
            else
            {
                EnvironmentGraphBias bias = graphBindings != null ? graphBindings.SampleEnvironmentBias() : default;
                sample = new EnvironmentSampleProduct(System.Guid.Empty, transform.position, 0, 0, 0,
                    UCWeatherSystemManager.ResolveWeatherFor(transform), Clock, bias.Turbidity, bias.ExposureEv, bias.SkyTint, bias.AmbientScale);
            }
            ApplySampleLighting(sample, observerLighting, allowCreate, animate, refreshIrradiance: true);
        }

        // UC-T08-07: one apply path serves the observer and camera adapters; their view ownership/restoration stays separate from the maths.
        private float ApplySampleLighting(in EnvironmentSampleProduct sample, LightingSampleCache cache, bool allowCreate, bool animate, bool refreshIrradiance)
        {
            ClockState clock = sample.Clock;

            // settings.turbidity is an absolute value; AtmosphereState wants a bias off the
            // 2.2 default, so the authored default resolves to a zero offset. settings.nightFloorLux
            // is the authored playability floor (0 = physical starlight night, the default - a
            // positive value keeps a moonless night navigable instead of runaway-dark).
            AtmosphereState atmo = AtmosphereForSample(sample, out float targetEv);

            Light sunLight = ResolveSunLight(allowCreate);
            Light moonLight = ResolveMoonLight(allowCreate);

            // Orientation: the rig points the transforms; it owns no colour or intensity.
            new SunMoonRig(sunLight, moonLight).Orient(clock);

            // One exposure for the whole frame (S-11). Adaptive only advances when animating.
            float ev = targetEv;
            if (settings.exposureMode == ExposureMode.Adaptive && animate)
            {
                if (!cache.HasAdapted) { cache.AdaptedEv = ev; cache.HasAdapted = true; }
                else if (cache.AdaptedFrame != Time.frameCount)
                    cache.AdaptedEv = ExposureModel.AdaptEv100(cache.AdaptedEv, ev, Time.deltaTime);
                cache.AdaptedFrame = Time.frameCount;
                ev = cache.AdaptedEv;
            }
            float exposureMul = ExposureModel.ExposureMultiplier(ev);

            // Sun: physical colour, illuminance scaled to a viewable stop, faded through twilight.
            float sunWeight = SunMoonRig.SunTwilightWeight01(clock.elevationDeg);
            Color tint = sample.SkyTint >= 0 ? new Color(1, 0.85f, 0.65f) : new Color(0.65f, 0.85f, 1);
            // UC midday repair: metered lux owns brightness; spectrum owns hue. URP supplies the surface cosine once.
            Color sunSpectrum = ExposureModel.UnitLuminanceColor(Color.Lerp(atmo.sunColor, tint, Mathf.Clamp01(Mathf.Abs(sample.SkyTint))));
            Color moonSpectrum = ExposureModel.UnitLuminanceColor(atmo.moonColor);
            float sunNormalLux = SkyModel.SunDirectNormalIlluminanceLux(clock.airMass, atmo.turbidity, atmo.cloudCoverCombined01);
            float fullMoonNormalLux = MoonLight.Intensity(90, 1, settings.moonIntensity) * atmo.moonBeamTransmission01;
            float cloudTransmission = SkyModel.CloudTransmission01(atmo.cloudCoverCombined01);
            if (sunLight != null)
            {
                if (sunLight == createdSun) sunLight.enabled = true;
                RenderSettings.sun = sunLight;
                sunLight.color = ExposureModel.UnityLightColor(sunSpectrum);
                sunLight.intensity = ExposureModel.UrpLightIntensity(SkyLightSplit.DirectionalLux(sunNormalLux, atmo.cloudCoverCombined01), exposureMul) * sunWeight;
            }

            // Opt-in (S-cloud): let cloud soften the key shadows. SkyLightSplit moves light
            // out of the shadow-casting beam into the diffuse remainder as cover thickens, so
            // the shadow strength falls from hard (clear) to shadowless (overcast) off the
            // combined cover alone. OFF by default - the sun's shadowStrength is left untouched,
            // so an existing world is bit-identical. One owner: the driver reads the term, it is
            // not a second lighting solution.
            if (settings.cloudSoftensShadows && sunLight != null)
                sunLight.shadowStrength = SkyLightSplit.ShadowStrength01(atmo.cloudCoverCombined01);

            // Moon: physical colour and intensity, on the same stop, with a continuous rise/set of the visible disc area.
            if (moonLight != null)
            {
                moonLight.color = ExposureModel.UnityLightColor(moonSpectrum);
                float moonInt = SkyLightSplit.DirectionalLux(fullMoonNormalLux * clock.lunarIlluminatedFraction * cloudTransmission *
                    MoonLight.HorizonVisibility01(clock.lunarElevationDeg, settings.moonDiscSizeDeg), atmo.cloudCoverCombined01);
                moonLight.intensity = ExposureModel.UrpLightIntensity(moonInt, exposureMul) * (1f - sunWeight);
                moonLight.enabled = moonEnabled;
            }

            // Ambient: sky-derived SH, scaled onto the same stop, assigned as the custom probe.
            EnsureSkyLutBaked(atmo, sample, cache, Time.realtimeSinceStartupAsDouble);
            SphericalHarmonicsL2 probe = SampleSkyAmbient(cache);
            probe *= exposureMul;
            probe *= Mathf.Max(0, 1 + sample.AmbientScaleBias); // UC-T05-04: additive scale bias on the same physical ambient probe.
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = probe;

            // Irradiance GI grid go-live (opt-in): fill a budgeted slice of the probe grid and
            // bind it so UCSampleIrradiance reads real stored bounce instead of engine ambient.
            // Off binds nothing, so the ambient probe above remains the whole story (zero
            // regression). One owner: this is the only place the grid is driven. (LIGHT-WIRE3)
            if (refreshIrradiance) RefreshIrradianceGrid(in atmo, allowCreate, sample.PositionMetres, animate ? Time.unscaledDeltaTime : 0);
            else BindViewIrradiance(); // UC-T08-07: views bind the already accepted grid; nested cameras never refill/upload it.
            PublishEnvReflectionStrength();

            // Shader globals the sky/fog/reflection HLSL reads.
            Shader.SetGlobalVector(s_SunDirId, new Vector4(atmo.sunDir.x, atmo.sunDir.y, atmo.sunDir.z, 0f));
            Shader.SetGlobalVector(s_MoonDirId, new Vector4(atmo.moonDir.x, atmo.moonDir.y, atmo.moonDir.z, 0f));
            // UC Scene/Play: a visible disc needs radiance per solid angle, rather than ground illuminance in lux.
            Color sunDisc = sunSpectrum *
                (SunMoonRig.DiscRadiance(sunNormalLux * sunWeight, settings.sunDiscSizeDeg) * exposureMul);
            // The shader's spherical phase mask supplies the lit fraction once. Daylight may outshine the moon, but does not switch it off.
            Color moonDisc = moonSpectrum * (moonEnabled ?
                SunMoonRig.DiscRadiance(fullMoonNormalLux * cloudTransmission, settings.moonDiscSizeDeg) * exposureMul : 0);
            // Realistic disc: hue-preserving display cap (see SunMoonRig.SunDiscDisplayPeak).
            sunDisc = SunMoonRig.LimitPeakPreservingHue(sunDisc, SunMoonRig.SunDiscDisplayPeak(sunNormalLux * sunWeight));
            moonDisc = SunMoonRig.LimitPeakPreservingHue(moonDisc, 3f);
            Shader.SetGlobalVector(s_SunColorId, new Vector4(sunDisc.r, sunDisc.g, sunDisc.b, 1));
            // UC celestial repair: alpha carries the authoritative lit fraction; orbital latitude must not create a visible new moon.
            Shader.SetGlobalVector(s_MoonColorId, new Vector4(moonDisc.r, moonDisc.g, moonDisc.b, clock.lunarIlluminatedFraction));
            float sunRadius = settings.sunDiscSizeDeg * 0.5f * Mathf.Deg2Rad, moonRadius = settings.moonDiscSizeDeg * 0.5f * Mathf.Deg2Rad;
            Shader.SetGlobalVector(s_DiscParamsId, new Vector4(sunRadius, sunRadius * 0.1f, moonRadius, moonRadius * 0.1f));
            Shader.SetGlobalFloat(s_SkyExposureId, exposureMul);
            Shader.SetGlobalVector(s_BeamExtinctionId, LightingConstants.BeamExtinction);

            // Sun shafts (LIGHT-WIRE5, producer half): publish the crepuscular-ray master intensity
            // and tint UC_SunShafts.shader reads. SunShafts.Intensity01 self-gates to zero at a clear
            // sky, a solid overcast and a high sun, so the global is non-zero only for a low sun
            // through broken cloud. This makes SunShafts a live producer; the full-screen pass that
            // consumes it is registered the same way the other screen-space features are (setup path).
            float shaftIntensity = SunShafts.Intensity01(atmo.cloudCoverCombined01, clock.elevationDeg);
            Shader.SetGlobalFloat(s_ShaftIntensityId, shaftIntensity);
            Shader.SetGlobalVector(s_ShaftColorId, new Vector4(atmo.sunColor.r, atmo.sunColor.g, atmo.sunColor.b, 1f));

            // Sky LUT: rebuilt only on a meaningful change, then bound for the sky sampler.
            if (cache.SkyLut != null) Shader.SetGlobalTexture(s_SkyLutId, cache.SkyLut);
            Shader.SetGlobalTexture(s_PreviousSkyLutId, cache.PreviousSkyLut != null ? cache.PreviousSkyLut : cache.SkyLut);
            Shader.SetGlobalFloat(s_SkyBlendId, cache.SkyBlend);
            // UC celestial repair: catalog parsing/upload occurs once, outside per-frame/view sampling.
            if (settings.enableStars && allowCreate && !starCatalogAttempted)
            {
                starCatalogAttempted = true;
                var catalog = Resources.Load<TextAsset>(StarSky.ResourcePath);
                try { starSky = new StarSky(catalog); }
                catch (System.ArgumentException error) { Debug.LogError(error.Message, this); }
                finally { if (catalog != null) Resources.UnloadAsset(catalog); }
            }
            Shader.SetGlobalTexture(s_StarsId, starSky != null ? starSky.Texture : null);
            Shader.SetGlobalMatrix(s_StarRotationId, StarSky.WorldToEquatorial(clock));
            float stars = settings.enableStars && starSky != null ?
                StarSky.Visibility(clock.elevationDeg) * Mathf.Clamp(settings.starBrightness, 0, 4) *
                    starSky.RadianceScale * exposureMul : 0;
            // UC celestial follow-up: share captured haze and the existing bounded air-path floor with the star pass.
            Shader.SetGlobalVector(s_StarParamsId, new Vector4(stars, atmo.turbidity, AtmoConstants.AirMassDenominatorFloor, 0));
            BindSampleSkybox(cache, exposureMul, allowCreate);
            PublishSampleClouds(sample, atmo, cache, exposureMul, allowCreate); // UC-T08-08: the shared sky pass receives this world's captured decks and clock-derived drift.

            // Tonemapping go-live (opt-in): ensure the module's single low-priority Volume so a
            // physically-exposed HDR scene maps to display. Off (default) adds nothing.
            if (settings.enableModuleTonemapping) EnsureTonemappingVolume(allowCreate);
            else ReleaseOwnedTonemapping(); // UC midday repair: switching the saved opt-in off must retire its native Volume.
            return exposureMul;
        }

        /// <summary>
        /// The directional light this driver colours and orients: the authored <c>sun</c>
        /// field (unless it belongs to a different world), else this world's RenderSettings sun, else a child named "UC Sun Light"
        /// (found, or created when <paramref name="allowCreate"/> allows it). Returns null
        /// when none exists and creation is not permitted, so the caller drives nothing.
        /// </summary>
        private Light ResolveSunLight(bool allowCreate)
        {
            if (sun != null && IsLocalSun(sun, explicitlyAssigned: true)) return sun;
            if (RenderSettings.sun != null && IsLocalSun(RenderSettings.sun, explicitlyAssigned: false)) { sun = RenderSettings.sun; return sun; }

            Transform existing = transform.Find(SunChildName);
            if (existing != null)
            {
                sun = existing.GetComponent<Light>();
                if (sun != null) return sun;
            }
            if (!allowCreate) return null;

            GameObject go = new GameObject(SunChildName);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.enabled = false; createdSun = sun;
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
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            _moon = go.AddComponent<Light>();
            _moon.type = LightType.Directional;
            _moon.enabled = false; createdMoon = _moon;
            return _moon;
        }

        /// <summary>Editor entry point: light the scene view now, from the current world state.</summary>
        [ContextMenu("Apply Lighting Now")]
        private void ApplyLightingNow() => ApplyLighting(allowCreate: true, animate: false);

#if UC_URP
        [System.NonSerialized] private UnityEngine.Rendering.Volume _tonemapVolume;
        private UnityEngine.Rendering.VolumeProfile ownedTonemapProfile;

        /// <summary>
        /// Ensure the module's single low-priority global tonemapping Volume (opt-in). Created once
        /// as a DontSave child and reused; its priority sits far below an author's own Volume so
        /// theirs always wins. Owns at most one Volume (UCTonemapping.OwnedVolumeCount). When
        /// creation is not permitted (OnValidate) it only re-applies an existing one.
        /// </summary>
        private void EnsureTonemappingVolume(bool allowCreate)
        {
            const string volName = "UC Tonemapping Volume";
            // UC midday repair: a similarly named authored Volume is not ours to adopt or overwrite.
            var mode = UCTonemapping.ToUrpMode(UCTonemapping.DefaultPolicy);

            if (_tonemapVolume == null)
            {
                if (!allowCreate) return;
                GameObject go = new GameObject(volName);
                go.transform.SetParent(transform, false);
                go.hideFlags = HideFlags.DontSave;
                _tonemapVolume = go.AddComponent<UnityEngine.Rendering.Volume>();
                _tonemapVolume.isGlobal = true;
                _tonemapVolume.priority = UCTonemapping.ModuleVolumePriority;

                var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                profile.hideFlags = HideFlags.DontSave;
                var tm = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>(true);
                tm.mode.overrideState = true;
                tm.mode.value = mode;
                _tonemapVolume.sharedProfile = profile;
                ownedTonemapProfile = profile;
            }
            else if (_tonemapVolume.sharedProfile != null
                     && _tonemapVolume.sharedProfile.TryGet(out UnityEngine.Rendering.Universal.Tonemapping existingTm))
            {
                existingTm.mode.overrideState = true;
                existingTm.mode.value = mode;
            }
        }

        private void ReleaseOwnedTonemapping()
        {
            if (ownedTonemapProfile == null) return;
            if (_tonemapVolume != null) ReleaseLightingObject(_tonemapVolume.gameObject);
            foreach (var component in ownedTonemapProfile.components)
                if (component != null) ReleaseLightingObject(component);
            ReleaseLightingObject(ownedTonemapProfile); ownedTonemapProfile = null; _tonemapVolume = null;
        }
#else
        private void EnsureTonemappingVolume(bool allowCreate) { }
        private void ReleaseOwnedTonemapping() { }
#endif

        /// <summary>
        /// Rebuild the sky table only when the sky has meaningfully changed (sun elevation,
        /// turbidity or cloud, per SkyLut.ShouldRebuild), and create the RGBAHalf texture
        /// lazily. RGB is scaled radiance; alpha holds a shared base-two storage exponent.
        /// Keeps the day cycle cheap; two reusable endpoints crossfade in 0.25 real seconds.
        /// Explicit seeks/settings invalidation apply immediately. A static noon does not re-bake.
        /// </summary>
        private void EnsureSkyLutBaked(in AtmosphereState atmo, in EnvironmentSampleProduct sample, LightingSampleCache cache, double realtimeSeconds)
        {
            float elevDeg = Mathf.Asin(Mathf.Clamp(atmo.sunDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            if (cache.SkyLut == null)
            {
                cache.SkyLut = CreateSkyLut();
                cache.HasBaked = false;
            }

            // A completed/initial/seek bake stays complete; only an active crossfade advances its timer.
            if (cache.HasBaked && cache.SkyBlend < 1)
                cache.SkyBlend = Mathf.SmoothStep(0, 1, Mathf.Clamp01((float)((realtimeSeconds - cache.SkyTransitionStarted) / 0.25)));
            bool need = !cache.HasBaked || cache.SkyTint != sample.SkyTint || SkyLut.ShouldRebuild(
                cache.ElevationDeg, elevDeg,
                cache.Turbidity, atmo.turbidity,
                cache.Cloud, atmo.cloudCoverCombined01);
            if (!need) return;
            // Finish the current bounded transition before reusing its previous endpoint; never replace a partly displayed texture.
            if (cache.HasBaked && cache.SkyBlend < 1) return;

            bool transition = cache.HasBaked;
            if (transition && cache.PreviousSkyLut == null) cache.PreviousSkyLut = CreateSkyLut();
            Texture2D target = transition ? cache.PreviousSkyLut : cache.SkyLut;
            BakeSkyLut(atmo, sample, cache, target);
            // UC-T08-07: the retained daylight model must fade below the horizon; twilight and the explicit night floor also reach ambient.
            SphericalHarmonicsL2 ambient = AmbientLight.BuildAmbientProbe(atmo.sunDir, atmo.turbidity, atmo.cloudCoverCombined01, GroundAlbedoDefault);
            ambient *= DaySkyWeight(sample.Clock.elevationDeg);
            Color ambientTint = Color.Lerp(Color.white, sample.SkyTint >= 0 ? new Color(1, 0.85f, 0.65f) : new Color(0.65f, 0.85f, 1),
                Mathf.Clamp01(Mathf.Abs(sample.SkyTint)));
            for (int coefficient = 0; coefficient < 9; coefficient++)
            { ambient[0, coefficient] *= ambientTint.r; ambient[1, coefficient] *= ambientTint.g; ambient[2, coefficient] *= ambientTint.b; }
            Color horizon = SkyRadianceForSample(Vector3.forward, atmo, sample) + SkyRadianceForSample(Vector3.back, atmo, sample);
            float nightWeight = 1 - DaySkyWeight(sample.Clock.elevationDeg);
            ambient.AddAmbientLight(horizon * (0.5f * nightWeight));
            if (transition) { cache.PreviousSkyLut = cache.SkyLut; cache.SkyLut = target; }
            cache.PreviousAmbient = transition ? cache.Ambient : ambient;
            cache.Ambient = ambient;
            cache.SkyBlend = transition ? 0 : 1;
            cache.SkyTransitionStarted = realtimeSeconds;
            cache.HasBaked = true;
            cache.ElevationDeg = elevDeg;
            cache.Turbidity = atmo.turbidity;
            cache.Cloud = atmo.cloudCoverCombined01;
            cache.SkyTint = sample.SkyTint;
        }

        private static Texture2D CreateSkyLut() => new Texture2D(SkyLut.Width, SkyLut.Height, TextureFormat.RGBAHalf, false, true) {
            name = "_UCSkyLUT", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
        };

        /// <summary>Interpolates all 27 linear SH coefficients using the same endpoint weight as the rendered sky.</summary>
        /// <param name="cache">Main-thread sky cache; SkyBlend is clamped to 0..1 by its monotonic real-time update.</param>
        /// <returns>Physical ambient before exposure; no allocation or mutation of either retained endpoint.</returns>
        internal static SphericalHarmonicsL2 SampleSkyAmbient(LightingSampleCache cache)
        {
            SphericalHarmonicsL2 result = cache.Ambient;
            if (cache.SkyBlend >= 1) return result;
            for (int channel = 0; channel < 3; channel++)
                for (int coefficient = 0; coefficient < 9; coefficient++)
                    result[channel, coefficient] = Mathf.Lerp(cache.PreviousAmbient[channel, coefficient], result[channel, coefficient], cache.SkyBlend);
            return result;
        }

        /// <summary>
        /// Fill the sky table by inverting UC_Sky.hlsl's UCSkyUV: texel (u,v) maps to an
        /// azimuth-from-sun in [0,PI] and an elevation in [0,PI/2], the view direction that
        /// samples it. Radiance in cd/m^2 is scaled by a texture-wide power of two before
        /// half-float storage; alpha records the exponent. The shared sky/reflection reader
        /// decodes it before _SkyExposure, preserving brightness and linear interpolation.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The sky model returned nonfinite radiance; the invalid bake is not published.</exception>
        private void BakeSkyLut(in AtmosphereState atmo, in EnvironmentSampleProduct sample, LightingSampleCache cache, Texture2D target)
        {
            Vector3 sunDir = atmo.sunDir;
            Vector3 sunHoriz = new Vector3(sunDir.x, 0f, sunDir.z);
            sunHoriz = sunHoriz.sqrMagnitude < 1e-6f ? Vector3.forward : sunHoriz.normalized;
            if (cache.SkyPixels == null) cache.SkyPixels = new Color[SkyLut.Width * SkyLut.Height];
            float maximumRadiance = 0;

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

                    Color rad = SkyRadianceForSample(viewDir, atmo, sample);
                    if (!UniverseCreator.Weather.ClimateBaselineProduct.Finite(rad.r) || !UniverseCreator.Weather.ClimateBaselineProduct.Finite(rad.g) ||
                        !UniverseCreator.Weather.ClimateBaselineProduct.Finite(rad.b))
                        throw new System.InvalidOperationException("The sampled sky model produced nonfinite radiance before texture storage.");
                    cache.SkyPixels[y * SkyLut.Width + x] = rad;
                    maximumRadiance = Mathf.Max(maximumRadiance, rad.maxColorComponent);
                }
            }
            // UC-T08-14: raw daylight can exceed half-float range. One exact exponent retains 4 KB GPU storage without clipping physical light.
            const float storageHeadroom = 65504f * 0.5f; // Half-float maximum with one bit of headroom for filtering.
            float exponent = maximumRadiance > storageHeadroom ? Mathf.Ceil(Mathf.Log(maximumRadiance / storageHeadroom, 2)) : 0;
            float scale = Mathf.Pow(2, -exponent);
            for (int i = 0; i < cache.SkyPixels.Length; i++)
            {
                Color pixel = cache.SkyPixels[i] * scale;
                pixel.a = exponent;
                cache.SkyPixels[i] = pixel;
            }
            target.SetPixels(cache.SkyPixels);
            target.Apply(false, false);
        }
    }
}
