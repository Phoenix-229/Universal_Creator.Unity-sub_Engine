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
    public class PortalOutNode : Generator, IPortalOut
    {
        public override string NodeName { get { return "Portal Out"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "Sends to a PortalIn. Wireless graph connection."; } }

        /// <summary>Channel name. A PortalIn with the same channel receives this output.</summary>
        public string channel = "default";

        /// <summary>The channel this sender broadcasts on (for the shared resolver).</summary>
        public string PortalChannel { get { return channel; } }

        // The input socket colours itself to match whatever is wired in - there is no
        // Port Type field. Matrix (height) is the fallback until something is wired.
        // Non-serialized: recomputed from the graph, so an old graph's stored portType
        // is harmlessly ignored on load.
        [System.NonSerialized] private PortType resolvedType = PortType.Matrix;

        [System.NonSerialized] private PortType builtType = (PortType)(-1);
        [System.NonSerialized] private Port[] cachedIn;

        /// <summary>
        /// Recompute the input socket type from whatever is wired into this sender.
        /// Called by the editor when the graph is built or rewired.
        /// </summary>
        public void ResolveType(Graph graph)
        {
            resolvedType = PortalRouting.ResolveSourceType(graph, this, 0, PortType.Matrix);
        }

        public override Port[] GetInputs()
        {
            if (cachedIn == null || builtType != resolvedType)
            {
                builtType = resolvedType;
                cachedIn = PortalRouting.BuildPortArray(resolvedType, PortDirection.Input);
            }
            return cachedIn;
        }

        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            // A portal output produces no downstream product; it is read by the
            // PortalIn when that node is evaluated.
            return null;
        }
    }
}
