// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GraphEvaluator.cs
//  Purpose: Pull-based lazy evaluation of a node graph for one chunk.
//
//  MODEL
//  -----
//  Nothing runs until an output node is asked for its product. Pulling an input
//  recurses upstream, and every product is memoised in TileData keyed by
//  (nodeId, portIndex), so a node feeding three consumers still computes once.
//
//  THREADING
//  ---------
//  Evaluate() is designed to run on a worker thread. It touches no UnityEngine
//  API. Output nodes deposit results into TileData.outputs, which the caller
//  applies to TerrainData on the main thread afterwards.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace UniverseCreator.Nodes
{
    public class GraphEvaluator
    {
        private readonly Graph graph;

        // Nodes currently on the recursion stack. Guards against cycles that
        // slipped past Graph.WouldCreateCycle (e.g. hand-edited asset files).
        private readonly HashSet<string> visiting = new HashSet<string>();

        public GraphEvaluator(Graph graph)
        {
            // ReferenceEquals, not ==: Graph is a UnityEngine.Object and its ==
            // operator marshals into native code to test whether the native side is
            // still alive. This constructor may be reached from a worker thread, so
            // the real "is it a live asset" check belongs to the main-thread caller.
            if (object.ReferenceEquals(graph, null)) throw new ArgumentNullException("graph");
            this.graph = graph;
        }

        public Graph Graph { get { return graph; } }

        /// <summary>
        /// Runs the graph for one chunk. Pulls every output node in turn; each pull
        /// recursively drags in only the upstream nodes it actually needs, so
        /// disconnected islands cost nothing.
        /// </summary>
        public void Evaluate(TileData tile, StopToken stop)
            => Evaluate(tile, stop, null); // UC-T05-04: preserve the original whole-graph entry point.

        /// <summary>Evaluates selected terminal outputs through the same compiled pulls, cache and publication accounting.</summary>
        /// <param name="tile">Non-null owned evaluation tile.</param>
        /// <param name="stop">Cooperative cancellation token, or null for a new token.</param>
        /// <param name="outputFilter">Optional pure predicate over frozen output nodes; null includes every enabled terminal.</param>
        /// <remarks>UC-T05-04: baseline climate excludes dynamic lighting roots without duplicating an evaluator or pulling accepted terrain.</remarks>
        /// <exception cref="ArgumentNullException">The tile is null.</exception>
        public void Evaluate(TileData tile, StopToken stop, Func<Generator, bool> outputFilter)
        {
            if (tile == null) throw new ArgumentNullException("tile");
            if (stop == null) stop = new StopToken();

            tile.Bind(this);
            visiting.Clear();

            // UC-T01-08: compiled roots avoid graph scans and port-array construction for every tile.
            CompiledGraphPlan plan = graph.compiledPlan;
            int roots = plan != null ? plan.Outputs.Count : graph.nodes.Count;
            for (int root = 0; root < roots; root++)
            {
                Generator node = plan != null ? plan.Outputs[root].Node : graph.nodes[root];
                if (!(node is IOutputNode) || (outputFilter != null && !outputFilter(node))) continue;
                if (stop.StopRequested) return;
                if (node.disabled) continue;

                Port[] outs = plan == null ? node.GetOutputs() : null;
                int portCount = plan != null ? plan.Outputs[root].PortCount : outs != null ? outs.Length : 0;

                // Output nodes typically have no outgoing ports; they consume and
                // write into tile.outputs. Pull port 0 anyway to drive Generate().
                if (portCount == 0)
                    PullNode(node, 0, tile, stop);
                else
                    for (int i = 0; i < portCount; i++)
                        PullNode(node, i, tile, stop);
            }
            tile.FinishPublications(); // UC-T02-07: an empty publication set explicitly replaces the previous owner's channels.
            tile.TrackProductMemory(tile.outputs); // UC-T02-06: outputs also own arrays created by void output nodes.
            tile.metrics?.ObserveMemory(tile.MemoryBudget); // UC-T02-09: whole-world evaluation also reports its observed payload ownership.
        }

        /// <summary>
        /// Resolves the product feeding a node's input port, or null if unconnected.
        /// This is what Generator.GetInputMatrix routes to.
        /// </summary>
        public object PullInput(Generator consumer, int inputIndex, TileData tile, StopToken stop)
        {
            if (consumer == null) return null;

            Connection conn = graph.GetInputConnection(consumer.id, inputIndex);
            if (string.IsNullOrEmpty(conn.sourceNodeId)) return null;

            Generator source = graph.GetNodeById(conn.sourceNodeId);
            if (source == null) return null;

            return PullNode(source, conn.sourcePortIndex, tile, stop);
        }

        /// <summary>
        /// Resolves ALL products feeding one input port (fan-in), in connection-list
        /// order, computing each upstream source as needed. Returns null when the
        /// port carries no live edge. The per-product memoisation in PullNode still
        /// applies, so a source feeding several fan-in slots computes once.
        /// </summary>
        public List<object> PullInputAll(Generator consumer, int inputIndex, TileData tile, StopToken stop)
        {
            if (consumer == null) return null;

            // UC-T01-08: indexed inlet edges are immutable; only the returned product list is per pull.
            IReadOnlyList<Connection> conns = graph.compiledPlan != null
                ? graph.compiledPlan.GetInputEdges(consumer.id, inputIndex)
                : graph.GetInputConnections(consumer.id, inputIndex);
            if (conns == null || conns.Count == 0) return null;

            List<object> products = new List<object>(conns.Count);
            for (int i = 0; i < conns.Count; i++)
            {
                if (stop != null && stop.StopRequested) break;

                Generator source = graph.GetNodeById(conns[i].sourceNodeId);
                if (source == null) continue;

                object p = PullNode(source, conns[i].sourcePortIndex, tile, stop);
                if (p != null) products.Add(p);
            }

            return products.Count > 0 ? products : null;
        }

        /// <summary>
        /// UC-T01-12: resolves a borrowed input for a writer; published cache products must be copied before mutation.
        /// </summary>
        /// <param name="consumer">Receiving node; null returns null.</param>
        /// <param name="inputIndex">Zero-based inlet; an unwired inlet returns null.</param>
        /// <param name="tile">Non-null tile that owns the memoized products.</param>
        /// <param name="stop">Optional cancellation token; cancelled pulls return null.</param>
        /// <param name="mayMutate">Always false: a direct-wire count cannot prove ownership through aliases or retained parents.</param>
        /// <returns>Borrowed product or null. Only the caller that intends to write allocates a copy.</returns>
        public object PullInputForMutation(Generator consumer, int inputIndex, TileData tile,
                                           StopToken stop, out bool mayMutate)
        {
            mayMutate = false;
            if (consumer == null) return null;

            Connection conn = graph.GetInputConnection(consumer.id, inputIndex);
            if (string.IsNullOrEmpty(conn.sourceNodeId)) return null;

            Generator source = graph.GetNodeById(conn.sourceNodeId);
            if (source == null) return null;

            object product = PullNode(source, conn.sourcePortIndex, tile, stop);
            if (product == null) return null;

            // UC-T01-12: even a sole consumer can alias a cached parent through disabled bypass or a plugin passthrough.
            // PullNode has already published this object; retaining read-only pulls avoids cloning unrelated products.
            return product;
        }

        /// <summary>
        /// Returns a node's product for one output port, computing it if this is the
        /// first request for this chunk.
        /// </summary>
        /// <param name="node">Prepared node; null returns null.</param>
        /// <param name="outputIndex">Zero-based requested outlet.</param>
        /// <param name="tile">Non-null owning tile and caches.</param>
        /// <param name="stop">Optional cancellation token; cancelled results are not cached.</param>
        /// <returns>Borrowed published product, or null for absent/cancelled output.</returns>
        /// <exception cref="InvalidOperationException">UC-T02-05: the calling thread or graphics readiness cannot satisfy the node's domain.</exception>
        public object PullNode(Generator node, int outputIndex, TileData tile, StopToken stop)
        {
            if (node == null) return null;
            tile.BeginPublications(node.id + ":" + outputIndex); // UC-T02-07: collect transitive side results even through cached ancestors.
            bool succeeded = false;
            try
            {
                object product = PullNodeCore(node, outputIndex, tile, stop);
                succeeded = stop == null || !stop.StopRequested;
                return product;
            }
            finally { tile.EndPublications(succeeded); }
        }

        private object PullNodeCore(Generator node, int outputIndex, TileData tile, StopToken stop)
        {
            if (node == null) return null;
            if (stop != null && stop.StopRequested) return null;

            // UC-T02-05: enforce domains before either cache path; a native result cannot be smuggled onto a worker by a cache hit.
            if (graph.compiledPlan != null && graph.compiledPlan.TryGetExecutionStep(node.id, out CompiledGraphPlan.ExecutionStep step) && ReferenceEquals(step.Node, node))
                GraphExecutionGuard.Require(node, step.Domain, step.HasCpuFallback);
            else GraphExecutionGuard.Require(node, node.ExecutionDomain, node.HasCpuFallback);

            object cached = tile.GetCached(node.id, outputIndex);
            if (cached != null)
            {
                tile.RecordLocalHit(); // UC-T02-09: distinguish evaluation-local reuse from retained cross-bake hits.
                tile.metrics?.RecordNode(node.id, 1); // UC-T05-08: opt-in measured run diagnostics; no preview timing is mixed in.
                tile.ReplayPublications(node.id + ":" + outputIndex); // UC-T02-07: a local alias must forward its transitive publications.
                return cached;
            }

            // A disabled node is transparent, not a hole: pass the first
            // type-compatible input through so downstream work keeps its shape.
            if (node.disabled)
            {
                object passthrough = GetPassthrough(node, outputIndex, tile, stop);
                if (passthrough != null)
                    tile.SetCached(node.id, outputIndex, passthrough);
                return passthrough;
            }

            // T1: cross-bake retained cache. When the node's content hash matches what we
            // stored for this chunk+port on a previous bake, FORWARD that product without
            // recomputing - an edit elsewhere only recomputes the subtree it actually
            // changed. Correct by construction: the hash is the key, so a changed node (or
            // changed upstream) has a different hash and misses. Opt-in: null retained store
            // (single-tile path, tests) behaves exactly as before.
            if (tile.retained != null && node.contentHash != 0UL)
            {
                string rk = node.id + ":" + outputIndex;
                RetainedProduct rp;
                if (tile.retained.TryGetValue(rk, out rp) && rp.hash == node.contentHash && tile.TryRetainedProduct(rp, out object retainedProduct))
                {
                    tile.RecordRetainedHit();
                    tile.metrics?.RecordNode(node.id, 2); // UC-T05-08: distinguish retained and local node reuse.
                    tile.SetCached(node.id, outputIndex, retainedProduct);
                    tile.ReplayPublications(rp.Publications); // UC-T02-07: retained fields still have current-run consumer obligations.
                    return retainedProduct;
                }
            }

            if (visiting.Contains(node.id))
            {
                // Cycle. Bail with null rather than blowing the stack; Graph.Validate
                // surfaces this to the user in the inspector.
                return null;
            }

            visiting.Add(node.id);
            long nodeStarted = tile.metrics != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // UC-T05-08: no clock work when disabled.
            object product;
            try
            {
                tile.RecordCompute(); // UC-T02-09: attempts include failed/cancelled bodies, never cache hits.
                product = node.Generate(outputIndex, tile, stop);
            }
            finally
            {
                if (tile.metrics != null) tile.metrics.RecordNode(node.id, 0,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - nodeStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); // UC-T05-08: inclusive time also records failures.
                visiting.Remove(node.id);
            }

            // A cancelled run yields half-finished data; don't poison the cache.
            if (stop != null && stop.StopRequested) return null;

            if (product != null)
            {
                tile.SetCached(node.id, outputIndex, product);

                // T1: retain under the current hash so the next bake can forward it.
                if (tile.retained != null && node.contentHash != 0UL)
                    tile.retained[node.id + ":" + outputIndex] =
                        tile.RetainProduct(node.contentHash, product); // UC-T02-06: retained dictionaries hold evictable tokens, not hidden strong references.
            }

            return product;
        }

        // Finds an input on a disabled node whose type matches the requested output
        // and returns its product, so bypassing a modifier behaves like a wire.
        private object GetPassthrough(Generator node, int outputIndex, TileData tile, StopToken stop)
        {
            Port[] outs = node.GetOutputs();
            if (outs == null || outputIndex < 0 || outputIndex >= outs.Length) return null;

            PortType wanted = outs[outputIndex].type;

            Port[] ins = node.GetInputs();
            if (ins == null) return null;

            for (int i = 0; i < ins.Length; i++)
            {
                if (ins[i].type != wanted) continue;
                object upstream = PullInput(node, i, tile, stop);
                if (upstream != null) return upstream;
            }
            return null;
        }
    }
}
