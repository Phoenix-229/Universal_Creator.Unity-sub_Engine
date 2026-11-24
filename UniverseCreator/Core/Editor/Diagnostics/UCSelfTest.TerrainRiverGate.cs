// UC-T03-11: production terrain-only regeneration and native scene reload, with contract-specific diagnostics.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Runs the complete Topic 3 numerical/native terrain regression groups in an isolated batch process.</summary>
        /// <remarks>Reports each contract group's exceptions as failures and exits zero only when every group passes.
        /// Scene/domain reload and visual/device acceptance use their separate gates.</remarks>
        /// <exception cref="InvalidOperationException">Called outside Unity batch mode.</exception>
        public static void RunTerrainRiverBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Terrain/river acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            var groups = new (string id, Action run)[] {
                ("UC-T03-01 lake-footprint production", TestRiverNetworkNodeCarvesTheTerrain),
                ("UC-T03-01/02", TestLowlandTrenchProduction), ("UC-T03-03", TestGroundNodeContracts),
                ("UC-T03-04", TestBoundedRiverSearch), ("UC-T03-05", TestRiverHierarchyTargets),
                ("UC-T03-06", TestDirectedRiverOutlets), ("UC-T03-07", TestLakeOutletSources),
                ("UC-T03-08", TestLakeDistributionScaling), ("UC-T03-09", TestCommittedRiverProfiles),
                ("UC-T03-10", TestFinalGroundComposition), ("UC-T03-11", TestTerrainRiverRegeneration),
                ("UC-T08-15", TestWaterRepairContracts),
                ("UC-T08-15 native lake retries", TestSpawnerPlacesRealWaterObjects),
                ("UC-T08-15 network confluences", TestRiverNetworkExtractsConfluencesAndMouth),
                ("UC-T08-15 manual course", TestRiverCourseInputMaskWire),
                ("UC-T08-15 saved sculpted river", TestManualRiverCourseOwnership),
                ("UC-T08-15 manual descending beds", TestRiverBedUphillCourseStillDescends),
                ("UC-T08-15 channel carve", TestRiverCarveCutsDescendingChannel),
                ("UC-T08-15 mouths", TestRiverMouthBlendsToWaterSurface),
                ("UC-T08-15 fill", TestRiverFillWetsChannelNotBank) };
            foreach (var group in groups)
                try { group.run(); }
                catch (Exception error) { Check(false, group.id + " terrain/river contract exception: " + error); }
            ExitTerrainRiverGate();
        }

        private static Graph TerrainRiverGateGraph()
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            graph.nodes.AddRange(new Generator[] {
                new LowlandTrenchGround { id = "gate-ground" },
                new RiverNetworkNode { id = "gate-river", seaLevel = 0.35f, solveResolution = 64,
                    channelInitiationFraction = 0.001f, slopeAreaThreshold = 0, trunkChannelWidthMetres = 32, autoTrunkWidth = false },
                new HeightOutputNode { id = "gate-height", seaLevel = 0.35f, heightScale = 600 } });
            graph.AddConnection(new Connection("gate-ground", 0, "gate-river", 0));
            graph.AddConnection(new Connection("gate-river", 0, "gate-height", 0));
            return graph;
        }

        private static void TestTerrainRiverRegeneration()
        {
            var root = new GameObject("UC terrain-only regeneration owner"); root.SetActive(false);
            Graph graph = TerrainRiverGateGraph();
            try { var world = root.AddComponent<UniverseCreatorObject>(); world.graph = graph; CheckTerrainRiverRegeneration(world, null); }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph); }
        }

        // UC-T03-11: compare actual graph products and native readback across cold, retained and fresh-run evaluations.
        private static float[,] CheckTerrainRiverRegeneration(UniverseCreatorObject world, float[,] previous, bool preserveGround = false)
        {
            world.maxHeightMeters = 600; world.generateCollider = true;
            Graph graph = world.graph; GraphContentHasher.ComputeHashes(graph); graph.compiledPlan = CompiledGraphPlan.Build(graph);
            TerrainData native = TerrainBuilder.GetOrCreateTerrainData(null, 129, new Vector3(2048, 600, 2048));
            Dictionary<string, RetainedProduct> retained = null; float[,] baseline = previous;
            try
            {
                foreach (string phase in new[] { "Cold", "Retained", "Regenerated" })
                {
                    if (phase != "Retained") world.GenerationScheduler.BeginRun();
                    var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 129, 129), Vector3.zero, new Vector3(2048, 600, 2048), 600);
                    tile.AssignRun(world.GenerationScheduler.CurrentRun); tile.AttachMemory(world.ProductMemory);
                    tile.retained = phase == "Retained" ? retained : new Dictionary<string, RetainedProduct>();
                    try
                    {
                        var evaluator = new GraphEvaluator(graph); evaluator.Evaluate(tile, new StopToken());
                        Check(tile.outputs.heightmap != null && tile.outputs.channelPublications.Length > 0,
                            "UC-T03-11 " + phase + " wired headwaters reach actual Height Output and owned channel publication.");
                        if (tile.outputs.heightmap == null) continue;
                        var bounds = new Bounds(new Vector3(1024, 300, 1024), new Vector3(2048, 600, 2048));
                        TerrainBuilder.ApplyOutputs(native, tile.outputs, world, null, bounds, 16, null);
                        float[,] readback = native.GetHeights(0, 0, 129, 129); bool equal = true, accepted = true;
                        for (int z = 0; z < 129; z++) for (int x = 0; x < 129; x++)
                        {
                            equal &= Mathf.Abs(readback[z, x] - tile.outputs.heightmap[z, x]) < 2e-5f &&
                                (baseline == null || Mathf.Abs(readback[z, x] - baseline[z, x]) < 2e-5f);
                            accepted &= RiverChannelRegistry.TrySampleGround(world, x * 16, z * 16, out float y, out var run) &&
                                run.Equals(tile.RunId) && Mathf.Abs(y - readback[z, x] * 600) < 0.02f;
                        }
                        Check(equal && accepted, "UC-T03-11 " + phase + " final native terrain and accepted ground match cold/retained/regenerated heights.");
                        bool trench = false;
                        for (int z = 8; z < 121 && !trench; z += 4) for (int x = 8; x < 121 && !trench; x += 4)
                            trench = RiverChannelRegistry.TrySampleCommitted(world, x * 16, z * 16, 32, out _, out _);
                        Check(trench, "UC-T03-11 " + phase + " guard-composed native terrain retains a readable bed and both banks.");
                        baseline ??= readback; retained = tile.retained;
                    }
                    finally { tile.ReleaseProductMemory(); }
                }
                Check(UCWaterSystemManager.ResolveHydrology(graph, null) == null && world.GetComponentsInChildren<UCWaterBody>(true).Length == 0,
                    "UC-T03-11 a terrain-only river graph produces native trenches without requesting or spawning scene water bodies.");
                return baseline;
            }
            finally
            {
                // UC-T03-11: reload acceptance must leave a live owner witness for scene unload to retire.
                if (!preserveGround) RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                UnityEngine.Object.DestroyImmediate(native);
            }
        }

        /// <summary>Runs terrain-only river regeneration before and after an actual saved-scene unload/reload in an isolated batch.</summary>
        /// <remarks>Uses temporary Unity-authored fixture assets; preserves a saved startup scene and removes only its own fixtures.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode or with a nonempty unsaved startup scene.</exception>
        public static void RunTerrainRiverSceneReloadBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("River scene reload requires isolated batch mode.");
            checks = 0; failures.Clear();
            Scene original = SceneManager.GetActiveScene(); bool single = string.IsNullOrEmpty(original.path);
            Scene fixture = default; Graph graph = null;
            string folder = "Assets/UCTerrainRiverReload_" + Guid.NewGuid().ToString("N");
            try
            {
                if (!original.IsValid() || SceneManager.sceneCount != 1 || (single && original.GetRootGameObjects().Length != 0))
                    throw new InvalidOperationException("Start the river reload gate with a saved scene or an empty untitled scene.");
                AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
                graph = TerrainRiverGateGraph(); AssetDatabase.CreateAsset(graph, folder + "/Graph.asset");
                var root = new GameObject("UC river scene reload owner"); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, fixture);
                var world = root.AddComponent<UniverseCreatorObject>(); world.graph = graph;
                ulong climateHash = PrepareClimateReloadFixture(world, folder); // UC repair: persisted acceptance crosses a real scene close/open.
                float[,] baseline = CheckTerrainRiverRegeneration(world, null, true);
                Guid formerWorld = world.GenerationScheduler.CurrentRun.WorldId;
                Check(RiverChannelRegistry.TrySampleGround(world, 1024, 1024, out _, out _),
                    "UC-T03-11 scene unload begins with a live accepted-ground owner witness.");
                Check(EditorSceneManager.SaveScene(fixture, folder + "/World.unity"), "UC-T03-11 native river fixture saves its authored graph and world.");
                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else EditorSceneManager.CloseScene(fixture, true); // UC-T03-11: Unity cannot close its only loaded scene.
                Check(!RiverChannelRegistry.TrySampleGround(formerWorld, 1024, 1024, 0, out _, out _),
                    "UC-T03-11 scene unload retires accepted river ground instead of persisting stale runtime records.");
                fixture = EditorSceneManager.OpenScene(folder + "/World.unity", single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                world = fixture.GetRootGameObjects()[0].GetComponent<UniverseCreatorObject>();
                Check(world != null && world.graph != null && world.GenerationScheduler.CurrentRun.WorldId != formerWorld,
                    "UC-T03-11 scene reload restores authored graph settings with a fresh runtime owner.");
                if (world != null && world.graph != null)
                { CheckClimateReloadFixture(world, climateHash); CheckTerrainRiverRegeneration(world, baseline); }
            }
            catch (Exception error) { Check(false, "UC-T03-11 native river scene reload exception: " + error); }
            finally
            {
                if (fixture.IsValid() && fixture.isLoaded)
                { if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); else EditorSceneManager.CloseScene(fixture, true); }
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (graph != null && !AssetDatabase.Contains(graph)) UnityEngine.Object.DestroyImmediate(graph);
            }
            ExitTerrainRiverGate();
        }

        private static void ExitTerrainRiverGate()
        {
            bool ok = failures.Count == 0; Debug.Log(Report(ok)); Console.WriteLine(Report(ok)); EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
