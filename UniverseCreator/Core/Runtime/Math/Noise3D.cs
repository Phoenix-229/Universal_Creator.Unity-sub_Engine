// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    Noise3D.cs
//  Purpose: Volumetric noise bases for the Noise class. Caves, overhangs, and
//           domain warping need a field defined at (x, y, z), not just a
//           heightfield sample, because an overhang is two surfaces at the same
//           XZ column and a 2-D basis cannot express that.
//
//  This is a partial of Noise.cs, not a separate class: it reuses the same
//  permutation table and the same THREAD SAFETY contract (immutable instance,
//  read-only table, shareable across any number of workers). No new state is
//  introduced here, so nothing about that contract changes.
//
//  Managed C# on plain floats throughout. No Burst, no NativeArray, no
//  Unity.Mathematics - the math layer stays callable from a bare Task worker
//  with no engine or package dependency, exactly as the 2-D side is.
// -----------------------------------------------------------------------------

namespace UniverseCreator
{
    /// <summary>Which volumetric basis a 3-D fractal sum should accumulate.</summary>
    public enum Noise3DType
    {
        /// <summary>Trilinear-interpolated lattice values. Cheapest.</summary>
        Value = 0,
        /// <summary>Improved Perlin over 12 edge gradients. The general default.</summary>
        Perlin = 1,
        /// <summary>Simplex on the tetrahedral lattice. No axis-aligned bias.</summary>
        Simplex = 2,
        /// <summary>Folded Perlin. Sharp crests, smooth hollows - cave ceilings.</summary>
        Ridged = 3,
        /// <summary>Inverse fold. Rounded lobes - blobby chambers.</summary>
        Billow = 4,
        /// <summary>Worley cell distances. Tunnel networks and rock jointing.</summary>
        Cellular = 5,
        /// <summary>Unfiltered per-cell randomness. Seeds and jitter, not surfaces.</summary>
        White = 6
    }

    public sealed partial class Noise
    {
        // ---------------------------------------------------------------------
        //  3-D gradient set and simplex constants
        // ---------------------------------------------------------------------

        // The 12 midpoints of a cube's edges. Perlin's improved-noise choice: all
        // 12 have equal length, so no direction is favoured, and each component is
        // 0 or +/-1 so the dot product is adds and negations rather than multiplies.
        private static readonly int[] Grad3X = { 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0 };
        private static readonly int[] Grad3Y = { 1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1 };
        private static readonly int[] Grad3Z = { 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1 };

        // Skew/unskew for the 3-D simplex lattice.
        private const float F3 = 1.0f / 3.0f;
        private const float G3 = 1.0f / 6.0f;

        /// <summary>Deterministic 0-1 hash of a 3-D integer coordinate, table-free of order.</summary>
        private float Hash3(int x, int y, int z)
        {
            return Random(x, y, z);
        }

        /// <summary>Picks one of the 12 edge gradients for a lattice corner.</summary>
        private int GradIndex3(int x, int y, int z)
        {
            x &= permutationMask;
            y &= permutationMask;
            z &= permutationMask;
            uint h = permutation[x + permutation[z + permutation[y + permutation[subSeed]]]];
            return (int)(h % 12u);
        }

        // ---------------------------------------------------------------------
        //  Value noise, 3-D
        // ---------------------------------------------------------------------

        /// <summary>
        /// Trilinear value noise with the quintic fade. Random value per lattice
        /// point, smoothly reconstructed between them. Output 0-1.
        /// </summary>
        public float Value(float x, float y, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int yi = y > 0 ? (int)y : (int)y - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = InterpolationMath.QuinticFade(x - xi);
            float yf = InterpolationMath.QuinticFade(y - yi);
            float zf = InterpolationMath.QuinticFade(z - zi);

            float c000 = Hash3(xi, yi, zi);
            float c100 = Hash3(xi + 1, yi, zi);
            float c010 = Hash3(xi, yi + 1, zi);
            float c110 = Hash3(xi + 1, yi + 1, zi);
            float c001 = Hash3(xi, yi, zi + 1);
            float c101 = Hash3(xi + 1, yi, zi + 1);
            float c011 = Hash3(xi, yi + 1, zi + 1);
            float c111 = Hash3(xi + 1, yi + 1, zi + 1);

            float lo = InterpolationMath.Bilinear(c000, c100, c010, c110, xf, yf);
            float hi = InterpolationMath.Bilinear(c001, c101, c011, c111, xf, yf);

            return lo + (hi - lo) * zf;
        }

        // ---------------------------------------------------------------------
        //  Perlin, 3-D
        // ---------------------------------------------------------------------

        /// <summary>
        /// Improved Perlin gradient noise in three dimensions. Dots a hashed edge
        /// gradient against the corner offset at all eight cube corners, then
        /// blends with the quintic fade on each axis. Output 0-1.
        /// </summary>
        public float Perlin(float x, float y, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int yi = y > 0 ? (int)y : (int)y - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float fx = x - xi;
            float fy = y - yi;
            float fz = z - zi;

            float u = InterpolationMath.QuinticFade(fx);
            float v = InterpolationMath.QuinticFade(fy);
            float w = InterpolationMath.QuinticFade(fz);

            float n000 = Dot3(GradIndex3(xi, yi, zi), fx, fy, fz);
            float n100 = Dot3(GradIndex3(xi + 1, yi, zi), fx - 1f, fy, fz);
            float n010 = Dot3(GradIndex3(xi, yi + 1, zi), fx, fy - 1f, fz);
            float n110 = Dot3(GradIndex3(xi + 1, yi + 1, zi), fx - 1f, fy - 1f, fz);
            float n001 = Dot3(GradIndex3(xi, yi, zi + 1), fx, fy, fz - 1f);
            float n101 = Dot3(GradIndex3(xi + 1, yi, zi + 1), fx - 1f, fy, fz - 1f);
            float n011 = Dot3(GradIndex3(xi, yi + 1, zi + 1), fx, fy - 1f, fz - 1f);
            float n111 = Dot3(GradIndex3(xi + 1, yi + 1, zi + 1), fx - 1f, fy - 1f, fz - 1f);

            float lo = InterpolationMath.Bilinear(n000, n100, n010, n110, u, v);
            float hi = InterpolationMath.Bilinear(n001, n101, n011, n111, u, v);

            // Unit-length gradients bound the sum to +/-sqrt(3)/2; the standard
            // convention normalises by assuming +/-1 and clamping the tails.
            float result = lo + (hi - lo) * w;
            return InterpolationMath.Clamp01((result + 1f) * 0.5f);
        }

        private static float Dot3(int gradIndex, float dx, float dy, float dz)
        {
            return Grad3X[gradIndex] * dx + Grad3Y[gradIndex] * dy + Grad3Z[gradIndex] * dz;
        }
        // ---------------------------------------------------------------------
        //  Simplex, 3-D
        // ---------------------------------------------------------------------

        /// <summary>
        /// 3-D simplex noise on the tetrahedral lattice. Four corner
        /// contributions instead of Perlin's eight, and no axis-aligned
        /// directional bias, which matters when the field drives cave tunnels
        /// that would otherwise all run parallel to the world axes. Output 0-1.
        /// </summary>
        public float Simplex(float x, float y, float z)
        {
            // Skew the input cube onto the simplex lattice.
            float s = (x + y + z) * F3;
            float xs = x + s;
            float ys = y + s;
            float zs = z + s;

            int i = xs > 0 ? (int)xs : (int)xs - 1;
            int j = ys > 0 ? (int)ys : (int)ys - 1;
            int k = zs > 0 ? (int)zs : (int)zs - 1;

            float t = (i + j + k) * G3;
            float x0 = x - (i - t);
            float y0 = y - (j - t);
            float z0 = z - (k - t);

            // Rank the three offsets to pick which of the six tetrahedra we are
            // inside. The two step vectors follow from that ordering.
            int i1, j1, k1;
            int i2, j2, k2;

            if (x0 >= y0)
            {
                if (y0 >= z0)      { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
                else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
                else               { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
            }
            else
            {
                if (y0 < z0)       { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
                else if (x0 < z0)  { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
                else               { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            }

            float x1 = x0 - i1 + G3;
            float y1 = y0 - j1 + G3;
            float z1 = z0 - k1 + G3;

            float x2 = x0 - i2 + 2f * G3;
            float y2 = y0 - j2 + 2f * G3;
            float z2 = z0 - k2 + 2f * G3;

            float x3 = x0 - 1f + 3f * G3;
            float y3 = y0 - 1f + 3f * G3;
            float z3 = z0 - 1f + 3f * G3;

            float n = 0f;
            n += Corner3(GradIndex3(i, j, k), x0, y0, z0);
            n += Corner3(GradIndex3(i + i1, j + j1, k + k1), x1, y1, z1);
            n += Corner3(GradIndex3(i + i2, j + j2, k + k2), x2, y2, z2);
            n += Corner3(GradIndex3(i + 1, j + 1, k + 1), x3, y3, z3);

            // 32 is the conventional scale factor that maps the summed corner
            // contributions of this gradient set into roughly [-1, 1].
            return InterpolationMath.Clamp01((32f * n + 1f) * 0.5f);
        }

        /// <summary>Radially symmetric (0.6 - r^2)^4 attenuation for one simplex corner.</summary>
        private static float Corner3(int gradIndex, float dx, float dy, float dz)
        {
            float t = 0.6f - dx * dx - dy * dy - dz * dz;
            if (t < 0f) return 0f;
            t *= t;
            return t * t * Dot3(gradIndex, dx, dy, dz);
        }

        // ---------------------------------------------------------------------
        //  Folded bases
        // ---------------------------------------------------------------------

        /// <summary>
        /// Folded 3-D Perlin. Crests where the raw field crossed zero, hollows
        /// elsewhere. Drives cave ceilings and jointed rock faces.
        /// </summary>
        public float Ridged(float x, float y, float z, float sharpness = 2f)
        {
            float v = Perlin(x, y, z) * 2f - 1f;
            float ridge = 1f - InterpolationMath.Abs(v);
            return Pow(ridge, sharpness);
        }

        /// <summary>Inverse fold of <see cref="Ridged(float,float,float,float)"/>. Rounded lobes.</summary>
        public float Billow(float x, float y, float z)
        {
            float v = Perlin(x, y, z) * 2f - 1f;
            return InterpolationMath.Abs(v);
        }

        /// <summary>
        /// Unfiltered per-lattice-cell randomness. No interpolation, so adjacent
        /// samples are uncorrelated - useful as a jitter source, never as a
        /// surface.
        /// </summary>
        public float White(float x, float y, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int yi = y > 0 ? (int)y : (int)y - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;
            return Hash3(xi, yi, zi);
        }

        // ---------------------------------------------------------------------
        //  Cellular / Worley, 3-D
        // ---------------------------------------------------------------------

        /// <summary>
        /// Worley cellular noise in three dimensions. One jittered feature point
        /// per lattice cell; the two nearest distances are reduced per
        /// <paramref name="blend"/>. The Cellular blend (d2 - d1) traces the
        /// planes between cells, which is what reads as a tunnel network when
        /// thresholded.
        /// </summary>
        /// <param name="uniformity">0 = fully jittered, 1 = pinned to cell centres.</param>
        public float Cellular(float x, float y, float z, float uniformity = 0f,
                              VoronoiBlend blend = VoronoiBlend.Cellular)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int yi = y > 0 ? (int)y : (int)y - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float jitter = 1f - InterpolationMath.Clamp01(uniformity);

            float d1 = float.MaxValue;
            float d2 = float.MaxValue;
            int nx = xi, ny = yi, nz = zi;

            // 3x3x3 is the minimum sound neighbourhood: with unit cell spacing a
            // point two cells away can never beat one in the immediate ring.
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int cx = xi + dx;
                        int cy = yi + dy;
                        int cz = zi + dz;

                        // Three decorrelated hashes place the feature point. The
                        // large primes rotate the coordinate triple so the three
                        // offsets do not collapse onto the cell diagonal.
                        float ox = Hash3(cx, cy, cz);
                        float oy = Hash3(cy + 7919, cz + 104729, cx + 1299709);
                        float oz = Hash3(cz + 15485863, cx + 32452843, cy + 49979687);

                        float px = cx + 0.5f + (ox - 0.5f) * jitter;
                        float py = cy + 0.5f + (oy - 0.5f) * jitter;
                        float pz = cz + 0.5f + (oz - 0.5f) * jitter;

                        float ddx = px - x;
                        float ddy = py - y;
                        float ddz = pz - z;
                        float dist = (float)System.Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);

                        if (dist < d1)
                        {
                            d2 = d1;
                            d1 = dist;
                            nx = cx; ny = cy; nz = cz;
                        }
                        else if (dist < d2)
                        {
                            d2 = dist;
                        }
                    }
                }
            }

            switch (blend)
            {
                case VoronoiBlend.Flat:          return Hash3(nx, ny, nz);
                case VoronoiBlend.Closest:       return InterpolationMath.Clamp01(d1);
                case VoronoiBlend.SecondClosest: return InterpolationMath.Clamp01(d2 * 0.5f);
                case VoronoiBlend.Cellular:      return InterpolationMath.Clamp01(d2 - d1);
                case VoronoiBlend.Organic:       return InterpolationMath.Clamp01((d1 + d2) * 0.5f);
                default:                         return InterpolationMath.Clamp01(d2 - d1);
            }
        }

        // ---------------------------------------------------------------------
        //  Basis dispatch and fractal sum
        // ---------------------------------------------------------------------

        /// <summary>Samples one octave of the requested volumetric basis.</summary>
        public float Sample3D(Noise3DType type, float x, float y, float z)
        {
            switch (type)
            {
                case Noise3DType.Value:    return Value(x, y, z);
                case Noise3DType.Perlin:   return Perlin(x, y, z);
                case Noise3DType.Simplex:  return Simplex(x, y, z);
                case Noise3DType.Ridged:   return Ridged(x, y, z);
                case Noise3DType.Billow:   return Billow(x, y, z);
                case Noise3DType.Cellular: return Cellular(x, y, z);
                case Noise3DType.White:    return White(x, y, z);
                default:                   return 0f;
            }
        }

        /// <summary>
        /// Volumetric fractal Brownian motion. Same contract as the 2-D
        /// <see cref="Fractal"/>: <paramref name="size"/> is the feature size of
        /// the first octave in world metres, so callers pass absolute world
        /// coordinates and chunks agree along shared edges.
        /// </summary>
        /// <param name="lacunarity">
        /// Frequency multiplier per octave. 2 doubles the frequency each step;
        /// a non-integer value like 2.017 stops octave lattices from lining up,
        /// which removes the faint grid that otherwise shows through at depth.
        /// </param>
        public float Fractal3D(float x, float y, float z, float size, int iterations = 6,
                               float detail = 0.5f, float lacunarity = 2f,
                               Noise3DType type = Noise3DType.Perlin)
        {
            if (size < 1e-6f) return 0f;
            if (iterations < 1) iterations = 1;
            if (iterations > 16) iterations = 16;

            float result = 0f;
            float norm = 0f;
            float freq = 1f / size;
            float amp = 1f;

            int permSeed = (int)permutation[subSeed];

            for (int i = 0; i < iterations; i++)
            {
                // Per-octave offset drawn from the permutation table. Without it
                // every octave shares the lattice origin and the sum shows a
                // stationary artefact wherever all octaves happen to align.
                float offX = permutation[(permSeed + i * 3) & permutationMask] * 0.000122f;
                float offY = permutation[(permSeed + i * 3 + 1) & permutationMask] * 0.000122f;
                float offZ = permutation[(permSeed + i * 3 + 2) & permutationMask] * 0.000122f;

                result += Sample3D(type, x * freq + offX, y * freq + offY, z * freq + offZ) * amp;
                norm += amp;

                freq *= lacunarity;
                amp *= detail;
            }

            return norm > 1e-6f ? result / norm : 0f;
        }

        /// <summary>
        /// Ridged multifractal in three dimensions. Each octave's amplitude is
        /// weighted by the previous octave's value, which concentrates detail on
        /// the crests and leaves the hollows smooth - the difference between a
        /// cave system with sharp rock ribs and one that is uniformly noisy.
        /// </summary>
        public float RidgedMultifractal3D(float x, float y, float z, float size,
                                          int iterations = 6, float detail = 0.5f,
                                          float sharpness = 2f, float weightFalloff = 0.8f,
                                          float lacunarity = 2f)
        {
            if (size < 1e-6f) return 0f;
            if (iterations < 1) iterations = 1;
            if (iterations > 16) iterations = 16;

            float result = 0f;
            float norm = 0f;
            float freq = 1f / size;
            float amp = 1f;
            float weight = 1f;

            for (int i = 0; i < iterations; i++)
            {
                float signal = Ridged(x * freq, y * freq, z * freq, sharpness);

                signal *= weight;
                weight = InterpolationMath.Clamp01(signal * weightFalloff);

                result += signal * amp;
                norm += amp;

                freq *= lacunarity;
                amp *= detail;
            }

            return norm > 1e-6f ? result / norm : 0f;
        }

        /// <summary>
        /// Hybrid multifractal. Roughness varies with elevation in the field
        /// rather than being constant, so low ground stays smooth while high
        /// ground breaks up - the standard Musgrave construction.
        /// </summary>
        public float HybridMultifractal3D(float x, float y, float z, float size,
                                          int iterations = 6, float detail = 0.5f,
                                          float offset = 0.7f, float lacunarity = 2f)
        {
            if (size < 1e-6f) return 0f;
            if (iterations < 1) iterations = 1;
            if (iterations > 16) iterations = 16;

            float freq = 1f / size;

            float signal = Perlin(x * freq, y * freq, z * freq) * 2f - 1f + offset;
            float result = signal;
            float weight = signal;
            float amp = detail;

            for (int i = 1; i < iterations; i++)
            {
                freq *= lacunarity;

                // Weight saturates rather than running away; without the clamp a
                // few octaves of positive feedback push the sum out of range and
                // the terrain clips to a plateau.
                if (weight > 1f) weight = 1f;
                if (weight < 0f) weight = 0f;

                signal = (Perlin(x * freq, y * freq, z * freq) * 2f - 1f + offset) * amp;
                result += weight * signal;
                weight += signal;
                amp *= detail;
            }

            return InterpolationMath.Clamp01(result * 0.5f);
        }

        // ---------------------------------------------------------------------
        //  Curl and domain warp
        // ---------------------------------------------------------------------

        /// <summary>
        /// Curl of a 3-D potential field, evaluated by central differences.
        /// The result is divergence-free by construction, which is what makes it
        /// read as flow rather than as displacement: warping with it stretches
        /// and swirls a field without pooling or tearing it.
        /// </summary>
        /// <param name="epsilon">
        /// Finite-difference step in the same units as the coordinates. Too small
        /// and the subtraction loses precision to float cancellation; too large
        /// and the derivative is smoothed away. A few thousandths of the feature
        /// size is the usable band.
        /// </param>
        public void Curl3D(float x, float y, float z, float size, out float cx, out float cy, out float cz,
                           float epsilon = 0.05f, int iterations = 3)
        {
            if (epsilon < 1e-5f) epsilon = 1e-5f;
            float inv = 1f / (2f * epsilon);

            // Three decorrelated potential components. Offsetting each into its
            // own region of the noise field is cheaper than building three
            // permutation tables and just as independent in practice.
            const float SepA = 0f;
            const float SepB = 137.13f;
            const float SepC = 431.71f;

            float p1y = Fractal3D(x + SepA, y + SepA + epsilon, z + SepA, size, iterations);
            float p2y = Fractal3D(x + SepA, y + SepA - epsilon, z + SepA, size, iterations);
            float p1z = Fractal3D(x + SepA, y + SepA, z + SepA + epsilon, size, iterations);
            float p2z = Fractal3D(x + SepA, y + SepA, z + SepA - epsilon, size, iterations);

            float q1x = Fractal3D(x + SepB + epsilon, y + SepB, z + SepB, size, iterations);
            float q2x = Fractal3D(x + SepB - epsilon, y + SepB, z + SepB, size, iterations);
            float q1z = Fractal3D(x + SepB, y + SepB, z + SepB + epsilon, size, iterations);
            float q2z = Fractal3D(x + SepB, y + SepB, z + SepB - epsilon, size, iterations);

            float r1x = Fractal3D(x + SepC + epsilon, y + SepC, z + SepC, size, iterations);
            float r2x = Fractal3D(x + SepC - epsilon, y + SepC, z + SepC, size, iterations);
            float r1y = Fractal3D(x + SepC, y + SepC + epsilon, z + SepC, size, iterations);
            float r2y = Fractal3D(x + SepC, y + SepC - epsilon, z + SepC, size, iterations);

            // curl F = (dFz/dy - dFy/dz, dFx/dz - dFz/dx, dFy/dx - dFx/dy)
            cx = ((r1y - r2y) - (q1z - q2z)) * inv;
            cy = ((p1z - p2z) - (r1x - r2x)) * inv;
            cz = ((q1x - q2x) - (p1y - p2y)) * inv;
        }

        /// <summary>
        /// Two-dimensional curl, for warping a heightfield in the XZ plane. The
        /// 3-D form projected onto a single Y slice: the gradient of one scalar
        /// potential rotated 90 degrees, which is divergence-free in the plane.
        /// </summary>
        public void Curl2D(float x, float z, float size, out float cx, out float cz,
                           float epsilon = 0.05f, int iterations = 3)
        {
            if (epsilon < 1e-5f) epsilon = 1e-5f;
            float inv = 1f / (2f * epsilon);

            float pz1 = Fractal(x, z + epsilon, size, iterations);
            float pz2 = Fractal(x, z - epsilon, size, iterations);
            float px1 = Fractal(x + epsilon, z, size, iterations);
            float px2 = Fractal(x - epsilon, z, size, iterations);

            cx = (pz1 - pz2) * inv;
            cz = -(px1 - px2) * inv;
        }

        /// <summary>
        /// Swiss-turbulence variant: each octave is displaced by the running
        /// gradient of the octaves before it, so detail bends along the slope
        /// it sits on instead of being stamped on flat. Produces the eroded,
        /// wind-carved ridgeline look without running an erosion solver.
        /// </summary>
        public float SwissTurbulence(float x, float z, float size, int iterations = 6,
                                     float detail = 0.5f, float warp = 0.15f,
                                     float lacunarity = 2f)
        {
            if (size < 1e-6f) return 0f;
            if (iterations < 1) iterations = 1;
            if (iterations > 16) iterations = 16;

            float sum = 0f;
            float norm = 0f;
            float freq = 1f / size;
            float amp = 1f;

            float dx = 0f;
            float dz = 0f;

            const float Eps = 0.5f;

            for (int i = 0; i < iterations; i++)
            {
                float sx = (x + warp * dx) * freq;
                float sz = (z + warp * dz) * freq;

                float v = Perlin(sx, sz) * 2f - 1f;
                float ridged = 1f - InterpolationMath.Abs(v);

                sum += ridged * amp;
                norm += amp;

                // Central-difference gradient of this octave, accumulated so the
                // next octave rides the surface this one just described.
                float gx = (Perlin(sx + Eps, sz) - Perlin(sx - Eps, sz)) * 0.5f;
                float gz = (Perlin(sx, sz + Eps) - Perlin(sx, sz - Eps)) * 0.5f;
                dx += -gx * amp;
                dz += -gz * amp;

                freq *= lacunarity;
                amp *= detail;
            }

            return norm > 1e-6f ? InterpolationMath.Clamp01(sum / norm) : 0f;
        }

        /// <summary>
        /// Marble banding: a sine sweep along one axis, its phase disturbed by
        /// fractal noise. Standard for rock strata and veined material masks.
        /// </summary>
        public float Marble(float x, float z, float size, float bandFrequency = 4f,
                            float distortion = 1f, int iterations = 4)
        {
            if (size < 1e-6f) return 0f;

            float n = Fractal(x, z, size, iterations);
            float phase = (x / size) * bandFrequency + n * distortion * 6.2831853f;
            return (float)(System.Math.Sin(phase) + 1.0) * 0.5f;
        }

        /// <summary>
        /// Caustic pattern: the folded absolute of a cellular field, sharpened.
        /// Reads as the light net cast through moving water onto a seabed, and is
        /// used by the underwater material path rather than by the heightfield.
        /// </summary>
        public float Caustic(float x, float z, float size, float sharpness = 3f)
        {
            if (size < 1e-6f) return 0f;

            float a = Voronoi(x / size, z / size, 0.2f, VoronoiBlend.Closest);
            float folded = 1f - InterpolationMath.Abs(a * 2f - 1f);
            return Pow(InterpolationMath.Clamp01(folded), sharpness);
        }
    }
}
