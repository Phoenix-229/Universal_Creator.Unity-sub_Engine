// UC-T03-08: scaling is checked independently of basin eligibility and through graph/native callers.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestLakeDistributionScaling()
        {
            var settings = new LakeDistributionSettings { scaleToWorld = true, absoluteAreas = false,
                referenceWorldAreaM2 = 4000000, referenceLakeCount = 4, maximumAreaSquareMetres = 120 };
            var reference = settings.Resolve(4000000, 25, 0.24f, 0, 60);
            var large = settings.Resolve(16000000, 25, 0.24f, 0, 60);
            var small = settings.Resolve(250000, 25, 0.24f, 0, 60);
            Check(reference.TargetCount == 4 && large.TargetCount == 16 && small.TargetCount == 1 &&
                reference.MinimumAreaM2 == 60 && large.MinimumAreaM2 == 240 && small.MinimumAreaM2 == 3.75f &&
                large.MaximumAreaM2 == 480,
                "UC-T03-08 automatic lake count and area limits scale with world area, including a smaller-than-density world.");
            settings.absoluteAreas = true;
            var absolute = settings.Resolve(16000000, 25, 0.24f, 3, 60);
            Check(absolute.TargetCount == 3 && absolute.MinimumAreaM2 == 60 && absolute.MaximumAreaM2 == 120,
                "UC-T03-08 authored positive lake counts and absolute area overrides survive automatic scaling.");
            var capped = settings.Resolve(1e12, 25, 0.24f, 0, 60);
            Check(capped.TargetCount == 4096 && capped.CountLimited && settings.referenceLakeCount == 4 &&
                settings.referenceWorldAreaM2 == 4000000 && settings.maximumAreaSquareMetres == 120,
                "UC-T03-08 large automatic targets are bounded without multiplying or rewriting authored settings.");
            settings.scaleToWorld = false;
            var legacy = settings.Resolve(4000000, 100, 0.24f, 0, 60);
            Check(legacy.TargetCount == 24 && settings.Resolve(4000000, 1, 0.24f, 0, 60).TargetCount == 1 &&
                settings.Resolve(4000000, 100, 0, 0, 60).TargetCount == 0,
                "UC-T03-08 disabling automatic scaling retains nearest-rounded chunk coverage, the small-world density floor and zero coverage.");
            string json = JsonUtility.ToJson(settings); var copy = JsonUtility.FromJson<LakeDistributionSettings>(json);
            Check(copy.referenceLakeCount == settings.referenceLakeCount && copy.absoluteAreas == settings.absoluteAreas &&
                copy.maximumAreaSquareMetres == settings.maximumAreaSquareMetres,
                "UC-T03-08 save/reload retains authored distribution settings rather than resolved world values.");
            bool invalid = false;
            try { settings.Resolve(double.NaN, 1, 0.24f, 0, 60); }
            catch (ArgumentException) { invalid = true; }
            Check(invalid, "UC-T03-08 nonfinite world geometry is rejected before resolving lake targets.");
            invalid = false; settings.maximumAreaSquareMetres = 1;
            try { settings.Resolve(4000000, 1, 0.24f, 0, 60); }
            catch (ArgumentException) { invalid = true; }
            Check(invalid, "UC-T03-08 inverted area limits are rejected rather than forcing a lake selection.");
            TestEligibleLakeSelection();
            TestLakeDistributionProduction();
            TestLakeDistributionNative();
        }

        private static void TestEligibleLakeSelection()
        {
            var height = new Matrix(new CoordRect(0, 0, 8, 8)); height.Fill(0.95f);
            height.worldPos = Vector3.zero; height.worldSize = new Vector3(700, 600, 700); height.worldHeight = 600;
            var filled = (float[])height.arr.Clone();
            foreach (int c in new[] { 9, 10 }) { height.arr[c] = 0.2f; filled[c] = 0.7f; }
            foreach (int c in new[] { 34, 35, 42, 43 }) { height.arr[c] = 0.2f; filled[c] = 0.8f; }
            var solve = WorldDrainage.CreateForTests(height, filled); var before = (float[])filled.Clone();
            var selected = LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, 0, float.PositiveInfinity, 1, out var report, null);
            Check(report.RequestedCount == 1 && report.EligibleCount == 2 && report.SelectedCount == 1 &&
                selected[9] == height.arr[9] && selected[34] == filled[34] && selected[43] == filled[43] &&
                report.SelectedAreaM2 == solve.AreaOfCells(4),
                "UC-T03-08 a count cap selects the largest independently authored eligible lake and reports its area.");
            var shortfall = LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, 0, float.PositiveInfinity, 20, out report, null);
            bool unchanged = true;
            for (int c = 0; c < filled.Length; c++) unchanged &= filled[c] == before[c] &&
                (height.arr[c] < filled[c] || shortfall[c] == height.arr[c]);
            Check(report.EligibleCount == 2 && report.SelectedCount == 2 && unchanged,
                "UC-T03-08 an infeasible requested count neither raises spill levels nor floods dry cells.");
            LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, solve.AreaOfCells(3), float.PositiveInfinity, -1, out report, null);
            Check(report.EligibleCount == 1 && report.SelectedCount == 1,
                "UC-T03-08 square-metre minimum area excludes only the smaller eligible pool.");
            LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, 0, solve.AreaOfCells(3), -1, out report, null);
            Check(report.EligibleCount == 1 && report.SelectedCount == 1 && report.LargestAreaM2 == solve.AreaOfCells(2),
                "UC-T03-08 an upper area limit excludes an oversized pool without changing its terrain.");
            var scaling = new LakeDistributionSettings { scaleToWorld = true, absoluteAreas = false, referenceWorldAreaM2 = 490000 };
            var baseCriteria = scaling.Resolve(490000, 1, 0, 0, solve.AreaOfCells(3));
            var largerHeight = new Matrix(height); largerHeight.worldSize = new Vector3(1400, 600, 1400);
            var largerSolve = WorldDrainage.CreateForTests(largerHeight, (float[])filled.Clone());
            var largerCriteria = scaling.Resolve(1960000, 1, 0, 0, solve.AreaOfCells(3));
            LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, baseCriteria.MinimumAreaM2, baseCriteria.MaximumAreaM2,
                baseCriteria.TargetCount, out var baseReport, null);
            LakeFillNode.LakeSurface(largerSolve, 0.01f, 0.1f, largerCriteria.MinimumAreaM2, largerCriteria.MaximumAreaM2,
                largerCriteria.TargetCount, out var largerReport, null);
            Check(baseReport.EligibleCount == 1 && largerReport.EligibleCount == 1 &&
                largerReport.SelectedAreaM2 == baseReport.SelectedAreaM2 * 4,
                "UC-T03-08 equivalent normalized basins retain scaled area eligibility on a world with twice the XZ extent.");
            var none = LakeFillNode.LakeSurface(solve, 0.9f, 0.1f, 0, float.PositiveInfinity, 20, out report, null);
            bool dry = true; for (int c = 0; c < none.Length; c++) dry &= none[c] == height.arr[c];
            Check(report.EligibleCount == 0 && report.SelectedCount == 0 && dry,
                "UC-T03-08 zero depth-eligible basins leave the entire terrain dry despite a positive target.");
            var zero = LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, 0, float.PositiveInfinity, 0, out report, null);
            dry = true; for (int c = 0; c < zero.Length; c++) dry &= zero[c] == height.arr[c];
            Check(report.EligibleCount == 2 && report.SelectedCount == 0 && report.SelectedAreaM2 == 0 && dry,
                "UC-T03-08 a zero target reports eligible basins while retaining dry ground everywhere.");
            var stop = new StopToken(); stop.RequestStop();
            Check(LakeFillNode.LakeSurface(solve, 0.01f, 0.1f, 0, float.PositiveInfinity, 20, out report, stop) == null && report == null,
                "UC-T03-08 cancelled selection returns neither a partial lake field nor a report.");
        }

        private static void TestLakeDistributionProduction()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>(); var tiles = new List<TileData>();
            try
            {
                var ground = new LakeOutletGround { id = "distribution-ground", contentHash = 8301 };
                var hydrology = new HydrologyNode { id = "distribution-mask", contentHash = 8302, targetLakeCount = 16,
                    drainageResolution = 32, minimumAreaSquareMetres = 60, seaLevel = 0.2f };
                hydrology.lakeDistribution.scaleToWorld = true;
                var fill = new LakeFillNode { id = "distribution-fill", contentHash = 8303, drainageResolution = 32, seaLevel = 0.2f };
                fill.lakeDistribution.scaleToWorld = true;
                graph.nodes.Add(ground); graph.nodes.Add(hydrology); graph.nodes.Add(fill);
                graph.AddConnection(new Connection(ground.id, 0, hydrology.id, 0));
                graph.AddConnection(new Connection(ground.id, 0, fill.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); var evaluator = new GraphEvaluator(graph);
                Vector3 size = new Vector3(1200, 600, 600);
                TileData cold = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(cold);
                cold.retained = new Dictionary<string, RetainedProduct>();
                Matrix mask = evaluator.PullNode(hydrology, 0, cold, null) as Matrix;
                var report = cold.outputs.lakeDistributionReports[hydrology.id + ":lakes"];
                Check(report.RequestedCount == 16 && report.EligibleCount > 0 && report.SelectedCount == report.EligibleCount && report.SelectedCount < 16,
                    "UC-T03-08 production Hydrology reports a real eligible-basin shortfall instead of inventing lakes.");
                Matrix fillMask = evaluator.PullNode(fill, 0, cold, null) as Matrix;
                bool same = true; for (int c = 0; c < mask.arr.Length; c++) same &= Mathf.Abs(mask.arr[c] - fillMask.arr[c]) < 1e-6f;
                Check(same, "UC-T03-08 Lake Fill and Hydrology consume matching eligible lake depth/area criteria on the same captured terrain.");
                TileData warm = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(warm); warm.retained = cold.retained;
                evaluator.PullNode(hydrology, 0, warm, null);
                Check(ReferenceEquals(report, warm.outputs.lakeDistributionReports[hydrology.id + ":lakes"]),
                    "UC-T03-08 cached lake masks replay the exact eligible/requested/selected area report.");
                TileData chunk = SearchTile(evaluator, Vector3.zero, new Vector3(600, 600, 600), Vector3.zero, size, 17); tiles.Add(chunk);
                evaluator.PullNode(hydrology, 0, chunk, null);
                var chunkReport = chunk.outputs.lakeDistributionReports[hydrology.id + ":lakes"];
                Check(chunkReport.RequestedCount == report.RequestedCount && chunkReport.SelectedCount == report.SelectedCount &&
                    chunkReport.SelectedAreaM2 == report.SelectedAreaM2,
                    "UC-T03-08 mixed chunk resolution preserves whole-world lake counts and selected areas.");
                cold.outputs.Clear();
                Check(cold.outputs.lakeDistributionReports.Count == 0, "UC-T03-08 resetting output metadata withdraws obsolete lake distribution reports.");
            }
            finally
            {
                foreach (TileData tile in tiles) tile.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestLakeDistributionNative()
        {
            var terrainRoot = new GameObject("UC distribution native terrain");
            var worldRoot = new GameObject("UC distribution native owner");
            var foreignRoot = new GameObject("UC distribution foreign terrain owner");
            TerrainData data = BuildHydrologyTestTerrain();
            try
            {
                var terrain = terrainRoot.AddComponent<Terrain>(); terrain.terrainData = data;
                var world = worldRoot.AddComponent<UniverseCreatorObject>(); world.worldSizeKm = 2;
                terrainRoot.transform.SetParent(worldRoot.transform, true); // UC-T08-15: only owned ground may feed the spawner.
                foreignRoot.AddComponent<UniverseCreatorObject>();
                var foreignTerrainRoot = new GameObject("Foreign basin must not be sampled");
                foreignTerrainRoot.transform.SetParent(foreignRoot.transform, false);
                foreignTerrainRoot.transform.position = new Vector3(10000, -100, 10000);
                foreignTerrainRoot.AddComponent<Terrain>().terrainData = data;
                world.maxHeightMeters = HydrologyTestHeightMetres; world.chunkSizeMeters = 400; world.chunkCountPerSide = 5; world.waterLayer = 0;
                var node = new HydrologyNode { generateOcean = false, lakeChunkPercentage = 0, targetLakeCount = 0, minimumAreaSquareMetres = 0 };
                node.lakeDistribution.scaleToWorld = true; node.lakeDistribution.referenceLakeCount = 1;
                node.lakeDistribution.maximumAreaSquareMetres = 1;
                HydrologyGenerator.GenerateFromNode(world, node);
                Transform waterRoot = worldRoot.transform.Find("WaterSystem");
                Check(waterRoot != null && waterRoot.childCount == 0,
                    "UC-T03-08 the native spawner rejects oversized real basins without flooding terrain to satisfy an automatic target.");
                node.lakeDistribution.maximumAreaSquareMetres = 0;
                HydrologyGenerator.GenerateFromNode(world, node);
                int first = waterRoot.GetComponentsInChildren<UCWaterBody>(true).Length;
                var firstLake = waterRoot.GetComponentInChildren<UCWaterBody>(true);
                Check(firstLake != null && firstLake.TryGetWorldFootprint(out _, out Bounds firstBounds) &&
                    firstBounds.max.x < HydrologyTestWorldMetres && firstBounds.max.z < HydrologyTestWorldMetres,
                    "UC-T08-15 native spawner excludes a foreign world's lower basin from sampling and placement bounds.");
                Check(firstLake != null && firstLake.lakeFormation != LakeFormation.Legacy,
                    "UC-T08-15 native procedural spawn actually consumes the terrain-weighted formation selector.");
                LakeFormation firstFormation = firstLake != null ? firstLake.lakeFormation : LakeFormation.Legacy;
                HydrologyGenerator.GenerateFromNode(world, node);
                Check(first == 1 && waterRoot.GetComponentsInChildren<UCWaterBody>(true).Length == first,
                    "UC-T03-08 repeated native generation keeps the scaled target without multiplying existing lakes.");
                Check(firstLake != null && firstLake.lakeFormation == firstFormation,
                    "UC-T08-15 repeated native generation preserves existing saved formation choices.");
                // UC river repair: an unmet target must search distinct basins, never count the same already-carved basin twice.
                node.targetLakeCount = 4;
                HydrologyGenerator.GenerateFromNode(world, node);
                var expanded = waterRoot.GetComponentsInChildren<UCWaterBody>(true);
                HydrologyGenerator.GenerateFromNode(world, node);
                Check(waterRoot.GetComponentsInChildren<UCWaterBody>(true).Length == expanded.Length,
                    "UC river repair repeated unmet native lake targets do not add duplicate bodies.");
                var overlaps = typeof(HydrologyGenerator).GetMethod("LakeFootprintsOverlap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                bool unique = overlaps != null;
                for (int i = 0; i < expanded.Length; i++)
                    for (int j = i + 1; j < expanded.Length; j++)
                        if (expanded[i].TryGetWorldFootprint(out Vector2[] a, out Bounds ab) && expanded[j].TryGetWorldFootprint(out Vector2[] b, out Bounds bb))
                            unique &= !(bool)overlaps.Invoke(null, new object[] { a, ab, b, bb });
                Check(unique, "UC river repair native lake contours remain distinct when the requested count exceeds basin availability.");
                node.targetLakeCount = 1; world.worldSizeKm = 4; // UC-T03-08: explicit count survives a changed automatic density.
                HydrologyGenerator.GenerateFromNode(world, node);
                Check(waterRoot.GetComponentsInChildren<UCWaterBody>(true).Length == first,
                    "UC-T03-08 increasing world scale with an absolute count reuses existing native lakes.");
                var retainedLake = waterRoot.GetComponentInChildren<UCWaterBody>(true);
                retainedLake.placementSaved = true;
                node.targetLakeCount = 0; node.lakeDistribution.referenceLakeCount = 0;
                HydrologyGenerator.GenerateFromNode(world, node);
                Check(retainedLake != null && waterRoot.GetComponentsInChildren<UCWaterBody>(true).Length == first,
                    "UC-T03-08 a saved placement survives a zero scaled target without spawning additional lakes.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(worldRoot); UnityEngine.Object.DestroyImmediate(terrainRoot);
                UnityEngine.Object.DestroyImmediate(foreignRoot);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }
    }
}
