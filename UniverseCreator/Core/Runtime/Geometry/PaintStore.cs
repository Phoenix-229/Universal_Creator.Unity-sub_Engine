// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PaintStore.cs
//  Purpose: Sparse paint storage with a stated memory ceiling (§ 9.8 G-49). The store
//           holds only the tiles an author actually touched, keyed by tile coordinate, so
//           untouched world costs nothing - a painted world is not a full-resolution paint
//           texture of the whole terrain, it is a handful of tiles. The § 9.12 memory
//           ceiling is enforced by evicting the least-recently-COMPOSITED tile's RASTER
//           while keeping its strokes: the raster is a cache, recomputable at any time from
//           the strokes; the strokes are the data, the author's actual work. Confusing the
//           two - evicting strokes to save memory - is exactly how a paint tool loses work,
//           so this store can never do it: eviction touches rasters only.
//
//  RE-ENTRY IS RE-COMPOSITE, NOT RE-PAINT
//  --------------------------------------
//  Because the raster is a deterministic function of the tile's stroke list (G-50), an
//  evicted tile re-composites to exactly the value it held before eviction when the author
//  walks back into it. The self-test pins that identity, so eviction is provably lossless.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  No Texture, no Terrain; the "raster" here is a deterministic value standing in for the
//  composited weights, so eviction and re-composite can be pinned without a GPU. The
//  Dictionary keys are tile coordinates and the eviction order is an explicit recency list,
//  so nothing depends on hash iteration order. The real raster upload is the scene layer.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Sparse, ceiling-bounded paint storage: only touched tiles exist, each keyed by a <see cref="PageKey"/> tile
    /// coordinate and holding a stroke list (data) plus an optional cached raster (a recomputable cache). Eviction
    /// drops rasters, never strokes, so it is lossless. Device-free; deterministic recency order.
    /// </summary>
    public sealed class PaintStore
    {
        private sealed class Tile
        {
            public readonly List<PaintStroke> Strokes = new List<PaintStroke>();
            public bool HasRaster;
            public int RasterValue;
        }

        private readonly Dictionary<PageKey, Tile> _tiles = new Dictionary<PageKey, Tile>();
        private readonly List<PageKey> _compositeOrder = new List<PageKey>();   // front = most-recently composited

        public int TileCount => _tiles.Count;

        public int ResidentRasterCount
        {
            get { int n = 0; foreach (var kv in _tiles) if (kv.Value.HasRaster) n++; return n; }
        }

        /// <summary>Strokes recorded in a tile; 0 for an untouched tile (which is never created by the query).</summary>
        public int StrokeCount(PageKey tile) => _tiles.TryGetValue(tile, out var t) ? t.Strokes.Count : 0;

        /// <summary>Whether a tile currently holds a cached raster. False for untouched or evicted tiles.</summary>
        public bool HasRaster(PageKey tile) => _tiles.TryGetValue(tile, out var t) && t.HasRaster;

        /// <summary>Record a stroke in its tile, creating the tile on first touch. Strokes are the data - never evicted.</summary>
        public void AddStroke(PageKey tile, in PaintStroke s)
        {
            if (!_tiles.TryGetValue(tile, out var t)) { t = new Tile(); _tiles[tile] = t; }
            t.Strokes.Add(s);
        }

        /// <summary>
        /// (Re)composite a tile's raster from its strokes - a deterministic fold, so an evicted tile re-composites
        /// to the same value. Marks the raster resident and most-recently-composited. Returns the raster value; 0
        /// for an untouched tile.
        /// </summary>
        public int Composite(PageKey tile)
        {
            if (!_tiles.TryGetValue(tile, out var t)) return 0;
            t.RasterValue = RasterValue(t.Strokes);
            t.HasRaster = true;
            _compositeOrder.Remove(tile);
            _compositeOrder.Insert(0, tile);
            return t.RasterValue;
        }

        /// <summary>
        /// Enforce the raster ceiling: while more than <paramref name="maxResidentRasters"/> rasters are resident,
        /// evict the least-recently-composited tile's raster - keeping its strokes. Returns the number evicted.
        /// </summary>
        public int EnforceCeiling(int maxResidentRasters)
        {
            if (maxResidentRasters < 0) maxResidentRasters = 0;
            int evicted = 0;
            for (int i = _compositeOrder.Count - 1; i >= 0 && ResidentRasterCount > maxResidentRasters; i--)
            {
                if (_tiles.TryGetValue(_compositeOrder[i], out var t) && t.HasRaster)
                {
                    t.HasRaster = false;   // drop the cache; strokes untouched
                    _compositeOrder.RemoveAt(i);
                    evicted++;
                }
            }
            return evicted;
        }

        // Deterministic composite stand-in: a fold over the strokes in recorded order (C-04).
        private static int RasterValue(List<PaintStroke> strokes)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < strokes.Count; i++)
                {
                    var s = strokes[i];
                    h = h * 31 + s.TargetLayer;
                    h = h * 31 + Mathf.RoundToInt(s.WorldX * 100f);
                    h = h * 31 + Mathf.RoundToInt(s.WorldZ * 100f);
                    h = h * 31 + Mathf.RoundToInt(s.RadiusM * 100f);
                    h = h * 31 + Mathf.RoundToInt(s.Strength * 1000f);
                }
                return h;
            }
        }
    }
}
