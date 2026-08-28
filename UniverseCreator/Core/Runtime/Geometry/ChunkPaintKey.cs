// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ChunkPaintKey.cs
//  Purpose: Fold the paint strokes into a chunk's cache key so a painted tile rebuilds
//           exactly when - and only when - its strokes actually changed (§ 9.8 G-54).
//           A chunk's build is cached against a key; if the strokes over a chunk are not
//           part of that key, a new stroke changes nothing the cache can see, and the brush
//           appears dead until the author walks far enough to force an unrelated rebuild.
//           So the key must fold in the strokes - but ONLY the strokes that overlap the
//           chunk, or every chunk in the world invalidates on every stroke and painting one
//           corner rebuilds the whole terrain.
//
//  OVERLAP IS A CIRCLE-VERSUS-RECT TEST
//  ------------------------------------
//  A stroke is a disc (centre in world metres, radius in metres); a chunk is an axis-aligned
//  world rectangle. `StrokeOverlapsChunk` clamps the disc centre to the rectangle and compares
//  the distance to the radius - the standard, exact circle/AABB intersection. A stroke that
//  misses the chunk contributes nothing to its key, so that chunk's cache stays valid.
//
//  THE HASH IS DETERMINISTIC AND ORDER-SIGNIFICANT
//  -----------------------------------------------
//  Strokes are quantised to integers (the same RoundToInt scheme the paint store uses) and
//  folded with an unchecked h*31 + term, in list order. Order matters because paint compositing
//  is order-dependent, so two different stroke orders must produce two different keys. Pure and
//  device-free: the self-test pins that a stroke changes the key of the chunks it overlaps and
//  leaves every other chunk's key byte-identical.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Folds paint strokes into a chunk's cache key (§ 9.8 G-54): only strokes whose disc overlaps the chunk's
    /// world rectangle contribute, so one stroke invalidates exactly the chunks it touches and no others.
    /// Deterministic and order-significant; device-free.
    /// </summary>
    public static class ChunkPaintKey
    {
        /// <summary>Whether a stroke's disc overlaps a chunk's axis-aligned world rectangle: the exact
        /// circle/AABB intersection (clamp the centre to the rect, compare distance to radius).</summary>
        public static bool StrokeOverlapsChunk(in PaintStroke s, float minX, float minZ, float maxX, float maxZ)
        {
            float cx = s.WorldX < minX ? minX : (s.WorldX > maxX ? maxX : s.WorldX);
            float cz = s.WorldZ < minZ ? minZ : (s.WorldZ > maxZ ? maxZ : s.WorldZ);
            float dx = s.WorldX - cx, dz = s.WorldZ - cz;
            float r = s.RadiusM < 0f ? 0f : s.RadiusM;
            return dx * dx + dz * dz <= r * r;
        }

        /// <summary>A deterministic integer hash of one stroke, quantising each field to an int before folding
        /// (the same RoundToInt scheme the paint store uses) so the value is bit-identical across runs.</summary>
        public static int HashStroke(in PaintStroke s)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + s.TargetLayer;
                h = h * 31 + Mathf.RoundToInt(s.WorldX * 100f);
                h = h * 31 + Mathf.RoundToInt(s.WorldZ * 100f);
                h = h * 31 + Mathf.RoundToInt(s.RadiusM * 100f);
                h = h * 31 + Mathf.RoundToInt(s.Strength * 1000f);
                h = h * 31 + Mathf.RoundToInt(s.FalloffExponent * 1000f);
                return h;
            }
        }

        /// <summary>
        /// The chunk's paint contribution to its cache key: fold, in list order, the strokes whose disc overlaps the
        /// chunk rectangle. A chunk no stroke overlaps returns the empty seed, so its cache stays valid.
        /// Order-significant, because paint compositing is order-dependent.
        /// </summary>
        public static int ChunkStrokeKey(IReadOnlyList<PaintStroke> strokes, float minX, float minZ, float maxX, float maxZ)
        {
            unchecked
            {
                int h = 17;   // empty seed: a chunk with no overlapping stroke folds to this
                if (strokes != null)
                    for (int i = 0; i < strokes.Count; i++)
                    {
                        PaintStroke s = strokes[i];
                        if (StrokeOverlapsChunk(in s, minX, minZ, maxX, maxZ))
                            h = h * 31 + HashStroke(in s);
                    }
                return h;
            }
        }

        /// <summary>Combine a chunk's geometry build key with its paint key into the final cache key. Both a
        /// geometry change and a stroke change over this chunk therefore invalidate its cached build.</summary>
        public static int CombineWithBuildKey(int buildKey, int strokeKey)
        {
            unchecked { return buildKey * 31 + strokeKey; }
        }
    }
}
