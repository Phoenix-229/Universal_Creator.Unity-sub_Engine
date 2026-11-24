using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable]
        private sealed class HaloSignalNode : CreatorNode
        {
            public override string NodeName => "Halo signal";
            // UC-T02-08: use the production creator metadata contract for halo evaluation.
            public override NodeCategory Category => NodeCategory.Creator;
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                for (int z = 0; z < matrix.rect.size.z; z++)
                    for (int x = 0; x < matrix.rect.size.x; x++)
                    {
                        float wx = matrix.worldPos.x + x * matrix.PixelSize.x;
                        float wz = matrix.worldPos.z + z * matrix.PixelSize.y;
                        matrix.arr[z * matrix.rect.size.x + x] = 0.5f + 0.23f * Mathf.Sin(wx * 0.13f) + 0.21f * Mathf.Cos(wz * 0.19f);
                    }
            }
        }

        // UC-T02-08: timestamps are injected into plain demand values; barriers control real admission without timing guesses.
        private static void TestGenerationDemand()
        {
            long now = Stopwatch.GetTimestamp();
            var distant = new GenerationDemand(false, 3, 900, now - 3 * Stopwatch.Frequency);
            var near = new GenerationDemand(false, 0, 20, now);
            var collision = new GenerationDemand(true, 3, 20, now);
            Check(GenerationDemand.Compare(distant, near, now) < 0 && GenerationDemand.Compare(collision, distant, now) < 0,
                "UC-T02-08 aged distant work progresses ahead of fresh visibility demand while collision-critical work stays first.");
            var turned = new GenerationDemand(false, 0, 10, now);
            var behind = new GenerationDemand(false, 3, 5, now);
            Check(GenerationDemand.Compare(turned, behind, now) < 0,
                "UC-T02-08 camera reversal changes fresh demand order without distance overriding visibility.");
            var oldest = new GenerationDemand(false, 3, 9999, now - 4 * Stopwatch.Frequency);
            Check(GenerationDemand.Compare(oldest, distant, now) < 0,
                "UC-T02-08 overdue work stays FIFO after teleport/reclassification instead of restarting its wait.");

            int originalLimit = WorldGenerationScheduler.GlobalWorkerLimit;
            var scheduler = new WorldGenerationScheduler();
            var tasks = new List<Task>();
            var order = new List<int>();
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                try
                {
                    WorldGenerationScheduler.ConfigureGlobalWorkerLimit(1);
                    var run = scheduler.CurrentRun;
                    tasks.Add(scheduler.RunWorkerAsync(run, 1, new StopToken(), () =>
                    {
                        entered.Set();
                        if (!release.Wait(5000)) throw new TimeoutException("UC-T02-08 demand fixture release timed out.");
                    }));
                    Check(entered.Wait(5000), "UC-T02-08 demand fixture holds the actual global admission slot.");
                    var movingDemand = new StopToken();
                    tasks.Add(scheduler.RunWorkerAsync(run, 1, movingDemand, () => order.Add(3), null, behind));
                    Check(scheduler.UpdateQueuedDemand(movingDemand, near),
                        "UC-T02-08 queued global requests accept camera-demand updates while preserving their original wait age.");
                    tasks.Add(scheduler.RunWorkerAsync(run, 1, new StopToken(), () => order.Add(2), null, distant));
                    tasks.Add(scheduler.RunWorkerAsync(run, 1, new StopToken(), () => order.Add(1), null, collision));
                    release.Set();
                    Check(Task.WaitAll(tasks.ToArray(), 10000) && order.Count == 3 && order[0] == 1 && order[1] == 2 && order[2] == 3,
                        "UC-T02-08 production worker admission serves collision, aged and fresh requests in order with one real worker.");
                }
                finally
                {
                    release.Set();
                    Task.WaitAll(tasks.ToArray(), 10000);
                    WorldGenerationScheduler.ConfigureGlobalWorkerLimit(originalLimit);
                }
            }
            TestBlurHaloContract();
            TestMixedResolutionCoarseReuse();
        }

        private static void TestMixedResolutionCoarseReuse()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new HaloSignalNode { id = "coarse-signal" };
            var consumer = new OwnershipReaderNode { id = "coarse-consumer" };
            graph.nodes.Add(source); graph.nodes.Add(consumer);
            graph.connections.Add(new Connection(source.id, 0, consumer.id, 0));
            GraphContentHasher.ComputeHashes(graph);
            var scheduler = new WorldGenerationScheduler();
            var memory = new ProductMemoryBudget(4 * 1024 * 1024);
            var tiles = new List<TileData>();
            using (var cache = new WorldDrainageCache(memory: memory))
            try
            {
                WorldDrainage previous = null;
                foreach (int resolution in new[] { 17, 33, 9 })
                {
                    var tile = new TileData(Coord.Zero, new CoordRect(0, 0, resolution, resolution),
                        new Vector3(resolution == 33 ? 32 : 0, 0, 0), new Vector3(32, 100, 32), 100);
                    tiles.Add(tile); tile.AttachMemory(memory); tile.AssignRun(scheduler.CurrentRun);
                    tile.SetFullWorld(Vector3.zero, new Vector3(64, 100, 32)); tile.Bind(new GraphEvaluator(graph));
                    tile.drainageCache = cache;
                    Task<WorldDrainage> solve = Task.Run(() => WorldDrainage.Solve(consumer, 0, tile, 16, 0, false, null));
                    Check(solve.Wait(5000) && solve.Result != null && (previous == null || ReferenceEquals(previous, solve.Result)),
                        "UC-T02-08 compatible child demand reuses the same immutable coarse product at resolution " + resolution + ".");
                    previous = solve.Result;
                }
                Check(cache.SolveCount == 1 && previous.height.worldPos == Vector3.zero && previous.height.worldSize.x == 64,
                    "UC-T02-08 traversal order and mixed child grids preserve one full-world drainage lattice and coordinate contract.");
            }
            finally
            {
                foreach (TileData tile in tiles) tile.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestBlurHaloContract()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new HaloSignalNode { id = "halo-signal" };
            var blur = new BlurNode { id = "halo-blur", radius = 32, intensity = 1 };
            graph.nodes.Add(source); graph.nodes.Add(blur);
            graph.connections.Add(new Connection(source.id, 0, blur.id, 0));
            var tiles = new List<TileData>();
            try
            {
                // Evaluate east before west, then compare each with the same-resolution whole-world field.
                var evaluator = new GraphEvaluator(graph);
                var whole = new TileData(Coord.Zero, new CoordRect(0, 0, 65, 33), Vector3.zero, new Vector3(64, 100, 32), 100);
                tiles.Add(whole);
                whole.Bind(evaluator);
                Matrix expected = (Matrix)evaluator.PullNode(blur, 0, whole, new StopToken());
                foreach (int start in new[] { 32, 0 })
                {
                    var tile = new TileData(new Coord(start / 32, 0), new CoordRect(start, 0, 33, 33),
                        new Vector3(start, 0, 0), new Vector3(32, 100, 32), 100);
                    tiles.Add(tile);
                    tile.Bind(evaluator);
                    Matrix actual = (Matrix)evaluator.PullNode(blur, 0, tile, new StopToken());
                    float error = 0;
                    for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++)
                        error = Mathf.Max(error, Mathf.Abs(actual.arr[z * 33 + x] - expected.arr[z * 65 + start + x]));
                    Check(error < 2e-6f, "UC-T02-08 full-radius blur halo matches whole-world evaluation for reversed cell " + start + ".");
                }
                var coarse = new TileData(Coord.Zero, new CoordRect(0, 0, 17, 9), Vector3.zero, new Vector3(32, 100, 32), 100);
                tiles.Add(coarse);
                Check(TileWindows.MarginForMetres(coarse, 8) == 5 && TileWindows.MarginForMetres(whole, 8) == 9,
                    "UC-T02-08 metre-based halo contracts conservatively cover unequal horizontal spacing and mixed resolutions.");
                bool refused = false;
                try { TileWindows.MarginForMetres(coarse, float.NaN); }
                catch (ArgumentOutOfRangeException) { refused = true; }
                Check(refused, "UC-T02-08 nonfinite halo demand fails explicitly before allocating a view.");
            }
            finally
            {
                foreach (TileData tile in tiles) tile.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
