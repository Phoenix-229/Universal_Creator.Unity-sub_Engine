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
//                       phase). Left alone so its basin survives - a basin holding
//                       water is a lake bed, not dry land. Lifting these terraced
//                       the author's lake basins into stepped pits (2026-10-06), so
//                       the exemption is deliberate and must not be removed again.
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
        /// Retained for signature compatibility with the callers that pass the shared
        /// basin threshold (HeightOutputNode, UCSelfTest) and with
        /// <see cref="ContinentalRise"/>, which still needs it. This pass no longer
        /// reads it: every cell it can act on is already below the waterline, so a
        /// pooling exemption here could only ever protect sub-sea dry ground, which is
        /// the artifact rather than a lake basin. See the class header.
        /// </param>
        /// <param name="freeboard">
        /// Normalised height a lifted dry-land dip is raised ABOVE sea, so dry ground is
        /// never left sitting exactly at the waterline. 0 restores the old "floor to
        /// exactly sea" behaviour. Only the dry-land-dip class is affected; genuine ocean
        /// and closed lake basins are never touched, so the sea's own depth is preserved.
        /// </param>
        /// <param name="strength">0..1 blend of the lift, 1 = full.</param>
        /// <param name="stop">Optional worker cancellation; do not publish a cancelled output.</param>
        /// <param name="lakeMask">Optional accepted lake occupancy on the drainage grid, values 0..1. Never modified.</param>
        /// <param name="shoreBlendMetres">Metres over which coastal freeboard is released; zero retains legacy direct-call behavior.</param>
        /// <param name="shoreDeadzoneMetres">Metres from the ocean exempt from freeboard, nonnegative.</param>
        public static void Apply(WorldDrainage wd, Matrix src, float seaLevel,
                                 float coastGrade, float basinDepth, float freeboard,
                                 float strength, StopToken stop, float[] lakeMask = null,
                                 float shoreBlendMetres = 0, float shoreDeadzoneMetres = 0)
        {
            if (wd == null || src == null || strength <= 1e-5f) return;
            if (wd.height == null || wd.filled == null) return;

            float sea  = Mathf.Clamp01(seaLevel);
            float band = Mathf.Max(1e-5f, coastGrade);
            float amt  = Mathf.Clamp01(strength);

            // Dry-land dips floor to sea + freeboard, never to exactly sea, so raised dry
            // ground always stands STRICTLY above the waterline (the user's rule: continent
            // is never below OR at sea level). Clamped to 1 so it can never exceed the map.
            // Classification still keys on `sea`, so genuine ocean and closed basins are
            // exempt exactly as before - only the lifted dry class rises to this floor.
            float dryFloor = Mathf.Min(1f, sea + Mathf.Max(0f, freeboard));

            int count = wd.resX * wd.resZ;
            if (count <= 0 || wd.height.arr.Length < count) return;

            // 1) Which coarse cells are genuine ocean (sub-sea, edge-reachable).
            bool[] ocean = FloodOceanFromEdges(wd.height.arr, wd.resX, wd.resZ, sea);
            if (stop != null && stop.StopRequested) return;

            // 2) Coarse "allowed to raise" field: below sea, not ocean, not a real
            //    pooling basin. Kept as a float so the per-chunk read blends smoothly
            //    across the coast instead of stepping cell to cell.
            //
            //    The basin exemption was briefly removed on 2026-10-06 so that sub-sea
            //    closed depressions would be lifted. The author's live world showed that
            //    to be wrong - it terraced the lake basins into stepped pits - and the
            //    instruction was explicit: "make sure you do not affect the basins". A
            //    depression that holds WATER is a lake bed, not dry land, so it is exempt
            //    at any elevation. The "no plain at or below sea level" guarantee is
            //    carried instead by ContinentalRise running LAST (so CoastalShelf cannot
            //    cut it back to the waterline) and by its smooth lift.
            float[] raise = new float[count];
            for (int i = 0; i < count; i++)
            {
                float h = wd.height.arr[i];
                if (ocean[i] || lakeMask != null && i < lakeMask.Length && lakeMask[i] > 0.5f) continue;
                // UC-T08-15: include the transition ABOVE sea; a binary below-sea permission printed a freeboard step.
                if (h >= dryFloor + band) continue;
                float pooled = wd.filled[i] - h;                    // depression depth here
                if (pooled > basinDepth) continue;                  // real basin: a lake's job
                raise[i] = 1f;
            }
            if (stop != null && stop.StopRequested) return;

            // 3) Read the coarse permission field onto this chunk's lattice.
            float[] chunkRaise = wd.ReadAtChunk(raise, src, WorldRead.Bilinear);
            float[] chunkDist = null;
            if (shoreBlendMetres > 0)
            {
                Vector2 pix = wd.height.PixelSize;
                var distance = CoastalShelf.ChamferDistanceToOcean(ocean, wd.resX, wd.resZ,
                    Mathf.Max(1e-3f, pix.x), Mathf.Max(1e-3f, pix.y), shoreBlendMetres + shoreDeadzoneMetres);
                chunkDist = wd.ReadAtChunk(distance, src, WorldRead.Bilinear);
            }
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
                float release = chunkDist == null ? 1f : ShoreRelease(chunkDist[i], shoreBlendMetres, shoreDeadzoneMetres);
                if (release <= 0) continue;
                float localFloor = Mathf.Lerp(sea, dryFloor, release);
                float lifted = ContinentalRise.SoftMax(h, localFloor, band);
                src.arr[i] = h + (lifted - h) * w * amt;
            }
        }

        // UC-T08-15: one release curve for floor/shelf/rise, in metres, with zero value/slope at either boundary.
        internal static float ShoreRelease(float distanceMetres, float blendMetres, float deadzoneMetres)
        {
            if (blendMetres <= 0) return 1;
            float t = Mathf.Clamp01((distanceMetres - Mathf.Max(0, deadzoneMetres)) / Mathf.Max(1, blendMetres));
            return t * t * (3 - 2 * t);
        }

        /// <summary>
        /// Marks every below-sea cell reachable from a world edge through other
        /// below-sea cells: the open ocean. Four-neighbour flood from the border,
        /// managed stack, O(cells). Internal so the coastal-shelf pass can reuse the
        /// exact same ocean classification instead of maintaining a second one.
        /// </summary>
        internal static bool[] FloodOceanFromEdges(float[] h, int resX, int resZ, float sea)
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
