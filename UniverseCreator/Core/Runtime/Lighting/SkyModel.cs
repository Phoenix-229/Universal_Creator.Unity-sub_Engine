// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    SkyModel.cs
//  Purpose: The analytic sky. Blackbody sun colour and the atmospheric extinction
//           that reddens it toward the horizon - pure functions of the clock's
//           altitude and air mass and an authored turbidity, so a world lights the
//           same on every machine with no colour-ramp asset anywhere.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Sun colour from real physics: a blackbody whose temperature climbs with the
    /// sun's altitude, thinned per channel by the beam's extinction along the air
    /// mass to the sun. One mechanism gives both the white midday sun and the red
    /// sunset; there is no separate sunset gradient.
    /// </summary>
    public static class SkyModel
    {
        /// <summary>
        /// Linear-RGB hue of a Planckian radiator at this colour temperature,
        /// normalised so the brightest channel is 1. Brightness is the light's
        /// intensity, set elsewhere; this is only the colour. Valid ~1700-25000 K.
        /// </summary>
        public static Color Blackbody(float kelvin)
        {
            float t = Mathf.Clamp(kelvin, 1667f, 25000f);
            float t2 = t * t;
            float t3 = t2 * t;

            // CIE 1931 chromaticity of the Planckian locus (Kim et al. cubic fit).
            float xc;
            if (t < 4000f)
                xc = -0.2661239e9f / t3 - 0.2343589e6f / t2 + 0.8776956e3f / t + 0.179910f;
            else
                xc = -3.0258469e9f / t3 + 2.1070379e6f / t2 + 0.2226347e3f / t + 0.240390f;

            float xc2 = xc * xc;
            float xc3 = xc2 * xc;
            float yc;
            if (t < 2222f)
                yc = -1.1063814f * xc3 - 1.34811020f * xc2 + 2.18555832f * xc - 0.20219683f;
            else if (t < 4000f)
                yc = -0.9549476f * xc3 - 1.37418593f * xc2 + 2.09137015f * xc - 0.16748867f;
            else
                yc = 3.0817580f * xc3 - 5.87338670f * xc2 + 3.75112997f * xc - 0.37001483f;

            return ChromaticityToLinear(xc, yc);
        }

        /// <summary>
        /// Per-channel beam transmittance along the path, exp(-k * airMass * turbidity)
        /// from the L-27 coefficients. This is the one atmospheric extinction on the CPU
        /// side - the sun colour and the sun illuminance below both read it rather than
        /// each rolling the exponential themselves, and its GPU twin lives in
        /// UC_Lighting.hlsl reading the same coefficients as a published global. Blue
        /// falls off fastest, which is the reddening of a low sun and the blue of the sky.
        /// </summary>
        public static Vector3 AtmosphericTransmittance(float airMass, float turbidity)
        {
            float m = Mathf.Max(0f, airMass) * Mathf.Max(0f, turbidity);
            Vector3 k = LightingConstants.BeamExtinction;
            return new Vector3(Mathf.Exp(-k.x * m), Mathf.Exp(-k.y * m), Mathf.Exp(-k.z * m));
        }

        /// <summary>
        /// Linear-RGB sun colour. The blackbody temperature rises with altitude
        /// (ember at the horizon, near-white overhead); the beam extinction then
        /// dims each channel by exp(-k * airMass * turbidity), taking more blue
        /// than red, which is the reddening at low sun. Air mass is the clock's,
        /// not recomputed here.
        /// </summary>
        public static Color SunColorLinear(float elevationDeg, float airMass, float turbidity)
        {
            float warm01 = Mathf.Clamp01(
                (elevationDeg - LightingConstants.SunColorElevationFloorDeg)
                / LightingConstants.SunColorElevationSpanDeg);
            float kelvin = Mathf.Lerp(LightingConstants.TwilightSunKelvin,
                                      LightingConstants.DaylightSunKelvin, warm01);
            Color body = Blackbody(kelvin);

            Vector3 t = AtmosphericTransmittance(airMass, turbidity);
            return new Color(body.r * t.x, body.g * t.y, body.b * t.z, 1f);
        }

        /// <summary>
        /// Horizontal illuminance from the sun in lux: the clear-sky noon anchor
        /// projected by the sun's altitude and thinned by the beam transmittance
        /// along the air mass, floored to civil-twilight glow in the last degrees
        /// before the sun clears the horizon, then dimmed by cloud cover. Below
        /// civil twilight it returns zero - the moon and stars are a separate path.
        /// The photometric anchors are named in LightingConstants (S-07).
        /// </summary>
        public static float SunIlluminanceLux(
            float elevationDeg, float airMass, float turbidity, float cloudCover01)
        {
            if (elevationDeg <= LightingConstants.SunIlluminanceFloorDeg) return 0f;

            float sinElev = Mathf.Sin(elevationDeg * Mathf.Deg2Rad);
            float transmit = AtmosphericTransmittance(airMass, turbidity).y;
            float clearHoriz = LightingConstants.NoonClearSkyLux * Mathf.Max(0f, sinElev) * transmit;

            // A civil-twilight floor across [floor, 0], so the band just before
            // sunrise carries light even though the sun's projection is negative.
            float twilight01 = Mathf.Clamp01(
                (elevationDeg - LightingConstants.SunIlluminanceFloorDeg)
                / -LightingConstants.SunIlluminanceFloorDeg);
            float key = Mathf.Max(clearHoriz, LightingConstants.CivilTwilightLux * twilight01);

            // Full overcast lands at the overcast anchor's fraction of clear sky,
            // which is the order-of-magnitude drop a real overcast noon is.
            float overcastFraction = LightingConstants.OvercastDayLux / LightingConstants.NoonClearSkyLux;
            float cloudFactor = Mathf.Lerp(1f, overcastFraction, Mathf.Clamp01(cloudCover01));
            return key * cloudFactor;
        }

        /// <summary>
        /// Linear-RGB sky radiance for a view direction, from the Perez/Preetham
        /// analytic model (L-28). One physical parameter, turbidity, produces the
        /// circumsolar brightening, the brighter horizon, the deeper overhead and the
        /// blue gradient together - a two-colour ramp can do none of them. Luminance
        /// is carried in cd/m^2 (the exposure model maps it); directions are world
        /// space with +y up. Clamped at the horizon and the zenith so no view is a NaN.
        /// </summary>
        public static Color SkyRadiance(Vector3 viewDir, Vector3 sunDir, float turbidity)
        {
            Vector3 v = viewDir.normalized;
            Vector3 s = sunDir.normalized;
            float cosThetaV = Mathf.Max(v.y, 0.01f);
            float cosThetaS = Mathf.Clamp(s.y, 0.01f, 1f);
            float thetaS = Mathf.Acos(cosThetaS);
            float gamma = Mathf.Acos(Mathf.Clamp(Vector3.Dot(v, s), -1f, 1f));
            float T = Mathf.Max(1f, turbidity);

            // The five distribution coefficients, fitted linearly from turbidity, for
            // luminance and for each chromaticity channel (Preetham et al.).
            float aY = 0.1787f * T - 1.4630f, bY = -0.3554f * T + 0.4275f, cY = -0.0227f * T + 5.3251f, dY = 0.1206f * T - 2.5771f, eY = -0.0670f * T + 0.3703f;
            float ax = -0.0193f * T - 0.2592f, bx = -0.0665f * T + 0.0008f, cx = -0.0004f * T + 0.2125f, dx = -0.0641f * T - 0.8989f, ex = -0.0033f * T + 0.0452f;
            float ay = -0.0167f * T - 0.2608f, by = -0.0950f * T + 0.0092f, cy = -0.0079f * T + 0.2102f, dy = -0.0441f * T - 1.6537f, ey = -0.0109f * T + 0.0529f;

            float Y = SkyChannel(ZenithLuminance(thetaS * Mathf.Rad2Deg, T), cosThetaV, gamma, thetaS, aY, bY, cY, dY, eY);
            float x = SkyChannel(ZenithChromaX(thetaS, T), cosThetaV, gamma, thetaS, ax, bx, cx, dx, ex);
            float y = SkyChannel(ZenithChromaY(thetaS, T), cosThetaV, gamma, thetaS, ay, by, cy, dy, ey);
            return YxyToLinear(Mathf.Max(0f, Y), x, y);
        }

        /// <summary>
        /// Linear-RGB radiance of the twilight sky near and below the horizon, live
        /// only while the sun sits in the three twilight bands (0 to -18 deg). It is
        /// warm on the sun's side - the beam reddened along the grazing air mass, the
        /// one extinction owner - and on the far side shows the Earth's shadow rising
        /// as a dusky wedge with the belt of Venus pink along its top edge. Brightness
        /// dies continuously toward astronomical twilight, so nothing is painted as a
        /// band. Returns near-zero when the sun is high (the day model owns the sky) or
        /// the view is well above the horizon.
        /// </summary>
        public static Color TwilightRadiance(Vector3 viewDir, Vector3 sunDir, float turbidity)
        {
            Vector3 v = viewDir.normalized;
            Vector3 s = sunDir.normalized;

            float sunElevDeg = Mathf.Asin(Mathf.Clamp(s.y, -1f, 1f)) * Mathf.Rad2Deg;
            // One envelope for the whole twilight: full at the horizon, gone by
            // astronomical twilight, cubed so civil twilight carries nearly all of
            // the light and the darkening reads as continuous rather than stepped.
            float band = Mathf.Clamp01(
                (sunElevDeg - LightingConstants.AstronomicalTwilightDeg)
                / -LightingConstants.AstronomicalTwilightDeg);
            if (band <= 0f) return new Color(0f, 0f, 0f, 1f);
            float glow = band * band * band;

            float viewElevDeg = Mathf.Asin(Mathf.Clamp(v.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lowness = Mathf.Clamp01(1f - viewElevDeg / LightingConstants.TwilightGlowTopDeg);

            // Azimuth of the view about the sun: 1 toward it, 0 opposite.
            Vector2 hv = new Vector2(v.x, v.z);
            Vector2 hs = new Vector2(s.x, s.z);
            float lv = hv.magnitude, ls = hs.magnitude;
            float cosAz = (lv > 1e-4f && ls > 1e-4f) ? Vector2.Dot(hv, hs) / (lv * ls) : 1f;
            float sunward = Mathf.Clamp01(0.5f * (cosAz + 1f));

            // The warm side: the sun's own colour reddened along the grazing horizon
            // path, so there is one reddening owner rather than a second fit.
            Color warm = SunColorLinear(
                Mathf.Max(sunElevDeg, LightingConstants.CivilTwilightDeg),
                LightingConstants.TwilightGrazingAirMass, turbidity);

            // The far side: the Earth's shadow top rises with the sun's depression;
            // below it the sky is dusky, above it a cool twilight blue, and a pink
            // belt of Venus straddles the top where it still catches reddened light.
            float shadowTopDeg = Mathf.Max(0f, -sunElevDeg);
            float w = LightingConstants.BeltOfVenusWidthDeg;
            float inShadow = 1f - Mathf.SmoothStep(shadowTopDeg - w, shadowTopDeg, viewElevDeg);
            float belt = Mathf.Clamp01(1f - Mathf.Abs(viewElevDeg - shadowTopDeg) / w);
            Color antisolar = Color.Lerp(LightingConstants.TwilightSkyColor,
                                         LightingConstants.EarthShadowColor, inShadow);
            antisolar = Color.Lerp(antisolar, warm, belt * 0.7f);

            Color hue = Color.Lerp(antisolar, warm, sunward);
            float amp = glow * lowness * LightingConstants.TwilightGlowScale;
            return new Color(hue.r * amp, hue.g * amp, hue.b * amp, 1f);
        }

        /// <summary>
        /// CIE 1931 xy at unit luminance to normalised linear sRGB (D65). Negative
        /// channels outside the gamut are clamped, then the result is scaled so its
        /// brightest channel is 1 - a hue, carrying no brightness of its own.
        /// </summary>
        private static Color ChromaticityToLinear(float x, float y)
        {
            float yInv = 1f / Mathf.Max(y, 1e-4f);
            float bigX = x * yInv;
            float bigZ = (1f - x - y) * yInv;

            float r = 3.2404542f * bigX - 1.5371385f - 0.4985314f * bigZ;
            float g = -0.9692660f * bigX + 1.8760108f + 0.0415560f * bigZ;
            float b = 0.0556434f * bigX - 0.2040259f + 1.0572252f * bigZ;

            r = Mathf.Max(0f, r);
            g = Mathf.Max(0f, g);
            b = Mathf.Max(0f, b);

            float peak = Mathf.Max(r, Mathf.Max(g, b));
            if (peak > 0f) { r /= peak; g /= peak; b /= peak; }
            return new Color(r, g, b, 1f);
        }

        /// <summary>
        /// Absolute luminance of the zenith sky in cd/m^2 (Preetham), rising with
        /// turbidity and falling as the sun sinks. It is the anchor the Perez
        /// distribution scales, so the whole sky carries real photometric units the
        /// exposure model can map rather than an arbitrary 0..1. Sun zenith in degrees.
        /// </summary>
        public static float ZenithLuminance(float sunZenithDeg, float turbidity)
        {
            float T = Mathf.Max(1f, turbidity);
            float ts = Mathf.Clamp(sunZenithDeg, 0f, 90f) * Mathf.Deg2Rad;
            float chi = (4f / 9f - T / 120f) * (Mathf.PI - 2f * ts);
            float yzKilo = (4.0453f * T - 4.9710f) * Mathf.Tan(chi) - 0.2155f * T + 2.4192f;
            return Mathf.Max(0f, yzKilo) * 1000f; // kcd/m^2 -> cd/m^2
        }

        /// <summary>
        /// Zenith CIE-x chromaticity (Preetham polynomial in turbidity and sun zenith,
        /// radians). With ZenithChromaY it fixes the hue the sky fades toward overhead;
        /// the two together turn the luminance model into a coloured sky. </summary>
        private static float ZenithChromaX(float thetaS, float turbidity)
        {
            float T = Mathf.Max(1f, turbidity);
            float s = Mathf.Clamp(thetaS, 0f, Mathf.PI * 0.5f);
            float s2 = s * s, s3 = s2 * s;
            return T * T * (0.00166f * s3 - 0.00375f * s2 + 0.00209f * s)
                 + T * (-0.02903f * s3 + 0.06377f * s2 - 0.03202f * s + 0.00394f)
                 + (0.11693f * s3 - 0.21196f * s2 + 0.06052f * s + 0.25886f);
        }

        /// <summary> Zenith CIE-y chromaticity, the companion polynomial to
        /// ZenithChromaX. </summary>
        private static float ZenithChromaY(float thetaS, float turbidity)
        {
            float T = Mathf.Max(1f, turbidity);
            float s = Mathf.Clamp(thetaS, 0f, Mathf.PI * 0.5f);
            float s2 = s * s, s3 = s2 * s;
            return T * T * (0.00275f * s3 - 0.00610f * s2 + 0.00317f * s)
                 + T * (-0.04214f * s3 + 0.08970f * s2 - 0.04153f * s + 0.00516f)
                 + (0.15346f * s3 - 0.26756f * s2 + 0.06670f * s + 0.26688f);
        }

        // One Perez channel: the zenith value scaled by the distribution at the view
        // direction over the distribution at the zenith, so the returned field is
        // normalised to that zenith anchor. cosThetaV and gamma are the view's; thetaS
        // is the sun's zenith (the gamma at the zenith point).
        private static float SkyChannel(float zenithVal, float cosThetaV, float gamma,
                                        float thetaS, float a, float b, float c, float d, float e)
        {
            float num = Perez(cosThetaV, gamma, a, b, c, d, e);
            float den = Perez(1f, thetaS, a, b, c, d, e);
            return zenithVal * num / Mathf.Max(den, 1e-4f);
        }

        // Perez F(theta,gamma) = (1 + a e^{b/cos theta})(1 + c e^{d gamma} + e cos^2 gamma).
        // The first bracket brightens the horizon, the second the circumsolar sky.
        private static float Perez(float cosTheta, float gamma,
                                   float a, float b, float c, float d, float e)
        {
            float ct = Mathf.Max(cosTheta, 1e-3f);
            float cg = Mathf.Cos(gamma);
            return (1f + a * Mathf.Exp(b / ct)) * (1f + c * Mathf.Exp(d * gamma) + e * cg * cg);
        }

        /// <summary>
        /// CIE Yxy to linear sRGB (D65) keeping luminance - the sky's colour AND its
        /// brightness in one, unlike ChromaticityToLinear which drops brightness to
        /// return a bare hue. Out-of-gamut negatives are clamped; Y stays in cd/m^2.
        /// </summary>
        private static Color YxyToLinear(float Y, float x, float y)
        {
            float yy = Mathf.Max(y, 1e-4f);
            float bigX = (x / yy) * Y;
            float bigZ = ((1f - x - y) / yy) * Y;
            float r = 3.2404542f * bigX - 1.5371385f * Y - 0.4985314f * bigZ;
            float g = -0.9692660f * bigX + 1.8760108f * Y + 0.0415560f * bigZ;
            float b = 0.0556434f * bigX - 0.2040259f * Y + 1.0572252f * bigZ;
            return new Color(Mathf.Max(0f, r), Mathf.Max(0f, g), Mathf.Max(0f, b), 1f);
        }

        /// <summary>
        /// The sky's own ambient as nine linear-RGB L2 spherical-harmonic coefficients,
        /// projected from the Perez sky over the upper hemisphere (the ground adds
        /// nothing here). This is the T0 analytic ambient - the fill light that tracks
        /// the sky's colour with no baked probe - evaluated back with EvaluateSH.
        /// </summary>
        public static Color[] AmbientSH(Vector3 sunDir, float turbidity)
        {
            const int nTheta = 8, nPhi = 16;
            float dTheta = (Mathf.PI * 0.5f) / nTheta;
            float dPhi = (Mathf.PI * 2f) / nPhi;
            var sh = new Color[9];
            for (int it = 0; it < nTheta; it++)
            {
                float theta = (it + 0.5f) * dTheta;
                float st = Mathf.Sin(theta), ct = Mathf.Cos(theta);
                float dw = st * dTheta * dPhi; // solid angle of this sample
                for (int ip = 0; ip < nPhi; ip++)
                {
                    float phi = ip * dPhi;
                    var dir = new Vector3(st * Mathf.Cos(phi), ct, st * Mathf.Sin(phi));
                    Color L = SkyRadiance(dir, sunDir, turbidity);
                    float[] b = ShBasis(dir);
                    for (int i = 0; i < 9; i++)
                    {
                        sh[i].r += L.r * b[i] * dw;
                        sh[i].g += L.g * b[i] * dw;
                        sh[i].b += L.b * b[i] * dw;
                    }
                }
            }
            return sh;
        }

        /// <summary> Reconstruct linear-RGB radiance in a direction from the nine SH
        /// coefficients AmbientSH returns. </summary>
        public static Color EvaluateSH(Color[] sh, Vector3 dir)
        {
            float[] b = ShBasis(dir.normalized);
            Color c = new Color(0f, 0f, 0f, 1f);
            for (int i = 0; i < 9 && i < sh.Length; i++)
            {
                c.r += sh[i].r * b[i];
                c.g += sh[i].g * b[i];
                c.b += sh[i].b * b[i];
            }
            return c;
        }

        // The nine real L2 SH basis values for a unit direction; project and evaluate
        // share this one definition so a round trip is self-consistent.
        private static float[] ShBasis(Vector3 d)
        {
            return new float[]
            {
                0.282095f,
                0.488603f * d.y,
                0.488603f * d.z,
                0.488603f * d.x,
                1.092548f * d.x * d.y,
                1.092548f * d.y * d.z,
                0.315392f * (3f * d.z * d.z - 1f),
                1.092548f * d.x * d.z,
                0.546274f * (d.x * d.x - d.y * d.y)
            };
        }
    }
}
