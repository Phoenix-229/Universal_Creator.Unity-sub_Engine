// UC-T06-12: exercise accepted native/GPU/collider ownership, cancellation, independent retirement and revisit.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Nodes;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationOwnershipLifetime()
        {
            var root = new GameObject("UC region ownership acceptance"); root.SetActive(false);
            var dataA = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 40, 16) };
            var dataB = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 40, 16) };
            dataA.SetDetailResolution(16, 8); dataB.SetDetailResolution(16, 8);
            var groundA = Terrain.CreateTerrainGameObject(dataA); groundA.transform.SetParent(root.transform, false);
            var groundB = Terrain.CreateTerrainGameObject(dataB); groundB.transform.SetParent(root.transform, false);
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube); prefab.SetActive(false);
            var texture = new Texture2D(2, 2);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { enableInstancing = true };
            var objectsA = new List<GameObject>(); var objectsB = new List<GameObject>();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var chunks = root.AddComponent<ChunkManager>(); chunks.Initialize(world);
                var vegetation = root.AddComponent<UCVegetationSystemManager>(); vegetation.parentUnderVegetationSystem = true;
                var gpu = root.AddComponent<GPUInstancingManager>();
                var library = world.scatterLibrary;
                library.trees.Add(new TreePrototypeDef { prefab = prefab });
                library.objects.Add(new ObjectPrototypeDef { prefab = prefab });
                library.objects[0].eco.instancingPolicy = Ecology.InstancingPolicy.Never;
                library.objects[0].eco.colliderPolicy = Ecology.ColliderPolicy.Solid;
                library.details.Add(new DetailPrototypeDef { billboardTexture = texture });
                var treeA = IdentityInstance("ownership-world", "trees:a", "cube", 4, 4);
                var objectA = IdentityInstance("ownership-world", "objects:a", "cube", 4, 4);
                var treeB = IdentityInstance("ownership-world", "trees:b", "cube", 12, 12);
                var objectB = IdentityInstance("ownership-world", "objects:b", "cube", 12, 12);
                var bagA = new OutputBag(); bagA.AddScatter("trees", new List<ScatterInstance> { treeA });
                bagA.AddScatter("objects", new List<ScatterInstance> { objectA }); bagA.detailLayers[0] = new int[16, 16]; bagA.detailLayers[0][4, 4] = 3;
                var bagB = new OutputBag(); bagB.AddScatter("trees", new List<ScatterInstance> { treeB });
                bagB.AddScatter("objects", new List<ScatterInstance> { objectB });
                const int ownerA = 61201, ownerB = 61202;
                var terrainA = groundA.GetComponent<Terrain>(); var terrainB = groundB.GetComponent<Terrain>();
                ScatterApplier.Apply(terrainA, dataA, bagA, library, vegetation.transform, gpu, ownerA, vegetation, objectsA);
                ScatterApplier.Apply(terrainB, dataB, bagB, library, vegetation.transform, gpu, ownerB, vegetation, objectsB);
                Check(dataA.treeInstanceCount == 1 && dataB.treeInstanceCount == 1 && objectsA.Count == 1 && objectsB.Count == 1 &&
                    vegetation.RegisteredTreeCount == 2 && vegetation.RegisteredObjectCount == 2,
                    "UC-T06-12 overlapping native regions retain independent tree/object resources and exact registry totals.");
                var mesh = prefab.GetComponent<MeshFilter>().sharedMesh;
                gpu.AddInstance(ownerA, mesh, material, Matrix4x4.Translate(objectA.position), 0, ShadowCastingMode.Off);
                gpu.AddInstance(ownerB, mesh, material, Matrix4x4.Translate(objectB.position), 0, ShadowCastingMode.Off);
                var priorA = objectsA[0]; var priorB = objectsB[0];
                var replacementBag = new OutputBag(); replacementBag.detailLayers[0] = new int[16, 16]; replacementBag.detailLayers[0][4, 4] = 7;
                long managedBefore = world.ProductMemory.ManagedBytes, nativeBefore = world.ProductMemory.NativeBytes;
                using (var replacement = ScatterApplier.PrepareReplacement(terrainA, dataA, replacementBag, library,
                    vegetation.transform, gpu, ownerA, vegetation, objectsA))
                {
                    Check(gpu.InstanceCount == 2 && vegetation.RegisteredTreeCount == 2 && objectsA[0] == priorA && dataA.treeInstanceCount == 1 &&
                        dataA.GetDetailLayer(4, 4, 1, 1, 0)[0, 0] == 3,
                        "UC-T06-12 unpublished replacement preserves accepted native density, objects, GPU draws and live registration.");
                    replacement.PrepareCommit(); // Exercise cancellation after native upload, before the owner publication boundary.
                }
                Check(dataA.treeInstanceCount == 1 && dataA.GetDetailLayer(4, 4, 1, 1, 0)[0, 0] == 3 && objectsA[0] == priorA &&
                    gpu.InstanceCount == 2 && vegetation.RegisteredTreeCount == 2 &&
                    world.ProductMemory.ManagedBytes == managedBefore && world.ProductMemory.NativeBytes == nativeBefore,
                    "UC-T06-12 cancellation after native preparation restores accepted trees/details and retains neighbouring/shared resources.");
                library.objects[0].variation = new ScatterPrototypeVariation { enabled = true, scaleRange = new Vector2(float.NaN, 1) };
                bool rejected = false;
                try { ScatterApplier.Apply(terrainA, dataA, bagA, library, vegetation.transform, gpu, ownerA, vegetation, objectsA); }
                catch (InvalidOperationException) { rejected = true; }
                library.objects[0].variation.enabled = false;
                Check(rejected && objectsA[0] == priorA && gpu.InstanceCount == 2 && dataA.treeInstanceCount == 1 && vegetation.RegisteredTreeCount == 2,
                    "UC-T06-12 real invalid prototype application fails without withdrawing accepted colliders/native trees/batches.");

                Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                object chunkA = Activator.CreateInstance(chunkType, true), chunkB = Activator.CreateInstance(chunkType, true);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                foreach (var pair in new[] { (chunkA, groundA, terrainA, dataA, objectsA, ownerA), (chunkB, groundB, terrainB, dataB, objectsB, ownerB) })
                {
                    SetCommitField(chunkType, pair.Item1, "go", pair.Item2); SetCommitField(chunkType, pair.Item1, "terrain", pair.Item3);
                    SetCommitField(chunkType, pair.Item1, "data", pair.Item4); SetCommitField(chunkType, pair.Item1, "scatterObjects", pair.Item5);
                    SetCommitField(chunkType, pair.Item1, "scatterResourceOwner", pair.Item6);
                    ((IList)typeof(ChunkManager).GetField("pool", flags).GetValue(chunks)).Add(pair.Item1);
                }
                typeof(ChunkManager).GetField("instancing", flags).SetValue(chunks, gpu);
                SetCommitField(chunkType, chunkA, "retainedScatter", bagA); SetCommitField(chunkType, chunkB, "retainedScatter", bagB);
                root.SetActive(true); priorA.SetActive(true); priorB.SetActive(true);
                var park = typeof(ChunkManager).GetMethod("SetChunkLive", flags);
                park.Invoke(chunks, new[] { chunkA, (object)false }); park.Invoke(chunks, new[] { chunkA, (object)false });
                Check(!priorA.activeInHierarchy && priorB.activeInHierarchy && vegetation.RegisteredTreeCount == 1 && gpu.RenderFrame(null, false).submittedRecords == 1,
                    "UC-T06-12 repeated parking hides manager-parented colliders and all owned GPU colour/shadows while the adjacent region stays live.");
                park.Invoke(chunks, new[] { chunkA, (object)true });
                Check(priorA.activeInHierarchy && vegetation.RegisteredTreeCount == 2 && gpu.InstanceCount == 2,
                    "UC-T06-12 revisit resumes accepted objects and tallies without reapplication or duplicate records.");
                priorA.SetActive(false); park.Invoke(chunks, new[] { chunkA, (object)false }); park.Invoke(chunks, new[] { chunkA, (object)true });
                Check(!priorA.activeSelf, "UC-T06-12 revisit preserves intentionally inactive prototype/object states.");
                vegetation.RemovedInstances.Remove(objectA.identity); string edits = JsonUtility.ToJson(vegetation.RemovedInstances);
                var restored = JsonUtility.FromJson<ScatterRemovalStore>(edits);
                Check(restored.IsRemoved(objectA) && !restored.IsRemoved(objectB), "UC-T06-12 serialized removal edits retain exact world/layer identity across reload.");
                typeof(ChunkManager).GetMethod("RetireChunkScatter", flags).Invoke(chunks, new[] { chunkA });
                Check(priorA == null && priorB != null && objectsA.Count == 0 && dataA.treeInstanceCount == 0 && dataB.treeInstanceCount == 1 &&
                    gpu.InstanceCount == 1 && vegetation.RegisteredTreeCount == 1 && material != null && mesh != null && prefab != null,
                    "UC-T06-12 retiring one overlapping region releases exactly its objects/native instances/GPU records while borrowed shared assets remain alive.");
                ScatterApplier.Apply(terrainA, dataA, bagA, library, vegetation.transform, gpu, ownerA, vegetation, objectsA);
                Check(objectsA.Count == 0 && dataA.treeInstanceCount == 1 && priorB != null && JsonUtility.ToJson(vegetation.RemovedInstances) == edits,
                    "UC-T06-12 regeneration/revisit respects persisted removals without losing unrelated planting or mutating saved intent.");
                SetCommitField(chunkType, chunkA, "retainedScatter", bagA);
                managedBefore = world.ProductMemory.ManagedBytes;
                var pending = new GraphVegetationResult { Outputs = replacementBag, Memory = world.ProductMemory.Reserve(128) };
                SetCommitField(chunkType, chunkA, "pendingGraphScatter", pending);
                chunks.CancelAll();
                Check(pending.Outputs == null && world.ProductMemory.ManagedBytes == managedBefore &&
                    ReferenceEquals(chunkType.GetField("retainedScatter").GetValue(chunkA), bagA) && dataA.treeInstanceCount == 1 &&
                    JsonUtility.ToJson(vegetation.RemovedInstances) == edits,
                    "UC-T06-12 real cancellation retires queued candidate memory while accepted native planting and persistent edits remain pinned.");

                // Simulate the managed ownership dictionaries disappearing on domain reload; scene objects/native data survive.
                gpu.ClearAll(); vegetation.ReleaseChunk(ownerA); vegetation.ReleaseChunk(ownerB);
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(chunks); pool.Clear();
                typeof(ChunkManager).GetMethod("AdoptExistingCells", flags).Invoke(chunks, new object[] { root.transform });
                object adoptedB = null;
                foreach (object adopted in pool) if (ReferenceEquals(chunkType.GetField("terrain").GetValue(adopted), terrainB)) adoptedB = adopted;
                var tagB = priorB.GetComponent<UCScatterRegionResource>();
                Check(adoptedB != null && ((List<GameObject>)chunkType.GetField("scatterObjects").GetValue(adoptedB)).Contains(priorB) &&
                    tagB.World == world && tagB.Region == terrainB && tagB.Identity.Equals(objectB.identity),
                    "UC-T06-12 scene/domain re-adoption recovers manager-parented resources by exact world/native-region references and full identity.");
                typeof(ChunkManager).GetMethod("RetireChunkScatter", flags).Invoke(chunks, new[] { adoptedB });
                Check(priorB == null && dataB.treeInstanceCount == 0 && prefab != null && texture != null,
                    "UC-T06-12 re-adopted scene resources retire without orphan colliders or destruction of shared prototypes/textures.");
            }
            catch (Exception error) { Check(false, "UC-T06-12 ownership acceptance exception: " + error); }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); ScatterApplier.Release(objectsA, null, 0); ScatterApplier.Release(objectsB, null, 0);
                UnityEngine.Object.DestroyImmediate(dataA); UnityEngine.Object.DestroyImmediate(dataB);
                UnityEngine.Object.DestroyImmediate(prefab); UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
