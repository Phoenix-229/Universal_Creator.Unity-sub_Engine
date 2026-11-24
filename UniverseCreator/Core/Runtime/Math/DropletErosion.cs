// -----------------------------------------------------------------------------
//  Universe Creator - Terrain math
//  File:    DropletErosion.cs        (RIV-1b, absorbs R7)
//  Purpose: A PARTICLE (droplet) hydraulic-erosion solver. The shipped
//           Erosion.Hydraulic is a GRID solver - it orders heights, accumulates
//           torrent and cuts - which carves drainage networks but cannot produce the
//           fine rills, undercut banks and alluvial fans that come from following
//           individual water parcels. This is the missing half, and a grep of the
//           tree confirmed no droplet solver existed.
//
//  WHY CPU AND NOT A COMPUTE SHADER
//  --------------------------------
//  The task was first framed as a GPU path. The river reference notes - and the
//  mobile performance floor - say otherwise: on a mid-tier flagship phone the win
//  comes from the HYBRID COARSE PASS below (erode a downsampled field, then add the
//  smooth delta back at full resolution), not from a compute dispatch. A coarse pass
//  cuts the droplet count by the square of the factor, needs no compute-capability
//  branch, no buffer upload, no read-back stall, and behaves identically on every
//  device - including the ones where compute is absent or throttled. So the solver is
//  plain deterministic C# with a coarse mode, and there is deliberately NO
//  useGpuErosion dial: a toggle that cannot be executed or verified on this floor
//  would be an inert dial, and an unverifiable one.
//
//  FOUR CORRECTIONS OVER THE COMMON REFERENCE IMPLEMENTATION
//  ---------------------------------------------------------
//   1. SPEED UPDATE SIGN. The widely-copied version does
//      speed = sqrt(speed^2 + deltaHeight * gravity) with deltaHeight NEGATIVE going
//      downhill, so a droplet SLOWS as it falls and accelerates climbing - inverted.
//      Here it is -deltaHeight * gravity, clamped at zero before the sqrt.
//   2. SHARED BRUSH LUT. The erosion brush (offset + weight per ring cell) is built
//      ONCE per run and indexed, instead of recomputed per droplet per step.
//   3. HASHED DETERMINISTIC SPAWN. A droplet's start comes from a hash of
//      (seed, index), not from a running RNG, so the result does not depend on
//      evaluation order, is bit-reproducible for a seed, and stays reproducible if
//      the loop is ever split across workers.
//   4. HYBRID COARSE PASS. See above - the mobile-floor lever.
//
//  Off-thread safe: pure arrays and Mathf, no UnityEngine object access.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>Tuning for one droplet-erosion run. All fields have safe defaults.</summary>
    public struct DropletSettings
    {
        /// <summary>How many droplets to simulate (before any coarse-pass reduction).</summary>
        public int droplets;

        /// <summary>Maximum steps one droplet may take before it is retired.</summary>
        public int maxLifetime;

        /// <summary>Radius in cells of the erosion brush. 0 = erode the single cell.</summary>
        public int brushRadius;

        /// <summary>0-1. How much a droplet keeps its old direction versus following the gradient.</summary>
        public float inertia;

        /// <summary>Scales how much sediment a droplet can carry for a given speed/water/slope.</summary>
        public float capacityFactor;

        /// <summary>Floor on carrying capacity, so a droplet on flat ground still deposits sanely.</summary>
        public float minCapacity;

        /// <summary>0-1. Fraction of the excess deposited per step.</summary>
        public float depositSpeed;

        /// <summary>0-1. Fraction of the deficit eroded per step.</summary>
        public float erodeSpeed;

        /// <summary>0-1. Water lost per step.</summary>
        public float evaporateSpeed;

        /// <summary>Acceleration applied to the speed update per unit of descent.</summary>
        public float gravity;

        /// <summary>Starting water volume of a droplet.</summary>
        public float startWater;

        /// <summary>Starting speed of a droplet.</summary>
        public float startSpeed;

        /// <summary>Spawn hash seed. The same seed reproduces the run bit for bit.</summary>
        public int seed;

        /// <summary>
        /// 1 = simulate at full resolution. 2+ = HYBRID COARSE PASS: erode a field
        /// downsampled by this factor and add the (smoothly upsampled) delta back, which
        /// cuts droplet work by about the square of the factor. The mobile-floor lever.
        /// </summary>
        public int coarseFactor;

        /// <summary>Sane defaults, tuned to leave visible rills without destroying a silhouette.</summary>
        public static DropletSettings Default
        {
            get
            {
                return new DropletSettings
                {
                    droplets = 20000,
                    maxLifetime = 30,
                    brushRadius = 2,
                    inertia = 0.05f,
                    capacityFactor = 4f,
                    minCapacity = 0.01f,
                    depositSpeed = 0.3f,
                    erodeSpeed = 0.3f,
                    evaporateSpeed = 0.01f,
                    gravity = 4f,
                    startWater = 1f,
                    startSpeed = 1f,
                    seed = 0,
                    coarseFactor = 1
                };
            }
        }
    }

    /// <summary>
    /// The droplet solver. <see cref="Run"/> erodes a height field in place.
    /// </summary>
    public static class DropletErosion
    {
        // ------------------------------------------------------------------
        //  Brush LUT  (correction 2)
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the erosion brush once per run: the cell offsets within
        /// <paramref name="radius"/> and their normalised weights, falling off linearly
        /// with distance. Weights sum to 1, so spreading an erosion amount over the brush
        /// removes exactly that amount of material and never more.
        /// </summary>
        public static void BuildBrush(int radius, out int[] offsetX, out int[] offsetZ, out float[] weights)
        {
            if (radius < 1)
            {
                offsetX = new int[] { 0 };
                offsetZ = new int[] { 0 };
                weights = new float[] { 1f };
                return;
            }

            int side = radius * 2 + 1;
            int[] ox = new int[side * side];
            int[] oz = new int[side * side];
            float[] w = new float[side * side];

            int n = 0;
            float total = 0f;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    float sqrDst = dx * dx + dz * dz;
                    if (sqrDst > radius * radius) continue;

                    float weight = 1f - Mathf.Sqrt(sqrDst) / radius;
                    if (weight <= 0f) continue;

                    ox[n] = dx; oz[n] = dz; w[n] = weight;
                    total += weight;
                    n++;
                }
            }

            offsetX = new int[n];
            offsetZ = new int[n];
            weights = new float[n];
            float inv = total > 0f ? 1f / total : 0f;
            for (int i = 0; i < n; i++)
            {
                offsetX[i] = ox[i];
                offsetZ[i] = oz[i];
                weights[i] = w[i] * inv;
            }
        }

        // ------------------------------------------------------------------
        //  Hashed spawn  (correction 3)
        // ------------------------------------------------------------------

        /// <summary>
        /// A 32-bit integer hash. Deterministic, order-independent, and well-mixed enough
        /// that consecutive droplet indices land nowhere near each other - which is what
        /// lets a run be reproduced from a seed and, if ever needed, split across workers.
        /// </summary>
        public static uint Hash(uint x)
        {
            x ^= 2747636419u;
            x *= 2654435769u;
            x ^= x >> 16;
            x *= 2654435769u;
            x ^= x >> 16;
            x *= 2654435769u;
            return x;
        }

        private static float Hash01(int seed, int index, int salt)
        {
            uint h = Hash((uint)seed * 747796405u + (uint)index * 2891336453u + (uint)salt * 283923481u);
            return (h & 0xFFFFFF) / 16777216f;   // 24 bits -> [0,1)
        }

        // ------------------------------------------------------------------
        //  Entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Erodes <paramref name="heights"/> (row-major, <paramref name="width"/> x
        /// <paramref name="height"/>, normalised 0-1) in place. Honours a coarse factor,
        /// and probes <paramref name="cancel"/> periodically so a stopped bake leaves a
        /// partially-eroded but still valid field.
        /// </summary>
        public static void Run(float[] heights, int width, int height, DropletSettings s, Func<bool> cancel)
        {
            if (heights == null || width < 3 || height < 3) return;
            if (heights.Length < width * height) return;

            int factor = Mathf.Max(1, s.coarseFactor);
            if (factor == 1)
            {
                Simulate(heights, width, height, s, cancel);
                return;
            }

            // HYBRID COARSE PASS (correction 4). Erode a downsampled copy, then add the
            // difference back at full resolution. The delta is what is upsampled, not the
            // height, so the full-resolution detail the graph already produced survives -
            // only the erosion signal is coarse.
            int cw = Mathf.Max(3, width / factor);
            int ch = Mathf.Max(3, height / factor);

            float[] coarse = Downsample(heights, width, height, cw, ch);
            float[] before = (float[])coarse.Clone();

            DropletSettings cs = s;
            cs.coarseFactor = 1;
            // One droplet on the coarse grid stands for factor^2 cells, so the count scales
            // down with the area - that is where the mobile saving actually comes from.
            cs.droplets = Mathf.Max(1, s.droplets / (factor * factor));
            Simulate(coarse, cw, ch, cs, cancel);

            for (int i = 0; i < coarse.Length; i++) before[i] = coarse[i] - before[i];
            AddUpsampled(heights, width, height, before, cw, ch);
        }

        private static void Simulate(float[] map, int width, int height, DropletSettings s, Func<bool> cancel)
        {
            int[] brushX, brushZ;
            float[] brushW;
            BuildBrush(Mathf.Max(0, s.brushRadius), out brushX, out brushZ, out brushW);

            int lifetime = Mathf.Clamp(s.maxLifetime, 1, 256);
            int count = Mathf.Max(0, s.droplets);
            float inertia = Mathf.Clamp01(s.inertia);
            float evaporate = Mathf.Clamp01(s.evaporateSpeed);
            float depositSpeed = Mathf.Clamp01(s.depositSpeed);
            float erodeSpeed = Mathf.Clamp01(s.erodeSpeed);

            for (int d = 0; d < count; d++)
            {
                // Cancellation is probed per droplet rather than per step: a droplet is a few
                // dozen float ops, so a per-step probe would cost more than the work.
                if ((d & 255) == 0 && cancel != null && cancel()) return;

                // Hashed spawn, inset by one cell so the first bilinear read is in bounds.
                float posX = 1f + Hash01(s.seed, d, 1) * (width - 3f);
                float posZ = 1f + Hash01(s.seed, d, 2) * (height - 3f);

                float dirX = 0f, dirZ = 0f;
                float speed = Mathf.Max(0f, s.startSpeed);
                float water = Mathf.Max(0f, s.startWater);
                float sediment = 0f;

                for (int step = 0; step < lifetime; step++)
                {
                    int nodeX = (int)posX;
                    int nodeZ = (int)posZ;
                    float offX = posX - nodeX;
                    float offZ = posZ - nodeZ;

                    float gradX, gradZ, oldHeight;
                    SampleHeightAndGradient(map, width, height, nodeX, nodeZ, offX, offZ,
                                            out oldHeight, out gradX, out gradZ);

                    // Blend momentum with the downhill gradient, then renormalise so a step is
                    // one cell regardless of how steep the slope is.
                    dirX = dirX * inertia - gradX * (1f - inertia);
                    dirZ = dirZ * inertia - gradZ * (1f - inertia);

                    float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
                    if (len < 1e-6f) break;     // a flat pit with no momentum: the droplet is done
                    dirX /= len; dirZ /= len;

                    posX += dirX;
                    posZ += dirZ;

                    // Left the field (or reached the border where a bilinear read would be
                    // out of bounds): retire the droplet and drop nothing, so a border row
                    // cannot accumulate a ridge of deposited sediment.
                    if (posX < 1f || posX >= width - 2f || posZ < 1f || posZ >= height - 2f) break;

                    float newHeight = SampleHeight(map, width, posX, posZ);
                    float deltaHeight = newHeight - oldHeight;

                    // Carrying capacity: more for a fast, wet droplet running down a steep slope.
                    float capacity = Mathf.Max(-deltaHeight * speed * water * s.capacityFactor,
                                               s.minCapacity);

                    if (sediment > capacity || deltaHeight > 0f)
                    {
                        // DEPOSIT. Running uphill deposits at most enough to fill the step it is
                        // climbing (which is what forms a fan at the foot of a slope rather than
                        // a bump on the far side); otherwise it sheds the excess over capacity.
                        float amount = (deltaHeight > 0f)
                            ? Mathf.Min(deltaHeight, sediment)
                            : (sediment - capacity) * depositSpeed;

                        if (amount > 0f)
                        {
                            sediment -= amount;
                            DepositBilinear(map, width, height, nodeX, nodeZ, offX, offZ, amount);
                        }
                    }
                    else
                    {
                        // ERODE. Never take more than the height step itself, so a droplet cannot
                        // dig a hole deeper than the descent that fed it.
                        float amount = Mathf.Min((capacity - sediment) * erodeSpeed, -deltaHeight);
                        if (amount > 0f)
                        {
                            amount = ErodeBrush(map, width, height, nodeX, nodeZ,
                                                brushX, brushZ, brushW, amount);
                            sediment += amount;
                        }
                    }

                    // CORRECTION 1: descending (deltaHeight < 0) ACCELERATES the droplet.
                    // Clamped before the root so a climb cannot produce a NaN.
                    speed = Mathf.Sqrt(Mathf.Max(0f, speed * speed + (-deltaHeight) * s.gravity));
                    water *= (1f - evaporate);
                    if (water <= 1e-5f) break;
                }
            }
        }

        // ------------------------------------------------------------------
        //  Sampling / scatter helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Bilinear height at the droplet's position plus the surface gradient there.
        /// The gradient is the analytic derivative of the bilinear patch (not a finite
        /// difference of neighbours), so the direction a droplet turns is consistent with
        /// the height it is standing on - mixing the two is what makes droplets jitter
        /// across cell boundaries.
        /// </summary>
        private static void SampleHeightAndGradient(float[] map, int width, int height,
            int nodeX, int nodeZ, float offX, float offZ,
            out float outHeight, out float gradX, out float gradZ)
        {
            int i = nodeZ * width + nodeX;

            float h00 = map[i];                  // (x,   z)
            float h10 = map[i + 1];              // (x+1, z)
            float h01 = map[i + width];          // (x,   z+1)
            float h11 = map[i + width + 1];      // (x+1, z+1)

            // d/dx and d/dz of the bilinear patch at (offX, offZ).
            gradX = (h10 - h00) * (1f - offZ) + (h11 - h01) * offZ;
            gradZ = (h01 - h00) * (1f - offX) + (h11 - h10) * offX;

            outHeight = h00 * (1f - offX) * (1f - offZ) + h10 * offX * (1f - offZ)
                      + h01 * (1f - offX) * offZ + h11 * offX * offZ;
        }

        private static float SampleHeight(float[] map, int width, float x, float z)
        {
            int x0 = (int)x, z0 = (int)z;
            float fx = x - x0, fz = z - z0;
            int i = z0 * width + x0;

            float h00 = map[i];
            float h10 = map[i + 1];
            float h01 = map[i + width];
            float h11 = map[i + width + 1];

            return h00 * (1f - fx) * (1f - fz) + h10 * fx * (1f - fz)
                 + h01 * (1f - fx) * fz + h11 * fx * fz;
        }

        private static void DepositBilinear(float[] map, int width, int height,
            int nodeX, int nodeZ, float offX, float offZ, float amount)
        {
            int i = nodeZ * width + nodeX;
            map[i] += amount * (1f - offX) * (1f - offZ);
            map[i + 1] += amount * offX * (1f - offZ);
            map[i + width] += amount * (1f - offX) * offZ;
            map[i + width + 1] += amount * offX * offZ;
        }

        /// <summary>
        /// Removes <paramref name="amount"/> spread over the brush, and returns how much was
        /// ACTUALLY removed - cells clipped at zero give back less, and returning the real
        /// figure is what keeps the droplet's sediment ledger honest (it cannot pick up
        /// material that was not there).
        /// </summary>
        private static float ErodeBrush(float[] map, int width, int height, int nodeX, int nodeZ,
            int[] brushX, int[] brushZ, float[] brushW, float amount)
        {
            float removed = 0f;
            for (int b = 0; b < brushW.Length; b++)
            {
                int cx = nodeX + brushX[b];
                int cz = nodeZ + brushZ[b];
                if (cx < 0 || cx >= width || cz < 0 || cz >= height) continue;

                int i = cz * width + cx;
                float want = amount * brushW[b];
                float take = map[i] >= want ? want : Mathf.Max(0f, map[i]);
                map[i] -= take;
                removed += take;
            }
            return removed;
        }

        // ------------------------------------------------------------------
        //  Coarse-pass resampling
        // ------------------------------------------------------------------

        /// <summary>Box-average down to (cw x ch).</summary>
        public static float[] Downsample(float[] src, int width, int height, int cw, int ch)
        {
            float[] dst = new float[cw * ch];
            for (int z = 0; z < ch; z++)
            {
                int z0 = z * height / ch;
                int z1 = Mathf.Max(z0 + 1, (z + 1) * height / ch);
                for (int x = 0; x < cw; x++)
                {
                    int x0 = x * width / cw;
                    int x1 = Mathf.Max(x0 + 1, (x + 1) * width / cw);

                    float sum = 0f; int n = 0;
                    for (int zz = z0; zz < z1 && zz < height; zz++)
                        for (int xx = x0; xx < x1 && xx < width; xx++)
                        { sum += src[zz * width + xx]; n++; }

                    dst[z * cw + x] = n > 0 ? sum / n : 0f;
                }
            }
            return dst;
        }

        /// <summary>
        /// Adds a coarse delta field back at full resolution, bilinearly interpolated so the
        /// erosion signal arrives smooth rather than as visible coarse-cell steps.
        /// </summary>
        public static void AddUpsampled(float[] dst, int width, int height,
                                        float[] delta, int cw, int ch)
        {
            for (int z = 0; z < height; z++)
            {
                float cz = ch > 1 ? (z * (ch - 1f)) / Mathf.Max(1, height - 1) : 0f;
                int z0 = Mathf.Clamp((int)cz, 0, ch - 1);
                int z1 = Mathf.Min(z0 + 1, ch - 1);
                float fz = cz - z0;

                for (int x = 0; x < width; x++)
                {
                    float cx = cw > 1 ? (x * (cw - 1f)) / Mathf.Max(1, width - 1) : 0f;
                    int x0 = Mathf.Clamp((int)cx, 0, cw - 1);
                    int x1 = Mathf.Min(x0 + 1, cw - 1);
                    float fx = cx - x0;

                    float d00 = delta[z0 * cw + x0];
                    float d10 = delta[z0 * cw + x1];
                    float d01 = delta[z1 * cw + x0];
                    float d11 = delta[z1 * cw + x1];

                    float d = d00 * (1f - fx) * (1f - fz) + d10 * fx * (1f - fz)
                            + d01 * (1f - fx) * fz + d11 * fx * fz;

                    dst[z * width + x] += d;
                }
            }
        }
    }
}
