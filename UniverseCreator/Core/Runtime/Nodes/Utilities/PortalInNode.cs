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
    public class PortalInNode : Generator, IPortalTyped
    {
        public override string NodeName { get { return "Portal In"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "Receives from a PortalOut. Wireless graph connection."; } }

        /// <summary>Channel name. A PortalOut with the same channel feeds this input.</summary>
        public string channel = "default";

        // The socket type is DERIVED from the sender on this channel, never authored:
        // a portal carries whatever the partner is fed, and colours itself to match,
        // so there is no Port Type field to set. Matrix (the height currency) is the
        // fallback when the channel has no wired sender yet. Non-serialized: it is
        // recomputed from the graph, so nothing is lost when an old graph that stored
        // a portType loads (the stale field is simply ignored).
        [System.NonSerialized] private PortType resolvedType = PortType.Matrix;

        // Cached so GetOutputs does not allocate on every editor repaint; rebuilt
        // only when the resolved type actually changes.
        [System.NonSerialized] private PortType builtType = (PortType)(-1);
        [System.NonSerialized] private Port[] cachedOut;

        /// <summary>
        /// Recompute the socket type from the sender on this channel. Called by the
        /// editor when the graph is built or rewired, and once per evaluation off the
        /// live graph, so the socket always reads the data actually crossing it.
        /// </summary>
        public void ResolveType(Graph graph)
        {
            resolvedType = PortalRouting.ResolveChannelType<PortalOutNode>(graph, channel, PortType.Matrix);
        }

        public override Port[] GetInputs() { return SharedPorts.None; }

        public override Port[] GetOutputs()
        {
            if (cachedOut == null || builtType != resolvedType)
            {
                builtType = resolvedType;
                cachedOut = PortalRouting.BuildPortArray(resolvedType, PortDirection.Output);
            }
            return cachedOut;
        }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (string.IsNullOrEmpty(channel))
            {
                return PortalRouting.BlankProduct(resolvedType, tile);
            }

            // Find the PortalOut with matching channel. ReferenceEquals rather than
            // ==: Generate() runs on a worker thread and Graph is a UnityEngine.Object,
            // whose == operator marshals into native code.
            if (object.ReferenceEquals(tile.Graph, null))
            {
                return PortalRouting.BlankProduct(resolvedType, tile);
            }

            // Refresh the carried type from the live sender so a missing-partner blank
            // and the socket colour both reflect the current wiring, not a stale value.
            ResolveType(tile.Graph);

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
                return PortalRouting.BlankProduct(resolvedType, tile);
            }

            // Pull whatever feeds the partner's input port and forward it AS-IS,
            // deep-copied. Routing through tile.PullInput keeps the evaluator's cache
            // and its re-entry guard in play, so a portal wired back into its own
            // chain degrades to a blank field rather than recursing. No forced cast:
            // the portal carries whatever type the user wired, not height alone.
            object pulled = tile.PullInput(partner, 0, stop);
            if (pulled == null) return PortalRouting.BlankProduct(resolvedType, tile);

            return PortalRouting.CopyProduct(pulled);
        }
    }
}
