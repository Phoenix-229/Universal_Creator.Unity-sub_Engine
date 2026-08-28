// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    ExposureModel.cs
//  Purpose: Turn the scene's physical key illuminance into a display exposure,
//           analytically. A sun that spans 100000 lux at noon and a quarter of a
//           lux at full moon cannot be shown on one screen without this, and an
//           analytic exposure - computed from the sky, not from the image - is
//           deterministic and does not swim when the camera turns.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Pure photometric exposure: illuminance to EV100, EV100 to an exposure
    /// multiplier, and a rate-limited eye adaptation. No frame time, no camera and
    /// no image histogram - the same illuminance yields the same exposure on every
    /// machine, which is what keeps a turned camera from changing the brightness.
    /// </summary>
    public static class ExposureModel
    {
        // Below this the log would run away; the scene key is floored here so a
        // moonless value still maps to a finite, very dark exposure.
        private const float MinKeyLux = 1e-4f;

        /// <summary>
        /// Exposure value at ISO 100 for a horizontal illuminance, from
        /// EV100 = log2(E * S / C). Noon ~15, overcast ~12, full moon ~-3.
        /// </summary>
        public static float Ev100FromLux(float illuminanceLux)
        {
            float e = Mathf.Max(illuminanceLux, MinKeyLux);
            float ratio = e * LightingConstants.ExposureMeterIso / LightingConstants.IncidentMeterCalibration;
            return Mathf.Log(ratio, 2f);
        }

        /// <summary>
        /// Scene luminance that maps to white at this exposure value,
        /// maxLuminance = (78 / (S q)) * 2^EV100.
        /// </summary>
        public static float MaxLuminance(float ev100)
        {
            return LightingConstants.MaxLuminanceFactor * Mathf.Pow(2f, ev100);
        }

        /// <summary>The linear multiplier applied to the scene, the reciprocal of that white point.</summary>
        public static float ExposureMultiplier(float ev100)
        {
            return 1f / Mathf.Max(MaxLuminance(ev100), MinKeyLux);
        }

        /// <summary>Exposure multiplier straight from a key illuminance in lux.</summary>
        public static float ExposureMultiplierFromLux(float illuminanceLux)
        {
            return ExposureMultiplier(Ev100FromLux(illuminanceLux));
        }

        /// <summary>
        /// Horizontal illuminance from the moon in lux: the full-moon anchor scaled by
        /// how much of the disc is lit and by how high the moon rides (its projection
        /// onto the ground). Zero below the horizon. The one place moonlight becomes a
        /// number, so the scene key and the sky's night fill cannot disagree on it.
        /// </summary>
        public static float MoonIlluminanceLux(float moonElevationDeg, float moonIlluminatedFraction)
        {
            float moonSin = Mathf.Sin(moonElevationDeg * Mathf.Deg2Rad);
            return LightingConstants.FullMoonLux
                   * Mathf.Clamp01(moonIlluminatedFraction)
                   * Mathf.Max(0f, moonSin);
        }

        /// <summary>
        /// The scene's key illuminance in lux: the brightest of the direct sun, the
        /// moon and the starlight floor. The sun term is the sky module's own
        /// photometric value; the moon term is <see cref="MoonIlluminanceLux"/>; the
        /// floor keeps a moonless night finite and navigable. Taking the brightest
        /// source, not a sum, avoids double-counting a twilight where sun and moon are
        /// both faint.
        /// </summary>
        public static float SceneKeyIlluminanceLux(float sunElevationDeg, float sunAirMass, float turbidity,
                                                   float cloudCover01, float moonElevationDeg,
                                                   float moonIlluminatedFraction)
        {
            float sunLux = SkyModel.SunIlluminanceLux(sunElevationDeg, sunAirMass, turbidity, cloudCover01);
            float moonLux = MoonIlluminanceLux(moonElevationDeg, moonIlluminatedFraction);
            return Mathf.Max(sunLux, Mathf.Max(moonLux, LightingConstants.StarlightLux));
        }

        /// <summary>
        /// One rate-limited step of eye adaptation from the current EV100 toward a
        /// target, at authorable EV/second rates. Brightening (target above current)
        /// uses the fast light-adaptation rate, darkening the slow one, and the step
        /// never overshoots the target.
        /// </summary>
        public static float AdaptEv100(float currentEv100, float targetEv100, float deltaSeconds,
                                       float lightRateEvPerSec, float darkRateEvPerSec)
        {
            float delta = targetEv100 - currentEv100;
            float rate = delta > 0f ? lightRateEvPerSec : darkRateEvPerSec;
            float maxStep = Mathf.Max(0f, rate) * Mathf.Max(0f, deltaSeconds);
            return currentEv100 + Mathf.Clamp(delta, -maxStep, maxStep);
        }

        /// <summary>Eye adaptation at the module's default asymmetric rates.</summary>
        public static float AdaptEv100(float currentEv100, float targetEv100, float deltaSeconds)
        {
            return AdaptEv100(currentEv100, targetEv100, deltaSeconds,
                LightingConstants.LightAdaptationEvPerSec, LightingConstants.DarkAdaptationEvPerSec);
        }
    }
}
