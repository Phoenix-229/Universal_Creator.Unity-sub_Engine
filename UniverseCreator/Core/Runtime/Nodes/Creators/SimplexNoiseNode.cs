// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SimplexNoiseNode.cs
//  Purpose: Gustavson simplex noise creator with full fBm octave control.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class SimplexNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Simplex Noise"; } }
        public override string Tooltip { get { return "Gustavson simplex noise. Fewer axis-aligned artifacts than Perlin."; } }

        protected override NoiseType BasisType { get { return NoiseType.Simplex; } }
    }
}
