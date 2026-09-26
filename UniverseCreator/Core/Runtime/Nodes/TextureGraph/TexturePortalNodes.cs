// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    TexturePortalNodes.cs
//  Purpose: Texture-graph Portal In / Out. The wireless connection the height
//           graph already has (PortalInNode / PortalOutNode), but carrying a
//           LAYER instead of a Matrix and living in the Texture category so the
//           texture window admits them.
//
//  WHY A SEPARATE PAIR RATHER THAN REUSING THE HEIGHT PORTALS
//  ----------------------------------------------------------
//  The height PortalIn/Out are Category=Utility and Matrix-typed. The texture
//  window filters to Texture | Surface | Water, so those never appear in it, and
//  widening the filter to admit Utility would leak every height node into the
//  texture search. The MECHANISM is worth reusing - resolve the partner by
//  channel-name match, pull through tile.PullInput so the evaluator's cache and
//  re-entry guard stay in play - so this file reuses the mechanism and nothing
//  else. A texture portal carries a Layer, which is the backbone product the
//  layer nodes pass between each other; that is what a user most wants to route
//  across a large texture canvas without a spaghetti wire.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  Texture Portal Out - sends a Layer to the matching Texture Portal In
    // -------------------------------------------------------------------------

    /// <summary>
    /// Receives a Layer and makes it available to any Texture Portal In on the
    /// same channel. Produces no downstream product itself - it is read by the
    /// portal-in when THAT node is evaluated, exactly like the height PortalOut.
    /// </summary>
    [System.Serializable]
    public class TexturePortalOutNode : Generator
    {
        public override string NodeName { get { return "Texture Portal Out"; } }
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "Sends a layer to a Texture Portal In on the same channel. Wireless graph connection."; }
        }

        /// <summary>Channel name. A Texture Portal In with the same channel receives this layer.</summary>
        public string channel = "default";

        /// <summary>
        /// The kind of data this texture portal carries. Set it to the SAME type as
        /// the Texture Portal In on the matching channel. Defaults to Layer - the
        /// backbone product of the layer nodes - so texture portals saved before
        /// this field existed keep carrying a Layer on load.
        /// </summary>
        public PortType portType = PortType.Layer;

        [System.NonSerialized] private PortType builtType = (PortType)(-1);
        [System.NonSerialized] private Port[] cachedIn;

        public override Port[] GetInputs()
        {
            if (cachedIn == null || builtType != portType)
            {
                builtType = portType;
                cachedIn = PortalRouting.BuildPortArray(portType, PortDirection.Input);
            }
            return cachedIn;
        }

        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            // No downstream product; the portal-in pulls the input when evaluated.
            return null;
        }
    }

    // -------------------------------------------------------------------------
    //  Texture Portal In - receives a Layer from the matching Texture Portal Out
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits the Layer fed into the Texture Portal Out with the same channel.
    /// A missing or self-referential partner degrades to an empty layer rather
    /// than throwing, so a half-wired portal never collapses the evaluation.
    /// </summary>
    [System.Serializable]
    public class TexturePortalInNode : Generator
    {
        public override string NodeName { get { return "Texture Portal In"; } }
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "Receives a layer from a Texture Portal Out on the same channel. Wireless graph connection."; }
        }

        /// <summary>Channel name. A Texture Portal Out with the same channel feeds this.</summary>
        public string channel = "default";

        /// <summary>
        /// The kind of data this texture portal emits. Set it to the SAME type as
        /// the Texture Portal Out on the matching channel. Defaults to Layer.
        /// </summary>
        public PortType portType = PortType.Layer;

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
            if (string.IsNullOrEmpty(channel)) return PortalRouting.BlankProduct(portType, tile);

            // ReferenceEquals rather than ==: the == operator on a UnityEngine.Object
            // marshals into native code, and Generate can run off the main thread in
            // the general node path. Same guard the height PortalIn uses.
            if (object.ReferenceEquals(tile.Graph, null)) return PortalRouting.BlankProduct(portType, tile);

            TexturePortalOutNode partner = null;
            foreach (Generator node in tile.Graph.nodes)
            {
                TexturePortalOutNode outNode = node as TexturePortalOutNode;
                if (outNode != null && outNode.channel == channel && !outNode.disabled)
                {
                    partner = outNode;
                    break;
                }
            }

            // No partner: a typed blank, not null. A node that vanishes when
            // half-wired is indistinguishable from a node that is broken.
            if (partner == null) return PortalRouting.BlankProduct(portType, tile);

            // Pull whatever feeds the partner's input and forward it AS-IS, deep-copied.
            // No forced `as LayerProduct` cast: the portal carries whatever type the
            // user wired (Layer, Surface, Water, ...), which is what stops a non-Layer
            // product from casting to null and emitting an empty layer. Routing through
            // tile.PullInput keeps the evaluator's cache and re-entry guard in play, so
            // a portal wired back into its own chain degrades to a blank rather than
            // recursing.
            object pulled = tile.PullInput(partner, 0, stop);
            if (pulled == null) return PortalRouting.BlankProduct(portType, tile);

            return PortalRouting.CopyProduct(pulled);
        }
    }
}
