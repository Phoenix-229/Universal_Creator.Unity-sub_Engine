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

        private ParticleSystem system;
        private ParticleSystemRenderer systemRenderer;
        private ParticleSystem.EmissionModule emission;
        private ParticleSystem.MainModule main;
        private ParticleSystem.VelocityOverLifetimeModule velocity;
        private bool cached;

        private void Awake()
        {
            CacheModules();
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
            qualityScale01 = Mathf.Clamp01(scale01);
        }

        /// <summary>
        /// Drives the particle system for one frame from the rain rate, the snow flag and
        /// the world wind vector. Rain falls fast and almost straight; snow falls slow and
        /// blows sideways. Below a detectable rate the emitter simply stops emitting.
        /// Allocates nothing: every setter writes a value-type module.
        /// </summary>
        public void UpdatePrecipitation(float rainfallMmPerH, bool isSnow, Vector3 windMPerS)
        {
            if (!cached) CacheModules();

            if (rainfallMmPerH < AtmoConstants.PrecipDetectableMmPerH)
            {
                emission.rateOverTime = 0f;
                return;
            }

            float intensity01 = Mathf.Clamp01(rainfallMmPerH / AtmoConstants.ConvectiveRainReferenceMmPerH);
            emission.rateOverTime = emissionRateAtFull * intensity01 * qualityScale01;

            float fallSpeed = isSnow
                ? AtmoConstants.SnowTerminalVelocityMPerS
                : RainFallSpeedMPerS(MedianDropDiameterMm(rainfallMmPerH));
            main.startSpeed = 0f;

            float windGain = isSnow ? AtmoConstants.SnowWindAdvectionGain : 1f;
            velocity.x = windMPerS.x * windGain;
            velocity.z = windMPerS.z * windGain;
            velocity.y = -fallSpeed;

            // A stretched-billboard renderer draws each particle as long as it falls in
            // one shutter: rain streaks, snow stays a dot.
            if (systemRenderer != null)
                systemRenderer.lengthScale = StreakLengthM(fallSpeed);
        }
    }
}
