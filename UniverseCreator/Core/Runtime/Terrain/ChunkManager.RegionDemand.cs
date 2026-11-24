// UC-T09-04: additional demand belongs to the existing world, stream predicate and generation scheduler.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Required content for a bounded region request. Terrain is always required; visibility and collision flags are independent.</summary>
    [Flags]
    public enum ChunkDemandContent { Terrain = 0, Visible = 1, Collision = 2 }

    /// <summary>Observation of a region request; Ready certifies its current accepted content, never a queued placeholder.</summary>
    public enum ChunkRegionDemandStatus { Unavailable, Pending, Ready, Cancelled, Expired }

    public partial class ChunkManager
    {
        private const int MaxRegionRequests = 16, MaxCellsPerRegionRequest = 64, MaxRegionDemandCells = 128;
        private sealed class RegionRequest
        {
            internal int Id, MinX, MinZ, MaxX, MaxZ, Side, Band;
            internal float CellSize;
            internal Vector3 Origin;
            internal double Expires;
            internal ChunkDemandContent Content;
            internal ChunkRegionDemandStatus Status;
        }
        private struct RegionCellDemand
        {
            internal ChunkDemandContent Content;
            internal int Band;
        }
        private readonly List<RegionRequest> regionRequests = new List<RegionRequest>(MaxRegionRequests);
        private readonly Dictionary<Coord, RegionCellDemand> regionDemand = new Dictionary<Coord, RegionCellDemand>(MaxRegionDemandCells);
        private int nextRegionRequestId;
        private bool regionDemandChanged;
        private int appliedVisibilityRadius = int.MinValue, appliedCollisionRadius = int.MinValue;

        /// <summary>Reports why the most recent region admission failed; empty after successful admission. Read on the main thread.</summary>
        public string RegionDemandDiagnostic { get; private set; } = string.Empty;

        /// <summary>Requests a clipped world-space X/Z volume through the existing residency and asynchronous generation budgets. Main thread; never activates or bakes terrain directly.</summary>
        /// <param name="boundsMetres">Finite world-space bounds with positive X/Z size; Y is validated but does not select terrain cells. Upper X/Z faces are exclusive.</param>
        /// <param name="content">Terrain plus optional Visible and/or Collision flags. Collision requires this world's authored generateCollider setting.</param>
        /// <param name="priorityBand">0 (nearest/important) through 3 (background); existing collision precedence and FIFO aging remain authoritative.</param>
        /// <param name="timeoutSeconds">Finite unscaled lifetime in seconds, 0.1 through 120.</param>
        /// <returns>Positive manager-local ID on admission; zero and a diagnostic for invalid bounds, unprepared ownership, or caps (16 requests, 64 cells per request, 128 distinct requested cells). Poll GetRegionDemandStatus.</returns>
        public int RequestRegionDemand(Bounds boundsMetres, ChunkDemandContent content = ChunkDemandContent.Terrain,
            int priorityBand = 1, float timeoutSeconds = 30)
        {
            var world = Owner;
            RefreshRegionDemand(world);
            if (world == null || !built) return RejectRegionDemand("This world's chunk pool is not prepared.");
            if (((int)content & ~3) != 0 || priorityBand < 0 || priorityBand > 3 ||
                !RiverChannelSnapshot.Finite(timeoutSeconds) || timeoutSeconds < 0.1f || timeoutSeconds > 120 ||
                !FiniteRegionVector(boundsMetres.center) || !FiniteRegionVector(boundsMetres.size) ||
                boundsMetres.size.x <= 0 || boundsMetres.size.z <= 0 || boundsMetres.size.y < 0 ||
                !FiniteRegionVector(boundsMetres.min) || !FiniteRegionVector(boundsMetres.max) ||
                boundsMetres.max.x <= boundsMetres.min.x || boundsMetres.max.z <= boundsMetres.min.z)
                return RejectRegionDemand("Region bounds, flags, priority or lifetime are invalid.");
            if ((content & ChunkDemandContent.Collision) != 0 && !world.generateCollider)
                return RejectRegionDemand("Collision demand requires this world's Generate Collider setting.");
            Vector3 origin = WorldMinCorner(world);
            float size = world.GetChunkSize(); int side = world.chunkCountPerSide;
            if (!FiniteRegionVector(origin) || !RiverChannelSnapshot.Finite(size) || size <= 0 || side <= 0)
                return RejectRegionDemand("The world's cell lattice is invalid.");
            double minX = ((double)boundsMetres.min.x - origin.x) / size, minZ = ((double)boundsMetres.min.z - origin.z) / size;
            double maxX = ((double)boundsMetres.max.x - origin.x) / size, maxZ = ((double)boundsMetres.max.z - origin.z) / size;
            if (maxX <= 0 || maxZ <= 0 || minX >= side || minZ >= side)
                return RejectRegionDemand("Region does not intersect this world.");
            int x0 = (int)Math.Floor(Math.Max(0, minX)), z0 = (int)Math.Floor(Math.Max(0, minZ));
            int x1 = (int)Math.Ceiling(Math.Min(side, maxX)) - 1, z1 = (int)Math.Ceiling(Math.Min(side, maxZ)) - 1;
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > MaxCellsPerRegionRequest)
                return RejectRegionDemand("Region exceeds the 64-cell per-request cap.");
            int union = regionDemand.Count, slot = -1;
            for (int i = 0; i < regionRequests.Count; i++)
                if (!RegionRequestActive(regionRequests[i])) { slot = i; break; }
            if (slot < 0 && regionRequests.Count >= MaxRegionRequests)
                return RejectRegionDemand("All 16 region request slots are active.");
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                var cell = new Coord(x, z);
                if (!byCell.TryGetValue(cell, out var chunk) || !IsLiveCell(world, chunk) ||
                    chunk.go == null || chunk.terrain == null || chunk.terrain.gameObject != chunk.go)
                    return RejectRegionDemand("The requested region includes an unprepared cell.");
                if (!regionDemand.ContainsKey(cell) && ++union > MaxRegionDemandCells)
                    return RejectRegionDemand("Region exceeds the 128-distinct-cell demand cap.");
            }
            // IDs are never reused within this manager's lifetime, so stale cancellation cannot affect a later request.
            if (nextRegionRequestId == int.MaxValue) return RejectRegionDemand("Region request ID space is exhausted.");
            var request = new RegionRequest { Id = ++nextRegionRequestId, MinX = x0, MinZ = z0, MaxX = x1, MaxZ = z1,
                Origin = origin, CellSize = size, Side = side, Content = content, Band = priorityBand,
                Expires = Time.realtimeSinceStartupAsDouble + timeoutSeconds, Status = ChunkRegionDemandStatus.Pending };
            if (slot < 0) regionRequests.Add(request); else regionRequests[slot] = request;
            RebuildRegionDemand(); RegionDemandDiagnostic = string.Empty;
            return request.Id;
        }

        /// <summary>Reads current accepted readiness for a manager-local request on the main thread. No generation, allocation or host movement is performed.</summary>
        /// <param name="requestId">Positive ID returned by this manager; expired/cancelled records remain observable until their bounded slot is reused.</param>
        /// <returns>Pending until all cells satisfy the requested flags; Ready is rechecked after rebuilds. Unknown or changed-lattice requests are Unavailable.</returns>
        public ChunkRegionDemandStatus GetRegionDemandStatus(int requestId)
        {
            var world = Owner; RefreshRegionDemand(world);
            foreach (var request in regionRequests)
            {
                if (request.Id != requestId) continue;
                if (!RegionRequestActive(request)) return request.Status;
                for (int z = request.MinZ; z <= request.MaxZ; z++) for (int x = request.MinX; x <= request.MaxX; x++)
                {
                    if (!TryGetChunkReadiness(new Coord(x, z), out var state) || !state.Resident || !state.TerrainAccepted ||
                        (request.Content & ChunkDemandContent.Visible) != 0 && (!state.Visible || !state.DependentsReady) ||
                        (request.Content & ChunkDemandContent.Collision) != 0 && (!state.CollisionReady || !state.DependentsReady))
                        return ChunkRegionDemandStatus.Pending;
                }
                return ChunkRegionDemandStatus.Ready;
            }
            return ChunkRegionDemandStatus.Unavailable;
        }

        /// <summary>Cancels only the matching region request on the main thread. Overlapping requests, authored data and other worlds remain intact; the next stream sweep applies the changed union.</summary>
        /// <param name="requestId">Positive manager-local ID; unknown/terminal IDs have no effect.</param>
        public void CancelRegionDemand(int requestId)
        {
            foreach (var request in regionRequests)
                if (request.Id == requestId && RegionRequestActive(request))
                { request.Status = ChunkRegionDemandStatus.Cancelled; RebuildRegionDemand(); return; }
        }

        private int RejectRegionDemand(string reason) { RegionDemandDiagnostic = reason; return 0; }
        private static bool FiniteRegionVector(Vector3 value) => RiverChannelSnapshot.Finite(value.x) &&
            RiverChannelSnapshot.Finite(value.y) && RiverChannelSnapshot.Finite(value.z);
        private static bool RegionRequestActive(RegionRequest request) => request.Status == ChunkRegionDemandStatus.Pending;

        private void RefreshRegionDemand(UniverseCreatorObject world)
        {
            if (regionRequests.Count == 0) return;
            bool changed = false; double now = Time.realtimeSinceStartupAsDouble;
            Vector3 origin = world != null ? WorldMinCorner(world) : default;
            float size = world != null ? world.GetChunkSize() : 0;
            foreach (var request in regionRequests)
            {
                if (!RegionRequestActive(request)) continue;
                if (world == null || request.Side != world.chunkCountPerSide || request.CellSize != size || !request.Origin.Equals(origin))
                { request.Status = ChunkRegionDemandStatus.Unavailable; changed = true; }
                else if ((request.Content & ChunkDemandContent.Collision) != 0 && !world.generateCollider)
                { request.Status = ChunkRegionDemandStatus.Unavailable; changed = true; }
                else if (now >= request.Expires) { request.Status = ChunkRegionDemandStatus.Expired; changed = true; }
            }
            if (changed) RebuildRegionDemand();
        }

        private void RebuildRegionDemand()
        {
            regionDemand.Clear();
            foreach (var request in regionRequests)
            {
                if (!RegionRequestActive(request)) continue;
                for (int z = request.MinZ; z <= request.MaxZ; z++) for (int x = request.MinX; x <= request.MaxX; x++)
                {
                    var cell = new Coord(x, z);
                    if (regionDemand.TryGetValue(cell, out var demand))
                    { demand.Content |= request.Content; demand.Band = Math.Min(demand.Band, request.Band); }
                    else demand = new RegionCellDemand { Content = request.Content, Band = request.Band };
                    regionDemand[cell] = demand;
                }
            }
            regionDemandChanged = true;
        }

        private void CancelRegionDemands()
        {
            foreach (var request in regionRequests)
                if (RegionRequestActive(request)) request.Status = ChunkRegionDemandStatus.Cancelled;
            regionDemand.Clear(); regionDemandChanged = true;
        }

        private bool IsRegionDemand(Chunk chunk) => regionDemand.ContainsKey(chunk.cell);
        private bool RegionRequires(Chunk chunk, ChunkDemandContent content) =>
            regionDemand.TryGetValue(chunk.cell, out var demand) && (demand.Content & content) != 0;
        private GenerationDemand ResolveRegionPriority(Chunk chunk, GenerationDemand normal)
        {
            if (!regionDemand.TryGetValue(chunk.cell, out var demand)) return normal;
            var world = Owner;
            bool normalWindow = world != null && (world.ShouldRenderWholeWorld() || IsLandingDemand(chunk) ||
                DualLODSystem.RingDistance(chunk.cell.x, chunk.cell.z, centreCell.x, centreCell.z) <= world.GetStreamRadius() + lodRingBoost ||
                InForwardStrip(world, chunk, centreCell, world.GetStreamRadius() + lodRingBoost));
            // Region-only background requests must not inherit the no-camera normal band's zero.
            return new GenerationDemand(normal.CollisionCritical || (demand.Content & ChunkDemandContent.Collision) != 0,
                normalWindow ? Math.Min(normal.Band, demand.Band) : demand.Band, normal.Distance, normal.QueuedAt);
        }

        private bool WantsChunkVisible(UniverseCreatorObject world, Chunk chunk, bool wholeWorld)
        {
            if (world == null || wholeWorld) return true;
            int radius = world.visibilityRadiusCells < 0 ? world.GetStreamRadius() + lodRingBoost :
                Math.Min(world.visibilityRadiusCells, world.GetStreamRadius() + lodRingBoost);
            return DualLODSystem.RingDistance(chunk.cell.x, chunk.cell.z, centreCell.x, centreCell.z) <= radius ||
                world.visibilityRadiusCells < 0 && InForwardStrip(world, chunk, centreCell, radius) ||
                IsLandingDemand(chunk) || RegionRequires(chunk, ChunkDemandContent.Visible);
        }

        private bool WantsChunkCollision(UniverseCreatorObject world, Chunk chunk, bool wholeWorld)
        {
            if (world == null || !world.generateCollider) return false;
            bool local = wholeWorld || (world.collisionRadiusCells < 0 ?
                DualLODSystem.ShouldHaveCollider(chunk.lod, true) :
                DualLODSystem.RingDistance(chunk.cell.x, chunk.cell.z, centreCell.x, centreCell.z) <= world.collisionRadiusCells);
            return local || IsLandingDemand(chunk) || RegionRequires(chunk, ChunkDemandContent.Collision);
        }

        private void UpdateChunkCollision(UniverseCreatorObject world, Chunk chunk, bool live)
        {
            if (chunk.readinessCollider == null && chunk.go != null) chunk.readinessCollider = chunk.go.GetComponent<TerrainCollider>();
            bool enabled = live && WantsChunkCollision(world, chunk, world != null && world.ShouldRenderWholeWorld());
            if (chunk.readinessCollider != null && chunk.readinessCollider.enabled != enabled) chunk.readinessCollider.enabled = enabled;
        }

        private bool CanPresentChunk(UniverseCreatorObject world, Chunk chunk, bool live) => live &&
            WantsChunkVisible(world, chunk, world != null && world.ShouldRenderWholeWorld()) &&
            (!WantsChunkCollision(world, chunk, world != null && world.ShouldRenderWholeWorld()) || HasAcceptedCollision(world, chunk, false)) &&
            !chunk.presentationPending;
    }
}
