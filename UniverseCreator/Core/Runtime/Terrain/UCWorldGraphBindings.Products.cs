// UC-T05-06: companion products borrow accepted payloads under the same world/run and memory owner.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    internal sealed class WorldGraphEnvironmentInput
    {
        private readonly UCWorldGraphBindings owner;
        private readonly long baselineRevision, dynamicRevision;
        private readonly List<GraphVegetationRegion> regions = new List<GraphVegetationRegion>();
        private readonly List<OutputBag> fields = new List<OutputBag>();
        private readonly int baselineCount;
        private readonly bool includeDynamic;

        internal WorldGraphEnvironmentInput(UCWorldGraphBindings owner, List<GraphVegetationResult> baseline,
            List<GraphVegetationResult> dynamic, bool includeDynamic)
        {
            this.owner = owner; this.includeDynamic = includeDynamic;
            baselineRevision = owner.BaselineClimateRevision; dynamicRevision = owner.LightingRevision;
            Add(baseline); baselineCount = fields.Count;
            if (includeDynamic) Add(dynamic);
        }

        private WorldGraphEnvironmentInput(WorldGraphEnvironmentInput source, Rect bounds)
        {
            owner = source.owner; baselineRevision = source.baselineRevision; dynamicRevision = source.dynamicRevision; includeDynamic = source.includeDynamic;
            for (int stage = 0; stage < 2; stage++)
            {
                int start = stage == 0 ? 0 : source.baselineCount, end = stage == 0 ? source.baselineCount : source.fields.Count;
                for (int i = start; i < end; i++)
                {
                    var r = source.regions[i];
                    if (r.Position.x > bounds.xMin || r.Position.z > bounds.yMin || r.Position.x + r.Size.x < bounds.xMax || r.Position.z + r.Size.z < bounds.yMax) continue;
                    regions.Add(r); fields.Add(source.fields[i]); break;
                }
                if (stage == 0) baselineCount = fields.Count;
            }
        }

        internal WorldGraphEnvironmentInput ForRegion(Rect bounds) => new WorldGraphEnvironmentInput(this, bounds); // At most one baseline and one dynamic field per sample.

        private void Add(List<GraphVegetationResult> results)
        {
            if (results == null) return;
            foreach (var result in results)
            {
                if (fields.Count == 4096) throw new InvalidOperationException("Accepted environment exceeds the companion region budget.");
                regions.Add(result.Region); fields.Add(result.Outputs); // Borrow immutable arrays, never live result wrappers that retirement clears.
            }
        }

        internal bool HasFields => fields.Count > 0;
        internal bool IsCurrent() => owner != null && owner.BaselineClimateRevision == baselineRevision &&
            owner.HasCapturedEnvironment(baselineRevision, dynamicRevision, includeDynamic);
        internal void Pin(TileData tile) { foreach (var field in fields) tile.TrackProductMemory(field, "accepted-environment-input"); }
        internal void Pin(ProductMemoryBudget budget, List<ProductMemoryBudget.Lease> pins)
        { foreach (var field in fields) pins.Add(budget.Acquire(field, ProductMemoryFootprint.Estimate(field), 0, "accepted-environment-input")); }

        internal float Sample(int port, float x, float z)
        {
            if (!HasFields) throw new InvalidOperationException("Pending: Run accepted baseline climate or dynamic environment first.");
            bool found = false; float value = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                var region = regions[i];
                if (x < region.Position.x || z < region.Position.z || x > region.Position.x + region.Size.x || z > region.Position.z + region.Size.z) continue;
                OutputBag field = fields[i];
                float[] samples = port == 6 ? field.weatherTemperatureBias : port == 7 ? field.weatherPrecipBias : field.weatherWindBias;
                if (samples != null)
                { value = UCWorldGraphBindings.SampleBias(samples, field.weatherBiasSize, field.weatherBiasWorldMin, field.weatherBiasWorldSize, new Vector3(x, 0, z)); found = true; }
                // Resolve shared boundaries once per stage. Dynamic connected fields replace their baseline field.
                i = i < baselineCount ? baselineCount - 1 : fields.Count;
            }
            if (!found) throw new InvalidOperationException("Pending: the requested accepted climate field is not connected in this region.");
            return value;
        }
    }

    public sealed partial class UCWorldGraphBindings
    {
        [SerializeField, Tooltip("Texture/material graph. Edits remain pending until Apply to Terrain.")] private Graph textureGraph;
        private readonly Dictionary<string, GraphProductHandle<SurfaceProduct>> acceptedSurfaces = new Dictionary<string, GraphProductHandle<SurfaceProduct>>();
        private readonly Dictionary<string, Guid> surfaceGroundVersions = new Dictionary<string, Guid>();
        private readonly List<string> retiredSurfaces = new List<string>();
        private long textureRevision;

        /// <summary>Saved Texture Graph; null unbinds, while foreign/future metadata is rejected before assignment.</summary>
        /// <exception cref="ArgumentException">A non-null graph is outside the supported Texture domain.</exception>
        public Graph TextureGraph
        {
            get => textureGraph;
            set
            {
                if (value != null && (!value.HasSupportedDomainMetadata || value.Domain != UCGraphDomain.TextureMaterialShader))
                    throw new ArgumentException("Select an explicit Texture Graph asset.", nameof(value));
                textureGraph = value;
            }
        }
        /// <summary>Number of accepted texture-region applications in this runtime lifetime; terrain revision is independent.</summary>
        public long TextureRevision => textureRevision;

        internal bool HasCapturedEnvironment(long baseline, long dynamic, bool includeDynamic) =>
            (baseline == 0 || baselineFields != null) && (!includeDynamic || (lightingRevision == dynamic && (dynamic == 0 || lightingFields != null)));

        internal WorldGraphEnvironmentInput CaptureEnvironmentInputs(bool includeDynamic)
        {
            GraphExecutionGuard.RequireMainThread("Accepted companion environment capture");
            bool dynamicCurrent = acceptedLighting != null && (!lightingUsesGround ||
                (!lightingGroundStale && Owner.GenerationScheduler.IsCurrent(lightingRun) && ReferenceEquals(Owner.graph, lightingMain)));
            var input = new WorldGraphEnvironmentInput(this, baselineFields, dynamicCurrent ? lightingFields : null, includeDynamic);
            return input.HasFields ? input : null;
        }

        internal void BindAcceptedInputs(TileData tile, Graph main, RiverChannelSnapshot ground,
            WorldGraphWaterInput water, WorldGraphEnvironmentInput environment)
        {
            tile.BindWorldGraphInputs(main, ground, water, environment);
            if (TryAcquireSurface(ground, out var surface))
            { tile.OwnGraphInput(surface); tile.worldGraphGround.Surface = surface; }
        }

        internal void AcceptTexture(Graph graph, RiverChannelSnapshot ground, SurfaceProduct surface)
        {
            GraphExecutionGuard.RequireMainThread("Accepted material publication");
            if (graph == null || graph.Domain != UCGraphDomain.TextureMaterialShader || !graph.HasSupportedDomainMetadata ||
                ground == null || !Owner.GenerationScheduler.IsCurrent(ground.Run))
                throw new InvalidOperationException("Texture product is pending or belongs to a foreign/stale terrain run.");
            RetireStaleSurfaces();
            if (!acceptedSurfaces.ContainsKey(ground.OwnerId) && acceptedSurfaces.Count >= 256)
                throw new InvalidOperationException("Accepted material regions exceed the 256-region budget.");
            SurfaceProduct frozen = surface.Copy(); // Persisted asset references survive the evaluation tile; descriptor writes belong to the next consumer.
            var pin = new GraphProductHandle<SurfaceProduct>(frozen, ground.Run, graph.Domain, Guid.NewGuid(), ground.Bounds,
                GraphProductUnits.SurfaceDescription, Owner.ProductMemory, ProductMemoryFootprint.Estimate(frozen));
            if (acceptedSurfaces.TryGetValue(ground.OwnerId, out var previous)) previous.Dispose();
            acceptedSurfaces[ground.OwnerId] = pin; surfaceGroundVersions[ground.OwnerId] = ground.GroundVersion;
            textureGraph = graph; textureRevision++;
        }

        internal bool TryAcquireSurface(RiverChannelSnapshot ground, out GraphProductHandle<SurfaceProduct> handle)
        {
            handle = null;
            if (ground == null || !Owner.GenerationScheduler.IsCurrent(ground.Run) ||
                !surfaceGroundVersions.TryGetValue(ground.OwnerId, out var version) || version != ground.GroundVersion ||
                !acceptedSurfaces.TryGetValue(ground.OwnerId, out var accepted) ||
                !accepted.TryGet(ground.Run, GraphProductUnits.SurfaceDescription, out var surface)) return false;
            handle = new GraphProductHandle<SurfaceProduct>(surface, accepted.Run, accepted.ProducerDomain, accepted.PublicationVersion,
                accepted.Bounds, accepted.Units, Owner.ProductMemory, ProductMemoryFootprint.Estimate(surface));
            return true;
        }

        internal void RetireStaleSurfaces()
        {
            retiredSurfaces.Clear();
            foreach (var pair in acceptedSurfaces)
            {
                var bounds = pair.Value.Bounds;
                if (!Owner.GenerationScheduler.IsCurrent(pair.Value.Run) ||
                    !RiverChannelRegistry.TryGetCommittedSnapshot(Owner, bounds.center.x, bounds.center.y, out var ground) ||
                    ground.OwnerId != pair.Key || ground.GroundVersion != surfaceGroundVersions[pair.Key]) retiredSurfaces.Add(pair.Key);
            }
            foreach (string key in retiredSurfaces)
            { acceptedSurfaces[key].Dispose(); acceptedSurfaces.Remove(key); surfaceGroundVersions.Remove(key); }
            retiredSurfaces.Clear();
        }

        private void RetireGraphProducts()
        {
            foreach (var surface in acceptedSurfaces.Values) surface.Dispose();
            acceptedSurfaces.Clear(); surfaceGroundVersions.Clear();
        }
    }
}
