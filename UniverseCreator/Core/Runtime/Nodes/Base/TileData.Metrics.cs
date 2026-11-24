using System.Threading;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes
{
    public partial class TileData
    {
        // UC-T02-09: capture once per evaluation; zero counter/clock work when metrics are disabled.
        internal UCGenerationMetrics.RunRecord metrics;
        internal void RecordCompute() { if (metrics != null) Interlocked.Increment(ref metrics.Computes); }
        internal void RecordLocalHit() { if (metrics != null) Interlocked.Increment(ref metrics.LocalHits); }
        internal void RecordRetainedHit() { if (metrics != null) Interlocked.Increment(ref metrics.RetainedHits); }
        internal void RecordDrainageSolve() { if (metrics != null) Interlocked.Increment(ref metrics.DrainageSolves); }
    }
}
