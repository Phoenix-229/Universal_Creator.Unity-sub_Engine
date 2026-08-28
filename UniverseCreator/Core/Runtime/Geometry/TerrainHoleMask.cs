// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    TerrainHoleMask.cs
//  Purpose: Hole painting, with Unity 6's two SetHoles traps designed out (§ 9.8 G-53).
//           Holes are how caves, overhangs and building interiors meet the terrain, and
//           TerrainData.SetHoles has two properties that break naive code - both encoded
//           here so a caller cannot hit them:
//
//           1. THE SENSE IS INVERTED. `true` means the surface is PRESENT; `false` punches
//              the hole. A bool[,] left at its default is all-false, which deletes the
//              ENTIRE terrain. So a fresh hole layer must be filled with `true` first, and
//              `NewFullyPresent` is the only constructor offered here - there is no way to
//              get an all-false (all-hole) array by accident.
//
//           2. IT IS PER-QUAD, NOT PER-VERTEX. The array is (heightmapResolution − 1)²,
//              not heightmapResolution². Sizing it from the heightmap resolution throws, or
//              worse, silently misaligns by one quad along two edges. `HoleArrayEdge`
//              returns resolution − 1 so the dimension is computed once, correctly.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  A bool[,] and integer quad coordinates; no TerrainData, no SetHoles call. The self-test
//  pins the (res − 1) dimension, the present-means-true sense, a hole of a known quad count,
//  and that a fresh layer leaves the terrain fully present. Handing the mask to
//  TerrainData.SetHoles is the one-line scene-layer step above.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure terrain hole-mask arithmetic that designs out Unity 6's two SetHoles traps: the inverted sense
    /// (true = surface present) and the per-quad (resolution − 1)² dimension. Device-free; the mask is a bool[,]
    /// ready to hand to TerrainData.SetHoles.
    /// </summary>
    public static class TerrainHoleMask
    {
        /// <summary>The edge length of the hole array: heightmapResolution − 1 (per-QUAD, not per-vertex). Zero for
        /// a resolution of one or less, where there are no quads.</summary>
        public static int HoleArrayEdge(int heightmapResolution)
        {
            return heightmapResolution <= 1 ? 0 : heightmapResolution - 1;
        }

        /// <summary>
        /// A fresh hole layer with the terrain FULLY PRESENT: an (res − 1)² array filled with <c>true</c>. This is
        /// the only array constructor offered, because a default (all-false) array would delete the whole terrain.
        /// </summary>
        public static bool[,] NewFullyPresent(int heightmapResolution)
        {
            int e = HoleArrayEdge(heightmapResolution);
            var mask = new bool[e, e];
            for (int x = 0; x < e; x++)
                for (int y = 0; y < e; y++)
                    mask[x, y] = true;   // true = surface present
            return mask;
        }

        /// <summary>Punch a circular hole: set every quad within <paramref name="radiusQuads"/> of the centre to
        /// <c>false</c> (absent). Returns the number of quads newly punched. Radius 0 punches the single centre quad.</summary>
        public static int PunchCircle(bool[,] mask, int centreX, int centreY, int radiusQuads)
        {
            if (mask == null) return 0;
            int w = mask.GetLength(0), h = mask.GetLength(1);
            int r2 = radiusQuads < 0 ? 0 : radiusQuads * radiusQuads;
            int punched = 0;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int dx = x - centreX, dy = y - centreY;
                    if (dx * dx + dy * dy <= r2 && mask[x, y]) { mask[x, y] = false; punched++; }
                }
            return punched;
        }

        /// <summary>The number of quads whose surface is present (<c>true</c>).</summary>
        public static int PresentCount(bool[,] mask)
        {
            if (mask == null) return 0;
            int w = mask.GetLength(0), h = mask.GetLength(1), n = 0;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (mask[x, y]) n++;
            return n;
        }

        /// <summary>The number of punched (hole) quads: total minus present.</summary>
        public static int HoleCount(bool[,] mask)
        {
            if (mask == null) return 0;
            return mask.GetLength(0) * mask.GetLength(1) - PresentCount(mask);
        }
    }
}
