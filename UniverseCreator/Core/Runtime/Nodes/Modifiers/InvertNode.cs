// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    InvertNode.cs
//  Purpose: Flips the heightfield upside down (1 - v). Valleys become ridges,
//           ridges become valleys.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class InvertNode : ModifierNode
    {
        public override string NodeName { get { return "Invert"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Flip the field: 1 - v. Valleys become ridges."; } }

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    m.arr[i] = 1f - m.arr[i];
                }
            }
        }
    }
}
