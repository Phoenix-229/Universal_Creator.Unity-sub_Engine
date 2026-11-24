// -----------------------------------------------------------------------------
//  Universe Creator - Module 1/2: shared portal plumbing
//  File:    PortalRouting.cs
//  Purpose: The routing logic the four portal nodes share, extracted so both the
//           height-graph portals (Utility, Matrix by default) and the texture-graph
//           portals (Texture, Layer by default) carry ANY port type instead of one
//           hardwired type.
//
//  WHY THIS EXISTS
//  ---------------
//  The old portals forced a single cast - `... as Matrix` in the height portal,
//  `... as LayerProduct` in the texture portal. A product of any other type cast
//  to null there and the portal quietly emitted a blank, which is the "it does not
//  show up / null" a user hits the moment they route anything but the default type
//  through a portal. A portal is a pass-through: it should hand on WHATEVER the
//  partner produced, typed by the socket the user picked, not reinterpret it.
//
//  Two operations cover that:
//    CopyProduct  - forward a pulled product, deep-copied so a downstream mutation
//                   cannot reach back through the portal into the source chain.
//    BlankProduct - a typed empty for a half-wired portal, of the RIGHT type, so a
//                   missing partner degrades to "an empty Layer" (etc.), never to
//                   null and never to an empty Matrix standing in for a Layer.
//
//  Runs off the main thread on the general node path (the height portal evaluates
//  on worker threads), so it touches no Unity API - every product here is plain
//  managed data, and TextureRef is copied by reference, never dereferenced.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// A portal whose socket type is DERIVED, not authored. The editor calls
    /// ResolveType when the graph is built or rewired, so the socket colours itself
    /// by the data actually crossing the channel - there is no Port Type field for a
    /// user to set or keep in sync. The channel name alone identifies a pair.
    /// </summary>
    public interface IPortalTyped
    {
        void ResolveType(Graph graph);
    }

    /// <summary>
    /// The sending half of a portal pair. Exposes its channel so the shared resolver
    /// can find it without knowing whether it is a height or a texture portal. The
    /// receiving half still matches only its OWN sender kind, so a height channel and
    /// a texture channel that happen to share a name never cross wires.
    /// </summary>
    public interface IPortalOut : IPortalTyped
    {
        string PortalChannel { get; }
    }

    /// <summary>
    /// UC-O01 / UC-A02: exposes a receiver's wireless dependency to graph hashing.
    /// Execution and hashing must resolve the same sender from a stable graph revision.
    /// </summary>
    public interface IPortalIn : IPortalTyped
    {
        /// <summary>Resolves the first enabled sender of this receiver's family and channel.</summary>
        /// <param name="graph">Graph to inspect without mutation; null yields no sender.</param>
        /// <returns>The selected sender, or null when the channel has no enabled partner.</returns>
        Generator ResolveSender(Graph graph);
    }

    public static class PortalRouting
    {
        /// <summary>
        /// UC-T01-12: copies editable field/descriptor products across a portal; native textures remain borrowed.
        /// </summary>
        /// <param name="product">Published input, or null. Unknown plugin products remain borrowed and read-only.</param>
        /// <returns>Independent mutable metadata/field, or the original value/unknown product; null remains null.</returns>
        /// <remarks>Surface baked matrices stay shared read-only; a writer copies them explicitly rather than on every feature hop.</remarks>
        public static object CopyProduct(object product)
        {
            if (product == null) return null;

            // Height / baked-map currency.
            Matrix m = product as Matrix;
            if (m != null) return new Matrix(m);

            // Texture-graph currencies that own a Copy().
            LayerProduct layer = product as LayerProduct;
            if (layer != null) return layer.Copy();

            SurfaceProduct surface = product as SurfaceProduct;
            if (surface != null) return surface.Copy();

            WaterProduct water = product as WaterProduct;
            if (water != null) return water.Copy();

            UVTransform uv = product as UVTransform;
            if (uv != null) return uv.Copy();

            SplatProduct splat = product as SplatProduct;
            if (splat != null) return splat.Copy();
            GradientProduct gradient = product as GradientProduct;
            if (gradient != null) return gradient.Copy();
            TextureRef texture = product as TextureRef;
            if (texture != null) return texture.Copy();

            // UC-T01-12: value products are immutable; unknown plugin products retain the borrowed-input contract.
            return product;
        }

        /// <summary>
        /// A typed empty product, for a portal whose partner is missing or whose
        /// input is unwired. Matching the declared type is the whole point: the old
        /// portals returned an empty Matrix (or a null the caller cast away) no
        /// matter what the socket claimed, so a Layer portal that lost its partner
        /// fed a Matrix into a Layer consumer. This returns the right shape of empty.
        /// </summary>
        public static object BlankProduct(PortType type, TileData tile)
        {
            switch (type)
            {
                case PortType.Matrix:   return tile != null ? tile.CreateMatrix() : null;
                case PortType.Layer:    return new LayerProduct();
                case PortType.Splat:    return new SplatProduct();
                case PortType.Surface:  return new SurfaceProduct();
                case PortType.Water:    return new WaterProduct();
                case PortType.Gradient: return new GradientProduct();
                case PortType.Texture:  return new TextureRef();
                case PortType.UV:       return new UVTransform();
                case PortType.Color:    return (object)Color.white;
                case PortType.Scalar:   return (object)0f;
                case PortType.Image:    return null; // UC-T07-02: missing pixels remain missing; never fabricate a dimensionless image.
                case PortType.Vector:   return (object)Vector4.zero; // UC-T07-03: a value parameter has no native lifetime.
                default:                return tile != null ? tile.CreateMatrix() : null;
            }
        }

        /// <summary>
        /// The single port array a portal exposes for a given type and direction.
        /// The socket is always named "Port"; the type it carries is shown by the
        /// node's own PortType dropdown and by the socket colour, not by the name.
        /// Both ends of a channel should be set to the same type. Callers cache the
        /// result and rebuild only when the type changes, so this never allocates
        /// on a repaint.
        /// </summary>
        public static Port[] BuildPortArray(PortType type, PortDirection direction)
        {
            return new[] { new Port("Port", type, direction, direction == PortDirection.Output) };
        }

        /// <summary>
        /// The type actually flowing into a node's input port, read from the output
        /// port of whatever is wired to it. This is how a portal colours itself by the
        /// data it carries instead of by a type the user has to pick: the source's own
        /// declared output type IS the truth. Returns <paramref name="fallback"/> when
        /// the input is unwired or the source cannot be resolved, so an empty portal
        /// still has a definite shape rather than a null. ReferenceEquals guards the
        /// Graph (a UnityEngine.Object) because this can run off the main thread; the
        /// Generators it walks are plain managed objects, safe to compare with ==.
        /// </summary>
        public static PortType ResolveSourceType(Graph graph, Generator consumer, int inputIndex, PortType fallback)
        {
            if (object.ReferenceEquals(graph, null) || consumer == null) return fallback;

            Connection c = graph.GetInputConnection(consumer.id, inputIndex);
            if (string.IsNullOrEmpty(c.sourceNodeId)) return fallback;

            Generator source = graph.GetNodeById(c.sourceNodeId);
            if (source == null) return fallback;

            Port[] outs = source.GetOutputs();
            if (outs == null || c.sourcePortIndex < 0 || c.sourcePortIndex >= outs.Length) return fallback;

            return outs[c.sourcePortIndex].type;
        }

        /// <summary>
        /// The type a portal-in should carry on <paramref name="channel"/>: found by
        /// locating the matching, enabled sender of type <typeparamref name="TOut"/>
        /// and reading what feeds ITS input. No sender, or a sender whose own input is
        /// unwired, yields <paramref name="fallback"/>. Restricting the sender to TOut
        /// is what keeps a height channel and a texture channel of the same name from
        /// pairing. The source's output type is a static declaration, so reading it
        /// here never recurses into another portal's live resolve.
        /// </summary>
        public static PortType ResolveChannelType<TOut>(Graph graph, string channel, PortType fallback)
            where TOut : Generator, IPortalOut
        {
            // UC-O01 / UC-A02: share sender precedence with generation and cache dependencies.
            TOut sender = FindSender<TOut>(graph, channel);
            return sender != null ? ResolveSourceType(graph, sender, 0, fallback) : fallback;
        }

        /// <summary>
        /// UC-O01 / UC-A02: finds the first enabled sender in graph-list order without
        /// allocating or touching Unity native state. Call against a stable graph revision.
        /// </summary>
        /// <typeparam name="TOut">Sender family; ordinary and texture channels stay independent.</typeparam>
        /// <param name="graph">Graph to inspect; null or absent nodes yield null.</param>
        /// <param name="channel">Case-sensitive channel name; null/empty has no partner.</param>
        /// <returns>The first enabled matching sender, or null if none exists.</returns>
        public static TOut FindSender<TOut>(Graph graph, string channel)
            where TOut : Generator, IPortalOut
        {
            if (object.ReferenceEquals(graph, null) || graph.nodes == null || string.IsNullOrEmpty(channel))
                return null;

            // UC-T01-08: preserve family/order selection while avoiding a full graph scan on every pull.
            if (graph.compiledPlan != null) return graph.compiledPlan.FindSender<TOut>(channel);
            foreach (Generator node in graph.nodes)
            {
                TOut sender = node as TOut;
                if (sender != null && !sender.disabled && sender.PortalChannel == channel)
                    return sender;
            }
            return null;
        }
    }
}
