// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    InterpolationMath.cs
//  Purpose: Scalar interpolation and easing helpers shared by the noise and
//           matrix layers.
//
//  THREAD SAFETY: every member is a pure static function over value types.
//  There is no mutable state of any kind, so this class is safe to call from
//  any number of background generation threads simultaneously.
// -----------------------------------------------------------------------------

namespace UniverseCreator
{
    /// <summary>
    /// Pure interpolation and easing primitives used across the math layer.
    /// </summary>
    public static class InterpolationMath
    {
        // ---------------------------------------------------------------------
        //  Basic
        // ---------------------------------------------------------------------

        /// <summary>Linear blend from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>Where <paramref name="v"/> sits between a and b, as 0-1. Returns 0 for a degenerate range.</summary>
        public static float InverseLerp(float a, float b, float v)
        {
            float d = b - a;
            if (d > -1e-12f && d < 1e-12f) return 0f;
            return Clamp01((v - a) / d);
        }

        /// <summary>Clamps to the 0-1 range without going through UnityEngine.Mathf.</summary>
        public static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        /// <summary>Clamps to an arbitrary range.</summary>
        public static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        /// <summary>Absolute value, inlined to keep tight noise loops free of call overhead.</summary>
        public static float Abs(float v)
        {
            return v < 0f ? -v : v;
        }

        // ---------------------------------------------------------------------
        //  Easing curves
        // ---------------------------------------------------------------------

        /// <summary>
        /// Cubic smoothstep: 3t^2 - 2t^3. First derivative is zero at both ends.
        /// </summary>
        public static float SmoothStep(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        /// <summary>Cubic smoothstep remapped onto an arbitrary edge pair.</summary>
        public static float SmoothStep(float edge0, float edge1, float x)
        {
            return SmoothStep(InverseLerp(edge0, edge1, x));
        }

        /// <summary>
        /// Ken Perlin's quintic fade: 6t^5 - 15t^4 + 10t^3. Both the first and
        /// second derivatives vanish at the endpoints, which is what removes the
        /// grid-aligned creasing that plain cubic smoothstep leaves behind in
        /// gradient noise.
        /// </summary>
        public static float QuinticFade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        /// <summary>Derivative of <see cref="QuinticFade"/>: 30t^4 - 60t^3 + 30t^2.</summary>
        public static float QuinticFadeDerivative(float t)
        {
            return 30f * t * t * (t * (t - 2f) + 1f);
        }

        // ---------------------------------------------------------------------
        //  Bilinear
        // ---------------------------------------------------------------------

        /// <summary>
        /// Bilinear blend of a 2x2 corner set. Corner naming is (x,z), with the
        /// first digit being x and the second z: v00 = (x0,z0), v10 = (x1,z0).
        /// </summary>
        public static float Bilinear(float v00, float v10, float v01, float v11, float tx, float tz)
        {
            float a = v00 + (v10 - v00) * tx;
            float b = v01 + (v11 - v01) * tx;
            return a + (b - a) * tz;
        }

        /// <summary>Bilinear blend using quintic-faded weights, for smoother value noise.</summary>
        public static float BilinearFaded(float v00, float v10, float v01, float v11, float tx, float tz)
        {
            return Bilinear(v00, v10, v01, v11, QuinticFade(tx), QuinticFade(tz));
        }

        // ---------------------------------------------------------------------
        //  Cubic / bicubic
        // ---------------------------------------------------------------------

        /// <summary>
        /// Catmull-Rom spline through p1 and p2, using p0 and p3 as tangent
        /// references. <paramref name="t"/> runs 0-1 across the p1..p2 span.
        /// </summary>
        public static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * ((2f * p1) +
                           (-p0 + p2) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                           (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>
        /// Bicubic blend of a 4x4 neighbourhood. <paramref name="p"/> is indexed
        /// as p[z * 4 + x], so the first four entries are the lowest z row.
        /// </summary>
        public static float Bicubic(float[] p, float tx, float tz)
        {
            float r0 = CatmullRom(p[0], p[1], p[2], p[3], tx);
            float r1 = CatmullRom(p[4], p[5], p[6], p[7], tx);
            float r2 = CatmullRom(p[8], p[9], p[10], p[11], tx);
            float r3 = CatmullRom(p[12], p[13], p[14], p[15], tx);
            return CatmullRom(r0, r1, r2, r3, tz);
        }

        // ---------------------------------------------------------------------
        //  Range shaping
        // ---------------------------------------------------------------------

        /// <summary>
        /// Trapezoidal selection window with smooth shoulders. Returns 1 inside
        /// [min1, max0], ramps up across [min0, min1], ramps down across
        /// [max0, max1], and returns 0 outside [min0, max1]. This is the shared
        /// kernel behind every Selector node.
        /// </summary>
        public static float SelectRange(float v, float min0, float min1, float max0, float max1)
        {
            if (v <= min0 || v >= max1) return 0f;
            if (v >= min1 && v <= max0) return 1f;

            if (v < min1) return SmoothStep(InverseLerp(min0, min1, v));
            return 1f - SmoothStep(InverseLerp(max0, max1, v));
        }
    }
}
