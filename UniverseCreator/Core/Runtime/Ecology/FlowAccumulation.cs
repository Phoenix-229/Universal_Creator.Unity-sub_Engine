// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    FlowAccumulation.cs
//  Purpose: Works out where water runs on a heightfield, and how much of the
//           hillside drains through each cell.
//
//  Single-flow-direction (D8) routing: every cell sends all of its water to
//  whichever of its eight neighbours lies steepest downhill. It is the oldest
//  method there is and it is the right one here, because it is exact in integers
//  - a cell either drains through another or it does not - and an integer count
//  is identical on every machine, which a float sum accumulated in a different
//  order is not.
//
//  Accumulation is bounded by path length rather than by the edge of the array.
//  That is the load-bearing decision in this file, and it is what makes the
//  wetness index continuous across a chunk seam. Counting everything in the
//  array would make a cell's answer depend on where the array happened to end,
//  so the same ground would report one catchment when the left chunk built it and
//  a different one when the right chunk did - a visible stripe of wrong
//  vegetation down every seam. Stopping every path at the same distance instead
//  makes the answer a pure function of the ground within that distance, and both
//  chunks hold all of that ground, so both compute the same number.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// D8 flow routing and bounded contributing-area counts.
    ///
    /// Everything here is static and writes only into arrays the caller owns, so
    /// it is safe to run on a worker thread.
    /// </summary>
    public static class FlowAccumulation
    {
        /// <summary>A cell with nowhere lower to send its water: a pit, or dead flat ground.</summary>
        public const int NoDirection = -1;

        // Eight neighbours, in a fixed order starting north and turning
            // clockwise. The order is part of the contract: when two neighbours
        // are exactly equally steep the lower index wins, so the order is what
        // makes that tie break the same way on every machine and in every build.
        private static readonly int[] OffsetX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] OffsetZ = { 1, 1, 0, -1, -1, -1, 0, 1 };

        // Diagonal neighbours sit root-two cells away, so a drop to one of them
        // is spread over a longer path and is a gentler gradient than the same
        // drop to a side neighbour. Comparing raw drops instead would bias every
        // flow path towards the diagonals.
        private static readonly float[] OffsetDistance =
        {
            1f, 1.41421356f, 1f, 1.41421356f,
            1f, 1.41421356f, 1f, 1.41421356f
        };

        /// <summary>
        /// Fills <paramref name="directionOut"/> with the index into the eight
        /// neighbour offsets that each cell drains towards, or
        /// <see cref="NoDirection"/> where nothing is lower.
        ///
        /// Heights are in metres and the grid is row-major, index = z * width + x.
        /// </summary>
        public static void ComputeDirections(
            float[] heights, int width, int depth, float cellSizeM, int[] directionOut)
        {
            if (heights == null || directionOut == null) return;

            float safeCell = cellSizeM > 0f ? cellSizeM : 1f;

            for (int z = 0; z < depth; z++)
            {
                int row = z * width;

                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    float here = heights[i];

                    int bestDir = NoDirection;
                    float bestGradient = 0f;

                    for (int k = 0; k < 8; k++)
                    {
                        int nx = x + OffsetX[k];
                        int nz = z + OffsetZ[k];

                        if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;

                        float drop = here - heights[nz * width + nx];
                        if (drop <= 0f) continue;

                        float gradient = drop / (OffsetDistance[k] * safeCell);

                        // Strictly greater, so an equally steep later neighbour
                        // cannot displace an earlier one. That is the tie break.
                        if (gradient > bestGradient)
                        {
                            bestGradient = gradient;
                            bestDir = k;
                        }
                    }

                    directionOut[i] = bestDir;
                }
            }
        }

        /// <summary>
        /// Counts, for every cell, how many cells drain through it along a path
        /// no longer than <paramref name="maxSteps"/> cells - itself included, so
        /// the minimum is one and no cell ever reports an empty catchment.
        ///
        /// Paths cannot loop: each step moves to strictly lower ground, so a walk
        /// downhill can only end at a pit, at flat ground or at the array edge.
        /// No visited set is needed and none is kept.
        /// </summary>
        public static void Accumulate(
            int[] directions, int width, int depth, int maxSteps, int[] countOut)
        {
            if (directions == null || countOut == null) return;

            int cells = width * depth;

            for (int i = 0; i < cells; i++) countOut[i] = 1;

            for (int z = 0; z < depth; z++)
            {
                int row = z * width;

                for (int x = 0; x < width; x++)
                {
                    int cx = x;
                    int cz = z;
                    int index = row + x;

                    for (int step = 0; step < maxSteps; step++)
                    {
                        int dir = directions[index];
                        if (dir == NoDirection) break;

                        cx += OffsetX[dir];
                        cz += OffsetZ[dir];

                        if (cx < 0 || cx >= width || cz < 0 || cz >= depth) break;

                        index = cz * width + cx;
                        countOut[index]++;
                    }
                }
            }
        }
    }
}
