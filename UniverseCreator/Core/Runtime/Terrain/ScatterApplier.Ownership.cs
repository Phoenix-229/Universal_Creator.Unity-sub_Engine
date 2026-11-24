// UC-T06-12: main-thread replacement stages scatter only, borrowing the accepted ground and shared assets.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    public static partial class ScatterApplier
    {
        internal sealed class Replacement : IDisposable
        {
            private readonly TerrainData target;
            private readonly Transform parent;
            private readonly List<GameObject> accepted;
            private readonly List<GameObject> stagedObjects = new List<GameObject>();
            private GameObject stagingRoot;
            private TerrainData staged;
            private GPUInstancingManager.OwnerReplacement gpu;
            private UCVegetationSystemManager.RegistrationReplacement registration;
            private ProductMemoryBudget.Lease memory;
            private NativeScatterState previous;
            private bool nativePrepared;
            private bool finished;

            internal Replacement(Terrain terrain, TerrainData data, OutputBag outputs, ScatterPrototypeLibrary library,
                Transform parent, GPUInstancingManager instancing, int owner, UCVegetationSystemManager vegetation,
                List<GameObject> accepted)
            {
                target = data; this.parent = parent; this.accepted = accepted;
                try
                {
                    // Two managed detail copies plus separate native staging are charged; no terrain-height clone.
                    long cells = (long)data.detailResolution * data.detailResolution;
                    long instances = data.treeInstanceCount;
                    if (outputs.scatter != null) foreach (var list in outputs.scatter.Values) instances += list?.Count ?? 0;
                    long bytes = checked(65536 + cells * Math.Max(data.detailPrototypes.Length, library.details.Count) * 8 + instances * 128);
                    var world = terrain.GetComponentInParent<UniverseCreatorObject>(true); // UC-T06-12/18: parked or staged hierarchies retain their explicit world owner.
                    if (vegetation != null && vegetation.RemovedInstances.ManualIntentCount > 0)
                    {
                        long manualCells = 0; foreach (var grid in outputs.detailLayers.Values) if (grid != null) manualCells = checked(manualCells + grid.LongLength);
                        if (manualCells > 4000000) throw new InvalidOperationException("Manual detail composition exceeds its four-million-cell envelope; reduce region/detail resolution.");
                        bytes = checked(bytes + manualCells * 4 + vegetation.RemovedInstances.ManualIntentCount * 256L); // UC-T06-18: charge sparse intent copies before creating them.
                    }
                    if (world != null) memory = world.ProductMemory.Reserve(bytes, 65536 + cells * library.details.Count * 4);
                    else if (bytes > 256L * 1024 * 1024) throw new InvalidOperationException("Scatter replacement exceeds the unowned 256 MiB staging envelope.");
                    if (vegetation != null && vegetation.RemovedInstances.ManualIntentCount > 0)
                    {
                        if (world == null) throw new InvalidOperationException("Manual intent requires its explicit UC world namespace.");
                        Vector3 origin = terrain.transform.position;
                        outputs = vegetation.RemovedInstances.ComposeManualIntent(outputs, library, world.ReadVegetationIdentity(),
                            new Rect(origin.x, origin.z, data.size.x, data.size.z), data.detailResolution); // UC-T06-18: compose intent within the existing staged/native ownership transaction.
                    }
                    gpu = instancing != null ? instancing.BeginOwnerReplacement(owner) : null;
                    registration = vegetation != null ? vegetation.BeginRegistrationReplacement(owner) : null;
                    stagingRoot = new GameObject("UC staged scatter"); stagingRoot.SetActive(false);
                    stagingRoot.transform.SetParent(parent, false);
                    staged = new TerrainData { size = data.size };
                    if (data.detailResolution > 0) staged.SetDetailResolution(data.detailResolution, data.detailResolutionPerPatch);
                    if (staged.detailScatterMode != data.detailScatterMode) staged.SetDetailScatterMode(data.detailScatterMode);
                    ApplyCore(terrain, staged, outputs, library, stagingRoot.transform, instancing, owner, vegetation, stagedObjects, data);
                }
                catch { Dispose(); throw; }
            }

            // No yield occurs between native publication and owner swap. Cancellation disposes this object before Commit.
            internal void PrepareCommit()
            {
                if (finished || target == null) throw new InvalidOperationException("Scatter replacement has retired or lost its terrain.");
                if (nativePrepared) return;
                gpu?.PrepareCommit(); registration?.PrepareCommit();
                if (accepted != null && accepted.Capacity < stagedObjects.Count) accepted.Capacity = stagedObjects.Count; // Installed profiles support Capacity without a newer List API.
                previous = new NativeScatterState(target);
                var replacement = new NativeScatterState(staged);
                try { replacement.Write(target); }
                catch (Exception failure)
                {
                    try { previous.Write(target); }
                    catch (Exception rollback) { throw new AggregateException("Native scatter publication and restoration both failed.", failure, rollback); }
                    throw;
                }
                nativePrepared = true;
            }

            internal void Commit()
            {
                PrepareCommit();
                gpu?.Commit(); registration?.Commit();
                // Retirement disables colliders immediately; Unity may destroy their native objects at frame end.
                Release(accepted, null, 0);
                foreach (var instance in stagedObjects)
                {
                    if (instance == null) continue;
                    instance.transform.SetParent(parent, true);
                    accepted?.Add(instance);
                }
                stagedObjects.Clear(); previous = null; finished = true; DestroyStaging();
            }

            /// <summary>Restores any unpublished native upload and retires staged objects/batches; committed replacements remain accepted.</summary>
            /// <exception cref="Exception">Unity refuses restoration of native tree/detail data; the actual failure remains visible.</exception>
            public void Dispose()
            {
                if (!finished)
                {
                    gpu?.Dispose(); registration?.Dispose();
                    try { if (nativePrepared && target != null) previous.Write(target); }
                    finally { Release(stagedObjects, null, 0); previous = null; finished = true; DestroyStaging(); }
                }
                DestroyStaging();
            }

            private void DestroyStaging()
            {
                // The native reservation survives deferred destruction, using the same lifetime contract as terrain commits.
                if (stagingRoot != null) { stagingRoot.SetActive(false); DestroyOwned(stagingRoot); stagingRoot = null; }
                if (staged != null)
                {
                    var retiring = staged; var reservation = memory; staged = null; memory = null;
                    DestroyOwned(retiring);
                    if (!Application.isPlaying || retiring == null) reservation?.Dispose();
                    else FinishScatterRetirement(retiring, reservation);
                }
                memory?.Dispose(); memory = null;
            }
        }

        private sealed class NativeScatterState
        {
            private readonly TreePrototype[] trees;
            private readonly TreeInstance[] instances;
            private readonly DetailPrototype[] details;
            private readonly DetailScatterMode mode;
            private readonly List<int[,]> layers = new List<int[,]>();
            private readonly TreeAppearanceSources appearance;

            internal NativeScatterState(TerrainData data)
            {
                trees = data.treePrototypes; instances = data.treeInstances; details = data.detailPrototypes; mode = data.detailScatterMode;
                treeAppearanceSources.TryGetValue(data, out appearance);
                if (data.detailResolution > 0)
                    for (int i = 0; i < details.Length; i++) layers.Add(data.GetDetailLayer(0, 0, data.detailResolution, data.detailResolution, i));
            }

            internal void Write(TerrainData data)
            {
                data.treePrototypes = trees; data.detailPrototypes = details;
                if (data.detailScatterMode != mode) data.SetDetailScatterMode(mode);
                for (int i = 0; i < layers.Count; i++) SetDetailLayerBounded(data, i, layers[i]);
                data.SetTreeInstances(instances, false);
                treeAppearanceSources.Remove(data);
                if (appearance != null) treeAppearanceSources.Add(data, appearance);
            }
        }

        internal static Replacement PrepareReplacement(Terrain terrain, TerrainData data, OutputBag outputs,
            ScatterPrototypeLibrary library, Transform parent, GPUInstancingManager instancing, int owner,
            UCVegetationSystemManager vegetation, List<GameObject> accepted)
        {
            if (terrain == null || data == null || outputs == null || library == null)
                throw new ArgumentException("Scatter replacement requires terrain, data, accepted outputs and a prototype library.");
            return new Replacement(terrain, data, outputs, library, parent, instancing, owner, vegetation, accepted);
        }

        private static void DestroyOwned(UnityEngine.Object resource)
        { if (Application.isPlaying) UnityEngine.Object.Destroy(resource); else UnityEngine.Object.DestroyImmediate(resource); }

        private static async void FinishScatterRetirement(TerrainData retiring, ProductMemoryBudget.Lease memory)
        {
            try
            {
                for (int frame = 0; frame < 2 && retiring != null; frame++) await Awaitable.NextFrameAsync();
                if (retiring == null) memory?.Dispose();
                else Debug.LogError("UC-T06-12: native scatter staging did not retire within two frames; its reservation remains charged.");
            }
            catch (Exception error) { Debug.LogError("UC-T06-12: scatter retirement failed: " + error); }
        }
    }
}
