// UC-T09-03: readiness and bounded landing demand belong to the existing chunk scheduler, not the host game's actor.
using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Detached main-thread observation of one UC cell. AcceptedRun identifies its published ground; flags describe residency, native terrain/collision, dependent work and current presentation.</summary>
    public readonly struct ChunkReadinessState
    {
        /// <summary>Existing published-ground revision; default means intentional graphless/adopted ground without a generation publication.</summary>
        public readonly GenerationRunId AcceptedRun;
        /// <summary>Observed membership, accepted native ground, active matching collision, completed dependencies and terrain presentation, respectively.</summary>
        public readonly bool Resident, TerrainAccepted, CollisionReady, DependentsReady, Visible;
        /// <summary>True when resident accepted terrain, collision and required dependent work are all ready; false is a host loading/landing hold, not permission to use a flat placeholder.</summary>
        public bool Ready => Resident && TerrainAccepted && CollisionReady && DependentsReady;
        internal ChunkReadinessState(GenerationRunId run, bool resident, bool terrain, bool collision, bool dependents, bool visible)
        { AcceptedRun = run; Resident = resident; TerrainAccepted = terrain; CollisionReady = collision; DependentsReady = dependents; Visible = visible; }
    }

    /// <summary>Observation of a caller-owned landing request. Pending requires the host to hold movement/loading; Ready permits entry, and Cancelled/Expired/Unavailable cannot imply safe collision.</summary>
    public enum ChunkLandingStatus
    {
        /// <summary>No matching request or prepared collision-owning world exists.</summary>
        Unavailable,
        /// <summary>At least one requested cell still lacks coherent accepted output.</summary>
        Pending,
        /// <summary>Every requested cell currently meets the accepted output/collision contract.</summary>
        Ready,
        /// <summary>The caller or a world restart withdrew this request.</summary>
        Cancelled,
        /// <summary>The unscaled request lifetime elapsed.</summary>
        Expired
    }

    public partial class ChunkManager
    {
        private int landingId;
        private Coord landingCell;
        private int landingRadius;
        private double landingExpires;
        private ChunkLandingStatus landingStatus;
        private bool landingDemandChanged;
        private UCWorldGraphBindings readinessBindings;
        private bool readinessBindingResolved;
        private bool readinessRefreshPending, waterPresentationPending;
        private string readinessFailure;
        private bool generationCompletionPending; // UC repair 35: only an explicit full rebuild arms a success message.
        private readonly System.Collections.Generic.List<UCWaterBody> readinessWater = new System.Collections.Generic.List<UCWaterBody>();

        /// <summary>Main-thread dependency failure diagnostic, or empty when no failure is held. A new terrain build can retry settlement; this observation never clears a failure or starts work.</summary>
        public string ReadinessDiagnostic
        {
            get
            {
                if (readinessFailure != null) return readinessFailure;
                foreach (var chunk in pool)
                    if (chunk.resident)
                    {
                        if (chunk.generationFailure != null) return chunk.generationFailure;
                        if (chunk.residencyFailure != null) return chunk.residencyFailure; // UC-T09-04: retired ground cannot silently hold a landing forever.
                    }
                return readinessBindings != null && readinessBindings.StreamingVegetationFailed ? readinessBindings.VegetationStatus : string.Empty;
            }
        }

        private bool DependenciesSettled(UniverseCreatorObject world)
        {
            if (!readinessBindingResolved) { readinessBindings = world.GetComponent<UCWorldGraphBindings>(); readinessBindingResolved = true; }
            // Water and companion tasks currently own world-wide settlement. A local flag cannot certify them per cell.
            bool settled = readinessFailure == null && activeWorkers == 0 && pending.Count == 0 && rescatterQueue.Count == 0 &&
                setupDepth == 0 && settlePhase == 0 && !settleStitchPending && pendingAfterSettle.Count == 0 &&
                (readinessBindings == null || !readinessBindings.IsVegetationRunning && !readinessBindings.HasStreamingVegetationWork && !readinessBindings.StreamingVegetationFailed);
            if (!settled) return false;
            foreach (var chunk in pool) if (chunk.resident && chunk.generationFailure != null) return false;
            return true;
        }

        internal void RequireReadinessRefresh(Coord cell)
        {
            if (byCell.TryGetValue(cell, out var chunk)) chunk.presentationPending = true;
            readinessBindingResolved = false; // A newly bound companion can follow an earlier negative lookup.
            readinessRefreshPending = true;
        }

        internal void NotifyReadinessDependencyCompleted() => readinessRefreshPending = true;

        internal void RegisterReadinessWater(UCWaterBody body)
        {
            if (body == null || body.CarveWorld != Owner) return;
            if (!readinessWater.Contains(body)) readinessWater.Add(body);
            body.SetStreamingPresentation(!waterPresentationPending);
        }

        internal void UnregisterReadinessWater(UCWaterBody body) => readinessWater.Remove(body);

        private bool RefreshReadinessPresentation(UniverseCreatorObject world)
        {
            if (world == null) return false;
            bool settled = DependenciesSettled(world), changed = readinessRefreshPending;
            readinessRefreshPending = false;
            if (settled && waterPresentationPending) { waterPresentationPending = false; changed = true; }
            for (int i = 0; i < readinessWater.Count; i++)
            {
                var body = readinessWater[i];
                if (body == null || body.CarveWorld != world)
                { readinessWater.RemoveAt(i--); continue; }
                body.SetStreamingPresentation(!waterPresentationPending);
            }
            for (int i = 0; i < pool.Count; i++)
            {
                var chunk = pool[i];
                if (settled) chunk.presentationPending = false;
                if (!built && !chunk.presentationParked) continue; // Detached/adopted pools retain their presentation until a real publication parks it.
                bool nativeLive = chunk.resident && IsLiveCell(world, chunk) && HasAcceptedTerrain(world, chunk);
                UpdateChunkCollision(world, chunk, nativeLive); // UC-T09-04: repaired collision can release presentation in this same sweep.
                bool present = CanPresentChunk(world, chunk, nativeLive); // UC-T09-04: dependent settlement uses the same independent presentation policy.
                if (chunk.go != null && (chunk.go.activeSelf != nativeLive || chunk.presentationParked == present))
                {
                    SetChunkLive(chunk, nativeLive); changed = true;
                    if (present) HideWireframe(chunk); // UC-T09-03: retire the preview on transition, not on every idle pump.
                }
            }
            ReportGenerationCompletion(world); // UC repair 35: observe acceptance after this pump restores coherent presentation.
            return changed;
        }

        // UC repair 35: main-thread observation only; old accepted ground and drained failed queues are not success.
        private void ReportGenerationCompletion(UniverseCreatorObject world)
        {
            if (!generationCompletionPending || world == null || bakeScheduler == null || !bakeScheduler.IsCurrent(bakeRun) ||
                IsRebuildingAll || bakeInputFailure != null || !DependenciesSettled(world)) return;
            int owned = 0, resident = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var chunk = pool[i];
                if (!IsLiveCell(world, chunk)) continue;
                owned++;
                if (!chunk.resident && !IsCellInLoadedWindow(world, chunk)) continue;
                if (!chunk.resident || chunk.go == null || !HasAcceptedTerrain(world, chunk) ||
                    !chunk.publicationRun.Equals(bakeRun) || chunk.inFlight || chunk.presentationPending ||
                    chunk.pendingScatterCommit != null || chunk.pendingGraphScatter != null ||
                    chunk.residencyFailure != null || !HasAcceptedCollision(world, chunk, false)) return;
                resident++;
            }
            // An absent dictionary entry must not turn 35 accepted cells into a complete 36-cell edit-mode world.
            if (resident == 0 || owned != (long)world.chunkCountPerSide * world.chunkCountPerSide) return;
            generationCompletionPending = false;
            string scope = world.ShouldRenderWholeWorld() ? "world" : "resident window";
            Debug.Log("[Universe Creator] Generation complete: " + resident + "/" + resident + " " + scope +
                " chunks created and accepted successfully; terrain, seams and dependent work settled.", world);
        }

        private static bool HasAcceptedTerrain(UniverseCreatorObject world, Chunk chunk) =>
            world != null && chunk != null && chunk.terrain != null && chunk.data != null &&
            chunk.terrain.terrainData == chunk.data && chunk.appliedHeights != null &&
            !chunk.publicationInProgress && (chunk.bakedThisSession || world.graph == null || chunk.restoredAcceptedData == chunk.data);

        private static bool HasAcceptedCollision(UniverseCreatorObject world, Chunk chunk, bool requireActive = true)
        {
            if (!world.generateCollider) return true; // No collision is an explicit authored contract; landing requests reject it.
            if (chunk.readinessCollider == null && chunk.go != null) chunk.readinessCollider = chunk.go.GetComponent<TerrainCollider>();
            var collider = chunk.readinessCollider;
            return collider != null && collider.enabled && collider.terrainData == chunk.data && (!requireActive || collider.gameObject.activeInHierarchy);
        }

        /// <summary>Reads readiness without generating or allocating terrain. Main thread; unknown/out-of-world cells return false and default state.</summary>
        /// <param name="cell">Stable integer X/Z coordinate in this world's finite cell lattice.</param>
        /// <param name="state">Detached accepted-run and readiness flags; a Pending worker never counts as accepted ground.</param>
        /// <returns>True if this world's pool owns the cell; state.Ready separately reports safe complete output.</returns>
        public bool TryGetChunkReadiness(Coord cell, out ChunkReadinessState state)
        {
            state = default;
            var world = Owner;
            if (world == null || !byCell.TryGetValue(cell, out var chunk) || !IsLiveCell(world, chunk)) return false;
            bool accepted = HasAcceptedTerrain(world, chunk);
            bool dependents = accepted && !chunk.presentationPending && !chunk.inFlight && chunk.pendingScatterCommit == null && chunk.pendingGraphScatter == null && DependenciesSettled(world);
            state = new ChunkReadinessState(chunk.publicationRun, chunk.resident, accepted,
                accepted && HasAcceptedCollision(world, chunk), dependents,
                chunk.terrain != null && chunk.go != null && chunk.go.activeInHierarchy && !chunk.presentationParked && chunk.terrain.drawHeightmap);
            return true;
        }

        /// <summary>Observes accepted native ground under a world position without generating, moving or revealing it. Main thread only.</summary>
        /// <param name="positionMetres">Finite world-space XYZ in metres; X/Z must lie inside this world's finite cell lattice. Y does not select ground.</param>
        /// <param name="terrain">Borrowed accepted Terrain, or null when absent, nonresident or still publishing; do not destroy or retain it across a rebuild.</param>
        /// <param name="state">Independent readiness/presentation flags for a known cell, otherwise default. Hidden accepted ground can still be returned.</param>
        /// <returns>True for resident accepted native ground, even when collision or dependent presentation is pending; false never supplies a placeholder.</returns>
        public bool TryGetAcceptedTerrainAt(Vector3 positionMetres, out Terrain terrain, out ChunkReadinessState state)
        {
            // UC repair 32: a zero-valued height sample cannot distinguish valid flat ground from an unavailable surface.
            terrain = null; state = default;
            if (!RiverChannelSnapshot.Finite(positionMetres.x) || !RiverChannelSnapshot.Finite(positionMetres.y) ||
                !RiverChannelSnapshot.Finite(positionMetres.z)) return false;
            var chunk = ChunkAtWorld(positionMetres.x, positionMetres.z);
            if (chunk == null || !TryGetChunkReadiness(chunk.cell, out state) || !state.Resident || !state.TerrainAccepted) return false;
            terrain = chunk.terrain;
            return true;
        }

        private void RevealAcceptedChunk(UniverseCreatorObject world, Chunk chunk)
        {
            if (chunk == null || chunk.go == null) return;
            chunk.resident = IsCellInLoadedWindow(world, chunk);
            bool reveal = chunk.resident && HasAcceptedTerrain(world, chunk);
            SetChunkLive(chunk, reveal);
            if (!reveal) return;
            StitchAround(chunk); // UC-T09-03: native neighbours must be linked before dependent water samples active terrain.
            SetChunkLive(chunk, true); // Stitching can invalidate accepted dependent content; park it before this pump returns.
            if (!chunk.presentationParked) HideWireframe(chunk);
        }

        /// <summary>Requests a bounded landing footprint through UC's existing worker/commit budget. Does not move actors, bake synchronously or change the camera. Replaces this owner's previous landing request.</summary>
        /// <param name="positionMetres">Finite world-space position inside the authored UC world; Y is validated but demand uses X/Z.</param>
        /// <param name="radiusCells">0 or 1: one cell or at most a clipped 3×3 footprint. Larger requests are rejected.</param>
        /// <param name="timeoutSeconds">Finite unscaled request lifetime, 0.1…120 seconds.</param>
        /// <returns>Positive request ID on admission; zero if inputs, ownership, prepared pool or authored collision are unavailable. Poll GetLandingStatus before releasing host movement.</returns>
        public int RequestLanding(Vector3 positionMetres, int radiusCells = 1, float timeoutSeconds = 30)
        {
            var world = Owner;
            if (world == null || !built || !world.generateCollider || radiusCells < 0 || radiusCells > 1 ||
                !RiverChannelSnapshot.Finite(positionMetres.x) || !RiverChannelSnapshot.Finite(positionMetres.y) ||
                !RiverChannelSnapshot.Finite(positionMetres.z) || !RiverChannelSnapshot.Finite(timeoutSeconds) || timeoutSeconds < 0.1f || timeoutSeconds > 120) return 0;
            Vector3 min = WorldMinCorner(world);
            float size = world.GetChunkSize();
            if (!RiverChannelSnapshot.Finite(size) || size <= 0) return 0;
            var cell = new Coord(Mathf.FloorToInt((positionMetres.x - min.x) / size), Mathf.FloorToInt((positionMetres.z - min.z) / size));
            if (!byCell.TryGetValue(cell, out var chunk) || !IsLiveCell(world, chunk)) return 0;
            landingId = landingId == int.MaxValue ? 1 : landingId + 1;
            landingCell = cell; landingRadius = radiusCells; landingExpires = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            landingStatus = ChunkLandingStatus.Pending; landingDemandChanged = true;
            return landingId;
        }

        /// <summary>Reads the requested footprint's actual readiness on the main thread; an obsolete request ID is Unavailable. Ready is rechecked, never latched across a rebuild.</summary>
        /// <param name="requestId">Positive ID returned by RequestLanding.</param>
        /// <returns>Pending until accepted collision and required dependent work are ready; reports explicit expiry or cancellation.</returns>
        public ChunkLandingStatus GetLandingStatus(int requestId)
        {
            if (requestId <= 0 || requestId != landingId) return ChunkLandingStatus.Unavailable;
            if (landingStatus == ChunkLandingStatus.Cancelled || landingStatus == ChunkLandingStatus.Expired) return landingStatus;
            if (Time.realtimeSinceStartupAsDouble >= landingExpires)
            { landingStatus = ChunkLandingStatus.Expired; landingDemandChanged = true; return landingStatus; }
            var world = Owner;
            if (world == null || !world.generateCollider || !byCell.TryGetValue(landingCell, out var centre) || !IsLiveCell(world, centre)) return ChunkLandingStatus.Unavailable;
            for (int z = Math.Max(0, landingCell.z - landingRadius); z <= Math.Min(world.chunkCountPerSide - 1, landingCell.z + landingRadius); z++)
                for (int x = Math.Max(0, landingCell.x - landingRadius); x <= Math.Min(world.chunkCountPerSide - 1, landingCell.x + landingRadius); x++)
                    if (!byCell.TryGetValue(new Coord(x, z), out var chunk) || chunk.lod != LODLevel.High ||
                        !TryGetChunkReadiness(new Coord(x, z), out var state) || !state.Ready) return ChunkLandingStatus.Pending;
            return ChunkLandingStatus.Ready;
        }

        /// <summary>Cancels only the matching landing demand on the main thread. Existing accepted terrain and unrelated scheduler work remain owned by UC.</summary>
        /// <param name="requestId">ID to cancel; an obsolete ID has no effect.</param>
        public void CancelLanding(int requestId)
        { if (requestId > 0 && requestId == landingId) { landingStatus = ChunkLandingStatus.Cancelled; landingDemandChanged = true; } }

        private bool IsLandingDemand(Chunk chunk) => landingId > 0 &&
            landingStatus != ChunkLandingStatus.Cancelled && landingStatus != ChunkLandingStatus.Expired &&
            Time.realtimeSinceStartupAsDouble < landingExpires &&
            Math.Abs(chunk.cell.x - landingCell.x) <= landingRadius && Math.Abs(chunk.cell.z - landingCell.z) <= landingRadius;

        private void RefreshLandingDemand(UniverseCreatorObject world)
        {
            if (landingId == 0) return;
            GetLandingStatus(landingId); // Expiry uses the same unscaled clock as the public observation.
            if (landingStatus == ChunkLandingStatus.Cancelled || landingStatus == ChunkLandingStatus.Expired) return;
            for (int z = Math.Max(0, landingCell.z - landingRadius); z <= Math.Min(world.chunkCountPerSide - 1, landingCell.z + landingRadius); z++)
                for (int x = Math.Max(0, landingCell.x - landingRadius); x <= Math.Min(world.chunkCountPerSide - 1, landingCell.x + landingRadius); x++)
                {
                    if (!byCell.TryGetValue(new Coord(x, z), out var chunk)) continue;
                    bool entering = !chunk.resident || chunk.go != null && !chunk.go.activeSelf;
                    bool changeLod = chunk.lod != LODLevel.High;
                    chunk.resident = true; chunk.lod = LODLevel.High;
                    if (chunk.readinessCollider == null && chunk.go != null) chunk.readinessCollider = chunk.go.GetComponent<TerrainCollider>();
                    if (chunk.readinessCollider != null) chunk.readinessCollider.enabled = world.generateCollider;
                    // UC Scene/Play repair: promoting accepted saved ground changes density, never its sculpt/river heightfield.
                    if (world.graph != null && !HasAcceptedTerrain(world, chunk) && !chunk.inFlight) QueueGenerate(world, chunk);
                    if (changeLod && chunk.terrain != null) ApplyDensity(world, chunk);
                    if (entering || changeLod) RevealAcceptedChunk(world, chunk); // A stationary ready landing never repeats seam/scatter publication every frame.
                }
        }
    }
}
