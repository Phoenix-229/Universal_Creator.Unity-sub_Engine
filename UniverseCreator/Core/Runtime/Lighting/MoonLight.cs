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
