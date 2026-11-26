// UC-T05-08: measured node pulls are separate from preview timing and are disabled with the existing metrics switch.
using System;
using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem
{
    public sealed partial class UCGenerationMetrics
    {
        /// <summary>Detached counts and compute timings for one node across tiles in a recorded world run.</summary>
        [Serializable]
        public struct NodeTiming
        {
            /// <summary>Compute attempts, evaluation-local hits and retained-product hits.</summary>
            public long computes, localHits, retainedHits;
            /// <summary>Total inclusive compute time in milliseconds, including failed/cancelled attempts.</summary>
            public double inclusiveMilliseconds;
            /// <summary>Total compute time in milliseconds after subtracting measured child node execution.</summary>
            public double exclusiveMilliseconds;
        }

        /// <summary>Reads a measured node without allocating or beginning a world run.</summary>
        /// <param name="run">Exact recorded world/run identity.</param>
        /// <param name="nodeId">Nonempty stable authored node ID.</param>
        /// <param name="timing">Detached timing/counts; default when not measured or outside the 256-node recording limit.</param>
        /// <returns>False when collection was disabled, the run retired, or the node was never pulled.</returns>
        public bool TryGetNode(GenerationRunId run, string nodeId, out NodeTiming timing)
        {
            timing = default;
            if (string.IsNullOrEmpty(nodeId)) return false;
            lock (gate)
                foreach (var record in runs) if (record.Id.Equals(run)) return record.TryGetNode(nodeId, out timing);
            return false;
        }

        internal sealed partial class RunRecord
        {
            private readonly Dictionary<string, NodeTiming> nodes = new Dictionary<string, NodeTiming>();
            internal void RecordNode(string id, int kind, double inclusiveMilliseconds = 0, double exclusiveMilliseconds = 0)
            {
                lock (sync)
                {
                    if (!nodes.TryGetValue(id, out var timing) && nodes.Count >= 256) return;
                    if (kind == 0)
                    {
                        timing.computes++;
                        timing.inclusiveMilliseconds += inclusiveMilliseconds;
                        timing.exclusiveMilliseconds += exclusiveMilliseconds;
                    }
                    else if (kind == 1) timing.localHits++;
                    else timing.retainedHits++;
                    nodes[id] = timing;
                }
            }
            internal bool TryGetNode(string id, out NodeTiming timing) { lock (sync) return nodes.TryGetValue(id, out timing); }
        }
    }
}
