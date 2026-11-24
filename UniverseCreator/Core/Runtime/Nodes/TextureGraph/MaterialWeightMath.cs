// UC-T07-06: independent material membership, normalized before height affinity; never colour/category arithmetic.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>Four-channel nonnegative material coverage and height affinity matching the TerrainLit control contract.</summary>
    public static class MaterialWeightMath
    {
        /// <summary>Normalizes finite nonnegative coverage; zero coverage falls back to the first material.</summary>
        /// <param name="weights">Four finite coverage values; negative values are clamped to zero.</param>
        /// <returns>Nonnegative material membership with sum one.</returns>
        /// <exception cref="ArgumentException">A component is nonfinite.</exception>
        public static Vector4 Normalize(Vector4 weights)
        {
            double sum = 0;
            for (int i = 0; i < 4; i++) { TextureImageSampling.RequireFinite(weights[i]); weights[i] = Mathf.Max(0, weights[i]); sum += weights[i]; }
            if (sum == 0) return new Vector4(1, 0, 0, 0);
            for (int i = 0; i < 4; i++) weights[i] = (float)(weights[i] / sum);
            return weights;
        }

        /// <summary>Reweights existing coverage by relative material height while preserving absent materials.</summary>
        /// <param name="weights">Four finite membership values, normalized by this function.</param>
        /// <param name="heights">Finite per-material height in the same units as transition.</param>
        /// <param name="transition">Positive finite height blend band; at least 0.001 in the shader's normalized mask units.</param>
        /// <returns>Normalized nonnegative coverage; an absent material can never gain coverage.</returns>
        /// <exception cref="ArgumentException">Heights/transition are invalid.</exception>
        public static Vector4 HeightBlend(Vector4 weights, Vector4 heights, float transition)
        {
            weights = Normalize(weights); TextureImageSampling.RequireFinite(transition);
            if (transition <= 0) throw new ArgumentException("Material height transition must be positive.");
            float peak = float.NegativeInfinity;
            for (int i = 0; i < 4; i++) { TextureImageSampling.RequireFinite(heights[i]); if (weights[i] > 0) peak = Mathf.Max(peak, heights[i]); }
            float band = Mathf.Max(transition, 0.001f);
            for (int i = 0; i < 4; i++) weights[i] *= Mathf.Clamp01((float)(((double)heights[i] - peak + band) / band));
            return Normalize(weights);
        }
    }
}
