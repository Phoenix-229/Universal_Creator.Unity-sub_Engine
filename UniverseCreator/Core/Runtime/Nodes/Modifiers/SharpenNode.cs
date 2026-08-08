// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SharpenNode.cs
//  Purpose: Unsharp mask. Subtracts a blurred copy scaled by intensity to
//           accentuate high-frequency detail.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class SharpenNode : ModifierNode
    {
        public override string NodeName { get { return "Sharpen"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Unsharp mask. Accentuates detail."; } }

        public int radius = 2;
        public float intensity = 0.5f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int r = Mathf.Clamp(radius, 1, 16);
            float str = Mathf.Clamp(intensity, 0f, 2f);
            if (str < 0.01f) return;

            int w = m.rect.size.x;
            int h = m.rect.size.z;

            // Build a blurred copy.
            float[] blurred = new float[m.arr.Length];
            float[] kernel = BuildGaussianKernel(r);

            // Horizontal pass
            float[] temp = new float[m.arr.Length];
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
                    blurred[z * w + x] = weightSum > 0f ? sum / weightSum : temp[z * w + x];
                }
            }

            // Subtract the blur from the original, scaled by intensity.
            for (int i = 0; i < m.arr.Length; i++)
            {
                float detail = m.arr[i] - blurred[i];
                m.arr[i] = m.arr[i] + detail * str;
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
