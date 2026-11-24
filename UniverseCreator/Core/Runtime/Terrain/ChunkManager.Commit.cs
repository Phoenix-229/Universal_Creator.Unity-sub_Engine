using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Integration;

namespace UniverseCreator.TerrainSystem
{
    public partial class ChunkManager
    {
        // UC-T02-04: at least one stage may progress; subsequent work stops at the measured deadline.
        private bool CommitBudgetSpent(UniverseCreatorObject owner) => commitPumpSteps > 0 &&
            (commitPumpSteps >= MaxCommitStagesPerPump ||
             (Time.realtimeSinceStartupAsDouble - commitPumpStarted) * 1000.0 >= owner.ChunkCommitBudgetMilliseconds);

        private void RecordCommitStep(UniverseCreatorObject owner, ChunkCommitStep step, double started)
        {
            ReapTerrainMemory(); // UC-T02-06: deferred native destruction releases its byte reservation only after completion.
            commitPumpSteps++;
            commitMetrics.Record(step, (Time.realtimeSinceStartupAsDouble - started) * 1000.0, owner.ChunkCommitBudgetMilliseconds);
            owner.GenerationScheduler.Metrics.Capture(owner.GenerationScheduler.CurrentRun)?.RecordStage(step,
                (Time.realtimeSinceStartupAsDouble - started) * 1000.0, owner.ProductMemory); // UC-T02-09: reuse the measured commit boundary and byte owner.
        }

        private bool AdvanceCommit(UniverseCreatorObject owner, Chunk chunk)
        {
            double started = Time.realtimeSinceStartupAsDouble;
            bool advanced = chunk.commit.MoveNext();
            if (advanced) RecordCommitStep(owner, chunk.commit.Current, started);
            return advanced;
        }

        // UC-T02-04: accepted seam writes stay atomic with collision; each native array is still bounded.
        private void UploadAcceptedHeights(Chunk chunk)
        {
            using (IEnumerator<ChunkCommitStep> uploads = TerrainBuilder.UploadHeightStages(chunk.data, chunk.appliedHeights).GetEnumerator())
            {
                while (true)
                {
                    double started = Time.realtimeSinceStartupAsDouble;
                    if (!uploads.MoveNext()) break;
                    RecordCommitStep(Owner, uploads.Current, started);
                }
            }
            chunk.scatterGroundDirty = true; // UC-T06-07: this existing native-write boundary covers actual seam changes.
        }

        private void RefreshAcceptedGround(Chunk chunk)
        {
            if (chunk == null || chunk.stagedData != null || chunk.publicationOwner == null || chunk.data == null) return;
            var owner = Owner;
            chunk.presentationPending = true; waterPresentationPending = true; readinessRefreshPending = true; // UC-T09-03: accepted seam/re-cut changes invalidate dependent presentation too.
            RiverChannelRegistry.RefreshGround(owner, chunk.publicationRun, chunk.publicationOwner, chunk.appliedHeights, chunk.data); // UC-T03-10: sync has accepted these native seam/re-cut changes.
            TerrainBuilder.ReconcileFinalNormals(chunk.retainedScatter, chunk.appliedHeights, owner, ChunkWorldBounds(chunk), chunk.data.size.y);
            var bindings = owner.GetComponent<UCWorldGraphBindings>();
            // UC-T06-07: accepted companion placement already owns refresh; legacy graphs reuse
            // their retained output through the same applier, without a second generator/hook.
            if (chunk.scatterGroundDirty && (bindings == null || !bindings.UsesAcceptedVegetationGround))
                RescatterChunk(owner, chunk);
            bindings?.OnGroundAccepted(this, chunk.cell); // UC-T05-03: re-cuts and seams also resample accepted companion ground inputs.
        }

        private static void DisposeCommit(Chunk chunk)
        {
            IEnumerator<ChunkCommitStep> commit = chunk.commit;
            chunk.commit = null;
            commit?.Dispose();
        }

        private void DestroyCommitData(TerrainData data, ProductMemoryBudget.Lease reservation = null)
        {
            if (data == null) { reservation?.Dispose(); return; }
            if (Application.isPlaying)
            {
                Object.Destroy(data);
                if (reservation != null) retiringTerrainMemory.Add(new KeyValuePair<TerrainData, ProductMemoryBudget.Lease>(data, reservation));
            }
            else { Object.DestroyImmediate(data); reservation?.Dispose(); }
        }

        private IEnumerable<ChunkCommitStep> StageChunk(UniverseCreatorObject owner, Chunk chunk)
        {
            TileData tile = chunk.tile;
            if (chunk.terrain == null || tile == null || !owner.GenerationScheduler.IsCurrent(tile.RunId)) yield break;
            TerrainData prepared = null;
            // UC-T02-06: reserve before native allocation and retain both old/new ownership until successful publication.
            ProductMemoryBudget.Lease nativeMemory = null, uploadMemory = null, pristineMemory = null;
            ProductMemoryBudget.Lease publicationMemory = null; // UC-T02-07: a rejected commit cannot leak or reveal scene metadata.
            try
            {
                ReapTerrainMemory();
                nativeMemory = owner.ProductMemory.Reserve(0, EstimateCommitNativeBytes(chunk, tile.outputs, owner.terrainLayers?.Length ?? 1));
                uploadMemory = owner.ProductMemory.Reserve(256 * 1024);
                // Keep the accepted native surface/collider together until every replacement upload has finished.
                // Clone preserves authored terrain/detail settings and holes absent from the new OutputBag.
                Vector3 size = new Vector3(owner.GetChunkSize(), owner.maxHeightMeters, owner.GetChunkSize());
                if (chunk.data != null && chunk.data.heightmapResolution == chunk.resolution)
                {
                    prepared = Object.Instantiate(chunk.data);
                    prepared.size = size;
                }
                else prepared = TerrainBuilder.GetOrCreateTerrainData(null, chunk.resolution, size);
                chunk.stagedData = prepared;
                prepared.name = "UC Terrain " + chunk.cell;
                TerrainBuilder.ApplyLayers(prepared, owner);
                yield return new ChunkCommitStep(ChunkCommitStage.AllocateTerrain);

                WeldChunkEdges(chunk, true);
                Bounds bounds = ChunkWorldBounds(chunk);
                IReadOnlyList<TerrainShapeRequest> shapes = ShapeRequests;
                // Preserve pristine ground before water -> road -> manual sculpt, so later carve removal can restore it.
                bool needsPristine = WaterCarveRegistry.CountAffecting(owner, bounds) > 0 ||
                    TerrainShapeApplier.Affects(shapes, bounds, tile.outputs.heightmap, owner.maxHeightMeters, owner.GetMetersPerPixel());
                if (needsPristine && tile.outputs.heightmap != null)
                    pristineMemory = owner.ProductMemory.Reserve(64 + tile.outputs.heightmap.LongLength * sizeof(float));
                float[,] pristine = needsPristine ? CloneHeights(tile.outputs.heightmap) : null;
                foreach (ChunkCommitStep step in TerrainBuilder.ApplyOutputStages(prepared, tile.outputs, owner,
                    owner.overrideLayer, bounds, owner.GetMetersPerPixel(), shapes)) yield return step;

                if (!owner.GenerationScheduler.IsCurrent(tile.RunId) || (chunk.stop != null && chunk.stop.StopRequested)) yield break;
                TerrainData previous = chunk.data;
                bool releasePrevious = chunk.ownsCommittedData;
                float[,] previousHeights = chunk.appliedHeights, previousPristine = chunk.graphHeights;
                bool previouslyBaked = chunk.bakedThisSession;
                TerrainData previousRestored = chunk.restoredAcceptedData;
                TerrainCollider oldCollider = chunk.go.GetComponent<TerrainCollider>();
                bool colliderEnabled = oldCollider != null && oldCollider.enabled;
                OutputBag previousScatter = chunk.retainedScatter;
                publicationMemory = GraphPublicationCommit.Prepare(owner, tile.outputs);
                chunk.publicationInProgress = true; // UC-T09-03: ApplyChunk writes native candidates before their content transaction accepts.
                try
                {
                    ApplyChunk(owner, chunk, prepared, pristine);
                    chunk.pendingScatterCommit?.PrepareCommit(); // UC-T06-12: native uploads/capacity checks finish before immutable channel publication.
                    if (!GraphPublicationCommit.Commit(owner, tile.outputs, publicationMemory, prepared)) // UC-T03-10: committed ground includes the native holes retained by staging.
                        throw new System.OperationCanceledException("Channel publication belongs to a superseded terrain commit.");
                    chunk.pendingScatterCommit?.Commit(); chunk.pendingScatterCommit = null; // UC-T06-12: dependent native placement accepts before withdrawing old resources.
                    chunk.scatterParkedStates.Clear(); chunk.scatterOwnership.Clear(); SetChunkLive(chunk, chunk.go.activeSelf); // UC-T06-12: parked accepted resources submit/collide nowhere.
                    chunk.pendingGraphScatter?.Dispose(); chunk.pendingGraphScatter = null;
                    publicationMemory = null;
                    chunk.publicationRun = tile.RunId;
                    chunk.publicationOwner = tile.outputs.publicationOwner;
                }
                catch
                {
                    chunk.pendingScatterCommit?.Dispose(); chunk.pendingScatterCommit = null; // UC-T06-12: retain accepted draws/colliders on failed publication.
                    // UC-T02-04: a failed dependent-content callback must not strand a half-accepted height/collider pair.
                    chunk.data = previous;
                    if (chunk.terrain != null) chunk.terrain.terrainData = previous;
                    TerrainCollider collider = chunk.go != null ? chunk.go.GetComponent<TerrainCollider>() : null;
                    if (collider != null) { collider.terrainData = previous; collider.enabled = colliderEnabled; }
                    chunk.appliedHeights = previousHeights; chunk.graphHeights = previousPristine;
                    chunk.bakedThisSession = previouslyBaked;
                    chunk.restoredAcceptedData = previousRestored; // UC Scene/Play repair: failed publication retains the accepted saved surface.
                    chunk.retainedScatter = previousScatter;
                    if (previousHeights != null) SeamWelder.Capture(previousHeights, chunk.edges);
                    else if (chunk.edges != null) chunk.edges.ready = false;
                    throw;
                }
                finally { chunk.publicationInProgress = false; }
                chunk.presentationPending = true; waterPresentationPending = true; // UC-T09-03: accepted new ground waits for its water/vegetation; rollback above retains previous presentation.
                chunk.ownsCommittedData = true;
                chunk.committedOutputMemory?.Dispose();
                chunk.committedOutputMemory = tile.DetachProductMemory(tile.outputs);
                chunk.flatHeightsMemory?.Dispose(); chunk.flatHeightsMemory = null;
                chunk.committedPristineMemory?.Dispose();
                chunk.committedPristineMemory = pristineMemory;
                pristineMemory = null;
                prepared = null; // Ownership transfers only after final terrain/collider/content publication succeeds.
                if (releasePrevious) DestroyCommitData(previous, chunk.committedNativeMemory);
                chunk.committedNativeMemory = nativeMemory;
                nativeMemory = null;
                chunk.graphScatterMemory?.Dispose(); chunk.graphScatterMemory = null; // UC-T05-03: only a successful terrain/content replacement retires the old companion pin.
                owner.GetComponent<UCWorldGraphBindings>()?.OnGroundAccepted(this, chunk.cell); // UC-T05-03: stream from the immutable applied plan, never changing authoring.
                RevealAcceptedChunk(owner, chunk); // UC-T09-03: also handles completion beneath a stationary observer.
                yield return new ChunkCommitStep(ChunkCommitStage.PublishContent);
            }
            finally
            {
                chunk.pendingScatterCommit?.Dispose(); chunk.pendingScatterCommit = null; // UC-T06-12: abandoned terrain enumerators own no orphan scatter staging.
                chunk.stagedData = null;
                if (prepared != null) { DestroyCommitData(prepared, nativeMemory); nativeMemory = null; }
                nativeMemory?.Dispose(); uploadMemory?.Dispose(); pristineMemory?.Dispose();
                publicationMemory?.Dispose();
            }
        }
    }
}
