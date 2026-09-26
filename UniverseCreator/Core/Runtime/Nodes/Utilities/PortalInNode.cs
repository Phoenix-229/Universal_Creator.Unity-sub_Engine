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

        /// <summary>
        /// The kind of data this portal carries. The portal is a pure pass-through,
        /// so it can route any currency the graph uses - not height alone. Both ends
        /// of a channel should be set to the same type; the socket is coloured and
        /// named by it. Defaults to Matrix, so every portal saved before this field
        /// existed stays a height portal on load.
        /// </summary>
        public PortType portType = PortType.Matrix;

        // Cached so GetOutputs does not allocate on every editor repaint; rebuilt
        // only when the chosen type actually changes.
        [System.NonSerialized] private PortType builtType = (PortType)(-1);
        [System.NonSerialized] private Port[] cachedOut;

        public override Port[] GetInputs() { return SharedPorts.None; }

        public override Port[] GetOutputs()
        {
            if (cachedOut == null || builtType != portType)
            {
                builtType = portType;
                cachedOut = PortalRouting.BuildPortArray(portType, PortDirection.Output);
            }
            return cachedOut;
        }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (string.IsNullOrEmpty(channel))
            {
                return PortalRouting.BlankProduct(portType, tile);
            }

            // Find the PortalOut with matching channel. ReferenceEquals rather than
            // ==: Generate() runs on a worker thread and Graph is a UnityEngine.Object,
            // whose == operator marshals into native code.
            if (object.ReferenceEquals(tile.Graph, null))
            {
                return PortalRouting.BlankProduct(portType, tile);
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
                // No partner found: return a typed blank rather than throwing, so a
                // disconnected portal does not collapse the entire evaluation.
                return PortalRouting.BlankProduct(portType, tile);
            }

            // Pull whatever feeds the partner's input port and forward it AS-IS,
            // deep-copied. Routing through tile.PullInput keeps the evaluator's cache
            // and its re-entry guard in play, so a portal wired back into its own
            // chain degrades to a blank field rather than recursing. No forced cast:
            // the portal carries whatever type the user wired, not height alone.
            object pulled = tile.PullInput(partner, 0, stop);
            if (pulled == null) return PortalRouting.BlankProduct(portType, tile);

            return PortalRouting.CopyProduct(pulled);
        }
    }
}
