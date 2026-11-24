// UC climate/memory repair: exercise actual owners, outputs and persisted acceptance.
using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Biome;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestClimateRestoreContracts()
        {
            TestColdClimateSurfaceRecovery();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC strict climate restore"); root.SetActive(false);
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater);
            var environment = ScriptableObject.CreateInstance<Graph>(); environment.SetDomain(UCGraphDomain.LightingClimateWeather);
            var context = new SnapshotRetirementContext(); var previous = SynchronizationContext.Current;
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
            GenerationSnapshot snapshot = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = main; world.worldSizeKm = 0.032f; world.chunkCountPerSide = 1; world.resolution = 33;
                var reader = new ClimateBaselineInputNode { id = "restore-climate", allowMissingBaseline = false };
                main.nodes.Add(reader); main.nodes.Add(new HeightOutputNode { id = "restore-height" });
                main.connections.Add(new Connection(reader.id, 0, "restore-height", 0));
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = environment;
                var baseline = new ClimateBaselineNode { id = "restore-baseline", temperatureC = 10, moisture01 = 0.4f };
                environment.nodes.Add(baseline); environment.nodes.Add(new WeatherOutputNode { id = "restore-weather" });
                environment.connections.Add(new Connection(baseline.id, 0, "restore-weather", 8));
                Check(WaitVegetationRun(bindings.PrepareTerrainClimateAsync(), context) && bindings.BaselineClimateRevision == 1,
                    "UC repair: a strict cold bake prepares the actual early Lighting stage once.");
                ulong hash = bindings.CaptureClimateBaseline().ContentHash;
                string saved = EditorJsonUtility.ToJson(bindings);
                baseline.temperatureC = 45;
                typeof(UCWorldGraphBindings).GetMethod("RetireLightingGraphs", flags).Invoke(bindings, null);
                EditorJsonUtility.FromJsonOverwrite(saved, bindings);
                Check(bindings.CaptureClimateBaseline().ContentHash == hash &&
                    WaitVegetationRun(bindings.PrepareTerrainClimateAsync(), context) && bindings.BaselineClimateRevision == 1,
                    "UC repair: accepted serialized climate survives retirement without applying pending graph edits or duplicating early work.");
                snapshot = GenerationSnapshot.Capture(main, world, true);
                Check(snapshot.Graph.GetNodeById(reader.id) is ClimateBaselineInputNode,
                    "UC repair: strict terrain input capture succeeds with restored accepted climate.");
                var emptyMask = bindings.CaptureClimateBaseline().SaveAccepted(); emptyMask.bands[0].mask = Array.Empty<float>();
                Check(UniverseCreator.Weather.ClimateBaselineProduct.RestoreAccepted(emptyMask).ContentHash == hash,
                    "UC repair: Unity's empty serialized mask preserves canonical unmasked climate acceptance.");
                emptyMask.bands[0].maskSize = new Vector2Int(2, 2);
                bool missingSamplesRejected = false;
                try { UniverseCreator.Weather.ClimateBaselineProduct.RestoreAccepted(emptyMask); }
                catch (InvalidOperationException) { missingSamplesRejected = true; }
                Check(missingSamplesRejected, "UC repair: empty samples cannot conceal a damaged saved regional mask.");
                var record = bindings.CaptureClimateBaseline().SaveAccepted(); record.bands[0].temperature += 1;
                bool corruptRejected = false;
                try { UniverseCreator.Weather.ClimateBaselineProduct.RestoreAccepted(record); }
                catch (InvalidOperationException) { corruptRejected = true; }
                Check(corruptRejected && bindings.CaptureClimateBaseline().ContentHash == hash,
                    "UC repair: corrupted saved acceptance is rejected without mutating the accepted immutable climate.");

                var manager = root.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var terrainRoot = new GameObject("Terrain"); terrainRoot.transform.SetParent(root.transform, false);
                var holder = new GameObject("Terrain_0_0"); holder.transform.SetParent(terrainRoot.transform, false);
                var terrain = holder.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = data;
                var collider = holder.AddComponent<TerrainCollider>(); collider.terrainData = data;
                data.SetHeights(0, 0, CommitHeights(33, 0.37f)); float[,] expected = data.GetHeights(0, 0, 33, 33);
                manager.RestoreSurfaces();
                typeof(UCWorldGraphBindings).GetMethod("RetireLightingGraphs", flags).Invoke(bindings, null);
                typeof(UCWorldGraphBindings).GetField("savedClimate", flags).SetValue(bindings, null);
                bindings.LightingGraph = null;
                bool settled = false;
                manager.RebuildAll();
                PumpClimateRepair(manager, context);
                Check(!manager.IsRebuildingAll, "UC repair: rejected prerequisite retires its asynchronous rebuild.");
                manager.RunAfterSettle(() => settled = true);
                for (int i = 0; i < 4; i++) { manager.PumpCompleted(); context.Drain(); }
                bool unchanged = terrain.terrainData == data && collider.terrainData == data && terrain.drawHeightmap;
                float[,] actual = data.GetHeights(0, 0, 33, 33);
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) unchanged &= actual[z, x] == expected[z, x];
                Check(unchanged && !settled && !manager.HasWork && !string.IsNullOrEmpty(manager.ReadinessDiagnostic),
                    "UC repair: missing strict climate preserves accepted native ground and never fires false hydrology/success or idle retries.");
                bindings.LightingGraph = environment;
                manager.RebuildAll(); // UC repair: retry through the real entry point, never clear failure flags in the fixture.
                manager.RunAfterSettle(() => settled = true);
                PumpClimateRepair(manager, context);
                Check(settled && bindings.CaptureClimateBaseline() != null && !manager.IsRebuildingAll &&
                    string.IsNullOrEmpty((string)typeof(ChunkManager).GetField("bakeInputFailure", flags).GetValue(manager)),
                    "UC repair: explicit corrected rebuild clears failure, prepares strict climate and settles without fallback.");
            }
            finally
            {
                snapshot?.Release(); context.Drain();
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(main); UnityEngine.Object.DestroyImmediate(environment);
                context.Drain(); SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private static void PumpClimateRepair(ChunkManager manager, SnapshotRetirementContext context)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            do { context.Drain(); manager.PumpCompleted(); Thread.Sleep(1); }
            while ((manager.IsRebuildingAll || manager.HasWork) && deadline.Elapsed.TotalSeconds < 15);
            context.Drain();
            if (manager.IsRebuildingAll || manager.HasWork) throw new TimeoutException("UC climate rebuild did not retire within 15 seconds.");
        }

        private static void TestColdClimateSurfaceRecovery()
        {
            var root = new GameObject("UC cold climate recovery"); root.SetActive(false);
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater);
            var environment = ScriptableObject.CreateInstance<Graph>(); environment.SetDomain(UCGraphDomain.LightingClimateWeather);
            var context = new SnapshotRetirementContext(); var previous = SynchronizationContext.Current;
            TerrainData native = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = main; world.worldSizeKm = 0.032f; world.chunkCountPerSide = 1; world.resolution = 33;
                main.nodes.Add(new ClimateBaselineInputNode { id = "cold-reader", allowMissingBaseline = false });
                main.nodes.Add(new HeightOutputNode { id = "cold-height" });
                main.connections.Add(new Connection("cold-reader", 0, "cold-height", 0));
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = environment;
                environment.nodes.Add(new ClimateBaselineNode { id = "cold-baseline" });
                environment.nodes.Add(new WeatherOutputNode { id = "cold-weather" });
                environment.connections.Add(new Connection("cold-baseline", 0, "cold-weather", 8));
                var terrainRoot = new GameObject("Terrain"); terrainRoot.transform.SetParent(root.transform, false);
                var holder = new GameObject("Terrain_0_0"); holder.transform.SetParent(terrainRoot.transform, false);
                var terrain = holder.AddComponent<UnityEngine.Terrain>(); holder.AddComponent<TerrainCollider>();
                var manager = root.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                int queued = manager.RestoreSurfaces();
                PumpClimateRepair(manager, context); native = terrain.terrainData;
                Check(queued == 1 && native != null && native.GetHeights(0, 0, 1, 1)[0, 0] > 0 && terrain.drawHeightmap &&
                    bindings.BaselineClimateRevision == 1 && !manager.HasWork,
                    "UC repair: cold missing-surface restoration prepares strict climate and resumes the existing pump without a prior worker or idle loop.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (native != null) UnityEngine.Object.DestroyImmediate(native);
                UnityEngine.Object.DestroyImmediate(main); UnityEngine.Object.DestroyImmediate(environment);
                context.Drain(); SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        // UC climate restore: real Unity scene/domain fixtures save acceptance before changing the authored recipe.
        private static ulong PrepareClimateReloadFixture(UniverseCreatorObject world, string folder)
        {
            world.graph.SetDomain(UCGraphDomain.TerrainWater); // UC repair: environment binding requires explicit fixture graph metadata.
            var environment = ScriptableObject.CreateInstance<Graph>(); environment.SetDomain(UCGraphDomain.LightingClimateWeather);
            var baseline = new ClimateBaselineNode { id = "reload-baseline", temperatureC = 10, moisture01 = 0.4f };
            environment.nodes.Add(baseline); environment.nodes.Add(new WeatherOutputNode { id = "reload-weather" });
            environment.connections.Add(new Connection(baseline.id, 0, "reload-weather", 8));
            AssetDatabase.CreateAsset(environment, folder + "/Climate.asset");
            world.graph.nodes.Add(new ClimateBaselineInputNode { id = "reload-strict-climate", allowMissingBaseline = false });
            EditorUtility.SetDirty(world.graph);
            var weather = world.gameObject.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
            var bindings = world.gameObject.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = environment;
            var context = new SnapshotRetirementContext(); var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                if (!WaitVegetationRun(bindings.PrepareTerrainClimateAsync(), context))
                    throw new InvalidOperationException("Could not prepare the reload fixture's early climate: " + bindings.LightingStatus);
                ulong hash = bindings.CaptureClimateBaseline().ContentHash;
                baseline.temperatureC = 45; EditorUtility.SetDirty(environment);
                AssetDatabase.SaveAssetIfDirty(environment); AssetDatabase.SaveAssetIfDirty(world.graph);
                return hash;
            }
            finally { context.Drain(); SynchronizationContext.SetSynchronizationContext(previous); }
        }

        private static void CheckClimateReloadFixture(UniverseCreatorObject world, ulong expected)
        {
            var bindings = world.GetComponent<UCWorldGraphBindings>();
            Check(bindings != null && bindings.CaptureClimateBaseline()?.ContentHash == expected && bindings.BaselineClimateRevision == 1,
                "UC repair: actual Unity reload restores accepted climate instead of pending authored temperature edits.");
            var captured = GenerationSnapshot.Capture(world.graph, world, true);
            try
            {
                Check(captured.Graph.GetNodeById("reload-strict-climate") is ClimateBaselineInputNode,
                    "UC repair: strict terrain snapshot captures persisted climate after actual Unity reload.");
            }
            finally { captured.Release(); }
        }

        private static void TestRiverMemoryOutputCoexistence()
        {
            var root = new GameObject("UC river output coexistence"); root.SetActive(false);
            Graph graph = TerrainRiverGateGraph();
            var memory = new ProductMemoryBudget(256L * 1024 * 1024);
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(2048, 600, 2048), 600);
            var native = new TerrainData { heightmapResolution = 33, size = new Vector3(2048, 600, 2048) };
            var layer = new TerrainLayer();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.graph = graph;
                world.maxHeightMeters = 600; world.terrainLayers = new[] { layer };
                var river = (RiverNetworkNode)graph.GetNodeById("gate-river"); river.solveResolution = 512;
                river.hierarchyTargets.enabled = true; // UC repair: exercise the repaired hierarchy reservation too.
                var splat = new BiomeSplatOutputNode { id = "repair-splat", layerCount = 1, waterBareEnabled = false,
                    beachEnabled = false, snowEnabled = false, wetStrength = 0 };
                graph.nodes.Add(splat); graph.connections.Add(new Connection(river.id, 0, splat.id, 0));
                GraphContentHasher.ComputeHashes(graph); graph.compiledPlan = CompiledGraphPlan.Build(graph);
                tile.AttachMemory(memory); tile.AssignRun(world.GenerationScheduler.CurrentRun);
                var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator);
                evaluator.PullNode(graph.GetNodeById("gate-height"), 0, tile, new StopToken());
                float[,] before = (float[,])tile.outputs.heightmap.Clone();
                evaluator.PullNode(splat, 0, tile, new StopToken());
                tile.FinishPublications(); // UC repair: exercise the same accepted native channel upload as worker completion.
                bool unchanged = tile.outputs.splatmap != null && tile.outputs.channelPublications?.Length > 0;
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) unchanged &= before[z, x] == tile.outputs.heightmap[z, x];
                Check(unchanged && memory.PeakBytes <= memory.LimitBytes,
                    "UC repair: real 512-square river publication and biome splats fit the unchanged 256 MiB budget without changing Height Output.");
                native.terrainLayers = new[] { layer };
                TerrainBuilder.ApplyOutputs(native, tile.outputs, world, null, new Bounds(new Vector3(1024, 300, 1024), native.size), 16, null);
                float[,] readback = native.GetHeights(0, 0, 33, 33); bool heightsMatch = true;
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heightsMatch &= Mathf.Abs(readback[z, x] - before[z, x]) < 2e-5f;
                Check(heightsMatch && native.GetAlphamaps(0, 0, 1, 1)[0, 0, 0] == 1,
                    "UC repair: native height and alphamap uploads coexist on the same TerrainData.");
            }
            finally
            {
                tile.ReleaseProductMemory(); memory.ClearRetained();
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC repair: river output fixture releases all admitted product memory.");
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph);
                UnityEngine.Object.DestroyImmediate(native); UnityEngine.Object.DestroyImmediate(layer);
            }
        }
    }
}
