// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MoonLight.cs
//  Purpose: The moon's own light, derived rather than authored. Moonlight is sunlight:
//           the sun's spectrum reflected off grey lunar rock, so its colour is neutral
//           and faintly warm, never blue. Its strength follows how much of the disc is
//           lit and how high the moon rides, scaled by the one authored trim the manager
//           carries. The blue of a moonlit night is the eye (the Purkinje shift, applied
//           to the final image), not this light - so nothing here tints toward blue, and
//           a full moon casts a soft neutral-grey shadow while a new moon casts none.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather; // UC celestial repair: use the existing clock's atmospheric air-path calculation.

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The moon's colour and directional-light intensity, derived from the sun's spectrum
    /// and the lit fraction of the disc. One place, so the moon that lights the ground and
    /// the moon disc drawn in the sky cannot end up two different colours.
    /// </summary>
    public static class MoonLight
    {
        /// <summary>
        /// Moonlight colour at source, in linear RGB: the daylight sun's blackbody spectrum
        /// reflected off the grey lunar albedo. Neutral by construction - the reddening of a
        /// low moon is the atmosphere's extinction (shared with the sun), applied downstream,
        /// not a property of the light itself, and there is no blue tint anywhere in it.
        /// </summary>
        public static Color ColorLinear()
        {
            Color sun = SkyModel.Blackbody(LightingConstants.DaylightSunKelvin);
            Vector3 a = LightingConstants.MoonAlbedoGrey;
            return new Color(sun.r * a.x, sun.g * a.y, sun.b * a.z, 1f);
        }

        /// <summary>Applies the shared atmospheric path to the lunar spectrum and its zenith-relative beam strength.</summary>
        /// <param name="elevationDeg">Finite lunar elevation in degrees; a below-horizon body uses the horizon air path.</param>
        /// <param name="turbidity">Finite atmospheric turbidity, at least one.</param>
        /// <param name="beamTransmission01">Output 0..1 beam attenuation relative to the same atmosphere at zenith; preserves the full-moon lux anchor.</param>
        /// <returns>Linear source colour after extinction; callers normalize hue separately from beam intensity. Horizon/cloud occlusion is applied by the existing driver.</returns>
        internal static Color ColorLinear(float elevationDeg, float turbidity, out float beamTransmission01)
        {
            // UC celestial repair: a low moon crosses the same longer, redder air path as the sun.
            Vector3 transmission = SkyModel.AtmosphericTransmittance(ClockMath.AirMass(Mathf.Max(0, elevationDeg)), turbidity);
            float zenithTransmission = SkyModel.AtmosphericTransmittance(ClockMath.AirMass(90), turbidity).y;
            beamTransmission01 = Mathf.Clamp01(transmission.y / Mathf.Max(zenithTransmission, 1e-8f));
            Color source = ColorLinear();
            return new Color(source.r * transmission.x, source.g * transmission.y, source.b * transmission.z, 1);
        }

        /// <summary>Computes the visible area of a uniform lunar disc crossing the flat astronomical horizon.</summary>
        /// <param name="elevationDeg">Finite centre elevation in degrees.</param>
        /// <param name="diameterDeg">Finite nonnegative angular diameter in degrees; zero disables the disc.</param>
        /// <returns>Visible fraction 0..1, continuous at moonrise and moonset; terrain/cloud occlusion remains renderer-owned.</returns>
        internal static float HorizonVisibility01(float elevationDeg, float diameterDeg)
        {
            // UC celestial repair: circular-segment area avoids switching a full directional beam on at zero altitude.
            if (diameterDeg <= 0) return 0;
            float height = Mathf.Clamp(2 * elevationDeg / diameterDeg, -1, 1);
            return (Mathf.Acos(-height) + height * Mathf.Sqrt(Mathf.Max(0, 1 - height * height))) / Mathf.PI;
        }

        /// <summary>
        /// The moon directional light's intensity: the physical illuminance it casts on the
        /// ground (full-moon anchor scaled by the lit fraction and the moon's altitude, via
        /// <see cref="ExposureModel.MoonIlluminanceLux"/>) times the authored trim. A full
        /// moon returns a small but non-zero value, a crescent much less, a new moon or a
        /// moon below the horizon exactly zero - the "soft shadows / almost none / none" law.
        /// </summary>
        public static float Intensity(float moonElevationDeg, float moonIlluminatedFraction, float moonIntensityTrim)
        {
            float lux = ExposureModel.MoonIlluminanceLux(moonElevationDeg, moonIlluminatedFraction);
            return Mathf.Max(0f, lux) * Mathf.Max(0f, moonIntensityTrim);
        }
    }
}
