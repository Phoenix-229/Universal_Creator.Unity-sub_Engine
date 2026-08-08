// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    BlurNode.cs
//  Purpose: Separable Gaussian blur. Two-pass (horizontal, then vertical) with
//           a parameterised kernel radius. Used for smoothing noise before erosion
//           and softening masks.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class BlurNode : ModifierNode
    {
        public override string NodeName { get { return "Blur"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Separable Gaussian blur. Smooths the heightfield."; } }

        /// <summary>Blur radius in pixels.</summary>
        public int radius = 3;

        public float intensity = 1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int r = Mathf.Clamp(radius, 0, 32);
            if (r < 1 || intensity < 0.01f) return;

            int w = m.rect.size.x;
            int h = m.rect.size.z;

            // Separable Gaussian: horizontal pass into temp, then vertical back into m.
            float[] temp = new float[m.arr.Length];
            float[] kernel = BuildGaussianKernel(r);

            // Horizontal pass
            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    float weightSum = 0f;
                    for (int k = -r; k <= r; k++)
                    {
                        int sx = x + k;
                        if (sx < 0 || sx >= w) continue;
                        float weight = kernel[k + r];
                        sum += m.arr[rowStart + sx] * weight;
                        weightSum += weight;
                    }
                    temp[rowStart + x] = weightSum > 0f ? sum / weightSum : m.arr[rowStart + x];
                }
            }

            // Vertical pass
            for (int x = 0; x < w; x++)
            {
                if (stop != null && stop.StopRequested) return;

                for (int z = 0; z < h; z++)
                {
                    float sum = 0f;
                    float weightSum = 0f;
                    for (int k = -r; k <= r; k++)
                    {
                        int sz = z + k;
                        if (sz < 0 || sz >= h) continue;
                        float weight = kernel[k + r];
                        sum += temp[sz * w + x] * weight;
                        weightSum += weight;
                    }
                    int i = z * w + x;
                    float blurred = weightSum > 0f ? sum / weightSum : temp[i];
                    m.arr[i] = Mathf.Lerp(m.arr[i], blurred, Mathf.Clamp01(intensity));
                }
            }
        }

        private float[] BuildGaussianKernel(int radius)
        {
            int size = radius * 2 + 1;
            float[] kernel = new float[size];
            float sigma = radius / 3f;
            float twoSigmaSq = 2f * sigma * sigma;

            for (int i = 0; i < size; i++)
            {
                int x = i - radius;
                kernel[i] = Mathf.Exp(-(x * x) / twoSigmaSq);
            }
            return kernel;
        }
    }
}
