// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    ShorelineSplatMutation.cs
//  Purpose: The safe arithmetic of laying a wet shoreline / bank band into a
//           terrain splat map and taking it back off again. The band material
//           (beach sand, lake gravel, river bank) is written into one configured
//           terrain layer around a water body; the remaining ground layers are
//           scaled down so the per-cell weights still sum to one, and the pre-band
//           map is kept so that removing the band restores the author's terrain
//           bit-for-bit rather than an approximation of it.
//
//  WHY THE BAND EXISTS
//  -------------------
//  The terrain shader draws the ocean's dry berm itself, keyed to the GLOBAL sea
//  level (_UC_BEACH_ON). A lake or a river sits at its own altitude, so that one
//  world-height band cannot reach it, and without a per-body band the inland grass
//  or rock runs straight into the waterline - the "grass standing in the water"
//  artefact. This bakes a per-body band into the alphamap around the body instead,
//  which is the baked-pipeline answer: computed once at build, free every frame.
//
//  WHY IT MIRRORS SnowSplatMutation
//  --------------------------------
//  Writing one layer to a cover fraction and rescaling the rest to keep the sum at
//  one is a single general operation, not snow-specific logic; the domain-specific
//  part here is BandCover, which turns a body's geometry into that fraction. The
//  renormalise below is deliberately the same shape as the snow one because it is
//  the same operation on a different source signal - it is a copy of a general op,
//  not a second copy of any domain rule. Melt/erase copies the cached base straight
//  back for the same reason snow does: renormalising is one-way lossy, a copy is
//  exact by construction.
//
//  Pure arithmetic over plain arrays. Reading and writing the actual TerrainData,
//  caching the base map, the layer guard and the per-body batching live on the
//  driver that calls it - never here, so this is safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Blends a wet shoreline / bank band into a terrain alphamap and restores it,
    /// keeping every cell's weights summed to one. Every method is a pure function
    /// of its arguments. See the file header for why it parallels SnowSplatMutation.
    /// </summary>
    public static class ShorelineSplatMutation
    {
        /// <summary>
        /// Whether the configured band layer exists in a world of this many layers.
        /// The caller warns and skips when this is false rather than indexing out of
        /// range or writing layer zero, which would flood the world with sand.
        /// </summary>
        public static bool CanWriteLayer(int layerCount, int bandLayer)
        {
            return bandLayer >= 0 && bandLayer < layerCount;
        }

        /// <summary>
        /// The band's cover fraction at one cell, from the two things a real shore
        /// grades on: how far inland the cell is from the waterline, and how steep it
        /// is.
        ///
        /// DISTANCE. Full band right at the water, easing to nothing by the band
        /// width. Smoothstepped rather than linear because a hard outer line reads as
        /// a painted ring; the grade is what makes sand become grass rather than stop.
        ///
        /// SLOPE. A worked band only holds on ground shallow enough to keep it - a
        /// wave-graded berm, a settled lake margin, the shallow inside of a river
        /// bend. Past the slope limit the wall is a cut bank and shows the inland
        /// surface, so the band fades out over the last few degrees rather than
        /// clipping, which would step.
        /// </summary>
        /// <param name="distanceFromWaterlineMetres">Horizontal distance inland from the waterline. Negative (under water) reads as at the line.</param>
        /// <param name="widthMetres">How far the band reaches inland.</param>
        /// <param name="slopeDegrees">Ground slope at this cell.</param>
        /// <param name="slopeLimitDegrees">Slope past which the band gives way to the inland surface.</param>
        public static float BandCover(float distanceFromWaterlineMetres, float widthMetres,
                                      float slopeDegrees, float slopeLimitDegrees)
        {
            float w = Mathf.Max(0.01f, widthMetres);
            float d = Mathf.Max(0f, distanceFromWaterlineMetres);
            float t = Mathf.Clamp01(d / w);
            float distanceCover = 1f - Mathf.SmoothStep(0f, 1f, t);

            // Fade out over the 5 degrees below the limit rather than clipping at it.
            // NOTE: Unity's Mathf.SmoothStep(from, to, t) takes t as a 0..1 parameter -
            // it is NOT HLSL smoothstep, which remaps a value across [from, to]. So the
            // slope is normalised to 0..1 by hand FIRST, then smoothed.
            float limit = Mathf.Clamp(slopeLimitDegrees, 1f, 89f);
            float st = Mathf.Clamp01((slopeDegrees - (limit - 5f)) / 5f);
            float slopeFactor = 1f - Mathf.SmoothStep(0f, 1f, st);

            return Mathf.Clamp01(distanceCover * slopeFactor);
        }

        /// <summary>
        /// Blends the band across a whole alphamap block from its cached base, one
        /// cover value per cell (from <see cref="BandCover"/>). The band layer takes
        /// the cover fraction; the ground layers share what is left in the proportion
        /// they had, so the result still sums to one. A no-op when the band layer is
        /// out of range, so a world with too few layers is left untouched rather than
        /// throwing.
        /// </summary>
        public static void WriteBand(float[,,] alpha, float[,,] baseAlpha, int bandLayer, float[,] cover01)
        {
            int h = alpha.GetLength(0);
            int w = alpha.GetLength(1);
            int layers = alpha.GetLength(2);
            if (!CanWriteLayer(layers, bandLayer)) return;

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float cover = Mathf.Clamp01(cover01[y, x]);
                float remaining = 1f - cover;

                float othersSum = 0f;
                for (int l = 0; l < layers; l++)
                    if (l != bandLayer) othersSum += Mathf.Max(0f, baseAlpha[y, x, l]);

                for (int l = 0; l < layers; l++)
                {
                    if (l == bandLayer) { alpha[y, x, l] = cover; continue; }
                    alpha[y, x, l] = othersSum > 1e-6f
                        ? Mathf.Max(0f, baseAlpha[y, x, l]) / othersSum * remaining
                        : remaining / Mathf.Max(1, layers - 1);
                }
            }
        }

        /// <summary>
        /// Copies the cached pre-band map straight back over the live one, so an
        /// erased cell is bit-for-bit the terrain the author painted. Exact by
        /// construction because it is a copy, not an inverse of the blend.
        /// </summary>
        public static void Restore(float[,,] alpha, float[,,] baseAlpha)
        {
            if (alpha.Length != baseAlpha.Length) return;
            System.Array.Copy(baseAlpha, alpha, alpha.Length);
        }
    }
}
