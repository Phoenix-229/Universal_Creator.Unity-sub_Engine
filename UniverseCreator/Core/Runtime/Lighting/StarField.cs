// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    StarField.cs
//  Purpose: The night sky's stars, as content rather than chance. A star field is a
//           fixed thing - the same stars in the same places every night - so it is built
//           from a hash of each star's index, never a random roll: reproducible, and the
//           same on every machine. The field turns with the sidereal day (about four
//           minutes short of the solar day, which is why a given star rises earlier each
//           night) about the celestial pole, whose height above the horizon is the
//           observer's latitude. Stars fade as the moon, the cloud deck and haze wash the
//           sky out, and vanish under overcast. Pure math; the skybox draw is the driver's.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>One star's equatorial position and brightness. Right ascension in degrees
    /// (0..360), declination in degrees (-90..90), apparent magnitude (smaller = brighter).</summary>
    public struct Star
    {
        public float raDeg;
        public float decDeg;
        public float magnitude;
    }

    /// <summary>
    /// The deterministic star field: its sidereal rotation, the equatorial-to-horizontal
    /// direction of a star for an observer's latitude, its visibility under moon/cloud/haze,
    /// and the per-index star generator that makes the field reproducible content.
    /// </summary>
    public static class StarField
    {
        // A clear night sits near turbidity 2; by 8 the sky is too milky for stars.
        private const float ClearTurbidity = 2.0f;
        private const float StarKillTurbidity = 8.0f;

        /// <summary>
        /// Local sidereal angle in degrees for a time in seconds since the star epoch. The
        /// field turns one full circle per <see cref="Ecology.EcoConstants.SecondsPerSiderealDay"/>
        /// - about four minutes short of the solar day, the reason the stars drift against
        /// the clock.
        /// </summary>
        public static float LocalSiderealDeg(double secondsSinceEpoch)
        {
            double turns = secondsSinceEpoch / UniverseCreator.Ecology.EcoConstants.SecondsPerSiderealDay;
            double frac = turns - System.Math.Floor(turns);
            return (float)(frac * 360.0);
        }

        /// <summary>
        /// Local horizontal direction (world axes: +x east, +y up, +z north) to a star at
        /// equatorial (ra, dec), for an observer latitude and the local sidereal angle. The
        /// celestial pole (dec = 90) maps to altitude = latitude at every sidereal angle, so
        /// the whole field turns about a pole fixed at the latitude's height - the axis the
        /// real sky turns on, which is what makes circumpolar stars circle and the rest rise
        /// and set.
        /// </summary>
        public static Vector3 StarDirection(float raDeg, float decDeg, float latitudeDeg, float localSiderealDeg)
        {
            float H = (localSiderealDeg - raDeg) * Mathf.Deg2Rad;   // hour angle
            float dec = decDeg * Mathf.Deg2Rad;
            float phi = latitudeDeg * Mathf.Deg2Rad;
            float sinDec = Mathf.Sin(dec), cosDec = Mathf.Cos(dec);
            float sinPhi = Mathf.Sin(phi), cosPhi = Mathf.Cos(phi);
            float cosH = Mathf.Cos(H), sinH = Mathf.Sin(H);

            float sinAlt = Mathf.Clamp(sinDec * sinPhi + cosDec * cosPhi * cosH, -1f, 1f);
            float alt = Mathf.Asin(sinAlt);
            float cosAlt = Mathf.Cos(alt);

            float az;
            if (cosAlt < 1e-6f)
            {
                az = 0f;   // at the zenith azimuth is undefined; any value gives the same point
            }
            else
            {
                float sinAz = -cosDec * sinH / cosAlt;
                float cosAz = (sinDec - sinPhi * sinAlt) / (cosPhi * cosAlt + 1e-9f);
                az = Mathf.Atan2(sinAz, cosAz);   // from north, positive toward east
            }

            float horiz = Mathf.Cos(alt);
            return new Vector3(horiz * Mathf.Sin(az), Mathf.Sin(alt), horiz * Mathf.Cos(az));
        }

        /// <summary>
        /// How visible the field is, 0..1: a dark, clear night is 1; moonlight, cloud and
        /// haze each wash it toward 0. Overcast alone drives it to zero (the deck hides the
        /// sky), a full moon (the 0.25 lux anchor) nearly so, and milky air the rest.
        /// </summary>
        public static float Visibility01(float moonIlluminanceLux, float cloudCover01, float turbidity)
        {
            float cloud = 1f - Mathf.Clamp01(cloudCover01);
            float moon = 1f - Mathf.Clamp01(moonIlluminanceLux / LightingConstants.FullMoonLux);
            float haze = 1f - Mathf.Clamp01((turbidity - ClearTurbidity) / (StarKillTurbidity - ClearTurbidity));
            return Mathf.Clamp01(cloud * moon * haze);
        }

        /// <summary>
        /// A star's equatorial position and brightness from its index alone - so the same
        /// field is rebuilt identically every run, on every machine. Points are uniform on
        /// the sphere (declination through an arcsine, so they do not bunch at the poles);
        /// magnitudes span the roughly -1 to 6 the naked eye sees.
        /// </summary>
        public static Star StarAt(int index)
        {
            float u = Hash01(index * 2 + 1);
            float v = Hash01(index * 2 + 2);
            float w = Hash01(index * 3 + 7);
            return new Star
            {
                raDeg = u * 360f,
                decDeg = Mathf.Asin(2f * v - 1f) * Mathf.Rad2Deg,
                magnitude = -1f + w * 7f,
            };
        }

        // Integer hash to [0, 1). Self-contained and deterministic: a star field is content,
        // not a roll of dice, so its randomness is a fixed function of the index.
        private static float Hash01(int n)
        {
            uint x = (uint)n;
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16;
            return (x & 0xffffffu) / 16777216f;
        }
    }
}
