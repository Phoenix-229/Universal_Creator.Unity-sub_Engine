// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    SnowSplatMutation.cs
//  Purpose: The safe arithmetic of laying snow into a terrain splat map and
//           taking it back off again. Snow is written into one configured
//           terrain layer; the remaining ground layers are scaled down so the
//           per-cell weights still sum to one, and the pre-snow map is kept so
//           that melting restores the author's terrain bit-for-bit rather than
//           an approximation of it.
//
//  WHY A CACHED BASE, NOT AN INVERSE
//  ---------------------------------
//  Renormalising a weight vector loses information: once the ground layers have
//  been scaled to make room for snow, dividing them back up cannot recover a
//  weight that was rounded away, and repeating the cycle would walk the author's
//  terrain steadily away from what they painted - a one-way corruption that would
//  survive into the saved world. So melt does not invert the blend; it copies the
//  cached pre-snow map straight back, which is exact by construction.
//
//  This file is pure arithmetic over plain arrays. Reading and writing the actual
//  TerrainData, caching the base map per chunk, the layer-count guard's inspector
//  warning and the per-frame batching all live on the driver that calls it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Blends snow into a terrain alphamap and restores it, keeping every cell's
    /// weights summed to one. Every method is a pure function of its arguments.
    /// </summary>
    public static class SnowSplatMutation
    {
        /// <summary>
        /// Whether the configured snow layer exists in a world of this many layers.
        /// The caller warns and skips when this is false rather than indexing out of
        /// range or silently writing layer zero, which would paint the world in sand.
        /// </summary>
        public static bool CanWriteLayer(int layerCount, int snowLayer)
        {
            return snowLayer >= 0 && snowLayer < layerCount;
        }

        /// <summary>
        /// Blends one cell's weights from its pre-snow base: the snow layer takes the
        /// cover fraction and the ground layers share what is left in the same
        /// proportion they had, so the result still sums to one. Full cover is pure
        /// snow; no cover leaves the ground layers as they were.
        /// </summary>
        public static void BlendCell(float[] weights, float[] baseWeights, int snowLayer, float cover01)
        {
            int layers = weights.Length;
            float cover = Mathf.Clamp01(cover01);
            float remaining = 1f - cover;

            float othersSum = 0f;
            for (int l = 0; l < layers; l++)
                if (l != snowLayer) othersSum += Mathf.Max(0f, baseWeights[l]);

            for (int l = 0; l < layers; l++)
            {
                if (l == snowLayer) { weights[l] = cover; continue; }
                weights[l] = othersSum > 1e-6f
                    ? Mathf.Max(0f, baseWeights[l]) / othersSum * remaining
                    : remaining / Mathf.Max(1, layers - 1);
            }
        }

        /// <summary>
        /// Blends snow across a whole alphamap block from its cached base, one cover
        /// value per cell. A no-op when the snow layer is out of range, so a world
        /// with too few layers is left untouched rather than throwing.
        /// </summary>
        public static void WriteSnow(float[,,] alpha, float[,,] baseAlpha, int snowLayer, float[,] cover01)
        {
            int h = alpha.GetLength(0);
            int w = alpha.GetLength(1);
            int layers = alpha.GetLength(2);
            if (!CanWriteLayer(layers, snowLayer)) return;

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float cover = Mathf.Clamp01(cover01[y, x]);
                float remaining = 1f - cover;

                float othersSum = 0f;
                for (int l = 0; l < layers; l++)
                    if (l != snowLayer) othersSum += Mathf.Max(0f, baseAlpha[y, x, l]);

                for (int l = 0; l < layers; l++)
                {
                    if (l == snowLayer) { alpha[y, x, l] = cover; continue; }
                    alpha[y, x, l] = othersSum > 1e-6f
                        ? Mathf.Max(0f, baseAlpha[y, x, l]) / othersSum * remaining
                        : remaining / Mathf.Max(1, layers - 1);
                }
            }
        }

        /// <summary>
        /// Copies the cached pre-snow map straight back over the live one, so a fully
        /// melted cell is bit-for-bit the terrain the author painted. Exact by
        /// construction because it is a copy, not an inverse of the blend.
        /// </summary>
        public static void Restore(float[,,] alpha, float[,,] baseAlpha)
        {
            if (alpha.Length != baseAlpha.Length) return;
            System.Array.Copy(baseAlpha, alpha, alpha.Length);
        }
    }
}
