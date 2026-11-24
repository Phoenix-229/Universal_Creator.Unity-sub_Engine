// UC-T03-07: independently authored lake spills plus the actual River Network publication path.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestLakeOutletSources()
        {
            WorldDrainage routing = LakeOutletTestWorld(out WorldDrainage flat, out RiverOutletWater water);
            var sources = RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, null);
            LakeOutletSource source = sources[0];
            Check(sources.Count == 1 && source.HasValidSpill && source.Selected && source.SpillCell == 26 &&
                source.SourceCell == 27 && source.ReceivingCell == 30 && source.ReceivingBodyId == "ocean" &&
                Mathf.Abs(source.SurfaceMetres - 480) < 0.001f && source.SpillPosition.y == source.SurfaceMetres,
                "UC-T03-07 an independently authored open lake has one dry spill at its flat captured water level.");
            var options = RiverNetworkOptions.Default(); options.minDrainageAreaM2 = float.MaxValue;
            options.slopeAreaThreshold = float.MaxValue; options.minReachCells = 20;
            var wet = new bool[64]; wet[25] = wet[26] = wet[30] = true;
            RiverNetwork absent = RiverNetworkExtractor.Extract(routing, options, wet, null);
            RiverNetwork network = RiverNetworkExtractor.Extract(routing, options, wet, sources, null);
            Check(absent.reaches.Count == 0 && network.reaches.Count == 1 &&
                network.reaches[0].cells.Count == 4 && network.reaches[0].cells[0] == 27 &&
                network.reaches[0].cells[3] == 30 && !network.reaches[0].cells.Contains(26),
                "UC-T03-07 a valid short lake connector bypasses initiation/length thresholds and never channels the protected source lake.");
            LakeOutletSnapshot snapshot = RiverNetworkExtractor.BindLakeOutlets(routing, network, sources, water.WorldId, "lake-test", 1, null);
            Check(snapshot.Lakes[0].DownstreamReachId == "lake-test:reach:27" && snapshot.Lakes[0].Selected,
                "UC-T03-07 admitted lake source binds to the actual selected directed reach identity.");
            var capped = RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, 0, null);
            Check(capped[0].HasValidSpill && !capped[0].Selected && !string.IsNullOrEmpty(capped[0].Diagnostic) &&
                RiverNetworkExtractor.Extract(routing, options, wet, capped, null).reaches.Count == 0,
                "UC-T03-07 zero outlet coverage keeps the valid spill diagnostic without inventing a channel.");
            var closed = RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water,
                new Dictionary<string, float> { { "source-lake", 450 } }, 0.2f, 0.01f, -1, null);
            Check(!closed[0].HasValidSpill && !closed[0].Selected && closed[0].SourceCell == -1 && closed[0].SpillCell == -1,
                "UC-T03-07 an authored lake below its real spill remains closed and emits no source.");
            var orderSources = RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, null);
            var orderSnapshot = RiverNetworkExtractor.BindLakeOutlets(routing, network, orderSources, water.WorldId, "lake-test", 2, null);
            Check(!orderSnapshot.Lakes[0].Selected && orderSnapshot.Lakes[0].DownstreamReachId == null,
                "UC-T03-07 authored order filtering is reflected in lake-source activation diagnostics.");

            // All terrain edits are independent of the routing fill: catch false downhill claims.
            for (int c = 25; c <= 29; c++) { routing.filled[c] = 0.8f + (30 - c) * 1e-5f; flat.filled[c] = 0.8f; }
            routing.height.arr[28] = 0.4f; routing.height.arr[29] = 0.6f;
            Check(!RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, null)[0].HasValidSpill,
                "UC-T03-07 descending filled routing cannot force a source through a dry uphill pit.");
            routing.height.arr[28] = routing.height.arr[29] = 0.8f;
            Check(RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, null)[0].HasValidSpill,
                "UC-T03-07 a flat spill remains valid without inventing a terrain drop.");
            routing.height.arr[28] = 0.65f; routing.height.arr[29] = 0.4f;
            var stop = new StopToken(); stop.RequestStop();
            Check(RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, stop) == null &&
                RiverNetworkExtractor.Extract(routing, options, wet, sources, stop) == null,
                "UC-T03-07 cancellation publishes neither source records nor a partial outlet network.");
            bool malformed = false;
            try { RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water,
                new Dictionary<string, float> { { "source-lake", float.NaN } }, 0.2f, 0.01f, -1, null); }
            catch (ArgumentException) { malformed = true; }
            Check(malformed, "UC-T03-07 nonfinite authored lake levels are rejected before source publication.");
            // Inlet and outlet are separated by protected source water, never joined by a trench through it.
            routing.dir[9] = routing.dir[17] = DirCode(0, 1);
            var inletOptions = options; inletOptions.minDrainageAreaM2 = 0; inletOptions.slopeAreaThreshold = 0;
            inletOptions.slopeExponent = 0; inletOptions.minReachCells = 1;
            var inletNet = RiverNetworkExtractor.Extract(routing, inletOptions, wet, sources, null);
            bool inlet = false, outlet = false;
            foreach (RiverReach reach in inletNet.reaches)
            {
                inlet |= reach.cells[0] == 9 && reach.cells[reach.cells.Count - 1] == 25;
                outlet |= reach.cells[0] == 27 && reach.cells[reach.cells.Count - 1] == 30;
            }
            Check(inlet && outlet, "UC-T03-07 a lake inlet and its dry outflow are distinct reaches separated by protected water.");
            TestChainedLakeOutletSources();
            TestLakeOutletProduction();
        }

        private static WorldDrainage LakeOutletTestWorld(out WorldDrainage flat, out RiverOutletWater water)
        {
            var height = new Matrix(new CoordRect(0, 0, 8, 8));
            height.worldPos = Vector3.zero; height.worldSize = new Vector3(700, 600, 700); height.worldHeight = 600;
            height.Fill(0.95f);
            var filled = (float[])height.arr.Clone(); var flatFill = (float[])filled.Clone();
            var directions = new int[64]; var accumulation = new float[64];
            for (int c = 0; c < 64; c++) { directions[c] = -1; accumulation[c] = 1; }
            height.arr[25] = height.arr[26] = 0.3f;
            for (int c = 25; c <= 30; c++)
            {
                if (c >= 27) height.arr[c] = c == 27 ? 0.8f : c == 28 ? 0.65f : c == 29 ? 0.4f : 0.2f;
                flatFill[c] = c <= 27 ? 0.8f : height.arr[c];
                filled[c] = flatFill[c] + (c < 27 ? (27 - c) * 1e-5f : 0);
                if (c < 30) directions[c] = DirCode(1, 0);
                accumulation[c] = c - 20;
            }
            var ocean = new bool[64]; var lakes = new bool[64]; var ids = new string[64];
            ocean[30] = true; lakes[25] = lakes[26] = true; ids[25] = ids[26] = "source-lake";
            water = RiverOutletWater.Capture(Guid.NewGuid(), "lake-test", 8, 8, ocean, lakes, ids, null);
            flat = WorldDrainage.CreateForTests(height, flatFill);
            return WorldDrainage.CreateForTests(height, filled, directions, accumulation);
        }

        private static void TestChainedLakeOutletSources()
        {
            WorldDrainage routing = LakeOutletTestWorld(out WorldDrainage flat, out RiverOutletWater unused);
            var ocean = new bool[64]; var lakes = new bool[64]; var ids = new string[64];
            lakes[25] = lakes[26] = lakes[29] = true;
            ids[25] = ids[26] = "lake-a"; ids[29] = "lake-b"; ocean[31] = true;
            routing.height.arr[30] = flat.filled[30] = routing.filled[30] = 0.35f;
            routing.height.arr[31] = flat.filled[31] = routing.filled[31] = 0.2f;
            routing.dir[30] = DirCode(1, 0);
            var water = RiverOutletWater.Capture(Guid.Empty, "chain", 8, 8, ocean, lakes, ids, null);
            var sources = RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, null);
            Check(sources.Count == 2 && sources[0].Selected && sources[0].ReceivingBodyId == "lake-b" &&
                sources[1].Selected && sources[1].ReceivingBodyId == "ocean",
                "UC-T03-07 a lake chain emits one source per real spill with distinct downstream body identities.");
            var limited = RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, 1, null);
            Check(limited.FindAll(item => item.Selected).Count == 1,
                "UC-T03-07 bounded outlet coverage selects exactly the requested valid source count.");
            var emptyWater = RiverOutletWater.Capture(Guid.Empty, "dry", 8, 8, new bool[64], new bool[64], null, null);
            Check(RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, emptyWater, null, 0.2f, 0.01f, -1, null).Count == 0,
                "UC-T03-07 dry worlds produce no lake-source records.");
            routing.height.arr[27] = routing.height.arr[28] = routing.height.arr[30] = 0.8f;
            flat.filled[28] = routing.filled[28] = 0.8f;
            routing.height.arr[29] = 0.3f;
            flat.filled[29] = flat.filled[30] = routing.filled[29] = routing.filled[30] = 0.8f;
            routing.dir[30] = DirCode(-1, 0); routing.dir[28] = DirCode(1, 0);
            // A -> B through 27/28; B -> A through a separate row, all at equal water level.
            routing.dir[29] = DirCode(0, 1); routing.dir[37] = DirCode(-1, 0);
            routing.dir[36] = DirCode(-1, 0); routing.dir[35] = DirCode(-1, 0);
            routing.dir[34] = DirCode(0, -1);
            foreach (int c in new[] { 34, 35, 36, 37 }) routing.height.arr[c] = flat.filled[c] = routing.filled[c] = 0.8f;
            bool cycle = false;
            try { RiverNetworkExtractor.ResolveLakeOutlets(routing, flat, water, null, 0.2f, 0.01f, -1, null); }
            catch (InvalidOperationException) { cycle = true; }
            Check(cycle, "UC-T03-07 equal-level lake-to-lake cycles are rejected across protected water boundaries.");
        }

        [Serializable]
        private sealed class LakeOutletGround : Generator
        {
            public override string NodeName => "Lake outlet fixture ground";
            public override NodeCategory Category => NodeCategory.Creator;
            public override Port[] GetInputs() => Array.Empty<Port>();
            public override Port[] GetOutputs() => new[] { new Port("Height", PortType.Matrix, PortDirection.Output, true) };
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                Matrix result = tile.CreateMatrix(); Vector2 pixel = result.PixelSize;
                for (int c = 0; c < result.arr.Length; c++)
                {
                    float x = result.worldPos.x + (c % result.rect.size.x) * pixel.x;
                    float z = result.worldPos.z + (c / result.rect.size.x) * pixel.y;
                    result.arr[c] = x >= 100 && x < 300 && z >= 200 && z <= 400 ? 0.3f :
                        x >= 300 && z >= 250 && z <= 350 ? 0.8f - (x - 300) * 0.0007f : 0.95f;
                }
                return result;
            }
        }

        private static void TestLakeOutletProduction()
        {
            var root = new GameObject("UC lake outlet owner"); root.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>();
            Graph graph = ScriptableObject.CreateInstance<Graph>(); var tiles = new List<TileData>();
            try
            {
                var ground = new LakeOutletGround { id = "lake-ground", contentHash = 7301 };
                var river = new RiverNetworkNode { id = "lake-river", contentHash = 7302, solveResolution = 32,
                    seaLevel = 0.2f, scaleToWorld = false, minDrainageAreaM2 = float.MaxValue, slopeAreaThreshold = float.MaxValue,
                    minReachCells = 1, naturalRuggedness = 0, lakeBasinDepth = 0.01f };
                var footprint = new HeightOutputNode.LakeFootprint { bodyId = "placed-source", hasSurfaceLevel = true, surfaceMetres = 480,
                    ringXZ = new[] { new Vector2(100, 200), new Vector2(300, 200), new Vector2(300, 400), new Vector2(100, 400) },
                    planBounds = new Bounds(new Vector3(200, 0, 300), new Vector3(200, 1, 200)) };
                river.SetLakeFootprints(new List<HeightOutputNode.LakeFootprint> { footprint });
                graph.nodes.Add(ground); graph.nodes.Add(river); graph.AddConnection(new Connection(ground.id, 0, river.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); var evaluator = new GraphEvaluator(graph);
                Vector3 size = new Vector3(1200, 600, 600);
                TileData cold = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(cold);
                cold.AssignRun(world.GenerationScheduler.CurrentRun);
                cold.retained = new Dictionary<string, RetainedProduct>();
                Matrix carved = evaluator.PullNode(river, 0, cold, null) as Matrix;
                LakeOutletSnapshot snapshot = cold.ChannelPublications()[0].LakeOutlets;
                LakeOutletSource source = null; foreach (var lake in snapshot.Lakes) if (lake.LakeId == "placed-source") source = lake;
                Check(source != null && source.Selected && source.DownstreamReachId != null && source.SurfaceMetres == 480,
                    "UC-T03-07 production captures a placed lake level and routes its dry downstream channel below ordinary initiation thresholds.");
                Matrix original = ground.Generate(0, cold, null) as Matrix; bool cut = false, protectedWater = true;
                for (int c = 0; c < carved.arr.Length; c++)
                {
                    float x = carved.worldPos.x + (c % carved.rect.size.x) * carved.PixelSize.x;
                    float z = carved.worldPos.z + (c / carved.rect.size.x) * carved.PixelSize.y;
                    cut |= carved.arr[c] < original.arr[c];
                    if (x >= 160 && x <= 240 && z >= 250 && z <= 350) protectedWater &= carved.arr[c] == original.arr[c];
                }
                Check(cut && protectedWater, "UC-T03-07 actual terrain carving creates the outlet while preserving interior placed-lake ground.");
                TileData warm = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(warm); warm.retained = cold.retained;
                warm.AssignRun(cold.RunId);
                evaluator.PullNode(river, 0, warm, null);
                Check(ReferenceEquals(snapshot, warm.ChannelPublications()[0].LakeOutlets) && warm.outputs.lakeOutletReports.Count == 1,
                    "UC-T03-07 retained generation replays one identical lake-source snapshot without duplicate outlets.");
                TileData east = SearchTile(evaluator, new Vector3(600, 0, 0), new Vector3(600, 600, 600), Vector3.zero, size, 33); tiles.Add(east);
                TileData west = SearchTile(evaluator, Vector3.zero, new Vector3(600, 600, 600), Vector3.zero, size, 17); tiles.Add(west);
                east.AssignRun(cold.RunId); west.AssignRun(cold.RunId);
                evaluator.PullNode(river, 1, east, null); evaluator.PullNode(river, 0, west, null);
                var eastLakes = east.ChannelPublications()[0].LakeOutlets.Lakes; var westLakes = west.ChannelPublications()[0].LakeOutlets.Lakes;
                bool same = eastLakes.Count == westLakes.Count;
                for (int i = 0; same && i < eastLakes.Count; i++) same &= eastLakes[i].LakeId == westLakes[i].LakeId &&
                    eastLakes[i].SpillPosition == westLakes[i].SpillPosition && eastLakes[i].DownstreamReachId == westLakes[i].DownstreamReachId;
                Check(same, "UC-T03-07 reversed mixed-resolution chunk pulls preserve whole-world spill positions and downstream reach identities.");
                warm.AssignRun(new GenerationRunId(Guid.NewGuid(), 1)); warm.FinishPublications();
                bool foreign = false;
                try { GraphPublicationCommit.Prepare(world, warm.outputs); }
                catch (InvalidOperationException) { foreign = true; }
                Check(foreign && !GraphPublicationCommit.Commit(world, warm.outputs, null),
                    "UC-T03-07 native commit refuses retained lake-source records from another world.");
                river.generateLakeOutlets = false; river.contentHash++;
                TileData disabled = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(disabled);
                Matrix unchanged = evaluator.PullNode(river, 0, disabled, null) as Matrix;
                bool unchangedGround = true;
                for (int c = 0; c < unchanged.arr.Length; c++) unchangedGround &= unchanged.arr[c] == original.arr[c];
                Check(unchangedGround && disabled.outputs.lakeOutletReports.Count == 1 && !disabled.ChannelPublications()[0].HasChannelCoverage,
                    "UC-T03-07 disabling lake-source generation preserves terrain and reports inactive valid spills.");
                var copied = new RiverNetworkNode(); river.CopyInputsTo(copied); footprint.surfaceMetres = 450;
                var prepared = (HeightOutputNode.PreparedLakeFootprints)copied.GetPreparedInputsForHash();
                Check(river.SetLakeFootprints(new List<HeightOutputNode.LakeFootprint> { footprint }) && prepared.footprints[0].surfaceMetres == 480,
                    "UC-T03-07 editing a placed lake level invalidates current preparation without changing its captured snapshot.");
            }
            finally
            {
                foreach (TileData tile in tiles) tile.ReleaseProductMemory();
                world.ProductMemory.ClearRetained();
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
