// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Erosion.cs
//  Purpose: Hydraulic, thermal and wind erosion solvers over a Matrix heightfield.
//
//  HYDRAULIC PIPELINE (5 stages, per the Module 1 spec)
//  ----------------------------------------------------
//    1. SetOrder              sort pixel indices by height, ascending
//    2. MaskBorders           mark border pixels -1 so they are skipped
//    3. CreateTorrents        cast rain, accumulate flow downhill (Moore 3x3)
//    4. Erode                 cut material where flow is strong, bank it as mudflow
//    5. TransferSettleMudflow redistribute mudflow downhill, then deposit it
//
//  WHY THE BORDER MASK EXISTS: stages 3-5 read the 4- and 8-neighbourhood of a
//  pixel by raw index offset with no bounds test, which is what makes them fast.
//  Masking the border ring to -1 in stage 2 is what makes that safe. Any new
//  stage added later must honour the same contract.
//
//  THREAD SAFETY: every method is static and writes only to matrices passed in
//  by the caller. Two threads eroding two different matrices never interact.
//
//  Reference: the hydraulic stages implement the standard sort-by-height /
//  cast-torrents / erode / transfer-settle formulation of stream-power erosion.
//  The thermal solver follows Musgrave's talus-angle relaxation and the wind
//  solver the standard saltation pickup/deposition model; both are written from
//  the formulations cited in the design document. Improvements are called out
//  inline.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// Static erosion solvers. See the file header for the pipeline contract.
    /// </summary>
    public static class Erosion
    {
        // =====================================================================
        //  NEIGHBOURHOOD HELPERS
        // =====================================================================

        /// <summary>
        /// The 4-neighbourhood plus centre, laid out as:
        /// <code>
        ///        [0]
        ///   [1]  [2]  [3]
        ///        [4]
        /// </code>
        /// Index 2 is the centre. Reads by raw offset, so the caller must have
        /// excluded border pixels first.
        /// </summary>
        public struct Cross
        {
            public float v0, v1, v2, v3, v4;

            public Cross(float[] m, int i, int sizeX)
            {
                v0 = m[i - sizeX];
                v1 = m[i - 1];
                v2 = m[i];
                v3 = m[i + 1];
                v4 = m[i + sizeX];
            }

            public Cross(float value)
            {
                v0 = v1 = v2 = v3 = v4 = value;
            }

            public float Centre { get { return v2; } }

            public void SetToMatrix(float[] m, int i, int sizeX)
            {
                m[i - sizeX] = v0;
                m[i - 1] = v1;
                m[i] = v2;
                m[i + 1] = v3;
                m[i + sizeX] = v4;
            }

            public void AddToMatrix(float[] m, int i, int sizeX)
            {
                m[i - sizeX] += v0;
                m[i - 1] += v1;
                m[i] += v2;
                m[i + 1] += v3;
                m[i + sizeX] += v4;
            }

            public float Sum() { return v0 + v1 + v2 + v3 + v4; }

            public float Min()
            {
                float m = v0;
                if (v1 < m) m = v1;
                if (v2 < m) m = v2;
                if (v3 < m) m = v3;
                if (v4 < m) m = v4;
                return m;
            }

            /// <summary>Lowest of the four neighbours, ignoring the centre.</summary>
            public float MinSides()
            {
                float m = v0;
                if (v1 < m) m = v1;
                if (v3 < m) m = v3;
                if (v4 < m) m = v4;
                return m;
            }

            /// <summary>Highest of the four neighbours, ignoring the centre.</summary>
            public float MaxSides()
            {
                float m = v0;
                if (v1 > m) m = v1;
                if (v3 > m) m = v3;
                if (v4 > m) m = v4;
                return m;
            }

            public float AvgSides() { return (v0 + v1 + v3 + v4) * 0.25f; }

            public static Cross operator +(Cross c, float f)
            {
                c.v0 += f; c.v1 += f; c.v2 += f; c.v3 += f; c.v4 += f; return c;
            }

            public static Cross operator -(Cross c, float f)
            {
                c.v0 -= f; c.v1 -= f; c.v2 -= f; c.v3 -= f; c.v4 -= f; return c;
            }

            public static Cross operator -(Cross a, Cross b)
            {
                a.v0 -= b.v0; a.v1 -= b.v1; a.v2 -= b.v2; a.v3 -= b.v3; a.v4 -= b.v4; return a;
            }

            public static Cross operator *(Cross c, float f)
            {
                c.v0 *= f; c.v1 *= f; c.v2 *= f; c.v3 *= f; c.v4 *= f; return c;
            }

            /// <summary>Raises every cell to at least <paramref name="f"/>.</summary>
            public static Cross RaiseTo(Cross c, float f)
            {
                if (c.v0 < f) c.v0 = f;
                if (c.v1 < f) c.v1 = f;
                if (c.v2 < f) c.v2 = f;
                if (c.v3 < f) c.v3 = f;
                if (c.v4 < f) c.v4 = f;
                return c;
            }

            /// <summary>
            /// Distributes <paramref name="liquid"/> across this height cross the
            /// way standing water would settle: fills the lowest cells first
            /// until a common surface level is reached. Returns the depth added
            /// per cell, which always sums back to the input volume.
            /// </summary>
            public static Cross Pour(Cross height, float liquid)
            {
                if (liquid < 1e-7f) return new Cross(0f);

                // Start from the level surface the total volume would produce.
                float avg = (height.Sum() + liquid) / 5f;

                Cross pour = new Cross(avg) - height;
                pour = RaiseTo(pour, 0f);   // cells already above that level take none

                // Clipping the negatives above pushed the total over the input
                // volume, so lower the wetted cells until the sum matches again.
                int wetCells = 0;
                if (pour.v0 > 1e-4f) wetCells++;
                if (pour.v1 > 1e-4f) wetCells++;
                if (pour.v2 > 1e-4f) wetCells++;
                if (pour.v3 > 1e-4f) wetCells++;
                if (pour.v4 > 1e-4f) wetCells++;

                if (wetCells == 0) return pour;

                float excess = (pour.Sum() - liquid) / wetCells;
                pour = pour - excess;
                pour = RaiseTo(pour, 0f);

                // One pass is usually exact. Where clipping bit twice, rescale.
                float sum = pour.Sum();
                float err = sum - liquid;
                if (err < 0) err = -err;

                if (err > 1e-5f)
                {
                    if (sum < 1e-6f) return new Cross(0f);
                    pour = pour * (liquid / sum);
                }

                return pour;
            }
        }

        /// <summary>
        /// The full Moore 8-neighbourhood plus centre, in row-major order:
        /// <code>
        ///   [0] [1] [2]
        ///   [3] [4] [5]
        ///   [6] [7] [8]
        /// </code>
        /// Index 4 is the centre. Used by the flow accumulation stage, which
        /// needs diagonals so torrents do not develop axis-aligned streaking.
        /// </summary>
        public struct MooreCross
        {
            public float[] vals;

            public static MooreCross Zero()
            {
                return new MooreCross { vals = new float[9] };
            }

            public MooreCross(float[] m, int i, int sizeX)
            {
                vals = new float[]
                {
                    m[i - 1 - sizeX], m[i - sizeX], m[i + 1 - sizeX],
                    m[i - 1],         m[i],         m[i + 1],
                    m[i - 1 + sizeX], m[i + sizeX], m[i + 1 + sizeX]
                };
            }

            public float Centre { get { return vals[4]; } }

            public void AddToMatrix(float[] m, int i, int sizeX)
            {
                m[i - 1 - sizeX] += vals[0]; m[i - sizeX] += vals[1]; m[i + 1 - sizeX] += vals[2];
                m[i - 1] += vals[3]; m[i] += vals[4]; m[i + 1] += vals[5];
                m[i - 1 + sizeX] += vals[6]; m[i + sizeX] += vals[7]; m[i + 1 + sizeX] += vals[8];
            }

            public float Sum()
            {
                float s = 0f;
                for (int i = 0; i < 9; i++) s += vals[i];
                return s;
            }

            /// <summary>Per-cell (scalar - cell), i.e. how far each cell sits below a level.</summary>
            public static MooreCross SubtractFrom(float f, MooreCross c)
            {
                MooreCross r = Zero();
                for (int i = 0; i < 9; i++) r.vals[i] = f - c.vals[i];
                return r;
            }

            public static MooreCross operator /(MooreCross c, float f)
            {
                MooreCross r = Zero();
                for (int i = 0; i < 9; i++) r.vals[i] = c.vals[i] / f;
                return r;
            }

            public static MooreCross operator *(MooreCross c, float f)
            {
                MooreCross r = Zero();
                for (int i = 0; i < 9; i++) r.vals[i] = c.vals[i] * f;
                return r;
            }

            /// <summary>Raises every cell to at least <paramref name="f"/>.</summary>
            public static MooreCross RaiseTo(MooreCross c, float f)
            {
                MooreCross r = Zero();
                for (int i = 0; i < 9; i++) r.vals[i] = c.vals[i] < f ? f : c.vals[i];
                return r;
            }
        }

        // =====================================================================
        //  HYDRAULIC EROSION
        // =====================================================================

        /// <summary>Tunable parameters for <see cref="Hydraulic"/>.</summary>
        [Serializable]
        public struct HydraulicSettings
        {
            /// <summary>How many full erode/settle passes to run.</summary>
            public int iterations;

            /// <summary>0-1. How well the rock resists being cut. Higher erodes less.</summary>
            public float durability;

            /// <summary>Scales how much material each pass removes.</summary>
            public float erosionAmount;

            /// <summary>Fraction of removed material that becomes transportable mudflow.</summary>
            public float sedimentAmount;

            /// <summary>Mudflow redistribution sub-passes. Higher spreads sediment further.</summary>
            public int fluidityIterations;

            /// <summary>Post-pass blur sigma, in pixels. Takes the edge off cut faces.</summary>
            public float relax;

            public static HydraulicSettings Default
            {
                get
                {
                    return new HydraulicSettings
                    {
                        iterations = 3,
                        durability = 0.9f,
                        erosionAmount = 1f,
                        sedimentAmount = 0.5f,
                        fluidityIterations = 3,
                        relax = 0.5f
                    };
                }
            }
        }

        /// <summary>
        /// Runs the full hydraulic pipeline in place.
        /// </summary>
        /// <param name="heights">Field to erode. Modified in place.</param>
        /// <param name="settings">Solver parameters.</param>
        /// <param name="sedimentOut">
        /// Optional. Receives accumulated deposition, for SedimentMapNode and for
        /// texture nodes that want to paint silt in the valleys.
        /// </param>
        /// <param name="flowOut">
        /// Optional. Receives the final flow accumulation, for FlowMapNode.
        /// </param>
        /// <param name="onProgress">
        /// Optional 0-1 progress callback, invoked once per iteration. Called
        /// from the worker thread, so implementations must not touch the Unity API.
        /// </param>
        /// <param name="isCancelled">
        /// Optional cancellation probe, checked once per iteration. Returning
        /// true leaves <paramref name="heights"/> partially eroded but valid.
        /// </param>
        public static void Hydraulic(Matrix heights, HydraulicSettings settings,
                                     Matrix sedimentOut = null, Matrix flowOut = null,
                                     Action<float> onProgress = null,
                                     Func<bool> isCancelled = null)
        {
            if (heights == null) throw new ArgumentNullException("heights");

            int sizeX = heights.rect.size.x;
            int sizeZ = heights.rect.size.z;

            // Below 3x3 there is no interior once borders are masked.
            if (sizeX < 3 || sizeZ < 3) return;

            int iterations = settings.iterations < 1 ? 1 : settings.iterations;

            int[] order = new int[heights.Count];
            Matrix torrents = heights.Blank();
            Matrix mudflow = heights.Blank();
            Matrix sediment = sedimentOut ?? heights.Blank();

            for (int iter = 0; iter < iterations; iter++)
            {
                if (isCancelled != null && isCancelled()) return;

                SetOrder(heights, order);
                MaskBorders(order, heights.rect);
                CreateTorrents(heights, order, torrents);
                Erode(heights, torrents, mudflow, order,
                      settings.durability, settings.erosionAmount, settings.sedimentAmount);
                TransferSettleMudflow(heights, mudflow, sediment, order, settings.fluidityIterations);

                if (onProgress != null) onProgress((iter + 1) / (float)iterations);
            }

            if (flowOut != null)
            {
                Array.Copy(torrents.arr, flowOut.arr, torrents.arr.Length);
                MatrixOperations.Normalize(flowOut);
            }

            if (settings.relax > 0.01f)
            {
                Matrix relaxed = MatrixOperations.GaussianBlur(heights, settings.relax);
                Array.Copy(relaxed.arr, heights.arr, heights.arr.Length);
            }

            // Erosion divides by slope sums; a degenerate field can still leak a
            // NaN through. Catching it here keeps it out of TerrainData, where it
            // would blank the chunk with no indication of the cause.
            if (heights.HasInvalidValues())
            {
                for (int i = 0; i < heights.arr.Length; i++)
                    if (float.IsNaN(heights.arr[i]) || float.IsInfinity(heights.arr[i]))
                        heights.arr[i] = 0f;
            }
        }

        /// <summary>
        /// Stage 1. Fills <paramref name="order"/> with pixel indices sorted by
        /// height, lowest first. Later stages walk it backwards to process the
        /// highest ground first, which is the order water actually flows.
        /// </summary>
        public static void SetOrder(Matrix heights, int[] order)
        {
            int length = heights.Count;

            for (int i = 0; i < length; i++) order[i] = i;

            // Sort a scratch copy so the caller's heights are left untouched.
            float[] keys = new float[length];
            Array.Copy(heights.arr, keys, length);
            Array.Sort(keys, order);
        }

        /// <summary>
        /// Stage 2. Replaces border entries with -1. Later stages skip negatives,
        /// which is what lets them read neighbours without bounds checks.
        /// </summary>
        public static void MaskBorders(int[] order, CoordRect rect)
        {
            int sizeX = rect.size.x;
            int sizeZ = rect.size.z;

            for (int j = 0; j < order.Length; j++)
            {
                int pos = order[j];
                if (pos < 0) continue;

                int x = pos % sizeX;
                int z = pos / sizeX;

                if (x == 0 || z == 0 || x == sizeX - 1 || z == sizeZ - 1)
                    order[j] = -1;
            }
        }

        /// <summary>
        /// Stage 3. Casts one unit of rain everywhere, then walks from the
        /// highest pixel down, handing each pixel's accumulated water to its
        /// lower neighbours in proportion to how far below they sit. Because the
        /// walk is strictly high-to-low, every donor is fully accumulated before
        /// it gives, so a single pass yields true drainage totals.
        /// </summary>
        public static void CreateTorrents(Matrix heights, int[] order, Matrix torrents)
        {
            int sizeX = heights.rect.size.x;

            for (int i = 0; i < torrents.arr.Length; i++) torrents.arr[i] = 1f;

            for (int j = heights.Count - 1; j >= 0; j--)
            {
                int pos = order[j];
                if (pos < 0) continue;

                MooreCross height = new MooreCross(heights.arr, pos, sizeX);
                MooreCross torrent = new MooreCross(torrents.arr, pos, sizeX);

                // Clamp pathological accumulation in near-flat basins, which can
                // otherwise run away and overflow.
                float centreTorrent = torrent.vals[4];
                if (centreTorrent > 2e8f) centreTorrent = 2e8f;

                // How far each neighbour lies below the centre.
                MooreCross delta = MooreCross.SubtractFrom(height.vals[4], height);
                delta = MooreCross.RaiseTo(delta, 0f);   // uphill neighbours receive nothing

                float sum = delta.Sum();
                if (sum <= 1e-5f) continue;               // pit or plateau: water stays put

                MooreCross share = (delta / sum) * centreTorrent;
                share.AddToMatrix(torrents.arr, pos, sizeX);
            }
        }

        /// <summary>
        /// Stage 4. Cuts material wherever flow is strong enough to overcome the
        /// rock's durability, and banks the cut volume as mudflow.
        ///
        /// The cut is bounded by the erode line, halfway between the pixel and
        /// its lowest neighbour. That bound is what stops a pixel from ever being
        /// cut below the ground around it, which would punch spikes into the mesh.
        /// </summary>
        public static void Erode(Matrix heights, Matrix torrents, Matrix mudflow, int[] order,
                                 float durability = 0.9f, float erosionAmount = 1f,
                                 float sedimentAmount = 0.5f)
        {
            int sizeX = heights.rect.size.x;

            for (int i = 0; i < mudflow.arr.Length; i++) mudflow.arr[i] = 0f;

            for (int j = heights.Count - 1; j >= 0; j--)
            {
                int pos = order[j];
                if (pos < 0) continue;

                Cross height = new Cross(heights.arr, pos, sizeX);
                float hMin = height.Min();

                float erodeLine = (heights.arr[pos] + hMin) * 0.5f;
                if (heights.arr[pos] < erodeLine) continue;

                float raised = heights.arr[pos] - erodeLine;

                // Flow above the baseline rain of 1 is what does the cutting.
                float maxRaised = raised * (torrents.arr[pos] - 1f) * (1f - durability);
                if (raised > maxRaised) raised = maxRaised;
                if (raised <= 0f) continue;

                raised *= erosionAmount;

                heights.arr[pos] -= raised;
                mudflow.arr[pos] += raised * sedimentAmount;
            }
        }

        /// <summary>
        /// Stage 5. Moves the banked mudflow downhill, then deposits what is left
        /// back onto the heightfield.
        /// </summary>
        public static void TransferSettleMudflow(Matrix heights, Matrix mudflow, Matrix sediments,
                                                 int[] order, int fluidityIterations = 3)
        {
            TransferMudflow(heights, mudflow, sediments, order, fluidityIterations);
            SettleMudflow(heights, mudflow);
        }

        /// <summary>
        /// Redistributes mudflow downhill using the pour model, so sediment pools
        /// in hollows instead of clinging to the slope it was cut from. Each extra
        /// fluidity iteration lets it travel roughly one more pixel.
        /// </summary>
        public static void TransferMudflow(Matrix heights, Matrix mudflow, Matrix sediments,
                                           int[] order, int fluidityIterations = 3)
        {
            int sizeX = heights.rect.size.x;

            if (sediments != null)
                for (int i = 0; i < sediments.arr.Length; i++) sediments.arr[i] = 0f;

            if (fluidityIterations < 1) fluidityIterations = 1;

            for (int l = 0; l < fluidityIterations; l++)
            {
                for (int j = heights.Count - 1; j >= 0; j--)
                {
                    int pos = order[j];
                    if (pos < 0) continue;

                    Cross height = new Cross(heights.arr, pos, sizeX);
                    Cross sediment = new Cross(mudflow.arr, pos, sizeX);

                    float sedimentSum = sediment.Sum();
                    if (sedimentSum < 1e-5f) continue;

                    Cross pour = Cross.Pour(height, sedimentSum);

                    pour.SetToMatrix(mudflow.arr, pos, sizeX);
                    if (sediments != null) pour.AddToMatrix(sediments.arr, pos, sizeX);
                }
            }
        }

        /// <summary>Deposits the settled mudflow back onto the heightfield.</summary>
        public static void SettleMudflow(Matrix heights, Matrix mudflow)
        {
            for (int i = 0; i < heights.arr.Length; i++)
                heights.arr[i] += mudflow.arr[i];
        }

        // =====================================================================
        //  THERMAL EROSION
        // =====================================================================

        /// <summary>Tunable parameters for <see cref="Thermal"/>.</summary>
        [Serializable]
        public struct ThermalSettings
        {
            /// <summary>Number of relaxation passes.</summary>
            public int iterations;

            /// <summary>
            /// Angle of repose in degrees. Slopes steeper than this shed material.
            /// 30-45 covers most natural scree; sand sits near 34.
            /// </summary>
            public float talusAngle;

            /// <summary>0-1. Fraction of the excess moved per pass.</summary>
            public float amount;

            /// <summary>
            /// Post-pass blur sigma, in pixels. Talus relaxation drives every steep
            /// slope to EXACTLY the angle of repose, so at high iteration counts the
            /// land breaks into flat plates meeting at hard creases - the "joined
            /// stones" / faceted mountainside the author saw. This is the twin of the
            /// hydraulic solver's own <see cref="HydraulicSettings.relax"/>: one light
            /// blur after the talus passes rounds those creases without undoing the
            /// scree fans. 0 disables it (the original behaviour).
            /// </summary>
            public float relax;

            public static ThermalSettings Default
            {
                get
                {
                    return new ThermalSettings
                    {
                        iterations = 8,
                        talusAngle = 35f,
                        amount = 0.5f,
                        relax = 0.5f
                    };
                }
            }
        }

        /// <summary>
        /// Thermal weathering. Any slope steeper than the talus angle sheds
        /// material to its downhill neighbours until it relaxes back to that
        /// angle, which is what builds scree fans below cliffs.
        ///
        /// Deltas are accumulated into a scratch buffer and applied at the end of
        /// each pass. Writing directly would make the result depend on scan
        /// order and bias material toward one corner of the map.
        /// </summary>
        public static void Thermal(Matrix heights, ThermalSettings settings,
                                   Action<float> onProgress = null,
                                   Func<bool> isCancelled = null)
        {
            if (heights == null) throw new ArgumentNullException("heights");

            int sizeX = heights.rect.size.x;
            int sizeZ = heights.rect.size.z;
            if (sizeX < 3 || sizeZ < 3) return;

            int iterations = settings.iterations < 1 ? 1 : settings.iterations;
            float amount = InterpolationMath.Clamp01(settings.amount);
            if (amount < 1e-4f) return;

            Vector2 pixelSize = heights.PixelSize;
            float worldHeight = heights.worldHeight > 1e-6f ? heights.worldHeight : 1f;

            // Convert the talus angle into the height delta, in normalised units,
            // that a single pixel step is allowed to sustain.
            const float DegToRad = 0.017453292519943295f;
            float talusRad = settings.talusAngle * DegToRad;
            float tanTalus = (float)System.Math.Tan(talusRad);

            float straightRun = pixelSize.x;
            float diagonalRun = (float)System.Math.Sqrt(pixelSize.x * pixelSize.x +
                                                        pixelSize.y * pixelSize.y);

            float maxDeltaStraight = tanTalus * straightRun / worldHeight;
            float maxDeltaDiagonal = tanTalus * diagonalRun / worldHeight;

            float[] delta = new float[heights.arr.Length];

            // Diagonal neighbours are further away, so they get a larger allowance
            // and a smaller share of the transported volume.
            int[] offX = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] offZ = { -1, -1, -1, 0, 0, 1, 1, 1 };
            bool[] isDiagonal = { true, false, true, false, false, true, false, true };

            // Hoisted out of the pixel loop: allocating this per pixel would mean
            // one array per cell per pass, which dominates the solver's cost.
            float[] excess = new float[8];

            for (int iter = 0; iter < iterations; iter++)
            {
                if (isCancelled != null && isCancelled()) return;

                Array.Clear(delta, 0, delta.Length);

                for (int z = 1; z < sizeZ - 1; z++)
                {
                    for (int x = 1; x < sizeX - 1; x++)
                    {
                        int pos = z * sizeX + x;
                        float centre = heights.arr[pos];

                        float totalExcess = 0f;
                        float maxExcess = 0f;

                        for (int n = 0; n < 8; n++)
                        {
                            excess[n] = 0f;

                            int npos = (z + offZ[n]) * sizeX + (x + offX[n]);
                            float drop = centre - heights.arr[npos];
                            if (drop <= 0f) continue;

                            float allowance = isDiagonal[n] ? maxDeltaDiagonal : maxDeltaStraight;
                            float over = drop - allowance;
                            if (over <= 0f) continue;

                            // Half the excess: the neighbour rises as this pixel
                            // falls, so half each lands them exactly at the angle.
                            excess[n] = over * 0.5f;
                            totalExcess += excess[n];
                            if (excess[n] > maxExcess) maxExcess = excess[n];
                        }

                        if (totalExcess <= 1e-7f) continue;

                        // Musgrave's cap: the volume shed is set by the STEEPEST
                        // single neighbour, then split among all downhill
                        // neighbours in proportion to their excess.
                        //
                        // Scaling by totalExcess instead is the obvious-looking
                        // mistake and it is unstable: a pixel atop a vertical
                        // wall has three downhill neighbours, so it would shed
                        // roughly three times too much in one pass, invert the
                        // profile, and rebuild the wall one pixel over. Verified
                        // by the thermal slope test, which caught exactly that.
                        float move = maxExcess * amount;
                        float scale = move / totalExcess;

                        for (int n = 0; n < 8; n++)
                        {
                            if (excess[n] <= 0f) continue;

                            float amountMoved = excess[n] * scale;
                            int npos = (z + offZ[n]) * sizeX + (x + offX[n]);

                            delta[pos] -= amountMoved;
                            delta[npos] += amountMoved;
                        }
                    }
                }

                for (int i = 0; i < heights.arr.Length; i++)
                    heights.arr[i] += delta[i];

                if (onProgress != null) onProgress((iter + 1) / (float)iterations);
            }

            // Take the edge off the talus facets. The passes above settle every steep
            // slope onto the exact repose angle, which at high iteration counts reads as
            // flat plates with hard creases between them - the faceted mountainside. One
            // light blur rounds those creases the same way the hydraulic solver relaxes
            // its cut faces, reusing the ONE shared GaussianBlur - no second smoother.
            // 0 keeps the original hard-faceted output.
            if (settings.relax > 0.01f)
            {
                Matrix relaxed = MatrixOperations.GaussianBlur(heights, settings.relax);
                Array.Copy(relaxed.arr, heights.arr, heights.arr.Length);
            }
        }

        // =====================================================================
        //  WIND EROSION
        // =====================================================================

        /// <summary>Tunable parameters for <see cref="Wind"/>.</summary>
        [Serializable]
        public struct WindSettings
        {
            /// <summary>Compass bearing the wind blows toward, in degrees.</summary>
            public float direction;

            /// <summary>Wind speed. Pickup scales with its square.</summary>
            public float windSpeed;

            /// <summary>Overall scale on how much material moves.</summary>
            public float strength;

            /// <summary>How many pixels downwind material is carried before settling.</summary>
            public float carryDistance;

            public int iterations;

            public static WindSettings Default
            {
                get
                {
                    return new WindSettings
                    {
                        direction = 270f,
                        windSpeed = 1f,
                        strength = 0.5f,
                        carryDistance = 8f,
                        iterations = 2
                    };
                }
            }
        }

        /// <summary>
        /// Aeolian erosion. Strips material from windward faces in proportion to
        /// their exposure, carries it downwind, and drops it where the surface
        /// turns away from the wind. That pickup/shelter asymmetry is what gives
        /// dunes their shallow windward slope and steep lee face.
        /// </summary>
        public static void Wind(Matrix heights, WindSettings settings,
                                Action<float> onProgress = null,
                                Func<bool> isCancelled = null)
        {
            if (heights == null) throw new ArgumentNullException("heights");

            int sizeX = heights.rect.size.x;
            int sizeZ = heights.rect.size.z;
            if (sizeX < 3 || sizeZ < 3) return;

            int iterations = settings.iterations < 1 ? 1 : settings.iterations;

            const float DegToRad = 0.017453292519943295f;
            float rad = settings.direction * DegToRad;
            float windX = (float)System.Math.Sin(rad);
            float windZ = (float)System.Math.Cos(rad);

            float carry = settings.carryDistance < 1f ? 1f : settings.carryDistance;
            float pickupScale = settings.strength * settings.windSpeed * settings.windSpeed;
            if (pickupScale <= 1e-6f) return;

            // Keep per-pass displacement small; the transport model assumes the
            // surface is quasi-static while a pass runs.
            pickupScale *= 0.01f;

            float[] delta = new float[heights.arr.Length];

            for (int iter = 0; iter < iterations; iter++)
            {
                if (isCancelled != null && isCancelled()) return;

                Array.Clear(delta, 0, delta.Length);
                Vector3[] normals = MatrixOperations.SobelNormals(heights);

                for (int z = 1; z < sizeZ - 1; z++)
                {
                    for (int x = 1; x < sizeX - 1; x++)
                    {
                        int pos = z * sizeX + x;

                        // Exposure: how squarely the surface faces into the wind.
                        Vector3 n = normals[pos];
                        float exposure = -(n.x * windX + n.z * windZ);
                        if (exposure <= 0f) continue;      // sheltered, nothing to pick up

                        float pickup = pickupScale * exposure;

                        float available = heights.arr[pos];
                        if (pickup > available) pickup = available;
                        if (pickup <= 1e-8f) continue;

                        // Deposit downwind, at the sheltered landing point.
                        int dx = (int)(windX * carry);
                        int dz = (int)(windZ * carry);

                        int tx = x + dx;
                        int tz = z + dz;

                        if (tx < 1) tx = 1; if (tx > sizeX - 2) tx = sizeX - 2;
                        if (tz < 1) tz = 1; if (tz > sizeZ - 2) tz = sizeZ - 2;

                        int target = tz * sizeX + tx;

                        delta[pos] -= pickup;
                        delta[target] += pickup;
                    }
                }

                for (int i = 0; i < heights.arr.Length; i++)
                    heights.arr[i] += delta[i];

                if (onProgress != null) onProgress((iter + 1) / (float)iterations);
            }
        }

        // =====================================================================
        //  COASTAL EROSION
        // =====================================================================

        /// <summary>
        /// Cuts a notch at the waterline and lays a beach terrace just above it.
        /// Restricted to a band around <paramref name="seaLevel"/>, so inland
        /// terrain is untouched.
        /// </summary>
        /// <param name="heights">Field to erode, in place.</param>
        /// <param name="seaLevel">Water plane, in normalised height.</param>
        /// <param name="waveHeight">Half-width of the affected band.</param>
        /// <param name="strength">0-1. How flat the beach terrace is cut.</param>
        public static void Coastal(Matrix heights, float seaLevel, float waveHeight, float strength)
        {
            if (heights == null) throw new ArgumentNullException("heights");
            if (waveHeight < 1e-5f) return;

            strength = InterpolationMath.Clamp01(strength);
            if (strength < 1e-4f) return;

            float lower = seaLevel - waveHeight;
            float upper = seaLevel + waveHeight;

            for (int i = 0; i < heights.arr.Length; i++)
            {
                float h = heights.arr[i];
                if (h <= lower || h >= upper) continue;

                // Full effect at the waterline, tapering to nothing at the edges
                // of the band.
                float influence = 1f - InterpolationMath.Abs(h - seaLevel) / waveHeight;
                influence = InterpolationMath.SmoothStep(influence);

                heights.arr[i] = h + (seaLevel - h) * influence * strength;
            }
        }
    }
}
