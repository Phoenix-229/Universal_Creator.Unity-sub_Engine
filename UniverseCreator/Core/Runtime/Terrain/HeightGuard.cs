// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HeightGuard.cs
//  Purpose: The last gate a heightfield passes before it becomes a Terrain.
//           Rejects values that are not numbers, and removes single-sample
//           needles that no landscape produces.
//
//  WHY THIS EXISTS
//  ---------------
//  The old guard was a Mathf.Clamp01 loop commented "definitively prevents
//  extreme vertex spikes". It does not. Unity implements Clamp01 as
//
//      if (value < 0F) return 0F; else if (value > 1F) return 1F; else return value;
//
//  and NaN fails BOTH comparisons, so it falls through the else and is returned
//  unchanged. A single NaN reaching SetHeights is what draws a vertex at an
//  undefined position - the needle through the terrain. Every one of the ways a
//  graph can produce one is ordinary arithmetic: 0/0 in a slope divide, a
//  negative under a sqrt, Mathf.Pow of a negative base, an erosion pass that
//  divides by an accumulated flow of zero. Clamping cannot see any of them.
//
//  So this file does two different jobs that were being confused for one:
//
//    Sanitize    - is every sample a real number in range? NaN and infinity are
//                  not clamped, they are replaced, because there is no sensible
//                  number to clamp them to.
//
//    RemoveSpikes - is every sample plausible next to its neighbours? A cliff is
//                  a run of samples that all step together and is left alone. A
//                  needle is ONE sample standing away from all four of its
//                  neighbours, which is a defect however finite its value is.
//
//  The needle test is the standard outlier pass used to condition digital
//  elevation models before hydrology runs on them (Lindsay & Creed 2005,
//  "Removal of artifact depressions from digital elevation data"): judge each
//  cell against the range of its neighbourhood rather than against a global
//  threshold, and replace an outlier with the neighbourhood median so the
//  surface continues through the hole instead of gaining a crater where the
//  needle was.
//
//  Cost is one linear pass over the heightfield per chunk apply - 66k samples at
//  257 - with no allocation unless something is actually wrong. It is cheap
//  enough to leave on permanently, which matters more than being clever: a
//  safeguard that only runs when you suspect a problem is not a safeguard.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    public static class HeightGuard
    {
        /// <summary>
        /// Steepest wall the needle test will accept as real terrain. Above this a
        /// one-sample step is treated as a defect rather than a cliff.
        /// </summary>
        /// <remarks>
        /// 85 degrees, not 90: at 90 the permitted step is infinite and nothing is
        /// ever a spike. 85 over a 3.13 m sample spacing allows a 36 m wall between
        /// two adjacent samples, which is past anything erosion or a voronoi ridge
        /// produces and still well clear of a deliberately vertical cliff node.
        /// </remarks>
        public const float MaxWallDegrees = 85f;

        /// <summary>
        /// Converts the wall limit into the normalised units a heightmap stores.
        /// </summary>
        /// <remarks>
        /// A heightmap sample is 0..1 across the world's full height, so the same
        /// physical slope is a different number in every world - a 36 m step is 0.06
        /// in a 600 m world and 0.36 in a 100 m one. Deriving it per call is what
        /// keeps the guard from being either useless on tall worlds or destructive
        /// on short ones.
        /// </remarks>
        public static float MaxStepNormalised(float metresPerSample, float maxHeightMetres)
        {
            if (metresPerSample <= 0f || maxHeightMetres <= 0f) return 1f;

            float metres = metresPerSample * Mathf.Tan(MaxWallDegrees * Mathf.Deg2Rad);

            // Never below a hundredth of the world height. Without a floor, a very
            // fine heightmap on a very tall world computes a step so small that
            // ordinary mountain slopes read as spikes and the guard flattens the
            // terrain it was meant to protect.
            return Mathf.Max(0.01f, metres / maxHeightMetres);
        }

        /// <summary>
        /// Replaces every sample that is not a finite number, and clamps the rest
        /// into [0,1]. Returns how many samples had to be replaced.
        /// </summary>
        /// <remarks>
        /// A repaired sample takes the value of the last good sample to its left,
        /// falling back to the row above, falling back to zero at the very first
        /// sample. Continuing the surface is deliberate: substituting a constant
        /// would turn a NaN needle into a NaN-shaped hole, which is just as visible
        /// and harder to recognise.
        /// </remarks>
        public static int Sanitize(float[,] heights)
        {
            if (heights == null) return 0;

            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            int repaired = 0;

            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float v = heights[z, x];

                    // float.IsNaN and IsInfinity rather than a comparison: the whole
                    // point is that NaN compares false against everything, so any test
                    // written as an inequality misses it.
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        float fill = 0f;
                        if (x > 0) fill = heights[z, x - 1];
                        else if (z > 0) fill = heights[z - 1, x];

                        // The fallback can itself be a value this pass has not reached
                        // yet only at (0,0), where it is the literal zero above.
                        heights[z, x] = Mathf.Clamp01(fill);
                        repaired++;
                        continue;
                    }

                    if (v < 0f) heights[z, x] = 0f;
                    else if (v > 1f) heights[z, x] = 1f;
                }
            }

            return repaired;
        }

        /// <summary>
        /// Removes single-sample needles: samples that stand further than
        /// <paramref name="maxStep"/> away from the whole of their four-neighbour
        /// range, in either direction. Returns how many were replaced.
        /// </summary>
        /// <remarks>
        /// Judged against the neighbourhood MIN and MAX rather than an average,
        /// which is what separates a needle from a cliff. On a cliff the sample sits
        /// inside the range its neighbours already span - the two along the cliff
        /// face agree with it - so the test passes and the cliff survives at full
        /// sharpness. A needle is outside the range on every side, which no
        /// continuous surface produces.
        ///
        /// Two passes, not one: every needle is found against the ORIGINAL
        /// neighbours before any repair is written, so a cluster of two adjacent
        /// needles cannot vouch for each other, and a repair cannot cascade into the
        /// samples still to be tested.
        ///
        /// Corners are skipped. With two neighbours there is not enough
        /// neighbourhood to tell a needle from a corner that legitimately rises,
        /// and the corner is the one sample the seam weld overwrites anyway.
        /// </remarks>
        public static int RemoveSpikes(float[,] heights, float maxStep)
        {
            if (heights == null || maxStep <= 0f) return 0;

            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            if (resZ < 3 || resX < 3) return 0;

            // Allocated lazily. The overwhelmingly common case is a clean
            // heightfield, and this runs on every chunk apply - paying a list
            // allocation per chunk to hold nothing would be a real cost on a
            // 25-cell world.
            List<int> found = null;

            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float v = heights[z, x];

                    float[] nb = new float[4];
                    int n = 0;

                    if (x > 0)        { nb[n++] = heights[z, x - 1]; }
                    if (x < resX - 1) { nb[n++] = heights[z, x + 1]; }
                    if (z > 0)        { nb[n++] = heights[z - 1, x]; }
                    if (z < resZ - 1) { nb[n++] = heights[z + 1, x]; }

                    if (n < 3) continue;

                    // Sort the neighbours (without allocation, using simple swaps)
                    if (nb[0] > nb[1]) { float t = nb[0]; nb[0] = nb[1]; nb[1] = t; }
                    if (n > 2)
                    {
                        if (nb[1] > nb[2]) { float t = nb[1]; nb[1] = nb[2]; nb[2] = t; }
                        if (nb[0] > nb[1]) { float t = nb[0]; nb[0] = nb[1]; nb[1] = t; }
                    }
                    if (n > 3)
                    {
                        if (nb[2] > nb[3]) { float t = nb[2]; nb[2] = nb[3]; nb[3] = t; }
                        if (nb[1] > nb[2]) { float t = nb[1]; nb[1] = nb[2]; nb[2] = t; }
                        if (nb[0] > nb[1]) { float t = nb[0]; nb[0] = nb[1]; nb[1] = t; }
                    }

                    // nb[0] is lowest, nb[n-1] is highest.
                    // To catch 1-sample and 2-sample spikes without destroying ridges (which have 2 high neighbours),
                    // we check if v stands away from the SECOND highest / lowest neighbour.
                    float hi2 = nb[n - 2];
                    float lo2 = nb[1];

                    if (v - hi2 > maxStep || lo2 - v > maxStep)
                    {
                        if (found == null) found = new List<int>(16);
                        found.Add(z * resX + x);
                    }
                }
            }

            if (found == null) return 0;

            for (int i = 0; i < found.Count; i++)
            {
                int idx = found[i];
                int z = idx / resX;
                int x = idx - z * resX;
                heights[z, x] = NeighbourMedian(heights, z, x, resZ, resX);
            }

            return found.Count;
        }

        // Median of the available four neighbours. Median rather than mean because a
        // needle usually sits next to at least one sample the same defect already
        // pulled part of the way with it, and an average carries that error into the
        // repair while a median discards it.
        private static float NeighbourMedian(float[,] h, int z, int x, int resZ, int resX)
        {
            float a = 0f, b = 0f, c = 0f, d = 0f;
            int n = 0;

            if (x > 0)        { a = h[z, x - 1]; n++; }
            if (x < resX - 1) { if (n == 1) b = h[z, x + 1]; else a = h[z, x + 1]; n++; }
            if (z > 0)        { if (n == 2) c = h[z - 1, x]; else if (n == 1) b = h[z - 1, x]; else a = h[z - 1, x]; n++; }
            if (z < resZ - 1) { if (n == 3) d = h[z + 1, x]; else if (n == 2) c = h[z + 1, x]; else if (n == 1) b = h[z + 1, x]; else a = h[z + 1, x]; n++; }

            if (n == 0) return 0f;
            if (n == 1) return a;
            if (n == 2) return (a + b) * 0.5f;

            // Sorting three or four values is a fixed sequence of swaps, so it is
            // written out rather than handed to Array.Sort - this runs once per
            // needle and must not allocate.
            if (a > b) { float t = a; a = b; b = t; }
            if (n == 3) return b > c ? (a > c ? a : c) : b;

            if (c > d) { float t = c; c = d; d = t; }
            float mid1 = a > c ? a : c;   // larger of the two lows
            float mid2 = b < d ? b : d;   // smaller of the two highs
            return (mid1 + mid2) * 0.5f;
        }

        /// <summary>
        /// Runs both passes and reports once, naming the cell, if either had to act.
        /// </summary>
        /// <remarks>
        /// One log line per affected chunk rather than per sample. A NaN storm can be
        /// tens of thousands of samples, and a per-sample message would bury the
        /// editor console and stall it doing so - which reads as the terrain having
        /// hung rather than as terrain having been repaired. The counts are what is
        /// diagnostic: "repaired 4" is a graph edge case, "repaired 60000" is a node
        /// producing nothing but NaN.
        /// </remarks>
        public static void Guard(float[,] heights, float metresPerSample, float maxHeightMetres,
                                 string cellLabel, Object context)
        {
            if (heights == null) return;

            int repaired = Sanitize(heights);
            int needles = RemoveSpikes(heights, MaxStepNormalised(metresPerSample, maxHeightMetres));

            if (repaired == 0 && needles == 0) return;

            Debug.LogWarning(
                "Universe Creator: height guard acted on " + (cellLabel ?? "a chunk") + " - "
                + repaired + " sample(s) were not a finite number, "
                + needles + " single-sample spike(s) removed. "
                + "The surface was repaired, but the graph produced values it should not have: "
                + "check divides, sqrt and pow inputs in the nodes feeding Height Output.",
                context);
        }
    }
}
