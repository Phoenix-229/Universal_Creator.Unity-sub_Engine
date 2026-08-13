// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    HydrologyNodes.cs
//  Purpose: River tracing and carving, lakes, ocean level, watershed basins,
//           wetlands, and submarine terrain.
//
//  All seven sit on the shared Hydrology solver, so they agree with each other:
//  the lake surface a river drains into is the same spill elevation Lake Fill
//  floods to, and the basin a wetland sits in is the one Watershed segments.
//  Deriving each independently is how you get rivers that miss their lakes.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Traces connected river channels and emits them as a width-graded mask.
    /// A raw accumulation threshold gives speckle; walking each channel downstream
    /// gives continuous courses that widen as tributaries join.
    /// </summary>
    [System.Serializable]
    public class RiverNetworkTraceNode : SelectorNode
    {
        public override string NodeName { get { return "River Network Trace"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Traces connected river courses from flow accumulation."; } }

        /// <summary>Upstream area a channel needs before it counts as a river.</summary>
        public float channelThreshold = 800f;

        /// <summary>Channel width at the threshold, in world metres.</summary>
        public float baseWidth = 6f;

        /// <summary>Extra width as discharge grows. 0.5 is the natural exponent.</summary>
        public float widthExponent = 0.5f;

        /// <summary>Cap on channel width, in world metres.</summary>
        public float maxWidth = 60f;

        /// <summary>Rivers stop at this elevation. Below it they have reached the sea.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Most channels traced per tile. Guards pathological heightfields.</summary>
        public int maxChannels = 512;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            int count = resX * resZ;
            if (count == 0) return;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            float[] filled = Hydrology.FillDepressions(src.arr, resX, resZ, 1e-6f, cancel);
            if (stop != null && stop.StopRequested) return;

            int[] dir = Hydrology.FlowDirection(filled, resX, resZ, cancel);
            float[] accum = Hydrology.FlowAccumulation(dir, resX, resZ, null, cancel);
            if (stop != null && stop.StopRequested) return;

            float threshold = Mathf.Max(1f, channelThreshold);
            List<List<int>> channels = Hydrology.TraceChannels(
                dir, accum, resX, resZ, threshold, Mathf.Max(1, maxChannels), cancel);

            if (stop != null && stop.StopRequested) return;

            Vector2 ps = src.PixelSize;
            float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);
            float sea = Mathf.Clamp01(seaLevel);
            float cap = Mathf.Max(baseWidth, maxWidth);

            for (int c = 0; c < channels.Count; c++)
            {
                List<int> channel = channels[c];

                for (int p = 0; p < channel.Count; p++)
                {
                    int i = channel[p];
                    if (src.arr[i] < sea) break;

                    // Hydraulic geometry: width scales with the square root of
                    // discharge, so a trunk river is wide without being absurd.
                    float discharge = accum[i] / threshold;
                    float width = baseWidth * Mathf.Pow(Mathf.Max(1f, discharge), widthExponent);
                    width = Mathf.Min(width, cap);

                    StampDisc(mask, resX, resZ, i % resX, i / resX, width / mpp);
                }
            }
        }

        private void StampDisc(Matrix mask, int resX, int resZ, int px, int pz, float radiusPx)
        {
            float r = Mathf.Max(0.5f, radiusPx * 0.5f);
            int span = Mathf.CeilToInt(r);

            int x0 = Mathf.Max(0, px - span);
            int x1 = Mathf.Min(resX - 1, px + span);
            int z0 = Mathf.Max(0, pz - span);
            int z1 = Mathf.Min(resZ - 1, pz + span);

            float invR = 1f / r;

            for (int z = z0; z <= z1; z++)
            {
                float dz = (z - pz) * invR;
                int row = z * resX;

                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x - px) * invR;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d > 1f) continue;

                    float v = 1f - InterpolationMath.SmoothStep(d);
                    if (v > mask.arr[row + x]) mask.arr[row + x] = v;
                }
            }
        }
    }

    /// <summary>
    /// Cuts the river channel into the heightfield. Separate from the trace so a
    /// user can carve one network and texture another, and so the carve can be
    /// masked without recomputing drainage.
    /// </summary>
    [System.Serializable]
    public class RiverCarveNode : Generator
    {
        public override string NodeName { get { return "River Carve"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts a channel profile into the terrain along a river mask."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("River Mask", PortType.Matrix, PortDirection.Input),
            new Port("Lake Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>Channel depth at full mask, as a fraction of world height.</summary>
        public float depth = 0.012f;

        /// <summary>Width of the banks either side of the channel, in metres.</summary>
        public float bankWidth = 50f;

        /// <summary>Cross-section shape. 1 is V-shaped, 2+ approaches a flat bed.</summary>
        public float profilePower = 1.6f;

        /// <summary>Flattens the channel floor so the river bed does not undulate.</summary>
        public float bedFlattening = 0.85f;

        /// <summary>Raises a levee alongside the channel, as real rivers deposit.</summary>
        public float leveeHeight;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix river = GetInputMatrix(tile, 1, stop);
            if (river == null) return new Matrix(src);
            if (stop != null && stop.StopRequested) return null;

            Matrix lakeMask = GetInputMatrix(tile, 2, stop);

            Matrix dst = new Matrix(src);

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            Vector2 ps = src.PixelSize;
            float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);

            // Bank width in pixels sets the blur sigma used to find the local river
            // grade: the bed follows the smoothed valley floor, not every bump.
            float sigma = Mathf.Clamp(bankWidth / mpp, 0.5f, 32f);
            Matrix smoothed = MatrixOperations.GaussianBlur(src, sigma);

            // Smooth the river mask itself to prevent aliased, zigzag stair-step trenches!
            // A 1-pixel flow accumulation line creates see-saw triangles when carved directly.
            Matrix smoothRiverMask = MatrixOperations.GaussianBlur(river, 1.5f);

            if (stop != null && stop.StopRequested) return null;

            float power = Mathf.Max(0.2f, profilePower);
            float flatten = Mathf.Clamp01(bedFlattening);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    
                    // Do not carve trenches into lakes
                    if (lakeMask != null && lakeMask.arr[i] > 0.05f) continue;
                    
                    // Use the smoothed mask to guarantee a continuous gradient carve without zigzags
                    float w = Mathf.Clamp01(smoothRiverMask.arr[i]);
                    if (w <= 0.001f) continue;

                    // Mask value doubles as distance from the centreline: 1 at the
                    // thalweg, 0 at the bank. Raising it to a power sets the profile.
                    float profile = Mathf.Pow(w, power);

                    // Ensure the riverbed is completely flattened and merged at junctions
                    float bed = Mathf.Lerp(src.arr[i], smoothed.arr[i], flatten * Mathf.Sqrt(w));
                    float cut = bed - depth * profile;

                    float value = Mathf.Min(dst.arr[i], cut);

                    if (leveeHeight > 1e-5f)
                    {
                        // Levees sit on the shoulder: strongest where the mask is
                        // partial, absent in the channel itself.
                        float shoulder = w * (1f - w) * 4f;
                        value += leveeHeight * shoulder;
                    }

                    dst.arr[i] = Mathf.Clamp01(value);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Floods every closed depression to its spill elevation. Port 0 is the water
    /// mask, port 1 the depth, so the texture graph can fade a lake from shallow
    /// shore to deep centre without a second solve.
    /// </summary>
    [System.Serializable]
    public class LakeFillNode : Generator
    {
        public override string NodeName { get { return "Lake Fill"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Floods local minima to their spill elevation. Outputs mask and depth."; } }

        private static readonly Port[] Outs =
        {
            new Port("Lake Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Water Level", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Depressions shallower than this are ignored, 0..1 of world height.</summary>
        public float minDepth = 0.002f;

        /// <summary>Deepest a lake reads as full, used to normalise the depth port.</summary>
        public float depthNormalise = 0.05f;

        /// <summary>Raises every water surface, merging small ponds into one lake.</summary>
        public float extraFill;

        /// <summary>Softness of the shoreline in the mask, 0..1.</summary>
        public float shoreSoftness = 0.15f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            int count = resX * resZ;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            // Zero epsilon: a lake surface is genuinely flat, and here we want it.
            float[] filled = Hydrology.FillDepressions(src.arr, resX, resZ, 0f, cancel);
            if (stop != null && stop.StopRequested) return null;

            float floor = Mathf.Max(0f, minDepth);
            float extra = Mathf.Max(0f, extraFill);
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);
            float soft = Mathf.Clamp01(shoreSoftness);

            for (int i = 0; i < count; i++)
            {
                float level = filled[i] + extra;
                float d = level - src.arr[i];

                if (d < floor) { dst.arr[i] = outputIndex == 2 ? src.arr[i] : 0f; continue; }

                if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(d * norm); continue; }
                if (outputIndex == 2) { dst.arr[i] = Mathf.Clamp01(level); continue; }

                // Mask: fade in over the first slice of depth so the shoreline is
                // not a hard one-pixel edge when it meets a shallow gradient.
                float edge = soft > 1e-4f
                    ? Mathf.Clamp01((d - floor) / (soft * Mathf.Max(1e-4f, depthNormalise)))
                    : 1f;

                dst.arr[i] = InterpolationMath.SmoothStep(edge);
            }

            return dst;
        }
    }

    /// <summary>
    /// Sea level as a mask, with depth and a shoreline band. Trivially a threshold,
    /// but every downstream node wants the same sea level and the same softened
    /// edge, so it belongs in one node rather than six copies of a Height Select.
    /// </summary>
    [System.Serializable]
    public class OceanLevelNode : Generator
    {
        public override string NodeName { get { return "Ocean Level"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Sea level to ocean mask, depth, and a shoreline band."; } }

        private static readonly Port[] Outs =
        {
            new Port("Ocean Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Shoreline", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Sea level, 0..1 of world height.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Vertical width of the shoreline band, 0..1.</summary>
        public float shorelineBand = 0.015f;

        /// <summary>Softness of the ocean mask edge, 0..1.</summary>
        public float edgeSoftness = 0.004f;

        /// <summary>Depth at which the depth port saturates.</summary>
        public float depthNormalise = 0.2f;

        /// <summary>Width of the ocean band in metres from the world edge. 0 disables this limit.</summary>
        public float oceanBandMeters = 200f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            float sea = Mathf.Clamp01(seaLevel);
            float band = Mathf.Max(1e-5f, shorelineBand);
            float soft = Mathf.Max(1e-5f, edgeSoftness);
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);
            float limit = Mathf.Max(0f, oceanBandMeters);

            float worldMinX = tile.fullWorldPos.x;
            float worldMinZ = tile.fullWorldPos.z;
            float worldMaxX = worldMinX + tile.fullWorldSize.x;
            float worldMaxZ = worldMinZ + tile.fullWorldSize.z;

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            float mppX = src.PixelSize.x;
            float mppZ = src.PixelSize.y;
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                
                float wz = originZ + z * mppZ;
                float dz = Mathf.Min(wz - worldMinZ, worldMaxZ - wz);
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = rowStart + x;
                    float h = src.arr[i];

                    if (limit > 0f)
                    {
                        float wx = originX + x * mppX;
                        float dx = Mathf.Min(wx - worldMinX, worldMaxX - wx);
                        if (Mathf.Min(dx, dz) > limit)
                        {
                            dst.arr[i] = 0f;
                            continue;
                        }
                    }

                    if (outputIndex == 1)
                    {
                    dst.arr[i] = Mathf.Clamp01((sea - h) * norm);
                    continue;
                }

                if (outputIndex == 2)
                {
                    // Peaks at the waterline and falls off both ways: this is the
                    // beach, not "everything below sea level".
                    float dist = Mathf.Abs(h - sea) / band;
                    dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(dist));
                    continue;
                }

                dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01((h - sea) / soft + 0.5f));
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Segments the tile into drainage basins. Port 0 is a basin id for texturing
    /// or biome variation, port 1 marks the divides - the ridge lines that separate
    /// one catchment from the next.
    /// </summary>
    [System.Serializable]
    public class WatershedBasinSegmentNode : Generator
    {
        public override string NodeName { get { return "Watershed Basin Segment"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Segments terrain into drainage basins; also emits the divides."; } }

        private static readonly Port[] Outs =
        {
            new Port("Basin Id", PortType.Matrix, PortDirection.Output, true),
            new Port("Divides", PortType.Matrix, PortDirection.Output, true),
            new Port("Basin Size", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Merges basins smaller than this fraction of the tile into neighbours.</summary>
        public float minBasinFraction = 0.002f;

        /// <summary>Randomises the emitted ids so adjacent basins contrast.</summary>
        public bool scrambleIds = true;

        public int seed = 616;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            int count = resX * resZ;
            if (count == 0) return dst;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            float[] filled = Hydrology.FillDepressions(src.arr, resX, resZ, 1e-6f, cancel);
            int[] dir = Hydrology.FlowDirection(filled, resX, resZ, cancel);
            if (stop != null && stop.StopRequested) return null;

            int basinCount;
            int[] label = Hydrology.Basins(dir, resX, resZ, out basinCount, cancel);
            if (stop != null && stop.StopRequested) return null;
            if (basinCount <= 0) return dst;

            int[] sizes = new int[basinCount];
            for (int i = 0; i < count; i++)
                if (label[i] >= 0) sizes[label[i]]++;

            if (outputIndex == 2)
            {
                float invCount = 1f / count;
                float maxFraction = 0f;
                for (int b = 0; b < basinCount; b++)
                {
                    float f = sizes[b] * invCount;
                    if (f > maxFraction) maxFraction = f;
                }

                float invMax = maxFraction > 1e-6f ? 1f / maxFraction : 0f;
                for (int i = 0; i < count; i++)
                    dst.arr[i] = label[i] >= 0 ? Mathf.Clamp01(sizes[label[i]] * invCount * invMax) : 0f;

                return dst;
            }

            if (outputIndex == 1)
            {
                // A divide is where two different basins touch.
                for (int z = 0; z < resZ; z++)
                {
                    if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                    int row = z * resX;
                    for (int x = 0; x < resX; x++)
                    {
                        int i = row + x;
                        int mine = label[i];
                        bool divide = false;

                        for (int n = 0; n < 4 && !divide; n++)
                        {
                            int nx = x + (n == 0 ? -1 : n == 1 ? 1 : 0);
                            int nz = z + (n == 2 ? -1 : n == 3 ? 1 : 0);
                            if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;
                            if (label[nz * resX + nx] != mine) divide = true;
                        }

                        dst.arr[i] = divide ? 1f : 0f;
                    }
                }

                return dst;
            }

            // Basin id, optionally scrambled so neighbouring ids are not adjacent
            // greys - two basins that share a divide should look different.
            int minSize = Mathf.RoundToInt(Mathf.Clamp01(minBasinFraction) * count);
            Noise noise = new Noise(seed);
            float invBasins = basinCount > 1 ? 1f / (basinCount - 1) : 0f;

            for (int i = 0; i < count; i++)
            {
                int b = label[i];
                if (b < 0 || sizes[b] < minSize) { dst.arr[i] = 0f; continue; }

                dst.arr[i] = scrambleIds ? noise.Random(b) : b * invBasins;
            }

            return dst;
        }
    }

    /// <summary>
    /// Wetlands: flat ground, high throughflow, close to standing water. All three
    /// have to hold at once - a flat hilltop is not a marsh and a steep stream bank
    /// does not hold water.
    /// </summary>
    [System.Serializable]
    public class GroundwaterWetlandMaskNode : Generator
    {
        public override string NodeName { get { return "Groundwater Wetland Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Marks marsh ground: low slope, high flow, near standing water."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Water Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        /// <summary>Slope above which ground drains too fast to stay wet, in degrees.</summary>
        public float maxSlope = 4f;

        /// <summary>Upstream area needed before ground stays saturated.</summary>
        public float flowThreshold = 120f;

        /// <summary>How far the influence of standing water reaches, in metres.</summary>
        public float waterProximity = 90f;

        /// <summary>Elevation above local water that still counts as wet, 0..1.</summary>
        public float tableDepth = 0.006f;

        /// <summary>Breaks the edges up so the marsh is not an obvious contour.</summary>
        public float edgeNoise = 0.3f;

        public int seed = 4242;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix water = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            float[] filled = Hydrology.FillDepressions(src.arr, resX, resZ, 1e-6f, cancel);
            int[] dir = Hydrology.FlowDirection(filled, resX, resZ, cancel);
            float[] accum = Hydrology.FlowAccumulation(dir, resX, resZ, null, cancel);
            if (stop != null && stop.StopRequested) return null;

            Matrix slopes = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return null;

            // Proximity to standing water: blurring the water mask is a cheap and
            // stable distance proxy, and it stays smooth across tile edges.
            Matrix nearWater = null;
            if (water != null)
            {
                Vector2 wps = src.PixelSize;
                float wmpp = Mathf.Max(1e-4f, (wps.x + wps.y) * 0.5f);
                float sigma = Mathf.Clamp(waterProximity / wmpp, 0.5f, 48f);
                nearWater = MatrixOperations.GaussianBlur(water, sigma);
            }

            Noise noise = new Noise(seed);
            float slopeLimit = Mathf.Max(0.5f, maxSlope);
            float flowLimit = Mathf.Max(1f, flowThreshold);
            float table = Mathf.Max(1e-5f, tableDepth);
            float noiseAmt = Mathf.Clamp01(edgeNoise);

            float mppX = src.PixelSize.x;
            float mppZ = src.PixelSize.y;
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    // SlopeAngles already reports degrees, 0 flat to 90 vertical.
                    float angle = slopes.arr[i];
                    float flat = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(angle / slopeLimit));
                    if (flat <= 0.001f) continue;

                    float wet = Mathf.Clamp01(accum[i] / flowLimit);

                    // Ponded depth from the fill: ground the water table is at or
                    // just below is saturated even without visible surface water.
                    float ponded = Mathf.Clamp01((filled[i] - src.arr[i]) / table);

                    float near = nearWater != null ? Mathf.Clamp01(nearWater.arr[i] * 2f) : 0f;

                    float v = flat * Mathf.Clamp01(Mathf.Max(Mathf.Max(wet, ponded), near));

                    if (noiseAmt > 0.001f)
                    {
                        float wx = originX + x * mppX;
                        float n = noise.Fractal(wx, wz, 140f, 3);
                        v *= 1f - noiseAmt + n * noiseAmt * 2f;
                    }

                    dst.arr[i] = Mathf.Clamp01(v);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Shapes the sea bed below sea level: submarine canyons cut along the drainage
    /// the land already established, plus seamounts. Coastal Shelf Shaper sets the
    /// broad gradient; this puts detail on it.
    /// </summary>
    [System.Serializable]
    public class UnderwaterTerrainExtendNode : ModifierNode
    {
        public override string NodeName { get { return "Underwater Terrain Extend"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Adds submarine canyons and seamounts below sea level."; } }

        public int seed = 1919;

        /// <summary>Sea level, 0..1. Nothing above it is touched.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Depth of submarine canyons, 0..1 of world height.</summary>
        public float canyonDepth = 0.05f;

        /// <summary>How far offshore canyons persist, 0..1 of the submarine range.</summary>
        public float canyonReach = 0.7f;

        /// <summary>Height of seamounts, 0..1.</summary>
        public float seamountHeight = 0.06f;

        /// <summary>Seamount spacing in world metres.</summary>
        public float seamountSpacing = 2200f;

        /// <summary>Fine sediment ripple amplitude, 0..1.</summary>
        public float sedimentDetail = 0.004f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float sea = Mathf.Clamp01(seaLevel);
            if (sea <= 1e-5f) return;

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            // Canyons follow the drainage the land already carved, which is why a
            // submarine canyon lines up with the river mouth that fed it.
            float[] filled = Hydrology.FillDepressions(m.arr, resX, resZ, 1e-6f, cancel);
            int[] dir = Hydrology.FlowDirection(filled, resX, resZ, cancel);
            float[] accum = Hydrology.FlowAccumulation(dir, resX, resZ, null, cancel);
            if (stop != null && stop.StopRequested) return;

            Noise noise = new Noise(seed);
            Noise mountNoise = noise.WithSubSeed(77);

            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSpacing = Mathf.Max(1f, seamountSpacing);
            float reach = Mathf.Clamp01(canyonReach);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float h = m.arr[i];
                    if (h >= sea) continue;

                    float wx = originX + x * mppX;

                    // 0 at the shoreline, 1 at the deepest point.
                    float submarine = 1f - Mathf.Clamp01(h / sea);
                    float value = h;

                    if (canyonDepth > 1e-5f)
                    {
                        float channel = Mathf.Clamp01(accum[i] / 400f);
                        float fade = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(submarine / Mathf.Max(1e-4f, reach)));
                        value -= canyonDepth * channel * fade;
                    }

                    if (seamountHeight > 1e-5f)
                    {
                        // Cellular distance inverted: a cone rising from each cell
                        // centre, only offshore where the abyssal plain is.
                        float d = mountNoise.Voronoi(wx / safeSpacing, wz / safeSpacing, 0f, VoronoiBlend.Cellular);
                        float cone = Mathf.Clamp01(1f - d * 3.5f);
                        value += seamountHeight * cone * cone * submarine;
                    }

                    if (sedimentDetail > 1e-6f)
                    {
                        float ripple = noise.Fractal(wx, wz, 60f, 3) - 0.5f;
                        value += ripple * 2f * sedimentDetail * submarine;
                    }

                    // Never push the sea bed above the waterline.
                    m.arr[i] = Mathf.Clamp(value, 0f, sea - 1e-5f);
                }
            }
        }
    }
    /// <summary>
    /// Fills a carved riverbed with water level and depth maps. Mirrors Lake Fill's 
    /// outputs so rivers and lakes can be wired identically into Water Body Output.
    /// </summary>
    [System.Serializable]
    public class RiverFillNode : Generator
    {
        public override string NodeName { get { return "River Fill"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Outputs river mask, depth, and water level for a carved riverbed."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("River Mask", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Water Level", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Maximum depth of the water at the centreline, as a fraction of world height.</summary>
        public float waterDepth = 0.012f;

        /// <summary>Deepest the river reads as full, used to normalise the depth port.</summary>
        public float depthNormalise = 0.012f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (height == null) return tile.CreateMatrix();
            
            Matrix mask = GetInputMatrix(tile, 1, stop);
            if (mask == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = height.Blank();
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);

            int count = dst.arr.Length;
            for (int i = 0; i < count; i++)
            {
                float w = Mathf.Clamp01(mask.arr[i]);
                if (outputIndex == 0) { dst.arr[i] = w; continue; }
                
                float d = w * waterDepth;
                if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(d * norm); continue; }
                if (outputIndex == 2) { dst.arr[i] = height.arr[i] + d; continue; }
            }

            return dst;
        }
    }
}
