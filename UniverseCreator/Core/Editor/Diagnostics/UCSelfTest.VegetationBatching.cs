// UC-T06-10: actual GPU-owner planning, native submesh/LOD templates and fallback placement; headless plans do not claim rendered results.
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
        private static void TestVegetationSpatialBatching()
        {
            var root = new GameObject("UC spatial batching fixture");
            var cameraRoot = new GameObject("UC spatial batching camera");
            var prefab = new GameObject("UC two-LOD prototype");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward } };
            mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 0, 2, 3 }, 1); mesh.RecalculateBounds();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { enableInstancing = true };
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 40, 32) };
            var terrainRoot = Terrain.CreateTerrainGameObject(data);
            var spawned = new List<GameObject>();
            float previousBias = QualitySettings.lodBias;
            try
            {
                QualitySettings.lodBias = 1f;
                var manager = root.AddComponent<GPUInstancingManager>(); manager.boundsPaddingMetres = 0f;
                var camera = cameraRoot.AddComponent<Camera>(); camera.enabled = false; camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 1000; camera.aspect = 1f;
                manager.AddInstance(1010, mesh, material, Matrix4x4.Translate(new Vector3(1, 0, 10)), 0,
                    ShadowCastingMode.Off, cullDistanceM: 80f);
                manager.AddInstance(1010, mesh, material, Matrix4x4.Translate(new Vector3(300, 0, 10)), 0,
                    ShadowCastingMode.Off, cullDistanceM: 80f);
                var plan = manager.RenderFrame(camera, false);
                Check(plan.residentRecords == 2 && plan.submittedRecords == 1 && plan.distanceRejectedRecords == 1 && plan.drawCalls == 1,
                    "UC-T06-10 actual same-owner spatial batches cull a distant group without a chunk-wide bounds retaining every instance.");
                Check(plan.submittedMatrixBytes == 64 && plan.residentPayloadBytes >= 160 && plan.cullCpuMilliseconds >= 0,
                    "UC-T06-10 matrix payload and measured CPU plan counters remain distinct from plant counts, GPU time and driver uploads.");
                manager.AddInstance(1011, mesh, material, Matrix4x4.Translate(new Vector3(0, 0, -10)), 0,
                    ShadowCastingMode.On, shadowDistanceM: 30f);
                manager.AddInstance(1012, mesh, material, Matrix4x4.Translate(new Vector3(0, 0, -10)), 0,
                    ShadowCastingMode.Off);
                plan = manager.RenderFrame(camera, false);
                Check(plan.shadowOnlyRecords == 1 && plan.frustumRejectedRecords == 1 && plan.submittedRecords == 2,
                    "UC-T06-10 offscreen casters remain shadow-only while noncasting offscreen geometry is actually rejected.");
                manager.ClearOwner(1011); manager.ClearOwner(1012);
                manager.AddInstance(1013, mesh, material, Matrix4x4.Translate(new Vector3(1, 0, 10)), 3, ShadowCastingMode.Off);
                camera.cullingMask &= ~(1 << 3); plan = manager.RenderFrame(camera, false);
                Check(plan.frustumRejectedRecords == 1 && plan.submittedRecords == 1,
                    "UC-T06-10 an explicit camera layer mask excludes the actual incompatible layer batch.");
                manager.ClearAll(); camera.cullingMask = -1;
                for (int i = 0; i < 600; i++) manager.AddInstance(1010, mesh, material, Matrix4x4.Translate(new Vector3(1, 0, 10)), 0, ShadowCastingMode.Off);
                plan = manager.RenderFrame(camera, false);
                Check(plan.submittedRecords == 600 && plan.drawCalls == 3 && plan.submittedMatrixBytes == 600L * 64,
                    "UC-T06-10 persistent linear records use bounded submit slices with no per-frame record-array construction.");
                manager.ClearAll();
                bool invalid = false;
                var poison = Matrix4x4.identity; poison.m03 = float.NaN;
                try { manager.AddInstance(1010, mesh, material, poison, 0); } catch (ArgumentException) { invalid = true; }
                Check(invalid && manager.InstanceCount == 0, "UC-T06-10 poisoned native transforms are refused before registering a resource owner.");

                var near = new GameObject("Near canopy"); near.transform.SetParent(prefab.transform, false);
                near.transform.localPosition = Vector3.up;
                var far = new GameObject("Far mesh impostor slot"); far.transform.SetParent(prefab.transform, false);
                foreach (var child in new[] { near, far })
                { child.AddComponent<MeshFilter>().sharedMesh = mesh; child.AddComponent<MeshRenderer>().sharedMaterials = new[] { material, material }; }
                var group = prefab.AddComponent<LODGroup>(); group.fadeMode = LODFadeMode.None;
                group.SetLODs(new[] { new LOD(0.5f, new[] { near.GetComponent<Renderer>() }), new LOD(0.05f, new[] { far.GetComponent<Renderer>() }) });
                group.size = 2f; group.localReferencePoint = Vector3.zero;
                var ground = new ScatterApplier.FinalGroundContext(terrainRoot.GetComponent<Terrain>(), data);
                bool queued = ground.TryAddInstancedPrefab(manager, 1010, prefab, new Vector3(0, 0, 2), Quaternion.identity, Vector3.one, null, 30, 100, null);
                plan = manager.RenderFrame(camera, false);
                Check(queued && plan.residentRecords == 4 && plan.submittedRecords == 2 && plan.lodRejectedRecords == 2,
                    "UC-T06-10 actual prefab templates register every material/submesh and select only the authored near LOD.");
                cameraRoot.transform.position = new Vector3(0, 0, -8); plan = manager.RenderFrame(camera, false);
                Check(plan.submittedRecords == 2 && plan.lodRejectedRecords == 2,
                    "UC-T06-10 camera movement selects the authored far mesh LOD without registering a second placement.");
                camera.orthographic = true; camera.orthographicSize = 3f;
                plan = manager.RenderFrame(camera, false);
                Check(plan.submittedRecords == 2 && Mathf.Abs(GPUInstancingManager.ScreenHeight(camera, Vector3.zero, 2, 1) - 1f / 3f) < 1e-5f,
                    "UC-T06-10 orthographic LOD uses projected height independently of camera distance.");
                Check(manager.RenderFrame(null, false).submittedRecords == 2,
                    "UC-T06-10 absent camera retains one highest LOD rather than drawing all authored levels together.");
                var batches = (IDictionary)typeof(GPUInstancingManager).GetField("batches", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
                Check(batches.Count == 4 && BatchingSubmeshes(batches).SetEquals(new[] { 0, 1 }),
                    "UC-T06-10 compatible keys keep actual submesh indices even when slots share the same material resource.");
                manager.ClearOwner(1010);
                Check(manager.InstanceCount == 0 && manager.RenderFrame(camera, false).residentPayloadBytes == 0,
                    "UC-T06-10 owner retirement releases its persistent record arrays and spatial/LOD/material variants.");

                group.fadeMode = LODFadeMode.CrossFade;
                ground = new ScatterApplier.FinalGroundContext(terrainRoot.GetComponent<Terrain>(), data);
                Check(!ground.TryAddInstancedPrefab(manager, 1010, prefab, Vector3.one, Quaternion.identity, Vector3.one, null, -1, -1, null) && manager.InstanceCount == 0,
                    "UC-T06-10 native crossfade/billboard layouts remain on the supported prefab path with no partial GPU registration.");
                manager.enableBatchedRendering = false;
                var library = new ScatterPrototypeLibrary { maxObjectsPerChunk = 2 };
                var definition = new ObjectPrototypeDef { prefab = prefab }; library.objects.Add(definition);
                definition.eco.instancingPolicy = Ecology.InstancingPolicy.Always; definition.eco.colliderPolicy = Ecology.ColliderPolicy.None;
                var outputs = new OutputBag(); outputs.AddScatter("objects", new List<ScatterInstance> { IdentityInstance("batch-world", "objects:batch", "prototype", 8, 8) });
                ScatterApplier.Apply(terrainRoot.GetComponent<Terrain>(), data, outputs, library, null, manager, 1010, null, spawned);
                Check(spawned.Count == 1 && spawned[0].GetComponent<LODGroup>() != null && manager.InstanceCount == 0,
                    "UC-T06-10 unavailable batching preserves accepted procedural placement and native LOD through the existing bounded GameObject consumer.");
            }
            finally
            {
                QualitySettings.lodBias = previousBias; root.GetComponent<GPUInstancingManager>()?.ClearOwner(1010);
                ScatterApplier.Release(spawned, null, 1010); // UC-T06-10: the existing Release argument is the vegetation registry, not the GPU manager.
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraRoot); UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(terrainRoot); UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static HashSet<int> BatchingSubmeshes(IDictionary batches)
        {
            var result = new HashSet<int>();
            foreach (object batch in batches.Values) result.Add((int)batch.GetType().GetField("submesh").GetValue(batch));
            return result;
        }
    }
}
