// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PurkinjeShift.cs
//  Purpose: The reason a moonless night reads blue. At scotopic light the rods carry
//           vision and the cones fall silent, so the eye loses its red sensitivity and
//           its luminous peak slides toward blue-green (~507 nm). That is a fact about
//           the observer, not about the light - the moon is neutral grey - so the shift
//           belongs to the eye model applied to the final image, after tonemapping, and
//           never to the moonlight's colour. This is pure math: a scotopic weight from
//           the scene's key illuminance (0 in daylight, rising as the night deepens) and
//           a desaturating cool cast scaled by an authored, deliberately subtle strength.
//           It fades to nothing as dawn brightens the scene back into cone vision.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The Purkinje (scotopic) shift: how strongly rod vision governs at a given light
    /// level, and the desaturating blue-green cast that follows, applied to a
    /// post-tonemap image colour. Keyed to the scene's key illuminance so it tracks the
    /// same photometry the exposure does, and it is off wherever the cones still see.
    /// </summary>
    public static class PurkinjeShift
    {
        // Cone vision holds above a few lux; the rods have it to themselves well below
        // one. Between the two the mesopic range blends, which is exactly this ramp.
        private const float PhotopicLux = 3.0f;
        private const float ScotopicLux = 0.01f;

        // A deliberately subtle default: honest perception, not a blue filter over the
        // whole night. The live value is an authored knob on the render binding (S-18).
        public const float DefaultStrength = 0.5f;

        // The rod-vision cast: cool and blue-green, normalised so its Rec.709 luminance is
        // 1, so applying it desaturates and tints without darkening the image on its own.
        private static readonly Vector3 ScotopicTintLinear = new Vector3(0.748f, 1.037f, 1.375f);

        /// <summary>
        /// How far into rod vision the eye is at this key illuminance, 0..1: 0 in daylight
        /// and at twilight (cones still lead), rising through the mesopic range and reaching
        /// 1 in the deep scotopic dark of a moonless, starlit night. Smooth in log-luminance,
        /// so there is no seam as the night falls.
        /// </summary>
        public static float ScotopicWeight01(float keyIlluminanceLux)
        {
            float logL = Mathf.Log10(Mathf.Max(keyIlluminanceLux, 1e-6f));
            float logHi = Mathf.Log10(PhotopicLux);
            float logLo = Mathf.Log10(ScotopicLux);
            float t = Mathf.InverseLerp(logHi, logLo, logL);   // 0 photopic, 1 scotopic
            return Mathf.SmoothStep(0f, 1f, t);
        }

        /// <summary>
        /// Apply the shift to one linear, post-tonemap colour. The colour is pulled toward
        /// a rod-monochrome grey (rods carry no hue) tinted cool blue-green, by
        /// <paramref name="weight"/> (from <see cref="ScotopicWeight01"/>) times the authored
        /// <paramref name="strength"/>. Weight or strength zero returns the colour untouched -
        /// so daylight, dawn and a strength of 0 all leave the image exactly as tonemapped.
        /// </summary>
        public static Color Apply(Color linearColor, float weight, float strength)
        {
            float amt = Mathf.Clamp01(weight) * Mathf.Max(0f, strength);
            if (amt <= 0f) return linearColor;

            float grey = 0.2126f * linearColor.r + 0.7152f * linearColor.g + 0.0722f * linearColor.b;
            Vector3 t = ScotopicTintLinear;
            return new Color(
                Mathf.Lerp(linearColor.r, grey * t.x, amt),
                Mathf.Lerp(linearColor.g, grey * t.y, amt),
                Mathf.Lerp(linearColor.b, grey * t.z, amt),
                linearColor.a);
        }
    }
}
