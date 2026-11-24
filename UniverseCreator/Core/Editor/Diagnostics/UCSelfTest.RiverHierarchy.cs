// UC-T03-05: count real connected courses, with explicit world-scale and feasibility limits.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestRiverHierarchyTargets()
        {
            bool[] water;
            WorldDrainage wd = HierarchyTestWorld(2000, out water);
            var options = RiverNetworkOptions.Default();
            options.minDrainageAreaM2 = 1; options.slopeAreaThreshold = 0;
            options.slopeExponent = 0; options.minReachCells = 3;
            RiverNetwork raw = RiverNetworkExtractor.Extract(wd, options, water, null);
            var targets = new RiverHierarchyTargets { enabled = true, tolerance = 0 };
            float[] before = (float[])wd.filled.Clone();
            int[] directions = (int[])wd.dir.Clone();
            RiverNetwork chosen = RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, true, 1, null);
            RiverHierarchyReport report = chosen.hierarchy;
            Check(report.Feasible && report.Trunks == 2 && report.Rivers == 4 && report.Streams == 16 &&
                report.RiversBelowStreamTarget == 0 && report.CandidateCourses == 22 && chosen.reaches.Count > 22,
                "UC-T03-05 2 km by 2 km scenario selects exactly 2 trunks, 4 rivers and 4 streams per river; confluence links do not inflate counts.");
            bool directed = true;
            var courses = new HashSet<int>();
            foreach (RiverReach reach in chosen.reaches)
            {
                courses.Add(reach.courseId);
                directed &= reach.hierarchyLevel >= 0 && reach.hierarchyLevel <= 2;
                if (reach.downstream >= 0)
                    directed &= reach.cells[reach.cells.Count - 1] == chosen.reaches[reach.downstream].cells[0];
                for (int i = 1; i < reach.cells.Count; i++)
                    directed &= Hydrology.DownstreamCell(wd.dir, reach.cells[i - 1], wd.resX, wd.resZ) == reach.cells[i] &&
                        wd.filled[reach.cells[i - 1]] >= wd.filled[reach.cells[i]];
            }
            bool unchanged = FieldsIdentical(before, wd.filled);
            for (int i = 0; i < directions.Length; i++) unchanged &= directions[i] == wd.dir[i];
            Check(directed && unchanged && courses.Count == 22 && raw.hierarchy == null,
                "UC-T03-05 selected courses retain real downstream joins and downhill cells; neither the solve nor source network is changed.");

            RiverNetwork reversed = ReverseHierarchyReaches(raw);
            RiverNetwork reordered = RiverNetworkExtractor.SelectHierarchy(wd, reversed, targets, true, 1, null);
            var reorderedIds = new HashSet<int>();
            foreach (RiverReach reach in reordered.reaches) reorderedIds.Add(reach.courseId);
            Check(reordered.hierarchy.Feasible && courses.SetEquals(reorderedIds),
                "UC-T03-05 input reach ordering cannot change selected course identities or counts.");

            WorldDrainage smaller = HierarchyTestWorld(1000, out water);
            RiverNetwork smallRaw = RiverNetworkExtractor.Extract(smaller, options, water, null);
            RiverHierarchyReport small = RiverNetworkExtractor.SelectHierarchy(smaller, smallRaw, targets, true, 1, null).hierarchy;
            WorldDrainage larger = HierarchyTestWorld(4000, out water);
            RiverNetwork largeRaw = RiverNetworkExtractor.Extract(larger, options, water, null);
            RiverHierarchyReport large = RiverNetworkExtractor.SelectHierarchy(larger, largeRaw, targets, true, 1, null).hierarchy;
            Check(small.Feasible && small.RequestedTrunks == 1 && small.RequestedRivers == 1 && small.Streams == 4 &&
                small.SeparationMetres == 25 && !large.Feasible && large.RequestedTrunks == 8 && large.RequestedRivers == 16 &&
                large.Trunks == 2 && large.Rivers == 4 && large.SeparationMetres == 100 && large.Diagnostic.Contains("infeasible"),
                "UC-T03-05 area scaling rounds positive counts to at least one; larger terrain reports missing families instead of inventing routes.");
            RiverHierarchyReport absolute = RiverNetworkExtractor.SelectHierarchy(larger, largeRaw, targets, false, 1, null).hierarchy;
            Check(absolute.Feasible && absolute.RequestedTrunks == 2 && absolute.RequestedRivers == 4 && absolute.SeparationMetres == 50 &&
                targets.trunks == 2 && targets.rivers == 4 && targets.referenceAreaM2 == 4000000,
                "UC-T03-05 automatic scaling off preserves authored counts and physical separation without cumulative changes.");
            var restored = JsonUtility.FromJson<RiverHierarchyTargets>(JsonUtility.ToJson(targets));
            restored.rivers = 1;
            Check(restored.enabled && restored.trunks == 2 && restored.streamsPerRiver == 4 && restored.referenceAreaM2 == 4000000 &&
                restored.minimumSeparationMetres == 50 && restored.tolerance == 0 && targets.rivers == 4,
                "UC-T03-05 serialized hierarchy controls round-trip independently without replacing authored absolute settings.");

            targets.minimumSeparationMetres = 10000;
            RiverHierarchyReport separated = RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, null).hierarchy;
            Check(!separated.Feasible && separated.Trunks == 1 && separated.SeparationRejected > 0,
                "UC-T03-05 incompatible head spacing produces an explicit constrained shortfall.");
            targets.minimumSeparationMetres = 0;
            targets.trunks = 0; targets.rivers = 0;
            RiverNetwork empty = RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, null);
            Check(empty.reaches.Count == 0 && empty.hierarchy.Feasible,
                "UC-T03-05 zero authored counts emit no courses and no spurious infeasibility.");
            targets.trunks = 3; targets.rivers = 4; targets.tolerance = 0.34f;
            Check(RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, null).hierarchy.Feasible,
                "UC-T03-05 fractional count tolerance accepts a documented shortfall while preserving achieved counts.");
            targets.trunks = 2; targets.tolerance = 0;
            targets.streamsPerRiver = 5;
            RiverHierarchyReport scarceStreams = RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, null).hierarchy;
            Check(!scarceStreams.Feasible && scarceStreams.Streams == 16 && scarceStreams.RiversBelowStreamTarget == 4,
                "UC-T03-05 stream quotas are checked per river; surplus links cannot hide missing tributary courses.");
            targets.streamsPerRiver = 4;
            RiverNetwork dry = new RiverNetwork { resX = wd.resX, resZ = wd.resZ };
            Check(!RiverNetworkExtractor.SelectHierarchy(wd, dry, targets, false, 1, null).hierarchy.Feasible,
                "UC-T03-05 zero eligible channels reports unmet targets without synthesising terrain or water.");
            RiverNetwork cyclic = ReverseHierarchyReaches(raw);
            cyclic.reaches[0].downstream = 1; cyclic.reaches[1].downstream = 0;
            bool rejectedCycle = false;
            try { RiverNetworkExtractor.SelectHierarchy(wd, cyclic, targets, false, 1, null); }
            catch (InvalidOperationException) { rejectedCycle = true; }
            Check(rejectedCycle, "UC-T03-05 malformed cyclic reach input is rejected instead of looping or inventing a terminal trunk.");
            var stop = new StopToken(); stop.RequestStop();
            Check(RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, stop) == null,
                "UC-T03-05 cancelled hierarchy selection returns no partial network.");
            targets.minimumSeparationMetres = float.NaN;
            bool invalid = false;
            try { RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, null); }
            catch (ArgumentException) { invalid = true; }
            targets.enabled = false;
            Check(invalid && ReferenceEquals(raw, RiverNetworkExtractor.SelectHierarchy(wd, raw, targets, false, 1, null)),
                "UC-T03-05 enabled invalid controls fail explicitly; disabled targets preserve the original network exactly.");
            TestHierarchyProductionReport();
        }

        private static WorldDrainage HierarchyTestWorld(float extent, out bool[] water)
        {
            const int res = 128;
            var height = new Matrix(new CoordRect(0, 0, res, res))
            { worldPos = new Vector3(-extent, 0, -extent / 2), worldSize = new Vector3(extent, 600, extent), worldHeight = 600 };
            int[] direction = new int[res * res]; float[] accumulation = new float[res * res];
            water = new bool[res * res];
            for (int z = 0; z < res; z++) for (int x = 0; x < res; x++)
            {
                int i = z * res + x; direction[i] = -1;
                height.arr[i] = 0.9f - 0.003f * z - 0.0002f * x;
            }
            for (int family = 0; family < 2; family++)
            {
                int trunkX = 50 + family * 60;
                for (int z = 5; z < 120; z++) direction[z * res + trunkX] = DirCode(0, 1);
                accumulation[5 * res + trunkX] = 500;
                water[120 * res + trunkX] = true;
                for (int river = 0; river < 2; river++)
                {
                    int riverZ = 40 + river * 40;
                    for (int x = trunkX - 30; x < trunkX; x++) direction[riverZ * res + x] = DirCode(1, 0);
                    accumulation[riverZ * res + trunkX - 30] = 100;
                    for (int stream = 0; stream < 4; stream++)
                    {
                        int streamX = trunkX - 25 + stream * 5;
                        for (int z = riverZ - 10; z < riverZ; z++) direction[z * res + streamX] = DirCode(0, 1);
                        accumulation[(riverZ - 10) * res + streamX] = 10;
                    }
                }
            }
            // All directions increase x or z: row order gives exact accumulation of the declared catchments.
            for (int i = 0; i < direction.Length; i++)
            {
                int down = Hydrology.DownstreamCell(direction, i, res, res);
                if (down >= 0) { accumulation[i] += 1; accumulation[down] += accumulation[i]; }
            }
            return WorldDrainage.CreateForTests(height, null, direction, accumulation);
        }

        private static RiverNetwork ReverseHierarchyReaches(RiverNetwork source)
        {
            var result = new RiverNetwork { resX = source.resX, resZ = source.resZ };
            for (int i = source.reaches.Count - 1; i >= 0; i--)
            {
                RiverReach r = source.reaches[i];
                result.reaches.Add(new RiverReach { cells = r.cells, order = r.order, end = r.end,
                    downstream = r.downstream < 0 ? -1 : source.reaches.Count - 1 - r.downstream });
            }
            return result;
        }

        private static void TestHierarchyProductionReport()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tiles = new List<TileData>();
            try
            {
                var ground = new SearchWorldField { id = "hierarchy-ground", contentHash = 6101 };
                var river = new RiverNetworkNode { id = "hierarchy-river", contentHash = 6102, solveResolution = 32, seaLevel = 0.2f,
                    scaleToWorld = false, minDrainageAreaM2 = 1, slopeAreaThreshold = 0, slopeExponent = 0, minReachCells = 1,
                    hierarchyTargets = new RiverHierarchyTargets { enabled = true, trunks = 0, rivers = 0 } };
                graph.nodes.Add(ground); graph.nodes.Add(river);
                graph.AddConnection(new Connection(ground.id, 0, river.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph);
                Vector3 size = new Vector3(1200, 600, 600);
                TileData cold = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(cold);
                cold.retained = new Dictionary<string, RetainedProduct>();
                Matrix selected = (Matrix)evaluator.PullNode(river, 0, cold, null);
                Matrix source = (Matrix)evaluator.PullNode(ground, 0, cold, null);
                RiverHierarchyReport report = cold.outputs.riverHierarchyReports[river.id + ":hierarchy"];
                Check(FieldsIdentical(selected.arr, source.arr) && report.Feasible && report.Trunks == 0,
                    "UC-T03-05 production consumes zero targets, preserves ground and exposes achieved counts on the output bag.");
                TileData warm = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(warm);
                warm.retained = cold.retained;
                Matrix cached = (Matrix)evaluator.PullNode(river, 0, warm, null);
                Check(ReferenceEquals(selected, cached) && ReferenceEquals(report, warm.outputs.riverHierarchyReports[river.id + ":hierarchy"]),
                    "UC-T03-05 retained river output replays its original target report without another extraction.");
                warm.outputs.Clear();
                Check(warm.outputs.riverHierarchyReports.Count == 0,
                    "UC-T03-05 replacing an output bag clears obsolete hierarchy diagnostics.");
                river.hierarchyTargets.enabled = false;
                TileData all = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(all);
                Matrix full = (Matrix)evaluator.PullNode(river, 0, all, null);
                bool cut = false;
                for (int i = 0; i < full.arr.Length; i++) cut |= full.arr[i] < source.arr[i];
                Check(cut && all.outputs.riverHierarchyReports.Count == 0 && river.minDrainageAreaM2 == 1 && river.slopeAreaThreshold == 0,
                    "UC-T03-05 disabling targets restores thresholded production cuts and leaves absolute authored settings unchanged.");
            }
            finally
            {
                foreach (TileData tile in tiles) tile.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
