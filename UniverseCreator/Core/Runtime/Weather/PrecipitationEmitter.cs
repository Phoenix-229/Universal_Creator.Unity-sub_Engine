// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    PrecipitationEmitter.cs
//  Purpose: The drops and flakes you actually see, parented to the camera so the
//           volume of falling precipitation travels with the eye and never has to
//           be simulated for the whole world. The physics that matters is the
//           physics that reads wrong when it is faked: rain falls at metres per
//           second and snow at a tenth of that, heavy rain is bigger drops rather
//           than more of them, a streak is exactly as long as the drop travels in
//           one shutter, and snow blows sideways in a wind that leaves rain almost
//           vertical.
//
//  WHY THE MATH IS STATIC AND THE EMITTER IS THIN
//  ----------------------------------------------
//  Fall speed, drop-size mix and streak length are pure functions of intensity and
//  drop diameter, so they live as static methods that can be checked without a
//  scene. The MonoBehaviour only pushes those numbers into a pre-built particle
//  system every frame - it allocates nothing once running, which is the whole
//  point of a camera-parented emitter.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Reads its physics from AtmoConstants and is handed the wind
//  vector and the rain rate; it never reaches into ecology or terrain. Emitter
//  budget scales by the quality tier, supplied from outside.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Camera-parented rain and snow. The static methods are the pure precipitation
    /// physics; the component feeds them into a pre-built particle system per frame.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class PrecipitationEmitter : MonoBehaviour
    {
        // Integer hash constants for the per-flake speed spread, kept local like the
        // storm stream's - not physics, so not in AtmoConstants.
        private const uint SnowHashMix = 2654435761u;
        private const int SnowHashShift = 15;

        /// <summary>
        /// m/s. Fall speed of a raindrop from its diameter (Atlas-Ulbrich): a drop of
        /// a couple of millimetres falls at about 6.5 m/s, a large one approaches 9.
        /// Clamped at zero so a vanishingly small drop does not fall upward.
        /// </summary>
        public static float RainFallSpeedMPerS(float dropDiameterMm)
        {
            float v = AtmoConstants.RainFallSpeedAsymptoteMPerS
                      - AtmoConstants.RainFallSpeedRangeMPerS
                        * Mathf.Exp(-AtmoConstants.RainFallSpeedDecayPerMm * Mathf.Max(0f, dropDiameterMm));
            return Mathf.Max(0f, v);
        }

        /// <summary>
        /// 1/mm. The Marshall-Palmer slope Lambda for a rain rate: heavier rain gives a
        /// gentler slope, which is the same as saying its drops are bigger.
        /// </summary>
        public static float MarshallPalmerSlopePerMm(float rainfallMmPerH)
        {
            float rate = Mathf.Max(AtmoConstants.PrecipDetectableMmPerH, rainfallMmPerH);
            return AtmoConstants.MarshallPalmerSlopeCoeff
                   * Mathf.Pow(rate, AtmoConstants.MarshallPalmerSlopeExponent);
        }

        /// <summary>
        /// mm. The median-volume drop diameter for a rain rate (D0 = 3.67 / Lambda):
        /// the drop that splits the falling water in half, and the one the emitter
        /// draws at. It grows with rate, so heavy rain streaks and drizzle mists.
        /// </summary>
        public static float MedianDropDiameterMm(float rainfallMmPerH)
        {
            return AtmoConstants.MarshallPalmerMedianFactor / MarshallPalmerSlopePerMm(rainfallMmPerH);
        }

        /// <summary>
        /// 1/m3/mm. Marshall-Palmer number density at a drop diameter for a rain rate:
        /// N0 * exp(-Lambda * D). Always more small drops than large, but the large
        /// ones grow more numerous as the rain hardens.
        /// </summary>
        public static float DropNumberDensity(float dropDiameterMm, float rainfallMmPerH)
        {
            float slope = MarshallPalmerSlopePerMm(rainfallMmPerH);
            return AtmoConstants.MarshallPalmerN0 * Mathf.Exp(-slope * Mathf.Max(0f, dropDiameterMm));
        }

        /// <summary>
        /// m. The streak a falling particle draws in one shutter: its fall speed across
        /// the exposure time. Fast rain streaks; slow snow barely does.
        /// </summary>
        public static float StreakLengthM(float fallSpeedMPerS)
        {
            return Mathf.Max(0f, fallSpeedMPerS) * AtmoConstants.PrecipExposureSeconds;
        }

        /// <summary>
        /// m/s. Fall speed of one snowflake, spread deterministically about the mean by
        /// up to the snow variance, so a field of flakes does not descend in lockstep.
        /// The same flake index always draws the same speed.
        /// </summary>
        public static float SnowFallSpeedMPerS(int flakeIndex)
        {
            uint h = (uint)flakeIndex * SnowHashMix;
            h ^= h >> SnowHashShift;
            float unit = (h & 0xFFFFu) / (float)0xFFFF;   // 0..1
            float signed = unit * 2f - 1f;                // -1..1
            return AtmoConstants.SnowTerminalVelocityMPerS
                   * (1f + AtmoConstants.SnowFallSpeedVariance01 * signed);
        }

        [Tooltip("Particles per second emitted at full intensity and full quality.")]
        [SerializeField] private int emissionRateAtFull = 4000;

        [Tooltip("Fraction of the emission budget this quality tier is allowed, 0..1.")]
        [SerializeField, Range(0f, 1f)] private float qualityScale01 = 1f;

        // UC-T08-05: existing camera-near emitter consumes its own world's sample, with an explicit reference for cameras outside the world hierarchy.
        [SerializeField, Tooltip("Weather owner for this camera-near emitter. Empty resolves its parent UC world; standalone externally driven emitters remain supported.")]
        private TerrainSystem.UCWeatherSystemManager weatherSource;

        private ParticleSystem system;
        private ParticleSystemRenderer systemRenderer;
        private ParticleSystem.EmissionModule emission;
        private ParticleSystem.MainModule main;
        private ParticleSystem.VelocityOverLifetimeModule velocity;
        private bool cached;
        private bool hadWeatherSource;
        private int runtimeParticleLimit = 4096;
        private bool wasEmitting, previousSnow;
        private bool cameraDriven;

        private void Awake()
        {
            CacheModules();
        }

        private void OnEnable()
        {
            if (weatherSource == null) weatherSource = TerrainSystem.UCWeatherSystemManager.FindForWorld(transform);
            hadWeatherSource = weatherSource != null;
        }
        private void OnDisable()
        { if (cached) { emission.rateOverTime = 0; system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); } }
        private void Update()
        { if (!cameraDriven) RefreshLocalWeather(); } // UC-T08-08: pooled cameras receive the already captured render sample; external emitters retain their direct adapter.

        internal void BindWeatherSource(TerrainSystem.UCWeatherSystemManager source)
        {
            if (weatherSource != source && hadWeatherSource) ClearLocalParticles(); // UC-T08-08: rebinding cannot leave the previous world's drops alive.
            weatherSource = source; hadWeatherSource = source != null;
        }

        internal void RefreshLocalWeather()
        {
            if (weatherSource == null)
            {
                if (hadWeatherSource) UpdatePrecipitation(0, false, Vector3.zero);
                return;
            }
            hadWeatherSource = true;
            ApplyEnvironmentSample(weatherSource.SampleEnvironmentAt(transform.position));
        }

        /// <summary>UC-T08-08: consumes the view adapter's captured sample; avoids a second weather query in this rendered frame.</summary>
        /// <param name="sample">Local weather from the bound owner at this emitter's camera position and shared instant.</param>
        internal void ApplyEnvironmentSample(in EnvironmentSampleProduct sample)
        {
            WeatherState weather = sample.Weather;
            float angle = weather.windDirectionDeg * Mathf.Deg2Rad;
            UpdatePrecipitation(weather.precipitationMmPerHour, weather.precipitationType == WeatherPrecipType.PrecipKind.Snow,
                new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * weather.gustSpeed); // UC-T08-06: precipitation follows the same clock-resolved gust as vegetation.
        }

        // Grabs the modules once so the per-frame update touches only value types.
        private void CacheModules()
        {
            if (cached) return;
            system = GetComponent<ParticleSystem>();
            systemRenderer = GetComponent<ParticleSystemRenderer>();
            emission = system.emission;
            main = system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            cached = true;
        }

        /// <summary>Sets the fraction of the emission budget this tier may use (from the quality tier).</summary>
        public void SetQuality(float scale01)
        {
            if (!ClimateBaselineProduct.Finite(scale01)) throw new System.ArgumentOutOfRangeException(nameof(scale01));
            qualityScale01 = Mathf.Clamp01(scale01);
            if (qualityScale01 == 0) ClearLocalParticles(); // UC-T08-08: disabled effects retire existing particles immediately.
        }

        /// <summary>Applies a hard native camera-particle cap and proportional emission budget without replacing authored quality.</summary>
        /// <param name="maximumParticles">1..4096 particles per camera; tier reductions clear obsolete particles once.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">The cap is outside the bounded range; modules are retained.</exception>
        internal void SetRenderBudget(int maximumParticles)
        {
            if (maximumParticles < 1 || maximumParticles > 4096) throw new System.ArgumentOutOfRangeException(nameof(maximumParticles));
            if (!cached) CacheModules();
            if (maximumParticles < runtimeParticleLimit) ClearLocalParticles();
            runtimeParticleLimit = maximumParticles; main.maxParticles = maximumParticles;
        }

        /// <summary>Configures a generated camera-near box (32×10×32 m); authored external emitters keep their volume settings.</summary>
        internal void ConfigureCameraVolume()
        {
            if (!cached) CacheModules();
            main.playOnAwake = false;
            cameraDriven = true;
            var shape = system.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(32, 10, 32); shape.position = new Vector3(0, 5, 0);
            ClearLocalParticles();
        }

        /// <summary>Clears native emission and world-space particles on teleport, retirement, seek or pool rebinding.</summary>
        internal void ClearLocalParticles()
        {
            if (!cached) CacheModules();
            emission.rateOverTime = 0; system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            wasEmitting = false;
        }

        /// <summary>
        /// Drives the particle system for one frame from the rain rate, the snow flag and
        /// the world wind vector. Rain falls fast and almost straight; snow falls slow and
        /// blows sideways. Below a detectable rate the emitter simply stops emitting.
        /// Allocates nothing: every setter writes a value-type module.
        /// UC-T08-05: active emitters with positive quality resume after being stopped on disable.
        /// </summary>
        /// <param name="rainfallMmPerH">Finite precipitation in millimetres/hour; negative or undetectable rates stop emission, higher rates saturate the particle budget.</param>
        /// <param name="isSnow">True selects the existing snow fall/advection model; false selects rain.</param>
        /// <param name="windMPerS">Finite world-space wind velocity in metres/second.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Rate or wind is nonfinite; particle modules are retained.</exception>
        public void UpdatePrecipitation(float rainfallMmPerH, bool isSnow, Vector3 windMPerS)
        {
            if (!ClimateBaselineProduct.Finite(rainfallMmPerH) || !ClimateBaselineProduct.Finite(windMPerS.x) ||
                !ClimateBaselineProduct.Finite(windMPerS.y) || !ClimateBaselineProduct.Finite(windMPerS.z))
                throw new System.ArgumentOutOfRangeException(nameof(rainfallMmPerH));
            if (!cached) CacheModules();

            if (rainfallMmPerH < AtmoConstants.PrecipDetectableMmPerH || qualityScale01 <= 0)
            {
                emission.rateOverTime = 0f;
                if (wasEmitting) ClearLocalParticles();
                return;
            }

            if (wasEmitting && previousSnow != isSnow) ClearLocalParticles();
            wasEmitting = true; previousSnow = isSnow;

            float intensity01 = Mathf.Clamp01(rainfallMmPerH / AtmoConstants.ConvectiveRainReferenceMmPerH);
            main.maxParticles = runtimeParticleLimit;
            emission.rateOverTime = emissionRateAtFull * intensity01 * qualityScale01 * (runtimeParticleLimit / 4096f);

            float fallSpeed = isSnow
                ? AtmoConstants.SnowTerminalVelocityMPerS
                : RainFallSpeedMPerS(MedianDropDiameterMm(rainfallMmPerH));
            main.startSpeed = 0f;
            main.startLifetime = Mathf.Clamp(10 / Mathf.Max(0.1f, fallSpeed), 0.25f, 12);
            main.startSize = isSnow ? 0.015f : 0.008f; // UC-T08-08: artistic particle footprint, independent of the physical drop-size/fall model.
            main.startColor = isSnow ? new Color(1, 1, 1, 0.8f) : new Color(0.7f, 0.75f, 0.8f, 0.45f);

            float windGain = isSnow ? AtmoConstants.SnowWindAdvectionGain : 1f;
            velocity.x = windMPerS.x * windGain;
            velocity.z = windMPerS.z * windGain;
            velocity.y = -fallSpeed;

            // A stretched-billboard renderer draws each particle as long as it falls in
            // one shutter: rain streaks, snow stays a dot.
            if (systemRenderer != null)
            {
                systemRenderer.renderMode = isSnow ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.Stretch;
                systemRenderer.lengthScale = 1;
                systemRenderer.velocityScale = isSnow ? 0 : AtmoConstants.PrecipExposureSeconds;
            }
            // UC-T08-05: a pooled/disabled emitter was stopped on disable; re-enabled wet weather must resume the actual system.
            if (isActiveAndEnabled && qualityScale01 > 0 && !system.isPlaying) system.Play(true);
        }
    }
}
