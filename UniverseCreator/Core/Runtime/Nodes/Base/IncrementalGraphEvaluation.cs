// UC-T07-09: bounded main-thread slices reuse the same prepared graph, evaluator and tile cache as ordinary execution.
using System;
using System.Collections.Generic;

namespace UniverseCreator.Nodes
{
    /// <summary>Incrementally pulls an immutable graph's required outputs in dependency order without clearing reusable products.</summary>
    /// <remarks>Owner-thread only. A slice runs one declared node/output; existing node cancellation and allocation
    /// bounds still apply within that node. Native/GPU nodes run on Unity's main thread. CPU-only callers may
    /// execute the complete sequence through the shared worker admission pool. The caller owns snapshot/tile retirement.</remarks>
    public sealed class IncrementalGraphEvaluation
    {
        private readonly struct Step
        {
            internal readonly Generator Node;
            internal readonly int Port;
            internal Step(Generator node, int port) { Node = node; Port = port; }
        }
        private readonly List<Step> steps = new List<Step>();
        private readonly GraphEvaluator evaluator;
        private readonly TileData tile;
        private readonly StopToken stop;
        /// <summary>Number of completed node/output pulls, including cache hits.</summary>
        public int Completed { get; private set; }
        /// <summary>Bounded number of node/output pulls in this sequence.</summary>
        public int Total => steps.Count;
        /// <summary>True when every required output has completed; cancellation never reports completion.</summary>
        public bool IsComplete => !stop.StopRequested && Completed == Total;

        /// <summary>Builds the required output dependency order and binds the existing tile/evaluator.</summary>
        /// <param name="graph">Non-null captured/validated graph, stable until the caller retires the evaluation.</param>
        /// <param name="tile">Non-null tile containing captured world geometry, accepted inputs and memory admission.</param>
        /// <param name="stop">Required cooperative cancellation token.</param>
        /// <param name="selected">Optional selected node for a preview; null selects all enabled Apply terminals.</param>
        /// <param name="outputIndex">Selected node's output slot; zero for terminals with no outward ports.</param>
        /// <exception cref="ArgumentException">Inputs are absent, a port is invalid, topology is cyclic or exceeds 8,192 output steps.</exception>
        public IncrementalGraphEvaluation(Graph graph, TileData tile, StopToken stop, Generator selected = null, int outputIndex = 0)
        {
            if (graph == null || tile == null || stop == null) throw new ArgumentException("A captured graph, tile and stop token are required.");
            this.tile = tile; this.stop = stop; evaluator = new GraphEvaluator(graph); tile.Bind(evaluator);
            var visited = new HashSet<string>(); var visiting = new HashSet<string>();
            if (selected != null) Append(graph, selected, outputIndex, visited, visiting);
            else foreach (Generator node in graph.nodes)
                if (node is IOutputNode && !node.disabled)
                {
                    int count = Math.Max(1, node.GetOutputs().Length);
                    for (int i = 0; i < count; i++) Append(graph, node, i, visited, visiting);
                }
        }

        private void Append(Graph graph, Generator node, int port, HashSet<string> visited, HashSet<string> visiting)
        {
            if (node == null) throw new ArgumentException("Restore the missing graph dependency before running.");
            string key = node.id + ":" + port;
            if (visited.Contains(key)) return;
            if (!visiting.Add(key)) throw new ArgumentException("Remove the graph cycle before incremental evaluation.");
            if (visiting.Count > 1024 || steps.Count >= 8192) throw new ArgumentException("Incremental graph exceeds its bounded dependency/output envelope.");
            if (port < 0 || port >= Math.Max(1, node.GetOutputs().Length)) throw new ArgumentException("Graph output port is unavailable.");
            Port[] inputs = node.GetInputs();
            for (int i = 0; !node.disabled && i < inputs.Length; i++) // Disabled bypass keeps the existing evaluator's first compatible input semantics.
            {
                if (inputs[i].allowMultiple)
                {
                    foreach (Connection edge in graph.GetInputConnections(node.id, i))
                        Append(graph, graph.GetNodeById(edge.sourceNodeId), edge.sourcePortIndex, visited, visiting);
                }
                else
                {
                    Connection edge = graph.GetInputConnection(node.id, i);
                    if (!string.IsNullOrEmpty(edge.sourceNodeId)) Append(graph, graph.GetNodeById(edge.sourceNodeId), edge.sourcePortIndex, visited, visiting);
                }
            }
            if (!node.disabled && node is IPortalIn portal)
            {
                Generator sender = portal.ResolveSender(graph);
                if (sender != null) Append(graph, sender, 0, visited, visiting);
            }
            visiting.Remove(key); visited.Add(key); steps.Add(new Step(node, port));
        }

        /// <summary>Pulls at most one required node/output through the existing cache and declared execution guard.</summary>
        /// <returns>True if a step ran; false after completion or cancellation.</returns>
        /// <exception cref="InvalidOperationException">Execution domain, resource admission or a node's input contract is unsupported.</exception>
        public bool StepNext()
        {
            if (stop.StopRequested || Completed == Total) return false;
            Step next = steps[Completed]; evaluator.PullNode(next.Node, next.Port, tile, stop);
            if (stop.StopRequested) return false;
            Completed++;
            if (Completed == Total)
            {
                tile.FinishPublications(); tile.TrackProductMemory(tile.outputs); // UC-T07-09: terminal side results keep ordinary evaluator accounting.
                tile.metrics?.ObserveMemory(tile.MemoryBudget);
            }
            return true;
        }
    }
}
