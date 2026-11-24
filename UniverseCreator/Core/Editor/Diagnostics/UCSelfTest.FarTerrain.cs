// UC-T09-04: actual distant uploads, native geometry fidelity and display-only ownership.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestFarTerrain()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var root = new GameObject("UC-T09-04 distant terrain world"); root.SetActive(false);
            var layerA = new TerrainLayer(); var layerB = new TerrainLayer();
            TerrainData original = null, nearData = null, displayData = null, restoredNear = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = null; world.retireDormantTerrain = true; world.farTerrainRadiusCells = 4;
                world.chunkCountPerSide = 16; world.worldSizeKm = 0.512f; world.maxHeightMeters = 100;
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                var manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var ground = new GameObject("Terrain_1_0"); ground.transform.SetParent(host.transform, false);
                ground.transform.position = new Vector3(32, 0, 0);
                var terrain = ground.AddComponent<UnityEngine.Terrain>();
                original = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32),
                    alphamapResolution = 256, baseMapResolution = 256, terrainLayers = new[] { layerA, layerB } };
                original.SetHeights(0, 0, CommitHeights(33, 0.2f));
                float[,] sculpt = original.GetHeights(0, 0, 33, 33); sculpt[9, 12] = 0.47f; original.SetHeights(0, 0, sculpt);
                var holes = new bool[32, 32];
                for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) holes[z, x] = z != 7 || x != 11;
                original.SetHoles(0, 0, holes);
                var alpha = new float[256, 256, 2];
                for (int z = 0; z < 256; z++) for (int x = 0; x < 256; x++)
                { alpha[z, x, 0] = (x & 1) == 0 && (z & 1) == 0 ? 1 : 0; alpha[z, x, 1] = 1 - alpha[z, x, 0]; }
                original.SetAlphamaps(0, 0, alpha); terrain.terrainData = original;
                float[,] expectedHeights = original.GetHeights(0, 0, 33, 33);
                var capture = typeof(UCTerrainDormantState).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
                var payload = (UCTerrainDormantState)capture.Invoke(null, new object[] { terrain }); terrain.terrainData = null;
                Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                Type viewType = typeof(ChunkManager).GetNestedType("FarTerrainView", BindingFlags.NonPublic);
                object chunk = Activator.CreateInstance(chunkType, true);
                SetCommitField(chunkType, chunk, "cell", new Coord(1, 0)); SetCommitField(chunkType, chunk, "go", ground);
                SetCommitField(chunkType, chunk, "terrain", terrain); SetCommitField(chunkType, chunk, "dormantTerrain", payload);
                SetCommitField(chunkType, chunk, "dormantChecked", true);
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager); pool.Add(chunk);
                var cells = (IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager); cells.Add(new Coord(1, 0), chunk);
                typeof(ChunkManager).GetField("centreCell", flags).SetValue(manager, new Coord(0, 0));
                typeof(ChunkManager).GetField("built", flags).SetValue(manager, true);
                var prepare = typeof(ChunkManager).GetMethod("PrepareFarTerrain", flags);
                var dispose = typeof(ChunkManager).GetMethod("DisposeFarCommit", flags);
                Func<object> newView = () =>
                {
                    object view = Activator.CreateInstance(viewType, true);
                    viewType.GetField("Chunk", fields).SetValue(view, chunk); viewType.GetField("Source", fields).SetValue(view, payload);
                    return view;
                };
                object partial = newView();
                var stages = ((IEnumerable)prepare.Invoke(manager, new[] { (object)world, partial })).GetEnumerator();
                typeof(ChunkManager).GetField("preparingFarTerrain", flags).SetValue(manager, partial);
                typeof(ChunkManager).GetField("farTerrainCommit", flags).SetValue(manager, stages);
                Check(stages.MoveNext() && manager.FarTerrainCount == 0 && terrain.terrainData == null && manager.FarTerrainBytes > 0,
                    "UC-T09-04 preparing a distant terrain charges native bytes without publishing ground or collision.");
                dispose.Invoke(manager, null);
                Check(manager.FarTerrainBytes == 0 && world.ProductMemory.NativeBytes == 0 && manager.DormantTerrainCount == 1 && payload != null,
                    "UC-T09-04 cancelled distant allocation releases native/scratch bytes and preserves re-admitted serialized recovery.");
                int authoredBudget = world.farTerrainBudgetMiB; world.farTerrainBudgetMiB = 0;
                object refused = newView(); stages = ((IEnumerable)prepare.Invoke(manager, new[] { (object)world, refused })).GetEnumerator();
                bool capRefused = false;
                try { stages.MoveNext(); } catch (InvalidOperationException) { capRefused = true; }
                finally { ((IDisposable)stages).Dispose(); world.farTerrainBudgetMiB = authoredBudget; }
                Check(capRefused && manager.FarTerrainBytes == 0 && world.ProductMemory.NativeBytes == 0 && payload != null,
                    "UC-T09-04 authored distant cap refuses before native allocation without losing the original payload.");
                int savedResolution = (int)typeof(UCTerrainDormantState).GetField("alphaResolution", flags).GetValue(payload);
                typeof(UCTerrainDormantState).GetField("alphaResolution", flags).SetValue(payload, 255);
                bool malformed = false;
                try { typeof(UCTerrainDormantState).GetMethod("ValidatePayload", flags).Invoke(payload, null); }
                catch (TargetInvocationException error) { malformed = error.InnerException is InvalidOperationException; }
                finally { typeof(UCTerrainDormantState).GetField("alphaResolution", flags).SetValue(payload, savedResolution); }
                Check(malformed, "UC-T09-04 malformed non-native alpha dimensions cannot silently drop filter samples.");
                displayData = new TerrainData(); int filterYields = 0;
                var uploads = (IEnumerable)typeof(UCTerrainDormantState).GetMethod("RestoreFarStages", flags).Invoke(payload, new object[] { displayData });
                foreach (ChunkCommitStep step in uploads) if (step.Stage == ChunkCommitStage.PrepareTextures) filterYields++;
                var actualHeights = displayData.GetHeights(0, 0, 33, 33); var actualHoles = displayData.GetHoles(0, 0, 32, 32);
                bool exact = true;
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) exact &= actualHeights[z, x] == expectedHeights[z, x];
                for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) exact &= actualHoles[z, x] == holes[z, x];
                Check(exact && displayData.heightmapResolution == 33 && displayData.size == original.size,
                    "UC-T09-04 distant uploads retain every accepted sculpt/edge/hole sample and world dimensions exactly.");
                var filtered = displayData.GetAlphamaps(0, 0, 128, 128); bool areaAverage = true;
                for (int z = 0; z < 128; z++) for (int x = 0; x < 128; x++)
                    areaAverage &= Mathf.Abs(filtered[z, x, 0] - 0.25f) < 0.005f && Mathf.Abs(filtered[z, x, 1] - 0.75f) < 0.005f;
                Check(areaAverage && filterYields >= 2 && displayData.alphamapResolution == 128 && displayData.baseMapResolution == 128,
                    "UC-T09-04 distant painting area-averages all source pixels, yielding bounded filtering rather than dropping alternate samples.");
                Check(displayData.treeInstanceCount == 0 && displayData.detailPrototypes.Length == 0 &&
                    displayData.terrainLayers[0] == layerA && displayData.terrainLayers[1] == layerB,
                    "UC-T09-04 distant detail omits native foliage while borrowing the existing surface-layer owners.");
                object complete = newView(); viewType.GetField("Data", fields).SetValue(complete, displayData);
                // UC-T09-04: charge the manually published test view as the production preparation path does.
                long farBytes = (long)typeof(UCTerrainDormantState).GetProperty("FarNativeBytes", flags).GetValue(payload);
                viewType.GetField("Memory", fields).SetValue(complete, world.ProductMemory.Reserve(0, farBytes));
                viewType.GetField("Bytes", fields).SetValue(complete, farBytes);
                typeof(ChunkManager).GetField("farTerrainBytes", flags).SetValue(manager, farBytes);
                typeof(ChunkManager).GetMethod("PublishFarTerrain", flags).Invoke(manager, new[] { complete });
                var display = (GameObject)viewType.GetField("Holder", fields).GetValue(complete);
                var farTerrain = (UnityEngine.Terrain)viewType.GetField("Terrain", fields).GetValue(complete);
                Check(manager.FarTerrainCount == 1 && display.transform.parent == null && display.scene == host.scene && !display.activeSelf &&
                    (display.hideFlags & HideFlags.DontSave) != 0 && display.GetComponent<TerrainCollider>() == null &&
                    root.GetComponentsInChildren<UnityEngine.Terrain>(true).Length == 1 && terrain.terrainData == null,
                    "UC-T09-04 distant display stays unsaved, detached from world ground/hydrology ownership and unable to certify collision.");
                var nearHolder = new GameObject("Terrain_0_0"); nearHolder.transform.SetParent(host.transform, false);
                var near = nearHolder.AddComponent<UnityEngine.Terrain>(); nearData = new TerrainData { heightmapResolution = 33, size = original.size };
                nearData.SetHeights(0, 0, CommitHeights(33, 0.2f)); near.terrainData = nearData;
                object nearChunk = Activator.CreateInstance(chunkType, true);
                SetCommitField(chunkType, nearChunk, "cell", new Coord(0, 0)); SetCommitField(chunkType, nearChunk, "go", nearHolder);
                SetCommitField(chunkType, nearChunk, "terrain", near); SetCommitField(chunkType, nearChunk, "data", nearData);
                cells.Add(new Coord(0, 0), nearChunk); pool.Add(nearChunk);
                root.SetActive(true); ground.SetActive(false); display.SetActive(true);
                var link = typeof(ChunkManager).GetMethod("LinkFarTerrainNeighbours", flags); link.Invoke(manager, null);
                bool observed = manager.TryGetChunkReadiness(new Coord(1, 0), out var ready);
                Check(near.rightNeighbor == farTerrain && farTerrain.leftNeighbor == near &&
                    nearData.GetHeights(0, 0, 33, 33)[9, 12] == expectedHeights[0, 0],
                    $"UC-T09-04 near/far render links are reciprocal without height edits. nearRight={near.rightNeighbor == farTerrain}, farLeft={farTerrain.leftNeighbor == near}, height={nearData.GetHeights(0, 0, 33, 33)[9, 12]}/{expectedHeights[0, 0]}");
                Check(observed && !ready.TerrainAccepted && !ready.CollisionReady,
                    "UC-T09-04 distant rendering cannot certify accepted ground or collision readiness.");
                // Leave one byte less than a near replacement needs: its own far copy must release first.
                long payloadBytes = (long)typeof(UCTerrainDormantState).GetProperty("PayloadBytes", flags).GetValue(payload);
                long scratchBytes = (long)typeof(UCTerrainDormantState).GetProperty("UploadScratchBytes", flags).GetValue(payload);
                long nearBytes = payloadBytes * 2 + scratchBytes + 64 + 33L * 33 * sizeof(float);
                var pressure = world.ProductMemory.Reserve(world.ProductMemory.LimitBytes - world.ProductMemory.ManagedBytes -
                    world.ProductMemory.NativeBytes - nearBytes + 1);
                try
                {
                    var restore = (IEnumerable)typeof(ChunkManager).GetMethod("RestoreDormantTerrain", flags).Invoke(manager, new[] { (object)world, chunk });
                    foreach (ChunkCommitStep step in restore) { }
                    restoredNear = terrain.terrainData;
                    Check(restoredNear != null && restoredNear.GetHeights(12, 9, 1, 1)[0, 0] == expectedHeights[9, 12] &&
                        manager.DormantTerrainCount == 0 && manager.FarTerrainBytes == 0,
                        "UC-T09-04 near restoration releases its own distant reservation before budget admission and restores accepted sculpt exactly.");
                }
                finally { pressure.Dispose(); }
                Check(manager.FarTerrainCount == 0 && near.rightNeighbor == null && display == null && displayData == null && payload == null && layerA != null,
                    "UC-T09-04 successful near replacement removes stale render links and consumes recovery only after native restoration; borrowed layers survive.");
                typeof(ChunkManager).GetMethod("ReleaseAcceptedMemory", flags).Invoke(manager, new[] { chunk });
                Check(world.ProductMemory.ManagedBytes == 0 && world.ProductMemory.NativeBytes == 0,
                    "UC-T09-04 near replacement teardown retires all recovery/display/native reservations under shared-budget pressure.");
                manager.CancelAll();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
                if (nearData != null) UnityEngine.Object.DestroyImmediate(nearData);
                if (displayData != null) UnityEngine.Object.DestroyImmediate(displayData);
                if (restoredNear != null) UnityEngine.Object.DestroyImmediate(restoredNear);
                UnityEngine.Object.DestroyImmediate(layerA); UnityEngine.Object.DestroyImmediate(layerB);
            }
        }
    }
}
