// UC-T06-18: exercise saved intent through the production native replacement, region boundaries, quality and Undo.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationManualIntent()
        {
            var root = new GameObject("UC persistent manual intent acceptance"); root.SetActive(false);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 40, 16) }; data.SetDetailResolution(16, 8);
            var terrainRoot = Terrain.CreateTerrainGameObject(data); terrainRoot.transform.SetParent(root.transform, false);
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube); prefab.SetActive(false);
            var texture = new Texture2D(2, 2); var spawned = new List<GameObject>();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var manager = root.AddComponent<UCVegetationSystemManager>();
                var library = world.scatterLibrary;
                library.objects.Add(new ObjectPrototypeDef { prefab = prefab });
                library.shrubs.Add(new ShrubPrototypeDef { prefab = prefab });
                library.trees.Add(new TreePrototypeDef { prefab = prefab });
                library.details.Add(new DetailPrototypeDef { billboardTexture = texture });
                library.maxObjectsPerChunk = 1; library.maxShrubsPerChunk = 1; library.shrubColliderBudget = 1;
                string id = world.ReadVegetationIdentity();
                var pose = new ScatterInstance(new Vector3(8, 3, 8), Quaternion.Euler(13, 37, 9), new Vector3(2, 3, 4), 0) { footprintRadius = 1 };
                var key = VegetationInstanceKey.Create(id, "objects:authored", library.objects[0].persistentId, 8, 8);
                var grass = VegetationInstanceKey.Create(id, "grass:authored", library.details[0].persistentId, 2, 2);
                var exclusion = VegetationInstanceKey.Create(id, "manual:clear", "exclusion", 0, 0);
                var store = manager.RemovedInstances;
                store.SetManualPlacement(key, "objects", pose); store.SetManualExclusion(exclusion, new Rect(0, 0, 16, 16));
                store.SetManualDetailPatch(grass, new Rect(2, 2, 4, 4), 2);
                var source = new OutputBag(); source.scatter["rocks"] = new List<ScatterInstance> { IdentityInstance(id, "rocks", library.objects[0].persistentId, 12, 12) };
                source.scatter["objects"] = new List<ScatterInstance> { IdentityInstance(id, "objects:authored", library.objects[0].persistentId, 8, 8) };
                source.detailLayers[0] = new int[16, 16]; source.detailLayers[0][1, 1] = 9;
                library.globalDetailDensityScale = 0;
                var terrain = terrainRoot.GetComponent<Terrain>();
                ScatterApplier.Apply(terrain, data, source, library, manager.transform, null, 6018, manager, spawned);
                Check(spawned.Count == 1 && spawned[0].transform.position == pose.position &&
                    Quaternion.Angle(spawned[0].transform.rotation, pose.rotation) < 0.01f && spawned[0].transform.localScale == pose.scale,
                    "UC-T06-18 real native replacement retains one exact locked rock pose despite conflicting procedural channels and a one-object budget.");
                Check(data.GetDetailLayer(2, 2, 4, 4, 0)[0, 0] == 2 && data.GetDetailLayer(1, 1, 1, 1, 0)[0, 0] == 0 && source.detailLayers[0][1, 1] == 9,
                    "UC-T06-18 native authored grass survives zero procedural quality density, overrides exclusions and leaves source products immutable.");
                var saved = JsonUtility.FromJson<ScatterRemovalStore>(JsonUtility.ToJson(store));
                var region = new Rect(0, 0, 16, 16);
                var left = saved.ComposeManualIntent(new OutputBag(), library, id, new Rect(0, 0, 8, 16), 16);
                var right = saved.ComposeManualIntent(new OutputBag(), library, id, new Rect(8, 0, 8, 16), 16);
                Check(!left.scatter.ContainsKey("objects") && right.scatter["objects"].Count == 1 && saved.ManualIntentCount == 3,
                    "UC-T06-18 serialized intent has one half-open resident owner at a chunk boundary, independent of application order.");
                var foreign = saved.ComposeManualIntent(new OutputBag(), library, "foreign-world", region, 16);
                Check(foreign.scatter.Count == 0 && foreign.detailLayers.Count == 0,
                    "UC-T06-18 saved manual intent cannot place or erase another world's vegetation.");
                var heights = new float[33, 33]; for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.5f;
                data.SetHeights(0, 0, heights);
                var manual = saved.ComposeManualIntent(new OutputBag(), library, id, region, 16).scatter["objects"][0];
                var ground = new ScatterApplier.FinalGroundContext(terrain, data);
                Check(ground.TrySeat(manual, prefab, manual.scale, 1, 2, out var fixedPosition, out var fixedRotation) && fixedPosition == pose.position &&
                    Quaternion.Angle(fixedRotation, pose.rotation) < 0.01f && ground.ManualGroundConflicts == 1,
                    "UC-T06-18 terrain changes diagnose conflicting final contact without moving, tilting or embedding the locked pose.");
                store.Remove(key); ScatterApplier.Apply(terrain, data, source, library, manager.transform, null, 6018, manager, spawned);
                Check(spawned.Count == 0 && store.ManualIntentCount == 3, "UC-T06-18 exact removal suppresses a locked pose without discarding its saved override.");
                store.Restore(key); library.globalDetailDensityScale = 1;
                ScatterApplier.Apply(terrain, data, source, library, manager.transform, null, 6018, manager, spawned);
                Check(spawned.Count == 1 && spawned[0].transform.position == pose.position && data.GetDetailLayer(2, 2, 1, 1, 0)[0, 0] == 2,
                    "UC-T06-18 restoring and revisiting reapplies the exact pose/grass once after terrain and quality changes.");
                var state = new UCVegetationManualAuthoring.State { Mode = 1, Bounds = new Rect(12, 12, 2, 2) };
                Undo.IncrementCurrentGroup(); UCVegetationManualAuthoring.Save(manager, state); Undo.FlushUndoRecordObjects();
                Check(manager.RemovedInstances.ManualIntentCount == 4, "UC-T06-18 real manager authoring saves a pending override without launching a second scatter pass.");
                Undo.PerformUndo(); Check(manager.RemovedInstances.ManualIntentCount == 3, "UC-T06-18 native Undo restores the previous sparse intent store.");
                Undo.PerformRedo(); Check(manager.RemovedInstances.ManualIntentCount == 4, "UC-T06-18 native Redo restores the exact authored override.");
                bool invalid = false; try { saved.SetManualPlacement(key, "trees", pose); } catch (ArgumentException) { invalid = true; }
                Check(invalid && saved.ManualIntentCount == 3, "UC-T06-18 tilted/nonuniform native tree authoring fails explicitly without replacing saved intent.");
                manager.RemovedInstances.ClearManualIntent(state.Key);
                var treeKey = VegetationInstanceKey.Create(id, "trees:manual", library.trees[0].persistentId, 10, 10);
                var treePose = new ScatterInstance(new Vector3(10, 4, 10), Quaternion.Euler(0, 41, 0), new Vector3(2, 3, 2), 0) { footprintRadius = 0.5f };
                var shrubKey = VegetationInstanceKey.Create(id, "shrubs:manual", library.shrubs[0].persistentId, 13, 13);
                var shrubPose = new ScatterInstance(new Vector3(13, 5, 13), Quaternion.Euler(7, 29, 5), Vector3.one * 2, 0) { footprintRadius = 0.5f };
                manager.RemovedInstances.SetManualPlacement(treeKey, "trees", treePose);
                manager.RemovedInstances.SetManualPlacement(shrubKey, "shrubs", shrubPose);
                ScatterApplier.Apply(terrain, data, source, library, manager.transform, null, 6018, manager, spawned);
                var tree = data.treeInstanceCount == 1 ? data.treeInstances[0] : default;
                Check(data.treeInstanceCount == 1 && Mathf.Abs(tree.position.y * data.size.y - 4) < 0.001f && tree.widthScale == 2 && tree.heightScale == 3 &&
                    Mathf.Abs(tree.rotation * Mathf.Rad2Deg - 41) < 0.01f && spawned.Exists(item => item.transform.position == shrubPose.position && Quaternion.Angle(item.transform.rotation, shrubPose.rotation) < 0.01f),
                    "UC-T06-18 native upright tree and selectable shrub retain category-appropriate authored positions, rotations and scales after ground changes.");
                manager.RemovedInstances.Remove(grass);
                ScatterApplier.Apply(terrain, data, source, library, manager.transform, null, 6018, manager, spawned);
                Check(data.GetDetailLayer(2, 2, 1, 1, 0)[0, 0] == 0, "UC-T06-18 removing an addressed manual grass patch erases its native counts while retaining the sparse intent for restoration.");
            }
            finally
            {
                ScatterApplier.Release(spawned, null, 6018); Undo.ClearAll();
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(prefab); UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
