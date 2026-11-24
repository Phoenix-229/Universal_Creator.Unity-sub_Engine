// UC-T09-04: the existing commit pump owns bounded native retirement and exact dormant restoration.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public partial class ChunkManager
    {
        private long dormantTerrainBytes;
        private int dormantTerrainCount, dormantEpoch = 1, lastDormantBudget;
        private Chunk residencyChunk;

        /// <summary>Bytes reserved for this world's recoverable dormant native payloads, including capture scratch. Excludes engine/driver overhead; main-thread observation.</summary>
        public long DormantTerrainBytes => dormantTerrainBytes;
        /// <summary>Number of terrain holders currently carrying this manager's recoverable payload; bounded to 64.</summary>
        public int DormantTerrainCount => dormantTerrainCount;
        /// <summary>Latest retirement/restoration refusal. Empty until a failure; accepted native content or its serialized recovery payload remains intact.</summary>
        public string TerrainResidencyDiagnostic { get; private set; } = string.Empty;

        /// <summary>Retries refused dormant terrain work through the existing bounded commit pump on the main thread; does not allocate, bake or activate terrain synchronously.</summary>
        /// <returns>Number of restoration failures cleared. Zero also permits a new retirement attempt after a cache refusal; a missing owner leaves work unchanged.</returns>
        /// <remarks>Use after correcting a saved payload or freeing memory. Failed retry keeps its payload and reports the new cause; normal budget-release retries require increased headroom.</remarks>
        public int RetryTerrainResidency()
        {
            if (Owner == null) return 0;
            int retried = 0;
            foreach (var chunk in pool)
                if (chunk.residencyFailure != null) { chunk.residencyFailure = null; retried++; }
            dormantEpoch++;
            TerrainResidencyDiagnostic = string.Empty;
            FarTerrainDiagnostic = string.Empty; // UC-T09-04: the incremented epoch retries distant display through the same authority.
            readinessRefreshPending = true; regionDemandChanged = true;
            return retried;
        }

        private bool HasDormantTerrain(Chunk chunk)
        {
            if (chunk.dormantChecked) return chunk.dormantTerrain != null;
            chunk.dormantChecked = true;
            if (chunk.go != null) chunk.dormantTerrain = chunk.go.GetComponent<UCTerrainDormantState>();
            return chunk.dormantTerrain != null;
        }

        private bool TryRetireDormantTerrain(UniverseCreatorObject world, Chunk chunk)
        {
            if (!world.retireDormantTerrain || !chunk.ownsCommittedData || chunk.data == null ||
                chunk.terrain == null || chunk.terrain.terrainData != chunk.data || chunk.resident || chunk.inFlight ||
                chunk.commit != null || chunk.publicationInProgress || HasDormantTerrain(chunk) ||
                chunk.residencyAttemptEpoch == dormantEpoch) return false;
            chunk.residencyAttemptEpoch = dormantEpoch;
            ProductMemoryBudget.Lease memory = null;
            UCTerrainDormantState state = null;
            try
            {
                long bytes = UCTerrainDormantState.EstimateCaptureBytes(chunk.data);
                if (dormantTerrainCount >= 64 || bytes > (long)world.dormantTerrainBudgetMiB * 1024 * 1024 - dormantTerrainBytes)
                {
                    TerrainResidencyDiagnostic = "Dormant cache is full (64 holders or authored MiB cap); cell " + chunk.cell + " retains its native content.";
                    return false;
                }
                memory = world.ProductMemory.Reserve(bytes);
                state = UCTerrainDormantState.Capture(chunk.terrain);
                // Snapshot completion precedes detachment. Do not withdraw accepted immutable ground/channel publication.
                TerrainData previous = chunk.data;
                chunk.terrain.terrainData = null;
                if (chunk.readinessCollider == null && chunk.go != null) chunk.readinessCollider = chunk.go.GetComponent<TerrainCollider>();
                if (chunk.readinessCollider != null) { chunk.readinessCollider.enabled = false; chunk.readinessCollider.terrainData = null; }
                chunk.dormantTerrain = state; chunk.dormantChecked = true;
                chunk.dormantMemory = memory; chunk.dormantBytes = bytes;
                dormantTerrainBytes += bytes; dormantTerrainCount++;
                chunk.data = null; chunk.ownsCommittedData = false;
                DestroyCommitData(previous, chunk.committedNativeMemory); chunk.committedNativeMemory = null;
                memory = null; state = null;
                return true;
            }
            catch (Exception error)
            {
                TerrainResidencyDiagnostic = "Cell " + chunk.cell + " retirement refused: " + error.Message;
                return false;
            }
            finally { memory?.Dispose(); if (state != null) UCTerrainDormantState.DestroyState(state); }
        }

        private void AdmitDormantMemory(UniverseCreatorObject world, Chunk chunk)
        {
            if (chunk.dormantMemory == null)
            {
                // Recovery after domain reload re-admits serialized bytes before allocating a replacement.
                long bytes = chunk.dormantTerrain.PayloadBytes + 256 * 1024;
                if (dormantTerrainCount >= 64 || bytes > (long)world.dormantTerrainBudgetMiB * 1024 * 1024 - dormantTerrainBytes)
                    throw new InvalidOperationException("Serialized dormant terrain exceeds the authored recovery cache cap.");
                chunk.dormantMemory = world.ProductMemory.Reserve(bytes); chunk.dormantBytes = bytes;
                dormantTerrainBytes += bytes; dormantTerrainCount++;
            }
        }

        private IEnumerable<ChunkCommitStep> RestoreDormantTerrain(UniverseCreatorObject world, Chunk chunk)
        {
            if (!HasDormantTerrain(chunk)) yield break;
            UCTerrainDormantState state = chunk.dormantTerrain;
            state.ValidatePayload(); AdmitDormantMemory(world, chunk);
            ReleaseFarTerrain(chunk); // UC-T09-04: near admission cannot be starved by its own distant native copy.
            TerrainData restored = null;
            ProductMemoryBudget.Lease native = null, upload = null, baselineMemory = null;
            try
            {
                native = world.ProductMemory.Reserve(0, checked(state.PayloadBytes * 2));
                upload = world.ProductMemory.Reserve(state.UploadScratchBytes);
                restored = new TerrainData { name = "UC Terrain " + chunk.cell };
                foreach (var step in state.RestoreStages(restored)) yield return step;
                if (chunk.go == null || chunk.terrain == null || chunk.dormantTerrain != state) yield break;
                float[,] recoveredBaseline = null;
                if (chunk.appliedHeights == null)
                {
                    // Scene/domain recovery has no managed baseline, matching the existing native adoption contract.
                    baselineMemory = world.ProductMemory.Reserve(64L + (long)restored.heightmapResolution * restored.heightmapResolution * 4);
                    recoveredBaseline = restored.GetHeights(0, 0, restored.heightmapResolution, restored.heightmapResolution);
                }
                // Keep restoration detached until every height, hole, alpha, tree and detail upload succeeds.
                chunk.terrain.terrainData = restored;
                if (chunk.readinessCollider == null) chunk.readinessCollider = chunk.go.GetComponent<TerrainCollider>();
                if (chunk.readinessCollider != null) chunk.readinessCollider.terrainData = restored;
                chunk.data = restored; chunk.resolution = restored.heightmapResolution; chunk.ownsCommittedData = true;
                chunk.committedNativeMemory = native; native = null; restored = null;
                // UC Scene/Play repair: the fully recovered native identity is accepted; its river beds are not a new pristine bake.
                chunk.restoredAcceptedData = chunk.data;
                // Accepted baseline arrays stay unchanged: later Save Manual Edits must still see sculpt deltas.
                if (recoveredBaseline != null)
                {
                    chunk.flatHeightsMemory?.Dispose(); chunk.flatHeightsMemory = baselineMemory; baselineMemory = null;
                    chunk.appliedHeights = recoveredBaseline;
                    chunk.applied = true;
                }
                ReleaseDormantPayload(chunk); chunk.residencyFailure = null;
                readinessRefreshPending = true; streamWindowChanged = true; regionDemandChanged = true;
                yield return new ChunkCommitStep(ChunkCommitStage.PublishContent);
            }
            finally
            {
                if (restored != null) { DestroyCommitData(restored, native); native = null; }
                native?.Dispose(); upload?.Dispose(); baselineMemory?.Dispose();
            }
        }

        private bool PumpTerrainResidency(UniverseCreatorObject world)
        {
            if (world == null || !built || CommitBudgetSpent(world)) return false;
            if (lastDormantBudget != world.dormantTerrainBudgetMiB)
            { lastDormantBudget = world.dormantTerrainBudgetMiB; dormantEpoch++; }
            if (residencyChunk == null)
            {
                long headroom = world.ProductMemory.LimitBytes - world.ProductMemory.ManagedBytes - world.ProductMemory.NativeBytes;
                // Resident restoration has precedence over background retirement, even while dependent work waits.
                foreach (var chunk in pool)
                {
                    if (chunk.residencyFailure != null && headroom > chunk.residencyFailureHeadroom)
                        chunk.residencyFailure = null; // UC-T09-04: release by any shared-budget owner permits one fresh admission.
                    if (HasDormantTerrain(chunk) && (IsCellInLoadedWindow(world, chunk) || !Application.isPlaying || !world.retireDormantTerrain) &&
                        chunk.residencyFailure == null && chunk.go != null && chunk.terrain != null)
                    { residencyChunk = chunk; chunk.residencyCommit = RestoreDormantTerrain(world, chunk).GetEnumerator(); break; }
                }
            }
            if (residencyChunk != null)
            {
                Chunk chunk = residencyChunk; bool changed = false;
                try
                {
                    while (!CommitBudgetSpent(world))
                    {
                        double started = Time.realtimeSinceStartupAsDouble;
                        if (!chunk.residencyCommit.MoveNext()) { DisposeResidencyCommit(); break; }
                        RecordCommitStep(world, chunk.residencyCommit.Current, started); changed = true;
                    }
                    return changed;
                }
                catch (Exception error)
                {
                    chunk.residencyFailure = error.Message;
                    TerrainResidencyDiagnostic = "Cell " + chunk.cell + " restoration is pending: " + error.Message;
                    DisposeResidencyCommit();
                    chunk.residencyFailureHeadroom = world.ProductMemory.LimitBytes - world.ProductMemory.ManagedBytes - world.ProductMemory.NativeBytes;
                    return changed; // Payload survives; the refusal is observable, never treated as ready ground.
                }
            }
            if (!Application.isPlaying || !world.retireDormantTerrain || !DependenciesSettled(world)) return false;
            foreach (var chunk in pool)
            {
                if (chunk.resident || chunk.residencyAttemptEpoch == dormantEpoch || !chunk.ownsCommittedData || chunk.data == null ||
                    chunk.terrain == null || chunk.terrain.terrainData != chunk.data || chunk.inFlight || chunk.commit != null ||
                    chunk.publicationInProgress || HasDormantTerrain(chunk)) continue; // UC-T09-04: an ineligible first holder cannot starve later retirement.
                double started = Time.realtimeSinceStartupAsDouble;
                bool retired = TryRetireDormantTerrain(world, chunk);
                // One native capture per pump; its indivisible getters are reported without pretending the soft budget preempts them.
                RecordCommitStep(world, new ChunkCommitStep(ChunkCommitStage.PrepareHeights), started);
                return retired;
            }
            return false;
        }

        private void DisposeResidencyCommit()
        {
            if (residencyChunk == null) return;
            var commit = residencyChunk.residencyCommit; residencyChunk.residencyCommit = null; residencyChunk = null;
            commit?.Dispose();
        }

        private void ReleaseDormantPayload(Chunk chunk)
        {
            ReleaseFarTerrain(chunk); // UC-T09-04: pending/visible displays release before the serialized source is cleared.
            if (chunk.dormantMemory != null) { dormantTerrainBytes -= chunk.dormantBytes; dormantTerrainCount--; }
            chunk.dormantMemory?.Dispose(); chunk.dormantMemory = null; chunk.dormantBytes = 0;
            UCTerrainDormantState.DestroyState(chunk.dormantTerrain); chunk.dormantTerrain = null;
            dormantEpoch++; // Budget released: previously refused cells can attempt one new admission.
        }

        private void RestoreDormantBeforeDisable()
        {
            DisposeResidencyCommit();
            var world = Owner;
            if (world == null) return;
            foreach (var chunk in pool)
            {
                if (!HasDormantTerrain(chunk)) continue;
                try { foreach (var step in RestoreDormantTerrain(world, chunk)) { } }
                catch (Exception error)
                {
                    // The component's serialized flat arrays remain the recovery authority if native allocation cannot fit.
                    TerrainResidencyDiagnostic = "Dormant cell " + chunk.cell + " remains serialized for recovery: " + error.Message;
                }
            }
        }
    }
}
