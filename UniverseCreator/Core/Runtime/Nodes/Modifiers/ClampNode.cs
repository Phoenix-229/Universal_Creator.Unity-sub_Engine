// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ClampNode.cs
//  Purpose: Hard clipping to [min, max]. Useful for enforcing water level or
//           preventing overshoot after combines.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class ClampNode : ModifierNode
    {
        public override string NodeName { get { return "Clamp"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Hard clip to [min, max]."; } }

        public float min = 0f;
        public float max = 1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float lo = Mathf.Min(min, max);
            float hi = Mathf.Max(min, max);

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    m.arr[i] = InterpolationMath.Clamp(m.arr[i], lo, hi);
                }
            }
        }
    }
}
