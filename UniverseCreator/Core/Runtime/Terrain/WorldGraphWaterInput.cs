// UC-T05-06: freeze accepted water triangle indexes once; workers never inspect live components or mutable water registries.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    internal sealed class WorldGraphWaterInput
    {
        private readonly UniverseCreatorObject owner;
        private readonly UCWaterVolume[] sources;
        private readonly WaterQuerySurface[] surfaces;
        private readonly WaterBodyKind[] kinds; // UC-T06-11: body categories are frozen with geometry; worker code never reads live volumes.
        private readonly WaterSurfaceParams quiet;

        private WorldGraphWaterInput(UniverseCreatorObject owner, UCWaterVolume[] sources, WaterQuerySurface[] surfaces)
        {
            this.owner = owner; this.sources = sources; this.surfaces = surfaces;
            kinds = new WaterBodyKind[sources.Length];
            for (int i = 0; i < kinds.Length; i++) kinds[i] = (WaterBodyKind)(int)sources[i].kind;
            quiet = WaterSurfaceParams.Default(0); quiet.waveHeight = 0; quiet.horizontalDisplacement = false;
        }

        internal static WorldGraphWaterInput Capture(UniverseCreatorObject world)
        {
            GraphExecutionGuard.RequireMainThread("World graph water capture");
            var volumes = new List<UCWaterVolume>(); var queries = new List<WaterQuerySurface>();
            foreach (var volume in UCWaterVolume.All)
            {
                if (volume == null || !volume.isActiveAndEnabled || volume.WorldOwner != world || volume.AcceptedQuerySurface == null) continue;
                if (volumes.Count == 256) throw new InvalidOperationException("World graph water input exceeds 256 accepted volumes.");
                if (!Enum.IsDefined(typeof(UCWaterVolume.BodyKind), volume.kind)) throw new InvalidOperationException("Accepted water has an unsupported body category."); // UC-T06-11
                volumes.Add(volume); queries.Add(volume.AcceptedQuerySurface);
            }
            return new WorldGraphWaterInput(world, volumes.ToArray(), queries.ToArray());
        }

        internal void Pin(TileData tile)
        {
            foreach (var surface in surfaces) tile.TrackProductMemory(surface, "world-graph-accepted-water", surface.EstimatedManagedBytes);
        }

        internal void Pin(ProductMemoryBudget budget, List<ProductMemoryBudget.Lease> pins)
        {
            foreach (var surface in surfaces)
                pins.Add(budget.Acquire(surface, surface.EstimatedManagedBytes, 0, "world-graph-accepted-water"));
        }

        internal bool IsCurrent()
        {
            GraphExecutionGuard.RequireMainThread("World graph water revision check");
            int count = 0;
            foreach (var volume in UCWaterVolume.All)
            {
                if (volume == null || !volume.isActiveAndEnabled || volume.WorldOwner != owner || volume.AcceptedQuerySurface == null) continue;
                if (count >= sources.Length || volume != sources[count] || !ReferenceEquals(volume.AcceptedQuerySurface, surfaces[count]) ||
                    (WaterBodyKind)(int)volume.kind != kinds[count]) return false; // UC-T06-11: changed salinity category invalidates the captured water input too.
                count++;
            }
            return count == sources.Length;
        }

        internal float Coverage(float x, float z)
        {
            foreach (var surface in surfaces)
                if (surface.TrySample(new Vector2(x, z), 0, quiet, out _, out _, out _, out _, out _)) return 1;
            return 0;
        }

        // UC-T06-07: final contact uses accepted, wave-free triangle coverage rather than a volume's rectangular bounds.
        internal bool HasSurfaces => surfaces.Length > 0;

        // UC-T07-11: actual source shore distance, signed from oriented pre-skirt contours. Clamp only beyond the explicit search envelope.
        internal float SignedShoreDistance(float x, float z, float radius, WaterBodyKind? selectedKind = null)
        {
            if (!RiverChannelSnapshot.Finite(radius) || radius <= 0 || radius > 256) throw new ArgumentException("Accepted shore search radius must be finite, positive and at most 256 metres.");
            float nearest = radius; bool inside = false, found = false;
            for (int i = 0; i < surfaces.Length; i++)
            {
                if (selectedKind.HasValue && kinds[i] != selectedKind.Value) continue;
                if (Ecology.WaterProximity.HorizontalDistanceTo(surfaces[i].WorldBounds, x, z) > radius) continue;
                float distance = surfaces[i].ShoreDistance(x, z, radius, out bool wetSide, out bool observed);
                if (!observed) wetSide = surfaces[i].TrySample(new Vector2(x, z), 0, quiet, out _, out _, out _, out _, out _);
                if (!found || distance < nearest || (distance == nearest && wetSide && !inside))
                { nearest = distance; inside = wetSide; found = true; }
            }
            return inside ? -nearest : nearest;
        }

        internal bool TryStillSurface(float x, float z, out float y)
        {
            y = float.NegativeInfinity;
            foreach (var surface in surfaces)
                if (surface.TrySample(new Vector2(x, z), 0, quiet, out float level, out _, out _, out _, out _))
                    y = Mathf.Max(y, level);
            return !float.IsNegativeInfinity(y);
        }

        // UC-T06-11: exact wave-free triangle contact/current. Highest surface wins; deterministic category/current ties are not an estuary mixing model.
        internal bool TryStillObservation(float x, float z, out float y, out Vector2 current, out WaterBodyKind kind)
        {
            y = float.NegativeInfinity; current = Vector2.zero; kind = WaterBodyKind.Lake;
            for (int i = 0; i < surfaces.Length; i++)
                if (surfaces[i].TrySample(new Vector2(x, z), 0, quiet, out float level, out _, out _, out Vector2 flow, out _) &&
                    (level > y || (level == y && ((int)kinds[i] < (int)kind || (kinds[i] == kind &&
                        (flow.x < current.x || (flow.x == current.x && flow.y < current.y)))))))
                { y = level; current = flow; kind = kinds[i]; }
            return !float.IsNegativeInfinity(y);
        }

        // Proximity outside triangle coverage is a bounds estimate, explicitly not an exact shore-distance or salinity measurement.
        internal bool TryNearestBounds(float x, float z, float radius, out float distance, out float surfaceY, out WaterBodyKind kind)
        {
            distance = radius; surfaceY = 0f; kind = WaterBodyKind.Lake; bool found = false;
            for (int i = 0; i < surfaces.Length; i++)
            {
                float d = Ecology.WaterProximity.HorizontalDistanceTo(surfaces[i].WorldBounds, x, z);
                float candidateY = surfaces[i].WorldBounds.max.y;
                if (d >= radius || (found && (d > distance || (d == distance &&
                    (candidateY < surfaceY || (candidateY == surfaceY && (int)kinds[i] >= (int)kind)))))) continue;
                distance = d; surfaceY = surfaces[i].WorldBounds.max.y; kind = kinds[i]; found = true;
            }
            return found;
        }

        internal float DistanceToSaltBounds(float x, float z, float radius)
        {
            float nearest = radius;
            for (int i = 0; i < surfaces.Length; i++) if (kinds[i] == WaterBodyKind.Ocean)
                nearest = Mathf.Min(nearest, Ecology.WaterProximity.HorizontalDistanceTo(surfaces[i].WorldBounds, x, z));
            return nearest;
        }
    }
}
