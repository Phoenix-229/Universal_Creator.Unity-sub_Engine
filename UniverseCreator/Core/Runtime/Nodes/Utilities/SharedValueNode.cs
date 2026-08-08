// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SharedValueNode.cs
//  Purpose: A reusable constant that can be wired to many nodes at once, so
//           tweaking the value in one place updates every consumer. Useful for
//           global parameters like sea level, biome scale, or world seed.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Utilities
{
    [System.Serializable]
    public class SharedValueNode : Generator
    {
        public override string NodeName { get { return "Shared Value"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "A constant wired to many nodes. One edit updates all."; } }

        public float value = 0.5f;

        /// <summary>Optional label shown in the node header and add-node menu search.</summary>
        public string label = "Value";

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix m = tile.CreateMatrix();
            m.Fill(value);
            return m;
        }
    }
}
