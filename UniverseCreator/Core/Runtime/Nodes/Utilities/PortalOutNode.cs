// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PortalOutNode.cs
//  Purpose: Sends to a PortalInNode elsewhere in the graph. The pair allow wiring
//           across long distances without spaghetti connection lines.
//
//  The partner is resolved by matching channel names, not by storing a hard ID
//  reference, so duplicate/copy/paste of the pair keeps working.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Utilities
{
    [System.Serializable]
    public class PortalOutNode : Generator
    {
        public override string NodeName { get { return "Portal Out"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "Sends to a PortalIn. Wireless graph connection."; } }

        /// <summary>Channel name. A PortalIn with the same channel receives this output.</summary>
        public string channel = "default";

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            // A portal output produces no downstream product; it is read by the
            // PortalIn when that node is evaluated.
            return null;
        }
    }
}
