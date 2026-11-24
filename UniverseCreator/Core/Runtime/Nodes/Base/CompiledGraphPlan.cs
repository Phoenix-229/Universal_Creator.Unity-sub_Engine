using System;
using System.Collections.Generic;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T01-08: ordered lookup indexes for one fixed graph revision.</summary>
    /// <remarks>
    /// Build on the main thread after node preparation. The owning snapshot must keep nodes alive
    /// and must not edit their IDs, ports, portal channels, disabled flags or topology after build.
    /// Queries are read-only and may run concurrently. This indexes existing semantics; validation
    /// remains a separate gate and no authored data is repaired or removed here.
    /// </remarks>
    public sealed class CompiledGraphPlan
    {
        private struct Socket : IEquatable<Socket>
        {
            internal readonly string node;
            internal readonly int port;
            internal Socket(string node, int port) { this.node = node; this.port = port; }
            public bool Equals(Socket other) { return port == other.port && node == other.node; }
            public override bool Equals(object other) { return other is Socket && Equals((Socket)other); }
            public override int GetHashCode() { unchecked { return (node != null ? node.GetHashCode() : 0) * 397 ^ port; } }
        }

        /// <summary>One output root and its captured outgoing port count; Vegetation roots apply canopy producers before understory, retaining authored order within a family.</summary>
        public readonly struct OutputRoot
        {
            /// <summary>Prepared node kept alive by the owning snapshot.</summary>
            public readonly Generator Node;
            /// <summary>Number of declared outputs; zero still drives Generate at port zero.</summary>
            public readonly int PortCount;
            internal readonly int SourceSlot; // UC-T06-05: ordering must also tolerate duplicate/empty IDs awaiting validation.
            internal OutputRoot(Generator node, int count, int slot) { Node = node; PortCount = count; SourceSlot = slot; }
        }

        /// <summary>UC-T01-08: captured execution requirement and source location for one authored node slot.</summary>
        public readonly struct ExecutionStep
        {
            /// <summary>Prepared node kept alive by the snapshot; never mutate it during evaluation.</summary>
            public readonly Generator Node;
            /// <summary>Node ID at compilation, including empty IDs awaiting the separate validation gate.</summary>
            public readonly string NodeId;
            /// <summary>Zero-based source slot, preserved even when earlier slots are unresolved.</summary>
            public readonly int NodeSlot;
            /// <summary>Declared domain captured once; queries never call the node's metadata getter again.</summary>
            public readonly NodeExecutionDomain Domain;
            /// <summary>UC-T02-05: captured availability of a main-thread CPU fallback when graphics are absent.</summary>
            public readonly bool HasCpuFallback;
            internal ExecutionStep(Generator node, int slot)
            {
                Node = node; NodeId = node.id; NodeSlot = slot; Domain = node.ExecutionDomain;
                HasCpuFallback = node.HasCpuFallback;
            }
        }

        private static readonly IReadOnlyList<Connection> NoEdges = Array.Empty<Connection>();
        private static readonly IReadOnlyList<Generator> NoNodes = Array.Empty<Generator>();
        private readonly Dictionary<string, Generator> nodes = new Dictionary<string, Generator>(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionStep> execution = new Dictionary<string, ExecutionStep>(StringComparer.Ordinal);
        private readonly Dictionary<Socket, IReadOnlyList<Connection>> incoming;
        private readonly Dictionary<Socket, IReadOnlyList<Connection>> outgoing;
        private readonly Dictionary<string, IReadOnlyList<Generator>> downstream;
        private readonly Dictionary<string, IReadOnlyList<Generator>> portalSenders;

        /// <summary>Captured output roots; the read-only collection is shared by all tile evaluations.</summary>
        public IReadOnlyList<OutputRoot> Outputs { get; private set; }

        /// <summary>UC-T01-08: read-only requirements in authored order, including disabled and disconnected nodes.</summary>
        /// <remarks>Null slots have no step. This is metadata, not a topological schedule or permission to run a node.</remarks>
        public IReadOnlyList<ExecutionStep> ExecutionSteps { get; private set; }

        private CompiledGraphPlan(Graph graph)
        {
            var inputLists = new Dictionary<Socket, List<Connection>>();
            var outputLists = new Dictionary<Socket, List<Connection>>();
            var senderLists = new Dictionary<string, List<Generator>>(StringComparer.Ordinal);
            var dependentLists = new Dictionary<string, List<Generator>>(StringComparer.Ordinal);
            var roots = new List<OutputRoot>();
            var steps = new List<ExecutionStep>(graph.nodes.Count);
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null) continue;
                var step = new ExecutionStep(node, i);
                steps.Add(step);
                // Legacy lookup selects the first node with an ID; diagnostics may reject duplicates later.
                if (!string.IsNullOrEmpty(node.id) && !nodes.ContainsKey(node.id))
                {
                    nodes.Add(node.id, node);
                    execution.Add(node.id, step); // UC-T02-05: evaluate captured domain metadata without calling live getters.
                }
                if (node is IOutputNode)
                {
                    Port[] ports = node.GetOutputs();
                    roots.Add(new OutputRoot(node, ports != null ? ports.Length : 0, i));
                }
                IPortalOut sender = node as IPortalOut;
                if (sender != null && !node.disabled && !string.IsNullOrEmpty(sender.PortalChannel))
                    Append(senderLists, sender.PortalChannel, node);
            }
            for (int i = 0; i < graph.connections.Count; i++)
            {
                Connection edge = graph.connections[i];
                Append(inputLists, new Socket(edge.targetNodeId, edge.targetPortIndex), edge);
                Append(outputLists, new Socket(edge.sourceNodeId, edge.sourcePortIndex), edge);
                AddDependent(dependentLists, edge.sourceNodeId, GetNode(edge.targetNodeId));
            }
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                IPortalIn receiver = node as IPortalIn;
                if (receiver == null) continue;
                Generator sender = receiver.ResolveSender(graph);
                if (sender != null) AddDependent(dependentLists, sender.id, node);
            }
            if (graph.Domain == UCGraphDomain.Vegetation)
                foreach (var consumer in graph.nodes)
                    foreach (var root in roots)
                        if (IsCanopyDependency(root.Node, consumer)) AddDependent(dependentLists, root.Node.id, consumer); // UC-T06-05: live invalidation follows the same semantic dependency as retained hashes.
            incoming = Freeze(inputLists);
            outgoing = Freeze(outputLists);
            downstream = Freeze(dependentLists);
            portalSenders = Freeze(senderLists);
            if (graph.Domain == UCGraphDomain.Vegetation)
            {
                // UC-T06-05: the existing publication/cache path deposits crowns before lower layers sample them.
                // Legacy mixed graphs retain their authored execution order for migration/serialization compatibility.
                roots.Sort((a, b) => {
                    int order = VegetationOrder(a.Node).CompareTo(VegetationOrder(b.Node));
                    return order != 0 ? order : a.SourceSlot.CompareTo(b.SourceSlot);
                });
            }
            Outputs = Array.AsReadOnly(roots.ToArray());
            ExecutionSteps = Array.AsReadOnly(steps.ToArray());
        }

        private static int VegetationOrder(Generator node)
        {
            if (node is Scatter.TreeScatterNode) return 0;
            if (node is Scatter.ShrubScatterNode) return 1;
            if (node is Scatter.GrassDetailScatterNode) return 3;
            return 2;
        }

        // UC-T06-05: canopy is a shared side product with a real dependency even without a visible raster wire.
        internal static bool IsCanopyDependency(Generator producer, Generator consumer)
        {
            if (producer == null || producer.disabled || consumer == null || ReferenceEquals(producer, consumer) ||
                !(producer is Scatter.TreeScatterNode || producer is Scatter.ShrubScatterNode)) return false;
            int consumerOrder;
            if (consumer is Scatter.VegetationHabitatNode habitat)
                consumerOrder = habitat.family == Scatter.VegetationHabitatFamily.Trees ? 0 :
                    habitat.family == Scatter.VegetationHabitatFamily.Shrubs ? 1 : 3;
            else if (consumer is Scatter.ScatterNodeBase) consumerOrder = VegetationOrder(consumer);
            else return false;
            return VegetationOrder(producer) < consumerOrder;
        }

        /// <summary>Builds ordered indexes without changing the source graph or resolving any Unity asset on a worker.</summary>
        /// <param name="graph">Prepared graph with non-null node/edge lists; its revision must remain fixed.</param>
        /// <returns>Immutable lookup collections owned by the caller's graph snapshot.</returns>
        /// <exception cref="ArgumentException">The graph or its containers are absent.</exception>
        /// <remarks>Cost and storage scale with nodes, edges and portal dependencies; no raster products are copied.</remarks>
        public static CompiledGraphPlan Build(Graph graph)
        {
            if (ReferenceEquals(graph, null) || graph.nodes == null || graph.connections == null)
                throw new ArgumentException("A graph with node and connection lists is required.", "graph");
            return new CompiledGraphPlan(graph);
        }

        /// <summary>Returns the first node with the exact ID, or null for an absent/empty ID.</summary>
        /// <param name="id">Case-sensitive serialized node ID.</param>
        public Generator GetNode(string id)
        {
            Generator node;
            return !string.IsNullOrEmpty(id) && nodes.TryGetValue(id, out node) ? node : null;
        }

        /// <summary>Gets captured execution requirements for the first node with an ID.</summary>
        /// <param name="id">Case-sensitive ID; null/empty IDs are absent.</param>
        /// <param name="step">Captured requirements, or the default value when absent.</param>
        /// <returns>True when the compiled plan contains the ID.</returns>
        public bool TryGetExecutionStep(string id, out ExecutionStep step)
        {
            step = default;
            return !string.IsNullOrEmpty(id) && execution.TryGetValue(id, out step);
        }

        /// <summary>Returns every inlet edge in authored order; an unconnected port returns a shared empty collection.</summary>
        /// <param name="nodeId">Exact target ID, including null for a malformed legacy edge.</param>
        /// <param name="port">Zero-based target port; out-of-range values simply have no edges unless serialized.</param>
        public IReadOnlyList<Connection> GetInputEdges(string nodeId, int port)
        {
            IReadOnlyList<Connection> edges;
            return incoming.TryGetValue(new Socket(nodeId, port), out edges) ? edges : NoEdges;
        }

        /// <summary>Returns every outlet edge in authored order; an unconnected port returns a shared empty collection.</summary>
        /// <param name="nodeId">Exact source ID.</param>
        /// <param name="port">Zero-based source port.</param>
        public IReadOnlyList<Connection> GetOutputEdges(string nodeId, int port)
        {
            IReadOnlyList<Connection> edges;
            return outgoing.TryGetValue(new Socket(nodeId, port), out edges) ? edges : NoEdges;
        }

        /// <summary>Returns the newest edge into an inlet, or default(Connection) when unconnected.</summary>
        /// <param name="nodeId">Exact target ID.</param>
        /// <param name="port">Zero-based target port.</param>
        public Connection GetInputConnection(string nodeId, int port)
        {
            IReadOnlyList<Connection> edges = GetInputEdges(nodeId, port);
            return edges.Count > 0 ? edges[edges.Count - 1] : default(Connection);
        }

        /// <summary>Returns distinct direct consumers, including resolved wireless portal receivers.</summary>
        /// <param name="nodeId">Exact source ID; null/empty or disconnected IDs return an empty collection.</param>
        public IReadOnlyList<Generator> GetDependents(string nodeId)
        {
            IReadOnlyList<Generator> result;
            return !string.IsNullOrEmpty(nodeId) && downstream.TryGetValue(nodeId, out result) ? result : NoNodes;
        }

        /// <summary>Returns the first enabled sender matching both family and case-sensitive channel.</summary>
        /// <typeparam name="TOut">Sender family; ordinary and texture portals remain independent.</typeparam>
        /// <param name="channel">Authored channel; null/empty yields null.</param>
        public TOut FindSender<TOut>(string channel) where TOut : Generator, IPortalOut
        {
            IReadOnlyList<Generator> candidates;
            if (string.IsNullOrEmpty(channel) || !portalSenders.TryGetValue(channel, out candidates)) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                TOut sender = candidates[i] as TOut;
                if (sender != null) return sender;
            }
            return null;
        }

        private static void AddDependent(Dictionary<string, List<Generator>> map, string id, Generator node)
        {
            if (string.IsNullOrEmpty(id) || node == null) return;
            List<Generator> list;
            if (!map.TryGetValue(id, out list)) { list = new List<Generator>(); map.Add(id, list); }
            if (!list.Contains(node)) list.Add(node);
        }

        private static void Append<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        {
            List<TValue> list;
            if (!map.TryGetValue(key, out list)) { list = new List<TValue>(); map.Add(key, list); }
            list.Add(value);
        }

        private static Dictionary<TKey, IReadOnlyList<TValue>> Freeze<TKey, TValue>(Dictionary<TKey, List<TValue>> source)
        {
            var result = new Dictionary<TKey, IReadOnlyList<TValue>>(source.Count, source.Comparer);
            foreach (var entry in source) result.Add(entry.Key, Array.AsReadOnly(entry.Value.ToArray()));
            return result;
        }
    }
}
