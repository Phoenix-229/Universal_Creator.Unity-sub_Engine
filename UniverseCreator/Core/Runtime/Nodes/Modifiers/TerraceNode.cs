// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TerraceNode.cs
//  Purpose: Quantizes the heightfield into discrete steps, creating mesa-like
//           plateaus. The edge softness parameter blends between steps to avoid
//           vertical walls.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class TerraceNode : ModifierNode
    {
        public override string NodeName { get { return "Terrace"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Quantize into discrete steps. Mesa plateaus."; } }

        public int steps = 5;
        public float edgeSoftness = 0.1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int n = Mathf.Max(2, steps);
            float soft = Mathf.Clamp01(edgeSoftness);

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float v = InterpolationMath.Clamp01(m.arr[i]);

                    // Quantize to the nearest step.
                    float scaled = v * (n - 1);
                    int lo = Mathf.FloorToInt(scaled);
                    int hi = Mathf.CeilToInt(scaled);
                    float frac = scaled - lo;

                    // Remap frac so the middle fraction of each step is flat and
                    // the edges blend.
                    float halfSoft = soft * 0.5f;
                    float t;
                    if (frac < halfSoft)
                    {
                        t = frac / Mathf.Max(0.0001f, halfSoft);
                        t = InterpolationMath.SmoothStep(t);
                        t = t * 0.5f;
                    }
                    else if (frac > 1f - halfSoft)
                    {
                        t = (frac - (1f - halfSoft)) / Mathf.Max(0.0001f, halfSoft);
                        t = InterpolationMath.SmoothStep(t);
                        t = 0.5f + t * 0.5f;
                    }
                    else
                    {
                        t = 0.5f;
                    }

                    float result = (lo + t) / (n - 1);
                    m.arr[i] = result;
                }
            }
        }
    }
}
