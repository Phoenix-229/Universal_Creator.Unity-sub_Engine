// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Noise.cs
//  Purpose: Thread-safe noise field generators: Perlin, Simplex, Value, Ridged,
//           Billow, Voronoi, and fractal Brownian motion over any of them.
//
//  THREAD SAFETY
//  -------------
//  This is an immutable instance class, not a static one. The permutation table
//  is built once in the constructor and is read-only thereafter, and subSeed is
//  readonly rather than a settable property. Any number of threads may share a
//  single Noise instance and call into it concurrently.
//
//  To vary output without paying to rebuild the 16384-entry table, call
//  WithSubSeed(); the returned instance aliases the same table array.
//
//  Reference: the permutation scheme and the Perlin/Simplex inner loops follow
//  Ken Perlin's improved noise (2002) and Stefan Gustavson's simplex noise
//  paper. Improvements made here are called out inline.
// -----------------------------------------------------------------------------

using System;

namespace UniverseCreator
{
    /// <summary>Which underlying noise basis a fractal sum should accumulate.</summary>
    public enum NoiseType
    {
        Value = 0,
        Perlin = 1,
        Simplex = 2,
        Ridged = 3,
        Billow = 4,
        Voronoi = 5,

        // Appended, never inserted: these are serialized as ints, so renumbering
        // the entries above would silently repoint every existing graph.
        /// <summary>Value noise with no fade curve. Hard, faceted lattice.</summary>
        ValueLinear = 6,
        /// <summary>Value noise reconstructed bicubically over a 4x4 neighbourhood.</summary>
        ValueCubic = 7
    }

    /// <summary>How a Voronoi/Worley sample turns cell distances into a value.</summary>
    public enum VoronoiBlend
    {
        /// <summary>Flat per-cell random value. Produces hard-edged plates.</summary>
        Flat = 0,
        /// <summary>Distance to the nearest feature point.</summary>
        Closest = 1,
        /// <summary>Distance to the second nearest feature point.</summary>
        SecondClosest = 2,
        /// <summary>d2 - d1. The classic cellular "cracks between plates" look.</summary>
        Cellular = 3,
        /// <summary>(d1 + d2) / 2. Softer, more organic blobs.</summary>
        Organic = 4
    }

    /// <summary>
    /// Immutable, deterministic, thread-safe noise generator.
    /// </summary>
    [Serializable]
    public sealed partial class Noise
    {
        // ---------------------------------------------------------------------
        //  State (all readonly - see THREAD SAFETY note in the file header)
        // ---------------------------------------------------------------------

        private readonly uint[] permutation;
        private readonly int permutationCount;
        private readonly int permutationMask;
        private readonly int seed;
        private readonly int subSeed;

        /// <summary>Seed the permutation table was built from.</summary>
        public int Seed { get { return seed; } }

        /// <summary>Table-free variation offset. Always within the table size.</summary>
        public int SubSeed { get { return subSeed; } }

        /// <summary>Number of entries in the logical (undoubled) permutation table.</summary>
        public int PermutationCount { get { return permutationCount; } }

        // Perlin's 8 planar gradient directions. Kept as the exact set the spec
        // calls for so output matches the reference implementation bit for bit.
        private static readonly int[] GradX = { 1, -1, 1, -1, 1, -1, 0, 0 };
        private static readonly int[] GradZ = { 0, 0, 1, 1, -1, -1, 1, -1 };

        // Simplex skew/unskew constants for 2D.
        private const float Sqrt3 = 1.7320508075688772f;
        private const float F2 = 0.5f * (Sqrt3 - 1.0f);
        private const float G2 = (3.0f - Sqrt3) / 6.0f;

        // ---------------------------------------------------------------------
        //  Construction
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds a new permutation table for the given seed.
        /// </summary>
        /// <param name="seed">Deterministic seed.</param>
        /// <param name="permutationCount">
        /// Table size. Must be a power of two; 16384 is the spec default.
        /// </param>
        public Noise(int seed, int permutationCount = 16384)
        {
            if (permutationCount <= 0 || (permutationCount & (permutationCount - 1)) != 0)
                throw new ArgumentException(
                    "permutationCount must be a positive power of two.", "permutationCount");

            this.permutationCount = permutationCount;
            this.permutationMask = permutationCount - 1;
            this.seed = seed;
            this.subSeed = 0;
            this.permutation = BuildPermutation(seed, permutationCount);
        }

        /// <summary>Private aliasing constructor used by <see cref="WithSubSeed"/>.</summary>
        private Noise(Noise source, int subSeed)
        {
            this.permutation = source.permutation;      // shared, read-only
            this.permutationCount = source.permutationCount;
            this.permutationMask = source.permutationMask;
            this.seed = source.seed;
            this.subSeed = subSeed & source.permutationMask;
        }

        /// <summary>
        /// Returns a new Noise that shares this instance's permutation table but
        /// samples it from a different offset. No table rebuild, no allocation
        /// beyond the small wrapper object, and the result is independently
        /// thread-safe.
        /// </summary>
        public Noise WithSubSeed(int newSubSeed)
        {
            return new Noise(this, newSubSeed);
        }

        /// <summary>
        /// Fills the table with a deterministic LCG shuffle, then mirrors it so
        /// index arithmetic can run past the end without wrapping logic.
        /// </summary>
        private static uint[] BuildPermutation(int seed, int count)
        {
            uint[] table = new uint[count * 2];

            // Numerical Recipes LCG. Chosen over System.Random because it is
            // guaranteed identical across .NET runtimes and Unity scripting
            // backends, which matters for reproducible worlds.
            int s = seed;
            for (int i = 0; i < count; i++)
            {
                s = 214013 * s + 2531011;
                float v = ((s >> 16) & 0x7FFF) / 32768f;
                table[i] = (uint)(v * count);
            }

            // Mirror half. Guarantees table[a + table[b]] is always in bounds
            // for any masked a, b, since both are <= count-1.
            for (int i = 0; i < count; i++)
                table[i + count] = table[i];

            return table;
        }

        /// <summary>Cantor pairing: uniquely encodes two non-negative ints as one.</summary>
        public static int Pair(int k1, int k2)
        {
            int t = (k1 + k2) * (k1 + k2 + 1);
            return t / 2 + k2;
        }

        // ---------------------------------------------------------------------
        //  Integer hashing -> 0-1
        // ---------------------------------------------------------------------

        /// <summary>Deterministic 0-1 hash of one integer coordinate.</summary>
        public float Random(int x)
        {
            x &= permutationMask;
            x = (int)permutation[x + permutation[subSeed]];
            return (float)x / permutationCount;
        }

        /// <summary>Deterministic 0-1 hash of a 2D integer coordinate.</summary>
        public float Random(int x, int z)
        {
            x &= permutationMask;
            z &= permutationMask;
            x = (int)permutation[x + permutation[z + permutation[subSeed]]];
            return (float)x / permutationCount;
        }

        /// <summary>Deterministic 0-1 hash of a 3D integer coordinate.</summary>
        public float Random(int x, int y, int z)
        {
            x &= permutationMask;
            y &= permutationMask;
            z &= permutationMask;
            x = (int)permutation[x + permutation[z + permutation[y + permutation[subSeed]]]];
            return (float)x / permutationCount;
        }

        /// <summary>Deterministic integer hash in [0, PermutationCount).</summary>
        public int RandomInt(int x)
        {
            x &= permutationMask;
            return (int)permutation[x + permutation[subSeed]];
        }

        // ---------------------------------------------------------------------
        //  Value noise
        // ---------------------------------------------------------------------

        /// <summary>
        /// Value noise: random values at lattice points, quintic-faded bilinear
        /// interpolation between them. Output is normalised to 0-1.
        /// </summary>
        public float Value(float x, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = x - xi;
            float zf = z - zi;

            xi &= permutationMask;
            zi &= permutationMask;

            uint permSeed = permutation[subSeed];
            uint aa = permutation[permutation[permSeed + xi] + zi];
            uint ab = permutation[permutation[permSeed + xi] + zi + 1];
            uint ba = permutation[permutation[permSeed + xi + 1] + zi];
            uint bb = permutation[permutation[permSeed + xi + 1] + zi + 1];

            // Quintic rather than the reference's cubic fade: removes the faint
            // lattice creasing that shows up once fBm stacks many octaves.
            float xfade = InterpolationMath.QuinticFade(xf);
            float zfade = InterpolationMath.QuinticFade(zf);

            float x1 = aa * (1 - xfade) + ba * xfade;
            float x2 = ab * (1 - xfade) + bb * xfade;
            float result = x1 * (1 - zfade) + x2 * zfade;

            return result / permutationCount;
        }

        /// <summary>
        /// Value noise with straight bilinear interpolation and no fade curve.
        /// The lattice stays visible as faceting, which is the point when the
        /// output drives a hard-edged or stylised surface.
        /// </summary>
        public float ValueLinear(float x, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = x - xi;
            float zf = z - zi;

            xi &= permutationMask;
            zi &= permutationMask;

            uint permSeed = permutation[subSeed];
            uint aa = permutation[permutation[permSeed + xi] + zi];
            uint ab = permutation[permutation[permSeed + xi] + zi + 1];
            uint ba = permutation[permutation[permSeed + xi + 1] + zi];
            uint bb = permutation[permutation[permSeed + xi + 1] + zi + 1];

            float x1 = aa * (1 - xf) + ba * xf;
            float x2 = ab * (1 - xf) + bb * xf;
            float result = x1 * (1 - zf) + x2 * zf;

            return result / permutationCount;
        }

        /// <summary>
        /// Value noise using bicubic (Catmull-Rom) reconstruction over a 4x4
        /// lattice neighbourhood. Smoother than <see cref="Value"/> at roughly
        /// four times the cost.
        /// </summary>
        public float ValueCubic(float x, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = x - xi;
            float zf = z - zi;

            float[] p = new float[16];
            for (int dz = -1; dz <= 2; dz++)
                for (int dx = -1; dx <= 2; dx++)
                    p[(dz + 1) * 4 + (dx + 1)] = Random(xi + dx, zi + dz);

            return InterpolationMath.Clamp01(InterpolationMath.Bicubic(p, xf, zf));
        }

        // ---------------------------------------------------------------------
        //  Perlin (improved)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Improved Perlin gradient noise. Dots a hashed gradient against the
        /// corner offset at each of the four lattice corners, then blends with
        /// the quintic fade. Output is normalised to 0-1.
        /// </summary>
        public float Perlin(float x, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = x - xi;
            float zf = z - zi;

            xi &= permutationMask;
            zi &= permutationMask;

            int permSeed = (int)permutation[subSeed];
            int aa = (int)permutation[permutation[permSeed + xi] + zi] & 0x7;
            int ab = (int)permutation[permutation[permSeed + xi] + zi + 1] & 0x7;
            int ba = (int)permutation[permutation[permSeed + xi + 1] + zi] & 0x7;
            int bb = (int)permutation[permutation[permSeed + xi + 1] + zi + 1] & 0x7;

            // Gradient dot products at the four corners.
            float aaDot = GradX[aa] * xf + GradZ[aa] * zf;
            float abDot = GradX[ab] * xf + GradZ[ab] * (zf - 1);
            float baDot = GradX[ba] * (xf - 1) + GradZ[ba] * zf;
            float bbDot = GradX[bb] * (xf - 1) + GradZ[bb] * (zf - 1);

            float xfade = InterpolationMath.QuinticFade(xf);
            float zfade = InterpolationMath.QuinticFade(zf);

            float x1 = aaDot * (1 - xfade) + baDot * xfade;
            float x2 = abDot * (1 - xfade) + bbDot * xfade;
            float result = x1 * (1 - zfade) + x2 * zfade;

            return (result + 1) * 0.5f;
        }

        // ---------------------------------------------------------------------
        //  Simplex (Gustavson)
        // ---------------------------------------------------------------------

        /// <summary>
        /// 2D simplex noise on an equilateral triangle lattice. Cheaper per
        /// sample than Perlin at higher dimensions and free of the axis-aligned
        /// directional bias Perlin exhibits. Output is normalised to 0-1.
        /// </summary>
        public float Simplex(float x, float z)
        {
            // Skew input space to determine which simplex cell we are in.
            float s = (x + z) * F2;
            float xs = x + s;
            float zs = z + s;

            int i = xs > 0 ? (int)xs : (int)xs - 1;
            int j = zs > 0 ? (int)zs : (int)zs - 1;

            // Unskew back to get the cell origin in input space.
            float t = (i + j) * G2;
            float x0 = x - (i - t);
            float z0 = z - (j - t);

            // Which of the two triangles of the cell are we in?
            int i1 = x0 > z0 ? 1 : 0;
            int j1 = x0 > z0 ? 0 : 1;

            float x1 = x0 - i1 + G2;
            float z1 = z0 - j1 + G2;
            float x2 = x0 - 1 + 2 * G2;
            float z2 = z0 - 1 + 2 * G2;

            int ii = i & permutationMask;
            int jj = j & permutationMask;

            uint permSeed = permutation[subSeed];
            uint gi0 = permutation[ii + permutation[jj + permSeed]] % 8;
            uint gi1 = permutation[ii + i1 + permutation[jj + j1 + permSeed]] % 8;
            uint gi2 = permutation[ii + 1 + permutation[jj + 1 + permSeed]] % 8;

            // Radially symmetric attenuation, (0.5 - r^2)^4, per corner.
            float n0, n1, n2;

            float t0 = 0.5f - x0 * x0 - z0 * z0;
            n0 = t0 < 0 ? 0f : t0 * t0 * t0 * t0 * (GradX[gi0] * x0 + GradZ[gi0] * z0);

            float t1 = 0.5f - x1 * x1 - z1 * z1;
            n1 = t1 < 0 ? 0f : t1 * t1 * t1 * t1 * (GradX[gi1] * x1 + GradZ[gi1] * z1);

            float t2 = 0.5f - x2 * x2 - z2 * z2;
            n2 = t2 < 0 ? 0f : t2 * t2 * t2 * t2 * (GradX[gi2] * x2 + GradZ[gi2] * z2);

            // 70 is the standard scale factor that maps the summed contributions
            // into roughly [-1, 1] for this gradient set.
            return (70.0f * (n0 + n1 + n2) + 1f) * 0.5f;
        }

        // ---------------------------------------------------------------------
        //  Ridged and Billow bases
        // ---------------------------------------------------------------------

        /// <summary>
        /// Single-octave ridged basis: pow(1 - |perlin*2-1|, sharpness).
        /// Folds the noise about its midline so the former zero crossings become
        /// sharp crests, which is what reads as mountain ridge lines.
        /// </summary>
        public float Ridged(float x, float z, float sharpness = 2f)
        {
            float v = Perlin(x, z) * 2f - 1f;
            float ridge = 1f - InterpolationMath.Abs(v);
            return Pow(ridge, sharpness);
        }

        /// <summary>
        /// Single-octave billow basis: |perlin*2-1|. The inverse fold of ridged,
        /// giving rounded, cloud-like or dune-like lobes.
        /// </summary>
        public float Billow(float x, float z)
        {
            float v = Perlin(x, z) * 2f - 1f;
            return InterpolationMath.Abs(v);
        }

        /// <summary>
        /// Positive-base power. Avoids UnityEngine.Mathf so the math layer stays
        /// callable from plain background threads with no engine dependency.
        /// </summary>
        private static float Pow(float value, float exponent)
        {
            if (value <= 0f) return 0f;
            if (exponent == 1f) return value;
            if (exponent == 2f) return value * value;
            return (float)System.Math.Pow(value, exponent);
        }

        // ---------------------------------------------------------------------
        //  Voronoi / Worley
        // ---------------------------------------------------------------------

        /// <summary>
        /// Worley cellular noise. Scatters one feature point per lattice cell,
        /// jittered by <paramref name="uniformity"/>, then reduces the two
        /// nearest distances according to <paramref name="blend"/>.
        /// </summary>
        /// <param name="uniformity">
        /// 0 = feature points fully jittered inside their cell (organic),
        /// 1 = every point pinned to its cell centre (a regular grid).
        /// </param>
        public float Voronoi(float x, float z, float uniformity = 0f, VoronoiBlend blend = VoronoiBlend.Cellular)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float jitter = 1f - InterpolationMath.Clamp01(uniformity);

            float d1 = float.MaxValue;   // nearest
            float d2 = float.MaxValue;   // second nearest
            int nearestX = xi;
            int nearestZ = zi;

            // 3x3 cell neighbourhood is sufficient: a point in an adjacent cell
            // can never be nearer than one two cells away, given unit spacing.
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = xi + dx;
                    int cz = zi + dz;

                    // Two decorrelated hashes give the point's offset in its cell.
                    float ox = Random(cx, cz);
                    float oz = Random(cz + 7919, cx + 104729);   // large primes decorrelate the pair

                    float px = cx + 0.5f + (ox - 0.5f) * jitter;
                    float pz = cz + 0.5f + (oz - 0.5f) * jitter;

                    float ddx = px - x;
                    float ddz = pz - z;
                    float dist = (float)System.Math.Sqrt(ddx * ddx + ddz * ddz);

                    if (dist < d1)
                    {
                        d2 = d1;
                        d1 = dist;
                        nearestX = cx;
                        nearestZ = cz;
                    }
                    else if (dist < d2)
                    {
                        d2 = dist;
                    }
                }
            }

            switch (blend)
            {
                case VoronoiBlend.Flat:
                    return Random(nearestX, nearestZ);

                case VoronoiBlend.Closest:
                    return InterpolationMath.Clamp01(d1);

                case VoronoiBlend.SecondClosest:
                    return InterpolationMath.Clamp01(d2 * 0.5f);

                case VoronoiBlend.Cellular:
                    return InterpolationMath.Clamp01(d2 - d1);

                case VoronoiBlend.Organic:
                    return InterpolationMath.Clamp01((d1 + d2) * 0.5f);

                default:
                    return InterpolationMath.Clamp01(d2 - d1);
            }
        }

        // ---------------------------------------------------------------------
        //  Fractal Brownian motion
        // ---------------------------------------------------------------------

        /// <summary>
        /// Samples one octave of the requested basis.
        /// </summary>
        private float Sample(NoiseType type, float x, float z)
        {
            switch (type)
            {
                case NoiseType.Value: return Value(x, z);
                case NoiseType.Perlin: return Perlin(x, z);
                case NoiseType.Simplex: return Simplex(x, z);
                case NoiseType.Ridged: return Ridged(x, z);
                case NoiseType.Billow: return Billow(x, z);
                case NoiseType.Voronoi: return Voronoi(x, z);
                case NoiseType.ValueLinear: return ValueLinear(x, z);
                case NoiseType.ValueCubic: return ValueCubic(x, z);
                default: return 0f;
            }
        }

        /// <summary>
        /// Fractal Brownian motion: sums successively higher-frequency, lower-
        /// amplitude octaves of the chosen basis.
        /// </summary>
        /// <param name="x">World X.</param>
        /// <param name="z">World Z.</param>
        /// <param name="size">Feature size of the first (largest) octave, in world units.</param>
        /// <param name="iterations">Octave count. Pass -1 to derive it from size.</param>
        /// <param name="detail">
        /// Amplitude falloff per octave (persistence). 0.5 is the neutral default.
        /// </param>
        /// <param name="turbulence">
        /// -1..1. Non-zero folds each octave about its midline before summing;
        /// positive values invert the fold, giving ridges instead of creases.
        /// </param>
        /// <param name="type">Which basis to accumulate.</param>
        /// <returns>
        /// 0-1, given a basis that returns 0-1. Amplitude is divided by the sum
        /// actually accumulated, so octave count and detail change the character of
        /// the surface without changing its range - raising detail roughens rather
        /// than rescales. Not hard-clamped, since a basis that overshoots slightly
        /// should stay visible to a caller rather than be silently flattened.
        /// </returns>
        public float Fractal(float x, float z, float size, int iterations = -1,
                             float detail = 0.5f, float turbulence = 0f,
                             NoiseType type = NoiseType.Perlin)
        {
            if (size < 1e-6f) return 0f;

            // Derive a sensible octave count from the feature size when unset:
            // stop once the octave wavelength drops below a single pixel.
            if (iterations < 0)
                iterations = (int)(System.Math.Log(size, 2)) + 1;
            if (iterations < 1) iterations = 1;

            float absTurbulence = turbulence > 0 ? turbulence : -turbulence;
            int permSeed = (int)permutation[subSeed];

            float result = 0f;
            float norm = 0f;
            float freq = size;
            float amp = 1f;

            for (int i = 0; i < iterations; i++)
            {
                // Per-octave offset. The reference only applies these to its
                // Mathf.PerlinNoise path, which lets its own octaves line up at
                // the lattice origin; deriving an offset from the permutation
                // table for every basis decorrelates them while staying fully
                // deterministic.
                float offX = permutation[(permSeed + i * 2) & permutationMask] * 0.000122f;
                float offZ = permutation[(permSeed + i * 2 + 1) & permutationMask] * 0.000122f;

                float val = Sample(type, x / freq + offX, z / freq + offZ);

                if (absTurbulence > 0.001f)
                {
                    float turb = val * 2f - 1f;
                    if (turb < 0) turb = -turb;
                    if (turbulence > 0) turb = 1f - turb;

                    val = val * (1f - absTurbulence) + turb * absTurbulence;
                }

                result += val * amp;
                norm += amp;

                freq *= 0.5f;
                amp *= detail;
            }

            // Divide by the amplitude actually accumulated, not by the closed form
            // (1 - detail). That closed form is the limit of the geometric series
            // at infinite octaves; the loop runs a finite count, so the real sum is
            // (1 - detail^N)/(1 - detail) and the two diverge as detail rises. At
            // detail 0.8 over 6 octaves the old factor reached only 74% of range
            // and at 0.9 only 47%, so turning detail up flattened the terrain
            // instead of roughening it and changing the octave count rescaled the
            // whole landform. Accumulating matches Fractal3D, which was already right.
            return norm > 1e-6f ? result / norm : 0f;
        }

        /// <summary>
        /// Ridged multifractal. Distinct from Fractal(NoiseType.Ridged) in that
        /// each octave's amplitude is additionally weighted by the previous
        /// octave's value, which is what concentrates detail onto the ridge
        /// crests and leaves the valleys smooth.
        /// </summary>
        public float RidgedMultifractal(float x, float z, float size, int iterations = 6,
                                        float detail = 0.5f, float sharpness = 2f,
                                        float weightFalloff = 0.8f)
        {
            if (size < 1e-6f) return 0f;
            if (iterations < 1) iterations = 1;

            float result = 0f;
            float freq = size;
            float amp = 1f;
            float weight = 1f;
            float norm = 0f;

            for (int i = 0; i < iterations; i++)
            {
                float signal = Ridged(x / freq, z / freq, sharpness);

                signal *= weight;
                weight = InterpolationMath.Clamp01(signal * weightFalloff);

                result += signal * amp;
                norm += amp;

                freq *= 0.5f;
                amp *= detail;
            }

            return norm > 1e-6f ? result / norm : 0f;
        }
    }
}
