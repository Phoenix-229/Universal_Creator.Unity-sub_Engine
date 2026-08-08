// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    DirectionNode.cs
//  Purpose: Extracts the dominant downhill flow direction into a grayscale field,
//           encoded as an angle. Used for anisotropic effects like wind streaks
//           that follow drainage.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class DirectionNode : ModifierNode
    {
        public override string NodeName { get { return "Direction"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Extract flow direction as an angle field."; } }

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int w = m.rect.size.x;
            int h = m.rect.size.z;
            float[] angles = new float[m.arr.Length];

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

                    // Sobel gradients.
                    float gx = (r - l) * 0.5f;
                    float gz = (d - u) * 0.5f;

                    float angle = Mathf.Atan2(gz, gx);
                    // Remap [-π, π] to [0, 1].
                    angles[i] = (angle / Mathf.PI) * 0.5f + 0.5f;
                }
            }

            System.Array.Copy(angles, m.arr, m.arr.Length);
        }
    }
}
