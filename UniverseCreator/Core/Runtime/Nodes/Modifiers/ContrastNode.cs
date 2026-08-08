// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ContrastNode.cs
//  Purpose: Expands or compresses the value distribution around a pivot point.
//           Values below pivot move toward 0, values above move toward 1.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class ContrastNode : ModifierNode
    {
        public override string NodeName { get { return "Contrast"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Expand or compress the value range around a pivot."; } }

        public float contrast = 0.5f;   // 0 = flat, 0.5 = no change, 1 = full split
        public float pivot = 0.5f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float c = Mathf.Clamp01(contrast);
            float factor = c < 0.5f ? c * 2f : (c - 0.5f) * 2f;
            bool compress = c < 0.5f;

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float v = m.arr[i];
                    float delta = v - pivot;

                    if (compress)
                    {
                        m.arr[i] = pivot + delta * (1f - factor);
                    }
                    else
                    {
                        m.arr[i] = pivot + delta * (1f + factor);
                    }
                }
            }
        }
    }
}
