// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    GeologyModifierNodes.cs
//  Purpose: Seven modifiers that shape rock rather than filter a signal:
//           droplet erosion, glacial carving, slope remapping, warp application,
//           rock strata, fault displacement, and the coastal shelf.
//
//  All of them run inside ModifierNode's ghost-margin wrapper, so each reads a
//  padded tile and the padding is discarded afterwards - neighbouring chunks stay
//  seamless even where a droplet or a glacier crosses the boundary.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    /// <summary>
    /// Musgrave/Olsen droplet erosion. Individual raindrops walk downhill picking
    /// up and dropping sediment, which cuts dendritic channels that a diffusion
    /// solver cannot produce - a diffusion pass smooths everything equally, while
    /// a droplet concentrates its work where water actually collects.
    /// </summary>
    [System.Serializable]
    public class DropletErosionNode : ModifierNode
    {
        public override string NodeName { get { return "Droplet Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Particle-based hydraulic erosion. Cuts dendritic drainage channels."; } }

        /// <summary>Droplets released per run. Cost scales linearly.</summary>
        public int raindrops = 30000;

        public int seed = 6060;

        /// <summary>Droplet lifetime in steps before it evaporates.</summary>
        public int maxLifetime = 48;

        /// <summary>Fraction of velocity retained per step, 0..1.</summary>
        public float inertia = 0.05f;

        /// <summary>Sediment a droplet can hold per unit of speed and water.</summary>
        public float capacity = 4f;

        /// <summary>Fraction of the excess a droplet erodes per step, 0..1.</summary>
        public float erodeRate = 0.3f;

        /// <summary>Fraction of the surplus a droplet deposits per step, 0..1.</summary>
        public float depositRate = 0.3f;

        /// <summary>Fraction of water lost per step, 0..1.</summary>
        public float evaporation = 0.02f;

        /// <summary>Downhill acceleration.</summary>
        public float gravity = 4f;

        /// <summary>Radius the erosion is spread over, in pixels. 0 is a single cell.</summary>
        public int erosionRadius = 2;

        /// <summary>Minimum slope used in the capacity term, so flats still carry sediment.</summary>
        public float minSlope = 0.01f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            if (resX < 4 || resZ < 4) return;

            // Seeding off the tile's world position rather than its chunk index
            // keeps the same droplets landing on the same ground when a chunk is
            // regenerated at a different grid slot.
            int posSeed = Mathf.RoundToInt(m.worldPos.x) * 73856093
                        ^ Mathf.RoundToInt(m.worldPos.z) * 19349663;
            Noise rng = new Noise(seed ^ posSeed);

            int drops = Mathf.Clamp(raindrops, 0, 500000);
            int life = Mathf.Clamp(maxLifetime, 1, 256);
            float safeInertia = Mathf.Clamp01(inertia);
            float safeErode = Mathf.Clamp01(erodeRate);
            float safeDeposit = Mathf.Clamp01(depositRate);
            float safeEvap = Mathf.Clamp01(evaporation);

            float[] arr = m.arr;

            // Precomputed brush weights, so erosion is spread over a disc rather
            // than punched into one cell - a single-cell cut makes spikes.
            int radius = Mathf.Clamp(erosionRadius, 0, 8);
            int brushCount;
            int[] brushDx, brushDz;
            float[] brushW;
            BuildBrush(radius, out brushDx, out brushDz, out brushW, out brushCount);

            for (int d = 0; d < drops; d++)
            {
                if (stop != null && (d & 511) == 0 && stop.StopRequested) return;

                float px = 1f + rng.Random(d, 0) * (resX - 3f);
                float pz = 1f + rng.Random(0, d) * (resZ - 3f);

                float dirX = 0f, dirZ = 0f;
                float speed = 1f;
                float water = 1f;
                float sediment = 0f;

                for (int step = 0; step < life; step++)
                {
                    int cx = (int)px;
                    int cz = (int)pz;
                    if (cx < 1 || cz < 1 || cx >= resX - 2 || cz >= resZ - 2) break;

                    float fx = px - cx;
                    float fz = pz - cz;

                    float gradX, gradZ, height;
                    SampleGradient(arr, resX, cx, cz, fx, fz, out gradX, out gradZ, out height);

                    dirX = dirX * safeInertia - gradX * (1f - safeInertia);
                    dirZ = dirZ * safeInertia - gradZ * (1f - safeInertia);

                    float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
                    if (len < 1e-6f) break;

                    dirX /= len;
                    dirZ /= len;

                    px += dirX;
                    pz += dirZ;

                    int nx = (int)px;
                    int nz = (int)pz;
                    if (nx < 1 || nz < 1 || nx >= resX - 2 || nz >= resZ - 2) break;

                    float newHeight;
                    float ngX, ngZ;
                    SampleGradient(arr, resX, nx, nz, px - nx, pz - nz, out ngX, out ngZ, out newHeight);

                    float deltaH = newHeight - height;

                    float carry = Mathf.Max(-deltaH, minSlope) * speed * water * capacity;

                    if (sediment > carry || deltaH > 0f)
                    {
                        // Uphill: drop only enough to fill the rise, so a droplet
                        // cannot build a hill higher than the one it climbed.
                        float amount = deltaH > 0f
                            ? Mathf.Min(deltaH, sediment)
                            : (sediment - carry) * safeDeposit;

                        sediment -= amount;
                        Deposit(arr, resX, cx, cz, fx, fz, amount);
                    }
                    else
                    {
                        float amount = Mathf.Min((carry - sediment) * safeErode, -deltaH);
                        sediment += ErodeBrush(arr, resX, resZ, cx, cz,
                                               brushDx, brushDz, brushW, brushCount, amount);
                    }

                    speed = Mathf.Sqrt(Mathf.Max(0f, speed * speed + (-deltaH) * gravity));
                    water *= (1f - safeEvap);
                    if (water < 0.001f) break;
                }
            }

            m.Clamp01();
        }

        private static void BuildBrush(int radius, out int[] dx, out int[] dz, out float[] w, out int count)
        {
            if (radius <= 0)
            {
                dx = new int[] { 0 };
                dz = new int[] { 0 };
                w = new float[] { 1f };
                count = 1;
                return;
            }

            int side = radius * 2 + 1;
            dx = new int[side * side];
            dz = new int[side * side];
            w = new float[side * side];
            count = 0;
            float total = 0f;

            for (int z = -radius; z <= radius; z++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    float dist = Mathf.Sqrt(x * x + z * z);
                    if (dist > radius) continue;

                    float weight = 1f - dist / radius;
                    dx[count] = x;
                    dz[count] = z;
                    w[count] = weight;
                    total += weight;
                    count++;
                }
            }

            if (total <= 1e-6f) return;
            for (int i = 0; i < count; i++) w[i] /= total;
        }

        private static void SampleGradient(float[] arr, int resX, int cx, int cz, float fx, float fz,
                                           out float gradX, out float gradZ, out float height)
        {
            int i = cz * resX + cx;

            float h00 = arr[i];
            float h10 = arr[i + 1];
            float h01 = arr[i + resX];
            float h11 = arr[i + resX + 1];

            gradX = (h10 - h00) * (1f - fz) + (h11 - h01) * fz;
            gradZ = (h01 - h00) * (1f - fx) + (h11 - h10) * fx;

            height = h00 * (1f - fx) * (1f - fz) + h10 * fx * (1f - fz)
                   + h01 * (1f - fx) * fz + h11 * fx * fz;
        }

        private static void Deposit(float[] arr, int resX, int cx, int cz, float fx, float fz, float amount)
        {
            int i = cz * resX + cx;
            arr[i] += amount * (1f - fx) * (1f - fz);
            arr[i + 1] += amount * fx * (1f - fz);
            arr[i + resX] += amount * (1f - fx) * fz;
            arr[i + resX + 1] += amount * fx * fz;
        }

        private static float ErodeBrush(float[] arr, int resX, int resZ, int cx, int cz,
                                        int[] dx, int[] dz, float[] w, int count, float amount)
        {
            float removed = 0f;

            for (int i = 0; i < count; i++)
            {
                int x = cx + dx[i];
                int z = cz + dz[i];
                if (x < 0 || z < 0 || x >= resX || z >= resZ) continue;

                int index = z * resX + x;
                float take = amount * w[i];

                // Never cut below zero, and return only what was actually removed
                // so sediment is conserved.
                if (take > arr[index]) take = arr[index];

                arr[index] -= take;
                removed += take;
            }

            return removed;
        }
    }

    /// <summary>
    /// Glacial carving. Ice fills valleys to a thickness set by flow accumulation
    /// and planes the cross-section toward a parabola, which is the U-shape that
    /// distinguishes a glaciated valley from a river's V.
    /// </summary>
    [System.Serializable]
    public class GlacialCarveNode : ModifierNode
    {
        public override string NodeName { get { return "Glacial Carve"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts U-shaped valleys above the snowline along flow accumulation."; } }

        /// <summary>Height above which ice forms, 0..1.</summary>
        public float snowline = 0.6f;

        /// <summary>Softens the snowline so ice thins out rather than stopping flat.</summary>
        public float snowlineFalloff = 0.12f;

        /// <summary>Maximum depth removed from a valley floor, 0..1.</summary>
        public float carveDepth = 0.06f;

        /// <summary>Half-width of the carved cross-section, in pixels.</summary>
        public int valleyWidth = 12;

        /// <summary>How strongly the profile is pushed toward a parabola, 0..1.</summary>
        public float uShape = 0.7f;

        /// <summary>Flow accumulation needed before ice carries enough load to cut.</summary>
        public float flowThreshold = 0.15f;

        /// <summary>Passes. More gives deeper, wider troughs.</summary>
        public int iterations = 3;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            if (resX < 8 || resZ < 8) return;

            int passes = Mathf.Clamp(iterations, 1, 12);
            int width = Mathf.Clamp(valleyWidth, 1, 64);

            // Flow accumulation marks where ice would actually collect. Reusing the
            // hydraulic ordering pass keeps this consistent with the Flow Map node.
            Matrix flow = m.Blank();
            Erosion.HydraulicSettings settings = new Erosion.HydraulicSettings
            {
                iterations = 1,
                durability = 1f,
                erosionAmount = 0f,
                sedimentAmount = 0f,
                fluidityIterations = 1,
                relax = 0f
            };

            System.Func<bool> isCancelled = null;
            if (stop != null) isCancelled = delegate { return stop.StopRequested; };

            Erosion.Hydraulic(m.Copy(), settings, null, flow, null, isCancelled);
            if (stop != null && stop.StopRequested) return;

            float flowMax = 0f;
            for (int i = 0; i < flow.arr.Length; i++)
                if (flow.arr[i] > flowMax) flowMax = flow.arr[i];

            float invFlowMax = flowMax > 1e-6f ? 1f / flowMax : 0f;
            float safeFalloff = Mathf.Max(1e-4f, snowlineFalloff);

            for (int pass = 0; pass < passes; pass++)
            {
                if (stop != null && stop.StopRequested) return;

                float[] src = (float[])m.arr.Clone();

                for (int z = 0; z < resZ; z++)
                {
                    if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                    int row = z * resX;

                    for (int x = 0; x < resX; x++)
                    {
                        int i = row + x;

                        float ice = Mathf.Clamp01((src[i] - snowline) / safeFalloff);
                        if (ice <= 0.001f) continue;

                        float f = Mathf.Clamp01(flow.arr[i] * invFlowMax);
                        if (f < flowThreshold) continue;

                        float load = ice * Mathf.Clamp01((f - flowThreshold) / Mathf.Max(1e-4f, 1f - flowThreshold));

                        // Parabolic target: the local minimum plus a quadratic rise
                        // over the valley half-width. Blending toward it is what
                        // planes the walls without flattening the floor.
                        float floor = LocalMin(src, resX, resZ, x, z, width);
                        float dist = Mathf.Clamp01(NormalisedOffset(src, resX, resZ, x, z, width, floor));
                        float target = floor + dist * dist * carveDepth * 2f;

                        float carved = Mathf.Lerp(src[i], target, uShape * load);
                        carved -= carveDepth * load * (1f - dist * dist);

                        m.arr[i] = Mathf.Clamp01(Mathf.Min(src[i], carved));
                    }
                }
            }
        }

        private static float LocalMin(float[] src, int resX, int resZ, int cx, int cz, int radius)
        {
            float min = float.MaxValue;
            int x0 = Mathf.Max(0, cx - radius);
            int x1 = Mathf.Min(resX - 1, cx + radius);
            int z0 = Mathf.Max(0, cz - radius);
            int z1 = Mathf.Min(resZ - 1, cz + radius);

            for (int z = z0; z <= z1; z++)
            {
                int row = z * resX;
                for (int x = x0; x <= x1; x++)
                {
                    float v = src[row + x];
                    if (v < min) min = v;
                }
            }

            return min == float.MaxValue ? 0f : min;
        }

        // How far this pixel sits above the trough floor, normalised by the local
        // relief. 0 is the valley bottom, 1 is the shoulder.
        private static float NormalisedOffset(float[] src, int resX, int resZ,
                                              int cx, int cz, int radius, float floor)
        {
            float max = 0f;
            int x0 = Mathf.Max(0, cx - radius);
            int x1 = Mathf.Min(resX - 1, cx + radius);
            int z0 = Mathf.Max(0, cz - radius);
            int z1 = Mathf.Min(resZ - 1, cz + radius);

            for (int z = z0; z <= z1; z++)
            {
                int row = z * resX;
                for (int x = x0; x <= x1; x++)
                {
                    float v = src[row + x];
                    if (v > max) max = v;
                }
            }

            float relief = max - floor;
            if (relief < 1e-6f) return 0f;

            return (src[cz * resX + cx] - floor) / relief;
        }
    }

    /// <summary>
    /// Redistributes the slope-angle histogram through a curve. Terrain from
    /// stacked noise is dominated by mid-range slopes; real ground is bimodal -
    /// mostly flat, occasionally cliff. This remaps toward that distribution
    /// without touching the landform's large-scale shape.
    /// </summary>
    [System.Serializable]
    public class SlopeRemapCurveNode : ModifierNode, IPreparedGraphInputHash
    {
        public override string NodeName { get { return "Slope Remap Curve"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Reshapes the slope-angle distribution toward flats and cliffs."; } }

        /// <summary>Maps input slope 0..1 (0-90 degrees) to output slope 0..1.</summary>
        public AnimationCurve slopeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        /// <summary>Blend against the untouched input, 0..1.</summary>
        public float strength = 1f;

        /// <summary>Solver passes. More converges harder on the target distribution.</summary>
        public int iterations = 4;

        // AnimationCurve.Evaluate is main-thread only, so it is baked to a LUT
        // before the worker starts.
        [System.NonSerialized] private float[] lut;

        /// <summary>UC-T01-07: fingerprints the prepared slope remap rather than a live curve.</summary>
        /// <returns>Deterministic fingerprint; call after main-thread preparation.</returns>
        public ulong GetPreparedInputHash() { return PreparedRasterInputs.Hash(256, 1, lut); }

        public override void PrepareMainThread()
        {
            const int n = 256;
            lut = new float[n];

            AnimationCurve c = slopeCurve;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                lut[i] = c != null ? Mathf.Clamp01(c.Evaluate(t)) : t;
            }
        }

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float[] table = lut;
            if (table == null || table.Length < 2) return;

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            if (resX < 3 || resZ < 3) return;

            Vector2 ps = m.PixelSize;
            if (ps.x <= 1e-9f || ps.y <= 1e-9f) return;

            float amount = Mathf.Clamp01(strength);
            int passes = Mathf.Clamp(iterations, 1, 16);
            float worldHeight = Mathf.Max(1e-4f, m.worldHeight);

            for (int pass = 0; pass < passes; pass++)
            {
                if (stop != null && stop.StopRequested) return;

                float[] src = (float[])m.arr.Clone();

                for (int z = 1; z < resZ - 1; z++)
                {
                    if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                    int row = z * resX;

                    for (int x = 1; x < resX - 1; x++)
                    {
                        int i = row + x;

                        float dx = (src[i + 1] - src[i - 1]) * worldHeight / (2f * ps.x);
                        float dz = (src[i + resX] - src[i - resX]) * worldHeight / (2f * ps.y);

                        float grad = Mathf.Sqrt(dx * dx + dz * dz);
                        float angle01 = Mathf.Atan(grad) / (Mathf.PI * 0.5f);

                        float target = Sample(table, angle01);

                        // Scale the local relief by the ratio of desired to actual
                        // slope. Applied to the deviation from the neighbourhood
                        // mean, so the landform's mean elevation is preserved.
                        float ratio = angle01 > 1e-4f ? target / angle01 : 1f;
                        ratio = Mathf.Clamp(ratio, 0.2f, 5f);

                        float mean = (src[i + 1] + src[i - 1] + src[i + resX] + src[i - resX]) * 0.25f;
                        float scaled = mean + (src[i] - mean) * ratio;

                        m.arr[i] = Mathf.Clamp01(Mathf.Lerp(src[i], scaled, amount));
                    }
                }
            }
        }

        private static float Sample(float[] table, float t)
        {
            t = Mathf.Clamp01(t) * (table.Length - 1);
            int i0 = (int)t;
            int i1 = Mathf.Min(table.Length - 1, i0 + 1);
            return Mathf.Lerp(table[i0], table[i1], t - i0);
        }
    }
}
