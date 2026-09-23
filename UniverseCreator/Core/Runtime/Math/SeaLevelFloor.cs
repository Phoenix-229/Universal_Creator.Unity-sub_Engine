// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    SeaLevelFloor.cs
//  Purpose: The always-on rule that dry land never sits below sea level. Runs in
//           the CORE of height output, not as a node the user has to wire, so it
//           applies to every world no matter what the graph does.
//
//  THE PROBLEM IT SOLVES
//  ---------------------
//  A continent shaped by noise dips below sea level in places it should not, and
//  where a river is cut through such a dip the un-lowered ground either side reads
//  as a levee wall standing over a sunk plain. The fix is to lift dry land that
//  has fallen below sea back up to it - but a per-chunk worker cannot tell the open
//  ocean from an inland pit, and lifting the ocean floor would erase the sea.
//
//  WHY IT NEEDS THE WHOLE WORLD
//  ----------------------------
//  "Is this the ocean" is a connectivity question the size of the map: a sub-sea
//  cell is ocean only if a continuous sub-sea path reaches a world edge. That is
//  solved once at coarse resolution through WorldDrainage/TileWindows.WholeWorld
//  and read back per chunk, the same pattern the drainage nodes use.
//
//  THREE CLASSES OF SUB-SEA CELL
//  -----------------------------
//    - Genuine ocean  : below sea AND flood-reachable from a world edge. Left alone
//                       so the sea bed keeps its depth and the ocean is not erased.
//    - Closed basin   : below sea, not ocean, but a real depression that pools water
//                       (the lake/inland-sea system fills it later in the settle
//                       phase). Left alone so its basin survives.
//    - Dry-land dip   : below sea, not ocean, not a pooling basin. THIS is the
//                       artifact. Raised to sea level with a SmoothStep coast grade.
//
//  RAISE ONLY, and driven by the chunk's own fine height so the shoreline is as
//  sharp as the heightmap under it. Rivers, lakes and ocean are stamped afterwards
//  in the settle phase, so this pass is strictly the LOW layer beneath them.
//
//  Managed C# over float[]/bool[]. No Unity API beyond Mathf; safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Lifts dry land that has fallen below sea level back up to it, leaving genuine
    /// ocean and closed lake basins untouched. Built from a coarse whole-world solve
    /// and applied to a chunk's fine height in place.
    /// </summary>
    public static class SeaLevelFloor
    {
        /// <summary>Whole-world cells per side for the ocean/basin classification.</summary>
        public const int DefaultResolution = 256;

        /// <summary>
        /// Applies the floor to <paramref name="src"/> in place. <paramref name="wd"/>
        /// is a whole-world drainage solve of the same height branch; when it is null
        /// (no graph to pull, or the world is a single chunk) the pass is skipped and
        /// the height is left exactly as it was.
        /// </summary>
        /// <param name="seaLevel">Normalised sea height (0.35 is the system default).</param>
        /// <param name="coastGrade">
        /// Height band over which a lifted cell eases back to its own terrain, so the
        /// new shore is a graded beach rather than a cliff at the waterline.
        /// </param>
        /// <param name="basinDepth">
        /// Pooled depth above which a non-ocean depression counts as a real basin and
        /// is exempt (a lake will fill it). Below this it is treated as a dry-land dip
        /// and raised.
        /// </param>
        /// <param name="strength">0..1 blend of the lift, 1 = full.</param>
        public static void Apply(WorldDrainage wd, Matrix src, float seaLevel,
                                 float coastGrade, float basinDepth, float strength,
                                 StopToken stop)
        {
            if (wd == null || src == null || strength <= 1e-5f) return;
            if (wd.height == null || wd.filled == null) return;

            float sea  = Mathf.Clamp01(seaLevel);
            float band = Mathf.Max(1e-5f, coastGrade);
            float amt  = Mathf.Clamp01(strength);

            int count = wd.resX * wd.resZ;
            if (count <= 0 || wd.height.arr.Length < count) return;

            // 1) Which coarse cells are genuine ocean (sub-sea, edge-reachable).
            bool[] ocean = FloodOceanFromEdges(wd.height.arr, wd.resX, wd.resZ, sea);
            if (stop != null && stop.StopRequested) return;

            // 2) Coarse "allowed to raise" field: below sea, not ocean, not a real
            //    pooling basin. Kept as a float so the per-chunk read blends smoothly
            //    across the coast instead of stepping cell to cell.
            float[] raise = new float[count];
            for (int i = 0; i < count; i++)
            {
                float h = wd.height.arr[i];
                if (h >= sea || ocean[i]) continue;                 // land or sea: no lift
                float pooled = wd.filled[i] - h;                    // depression depth here
                if (pooled > basinDepth) continue;                  // real basin: a lake's job
                raise[i] = 1f;
            }
            if (stop != null && stop.StopRequested) return;

            // 3) Read the coarse permission field onto this chunk's lattice.
            float[] chunkRaise = wd.ReadAtChunk(raise, src, WorldRead.Bilinear);
            if (stop != null && stop.StopRequested) return;

            // 4) Lift, using THIS chunk's fine height so the waterline is sharp. A cell
            //    already at or above sea floors to itself (no change); the deeper below
            //    sea it is the more of the lift the SmoothStep lets through, and the
            //    result is never lower than where it started.
            int n = src.arr.Length;
            for (int i = 0; i < n && i < chunkRaise.Length; i++)
            {
                float w = chunkRaise[i];
                if (w <= 0f) continue;

                float h = src.arr[i];
                float t = Mathf.Clamp01((h - sea) / band);
                float s = t * t * (3f - 2f * t);                    // SmoothStep(t)
                float floored = Mathf.Lerp(sea, h, s);
                float lifted  = h > floored ? h : floored;          // raise only
                src.arr[i] = h + (lifted - h) * w * amt;
            }
        }

        /// <summary>
        /// Marks every below-sea cell reachable from a world edge through other
        /// below-sea cells: the open ocean. Four-neighbour flood from the border,
        /// managed stack, O(cells).
        /// </summary>
        private static bool[] FloodOceanFromEdges(float[] h, int resX, int resZ, float sea)
        {
            int count = resX * resZ;
            bool[] ocean = new bool[count];
            int[] stack = new int[count];
            int top = 0;

            // Seed from any border cell that is itself below sea.
            for (int x = 0; x < resX; x++)
            {
                Seed(h, ocean, stack, ref top, x, sea);                       // bottom row
                Seed(h, ocean, stack, ref top, (resZ - 1) * resX + x, sea);   // top row
            }
            for (int z = 0; z < resZ; z++)
            {
                Seed(h, ocean, stack, ref top, z * resX, sea);                // left col
                Seed(h, ocean, stack, ref top, z * resX + (resX - 1), sea);   // right col
            }

            while (top > 0)
            {
                int i = stack[--top];
                int x = i % resX;
                int z = i / resX;

                if (x > 0)        TryFlood(h, ocean, stack, ref top, i - 1, sea);
                if (x < resX - 1) TryFlood(h, ocean, stack, ref top, i + 1, sea);
                if (z > 0)        TryFlood(h, ocean, stack, ref top, i - resX, sea);
                if (z < resZ - 1) TryFlood(h, ocean, stack, ref top, i + resX, sea);
            }

            return ocean;
        }

        private static void Seed(float[] h, bool[] ocean, int[] stack, ref int top,
                                 int i, float sea)
        {
            if (!ocean[i] && h[i] < sea) { ocean[i] = true; stack[top++] = i; }
        }

        private static void TryFlood(float[] h, bool[] ocean, int[] stack, ref int top,
                                     int i, float sea)
        {
            if (!ocean[i] && h[i] < sea) { ocean[i] = true; stack[top++] = i; }
        }
    }
}
