using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable]
        private sealed class MemoryCountingNode : CreatorNode
        {
            internal int computes;
            public override string NodeName => "Memory counting field";
            // UC-T02-06: the allocation fixture remains a complete generator implementation.
            public override NodeCategory Category => NodeCategory.Creator;
            /// <inheritdoc />
            protected override void Create(Matrix matrix, TileData tile, StopToken stop) { computes++; matrix.Fill(0.375f); }
        }

        // UC-T02-06: exercise real retained evaluation and byte admission, not only dictionary entry counts.
        private static void TestProductMemoryBudget()
        {
            TestGeneratedTextureMemory(); // UC-T02-06: transient native ownership, persistence and refusal before oversized source reads.
            for (int repetition = 0; repetition < 3; repetition++)
            {
                var budget = new ProductMemoryBudget(16);
                var a = new object(); var b = new object(); var c = new object();
                ProductMemoryBudget.Entry first, second;
                using (var pin = budget.Acquire(a, 8, 0, "a")) { first = pin.Entry; budget.Retain(first); }
                using (var pin = budget.Acquire(b, 8, 0, "b")) { second = pin.Entry; budget.Retain(second); }
                using (var hit = budget.TryAcquire(first)) Check(hit != null, "UC-T02-06 retained touch acquires an in-use pin.");
                using (var pin = budget.Acquire(c, 8, 0, "c"))
                {
                    Check(first.Product == a && second.Product == null && budget.Evictions == 1 && budget.PeakBytes == 16,
                        "UC-T02-06 deterministic LRU releases the least recently used eligible payload within the byte cap.");
                    bool refused = false;
                    try { using (budget.Reserve(16)) { } } catch (InvalidOperationException) { refused = true; }
                    Check(refused && ReferenceEquals(pin.Entry.Product, c) && budget.PinnedBytes == 8,
                        "UC-T02-06 pressure refuses admission without evicting a live consumer.");
                }
                budget.ClearRetained();
                Check(budget.ManagedBytes == 0 && budget.NativeBytes == 0, "UC-T02-06 release and world withdrawal return accounted residency to zero.");
            }

            var nativeBudget = new ProductMemoryBudget(1024);
            var native = nativeBudget.Reserve(128, 768);
            Check(nativeBudget.ManagedBytes == 128 && nativeBudget.NativeBytes == 768 && nativeBudget.PeakBytes == 896,
                "UC-T02-06 native and transient reservations contribute to one world peak.");
            native.Dispose(); native.Dispose();
            Check(nativeBudget.ManagedBytes == 0 && nativeBudget.NativeBytes == 0, "UC-T02-06 repeated lease release is harmless.");

            var drainageMemory = new ProductMemoryBudget(2048);
            using (var cache = new WorldDrainageCache(memory: drainageMemory))
            {
                var tooLarge = WorldDrainage.CreateForTests(new Matrix(new CoordRect(0, 0, 33, 33)), null);
                Task<bool> refused = Task.Run(() =>
                {
                    try { cache.GetOrCompute("drainage", _ => tooLarge); return false; }
                    catch (InvalidOperationException) { return true; }
                });
                Check(refused.Wait(5000) && refused.Result && cache.InFlightCount == 0,
                    "UC-T02-06 drainage budget failure retires its shared task instead of stranding followers.");
                var small = WorldDrainage.CreateForTests(new Matrix(new CoordRect(0, 0, 3, 3)), null);
                TileData consumer = MemoryTestTile(drainageMemory);
                try
                {
                    Task<WorldDrainage> retry = Task.Run(() => cache.GetOrCompute("drainage", _ => small, memoryConsumer: consumer));
                    Check(retry.Wait(5000) && ReferenceEquals(retry.Result, small), "UC-T02-06 a smaller drainage solve can retry the failed key.");
                    cache.Dispose();
                    Check(drainageMemory.PinnedBytes > 0, "UC-T02-06 retiring a shared drainage cache preserves an active tile's pin.");
                }
                finally { consumer.ReleaseProductMemory(); }
                Check(drainageMemory.ManagedBytes == 0, "UC-T02-06 final drainage consumer release removes shared payload residency.");
            }

            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new MemoryCountingNode { id = "memory-source", contentHash = 17 };
            graph.nodes.Add(source);
            var memory = new ProductMemoryBudget(4096);
            TileData firstTile = null, warmTile = null, coldTile = null;
            try
            {
                var evaluator = new GraphEvaluator(graph);
                firstTile = MemoryTestTile(memory);
                Matrix initial = (Matrix)evaluator.PullNode(source, 0, firstTile, new StopToken());
                var retained = new Dictionary<string, RetainedProduct>(firstTile.retained);
                firstTile.AcceptRetainedProducts(); firstTile.ReleaseProductMemory();
                warmTile = MemoryTestTile(memory); warmTile.retained = retained;
                Matrix warm = (Matrix)evaluator.PullNode(source, 0, warmTile, new StopToken());
                Check(ReferenceEquals(initial, warm) && source.computes == 1,
                    "UC-T02-06 warm retained evaluation reuses the exact field with an active pin.");
                warmTile.ReleaseProductMemory();
                using (memory.Reserve(4096)) Check(retained[source.id + ":0"].product == null,
                    "UC-T02-06 byte eviction clears payloads held through copied retained dictionaries.");
                coldTile = MemoryTestTile(memory); coldTile.retained = retained;
                Matrix cold = (Matrix)evaluator.PullNode(source, 0, coldTile, new StopToken());
                Check(source.computes == 2 && !ReferenceEquals(initial, cold) && cold.arr[0] == initial.arr[0] && cold.arr[cold.arr.Length - 1] == initial.arr[initial.arr.Length - 1],
                    "UC-T02-06 evicted fields recompute deterministically through the production evaluator.");
                coldTile.ReleaseProductMemory(); memory.ClearRetained();
                Check(memory.ManagedBytes == 0, "UC-T02-06 an unpublished tile releases its transient cache products.");

                var largeCapacity = new List<ScatterInstance>(1000);
                Check(ProductMemoryFootprint.Estimate(largeCapacity) >= 1000L * 64,
                    "UC-T02-06 scatter accounting includes allocated capacity even before elements are filled.");
                var alias = new float[1024];
                long single = ProductMemoryFootprint.Estimate(new object[] { alias });
                long duplicate = ProductMemoryFootprint.Estimate(new object[] { alias, alias });
                Check(duplicate - single == IntPtr.Size,
                    "UC-T02-06 a single payload counts shared array aliases once.");
            }
            finally
            {
                firstTile?.ReleaseProductMemory(); warmTile?.ReleaseProductMemory(); coldTile?.ReleaseProductMemory();
                memory.ClearRetained(); UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static TileData MemoryTestTile(ProductMemoryBudget memory)
        {
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 17, 17), Vector3.zero, new Vector3(16, 100, 16), 100);
            tile.AttachMemory(memory);
            tile.retained = new Dictionary<string, RetainedProduct>();
            return tile;
        }
    }
}
