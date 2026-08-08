// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    BillowNoiseNode.cs
//  Purpose: Billow (absolute-value folded) noise creator with fBm octave control.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class BillowNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Billow Noise"; } }
        public override string Tooltip { get { return "Absolute-value folded noise. Puffy forms for dunes and rolling hills."; } }

        protected override NoiseType BasisType { get { return NoiseType.Billow; } }
    }
}
