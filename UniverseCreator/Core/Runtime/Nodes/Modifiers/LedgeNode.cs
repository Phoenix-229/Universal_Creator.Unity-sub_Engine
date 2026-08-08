// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    LedgeNode.cs
//  Purpose: Accentuates horizontal breaks. Detects slope discontinuities and
//           carves a thin horizontal cut at each one, which is how cliffs and
//           ledges read in real rock.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class LedgeNode : ModifierNode
    {
        public override string NodeName { get { return "Ledge"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Carve horizontal ledges at slope breaks."; } }

        public float threshold = 0.3f;
        public float depth = 0.05f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            if (depth < 0.001f) return;

            int w = m.rect.size.x;
            int h = m.rect.size.z;

            float[] slopes = new float[m.arr.Length];

            // Compute per-pixel slope magnitude.
            for (int z = 1; z < h - 1; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 1; x < w - 1; x++)
                {
                    int i = rowStart + x;

                    float l = m.arr[i - 1];
                    float r = m.arr[i + 1];
                    float u = m.arr[i - w];
                    float d = m.arr[i + w];

                    float gx = (r - l) * 0.5f;
                    float gz = (d - u) * 0.5f;

                    slopes[i] = Mathf.Sqrt(gx * gx + gz * gz);
                }
            }

            // Detect discontinuities: neighbouring slope differs by more than threshold.
            for (int z = 1; z < h - 1; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 1; x < w - 1; x++)
                {
                    int i = rowStart + x;
                    float s = slopes[i];

                    float sl = slopes[i - 1];
                    float sr = slopes[i + 1];
                    float su = slopes[i - w];
                    float sd = slopes[i + w];

                    float maxDelta = Mathf.Max(
                        Mathf.Abs(s - sl),
                        Mathf.Abs(s - sr),
                        Mathf.Abs(s - su),
                        Mathf.Abs(s - sd)
                    );

                    if (maxDelta > threshold)
                    {
                        m.arr[i] -= depth;
                    }
                }
            }
        }
    }
}
