// UC celestial repair: bounded, catalog-backed sky data; the existing lighting manager owns its lifetime.
using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>Bakes BSC5 J2000 stellar positions once into a 16 MiB equatorial texture, without per-star scene objects.</summary>
    /// <remarks>Main-thread construction/upload; temporary pixels are released after upload. Coordinates omit precession/proper motion.
    /// The manager owns Texture and must destroy it after restoring camera bindings. The retained resource contains the original catalog.</remarks>
    internal sealed class StarSky
    {
        internal const string ResourcePath = "UniverseCreator/BSC5/catalog";
        // UC celestial follow-up: halve the angular raster footprint without per-frame work or unbounded texture growth.
        internal const int Width = 2048, Height = 1024;
        internal readonly Texture2D Texture;
        internal readonly int Count;
        internal readonly float RadianceScale;

        /// <summary>Parses and bakes the original fixed-width Bright Star Catalog, rejecting missing/invalid coordinates.</summary>
        /// <param name="catalog">Original BSC5 TextAsset; right ascension in hours, declination in degrees, visual magnitude.</param>
        /// <exception cref="ArgumentException">The source is missing or contains no usable stars.</exception>
        internal StarSky(TextAsset catalog)
        {
            if (catalog == null) throw new ArgumentException("UC stars require the retained BSC5 catalog.", nameof(catalog));
            var pixels = new Color[Width * Height];
            int count = 0;
            double integratedFlux = 0;
            using (var reader = new StringReader(catalog.text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!TryRead(line, out float ra, out float dec, out float magnitude)) continue;
                    // Magnitude defines relative flux; the retained starlight anchor sets the integrated all-sky scale.
                    float flux = Mathf.Pow(10f, -0.4f * magnitude);
                    float u = ra / 360f * Width - 0.5f, v = (dec / 180f + 0.5f) * Height - 0.5f;
                    int x = Mathf.FloorToInt(u), y = Mathf.FloorToInt(v);
                    float fx = u - x, fy = v - y;
                    Add(pixels, x, y, flux * (1 - fx) * (1 - fy));
                    Add(pixels, x + 1, y, flux * fx * (1 - fy));
                    Add(pixels, x, y + 1, flux * (1 - fx) * fy);
                    Add(pixels, x + 1, y + 1, flux * fx * fy);
                    integratedFlux += flux;
                    count++;
                }
            }
            if (count == 0) throw new ArgumentException("UC star catalog has no usable J2000 positions.", nameof(catalog));
            Count = count;
            // The raster uses equal RA/Dec bins, whose solid angle varies with cos(dec). Undo that density at sample time.
            double equatorialPixelAngle = (2 * Math.PI / Width) * (Math.PI / Height);
            RadianceScale = (float)(LightingConstants.StarlightLux / (integratedFlux * equatorialPixelAngle));
            Texture = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true) {
                name = "UC BSC5 stars (J2000)", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
            };
            Texture.SetPixels(pixels);
            Texture.Apply(false, true); // 32 MiB temporary pixel buffer and native CPU copy are not retained.
        }

        private static void Add(Color[] pixels, int x, int y, float flux)
        {
            x = (x % Width + Width) % Width; y = Mathf.Clamp(y, 0, Height - 1);
            pixels[y * Width + x] += new Color(flux, flux, flux, 0);
        }

        /// <summary>Reads BSC5's one-based columns 76–90 and 103–107 without accepting nonstellar/malformed records.</summary>
        /// <param name="line">One original catalog line; no mutation or locale-dependent parsing.</param>
        /// <param name="ra">Right ascension, 0..360 degrees.</param>
        /// <param name="dec">Declination, -90..90 degrees.</param>
        /// <param name="magnitude">Finite visual magnitude, -10..20.</param>
        /// <returns>False for missing, invalid or out-of-range input; outputs remain zero.</returns>
        internal static bool TryRead(string line, out float ra, out float dec, out float magnitude)
        {
            ra = dec = magnitude = 0;
            if (string.IsNullOrEmpty(line) || line.Length < 107 ||
                !Number(line, 75, 2, out float h) || !Number(line, 77, 2, out float m) || !Number(line, 79, 4, out float s) ||
                !Number(line, 84, 2, out float d) || !Number(line, 86, 2, out float dm) || !Number(line, 88, 2, out float ds) ||
                !Number(line, 102, 5, out float mag) || (line[83] != '+' && line[83] != '-') ||
                h < 0 || h >= 24 || m < 0 || m >= 60 || s < 0 || s >= 60 || d < 0 || d > 90 ||
                dm < 0 || dm >= 60 || ds < 0 || ds >= 60 || mag < -10 || mag > 20) return false;
            float declination = d + dm / 60f + ds / 3600f;
            if (declination > 90) return false;
            ra = (h + m / 60f + s / 3600f) * 15f;
            dec = line[83] == '-' ? -declination : declination;
            magnitude = mag;
            return true;
        }

        private static bool Number(string text, int start, int length, out float value) =>
            float.TryParse(text.Substring(start, length), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Rotates world +X east/+Y up/+Z north rays into J2000 RA/Dec using the existing game's solar calendar.</summary>
        /// <param name="clock">Captured latitude, fractional game time and solar hour angle; no host OS time.</param>
        /// <returns>Orthonormal direction matrix; camera translation/world dimensions have no effect.</returns>
        internal static Matrix4x4 WorldToEquatorial(in ClockState clock)
        {
            float latitude = clock.latitudeDeg * Mathf.Deg2Rad;
            float sidereal = ClockMath.LocalSiderealAngleDeg(clock) * Mathf.Deg2Rad;
            float cp = Mathf.Cos(latitude), sp = Mathf.Sin(latitude), cs = Mathf.Cos(sidereal), ss = Mathf.Sin(sidereal);
            var matrix = Matrix4x4.identity;
            matrix.SetRow(0, new Vector4(-ss, cs * cp, -cs * sp, 0));
            matrix.SetRow(1, new Vector4(0, sp, cp, 0));
            matrix.SetRow(2, new Vector4(cs, ss * cp, -ss * sp, 0));
            return matrix;
        }

        /// <summary>Fades discrete stars continuously through twilight; the shared sky/cloud pass handles local occlusion.</summary>
        /// <param name="solarElevationDeg">Sun altitude in degrees; fully visible below astronomical twilight, absent at civil twilight.</param>
        /// <returns>Visibility from zero to one. Shared exposure naturally reduces stars under moonlight.</returns>
        internal static float Visibility(float solarElevationDeg) => 1 - Mathf.SmoothStep(0, 1,
            Mathf.InverseLerp(LightingConstants.AstronomicalTwilightDeg, LightingConstants.CivilTwilightDeg, solarElevationDeg));
    }
}
