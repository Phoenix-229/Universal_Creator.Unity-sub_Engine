// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    MatrixOperations.cs
//  Purpose: Stateless operations over Matrix: blur, downsample, curve, levels,
//           blend and analysis.
//
//  DESIGN NOTE: the eleven blend algorithms live here rather than inside
//  BlendNode. Node classes are meant to be thin parameter carriers that call
//  into the math layer, and several nodes besides Blend need these.
//
//  THREAD SAFETY: every method is a pure static function that reads its inputs
//  and writes only to matrices it allocated or was handed. No shared state.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// Per-pixel combination algorithms exposed by BlendNode.
    /// </summary>
    /// <remarks>
    /// Values are explicit and APPEND-ONLY. The enum is serialized as an int in
    /// every saved graph, so renumbering an existing entry would silently change
    /// what a user's saved node does.
    ///
    /// 0-10 are the original set. 11+ are the extended operator set added in
    /// Module 2; several are the managed-C# equivalents of blend types the
    /// reference PCG toolkit exposes per attribute channel.
    /// </remarks>
    public enum BlendMode
    {
        Add = 0,
        Subtract = 1,
        Multiply = 2,
        Divide = 3,
        Difference = 4,
        Min = 5,
        Max = 6,
        Overlay = 7,
        HardLight = 8,
        SoftLight = 9,
        Mix = 10,

        // --- Module 2 extended operators ---------------------------------

        /// <summary>a + b * weight. Add with a scalable contribution from b.</summary>
        WeightedSum = 11,

        /// <summary>Linear interpolation from a to b by weight.</summary>
        Lerp = 12,

        /// <summary>Smaller of the two magnitudes, returned unsigned.</summary>
        UnsignedMin = 13,

        /// <summary>Larger of the two magnitudes, returned unsigned.</summary>
        UnsignedMax = 14,

        /// <summary>Operand with the smaller magnitude, keeping its own sign.</summary>
        AbsoluteMin = 15,

        /// <summary>Operand with the larger magnitude, keeping its own sign.</summary>
        AbsoluteMax = 16,

        /// <summary>a - b * weight. Subtract with a scalable bite.</summary>
        WeightedSubtract = 17,

        /// <summary>Discards a and returns b. Useful as a masked stamp.</summary>
        CopyOther = 18,

        /// <summary>
        /// Deterministic 0-1 hash of the operand pair. Not a visual blend -
        /// it turns two fields into stable per-pixel randomness for scatter
        /// variation and rock-strata banding.
        /// </summary>
        Hash = 19
    }

    /// <summary>
    /// Stateless heightfield operations shared across the node library.
    /// </summary>
    public static class MatrixOperations
    {
        // =====================================================================
        //  BLUR
        // =====================================================================

        /// <summary>
        /// Separable Gaussian blur. Runs a horizontal pass then a vertical pass,
        /// which is O(n*r) instead of the O(n*r^2) a 2D kernel would cost.
        /// Edges are clamped so borders do not darken.
        /// </summary>
        /// <param name="src">Source field. Not modified.</param>
        /// <param name="sigma">Standard deviation in pixels. Values below ~0.05 are a no-op.</param>
        /// <param name="stop">UC-T07-04: optional cooperative cancellation, checked before allocation and each pass row.</param>
        /// <returns>A new blurred matrix with the source's world placement, or null when cancelled.</returns>
        public static Matrix GaussianBlur(Matrix src, float sigma, Nodes.StopToken stop = null)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (stop != null && stop.StopRequested) return null;
            if (sigma < 0.05f) return src.Copy();

            float[] kernel = BuildGaussianKernel(sigma);
            int radius = kernel.Length - 1;

            int sizeX = src.rect.size.x;
            int sizeZ = src.rect.size.z;

            Matrix temp = src.Blank();
            Matrix dst = src.Blank();

            // Horizontal pass.
            for (int z = 0; z < sizeZ; z++)
            {
                if (stop != null && stop.StopRequested) return null; // UC-T07-04: obsolete membership/preview work retires between rows.
                int rowStart = z * sizeX;
                for (int x = 0; x < sizeX; x++)
                {
                    float sum = src.arr[rowStart + x] * kernel[0];
                    float weight = kernel[0];

                    for (int k = 1; k <= radius; k++)
                    {
                        int left = x - k; if (left < 0) left = 0;
                        int right = x + k; if (right >= sizeX) right = sizeX - 1;

                        sum += (src.arr[rowStart + left] + src.arr[rowStart + right]) * kernel[k];
                        weight += kernel[k] * 2f;
                    }

                    temp.arr[rowStart + x] = sum / weight;
                }
            }

            // Vertical pass.
            for (int z = 0; z < sizeZ; z++)
            {
                if (stop != null && stop.StopRequested) return null; // UC-T07-04: do not publish a partly filtered plane.
                for (int x = 0; x < sizeX; x++)
                {
                    float sum = temp.arr[z * sizeX + x] * kernel[0];
                    float weight = kernel[0];

                    for (int k = 1; k <= radius; k++)
                    {
                        int down = z - k; if (down < 0) down = 0;
                        int up = z + k; if (up >= sizeZ) up = sizeZ - 1;

                        sum += (temp.arr[down * sizeX + x] + temp.arr[up * sizeX + x]) * kernel[k];
                        weight += kernel[k] * 2f;
                    }

                    dst.arr[z * sizeX + x] = sum / weight;
                }
            }

            return dst;
        }

        /// <summary>
        /// Half-kernel of Gaussian weights, index 0 being the centre tap.
        /// Truncated at 3 sigma, where the remaining tail is under 0.3%.
        /// </summary>
        private static float[] BuildGaussianKernel(float sigma)
        {
            int radius = (int)System.Math.Ceiling(sigma * 3f);
            if (radius < 1) radius = 1;

            float[] kernel = new float[radius + 1];
            float twoSigmaSq = 2f * sigma * sigma;

            for (int i = 0; i <= radius; i++)
                kernel[i] = (float)System.Math.Exp(-(i * i) / twoSigmaSq);

            return kernel;
        }

        /// <summary>
        /// Combined downsample and blur. Box-averaging each factor x factor
        /// block is both the decimation and the anti-alias prefilter, so this is
        /// far cheaper than blurring at full resolution then sampling.
        /// Used to build the low-poly LOD ring.
        /// </summary>
        /// <param name="src">Source field.</param>
        /// <param name="factor">Integer reduction factor. 4 gives the quarter-res LOD.</param>
        public static Matrix DownsampleBlur(Matrix src, int factor)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (factor < 2) return src.Copy();

            int srcX = src.rect.size.x;
            int srcZ = src.rect.size.z;

            int dstX = srcX / factor; if (dstX < 1) dstX = 1;
            int dstZ = srcZ / factor; if (dstZ < 1) dstZ = 1;

            Matrix dst = new Matrix(new CoordRect(src.rect.offset.x, src.rect.offset.z, dstX, dstZ));
            dst.CopyWorldPlacement(src);

            for (int z = 0; z < dstZ; z++)
            {
                for (int x = 0; x < dstX; x++)
                {
                    float sum = 0f;
                    int samples = 0;

                    for (int bz = 0; bz < factor; bz++)
                    {
                        int sz = z * factor + bz;
                        if (sz >= srcZ) break;

                        for (int bx = 0; bx < factor; bx++)
                        {
                            int sx = x * factor + bx;
                            if (sx >= srcX) break;

                            sum += src.arr[sz * srcX + sx];
                            samples++;
                        }
                    }

                    dst.arr[z * dstX + x] = samples > 0 ? sum / samples : 0f;
                }
            }

            return dst;
        }

        // =====================================================================
        //  ANALYSIS
        // =====================================================================

        /// <summary>
        /// Largest absolute height difference to any of the eight neighbours.
        /// A cheap roughness proxy; the Slope selector uses true angles instead.
        /// </summary>
        public static Matrix Delta(Matrix src)
        {
            if (src == null) throw new ArgumentNullException("src");

            int sizeX = src.rect.size.x;
            int sizeZ = src.rect.size.z;
            int ox = src.rect.offset.x;
            int oz = src.rect.offset.z;

            Matrix dst = src.Blank();

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float centre = src.arr[z * sizeX + x];
                    float maxDelta = 0f;

                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;

                            float n = src.GetClamped(ox + x + dx, oz + z + dz);
                            float d = n - centre;
                            if (d < 0) d = -d;
                            if (d > maxDelta) maxDelta = d;
                        }
                    }

                    dst.arr[z * sizeX + x] = maxDelta;
                }
            }

            return dst;
        }

        /// <summary>
        /// Discrete Laplacian: mean of the eight neighbours minus the centre.
        /// Positive in concave pits and gullies, negative on convex ridges and
        /// crests. This is what the Cavity selector keys off.
        /// </summary>
        /// <param name="normalise">
        /// Remap the signed result into 0-1 with 0.5 meaning flat. Leave false to
        /// keep the raw signed curvature.
        /// </param>
        public static Matrix Cavity(Matrix src, bool normalise = true)
        {
            if (src == null) throw new ArgumentNullException("src");

            int sizeX = src.rect.size.x;
            int sizeZ = src.rect.size.z;
            int ox = src.rect.offset.x;
            int oz = src.rect.offset.z;

            Matrix dst = src.Blank();

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float centre = src.arr[z * sizeX + x];
                    float sum = 0f;

                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            sum += src.GetClamped(ox + x + dx, oz + z + dz);
                        }

                    float laplacian = (sum / 8f) - centre;
                    dst.arr[z * sizeX + x] = laplacian;
                }
            }

            if (normalise)
            {
                // Scale by the observed extreme rather than a fixed constant, so
                // the mask stays useful whether the terrain is alpine or gentle.
                float peak = 0f;
                for (int i = 0; i < dst.arr.Length; i++)
                {
                    float a = dst.arr[i] < 0 ? -dst.arr[i] : dst.arr[i];
                    if (a > peak) peak = a;
                }

                if (peak > 1e-9f)
                    for (int i = 0; i < dst.arr.Length; i++)
                        dst.arr[i] = dst.arr[i] / (peak * 2f) + 0.5f;
                else
                    dst.Fill(0.5f);
            }

            return dst;
        }

        /// <summary>
        /// Per-pixel surface normal via a 3x3 Sobel operator, in world space.
        /// Sobel is preferred over a two-tap central difference because it
        /// averages across the perpendicular axis, which suppresses the
        /// single-pixel noise erosion tends to leave behind.
        /// </summary>
        public static Vector3[] SobelNormals(Matrix src)
        {
            if (src == null) throw new ArgumentNullException("src");

            int sizeX = src.rect.size.x;
            int sizeZ = src.rect.size.z;
            int ox = src.rect.offset.x;
            int oz = src.rect.offset.z;

            Vector2 ps = src.PixelSize;
            float scaleX = ps.x > 1e-9f ? src.worldHeight / (8f * ps.x) : 0f;
            float scaleZ = ps.y > 1e-9f ? src.worldHeight / (8f * ps.y) : 0f;

            Vector3[] normals = new Vector3[src.arr.Length];

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int wx = ox + x;
                    int wz = oz + z;

                    float tl = src.GetClamped(wx - 1, wz + 1);
                    float t = src.GetClamped(wx, wz + 1);
                    float tr = src.GetClamped(wx + 1, wz + 1);
                    float l = src.GetClamped(wx - 1, wz);
                    float r = src.GetClamped(wx + 1, wz);
                    float bl = src.GetClamped(wx - 1, wz - 1);
                    float b = src.GetClamped(wx, wz - 1);
                    float br = src.GetClamped(wx + 1, wz - 1);

                    float gx = (tr + 2f * r + br) - (tl + 2f * l + bl);
                    float gz = (tl + 2f * t + tr) - (bl + 2f * b + br);

                    Vector3 n = new Vector3(-gx * scaleX, 1f, -gz * scaleZ);
                    normals[z * sizeX + x] = n.normalized;
                }
            }

            return normals;
        }

        /// <summary>
        /// Slope angle in degrees at every pixel, from the Sobel normal.
        /// 0 is flat ground, 90 is a vertical wall.
        /// </summary>
        public static Matrix SlopeAngles(Matrix src)
        {
            Vector3[] normals = SobelNormals(src);
            Matrix dst = src.Blank();

            const float RadToDeg = 57.29577951308232f;

            for (int i = 0; i < normals.Length; i++)
            {
                float up = normals[i].y;
                if (up > 1f) up = 1f;
                if (up < -1f) up = -1f;
                dst.arr[i] = (float)System.Math.Acos(up) * RadToDeg;
            }

            return dst;
        }

        /// <summary>
        /// Compass facing of the surface normal in degrees, 0-360, measured
        /// clockwise from north (+Z). Flat ground reports 0.
        /// </summary>
        public static Matrix AspectAngles(Matrix src)
        {
            Vector3[] normals = SobelNormals(src);
            Matrix dst = src.Blank();

            const float RadToDeg = 57.29577951308232f;

            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 n = normals[i];
                float angle = (float)System.Math.Atan2(n.x, n.z) * RadToDeg;
                if (angle < 0f) angle += 360f;
                dst.arr[i] = angle;
            }

            return dst;
        }

        /// <summary>Bucketed value distribution, for the inline preview histogram.</summary>
        public static int[] Histogram(Matrix src, int buckets = 256, float min = 0f, float max = 1f)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (buckets < 1) buckets = 1;

            int[] result = new int[buckets];
            float range = max - min;
            if (range < 1e-9f) return result;

            for (int i = 0; i < src.arr.Length; i++)
            {
                int b = (int)((src.arr[i] - min) / range * buckets);
                if (b < 0) b = 0;
                if (b >= buckets) b = buckets - 1;
                result[b]++;
            }

            return result;
        }

        // =====================================================================
        //  VALUE SHAPING
        // =====================================================================

        /// <summary>
        /// Trapezoidal range selection into a 0-1 mask. Values inside
        /// [min1, max0] map to 1, with smooth shoulders out to min0 and max1.
        /// </summary>
        public static Matrix SelectRange(Matrix src, float min0, float min1, float max0, float max1)
        {
            if (src == null) throw new ArgumentNullException("src");

            Matrix dst = src.Blank();
            for (int i = 0; i < src.arr.Length; i++)
                dst.arr[i] = InterpolationMath.SelectRange(src.arr[i], min0, min1, max0, max1);

            return dst;
        }

        /// <summary>
        /// Photoshop-style levels. Remaps [inMin, inMax] onto [outMin, outMax]
        /// with a gamma curve applied in between.
        /// </summary>
        public static void Levels(Matrix m, float inMin, float inMax, float gamma,
                                  float outMin, float outMax)
        {
            if (m == null) throw new ArgumentNullException("m");

            float inRange = inMax - inMin;
            if (inRange > -1e-9f && inRange < 1e-9f) inRange = 1e-9f;

            bool applyGamma = gamma > 0f && System.Math.Abs(gamma - 1f) > 1e-6f;
            float invGamma = applyGamma ? 1f / gamma : 1f;

            for (int i = 0; i < m.arr.Length; i++)
            {
                float v = (m.arr[i] - inMin) / inRange;
                v = InterpolationMath.Clamp01(v);

                if (applyGamma && v > 0f)
                    v = (float)System.Math.Pow(v, invGamma);

                m.arr[i] = outMin + v * (outMax - outMin);
            }
        }

        /// <summary>
        /// Brightness and contrast about a 0.5 pivot. Contrast is a multiplier,
        /// so 1 leaves the field untouched.
        /// </summary>
        public static void BrightnessContrast(Matrix m, float brightness, float contrast)
        {
            if (m == null) throw new ArgumentNullException("m");

            for (int i = 0; i < m.arr.Length; i++)
                m.arr[i] = (m.arr[i] - 0.5f) * contrast + 0.5f + brightness;
        }

        /// <summary>
        /// Snaps heights toward N discrete levels, producing stratified rock
        /// benches. Steepness 0 leaves the field unchanged; 1 gives hard steps.
        /// </summary>
        public static void Terrace(Matrix m, int levels, float steepness)
        {
            if (m == null) throw new ArgumentNullException("m");
            if (levels < 2) return;

            steepness = InterpolationMath.Clamp01(steepness);
            if (steepness < 1e-4f) return;

            float steps = levels - 1;

            // Sharpening exponent: 1 at steepness 0, rising steeply toward the
            // hard-step limit. Capped so pow() stays numerically sane.
            float k = 1f + steepness * steepness * 32f;

            for (int i = 0; i < m.arr.Length; i++)
            {
                float v = InterpolationMath.Clamp01(m.arr[i]);

                float scaled = v * steps;
                int band = (int)scaled;
                if (band >= steps) band = (int)steps - 1;
                float f = scaled - band;

                // Push f toward 0 or 1 symmetrically about the band midpoint.
                float sharp;
                if (f < 0.5f)
                    sharp = 0.5f * (float)System.Math.Pow(f * 2f, k);
                else
                    sharp = 1f - 0.5f * (float)System.Math.Pow((1f - f) * 2f, k);

                float terraced = (band + sharp) / steps;
                m.arr[i] = v + (terraced - v) * steepness;
            }
        }

        /// <summary>
        /// Applies a lookup table to every value. The LUT is sampled with linear
        /// interpolation, so a coarse table still yields a smooth remap.
        /// </summary>
        /// <param name="lut">Output values for inputs evenly spaced across 0-1.</param>
        public static void UniformCurve(Matrix m, float[] lut)
        {
            if (m == null) throw new ArgumentNullException("m");
            if (lut == null || lut.Length == 0) return;

            if (lut.Length == 1)
            {
                m.Fill(lut[0]);
                return;
            }

            int last = lut.Length - 1;

            for (int i = 0; i < m.arr.Length; i++)
            {
                float v = InterpolationMath.Clamp01(m.arr[i]);

                float pos = v * last;
                int idx = (int)pos;
                if (idx >= last) { m.arr[i] = lut[last]; continue; }

                float f = pos - idx;
                m.arr[i] = lut[idx] + (lut[idx + 1] - lut[idx]) * f;
            }
        }

        /// <summary>Raises every value to an exponent, clamping the base to 0.</summary>
        public static void Power(Matrix m, float exponent)
        {
            if (m == null) throw new ArgumentNullException("m");

            for (int i = 0; i < m.arr.Length; i++)
            {
                float v = m.arr[i];
                m.arr[i] = v <= 0f ? 0f : (float)System.Math.Pow(v, exponent);
            }
        }

        /// <summary>
        /// Rescales the field so its actual range fills [newMin, newMax].
        /// A flat field is left at newMin rather than dividing by zero.
        /// </summary>
        public static void Normalize(Matrix m, float newMin = 0f, float newMax = 1f)
        {
            if (m == null) throw new ArgumentNullException("m");
            if (m.arr.Length == 0) return;

            float min = m.Min();
            float max = m.Max();
            float range = max - min;

            if (range < 1e-9f)
            {
                m.Fill(newMin);
                return;
            }

            float scale = (newMax - newMin) / range;
            for (int i = 0; i < m.arr.Length; i++)
                m.arr[i] = (m.arr[i] - min) * scale + newMin;
        }

        // =====================================================================
        //  COMBINATION
        // =====================================================================

        /// <summary>
        /// Combines two fields with one of the blend algorithms.
        /// </summary>
        /// <param name="a">Base field.</param>
        /// <param name="b">Blend field.</param>
        /// <param name="mode">Algorithm.</param>
        /// <param name="opacity">How far to move from a toward the blended result.</param>
        /// <param name="weight">
        /// Second parameter for the weighted operators (WeightedSum, Lerp,
        /// WeightedSubtract). Ignored by every other mode.
        /// </param>
        public static Matrix Blend(Matrix a, Matrix b, BlendMode mode, float opacity = 1f,
                                   float weight = 0.5f)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            if (a.arr.Length != b.arr.Length)
                throw new ArgumentException("Blend: matrices differ in size.");

            Matrix dst = a.Blank();

            for (int i = 0; i < a.arr.Length; i++)
            {
                float va = a.arr[i];
                float vb = b.arr[i];
                float rawBlend = va + (BlendValue(va, vb, mode, weight) - va) * opacity;
                dst.arr[i] = Mathf.Clamp01(rawBlend);
            }

            return dst;
        }

        /// <summary>Single-pixel form of the blend algorithms.</summary>
        /// <param name="weight">
        /// Second parameter for the weighted operators. Defaults to 0.5 so the
        /// three-argument form still reads as an even mix.
        /// </param>
        public static float BlendValue(float a, float b, BlendMode mode, float weight = 0.5f)
        {
            switch (mode)
            {
                case BlendMode.Add:
                    return a + b;

                case BlendMode.Subtract:
                    return a - b;

                case BlendMode.Multiply:
                    return a * b;

                case BlendMode.Divide:
                    // Guarded: an unguarded divide is the most common source of
                    // NaN leaking into TerrainData and blanking a whole chunk.
                    return b > 1e-6f || b < -1e-6f ? a / b : 0f;

                case BlendMode.Difference:
                    {
                        float d = a - b;
                        return d < 0 ? -d : d;
                    }

                case BlendMode.Min:
                    return a < b ? a : b;

                case BlendMode.Max:
                    return a > b ? a : b;

                case BlendMode.Overlay:
                    return a < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b);

                case BlendMode.HardLight:
                    // Overlay with the operands swapped.
                    return b < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b);

                case BlendMode.SoftLight:
                    // W3C soft-light formulation.
                    if (b <= 0.5f)
                        return a - (1f - 2f * b) * a * (1f - a);
                    else
                    {
                        float d = a <= 0.25f
                            ? ((16f * a - 12f) * a + 4f) * a
                            : (float)System.Math.Sqrt(a < 0f ? 0f : a);
                        return a + (2f * b - 1f) * (d - a);
                    }

                case BlendMode.Mix:
                    return (a + b) * 0.5f;

                // --- Module 2 extended operators -------------------------

                case BlendMode.WeightedSum:
                    return a + b * weight;

                case BlendMode.Lerp:
                    return a + (b - a) * weight;

                case BlendMode.UnsignedMin:
                    {
                        float ua = a < 0f ? -a : a;
                        float ub = b < 0f ? -b : b;
                        return ua < ub ? ua : ub;
                    }

                case BlendMode.UnsignedMax:
                    {
                        float ua = a < 0f ? -a : a;
                        float ub = b < 0f ? -b : b;
                        return ua > ub ? ua : ub;
                    }

                case BlendMode.AbsoluteMin:
                    {
                        // Compares magnitude but returns the operand itself, so a
                        // signed field keeps its direction.
                        float ua = a < 0f ? -a : a;
                        float ub = b < 0f ? -b : b;
                        return ua < ub ? a : b;
                    }

                case BlendMode.AbsoluteMax:
                    {
                        float ua = a < 0f ? -a : a;
                        float ub = b < 0f ? -b : b;
                        return ua > ub ? a : b;
                    }

                case BlendMode.WeightedSubtract:
                    return a - b * weight;

                case BlendMode.CopyOther:
                    return b;

                case BlendMode.Hash:
                    return HashPair(a, b);

                default:
                    return a;
            }
        }

        /// <summary>
        /// Deterministic 0-1 hash of two floats, for the Hash blend mode.
        /// </summary>
        /// <remarks>
        /// Quantising to 1/4096 before hashing is deliberate: raw float bits
        /// would make the result flicker on the last mantissa bit, so two
        /// regenerations of the same terrain would disagree wherever a value
        /// landed a rounding step apart.
        /// </remarks>
        public static float HashPair(float a, float b)
        {
            const int Quant = 4096;

            int ia = (int)(a * Quant);
            int ib = (int)(b * Quant);

            // Integer avalanche (Wang-style). Chosen over a permutation-table
            // lookup so this stays allocation-free and needs no seeded instance.
            uint h = (uint)(ia * 73856093) ^ (uint)(ib * 19349663);
            h ^= h >> 16;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;

            return (h & 0xFFFFFF) / 16777215f;
        }

        /// <summary>
        /// Per-pixel mix of two fields steered by a third. Where the mask is 0
        /// the result is <paramref name="a"/>, where it is 1 the result is
        /// <paramref name="b"/>.
        /// </summary>
        public static Matrix MaskMix(Matrix a, Matrix b, Matrix mask)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            if (mask == null) throw new ArgumentNullException("mask");
            if (a.arr.Length != b.arr.Length || a.arr.Length != mask.arr.Length)
                throw new ArgumentException("MaskMix: matrices differ in size.");

            Matrix dst = a.Blank();
            for (int i = 0; i < a.arr.Length; i++)
            {
                float t = InterpolationMath.Clamp01(mask.arr[i]);
                dst.arr[i] = a.arr[i] + (b.arr[i] - a.arr[i]) * t;
            }

            return dst;
        }

        /// <summary>
        /// Normalises N layers so every pixel's weights sum to 1. This is the
        /// invariant Unity's splatmaps require; a pixel whose layers all read
        /// zero is assigned entirely to layer 0 rather than left undefined.
        /// </summary>
        public static void BlendLayers(Matrix[] layers)
        {
            if (layers == null || layers.Length == 0) return;

            int count = layers[0].arr.Length;
            for (int l = 1; l < layers.Length; l++)
                if (layers[l].arr.Length != count)
                    throw new ArgumentException("BlendLayers: layers differ in size.");

            for (int i = 0; i < count; i++)
            {
                float sum = 0f;
                for (int l = 0; l < layers.Length; l++)
                {
                    float v = layers[l].arr[i];
                    if (v < 0f) { v = 0f; layers[l].arr[i] = 0f; }
                    sum += v;
                }

                if (sum < 1e-9f)
                {
                    layers[0].arr[i] = 1f;
                    for (int l = 1; l < layers.Length; l++) layers[l].arr[i] = 0f;
                }
                else
                {
                    float inv = 1f / sum;
                    for (int l = 0; l < layers.Length; l++) layers[l].arr[i] *= inv;
                }
            }
        }

        /// <summary>Weighted average of N fields. Weights are normalised internally.</summary>
        public static Matrix WeightedAverage(Matrix[] inputs, float[] weights)
        {
            if (inputs == null || inputs.Length == 0)
                throw new ArgumentException("WeightedAverage: no inputs.");

            int count = inputs[0].arr.Length;
            Matrix dst = inputs[0].Blank();

            float weightSum = 0f;
            for (int l = 0; l < inputs.Length; l++)
            {
                float w = weights != null && l < weights.Length ? weights[l] : 1f;
                if (w < 0f) w = 0f;
                weightSum += w;
            }

            if (weightSum < 1e-9f) return dst;

            for (int l = 0; l < inputs.Length; l++)
            {
                if (inputs[l].arr.Length != count)
                    throw new ArgumentException("WeightedAverage: inputs differ in size.");

                float w = weights != null && l < weights.Length ? weights[l] : 1f;
                if (w < 0f) w = 0f;
                if (w < 1e-9f) continue;

                float nw = w / weightSum;
                for (int i = 0; i < count; i++)
                    dst.arr[i] += inputs[l].arr[i] * nw;
            }

            return dst;
        }

        // =====================================================================
        //  DISTANCE FIELD
        // =====================================================================

        /// <summary>
        /// Linear spread from source cells, i.e. a distance field. Cells at or
        /// above <paramref name="threshold"/> are sources and stay at 1; every
        /// other cell falls off linearly to 0 at <paramref name="maxDistance"/>
        /// pixels away.
        ///
        /// Implemented as a two-pass chamfer transform: one forward sweep and
        /// one backward sweep, each visiting four already-settled neighbours.
        /// That is O(n) versus the O(n * maxDistance^2) a brute-force search
        /// would cost, and the 1 / sqrt(2) diagonal weights keep the error under
        /// about 4% of true Euclidean distance.
        /// </summary>
        public static Matrix SpreadLinear(Matrix src, float threshold = 0.5f, float maxDistance = 16f)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (maxDistance < 1e-6f) return src.Copy();

            int sizeX = src.rect.size.x;
            int sizeZ = src.rect.size.z;

            const float Straight = 1f;
            const float Diagonal = 1.41421356f;

            float far = maxDistance + Diagonal;
            float[] dist = new float[src.arr.Length];

            for (int i = 0; i < dist.Length; i++)
                dist[i] = src.arr[i] >= threshold ? 0f : far;

            // Forward sweep: low z to high z, low x to high x.
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int i = z * sizeX + x;
                    float d = dist[i];

                    if (x > 0) d = Min(d, dist[i - 1] + Straight);
                    if (z > 0) d = Min(d, dist[i - sizeX] + Straight);
                    if (x > 0 && z > 0) d = Min(d, dist[i - sizeX - 1] + Diagonal);
                    if (x < sizeX - 1 && z > 0) d = Min(d, dist[i - sizeX + 1] + Diagonal);

                    dist[i] = d;
                }
            }

            // Backward sweep: high z to low z, high x to low x.
            for (int z = sizeZ - 1; z >= 0; z--)
            {
                for (int x = sizeX - 1; x >= 0; x--)
                {
                    int i = z * sizeX + x;
                    float d = dist[i];

                    if (x < sizeX - 1) d = Min(d, dist[i + 1] + Straight);
                    if (z < sizeZ - 1) d = Min(d, dist[i + sizeX] + Straight);
                    if (x < sizeX - 1 && z < sizeZ - 1) d = Min(d, dist[i + sizeX + 1] + Diagonal);
                    if (x > 0 && z < sizeZ - 1) d = Min(d, dist[i + sizeX - 1] + Diagonal);

                    dist[i] = d;
                }
            }

            Matrix dst = src.Blank();
            for (int i = 0; i < dist.Length; i++)
                dst.arr[i] = InterpolationMath.Clamp01(1f - dist[i] / maxDistance);

            return dst;
        }

        private static float Min(float a, float b) { return a < b ? a : b; }

        // =====================================================================
        //  MORPHOLOGY
        // =====================================================================

        /// <summary>
        /// Highest value within <paramref name="radiusPx"/> pixels on each axis,
        /// i.e. a grey-scale dilation over a square window. Across a valley this
        /// returns the crest of its banks, which is what a fill level is measured
        /// against.
        /// </summary>
        /// <param name="src">Source field, row-major, sizeX * sizeZ. Not modified.</param>
        /// <param name="radiusPx">Half the window side. Below 1 the source is returned unchanged.</param>
        public static float[] WindowMax(float[] src, int sizeX, int sizeZ, int radiusPx)
        {
            return WindowExtreme(src, sizeX, sizeZ, radiusPx, true);
        }

        /// <summary>
        /// Lowest value within <paramref name="radiusPx"/> pixels on each axis,
        /// i.e. a grey-scale erosion. Across a valley this returns the thalweg -
        /// the deepest line of the channel - flattened across its width.
        /// </summary>
        public static float[] WindowMin(float[] src, int sizeX, int sizeZ, int radiusPx)
        {
            return WindowExtreme(src, sizeX, sizeZ, radiusPx, false);
        }

        /// <summary>
        /// Separable sliding-window extreme. Runs a horizontal pass then a
        /// vertical one, which gives the square-window result at twice the cost of
        /// a single line pass rather than the window's area.
        /// </summary>
        private static float[] WindowExtreme(float[] src, int sizeX, int sizeZ,
                                             int radiusPx, bool takeMax)
        {
            if (src == null) throw new ArgumentNullException("src");

            int count = sizeX * sizeZ;
            float[] dst = new float[count];
            if (count == 0) return dst;

            Array.Copy(src, dst, count);
            if (radiusPx < 1) return dst;

            int longest = sizeX > sizeZ ? sizeX : sizeZ;
            float[] line = new float[longest];
            float[] result = new float[longest];

            // Holds indices into `line`, never more than one per element, so one
            // allocation of the longest axis serves both passes.
            int[] window = new int[longest];

            for (int z = 0; z < sizeZ; z++)
            {
                int row = z * sizeX;
                Array.Copy(dst, row, line, 0, sizeX);
                RunExtreme(line, result, window, sizeX, radiusPx, takeMax);
                Array.Copy(result, 0, dst, row, sizeX);
            }

            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++) line[z] = dst[z * sizeX + x];
                RunExtreme(line, result, window, sizeZ, radiusPx, takeMax);
                for (int z = 0; z < sizeZ; z++) dst[z * sizeX + x] = result[z];
            }

            return dst;
        }

        /// <summary>
        /// One line of the sliding extreme, edges clamped to the shortened window
        /// so borders are not pulled toward zero.
        /// </summary>
        /// <remarks>
        /// Lemire's streaming maximum-minimum filter (2006). `window` keeps only
        /// the indices that can still win, in decreasing order of value for a max:
        /// any earlier index whose value a newcomer beats can never win again, so
        /// it is dropped on arrival. Each element is therefore pushed and popped at
        /// most once and the whole line costs O(n) no matter how wide the window -
        /// the naive rescan would be O(n * radius).
        /// </remarks>
        private static void RunExtreme(float[] line, float[] result, int[] window,
                                       int length, int radius, bool takeMax)
        {
            int head = 0;   // oldest index still in contention
            int tail = 0;   // one past the newest
            int next = 0;   // next input index to admit

            for (int i = 0; i < length; i++)
            {
                int leading = i + radius;
                if (leading > length - 1) leading = length - 1;

                while (next <= leading)
                {
                    float v = line[next];
                    while (tail > head && (takeMax ? line[window[tail - 1]] <= v
                                                   : line[window[tail - 1]] >= v))
                        tail--;

                    window[tail++] = next;
                    next++;
                }

                // Retire indices that have fallen off the trailing edge. The guard
                // keeps one entry alive because one always qualifies: index i is
                // inside its own window and was admitted, and anything that evicted
                // it was itself no further ahead than the leading edge.
                int trailing = i - radius;
                while (head + 1 < tail && window[head] < trailing) head++;

                result[i] = line[window[head]];
            }
        }
    }
}
