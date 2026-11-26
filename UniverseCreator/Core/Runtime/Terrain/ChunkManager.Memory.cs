using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public partial class ChunkManager
    {
        // UC-T02-06: Destroy is deferred in Play mode; keep its reservation until Unity reports the object gone.
        private readonly List<KeyValuePair<TerrainData, ProductMemoryBudget.Lease>> retiringTerrainMemory =
            new List<KeyValuePair<TerrainData, ProductMemoryBudget.Lease>>();

        private void ReapTerrainMemory()
        {
            for (int i = retiringTerrainMemory.Count - 1; i >= 0; i--)
                if (retiringTerrainMemory[i].Key == null)
                {
                    retiringTerrainMemory[i].Value?.Dispose();
                    retiringTerrainMemory.RemoveAt(i);
                }
        }

        private static void DropRetainedProducts(Chunk chunk)
        {
            if (chunk.retained == null) return;
            foreach (RetainedProduct product in chunk.retained.Values) product.memoryOwner?.Forget(product.memoryEntry);
            chunk.retained = null;
        }

        // Main-thread-only estimate: CPU/native height, hole, alpha and detail payloads, excluding engine/driver overhead.
        private static long EstimateCommitNativeBytes(Chunk chunk, OutputBag outputs, int minimumLayers = 1)
        {
            long samples = (long)chunk.resolution * chunk.resolution;
            long bytes = samples * 8 + (long)(chunk.resolution - 1) * (chunk.resolution - 1) * 2;
            TerrainData existing = chunk.data;
            // A cold commit still assigns layers, even when the graph publishes no splat map.
            long oldAlphas = existing != null
                ? (long)existing.alphamapWidth * existing.alphamapHeight * Math.Max(minimumLayers, existing.alphamapLayers)
                : 512L * 512L * Math.Max(1, minimumLayers);
            bytes += Math.Max(oldAlphas, outputs?.splatmap?.LongLength ?? 0) * 8;
            long details = 0;
            if (outputs != null) foreach (int[,] layer in outputs.detailLayers.Values) if (layer != null) details += layer.LongLength;
            if (existing != null) details = Math.Max(details, (long)existing.detailWidth * existing.detailHeight * existing.detailPrototypes.Length);
            bytes += details * 4;
            if (existing != null) bytes += (long)existing.treeInstanceCount * 64;
            if (outputs != null) foreach (var instances in outputs.scatter.Values) if (instances != null) bytes += (long)instances.Count * 64;
            return bytes;
        }

        // Initial native constructor allocation is indivisible. Check its actual dimensions before installing it;
        // budget rejection destroys only the new object and leaves the previously accepted terrain attached.
        private void EnsureOwnedFlatTerrain(UniverseCreatorObject owner, Chunk chunk, int resolution, Vector3 size)
        {
            TerrainData created = TerrainBuilder.GetOrCreateTerrainData(chunk.data, resolution, size);
            if (ReferenceEquals(created, chunk.data)) return; // Authored/adopted data stays borrowed.
            ProductMemoryBudget.Lease reservation = null;
            try
            {
                var dimensions = new Chunk { data = created, resolution = resolution };
                reservation = owner.ProductMemory.Reserve(0, EstimateCommitNativeBytes(dimensions, null, owner.terrainLayers?.Length ?? 1));
            }
            catch { DestroyCommitData(created); throw; }
            if (chunk.ownsCommittedData) DestroyCommitData(chunk.data, chunk.committedNativeMemory);
            chunk.data = created;
            chunk.restoredAcceptedData = null; // UC Scene/Play repair: a replacement placeholder is not saved accepted ground.
            chunk.ownsCommittedData = true;
            chunk.committedNativeMemory = reservation;
        }

        private void ReleaseAcceptedMemory(Chunk chunk)
        {
            ReleaseDormantPayload(chunk); // UC-T09-04: explicit layout/owner teardown also retires this holder's recovery bytes.
            RetireChunkScatter(chunk); // UC-T06-12: destruction/layout changes withdraw exactly this region's draws/colliders before native data retirement.
            if (chunk.publicationOwner != null) RiverChannelRegistry.Withdraw(chunk.publicationRun.WorldId, chunk.publicationOwner);
            chunk.publicationOwner = null;
            chunk.restoredAcceptedData = null; // UC Scene/Play repair: retirement withdraws native acceptance without changing imported data.
            chunk.appliedHeights = null; chunk.graphHeights = null; chunk.retainedScatter = null;
            chunk.graphScatterMemory?.Dispose(); chunk.graphScatterMemory = null; // UC-T05-03: pooled cells retire accepted companion payloads too.
            chunk.committedOutputMemory?.Dispose(); chunk.committedOutputMemory = null;
            chunk.committedPristineMemory?.Dispose(); chunk.committedPristineMemory = null;
            chunk.flatHeightsMemory?.Dispose(); chunk.flatHeightsMemory = null;
            if (chunk.ownsCommittedData)
            {
                if (chunk.terrain != null && chunk.terrain.terrainData == chunk.data) chunk.terrain.terrainData = null;
                TerrainCollider collider = chunk.go != null ? chunk.go.GetComponent<TerrainCollider>() : null;
                if (collider != null && collider.terrainData == chunk.data) collider.terrainData = null;
                DestroyCommitData(chunk.data, chunk.committedNativeMemory);
            }
            chunk.committedNativeMemory = null; chunk.ownsCommittedData = false;
        }

        private static void SetFlatHeightsMemory(UniverseCreatorObject owner, Chunk chunk, int resolution)
        {
            ProductMemoryBudget.Lease reservation = owner.ProductMemory.Reserve(64 + (long)resolution * resolution * sizeof(float));
            try { chunk.appliedHeights = new float[resolution, resolution]; }
            catch { reservation.Dispose(); throw; }
            chunk.flatHeightsMemory?.Dispose();
            chunk.flatHeightsMemory = reservation;
        }

        private void OnDestroy()
        {
            // UC-T02-06: only native data allocated by this manager belongs to its teardown; imported/adopted assets survive.
            CancelAll();
            if (wireframeMaterial != null)
            {
                if (Application.isPlaying) Destroy(wireframeMaterial);
                else DestroyImmediate(wireframeMaterial);
            }
            foreach (Chunk chunk in pool) { DropRetainedProducts(chunk); ReleaseAcceptedMemory(chunk); }
            ReapTerrainMemory();
            if (retiringTerrainMemory.Count > 0)
            {
                var pending = retiringTerrainMemory.ToArray();
                retiringTerrainMemory.Clear();
                FinishTerrainRetirement(pending);
            }
        }

        // Unity 6000.5 CoreModule documents NextFrameAsync; the static continuation survives this manager's destruction.
        // The normal Destroy contract completes by the next frame. A second frame tolerates end-of-frame teardown ordering.
        private static async void FinishTerrainRetirement(KeyValuePair<TerrainData, ProductMemoryBudget.Lease>[] pending)
        {
            try
            {
                for (int frame = 0; frame < 2; frame++)
                {
                    await Awaitable.NextFrameAsync();
                    int remaining = 0;
                    for (int i = 0; i < pending.Length; i++)
                    {
                        if (pending[i].Key == null) pending[i].Value?.Dispose();
                        else remaining++;
                    }
                    if (remaining == 0) return;
                }
                Debug.LogError("UC-T02-06: native terrain destruction did not retire within two frames; its memory remains charged.");
            }
            catch (Exception error) { Debug.LogError("UC-T02-06: native memory retirement failed: " + error); }
        }
    }
}
