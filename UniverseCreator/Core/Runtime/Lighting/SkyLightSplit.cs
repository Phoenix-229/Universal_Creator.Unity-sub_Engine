// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SkyLightSplit.cs
//  Purpose: Split the one horizontal illuminance the sky reports into the two terms
//           cloud drives apart - a direct beam that casts shadows and a diffuse sky
//           remainder that does not. Cloud dims the beam and, because the total is
//           conserved, leaves relatively more of the light in the shadowless diffuse
//           term: an overcast day is soft and shadowless at a plausible brightness,
//           a clear day is hard-shadowed, and neither is an authored preset - both
//           fall out of the combined cover alone.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The directional-vs-diffuse division of horizontal illuminance under cloud, pure.
    /// The direct beam fraction is the clear-sky fraction scaled by how much of the sun
    /// disc the cover leaves unobscured; the diffuse term is the conserved remainder, so
    /// thickening cloud moves light out of the shadow-casting beam and into the soft sky
    /// fill without any separate overcast switch.
    /// </summary>
    public static class SkyLightSplit
    {
        /// <summary>
        /// Fraction of the total horizontal illuminance arriving as the direct,
        /// shadow-casting beam: the clear-sky direct fraction times the unobscured
        /// share of the sun disc (~1 - cover). Clear sky keeps most of it; full
        /// overcast drives it to zero.
        /// </summary>
        public static float DirectBeamFraction01(float combinedCover01)
        {
            float clear = 1f - Mathf.Clamp01(combinedCover01);
            return LightingConstants.ClearSkyDirectBeamFraction01 * clear;
        }

        /// <summary>
        /// The shadow-casting directional illuminance in lux: the beam fraction of the
        /// total. Falls to zero under full cover, which is what makes an overcast day
        /// shadowless rather than merely darker.
        /// </summary>
        public static float DirectionalLux(float totalKeyLux, float combinedCover01)
        {
            return Mathf.Max(0f, totalKeyLux) * DirectBeamFraction01(combinedCover01);
        }

        /// <summary>
        /// The diffuse sky illuminance in lux: the conserved remainder of the total
        /// after the beam is taken out. Its share of the total rises as cover thickens,
        /// which is the "raise the diffuse term" half of the two-term change.
        /// </summary>
        public static float DiffuseLux(float totalKeyLux, float combinedCover01)
        {
            float total = Mathf.Max(0f, totalKeyLux);
            return total - DirectionalLux(total, combinedCover01);
        }

        /// <summary>
        /// Shadow strength for the directional light in 0..1: full and hard under a
        /// clear sky, collapsing to zero under full cover so the key light stops
        /// drawing shadows at all. Normalised (independent of the clear-sky fraction)
        /// because it drives a light's shadow parameter, not an illuminance.
        /// </summary>
        public static float ShadowStrength01(float combinedCover01)
        {
            return 1f - Mathf.Clamp01(combinedCover01);
        }
    }
}
