// UC-T06-05: immutable sampling-stage evidence, separate from later habitat filtering and native placement counts.
namespace UniverseCreator
{
    /// <summary>Counts from one completed Poisson sampling pass; values describe work and rejection causes, never rendered plants.</summary>
    /// <remarks>Owned by one worker pass and copied by value into its graph publication. Cancelled/failed passes publish no report.</remarks>
    public readonly struct PoissonSamplingReport
    {
        /// <summary>True for the cap-independent world lattice; false for the legacy sequential sampler.</summary>
        public readonly bool StableWorldGrid;
        /// <summary>Grid cells visited, including legacy halo cells.</summary>
        public readonly long CellsVisited;
        /// <summary>Candidate attempts inside the requested half-open world rectangle.</summary>
        public readonly int Candidates;
        /// <summary>Inside candidates vetoed by minimum-distance competition.</summary>
        public readonly int SpacingRejected;
        /// <summary>Inside candidates rejected by a zero/fractional density or mask.</summary>
        public readonly int DensityRejected;
        /// <summary>Inside candidates rejected because their density/mask was nonfinite.</summary>
        public readonly int InvalidDensityRejected;
        /// <summary>Geometrically/density-admitted candidates omitted by the stable point cap; legacy early-stop passes cannot count unvisited candidates.</summary>
        public readonly int CapRejected;
        /// <summary>Candidates emitted before later scatter filters, habitat/cluster thinning and native application.</summary>
        public readonly int Emitted;
        internal PoissonSamplingReport(bool stable, long cells, int candidates, int spacing, int density, int invalid, int cap, int emitted)
        { StableWorldGrid = stable; CellsVisited = cells; Candidates = candidates; SpacingRejected = spacing;
            DensityRejected = density; InvalidDensityRejected = invalid; CapRejected = cap; Emitted = emitted; }
    }
}
