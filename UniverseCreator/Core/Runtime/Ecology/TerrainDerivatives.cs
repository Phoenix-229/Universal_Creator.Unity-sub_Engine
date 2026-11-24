// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    TerrainDerivatives.cs
//  Purpose: The two things about a point that cannot be known from the point.
//
//  Slope, aspect, curvature, soil depth and rockiness all fall out of the nine
//  heights immediately around a point, so TopographyMath computes them on demand
//  and nothing is cached. Caching them would mean a second copy of numbers the
//  sampler can produce in a few multiplies, and two copies of a number is how the
//  two come to disagree.
//
//  Two fields genuinely cannot be answered locally, and those are what this class
//  holds:
//
//    - Wetness. Whether a hollow is wet depends on how much hillside drains
//      through it, which is a question about ground the point cannot see.
//    - Wind exposure. Whether a site is sheltered depends on whether anything
//      upwind stands higher, out to a few hundred metres.
//
//  Both are built once per chunk, off the main thread, and then read by
//  interpolation. Both are pure functions of the ground within a bounded radius,
//  which is what lets two neighbouring chunks agree along their shared edge
//  instead of drawing a stripe of wrong vegetation down it.
//
//  The caller supplies heights padded outwards past the chunk it owns - far
//  enough that every bounded path and every skyline scan stays inside the array.
//  The padding is the whole reason the seam works, and a caller that omits it
//  gets a seam back.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Per-chunk wetness index and topographic wind exposure, sampled by world
    /// position.
    ///
    /// Build on a worker thread, read from anywhere. After Build returns, every
    /// field is read-only, so any number of threads may sample one instance.
    /// </summary>
    public sealed class TerrainDerivatives
    {
        private float[] wetness;
        private float[] exposure;
        private int[] flow;

        // Directions are scratch: needed to route the water, of no use once the
        // counts are in. Kept as a field rather than a local so a chunk rebuilt
        // at the same resolution reuses the allocation instead of making another.
        private int[] directions;

        private int width;
        private int depth;
        private float cellSizeM;
        private float originX;
        private float originZ;

        private bool built;

        /// <summary>False until Build has completed. Readers fall back to their local estimate while it is false.</summary>
        public bool IsBuilt { get { return built; } }

        /// <summary>Cells across the padded grid, including the apron.</summary>
        public int Width { get { return width; } }

        /// <summary>Cells down the padded grid, including the apron.</summary>
        public int Depth { get { return depth; } }

        /// <summary>m between neighbouring cells.</summary>
        public float CellSizeM { get { return cellSizeM; } }

        /// <summary>
        /// Computes both fields from a padded height grid, in metres, row-major
        /// with index = z * paddedWidth + x.
        ///
        /// originX and originZ are the world position of cell (0,0) of the padded
        /// grid - the apron's outer corner, not the chunk's. Getting that wrong
        /// shifts every lookup by the apron width, which reads as vegetation
        /// offset from the terrain it was placed against.
        ///
        /// catchmentRadiusM bounds the drainage paths and windFromDeg is the
        /// compass bearing the prevailing wind arrives from.
        /// </summary>
        public void Build(
            float[] paddedHeightsM,
            int paddedWidth,
            int paddedDepth,
            float cellSize,
            float paddedOriginX,
            float paddedOriginZ,
            float catchmentRadiusM,
            float windFromDeg)
        {
            built = false;

            if (paddedHeightsM == null) return;
            if (paddedWidth < 3 || paddedDepth < 3) return;
            if (paddedHeightsM.Length < paddedWidth * paddedDepth) return;

            width = paddedWidth;
            depth = paddedDepth;
            cellSizeM = cellSize > 0f ? cellSize : 1f;
            originX = paddedOriginX;
            originZ = paddedOriginZ;

            int cells = width * depth;

            if (flow == null || flow.Length != cells) flow = new int[cells];
            if (directions == null || directions.Length != cells) directions = new int[cells];
            if (wetness == null || wetness.Length != cells) wetness = new float[cells];
            if (exposure == null || exposure.Length != cells) exposure = new float[cells];

            BuildWetness(paddedHeightsM, catchmentRadiusM);
            BuildExposure(paddedHeightsM, windFromDeg);

            // Routing is spent. Freeing it here rather than in Release keeps the
            // per-chunk footprint to the two fields that are actually read.
            directions = null;

            built = true;
        }

        /// <summary>Drops the working arrays once a chunk is finished with them.</summary>
        public void Release()
        {
            wetness = null;
            exposure = null;
            flow = null;
            directions = null;
            built = false;
        }

        /// <summary>
        /// ln(a / tan beta) at a world position, interpolated between cells.
        /// Returns the single-cell floor when nothing has been built.
        /// </summary>
        public float WetnessIndexAt(float worldX, float worldZ)
        {
            if (!built) return EcoMath.TopographicWetnessIndex(
                EcoConstants.TwiMinCatchmentArea, EcoConstants.TwiMinSlopeRadians);

            return SampleBilinear(wetness, worldX, worldZ);
        }

        /// <summary>0-1 topographic exposure at a world position. Returns neutral when nothing has been built.</summary>
        public float WindExposureAt(float worldX, float worldZ)
        {
            if (!built) return 0.5f;
            return Mathf.Clamp01(SampleBilinear(exposure, worldX, worldZ));
        }

        /// <summary>Cells draining through this point, itself included. One when nothing has been built.</summary>
        public float FlowAccumulationAt(float worldX, float worldZ)
        {
            if (!built) return 1f;

            CellOf(worldX, worldZ, out int x, out int z);
            return flow[z * width + x];
        }

        private void BuildWetness(float[] heights, float catchmentRadiusM)
        {
            FlowAccumulation.ComputeDirections(heights, width, depth, cellSizeM, directions);

            // One step per cell of the radius, so a path is cut off at the same
            // ground distance whichever chunk traced it.
            int maxSteps = Mathf.Max(1, Mathf.CeilToInt(catchmentRadiusM / cellSizeM));
            FlowAccumulation.Accumulate(directions, width, depth, maxSteps, flow);

            // Specific catchment area is the drained area divided by the width of
            // contour it crosses. One cell of contour is one cell wide, so the
            // area in square metres over that width leaves a length in metres.
            float cellArea = cellSizeM * cellSizeM;
            float perContourWidth = cellArea / cellSizeM;

            for (int z = 0; z < depth; z++)
            {
                int row = z * width;

                for (int x = 0; x < width; x++)
                {
                    int i = row + x;

                    HeightStencil h = StencilAt(heights, x, z);
                    TopographyMath.SlopeAspect(in h, out float slopeDeg, out _);

                    float area = flow[i] * perContourWidth;
                    wetness[i] = EcoMath.TopographicWetnessIndex(area, slopeDeg * Mathf.Deg2Rad);
                }
            }
        }

        private void BuildExposure(float[] heights, float windFromDeg)
        {
            int dirCount = Mathf.Max(1, EcoConstants.WindExposureDirectionCount);
            int distCount = Mathf.Max(1, EcoConstants.WindExposureDistanceSamples);

            // The upwind arc, swept from one edge to the other. With a single
            // direction the arc collapses to the wind bearing itself.
            float arcSpan = 2f * EcoConstants.WindExposureArcHalfWidthDegrees;
            float arcStep = dirCount > 1 ? arcSpan / (dirCount - 1) : 0f;
            float arcStart = windFromDeg - EcoConstants.WindExposureArcHalfWidthDegrees;

            // Geometric spacing: the first sample is one cell out and the last is
            // at the full search distance, so nearby ground - which shelters most
            // - is examined most closely.
            float nearM = cellSizeM;
            float farM = Mathf.Max(EcoConstants.WindExposureMaxDistanceMetres, nearM * 2f);
            float ratio = distCount > 1 ? Mathf.Pow(farM / nearM, 1f / (distCount - 1)) : 1f;

            float shelterAngle = Mathf.Max(EcoConstants.WindExposureShelterAngleDegrees, Mathf.Epsilon);

            for (int z = 0; z < depth; z++)
            {
                int row = z * width;

                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    float here = heights[i];

                    float worstAngle = 0f;

                    for (int d = 0; d < dirCount; d++)
                    {
                        float bearing = (arcStart + d * arcStep) * Mathf.Deg2Rad;
                        float stepX = Mathf.Sin(bearing);
                        float stepZ = Mathf.Cos(bearing);

                        float distance = nearM;

                        for (int j = 0; j < distCount; j++)
                        {
                            float sx = x + stepX * distance / cellSizeM;
                            float sz = z + stepZ * distance / cellSizeM;

                            float rise = SampleHeightBilinear(heights, sx, sz) - here;

                            if (rise > 0f)
                            {
                                float angle = Mathf.Atan2(rise, distance) * Mathf.Rad2Deg;
                                if (angle > worstAngle) worstAngle = angle;
                            }

                            distance *= ratio;
                        }
                    }

                    exposure[i] = 1f - Mathf.Clamp01(worstAngle / shelterAngle);
                }
            }
        }

        private HeightStencil StencilAt(float[] heights, int x, int z)
        {
            HeightStencil h = new HeightStencil();
            h.stepM = cellSizeM;

            h.nw = HeightClamped(heights, x - 1, z + 1);
            h.n = HeightClamped(heights, x, z + 1);
            h.ne = HeightClamped(heights, x + 1, z + 1);
            h.w = HeightClamped(heights, x - 1, z);
            h.c = HeightClamped(heights, x, z);
            h.e = HeightClamped(heights, x + 1, z);
            h.sw = HeightClamped(heights, x - 1, z - 1);
            h.s = HeightClamped(heights, x, z - 1);
            h.se = HeightClamped(heights, x + 1, z - 1);

            return h;
        }

        private float HeightClamped(float[] heights, int x, int z)
        {
            if (x < 0) x = 0;
            else if (x >= width) x = width - 1;

            if (z < 0) z = 0;
            else if (z >= depth) z = depth - 1;

            return heights[z * width + x];
        }

        private float SampleHeightBilinear(float[] heights, float x, float z)
        {
            int xi = Mathf.FloorToInt(x);
            int zi = Mathf.FloorToInt(z);

            float fx = x - xi;
            float fz = z - zi;

            float h00 = HeightClamped(heights, xi, zi);
            float h10 = HeightClamped(heights, xi + 1, zi);
            float h01 = HeightClamped(heights, xi, zi + 1);
            float h11 = HeightClamped(heights, xi + 1, zi + 1);

            float a = Mathf.Lerp(h00, h10, fx);
            float b = Mathf.Lerp(h01, h11, fx);
            return Mathf.Lerp(a, b, fz);
        }

        private float SampleBilinear(float[] field, float worldX, float worldZ)
        {
            float px = (worldX - originX) / cellSizeM;
            float pz = (worldZ - originZ) / cellSizeM;

            int xi = Mathf.FloorToInt(px);
            int zi = Mathf.FloorToInt(pz);

            float fx = px - xi;
            float fz = pz - zi;

            float v00 = FieldClamped(field, xi, zi);
            float v10 = FieldClamped(field, xi + 1, zi);
            float v01 = FieldClamped(field, xi, zi + 1);
            float v11 = FieldClamped(field, xi + 1, zi + 1);

            float a = Mathf.Lerp(v00, v10, fx);
            float b = Mathf.Lerp(v01, v11, fx);
            return Mathf.Lerp(a, b, fz);
        }

        private float FieldClamped(float[] field, int x, int z)
        {
            if (x < 0) x = 0;
            else if (x >= width) x = width - 1;

            if (z < 0) z = 0;
            else if (z >= depth) z = depth - 1;

            return field[z * width + x];
        }

        private void CellOf(float worldX, float worldZ, out int x, out int z)
        {
            x = Mathf.Clamp(Mathf.RoundToInt((worldX - originX) / cellSizeM), 0, width - 1);
            z = Mathf.Clamp(Mathf.RoundToInt((worldZ - originZ) / cellSizeM), 0, depth - 1);
        }
    }
}
