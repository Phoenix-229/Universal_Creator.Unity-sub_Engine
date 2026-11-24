// UC-T03-06: distinct water mouths and directed, owned confluences through the production record builder.
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
        private static void TestDirectedRiverOutlets()
        {
            WorldDrainage wd = HierarchyTestWorld(2000, out bool[] wet);
            var options = RiverNetworkOptions.Default();
            options.minDrainageAreaM2 = 1; options.slopeAreaThreshold = 0; options.slopeExponent = 0; options.minReachCells = 3;
            RiverNetwork net = RiverNetworkExtractor.Extract(wd, options, wet, null);
            Guid world = Guid.NewGuid(); const string producer = "outlet-test";
            int count = wd.filled.Length;
            var ocean = new bool[count]; var lakes = new bool[count]; var ids = new string[count];
            ocean[120 * 128 + 50] = true; lakes[120 * 128 + 110] = true; ids[120 * 128 + 110] = "placed-lake-alpha";
            RiverOutletWater water = RiverOutletWater.Capture(world, producer, wd.resX, wd.resZ, ocean, lakes, ids, null);
            var width = new float[count]; var depth = new float[count];
            for (int i = 0; i < count; i++) if (!wet[i]) { width[i] = 2 + Mathf.Sqrt(wd.accum[i]); depth[i] = 2; }
            RiverOutletTopology topology = RiverNetworkExtractor.BuildOutlets(wd, net, water, world, producer, width, depth, 1, null);
            int seas = 0, lakeMouths = 0, joins = 0;
            var records = new Dictionary<string, RiverOutletRecord>(StringComparer.Ordinal);
            foreach (RiverOutletRecord record in topology.Reaches) records.Add(record.ReachId, record);
            bool dimensions = true, downhill = true;
            foreach (RiverOutletRecord record in topology.Reaches)
            {
                if (record.Kind == RiverOutletKind.Sea) seas++;
                if (record.Kind == RiverOutletKind.Lake)
                { lakeMouths++; dimensions &= record.ReceivingBodyId == "placed-lake-alpha"; }
                if (record.Kind == RiverOutletKind.ReceivingRiver)
                {
                    joins++;
                    RiverOutletRecord receiver = records[record.ReceivingReachId];
                    dimensions &= !record.IsWaterMouth && record.ReceivingBodyId == null && record.Position == receiver.SourcePosition &&
                        record.HalfWidthMetres == record.ReceivingHalfWidthMetres && record.DepthMetres == record.ReceivingDepthMetres &&
                        receiver.Order >= record.Order;
                }
                dimensions &= record.HalfWidthMetres > 0 && record.DepthMetres == 2;
                downhill &= record.SourcePosition.y >= record.Position.y && Mathf.Abs(record.DirectionXZ.sqrMagnitude - 1) < 1e-5f;
            }
            Check(seas == 1 && lakeMouths == 1 && joins == topology.Reaches.Count - 2 && joins > 2 && dimensions && downhill,
                "UC-T03-06 distinct sea/named-lake mouths and directed tributaries share receiver positions/dimensions; confluences do not multiply terminal outlets.");
            RiverOutletTopology reverse = RiverNetworkExtractor.BuildOutlets(wd, ReverseHierarchyReaches(net), water, world, producer, width, depth, 1, null);
            bool stable = reverse.Reaches.Count == topology.Reaches.Count;
            for (int i = 0; i < reverse.Reaches.Count; i++)
                stable &= reverse.Reaches[i].ReachId == topology.Reaches[i].ReachId &&
                    reverse.Reaches[i].ReceivingReachId == topology.Reaches[i].ReceivingReachId && reverse.Reaches[i].Order == topology.Reaches[i].Order;
            Check(stable && topology.WorldId == world && topology.ProducerId == producer,
                "UC-T03-06 directed identities and recomputed orders survive reversed extraction order within their owning world.");

            bool foreign = false, ambiguous = false;
            try { RiverNetworkExtractor.BuildOutlets(wd, net, water, Guid.NewGuid(), producer, width, depth, 1, null); }
            catch (InvalidOperationException) { foreign = true; }
            var overlapping = (bool[])lakes.Clone(); overlapping[120 * 128 + 50] = true;
            RiverOutletWater overlap = RiverOutletWater.Capture(world, producer, wd.resX, wd.resZ, (bool[])ocean.Clone(), overlapping, null, null);
            try { RiverNetworkExtractor.BuildOutlets(wd, net, overlap, world, producer, width, depth, 1, null); }
            catch (InvalidOperationException) { ambiguous = true; }
            Check(foreign && ambiguous, "UC-T03-06 foreign-world and ambiguous lake/ocean destinations reject the record build explicitly.");
            var stop = new StopToken(); stop.RequestStop();
            Check(RiverNetworkExtractor.BuildOutlets(wd, net, water, world, producer, width, depth, 1, stop) == null &&
                RiverOutletWater.Capture(world, producer, wd.resX, wd.resZ, ocean, lakes, null, stop) == null,
                "UC-T03-06 cancelled classification/extraction emits no partial topology.");
            TestUnresolvedRiverOutlets();
            TestOutletFootprintIdentity();
            TestOutletProductionPublication();
        }

        private static void TestUnresolvedRiverOutlets()
        {
            const int res = 8;
            var height = new Matrix(new CoordRect(0, 0, res, res))
            { worldPos = new Vector3(-200, 30, -100), worldSize = new Vector3(800, 600, 400), worldHeight = 600 };
            var dir = new int[64]; var accumulation = new float[64];
            for (int i = 0; i < 64; i++) { dir[i] = -1; height.arr[i] = 0.8f - 0.01f * (i / res); }
            var options = RiverNetworkOptions.Default();
            options.minDrainageAreaM2 = 1; options.slopeAreaThreshold = 0; options.slopeExponent = 0; options.minReachCells = 1;
            var width = new float[64]; var depth = new float[64];
            for (int i = 0; i < 64; i++) { width[i] = 3; depth[i] = 1; }
            bool classified = true;
            for (int scenario = 0; scenario < 3; scenario++)
            {
                for (int i = 0; i < 64; i++) { dir[i] = -1; accumulation[i] = 0; }
                int endZ = scenario == 0 ? 5 : 7;
                for (int z = 2; z < endZ; z++)
                { int c = z * res + 3; dir[c] = DirCode(0, 1); accumulation[c] = scenario == 2 && z >= 5 ? 0 : 1; }
                WorldDrainage wd = WorldDrainage.CreateForTests(height, null, dir, accumulation);
                RiverNetwork net = RiverNetworkExtractor.Extract(wd, options, null, null);
                RiverOutletWater water = RiverOutletWater.Capture(Guid.Empty, "dry", res, res, new bool[64], new bool[64], null, null);
                RiverOutletTopology result = RiverNetworkExtractor.BuildOutlets(wd, net, water, Guid.Empty, "dry", width, depth, 1, null);
                classified &= result.Reaches.Count == 1 && result.Reaches[0].Kind ==
                    (scenario == 0 ? RiverOutletKind.Endorheic : RiverOutletKind.UnresolvedBoundary) && !result.Reaches[0].IsWaterMouth &&
                    !string.IsNullOrEmpty(result.Reaches[0].Diagnostic);
            }
            Check(classified, "UC-T03-06 interior sinks, actual world edges and internal threshold gaps are explicit destinations; none is silently called ocean.");

            // A flat two-cell cycle has valid individual flow steps but invalid global topology.
            height.Fill(0.5f); dir[10] = DirCode(1, 0); dir[11] = DirCode(-1, 0);
            WorldDrainage cycleWorld = WorldDrainage.CreateForTests(height, null, dir, accumulation);
            var cycle = new RiverNetwork { resX = res, resZ = res };
            cycle.reaches.Add(new RiverReach { cells = new List<int> { 10, 11 }, downstream = 1, end = RiverReachEnd.Confluence });
            cycle.reaches.Add(new RiverReach { cells = new List<int> { 11, 10 }, downstream = 0, end = RiverReachEnd.Confluence });
            RiverOutletWater dry = RiverOutletWater.Capture(Guid.Empty, "cycle", res, res, new bool[64], new bool[64], null, null);
            bool rejected = false;
            try { RiverNetworkExtractor.BuildOutlets(cycleWorld, cycle, dry, Guid.Empty, "cycle", width, depth, 1, null); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("cycle"); }
            Check(rejected, "UC-T03-06 bounded topological ordering rejects a complete directed cycle.");
        }

        private static void TestOutletFootprintIdentity()
        {
            var node = new RiverNetworkNode();
            var footprint = new HeightOutputNode.LakeFootprint { bodyId = "lake-a", ringXZ = new[] { Vector2.zero, Vector2.right, Vector2.one } };
            var footprints = new List<HeightOutputNode.LakeFootprint> { footprint };
            node.SetLakeFootprints(footprints);
            var copy = new RiverNetworkNode(); node.CopyInputsTo(copy);
            footprint.bodyId = "lake-b";
            var copied = (HeightOutputNode.PreparedLakeFootprints)copy.GetPreparedInputsForHash();
            Check(node.SetLakeFootprints(footprints) && copied.footprints[0].bodyId == "lake-a" &&
                !ReferenceEquals(copied.footprints[0].ringXZ, footprint.ringXZ),
                "UC-T03-06 named lake identity changes invalidate prepared inputs while captured footprint identities remain independent.");
        }

        private static void TestOutletProductionPublication()
        {
            var root = new GameObject("UC outlet publication owner"); root.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>();
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tiles = new List<TileData>();
            try
            {
                var ground = new SearchWorldField { id = "outlet-ground", contentHash = 6201 };
                var river = new RiverNetworkNode { id = "outlet-river", contentHash = 6202, solveResolution = 32, seaLevel = 0.2f,
                    scaleToWorld = false, minDrainageAreaM2 = 1, slopeAreaThreshold = 0, slopeExponent = 0, minReachCells = 1 };
                graph.nodes.Add(ground); graph.nodes.Add(river); graph.AddConnection(new Connection(ground.id, 0, river.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph); Vector3 size = new Vector3(1200, 600, 600);
                TileData cold = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(cold);
                cold.AssignRun(world.GenerationScheduler.CurrentRun); cold.retained = new Dictionary<string, RetainedProduct>();
                evaluator.PullNode(river, 0, cold, null); cold.FinishPublications();
                ChannelGraphPublication[] channels = cold.ChannelPublications();
                Check(channels.Length == 1 && channels[0].Outlets != null && channels[0].Outlets.Reaches.Count > 0 &&
                    channels[0].Outlets.WorldId == cold.RunId.WorldId,
                    "UC-T03-06 production channel publication includes world-owned directed outlet records.");
                TileData warm = SearchTile(evaluator, Vector3.zero, size, Vector3.zero, size, 33); tiles.Add(warm);
                warm.AssignRun(cold.RunId); warm.retained = cold.retained;
                evaluator.PullNode(river, 0, warm, null); warm.FinishPublications();
                Check(ReferenceEquals(channels[0].Outlets, warm.ChannelPublications()[0].Outlets),
                    "UC-T03-06 cached channel fields replay identical topology without reclassification.");
                TileData west = SearchTile(evaluator, Vector3.zero, new Vector3(600, 600, 600), Vector3.zero, size, 17); tiles.Add(west);
                TileData east = SearchTile(evaluator, new Vector3(600, 0, 0), new Vector3(600, 600, 600), Vector3.zero, size, 33); tiles.Add(east);
                east.AssignRun(cold.RunId); west.AssignRun(cold.RunId);
                evaluator.PullNode(river, 1, east, null); evaluator.PullNode(river, 0, west, null);
                RiverOutletTopology eastTopology = east.ChannelPublications()[0].Outlets;
                RiverOutletTopology westTopology = west.ChannelPublications()[0].Outlets;
                bool chunkStable = eastTopology.Reaches.Count == channels[0].Outlets.Reaches.Count && westTopology.Reaches.Count == eastTopology.Reaches.Count;
                for (int i = 0; chunkStable && i < eastTopology.Reaches.Count; i++)
                    chunkStable &= eastTopology.Reaches[i].ReachId == westTopology.Reaches[i].ReachId &&
                        eastTopology.Reaches[i].Position == westTopology.Reaches[i].Position &&
                        eastTopology.Reaches[i].Position == channels[0].Outlets.Reaches[i].Position &&
                        eastTopology.Reaches[i].Kind == westTopology.Reaches[i].Kind;
                Check(chunkStable, "UC-T03-06 actual height/mask chunk sampling preserves whole-world outlet identities/positions across mixed resolutions and reversed chunk order.");
                warm.AssignRun(new GenerationRunId(Guid.NewGuid(), 1));
                bool foreign = false;
                try { GraphPublicationCommit.Prepare(world, warm.outputs); }
                catch (InvalidOperationException) { foreign = true; }
                Check(foreign && !GraphPublicationCommit.Commit(world, warm.outputs, null),
                    "UC-T03-06 native-commit preparation and direct commit reject foreign retained outlet records.");
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
