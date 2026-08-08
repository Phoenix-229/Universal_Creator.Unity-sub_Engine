// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ConstantNode.cs
//  Purpose: Fills the chunk with a single value. Useful as a blend operand,
//           a sea-level reference, or a neutral mask.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class ConstantNode : CreatorNode
    {
        public override string NodeName { get { return "Constant"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "A flat field of one value."; } }

        public float value = 0.5f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            m.Fill(value);
        }
    }
}
