// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    SeamWelder.cs
//  Purpose: Makes the boundary between two native Terrain cells bit-identical, so
//           a grid of cells reads as one continuous world with no crack, no step
//           and no gap a character can fall through.
//
//  WHY THIS EXISTS, AND WHY THE PREVIOUS APPROACH COULD NOT WORK
//
//  The old weld averaged the two shared edge rows and wrote the mean back into
//  both cells. Three things were wrong with that, and together they produced the
//  trench along every boundary rather than preventing it:
//
//   1. It averaged against cells that had not generated yet. An ungenerated cell
//      holds the flat baseline, so mean(real, flat) pulls a real edge halfway down
//      to flat. That is the trench. It was then copied into the override baseline,
//      which made it survive the next bake.
//
//   2. It ran after the heightfield had already been uploaded, so the next cell to
//      apply overwrote the welded row with raw graph output and the seam reopened.
//      Whichever cell landed last kept an unwelded edge.
//
//   3. A mean is not idempotent when one side is stale. Welding twice gives a
//      third value, so the seam drifted with build order instead of converging -
//      which is precisely the "cells not waiting for each other" symptom.
//
//  THE RULE HERE INSTEAD: DETERMINISTIC OWNERSHIP.
//
//  Exactly one cell owns each shared edge, chosen by coordinate and nothing else:
//  of two cells sharing a vertical seam the lower X owns it; of two sharing a
//  horizontal seam the lower Z owns it; of the four cells meeting at a corner the
//  lowest (Z, then X) owns that one sample. The non-owner copies. Copy is
//  idempotent, so welding a hundred times gives the same answer as welding once,
//  and the answer does not depend on which cell baked first. That is what makes
//  the seam immune to load order, to streaming, and to re-generation.
//
//  Because every creator already samples absolute world position, the owner and
//  the non-owner normally agree to the last bit before any weld happens - a cell's
//  sample at its east edge and its neighbour's sample at its west edge are the
//  same world coordinate. The copy exists to absorb the one case where they can
//  legitimately diverge: a non-local modifier (erosion, a wide blur) whose
//  influence reaches past the 16-pixel ghost margin.
//
//  FEATHERING. Snapping one row and leaving the row beside it untouched turns a
//  height difference into a wall one texel wide - a crease that catches light and
//  reads as a seam even though the surface is closed. So the correction is spread
//  inward over several samples with a smoothstep falloff. When the two sides
//  already agree the delta is zero and feathering costs nothing.
//
//  No Unity object is touched here. This operates on the staged float[,] before it
//  is uploaded, which is the whole point: a weld that happens before the write
//  cannot be undone by the write.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// The four boundary rows of one baked heightfield, kept so a neighbour that
    /// bakes later can weld against this cell without the cell being resident or
    /// its TerrainData being read back through native memory.
    /// </summary>
    public class EdgeSet
    {
        /// <summary>Column x = 0, indexed by z.</summary>
        public float[] west;

        /// <summary>Column x = res-1, indexed by z.</summary>
        public float[] east;

        /// <summary>Row z = 0, indexed by x.</summary>
        public float[] south;

        /// <summary>Row z = res-1, indexed by x.</summary>
        public float[] north;

        /// <summary>Heightmap resolution these edges came from.</summary>
        public int resolution;

        /// <summary>
        /// False until a real bake has filled this set. The weld skips any
        /// neighbour whose set is not ready, which is the guard whose absence
        /// created the trench: a flat placeholder must never contribute a height.
        /// </summary>
        public bool ready;

        public bool Matches(int res)
        {
            return ready
                && resolution == res
                && west != null && west.Length == res
                && east != null && east.Length == res
                && south != null && south.Length == res
                && north != null && north.Length == res;
        }
    }

    /// <summary>
    /// Captures and applies shared boundary rows between adjacent terrain cells.
    /// Pure managed math on a staged heightfield - C-01.
    /// </summary>
    public static class SeamWelder
    {
        /// <summary>
        /// How far a boundary correction is blended inward, in heightmap samples.
        /// One would close the surface but leave a one-texel crease; this spreads
        /// the same correction over a slope the eye reads as terrain.
        /// </summary>
        public const int DefaultFeather = 6;

        // Heightfields here are Unity's convention throughout: h[z, x], z running
        // north. Getting this backwards transposes the world, so it is stated once
        // and every loop below follows it.

        /// <summary>Reads the four boundary rows out of a baked heightfield.</summary>
        public static void Capture(float[,] h, EdgeSet into)
        {
            if (h == null || into == null) return;

            int resZ = h.GetLength(0);
            int resX = h.GetLength(1);
            if (resZ != resX) return;   // square cells only; a non-square cell cannot tile
            int res = resX;

            if (into.west == null || into.west.Length != res) into.west = new float[res];
            if (into.east == null || into.east.Length != res) into.east = new float[res];
            if (into.south == null || into.south.Length != res) into.south = new float[res];
            if (into.north == null || into.north.Length != res) into.north = new float[res];

            for (int z = 0; z < res; z++)
            {
                into.west[z] = h[z, 0];
                into.east[z] = h[z, res - 1];
            }

            for (int x = 0; x < res; x++)
            {
                into.south[x] = h[0, x];
                into.north[x] = h[res - 1, x];
            }

            into.resolution = res;
            into.ready = true;
        }

        /// <summary>
        /// Falloff for the inward blend. 1 at the boundary, 0 at the feather
        /// distance, with zero gradient at both ends so the blend leaves no
        /// second crease where it stops.
        /// </summary>
        private static float Falloff(int i, int feather)
        {
            if (feather <= 0) return i == 0 ? 1f : 0f;
            float t = (float)i / feather;
            if (t >= 1f) return 0f;
            return 1f - (t * t * (3f - 2f * t));
        }

        // ------------------------------------------------------------------
        //  OWNERSHIP
        //
        //  Pure functions of coordinate, so both cells reach the same verdict
        //  without communicating and without either needing to be resident.
        //  This is what makes the seam independent of build order.
        // ------------------------------------------------------------------

        /// <summary>True when the cell at cellX owns the seam it shares with cellX + 1.</summary>
        public static bool OwnsEastSeam(int cellX, int neighbourX)
        {
            return cellX < neighbourX;
        }

        /// <summary>True when the cell at cellZ owns the seam it shares with cellZ + 1.</summary>
        public static bool OwnsNorthSeam(int cellZ, int neighbourZ)
        {
            return cellZ < neighbourZ;
        }

        /// <summary>
        /// Whether this cell owns the corner sample shared by the 2x2 block whose
        /// lower-left cell is (blockX, blockZ). Lowest Z wins, then lowest X, so
        /// all four cells agree on one owner.
        /// </summary>
        public static bool OwnsCorner(int cellX, int cellZ, int blockX, int blockZ)
        {
            return cellX == blockX && cellZ == blockZ;
        }

        // ------------------------------------------------------------------
        //  APPLY
        //
        //  Each of these writes the authoritative row into the staged heightfield
        //  and feathers the correction inward. They are idempotent: running one
        //  twice with the same source produces the same field, because the second
        //  run sees a delta of zero.
        // ------------------------------------------------------------------

        /// <summary>Writes an authoritative column into this field's west edge (x = 0).</summary>
        public static void ApplyWest(float[,] h, float[] source, int feather)
        {
            if (h == null || source == null) return;
            int res = h.GetLength(1);
            if (h.GetLength(0) != res || source.Length != res) return;

            for (int z = 0; z < res; z++)
            {
                float delta = source[z] - h[z, 0];
                if (delta == 0f) continue;

                int span = Mathf.Min(feather, res - 1);
                for (int i = 0; i <= span; i++)
                {
                    h[z, i] = Mathf.Clamp01(h[z, i] + delta * Falloff(i, feather));
                }
            }
        }

        /// <summary>Writes an authoritative column into this field's east edge (x = res-1).</summary>
        public static void ApplyEast(float[,] h, float[] source, int feather)
        {
            if (h == null || source == null) return;
            int res = h.GetLength(1);
            if (h.GetLength(0) != res || source.Length != res) return;

            for (int z = 0; z < res; z++)
            {
                float delta = source[z] - h[z, res - 1];
                if (delta == 0f) continue;

                int span = Mathf.Min(feather, res - 1);
                for (int i = 0; i <= span; i++)
                {
                    h[z, res - 1 - i] = Mathf.Clamp01(h[z, res - 1 - i] + delta * Falloff(i, feather));
                }
            }
        }

        /// <summary>Writes an authoritative row into this field's south edge (z = 0).</summary>
        public static void ApplySouth(float[,] h, float[] source, int feather)
        {
            if (h == null || source == null) return;
            int res = h.GetLength(1);
            if (h.GetLength(0) != res || source.Length != res) return;

            for (int x = 0; x < res; x++)
            {
                float delta = source[x] - h[0, x];
                if (delta == 0f) continue;

                int span = Mathf.Min(feather, res - 1);
                for (int i = 0; i <= span; i++)
                {
                    h[i, x] = Mathf.Clamp01(h[i, x] + delta * Falloff(i, feather));
                }
            }
        }

        /// <summary>Writes an authoritative row into this field's north edge (z = res-1).</summary>
        public static void ApplyNorth(float[,] h, float[] source, int feather)
        {
            if (h == null || source == null) return;
            int res = h.GetLength(1);
            if (h.GetLength(0) != res || source.Length != res) return;

            for (int x = 0; x < res; x++)
            {
                float delta = source[x] - h[res - 1, x];
                if (delta == 0f) continue;

                int span = Mathf.Min(feather, res - 1);
                for (int i = 0; i <= span; i++)
                {
                    h[res - 1 - i, x] = Mathf.Clamp01(h[res - 1 - i, x] + delta * Falloff(i, feather));
                }
            }
        }

        /// <summary>
        /// Forces a single corner sample without feathering.
        ///
        /// Run after the four edges, never before: an edge pass writes the whole
        /// boundary row including its two end samples, so a corner set first would
        /// simply be overwritten. cornerX and cornerZ are 0 or res-1.
        /// </summary>
        public static void ApplyCorner(float[,] h, int cornerX, int cornerZ, float value)
        {
            if (h == null) return;
            int resZ = h.GetLength(0);
            int resX = h.GetLength(1);
            if (cornerX < 0 || cornerX >= resX || cornerZ < 0 || cornerZ >= resZ) return;

            h[cornerZ, cornerX] = Mathf.Clamp01(value);
        }
    }
}
