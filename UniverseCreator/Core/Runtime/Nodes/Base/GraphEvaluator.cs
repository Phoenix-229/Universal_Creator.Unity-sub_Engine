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
        {
            if (tile == null) throw new ArgumentNullException("tile");
            if (stop == null) stop = new StopToken();

            tile.Bind(this);
            visiting.Clear();

            List<Generator> outputNodes = new List<Generator>();
            foreach (Generator n in graph.nodes)
                if (n is IOutputNode) outputNodes.Add(n);

            foreach (Generator node in outputNodes)
            {
                if (stop.StopRequested) return;
                if (node.disabled) continue;

                Port[] outs = node.GetOutputs();
                int portCount = outs != null ? outs.Length : 0;

                // Output nodes typically have no outgoing ports; they consume and
                // write into tile.outputs. Pull port 0 anyway to drive Generate().
                if (portCount == 0)
                    PullNode(node, 0, tile, stop);
                else
                    for (int i = 0; i < portCount; i++)
                        PullNode(node, i, tile, stop);
            }
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
        /// Returns a node's product for one output port, computing it if this is the
        /// first request for this chunk.
        /// </summary>
        public object PullNode(Generator node, int outputIndex, TileData tile, StopToken stop)
        {
            if (node == null) return null;
            if (stop != null && stop.StopRequested) return null;

            object cached = tile.GetCached(node.id, outputIndex);
            if (cached != null) return cached;

            // A disabled node is transparent, not a hole: pass the first
            // type-compatible input through so downstream work keeps its shape.
            if (node.disabled)
            {
                object passthrough = GetPassthrough(node, outputIndex, tile, stop);
                if (passthrough != null)
                    tile.SetCached(node.id, outputIndex, passthrough);
                return passthrough;
            }

            if (visiting.Contains(node.id))
            {
                // Cycle. Bail with null rather than blowing the stack; Graph.Validate
                // surfaces this to the user in the inspector.
                return null;
            }

            visiting.Add(node.id);
            object product;
            try
            {
                product = node.Generate(outputIndex, tile, stop);
            }
            finally
            {
                visiting.Remove(node.id);
            }

            // A cancelled run yields half-finished data; don't poison the cache.
            if (stop != null && stop.StopRequested) return null;

            if (product != null)
                tile.SetCached(node.id, outputIndex, product);

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
