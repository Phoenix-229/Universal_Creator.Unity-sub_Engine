// UC-T05-06: typed accepted products retain their immutable payload under one world's existing memory owner.
using System;
using System.Threading;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes
{
    /// <summary>Physical interpretation of a published graph payload; consumers must request the matching units.</summary>
    public enum GraphProductUnits
    {
        /// <summary>World positions or heights in metres.</summary>
        WorldMetres,
        /// <summary>Height divided by the owning terrain's positive height scale.</summary>
        NormalizedHeight,
        /// <summary>Dimensionless coverage between zero and one.</summary>
        Coverage,
        /// <summary>Dimensionless component of a unit surface normal.</summary>
        UnitVectorComponent,
        /// <summary>Temperature in degrees Celsius.</summary>
        TemperatureCelsius,
        /// <summary>Immutable surface/material descriptor with its own documented channel units.</summary>
        SurfaceDescription
    }

    /// <summary>Pins one immutable accepted payload and its world, producer, revision, bounds and units until its consumer retires.</summary>
    /// <typeparam name="T">Managed published payload type; writers copy its data before mutation.</typeparam>
    /// <remarks>Acquisition occurs on the main thread; reads may run on workers. Dispose only after this handle's final consumer finishes.</remarks>
    public sealed class GraphProductHandle<T> : IDisposable where T : class
    {
        private T payload;
        private ProductMemoryBudget.Lease memory;
        /// <summary>World generation that supplied this payload; a later generation cannot consume it as current data.</summary>
        public GenerationRunId Run { get; }
        /// <summary>Producer's saved domain, independent of the consumer's domain.</summary>
        public UCGraphDomain ProducerDomain { get; }
        /// <summary>Accepted publication identity; changes on same-run ground replacement.</summary>
        public Guid PublicationVersion { get; }
        /// <summary>Immutable XZ region in world metres, including its edge samples.</summary>
        public Rect Bounds { get; }
        /// <summary>Interpretation required by a compatible consumer.</summary>
        public GraphProductUnits Units { get; }
        /// <summary>True after retirement; retired handles never return a payload.</summary>
        public bool IsReleased => payload == null;

        internal GraphProductHandle(T payload, GenerationRunId run, UCGraphDomain domain, Guid version, Rect bounds,
            GraphProductUnits units, ProductMemoryBudget budget, long bytes)
        {
            if (payload == null || budget == null || !run.IsAssigned || version == Guid.Empty ||
                !RiverChannelSnapshot.Finite(bounds.x) || !RiverChannelSnapshot.Finite(bounds.y) ||
                !RiverChannelSnapshot.Finite(bounds.width) || !RiverChannelSnapshot.Finite(bounds.height) ||
                !RiverChannelSnapshot.Finite(bounds.xMax) || !RiverChannelSnapshot.Finite(bounds.yMax) ||
                bounds.width <= 0 || bounds.height <= 0 || !Enum.IsDefined(typeof(GraphProductUnits), units) ||
                !Enum.IsDefined(typeof(UCGraphDomain), domain) || domain == UCGraphDomain.LegacyMixed)
                throw new ArgumentException("Invalid accepted graph product metadata.");
            memory = budget.Acquire(payload, bytes, 0, "accepted-graph-product");
            this.payload = payload; Run = run; ProducerDomain = domain; PublicationVersion = version; Bounds = bounds; Units = units;
        }

        /// <summary>Borrows this immutable payload only for an exactly matching world/revision and unit contract.</summary>
        /// <param name="run">Expected owning world and generation revision.</param>
        /// <param name="units">Required physical units.</param>
        /// <param name="value">Payload on success; null on retirement or a metadata mismatch.</param>
        /// <returns>True when this active handle satisfies both contracts.</returns>
        public bool TryGet(GenerationRunId run, GraphProductUnits units, out T value)
        { value = Run.Equals(run) && Units == units ? payload : null; return value != null; }

        /// <summary>Borrows a payload only when the expected accepted publication, world/run and units all match.</summary>
        /// <param name="run">Expected world and generation revision.</param>
        /// <param name="version">Expected publication identity, including same-run replacement.</param>
        /// <param name="units">Required physical interpretation.</param>
        /// <param name="value">Immutable borrowed payload, or null after release or a mismatch.</param>
        /// <returns>True for the exact accepted publication contract.</returns>
        public bool TryGet(GenerationRunId run, Guid version, GraphProductUnits units, out T value)
        { value = version == PublicationVersion && Run.Equals(run) && Units == units ? payload : null; return value != null; }

        /// <summary>Releases this consumer pin once; other consumers and the accepted producer retain their independent ownership.</summary>
        public void Dispose() { Interlocked.Exchange(ref payload, null); Interlocked.Exchange(ref memory, null)?.Dispose(); }
    }

    /// <summary>Acquires accepted main-world products without executing a second terrain graph.</summary>
    public static class WorldGraphProducts
    {
        /// <summary>Acquires the accepted material descriptor for a current main-world ground region without evaluating a graph.</summary>
        /// <param name="world">Owning world; null reports pending.</param>
        /// <param name="mainGraph">Explicit accepted main asset, or null for world.graph.</param>
        /// <param name="position">Finite world position in metres selecting the accepted region.</param>
        /// <param name="handle">Owned immutable material handle; dispose after its final consumer.</param>
        /// <param name="diagnostic">Pending/ownership reason on failure, or empty on success.</param>
        /// <returns>False for unavailable, stale or foreign ground/material inputs.</returns>
        /// <remarks>Main-thread entry. Imported textures remain borrowed; writers copy descriptors.</remarks>
        public static bool TryAcquireSurface(UniverseCreatorObject world, Graph mainGraph, Vector3 position,
            out GraphProductHandle<SurfaceProduct> handle, out string diagnostic)
        {
            handle = null;
            if (!TryAcquireGround(world, mainGraph, position, out var groundPin, out diagnostic)) return false;
            using (groundPin)
            {
                groundPin.TryGet(groundPin.Run, GraphProductUnits.WorldMetres, out var ground);
                var bindings = world.GetComponent<UCWorldGraphBindings>();
                try
                {
                    if (bindings != null && bindings.TryAcquireSurface(ground, out handle)) { diagnostic = string.Empty; return true; }
                }
                catch (InvalidOperationException error) { diagnostic = "Accepted material admission failed: " + error.Message; return false; }
                diagnostic = "Pending: Apply a Texture Graph material for this accepted ground region."; return false;
            }
        }

        /// <summary>Acquires immutable ground at a world XZ position under the selected world's current accepted main graph.</summary>
        /// <param name="world">Owning UC world; null reports pending.</param>
        /// <param name="mainGraph">Explicit main asset, or null to use world.graph; foreign/legacy domains are rejected.</param>
        /// <param name="position">Finite world position in metres; Y does not select another terrain.</param>
        /// <param name="handle">Owned consumer handle on success; dispose after the final read.</param>
        /// <param name="diagnostic">Pending or ownership/admission reason on failure; empty on success.</param>
        /// <returns>False for missing, foreign, stale, nonfinite or unadmitted inputs.</returns>
        /// <remarks>Main-thread entry. A missing product never triggers regeneration.</remarks>
        public static bool TryAcquireGround(UniverseCreatorObject world, Graph mainGraph, Vector3 position,
            out GraphProductHandle<RiverChannelSnapshot> handle, out string diagnostic)
        {
            GraphExecutionGuard.RequireMainThread("Accepted world graph input");
            handle = null; diagnostic = "Pending: build accepted ground for the bound world's main Node Graph.";
            if (world == null || !RiverChannelSnapshot.Finite(position.x) || !RiverChannelSnapshot.Finite(position.y) ||
                !RiverChannelSnapshot.Finite(position.z)) return false;
            Graph main = mainGraph != null ? mainGraph : world.graph;
            if (!ReferenceEquals(main, world.graph) || main == null || !main.HasSupportedDomainMetadata || main.Domain != UCGraphDomain.TerrainWater)
            { diagnostic = "World Graph Input selects a foreign or incompatible main graph."; return false; }
            if (!RiverChannelRegistry.TryGetCommittedSnapshot(world, position.x, position.z, out var ground) ||
                !world.GenerationScheduler.IsCurrent(ground.Run)) return false;
            try
            {
                handle = new GraphProductHandle<RiverChannelSnapshot>(ground, ground.Run, UCGraphDomain.TerrainWater,
                    ground.GroundVersion, ground.Bounds, GraphProductUnits.WorldMetres, world.ProductMemory, ProductMemoryFootprint.Estimate(ground));
                diagnostic = string.Empty; return true;
            }
            catch (InvalidOperationException error) { diagnostic = "Accepted ground admission failed: " + error.Message; return false; }
        }
    }
}
