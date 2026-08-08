// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PowerNode.cs
//  Purpose: Raises every value to an exponent. Exponent > 1 darkens and steepens
//           slopes; exponent < 1 brightens and flattens.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class PowerNode : ModifierNode
    {
        public override string NodeName { get { return "Power"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Raise values to an exponent. Steepens or flattens slopes."; } }

        public float exponent = 2f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float e = Mathf.Max(0.01f, exponent);

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float v = Mathf.Max(0f, m.arr[i]);
                    m.arr[i] = Mathf.Pow(v, e);
                }
            }
        }
    }
}
