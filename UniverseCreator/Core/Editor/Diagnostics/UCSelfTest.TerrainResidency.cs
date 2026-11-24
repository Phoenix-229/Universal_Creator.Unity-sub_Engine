// UC-T09-04: native retirement/restoration preserves actual terrain content, not just generation inputs.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTerrainResidency()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC-T09-04 dormant native world"); root.SetActive(false);
            var layerA = new TerrainLayer(); var layerB = new TerrainLayer();
            var detailTexture = new Texture2D(2, 2);
            var tree = GameObject.CreatePrimitive(PrimitiveType.Cube); tree.name = "UC native tree prototype"; tree.SetActive(false);
            Material treeMaterial = null;
            UniverseCreator.Nodes.Graph graph = null;
            TerrainData source = null, restored = null;
            try
            {
                // UC biome acceptance: this cube tests native prototype persistence, not SpeedTree rendering.
                // Unity's terrain prototype validator requires the native soft-occlusion shader for this mesh.
                Shader treeShader = Shader.Find("Nature/Tree Soft Occlusion Leaves");
                Check(treeShader != null, "UC-T09-04 native tree fixture has its required terrain-tree shader.");
                if (treeShader == null) return;
                treeMaterial = new Material(treeShader);
                tree.GetComponent<MeshRenderer>().sharedMaterial = treeMaterial;
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = null; world.generateCollider = true; world.retireDormantTerrain = true; world.maxHeightMeters = 100;
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                var manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var ground = new GameObject("Terrain_0_0"); ground.transform.SetParent(host.transform, false);
                var terrain = ground.AddComponent<UnityEngine.Terrain>(); var collider = ground.AddComponent<TerrainCollider>();
                source = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32),
                    alphamapResolution = 16, baseMapResolution = 32, terrainLayers = new[] { layerA, layerB } };
                source.SetHeights(0, 0, CommitHeights(33, 0.2f));
                float[,] baseline = source.GetHeights(0, 0, 33, 33);
                float[,] sculpt = source.GetHeights(0, 0, 33, 33); sculpt[11, 17] = 0.42f;
                for (int row = 2; row < 31; row++) sculpt[row, 16] = 0.08f; // UC Scene/Play repair: retained river-bed samples.
                source.SetHeights(0, 0, sculpt);
                var holeMap = new bool[32, 32];
                for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) holeMap[z, x] = x != 13 || z != 15;
                source.SetHoles(0, 0, holeMap);
                var alpha = new float[16, 16, 2];
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                { alpha[z, x, 0] = x / 15f; alpha[z, x, 1] = 1 - alpha[z, x, 0]; }
                source.SetAlphamaps(0, 0, alpha);
                source.SetDetailResolution(32, 8); source.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
                source.detailPrototypes = new[] { new DetailPrototype { prototypeTexture = detailTexture,
                    minWidth = 1, maxWidth = 2, minHeight = 1, maxHeight = 2, renderMode = DetailRenderMode.GrassBillboard } };
                var detail = new int[32, 32]; detail[9, 21] = 7; source.SetDetailLayer(0, 0, 0, detail);
                source.treePrototypes = new[] { new TreePrototype { prefab = tree, bendFactor = 0.25f } };
                source.SetTreeInstances(new[] { new TreeInstance { prototypeIndex = 0, position = new Vector3(0.3f, 0.17f, 0.6f),
                    widthScale = 1.2f, heightScale = 0.8f, rotation = 0.7f, color = Color.white, lightmapColor = Color.white } }, false);
                source.wavingGrassAmount = 0.4f; source.wavingGrassSpeed = 0.6f; source.wavingGrassStrength = 0.2f;
                terrain.terrainData = source; collider.terrainData = source;
                var marker = new GameObject("Authored placement"); marker.transform.SetParent(ground.transform, false);
                marker.transform.localPosition = new Vector3(6, 28, 12); Vector3 markerPosition = marker.transform.position;
                float[,] expectedHeight = source.GetHeights(0, 0, 33, 33);
                float[,,] expectedAlpha = source.GetAlphamaps(0, 0, 16, 16);
                int[,] expectedDetail = source.GetDetailLayer(0, 0, 32, 32, 0);
                TreeInstance expectedTree = source.treeInstances[0];
                Type type = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                object chunk = Activator.CreateInstance(type, true);
                SetCommitField(type, chunk, "go", ground); SetCommitField(type, chunk, "terrain", terrain);
                SetCommitField(type, chunk, "data", source); SetCommitField(type, chunk, "resolution", 33);
                SetCommitField(type, chunk, "cell", new Coord(0, 0)); SetCommitField(type, chunk, "appliedHeights", baseline);
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager); pool.Add(chunk);
                ((IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager)).Add(new Coord(0, 0), chunk);
                var retire = typeof(ChunkManager).GetMethod("TryRetireDormantTerrain", flags);
                var restore = typeof(ChunkManager).GetMethod("RestoreDormantTerrain", flags);
                Check(!(bool)retire.Invoke(manager, new[] { (object)world, chunk }) && terrain.terrainData == source,
                    "UC-T09-04 borrowed native terrain is never destroyed by dormant retirement.");
                SetCommitField(type, chunk, "ownsCommittedData", true);
                SetCommitField(type, chunk, "resident", true);
                Check(!(bool)retire.Invoke(manager, new[] { (object)world, chunk }),
                    "UC-T09-04 demanded native terrain cannot retire while resident.");
                SetCommitField(type, chunk, "resident", false);
                var lease = world.ProductMemory.Reserve(0, 100000); SetCommitField(type, chunk, "committedNativeMemory", lease);
                Check((bool)retire.Invoke(manager, new[] { (object)world, chunk }) && terrain.terrainData == null &&
                    collider.terrainData == null && !collider.enabled && source == null && manager.DormantTerrainCount == 1 && manager.DormantTerrainBytes > 0,
                    "UC-T09-04 only completed owned capture detaches/destroys native data and accounts recoverable bytes.");
                long dormantBytes = world.ProductMemory.ManagedBytes;
                // UC-T09-04: native foliage must survive Unity serialization, not merely an in-memory capture.
                var payload = ground.GetComponent<UCTerrainDormantState>();
                string serializedPayload = JsonUtility.ToJson(payload);
                JsonUtility.FromJsonOverwrite(serializedPayload, payload);
                Check(serializedPayload.Contains("PrototypeIndex") && serializedPayload.Contains("NavMeshLod") && serializedPayload.Contains("NoiseSeed"),
                    "UC-T09-04 dormant payload serializes trees and detail prototypes through supported managed records.");
                var stages = ((IEnumerable)restore.Invoke(manager, new[] { (object)world, chunk })).GetEnumerator();
                Check(stages.MoveNext() && terrain.terrainData == null,
                    "UC-T09-04 partial restoration stays detached, with no premature collision or presentation.");
                ((IDisposable)stages).Dispose();
                Check(world.ProductMemory.NativeBytes == 0 && world.ProductMemory.ManagedBytes == dormantBytes &&
                    manager.DormantTerrainCount == 1 && ground.GetComponent<UCTerrainDormantState>() != null,
                    "UC-T09-04 cancellation retires staging reservations and preserves the only recoverable payload.");
                // UC-T09-04: actual pump admission must hold a refused payload and retry after another owner releases memory.
                SetCommitField(type, chunk, "resident", true);
                typeof(ChunkManager).GetField("built", flags).SetValue(manager, true);
                var pump = typeof(ChunkManager).GetMethod("PumpTerrainResidency", flags);
                Action advanceResidency = () =>
                {
                    typeof(ChunkManager).GetField("commitPumpStarted", flags).SetValue(manager, Time.realtimeSinceStartupAsDouble);
                    typeof(ChunkManager).GetField("commitPumpSteps", flags).SetValue(manager, 0);
                    pump.Invoke(manager, new[] { (object)world });
                };
                var pressure = world.ProductMemory.Reserve(world.ProductMemory.LimitBytes - world.ProductMemory.ManagedBytes - world.ProductMemory.NativeBytes);
                try
                {
                    advanceResidency();
                    string failure = manager.ReadinessDiagnostic;
                    advanceResidency();
                    Check(failure.Length > 0 && manager.ReadinessDiagnostic == failure && terrain.terrainData == null &&
                        ground.GetComponent<UCTerrainDormantState>() != null && world.ProductMemory.NativeBytes == 0,
                        "UC-T09-04 failed native admission exposes the landing hold and preserves recovery without unchanged-frame retries.");
                }
                finally { pressure.Dispose(); }
                var version = typeof(UCTerrainDormantState).GetField("version", flags);
                version.SetValue(payload, 0);
                advanceResidency(); // Increased headroom permits one attempt; malformed recovery is still refused.
                Check(manager.ReadinessDiagnostic.Contains("version") && terrain.terrainData == null && manager.DormantTerrainCount == 1,
                    "UC-T09-04 increased headroom retries admission but never publishes an invalid saved payload.");
                version.SetValue(payload, 1);
                Check(manager.RetryTerrainResidency() == 1 && terrain.terrainData == null && world.ProductMemory.NativeBytes == 0,
                    "UC-T09-04 explicit retry clears the corrected hold without synchronous uploads, activation or regeneration.");
                for (int attempt = 0; attempt < 64 && ground.GetComponent<UCTerrainDormantState>() != null; attempt++) advanceResidency();
                restored = terrain.terrainData;
                Check(restored != null && collider.terrainData == restored && manager.DormantTerrainCount == 0 && manager.DormantTerrainBytes == 0 &&
                    ground.GetComponent<UCTerrainDormantState>() == null && marker.transform.position == markerPosition &&
                    ReferenceEquals(type.GetField("appliedHeights").GetValue(chunk), baseline),
                    "UC-T09-04 restoration preserves authored placement/baseline identity and releases dormant bytes only after native publication.");
                var actualHeight = restored.GetHeights(0, 0, 33, 33); var actualHole = restored.GetHoles(0, 0, 32, 32);
                var actualAlpha = restored.GetAlphamaps(0, 0, 16, 16); var actualDetail = restored.GetDetailLayer(0, 0, 32, 32, 0);
                bool equalHeights = true, equalHoles = true, equalAlphas = true, equalDetails = true;
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) equalHeights &= actualHeight[z, x] == expectedHeight[z, x];
                for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++)
                { equalHoles &= actualHole[z, x] == holeMap[z, x]; equalDetails &= actualDetail[z, x] == expectedDetail[z, x]; }
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) for (int channel = 0; channel < 2; channel++)
                    equalAlphas &= actualAlpha[z, x, channel] == expectedAlpha[z, x, channel];
                Check(equalHeights && equalHoles && equalAlphas && equalDetails,
                    "UC-T09-04 all native sculpt/paint/hole/detail samples survive the owned retirement round trip exactly.");
                TreeInstance actualTree = restored.treeInstances[0];
                Check(actualTree.position == expectedTree.position && actualTree.rotation == expectedTree.rotation &&
                    actualTree.widthScale == expectedTree.widthScale && actualTree.heightScale == expectedTree.heightScale &&
                    restored.terrainLayers[0] == layerA && restored.terrainLayers[1] == layerB && restored.treePrototypes[0].prefab == tree &&
                    restored.detailPrototypes[0].prototypeTexture == detailTexture && restored.detailResolutionPerPatch == 8 &&
                    restored.baseMapResolution == 32 && restored.wavingGrassAmount == 0.4f && restored.wavingGrassSpeed == 0.6f,
                    $"UC-T09-04 shared prototypes/settings and unsnapped native tree transforms remain unchanged. tree={actualTree.position}/{expectedTree.position}, rotation={actualTree.rotation}/{expectedTree.rotation}, scale={actualTree.widthScale}/{actualTree.heightScale}, patch={restored.detailResolutionPerPatch}, base={restored.baseMapResolution}, grass={restored.wavingGrassAmount}/{restored.wavingGrassSpeed}");
                // UC Scene/Play repair: serialized dormant recovery has no managed bake/sculpt baseline after a domain reload.
                graph = ScriptableObject.CreateInstance<UniverseCreator.Nodes.Graph>(); world.graph = graph;
                SetCommitField(type, chunk, "resident", false);
                Check((bool)retire.Invoke(manager, new[] { (object)world, chunk }),
                    "UC Scene/Play repair: accepted native river ground completes a second recoverable retirement.");
                SetCommitField(type, chunk, "appliedHeights", null); SetCommitField(type, chunk, "restoredAcceptedData", null);
                SetCommitField(type, chunk, "bakedThisSession", false);
                var reopened = ((IEnumerable)restore.Invoke(manager, new[] { (object)world, chunk })).GetEnumerator();
                try { while (reopened.MoveNext()) { } }
                finally { ((IDisposable)reopened).Dispose(); }
                restored = terrain.terrainData;
                bool recoveredHeights = restored != null;
                if (recoveredHeights)
                {
                    var reopenedHeights = restored.GetHeights(0, 0, 33, 33);
                    for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) recoveredHeights &= reopenedHeights[z, x] == expectedHeight[z, x];
                }
                Check(recoveredHeights && manager.TryGetChunkReadiness(new Coord(0, 0), out var recovered) && recovered.TerrainAccepted &&
                    (TerrainData)type.GetField("restoredAcceptedData").GetValue(chunk) == restored && !(bool)type.GetField("bakedThisSession").GetValue(chunk),
                    "UC Scene/Play repair: dormant reload accepts the recovered native identity and every sculpt/river sample without certifying a pristine bake.");
                typeof(ChunkManager).GetMethod("ReleaseAcceptedMemory", flags).Invoke(manager, new[] { chunk });
                // UC midday acceptance: report the actual outstanding owner before changing retirement behavior.
                Check(world.ProductMemory.ManagedBytes == 0 && world.ProductMemory.NativeBytes == 0 && layerA != null && layerB != null && tree != null,
                    $"UC-T09-04 owner teardown releases native/dormant reservations while borrowed layers/prototypes survive. managed={world.ProductMemory.ManagedBytes}, native={world.ProductMemory.NativeBytes}, layers={layerA != null}/{layerB != null}, tree={tree != null}, playing={Application.isPlaying}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
                if (restored != null) UnityEngine.Object.DestroyImmediate(restored);
                UnityEngine.Object.DestroyImmediate(tree); UnityEngine.Object.DestroyImmediate(detailTexture);
                if (treeMaterial != null) UnityEngine.Object.DestroyImmediate(treeMaterial);
                if (graph != null) UnityEngine.Object.DestroyImmediate(graph);
                UnityEngine.Object.DestroyImmediate(layerA); UnityEngine.Object.DestroyImmediate(layerB);
            }
        }
    }
}
