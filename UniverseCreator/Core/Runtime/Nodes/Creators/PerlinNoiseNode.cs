// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PerlinNoiseNode.cs
//  Purpose: Improved Perlin noise creator with full fBm octave control.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class PerlinNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Perlin Noise"; } }
        public override string Tooltip { get { return "Improved Perlin noise with fBm octaves. The general-purpose terrain base."; } }

        protected override NoiseType BasisType { get { return NoiseType.Perlin; } }
    }
}
