// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PortalInNode.cs
//  Purpose: Receives from a PortalOutNode elsewhere in the graph. Paired portals
//           allow wiring across long distances without spaghetti connection lines
//           cluttering the editor canvas.
//
//  The partner is resolved by matching channel names, not by storing a hard ID
//  reference, so duplicate/copy/paste of the pair keeps working.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Utilities
{
    [System.Serializable]
    public class PortalInNode : Generator
    {
        public override string NodeName { get { return "Portal In"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "Receives from a PortalOut. Wireless graph connection."; } }

        /// <summary>Channel name. A PortalOut with the same channel feeds this input.</summary>
        public string channel = "default";

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (string.IsNullOrEmpty(channel))
            {
                return tile.CreateMatrix();
            }

            // Find the PortalOut with matching channel. ReferenceEquals rather than
            // ==: Generate() runs on a worker thread and Graph is a UnityEngine.Object,
            // whose == operator marshals into native code.
            if (object.ReferenceEquals(tile.Graph, null))
            {
                return tile.CreateMatrix();
            }

            PortalOutNode partner = null;
            foreach (Generator node in tile.Graph.nodes)
            {
                PortalOutNode outNode = node as PortalOutNode;
                if (outNode != null && outNode.channel == channel && !outNode.disabled)
                {
                    partner = outNode;
                    break;
                }
            }

            if (partner == null)
            {
                // No partner found: return blank rather than throwing, so a
                // disconnected portal does not collapse the entire evaluation.
                return tile.CreateMatrix();
            }

            // Pull whatever feeds the partner's input port. Routing through
            // tile.PullInput keeps the evaluator's cache and its re-entry guard in
            // play, so a portal wired back into its own chain degrades to a blank
            // field rather than recursing.
            Matrix result = tile.PullInput(partner, 0, stop) as Matrix;
            if (result == null) return tile.CreateMatrix();

            return new Matrix(result);
        }
    }
}
