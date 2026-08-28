// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SunShafts.cs
//  Purpose: The strength of the crepuscular-ray term, pure. Sun shafts are the beam
//           made visible where it streams through the gaps in a broken deck, and they
//           are drawn as a screen-space radial term masked by the cloud's transmittance
//           at the sun's screen position - never a volumetric march. This is the CPU
//           side of that: the master intensity that turns the effect off where it has
//           no business showing (a clear sky has no gaps to define rays, a solid
//           overcast lets nothing through, and a high sun casts no rays) and full where
//           it does (a low sun through broken cloud). The screen-space radial blur that
//           consumes this lives in the fullscreen shader.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The sun-shaft master intensity, pure. A product of two windows - a coverage
    /// window that peaks in the broken band and dies at clear and overcast, and a
    /// low-sun window that dies as the sun climbs - so the effect is self-gating and
    /// needs no authored on/off state beyond the platform quality toggle.
    /// </summary>
    public static class SunShafts
    {
        /// <summary>
        /// The coverage window in 0..1: zero under a clear sky (no gaps to define rays),
        /// ramping up to full across the low edge of the broken band, holding full across
        /// the band, then falling back to zero by solid overcast (nothing gets through).
        /// A trapezoid centred on the broken band.
        /// </summary>
        public static float CoverageWindow01(float combinedCover01)
        {
            float c = Mathf.Clamp01(combinedCover01);
            float lo = LightingConstants.ShaftBrokenLowCover01;
            float hi = LightingConstants.ShaftBrokenHighCover01;
            if (c <= lo) return Mathf.InverseLerp(0f, lo, c);       // clear -> broken: rise
            if (c >= hi) return Mathf.InverseLerp(1f, hi, c);       // broken -> overcast: fall
            return 1f;                                              // full across the broken band
        }

        /// <summary>
        /// The low-sun window in 0..1: full with the sun at the horizon, falling to zero
        /// by the top elevation, and zero with the sun below the horizon (no direct beam
        /// to rake through the gaps). Rays are a low-sun phenomenon.
        /// </summary>
        public static float LowSunWeight01(float sunElevationDeg)
        {
            if (sunElevationDeg < 0f) return 0f;
            return 1f - Mathf.Clamp01(sunElevationDeg / LightingConstants.ShaftLowSunTopDeg);
        }

        /// <summary>
        /// The master shaft intensity in 0..1: the product of the coverage and low-sun
        /// windows. Full only for a low sun through broken cloud; zero for a clear sky, a
        /// solid overcast, or a high sun. This scales the screen-space radial term.
        /// </summary>
        public static float Intensity01(float combinedCover01, float sunElevationDeg)
        {
            return CoverageWindow01(combinedCover01) * LowSunWeight01(sunElevationDeg);
        }
    }
}
