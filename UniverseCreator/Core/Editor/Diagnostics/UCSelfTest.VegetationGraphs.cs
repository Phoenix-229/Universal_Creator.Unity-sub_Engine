// UC-T05-03: actual terrain publication, admitted companion workers and the native scatter pump.
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationGraphBinding()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC companion vegetation acceptance"); root.SetActive(false);
            var main = ScriptableObject.CreateInstance<Graph>();
            var graph = ScriptableObject.CreateInstance<Graph>();
            var foreign = ScriptableObject.CreateInstance<Graph>();
            var library = new ScatterPrototypeLibrary(); // UC-T05-03: this serialized world-owned library is managed data, not a native asset.
            var detailTexture = new Texture2D(2, 2);
            var original = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
            original.SetDetailResolution(32, 8); // UC-T05-03: native detail consumption needs an actual initialized grid in the fixture baseline.
            var context = new SnapshotRetirementContext();
            SynchronizationContext savedContext = SynchronizationContext.Current;
            ChunkManager manager = null;
            UCWorldGraphBindings bindings = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                main.SetDomain(UCGraphDomain.TerrainWater); foreign.SetDomain(UCGraphDomain.TerrainWater);
                graph.SetDomain(UCGraphDomain.Vegetation);
                var constant = new ConstantNode { id = "companion-main-height", value = 0.65f };
                var heightOutput = new HeightOutputNode { id = "companion-main-output" };
                main.nodes.Add(constant); main.nodes.Add(heightOutput);
                main.connections.Add(new Connection(constant.id, 0, heightOutput.id, 0));
                var input = new WorldGraphInputNode { id = "companion-accepted-ground" };
                var trees = new TreeScatterNode { id = "companion-trees", density = 20000, minSpacing = 4, maxSlope = 90 };
                var shrubs = new ShrubScatterNode { id = "companion-shrubs", density = 20000, minSpacing = 4, maxSlope = 90, clumping = 0 };
                var grass = new GrassDetailScatterNode { id = "companion-grass", maxSlope = 90, patchiness = 0 };
                graph.nodes.Add(input);
                foreach (Generator output in new Generator[] { trees, shrubs, grass })
                {
                    graph.nodes.Add(output);
                    graph.connections.Add(new Connection(input.id, 0, output.id, 0));
                    graph.connections.Add(new Connection(input.id, 1, output.id, 1));
                }
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false;
                world.autoRegenerate = false; world.graph = main; world.worldSizeKm = 0.032f;
                world.chunkCountPerSide = 1; world.maxHeightMeters = 100; world.enableGPUInstancing = false;
                bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.VegetationGraph = graph;
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                typeof(UniverseCreatorObject).GetField("chunkManager", flags).SetValue(world, manager);
                Check(!WaitVegetationRun(bindings.RunVegetationAsync(), context) && bindings.VegetationStatus.Contains("prototypes"),
                    "UC-T05-03 missing licensed prototypes report pending without a terrain run.");

                var treePrefab = GameObject.CreatePrimitive(PrimitiveType.Cube); treePrefab.name = "Tree test prototype";
                treePrefab.transform.SetParent(root.transform, false);
                var shrubPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube); shrubPrefab.name = "Shrub test prototype";
                shrubPrefab.transform.SetParent(root.transform, false);
                library.trees.Add(new TreePrototypeDef { prefab = treePrefab });
                library.shrubs.Add(new ShrubPrototypeDef { prefab = shrubPrefab });
                library.details.Add(new DetailPrototypeDef { billboardTexture = detailTexture });
                world.scatterLibrary = library;
                Check(!WaitVegetationRun(bindings.RunVegetationAsync(), context) && bindings.VegetationStatus.Contains("accepted main-world terrain"),
                    "UC-T05-03 a selected graph cannot silently rebuild missing accepted ground.");

                var terrainObject = new GameObject("Terrain_0_0"); terrainObject.transform.SetParent(root.transform, false);
                var terrain = terrainObject.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = original;
                var collider = terrainObject.AddComponent<TerrainCollider>(); collider.terrainData = original;
                Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                object chunk = Activator.CreateInstance(chunkType, true);
                SetCommitField(chunkType, chunk, "go", terrainObject); SetCommitField(chunkType, chunk, "terrain", terrain);
                SetCommitField(chunkType, chunk, "data", original); SetCommitField(chunkType, chunk, "resolution", 33);
                SetCommitField(chunkType, chunk, "cell", new Coord(0, 0)); SetCommitField(chunkType, chunk, "lod", LODLevel.High);
                ((IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager)).Add(chunk);
                ((IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager)).Add(new Coord(0, 0), chunk);
                QueueCommitFixture(world, chunkType, chunk, 0, 0.65f);
                ((TileData)chunkType.GetField("tile").GetValue(chunk)).outputs.heightmap = CommitHeights(33, 0.65f);
                var stages = (System.Collections.Generic.IEnumerable<ChunkCommitStep>)typeof(ChunkManager).GetMethod("StageChunk", flags)
                    .Invoke(manager, new[] { (object)world, chunk });
                foreach (var stage in stages) { }
                ((TileData)chunkType.GetField("tile").GetValue(chunk)).ReleaseProductMemory();
                SetCommitField(chunkType, chunk, "tile", null); SetCommitField(chunkType, chunk, "applied", true);
                SetCommitField(chunkType, chunk, "inFlight", false); SetCommitField(chunkType, chunk, "workerDone", false);
                root.SetActive(true);
                GenerationRunId terrainRun = world.GenerationScheduler.CurrentRun;
                float[,] heights = terrain.terrainData.GetHeights(0, 0, 33, 33);
                Check(WaitVegetationRun(UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.Run(bindings), context) && bindings.VegetationRevision == 1,
                    "UC-T05-03 explicit Run accepts vegetation against the real committed native terrain.");
                manager.PumpCompleted();
                var accepted = (OutputBag)chunkType.GetField("retainedScatter").GetValue(chunk);
                // UC-T06-15: exercise the controller used by both the manager and graph window against actual accepted native planting.
                Check(!UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.HasPendingChanges(bindings),
                    "UC-T06-15 shared graph/manager authoring records the explicitly accepted whole-world inputs.");
                EditorUtility.SetDirty(graph);
                Check(UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.HasPendingChanges(bindings) && bindings.VegetationRevision == 1 &&
                    ReferenceEquals(accepted, chunkType.GetField("retainedScatter").GetValue(chunk)),
                    "UC-T06-15 graph edits become pending without an implicit Run or replacement of accepted planting.");
                Check(accepted.scatter.TryGetValue("trees", out var plantedTrees) && plantedTrees.Count > 0 &&
                    accepted.scatter.TryGetValue("shrubs", out var plantedShrubs) && plantedShrubs.Count > 0 &&
                    terrain.terrainData.treeInstanceCount > 0 && ((IList)chunkType.GetField("scatterObjects").GetValue(chunk)).Count > 0 &&
                    terrain.terrainData.GetDetailLayer(16, 16, 1, 1, 0)[0, 0] > 0,
                    "UC-T05-03 trees, shrubs and ground cover reach their actual native scatter consumers.");
                Check(world.GenerationScheduler.CurrentRun.Equals(terrainRun) &&
                    terrain.terrainData.GetHeights(0, 0, 1, 1)[0, 0] == heights[0, 0],
                    "UC-T05-03 vegetation application preserves the terrain revision and exact accepted heights.");
                TestLightingGraphBinding(world); // UC-T05-04: reuse the real accepted terrain fixture for both companion domains.
                TestWorldGraphProducts(world, terrain, accepted); // UC-T05-06: real material consumers share the same accepted terrain and planting.

                // UC-T06-03: the region path uses the same accepted cells/publication gate and leaves missing regions and ground untouched.
                Check(!WaitVegetationRun(bindings.RunVegetationRegionAsync(new Coord(11, 11)), context) && bindings.VegetationRevision == 1 &&
                    ReferenceEquals(accepted, chunkType.GetField("retainedScatter").GetValue(chunk)),
                    "UC-T06-03 a nonresident requested region remains pending without erasing accepted planting.");
                Check(WaitVegetationRun(bindings.RunVegetationRegionAsync(Coord.Zero), context) && bindings.VegetationRevision == 2 &&
                    world.GenerationScheduler.CurrentRun.Equals(terrainRun) && terrain.terrainData.GetHeights(0, 0, 1, 1)[0, 0] == heights[0, 0],
                    "UC-T06-03 explicit resident-region Run reuses the same graph, world revision and exact native ground.");
                manager.PumpCompleted(); accepted = (OutputBag)chunkType.GetField("retainedScatter").GetValue(chunk);
                var region = manager.CaptureGraphVegetationRegions(terrainRun, Coord.Zero)[0];
                using (var invalidRegion = new GraphVegetationResult { Region = region, Outputs = new OutputBag() })
                {
                    Check(!manager.AreGraphVegetationRegionsCurrent(new[] { invalidRegion }, terrainRun),
                        "UC-T06-03 placement without the exact accepted world/revision is rejected before native publication.");
                    invalidRegion.Outputs = accepted; region.Position += Vector3.right;
                    Check(!manager.AreGraphVegetationRegionsCurrent(new[] { invalidRegion }, terrainRun),
                        "UC-T06-03 moved region geometry cannot publish a worker's stale placement.");
                }
                var beforePatch = accepted;
                Check(!WaitVegetationRun(bindings.RunVegetationPatchAsync(new Rect(24, 8, 16, 8)), context) && bindings.VegetationRevision == 2 &&
                    ReferenceEquals(beforePatch, chunkType.GetField("retainedScatter").GetValue(chunk)),
                    "UC-T06-03 a partly unloaded patch remains pending and preserves accepted placement/revision.");
                Check(WaitVegetationRun(bindings.RunVegetationPatchAsync(new Rect(8, 8, 8, 8)), context) && bindings.VegetationRevision == 3,
                    "UC-T06-03 a world-metre patch uses the existing admitted worker and native region acceptance path.");
                manager.PumpCompleted(); accepted = (OutputBag)chunkType.GetField("retainedScatter").GetValue(chunk);
                Check(beforePatch.scatter["trees"].OrderBy(i => i.instanceId).SequenceEqual(accepted.scatter["trees"].OrderBy(i => i.instanceId)) &&
                    world.GenerationScheduler.CurrentRun.Equals(terrainRun) && terrain.terrainData.GetHeights(0, 0, 1, 1)[0, 0] == heights[0, 0],
                    "UC-T06-03 unchanged authoring patched through native consumers preserves all tree identities/transforms and exact accepted ground.");
                long initialVegetationRevision = bindings.VegetationRevision;
                Check(UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.HasPendingChanges(bindings),
                    "UC-T06-15 regional and patch acceptance cannot claim every resident region matches current authoring.");

                trees.density = 0;
                Check(ReferenceEquals(accepted, chunkType.GetField("retainedScatter").GetValue(chunk)) && bindings.VegetationRevision == initialVegetationRevision,
                    "UC-T05-03 authored edits cannot replace accepted planting without Run.");
                Task<bool> cancelled = bindings.RunVegetationAsync(); bindings.CancelVegetation();
                Check(!WaitVegetationRun(cancelled, context) && bindings.VegetationRevision == initialVegetationRevision &&
                    ReferenceEquals(accepted, chunkType.GetField("retainedScatter").GetValue(chunk)),
                    "UC-T05-03 Cancel retires the candidate while preserving the accepted plan and planting.");

                Task<bool> replaced = bindings.RunVegetationAsync();
                Task<bool> replacement = bindings.RunVegetationAsync();
                Check(!WaitVegetationRun(replaced, context) && WaitVegetationRun(replacement, context) && bindings.VegetationRevision == initialVegetationRevision + 1 &&
                    bindings.VegetationStatus.StartsWith("Accepted vegetation", StringComparison.Ordinal),
                    "UC-T05-03 a superseded Run cannot overwrite the replacement revision or its visible status.");
                input.mainGraph = foreign;
                // Runtime validation also rejects programmatic foreign selectors; no second world's terrain is evaluated.
                Check(!WaitVegetationRun(bindings.RunVegetationAsync(), context) && bindings.VegetationRevision == initialVegetationRevision + 1 &&
                    bindings.VegetationStatus.Contains("foreign/unbuilt"),
                    "UC-T05-03 a foreign main selector is rejected while the previously accepted plan remains available.");
                input.mainGraph = null;
                world.GenerationScheduler.BeginRun(); bindings.RefreshVegetationDependencies();
                Check(bindings.VegetationStatus.StartsWith("Stale:", StringComparison.Ordinal) && bindings.VegetationRevision == initialVegetationRevision + 1,
                    "UC-T05-03 a changed terrain revision visibly marks vegetation stale without applying authored edits.");
                bool wrongDomain = false;
                try { bindings.VegetationGraph = main; } catch (ArgumentException) { wrongDomain = true; }
                Check(wrongDomain && ReferenceEquals(bindings.VegetationGraph, graph),
                    "UC-T05-03 incompatible programmatic binding preserves the valid vegetation asset.");
                TestVegetationBindingPersistence(main, graph);
            }
            finally
            {
                bindings?.CancelVegetation();
                UnityEngine.Object.DestroyImmediate(root); context.Drain();
                UnityEngine.Object.DestroyImmediate(original); UnityEngine.Object.DestroyImmediate(detailTexture);
                UnityEngine.Object.DestroyImmediate(main); // UC-T05-03: managed prototype definitions have no native Destroy lifecycle.
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(foreign);
                SynchronizationContext.SetSynchronizationContext(savedContext);
            }
        }

        private static void TestVegetationBindingPersistence(Graph main, Graph vegetation)
        {
            const string folder = "Assets/__UC_T05_03_BindingAcceptance";
            Scene originalScene = SceneManager.GetActiveScene();
            bool single = Application.isBatchMode && string.IsNullOrEmpty(originalScene.path); // UC-T05-03: never discard an interactive author's scene.
            Scene fixture = default;
            bool createdFolder = false;
            Graph savedMain = null, savedVegetation = null, savedLighting = null;
            try
            {
                if (AssetDatabase.IsValidFolder(folder)) throw new InvalidOperationException("UC-T05-03 fixture path is already occupied.");
                AssetDatabase.CreateFolder("Assets", "__UC_T05_03_BindingAcceptance");
                createdFolder = true;
                savedMain = UnityEngine.Object.Instantiate(main); savedVegetation = UnityEngine.Object.Instantiate(vegetation);
                // UC-T05-04: all companion references and spatial sampling targets must survive the same native world lifetime.
                savedLighting = ScriptableObject.CreateInstance<Graph>(); savedLighting.SetDomain(UCGraphDomain.LightingClimateWeather);
                savedLighting.AddNode(new UniverseCreator.Nodes.Outputs.WeatherOutputNode());
                AssetDatabase.CreateAsset(savedMain, folder + "/Main.asset");
                AssetDatabase.CreateAsset(savedVegetation, folder + "/Vegetation.asset");
                AssetDatabase.CreateAsset(savedLighting, folder + "/Lighting.asset");
                // UC-T05-03: isolated batch sessions begin with an unsaved untitled scene; use the established native reload pattern.
                fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
                // UC-T04-11: a Single scene switch can unload native assets despite managed local references; reacquire saved identities.
                savedMain = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Main.asset");
                savedVegetation = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Vegetation.asset");
                savedLighting = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Lighting.asset");
                Check(savedMain != null && savedVegetation != null,
                    "UC-T05-03 saved graph assets remain loadable before assigning the scene binding.");
                var savedRoot = new GameObject("Saved UC companion binding"); savedRoot.SetActive(false);
                SceneManager.MoveGameObjectToScene(savedRoot, fixture);
                var world = savedRoot.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false; world.graph = savedMain;
                var bindings = savedRoot.AddComponent<UCWorldGraphBindings>(); bindings.VegetationGraph = savedVegetation;
                bindings.LightingGraph = savedLighting;
                var sampleTarget = new GameObject("Saved environment sampling target"); sampleTarget.transform.SetParent(savedRoot.transform, false);
                sampleTarget.transform.localPosition = new Vector3(3, 4, 5); bindings.EnvironmentSampleTarget = sampleTarget.transform;
                Undo.IncrementCurrentGroup(); Undo.RecordObject(bindings, "UC-T05-04 environment binding acceptance");
                bindings.LightingGraph = null; bindings.EnvironmentSampleTarget = null; Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Check(bindings.LightingGraph == savedLighting && bindings.EnvironmentSampleTarget == sampleTarget.transform,
                    "UC-T05-04 Undo restores the exact environment asset and spatial target without a Run.");
                Undo.PerformRedo();
                Check(bindings.LightingGraph == null && bindings.EnvironmentSampleTarget == null && bindings.LightingRevision == 0,
                    "UC-T05-04 Redo preserves the authored unbinding without executing the environment graph.");
                bindings.LightingGraph = savedLighting; bindings.EnvironmentSampleTarget = sampleTarget.transform;
                AssetDatabase.SaveAssets();
                Check(EditorSceneManager.SaveScene(fixture, folder + "/Bindings.unity"),
                    "UC-T05-03 bindings save through supported native scene/asset APIs.");
                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else EditorSceneManager.CloseScene(fixture, true);
                fixture = EditorSceneManager.OpenScene(folder + "/Bindings.unity", single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                var restored = fixture.GetRootGameObjects()[0].GetComponent<UCWorldGraphBindings>();
                savedMain = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Main.asset");
                savedVegetation = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Vegetation.asset");
                savedLighting = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Lighting.asset");
                Check(restored != null && restored.SchemaVersion == 1 && restored.VegetationRevision == 0,
                    "UC-T05-03 actual scene reload resets the accepted runtime revision without changing the binding schema.");
                Check(restored != null && restored.Owner != null && savedMain != null && savedVegetation != null &&
                    restored.Owner.graph == savedMain && restored.VegetationGraph == savedVegetation &&
                    savedMain.Domain == UCGraphDomain.TerrainWater && savedVegetation.Domain == UCGraphDomain.Vegetation,
                    "UC-T05-03 actual scene reload preserves graph/world references and requires explicit capture for the new runtime lifetime.");
                Check(restored != null && savedLighting != null && restored.LightingGraph == savedLighting &&
                    restored.EnvironmentSampleTarget != null && restored.EnvironmentSampleTarget.parent == restored.transform &&
                    restored.EnvironmentSampleTarget.localPosition == new Vector3(3, 4, 5) &&
                    restored.LightingRevision == 0 && restored.BaselineClimateRevision == 0,
                    "UC-T05-04 scene reload restores the exact Lighting Graph and spatial target while accepted runtime captures remain absent.");
            }
            finally
            {
                if (fixture.IsValid() && fixture.isLoaded)
                {
                    if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    else EditorSceneManager.CloseScene(fixture, true);
                }
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                if (createdFolder && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (savedMain != null && !EditorUtility.IsPersistent(savedMain)) UnityEngine.Object.DestroyImmediate(savedMain);
                if (savedVegetation != null && !EditorUtility.IsPersistent(savedVegetation)) UnityEngine.Object.DestroyImmediate(savedVegetation);
                if (savedLighting != null && !EditorUtility.IsPersistent(savedLighting)) UnityEngine.Object.DestroyImmediate(savedLighting);
            }
        }

        // A bounded test-only main-thread context drains genuine admitted workers and their main-thread continuations.
        private static bool WaitVegetationRun(Task<bool> task, SnapshotRetirementContext context)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && deadline.Elapsed.TotalSeconds < 15)
            { context.Drain(); Thread.Sleep(1); }
            context.Drain();
            if (!task.IsCompleted) throw new TimeoutException("UC-T05-03 vegetation worker did not retire within 15 seconds.");
            return task.GetAwaiter().GetResult();
        }
    }
}
