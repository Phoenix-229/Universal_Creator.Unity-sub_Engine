// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SplatPageResidency.cs
//  Purpose: Analytic page residency for the splat cache (§ 9.7 G-42). Textbook
//           virtual texturing reads back a GPU feedback buffer to learn which pages a
//           frame touched; that needs a compute readback path C-01 does not allow, and
//           it carries a one-frame latency. This computes the resident page set on the
//           CPU instead - the pages covering the ground out to the cache's range around
//           the camera, each page sized for the mip G-43 selects. It is cheaper than a
//           readback, has no latency, and - the property C-04 needs - is DETERMINISTIC:
//           the same camera transform yields the same page set on every run and every
//           machine, because the set is pure integer floor arithmetic over page indices.
//
//  A PAGE IS A TILE OF THE GROUND AT A MIP
//  ---------------------------------------
//  The world is tiled into pages of a base size; a coarser mip doubles the page's world
//  size, so higher mips hold fewer, larger pages. The resident set at a mip is every
//  page index in the square [cam − range, cam + range] on each axis - a conservative
//  cover the scene layer refines against the true frustum. Because the bounds are
//  FloorToInt of exact ratios and the fill order is fixed (x outer, z inner), the output
//  list is already sorted, duplicate-free and bit-identical across machines - no hash
//  set, whose iteration order would betray determinism, is ever involved.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  No camera, no Texture, no GPU. The self-test pins determinism (two identical calls,
//  and a whole-page translation, give identical sets), the count law, and that a coarser
//  mip holds fewer pages. The frustum refinement and the actual page upload are the scene
//  layer above.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>A splat-cache page index on the ground plane: integer tile coordinates. Value-equal and ordered
    /// (by X then Z) so a resident set is deterministic and comparable across runs.</summary>
    public readonly struct PageKey : IEquatable<PageKey>, IComparable<PageKey>
    {
        public readonly int X;
        public readonly int Z;

        public PageKey(int x, int z) { X = x; Z = z; }

        public bool Equals(PageKey other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is PageKey k && Equals(k);
        public override int GetHashCode() => unchecked((X * 397) ^ Z);
        public int CompareTo(PageKey other) => X != other.X ? X.CompareTo(other.X) : Z.CompareTo(other.Z);
    }

    /// <summary>
    /// Pure analytic residency for the splat cache: the deterministic set of pages covering the ground out to a
    /// range around the camera, at a mip-scaled page size. Integer arithmetic only - no GPU readback, no hash
    /// set, so the page set is identical across runs and machines (C-04). Device-free.
    /// </summary>
    public static class SplatPageResidency
    {
        /// <summary>The world size of a page at <paramref name="mip"/>: the base tile size doubled per mip level,
        /// so a coarser mip covers more ground with each page. Fails closed to 0 on degenerate input.</summary>
        public static float PageWorldSizeM(float baseTileWorldSizeM, int mip)
        {
            if (baseTileWorldSizeM <= 0f || mip < 0) return 0f;
            return baseTileWorldSizeM * (1 << mip);
        }

        /// <summary>The page index a world position falls in: FloorToInt(world / pageSize). Deterministic. Returns
        /// (0,0) on a degenerate page size.</summary>
        public static PageKey PageCoordForWorld(float worldX, float worldZ, float pageWorldSizeM)
        {
            if (pageWorldSizeM <= 0f) return new PageKey(0, 0);
            return new PageKey(Mathf.FloorToInt(worldX / pageWorldSizeM), Mathf.FloorToInt(worldZ / pageWorldSizeM));
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the resident page set: every page index in the square
        /// [camera − range, camera + range] on each axis, at <paramref name="pageWorldSizeM"/>. The list is
        /// cleared first and filled x-outer/z-inner, so it comes out sorted and duplicate-free without a set -
        /// the deterministic order the done-when needs. Returns the page count; 0 on degenerate input.
        /// </summary>
        public static int FillResidentPages(float cameraX, float cameraZ, float cacheRangeM, float pageWorldSizeM, List<PageKey> into)
        {
            if (into == null) return 0;
            into.Clear();
            if (pageWorldSizeM <= 0f || cacheRangeM < 0f) return 0;

            int minX = Mathf.FloorToInt((cameraX - cacheRangeM) / pageWorldSizeM);
            int maxX = Mathf.FloorToInt((cameraX + cacheRangeM) / pageWorldSizeM);
            int minZ = Mathf.FloorToInt((cameraZ - cacheRangeM) / pageWorldSizeM);
            int maxZ = Mathf.FloorToInt((cameraZ + cacheRangeM) / pageWorldSizeM);

            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                    into.Add(new PageKey(x, z));
            return into.Count;
        }

        /// <summary>The resident page count without materialising the list: the page span on each axis multiplied.
        /// = (floor((cam+range)/size) − floor((cam−range)/size) + 1)². Fails closed to 0.</summary>
        public static int ResidentPageCount(float cameraX, float cameraZ, float cacheRangeM, float pageWorldSizeM)
        {
            if (pageWorldSizeM <= 0f || cacheRangeM < 0f) return 0;
            int spanX = Mathf.FloorToInt((cameraX + cacheRangeM) / pageWorldSizeM) - Mathf.FloorToInt((cameraX - cacheRangeM) / pageWorldSizeM) + 1;
            int spanZ = Mathf.FloorToInt((cameraZ + cacheRangeM) / pageWorldSizeM) - Mathf.FloorToInt((cameraZ - cacheRangeM) / pageWorldSizeM) + 1;
            return spanX * spanZ;
        }
    }
}
