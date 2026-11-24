// UC-T05-03: companion planting reuses accepted cells and the existing scatter commit/streaming pump.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    internal sealed class GraphVegetationRegion
    {
        internal Coord Cell;
        internal TerrainData Native;
        internal Vector3 Position, Size;
        internal RiverChannelSnapshot Ground;
        internal OutputBag PriorOutputs; // UC-T06-03: captured accepted placement for a patch; publication rejects concurrent replacement.
    }

    internal sealed class GraphVegetationResult : IDisposable
    {
        internal GraphVegetationRegion Region;
        internal OutputBag Outputs;
        internal ProductMemoryBudget.Lease Memory;
        internal Rect? Patch;
        public void Dispose() { Memory?.Dispose(); Memory = null; Outputs = null; }
    }

    public partial class ChunkManager
    {
        internal List<GraphVegetationRegion> CaptureGraphVegetationRegions(GenerationRunId run, Coord? onlyCell = null)
        {
            var result = new List<GraphVegetationRegion>();
            foreach (var chunk in pool)
            {
                if (chunk == null || chunk.data == null || chunk.terrain == null || !chunk.bakedThisSession ||
                    !chunk.publicationRun.Equals(run) || (onlyCell.HasValue && !chunk.cell.Equals(onlyCell.Value))) continue;
                Vector3 pos = chunk.terrain.transform.position, size = chunk.data.size;
                if (!RiverChannelRegistry.TryGetCommittedSnapshot(Owner, pos.x + size.x * 0.5f, pos.z + size.z * 0.5f, out var ground) ||
                    !ground.Run.Equals(run) || ground.OwnerId != chunk.publicationOwner) continue;
                result.Add(new GraphVegetationRegion { Cell = chunk.cell, Native = chunk.data, Position = pos, Size = size, Ground = ground, PriorOutputs = chunk.retainedScatter });
            }
            return result;
        }

        internal bool AreGraphVegetationRegionsCurrent(IReadOnlyList<GraphVegetationResult> results, GenerationRunId run)
        {
            if (Owner == null || results == null || !Owner.GenerationScheduler.IsCurrent(run)) return false;
            foreach (var result in results)
            {
                // UC-T06-03: never publish foreign/disposed output or a region moved/resized while its worker was admitted.
                if (result?.Region == null || result.Outputs == null || result.Region.Ground == null ||
                    !result.Outputs.RunId.Equals(run) || !result.Region.Ground.Run.Equals(run)) return false;
                var region = result.Region;
                if (!byCell.TryGetValue(region.Cell, out var chunk) || !ReferenceEquals(chunk.data, region.Native) ||
                    chunk.terrain == null || !chunk.terrain.transform.position.Equals(region.Position) || !chunk.data.size.Equals(region.Size) ||
                    (result.Patch.HasValue && !ReferenceEquals(chunk.retainedScatter, region.PriorOutputs)) ||
                    !chunk.publicationRun.Equals(run) || !RiverChannelRegistry.TryGetCommittedSnapshot(Owner,
                    region.Position.x + region.Size.x * 0.5f, region.Position.z + region.Size.z * 0.5f, out var current) ||
                    current.GroundVersion != region.Ground.GroundVersion) return false;
            }
            return true;
        }

        internal void AcceptGraphVegetation(IReadOnlyList<GraphVegetationResult> results, GenerationRunId run)
        {
            // The caller validates every region before entering the same world publication gate used by terrain.
            rescatterRun = run;
            foreach (var result in results)
            {
                var chunk = byCell[result.Region.Cell];
                // UC-T06-12: queuing admits a candidate; it does not retire accepted native resources or their memory.
                chunk.pendingGraphScatter?.Dispose();
                chunk.pendingGraphScatter = new GraphVegetationResult { Region = result.Region, Outputs = result.Outputs, Memory = result.Memory, Patch = result.Patch };
                result.Memory = null; result.Outputs = null;
                if (!rescatterQueue.Contains(chunk)) rescatterQueue.Add(chunk);
            }
        }

        private void ClearPendingScatter()
        {
            rescatterQueue.Clear();
            foreach (var chunk in pool)
            { chunk.pendingGraphScatter?.Dispose(); chunk.pendingGraphScatter = null; }
        }

        private void RetireChunkScatter(Chunk chunk)
        {
            // UC-T06-12: shared prototypes are borrowed; only this owner's registrations, native instances and spawned objects retire.
            chunk.pendingScatterCommit?.Dispose(); chunk.pendingScatterCommit = null;
            chunk.pendingGraphScatter?.Dispose(); chunk.pendingGraphScatter = null;
            int owner = ScatterOwnerOf(chunk);
            if (instancing != null) { instancing.ClearOwner(owner); instancing.SetOwnerLive(owner, true); }
            ScatterApplier.Release(chunk.scatterObjects, EnsureVegetationManager(), owner);
            ScatterApplier.ClearTerrainScatter(chunk.data);
            chunk.retainedScatter = null; chunk.graphScatterMemory?.Dispose(); chunk.graphScatterMemory = null;
            chunk.appearanceUpdate = null; chunk.appearanceData = null;
            chunk.scatterParkedStates.Clear();
            chunk.scatterOwnership.Clear();
        }
    }
}
