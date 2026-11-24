// UC-T06-03: absolute regional masks agree at shared cells regardless of evaluation order; native region acceptance uses the existing binding fixture.
using System;
using System.Linq;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationRegionMasks()
        {
            var coverage = new[] {
                new GraphVegetationRegion { Position = Vector3.zero, Size = new Vector3(8, 100, 8) },
                new GraphVegetationRegion { Position = new Vector3(8, 0, 0), Size = new Vector3(8, 100, 8) }
            };
            Check(GraphVegetationPatch.HasCoverage(coverage, new Rect(0, 0, 16, 8), null) &&
                GraphVegetationPatch.HasCoverage(coverage.Reverse().ToArray(), new Rect(0, 0, 16, 8), null),
                "UC-T06-03 full adjacent resident coverage is independent of capture order.");
            coverage[1].Position.x = 4;
            Check(!GraphVegetationPatch.HasCoverage(coverage, new Rect(0, 0, 16, 8), null),
                "UC-T06-03 overlapping resident area cannot compensate for an unloaded patch edge.");
            coverage[1].Position.x = 8.001f;
            Check(!GraphVegetationPatch.HasCoverage(coverage, new Rect(0, 0, 16, 8), null),
                "UC-T06-03 a narrow unloaded interior gap remains pending without guessed tolerances.");
            TestVegetationPatchMerge();
            var node = new VegetationRegionMaskNode { minimumWorldXZ = new Vector2(4, 0), sizeMetres = new Vector2(24, 16), fadeMetres = 4 };
            var left = new TileData(Coord.Zero, new CoordRect(0, 0, 17, 17), Vector3.zero, new Vector3(16, 100, 16), 100);
            var right = new TileData(new Coord(1, 0), new CoordRect(0, 0, 17, 17), new Vector3(16, 0, 0), new Vector3(16, 100, 16), 100);
            var whole = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 17), Vector3.zero, new Vector3(32, 100, 16), 100);
            try
            {
                var b = (Matrix)node.Generate(0, right, new StopToken());
                var a = (Matrix)node.Generate(0, left, new StopToken());
                var all = (Matrix)node.Generate(0, whole, new StopToken());
                bool equal = true;
                for (int z = 0; z < 17; z++) for (int x = 0; x < 17; x++)
                    equal &= a.arr[z * 17 + x] == all.arr[z * 33 + x] && b.arr[z * 17 + x] == all.arr[z * 33 + x + 16];
                Check(equal && all.arr.All(v => v >= 0 && v <= 1), "UC-T06-03 regional density is bounded and agrees across adjacent/overlapping demand tiles in reverse order.");
                Check(all.arr[8 * 33 + 3] == 0 && all.arr[8 * 33 + 4] == 0 && all.arr[8 * 33 + 6] == 0.5f && all.arr[8 * 33 + 8] == 1,
                    "UC-T06-03 world-metre regional fade follows independently known rectangle distances.");
                node.fadeMetres = 0;
                var hard = (Matrix)node.Generate(0, whole, new StopToken());
                Check(hard.arr[8 * 33 + 4] == 1 && hard.arr[8 * 33 + 28] == 0,
                    "UC-T06-03 zero-fade regions include their minimum and exclude their maximum boundary.");
                var stop = new StopToken(); stop.RequestStop();
                Check(node.Generate(0, whole, stop) == null, "UC-T06-03 regional mask cancellation returns no partial publication.");
                node.sizeMetres.x = float.NaN; bool rejected = false;
                try { node.Generate(0, whole, null); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "UC-T06-03 nonfinite regional dimensions fail before matrix sampling.");
            }
            finally { left.ReleaseProductMemory(); right.ReleaseProductMemory(); whole.ReleaseProductMemory(); }
        }

        private static void TestVegetationPatchMerge()
        {
            var prior = new OutputBag(); var next = new OutputBag();
            var inside = new ScatterInstance(new Vector3(1, 0, 1), Quaternion.identity, Vector3.one, 0) { instanceId = 1 };
            var outside = new ScatterInstance(new Vector3(4, 0, 1), Quaternion.identity, Vector3.one, 0) { instanceId = 2 };
            var replacement = new ScatterInstance(new Vector3(2, 0, 1), Quaternion.identity, Vector3.one, 0) { instanceId = 3 };
            var excluded = new ScatterInstance(new Vector3(6, 0, 1), Quaternion.identity, Vector3.one, 0) { instanceId = 4 };
            prior.scatter["trees"] = new System.Collections.Generic.List<ScatterInstance> { inside, outside };
            next.scatter["trees"] = new System.Collections.Generic.List<ScatterInstance> { replacement, excluded };
            prior.detailLayers[0] = new int[,] { { 1, 1 }, { 1, 1 } };
            next.detailLayers[0] = new int[,] { { 5, 5, 5, 5 }, { 5, 5, 5, 5 }, { 5, 5, 5, 5 }, { 5, 5, 5, 5 } };
            prior.detailLayers[1] = new int[,] { { 7, 7 }, { 7, 7 } };
            GraphVegetationPatch.Merge(prior, next, Vector3.zero, new Vector3(8, 100, 8), new Rect(0, 0, 4, 8), new StopToken());
            Check(next.scatter["trees"].SequenceEqual(new[] { outside, replacement }) && prior.scatter["trees"].SequenceEqual(new[] { inside, outside }),
                "UC-T06-03 patch replaces only inside instances, includes the exact maximum in preserved outside placement and never aliases/edits prior collections.");
            Check(next.detailLayers[0].Cast<int>().SequenceEqual(new[] { 5, 1, 5, 1 }) && next.detailLayers[1].Cast<int>().SequenceEqual(new[] { 0, 7, 0, 7 }) &&
                prior.detailLayers[0].Cast<int>().All(v => v == 1),
                "UC-T06-03 patch preserves outside grass cells and the prior lattice across changed resolution and intentionally removed inside layers.");
            bool rejected = false;
            try { GraphVegetationPatch.ValidateBounds(new Rect(0, 0, float.NaN, 1)); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "UC-T06-03 invalid patch bounds fail before application.");
            var stop = new StopToken(); stop.RequestStop(); bool cancelled = false;
            try { GraphVegetationPatch.Merge(prior, new OutputBag(), Vector3.zero, new Vector3(8, 100, 8), new Rect(0, 0, 4, 8), stop); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && prior.scatter["trees"].Count == 2 && prior.detailLayers[0][0, 0] == 1,
                "UC-T06-03 cancelled patch merging leaves prior accepted planting intact.");
        }
    }
}
