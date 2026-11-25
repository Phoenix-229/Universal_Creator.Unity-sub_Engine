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
            TestCompletedDrainageViewMemory(); // UC repair 29: result/publication handoff and failed-view retirement.
            TestCompletedHaloViewMemory(); // UC repair 29: real modifier crops retire independent upstream evaluators.
            TestSharedRiverWorldMemory(); // UC repair 30: real cold/warm carving, publications, invalidation and final consumer lifetime.
            TestSharedWorldProductCancellation(); // UC repair 30: followers, failed admission and revision cancellation must retire.
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
                // UC climate/memory repair: real river raster dimensions, shared IDs and retained safety bounds.
                var ids = new string[512 * 512];
                long emptyIds = ProductMemoryFootprint.Estimate(ids);
                string reach = new string('r', 64);
                for (int i = 0; i < ids.Length; i++) ids[i] = reach;
                Check(ProductMemoryFootprint.Estimate(ids) - emptyIds == 24 + reach.Length * 2L,
                    "UC repair: a 512-square river ID grid charges all slots and one shared identity.");
                bool overSlots = false, overObjects = false, genericBound = false;
                int maximumSlots = TileWindows.MaximumDrainageResolution * TileWindows.MaximumDrainageResolution;
                Check(ProductMemoryFootprint.Estimate(new string[maximumSlots]) >= (long)maximumSlots * IntPtr.Size,
                    "UC repair 30: supported 2048-square river identity grids charge every reference slot.");
                try { ProductMemoryFootprint.Estimate(new string[maximumSlots + 1]); } catch (InvalidOperationException) { overSlots = true; }
                try { ProductMemoryFootprint.Estimate(new object[100001]); } catch (InvalidOperationException) { genericBound = true; }
                for (int i = 0; i < 100001; i++) ids[i] = new string('r', 64);
                try { ProductMemoryFootprint.Estimate(ids); } catch (InvalidOperationException) { overObjects = true; }
                Check(overSlots && overObjects && genericBound,
                    "UC repair: string-slot, unique-object and recursive-array limits remain enforced.");
            }
            finally
            {
                firstTile?.ReleaseProductMemory(); warmTile?.ReleaseProductMemory(); coldTile?.ReleaseProductMemory();
                memory.ClearRetained(); UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        /// <summary>Runs real 1024/2048 drainage and river publication with explicit fixture budgets on background workers.</summary>
        /// <remarks>Isolated batch entry; keeps Unity responsive and awaits worker retirement before releasing inputs.
        /// Checks solver grids, detailed output, admission refusal, cancellation and final ownership, not a full native world at these resolutions.
        /// Each admitted solve has a five-minute diagnostic deadline; reports failures and exits nonzero.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static async void RunLargeRiverMemoryBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Large river acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            try
            {
                foreach (int resolution in new[] { 1024, 2048 })
                {
                    Graph graph = ScriptableObject.CreateInstance<Graph>();
                    var memory = new ProductMemoryBudget(3072L * 1024 * 1024);
                    var cache = new WorldDrainageCache(memory: memory);
                    var stop = new StopToken();
                    TileData tile = null;
                    Task worker = null;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var ramp = new RiverTestRampNode();
                        // UC repair 30: this parallel-slope fixture has narrow catchments; use the supported
                        // minimum area fraction so high-resolution columns can actually initiate channels.
                        var river = new UniverseCreator.Nodes.Selectors.RiverNetworkNode
                            { seaLevel = 0.605f, solveResolution = resolution, channelInitiationFraction = 0.0002f, trunkChannelWidthMetres = 18f };
                        graph.nodes.AddRange(new Generator[] { ramp, river });
                        graph.AddConnection(new Connection(ramp.id, 0, river.id, 0));
                        GraphContentHasher.ComputeHashes(graph);
                        var evaluator = new GraphEvaluator(graph);
                        tile = new TileData(new Coord(0, 0), new CoordRect(0, 0, 65, 65), Vector3.zero,
                            new Vector3(4096, 600, 4096), 600)
                            { fullWorldPos = Vector3.zero, fullWorldSize = new Vector3(4096, 600, 4096), drainageCache = cache };
                        tile.AttachMemory(memory); tile.Bind(evaluator);
                        tile.AssignRun(new UniverseCreator.TerrainSystem.WorldGenerationScheduler().CurrentRun);
                        WorldDrainage drainage = null;
                        Matrix height = null, mask = null;
                        worker = Task.Run(() =>
                        {
                            drainage = WorldDrainage.Solve(river, 0, tile, resolution, Mathf.Max(0, river.flatRoutingEpsilon), true, stop);
                            height = (Matrix)evaluator.PullNode(river, 0, tile, stop);
                            mask = (Matrix)evaluator.PullNode(river, 1, tile, stop);
                        });
                        if (await Task.WhenAny(worker, Task.Delay(300000)) != worker)
                        { stop.RequestStop(); throw new TimeoutException("Large river solve exceeded five minutes at " + resolution); }
                        await worker;
                        int cells = resolution * resolution;
                        Check(drainage != null && drainage.resX == resolution && drainage.resZ == resolution &&
                            drainage.height.arr.Length == cells && drainage.filled.Length == cells &&
                            drainage.dir.Length == cells && drainage.accum.Length == cells,
                            "UC repair 30: actual drainage preserves requested grid " + resolution);
                        var channels = tile.ChannelPublications();
                        Check(channels.Length == 1 && channels[0].ResolutionX == resolution && channels[0].ResolutionZ == resolution &&
                            channels[0].HalfWidth.Length == cells && channels[0].Depth.Length == cells,
                            "UC repair 30: river publishes full requested channel grid " + resolution);
                        bool valid = height != null && mask != null && height.arr.Length == 65 * 65 && mask.arr.Length == height.arr.Length;
                        bool carved = false;
                        if (valid) for (int i = 0; i < height.arr.Length; i++)
                        {
                            valid &= !float.IsNaN(height.arr[i]) && !float.IsInfinity(height.arr[i]) &&
                                !float.IsNaN(mask.arr[i]) && !float.IsInfinity(mask.arr[i]) && mask.arr[i] >= 0;
                            carved |= mask.arr[i] > 0 && height.arr[i] < rampValue(i);
                        }
                        Check(valid && carved, "UC repair 30: finite detailed terrain and nonempty trenches at river grid " + resolution);
                        stop.RequestStop();
                        Check(WorldDrainage.Solve(river, 0, tile, resolution, 0, true, stop) == null,
                            "UC repair 30: cancelled large-grid request returns without another allocation " + resolution);
                        Console.WriteLine("UC large river: resolution=" + resolution + " elapsedMs=" + timer.ElapsedMilliseconds +
                            " managed=" + memory.ManagedBytes + " peak=" + memory.PeakBytes + " limit=" + memory.LimitBytes);
                    }
                    finally
                    {
                        stop.RequestStop(); cache.Dispose();
                        // UC repair 30: a deadline never authorizes releasing inputs still used by a worker.
                        if (worker != null && !worker.IsCompleted)
                            try { await worker; } catch (OperationCanceledException) { }
                        tile?.ReleaseProductMemory(); memory.ClearRetained();
                        UnityEngine.Object.DestroyImmediate(graph);
                    }
                    Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0 && cache.InFlightCount == 0,
                        "UC repair 30: large-grid workers and shared products retire completely " + resolution);
                }
                bool unsupported = false;
                try { TileWindows.DrainageResolution(2049); } catch (ArgumentOutOfRangeException) { unsupported = true; }
                Check(unsupported, "UC repair 30: unsupported drainage resolution is rejected explicitly.");
                var small = new ProductMemoryBudget(1024 * 1024);
                bool refused = false;
                Graph refusedGraph = ScriptableObject.CreateInstance<Graph>();
                TileData refusedTile = null;
                try
                {
                    var ramp = new RiverTestRampNode();
                    var river = new UniverseCreator.Nodes.Selectors.RiverNetworkNode { solveResolution = 1024 };
                    refusedGraph.nodes.AddRange(new Generator[] { ramp, river });
                    refusedGraph.AddConnection(new Connection(ramp.id, 0, river.id, 0));
                    refusedTile = new TileData(new Coord(0, 0), new CoordRect(0, 0, 65, 65), Vector3.zero,
                        new Vector3(4096, 600, 4096), 600) { fullWorldSize = new Vector3(4096, 600, 4096) };
                    refusedTile.AttachMemory(small); refusedTile.Bind(new GraphEvaluator(refusedGraph));
                    try { await Task.Run(() => WorldDrainage.Solve(river, 0, refusedTile, 1024, 0, true, new StopToken())); }
                    catch (InvalidOperationException error) when (error.Message.StartsWith("UC-T02-06:", StringComparison.Ordinal)) { refused = true; }
                }
                finally
                {
                    refusedTile?.ReleaseProductMemory(); small.ClearRetained();
                    UnityEngine.Object.DestroyImmediate(refusedGraph);
                }
                Check(refused && small.ManagedBytes == 0 && small.NativeBytes == 0,
                    "UC repair 30: actual large-grid drainage refuses an impossible budget without retained ownership.");
            }
            catch (Exception error) { Check(false, "UC large river exception: " + error); }
            Console.WriteLine(Report(failures.Count == 0)); UnityEditor.EditorApplication.Exit(failures.Count == 0 ? 0 : 1);

            float rampValue(int index) => 0.95f - 0.35f * (index / 65) / 64f;
        }

        private static void TestSharedRiverWorldMemory()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var memory = new ProductMemoryBudget(32L * 1024 * 1024);
            var cache = new WorldDrainageCache(memory: memory);
            var tiles = new List<TileData>();
            var stop = new StopToken();
            Task worker = null;
            try
            {
                var ramp = new RiverTestRampNode();
                var river = new UniverseCreator.Nodes.Selectors.RiverNetworkNode
                    { seaLevel = 0.605f, solveResolution = 64, channelInitiationFraction = 0.001f, trunkChannelWidthMetres = 18f };
                graph.nodes.AddRange(new Generator[] { ramp, river });
                graph.AddConnection(new Connection(ramp.id, 0, river.id, 0));
                GraphContentHasher.ComputeHashes(graph);
                var evaluator = new GraphEvaluator(graph);
                var run = new UniverseCreator.TerrainSystem.WorldGenerationScheduler().CurrentRun;
                TileData MakeTile(int x, bool shared)
                {
                    var tile = new TileData(new Coord(x, 0), new CoordRect(0, 0, 65, 65),
                        new Vector3(x * 2048, 0, 0), new Vector3(2048, 600, 4096), 600)
                        { fullWorldPos = Vector3.zero, fullWorldSize = new Vector3(4096, 600, 4096), drainageCache = shared ? cache : null };
                    tile.AttachMemory(memory); tile.AssignRun(run); tile.Bind(evaluator); tiles.Add(tile); return tile;
                }
                TileData first = MakeTile(0, true), warm = MakeTile(1, true), cold = MakeTile(1, false);
                Matrix firstHeight = null, warmHeight = null, coldHeight = null, warmMask = null, coldMask = null;
                worker = Task.Run(() =>
                {
                    firstHeight = (Matrix)evaluator.PullNode(river, 0, first, stop);
                    warmMask = (Matrix)evaluator.PullNode(river, 1, warm, stop);
                    warmHeight = (Matrix)evaluator.PullNode(river, 0, warm, stop);
                    coldMask = (Matrix)evaluator.PullNode(river, 1, cold, stop);
                    coldHeight = (Matrix)evaluator.PullNode(river, 0, cold, stop);
                });
                if (!worker.Wait(20000)) throw new TimeoutException("Shared river regression exceeded 20 seconds.");
                bool equal = warmHeight != null && coldHeight != null && warmMask != null && coldMask != null;
                bool carved = false;
                if (equal) for (int i = 0; i < warmHeight.arr.Length; i++)
                {
                    equal &= warmHeight.arr[i] == coldHeight.arr[i] && warmMask.arr[i] == coldMask.arr[i];
                    carved |= warmMask.arr[i] > 0 && warmHeight.arr[i] < 0.95f - 0.35f * (i / 65) / 64f;
                }
                Check(equal && carved, "UC repair 30: shared/cold whole-world fields produce identical detailed heights and nonempty river masks.");
                var firstChannels = first.ChannelPublications();
                var warmChannels = warm.ChannelPublications();
                Check(firstChannels.Length == 1 && warmChannels.Length == 1 && ReferenceEquals(firstChannels[0], warmChannels[0]),
                    "UC repair 30: different chunks and output ports replay the exact shared channel publication.");
                cold.ReleaseProductMemory();
                river.depthScale *= 2;
                GraphContentHasher.ComputeHashes(graph);
                TileData changed = MakeTile(1, true);
                worker = Task.Run(() => evaluator.PullNode(river, 0, changed, stop));
                if (!worker.Wait(20000)) throw new TimeoutException("Changed river regression exceeded 20 seconds.");
                var changedChannels = changed.ChannelPublications();
                Check(changedChannels.Length == 1 && !ReferenceEquals(changedChannels[0], warmChannels[0]),
                    "UC repair 30: captured river options invalidate coarse fields within the existing world cache.");
                river.SetLakeFootprints(new List<UniverseCreator.Nodes.Outputs.HeightOutputNode.LakeFootprint>
                {
                    new UniverseCreator.Nodes.Outputs.HeightOutputNode.LakeFootprint { bodyId = "shared-field-lake",
                        ringXZ = new[] { new Vector2(1000, 1000), new Vector2(1100, 1000), new Vector2(1100, 1100) },
                        planBounds = new Bounds(new Vector3(1050, 0, 1050), new Vector3(100, 1, 100)) }
                });
                GraphContentHasher.ComputeHashes(graph);
                TileData lakeChanged = MakeTile(1, true);
                worker = Task.Run(() => evaluator.PullNode(river, 1, lakeChanged, stop));
                if (!worker.Wait(20000)) throw new TimeoutException("Captured lake regression exceeded 20 seconds.");
                var lakeChannels = lakeChanged.ChannelPublications();
                Check(lakeChannels.Length == 1 && !ReferenceEquals(lakeChannels[0], changedChannels[0]),
                    "UC repair 30: captured lake geometry invalidates shared river exclusions and channel records.");
                cache.Dispose();
                Check(memory.PinnedBytes > 0 && firstHeight.arr.Length == 65 * 65 && warmChannels[0].HalfWidth.Length == 64 * 64,
                    "UC repair 30: cache teardown preserves independent active chunk and channel consumers.");
            }
            finally
            {
                stop.RequestStop(); cache.Dispose();
                // UC repair 30: fixture teardown must not release a tile still owned by its worker.
                if (worker != null && !worker.IsCompleted && !worker.Wait(5000))
                    throw new TimeoutException("Cancelled shared river worker did not retire; its inputs remain protected.");
                foreach (TileData tile in tiles) tile.ReleaseProductMemory(); memory.ClearRetained();
                UnityEngine.Object.DestroyImmediate(graph);
            }
            Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0 && cache.InFlightCount == 0,
                "UC repair 30: all derived world fields and chunk consumers retire to zero.");
        }

        private static void TestSharedWorldProductCancellation()
        {
            var memory = new ProductMemoryBudget(4096);
            var cache = new WorldDrainageCache(memory: memory);
            using (var entered = new System.Threading.ManualResetEventSlim())
            using (var release = new System.Threading.ManualResetEventSlim())
            {
                var cancelled = new StopToken();
                int computes = 0;
                Task<float[]> owner = Task.Run(() => cache.GetOrComputeProduct("world-field", stop =>
                {
                    System.Threading.Interlocked.Increment(ref computes); entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("Shared field owner barrier timed out.");
                    return new float[16];
                }));
                Task<float[]> joined = null;
                try
                {
                    if (!entered.Wait(5000)) throw new TimeoutException("Shared field owner did not start.");
                    joined = Task.Run(() => cache.GetOrComputeProduct("world-field", _ => new float[16]));
                    if (!System.Threading.SpinWait.SpinUntil(() => cache.WaitingRequesters == 1, 5000))
                        throw new TimeoutException("Successful shared field follower did not join.");
                    Task<float[]> follower = Task.Run(() => cache.GetOrComputeProduct("world-field", _ => new float[16], cancelled));
                    cancelled.RequestStop();
                    Check(follower.Wait(5000) && follower.Result == null && !owner.IsCompleted,
                        "UC repair 30: a cancelled world-field follower leaves its computing owner alive.");
                }
                finally
                {
                    release.Set();
                    if (!owner.Wait(5000) || joined != null && !joined.Wait(5000))
                        throw new TimeoutException("Shared field workers did not retire before barrier disposal.");
                }
                if (!owner.Wait(5000)) throw new TimeoutException("Shared field owner did not retire.");
                Check(ReferenceEquals(owner.Result, joined.Result) && computes == 1,
                    "UC repair 30: simultaneous successful requesters execute one factory and share its immutable result.");
                var consumer = MemoryTestTile(memory);
                try
                {
                    Task<float[]> warm = Task.Run(() => cache.GetOrComputeProduct("world-field", _ => new float[16], consumer: consumer));
                    Check(warm.Wait(5000) && ReferenceEquals(owner.Result, warm.Result) && computes == 1,
                        "UC repair 30: cancelled followers do not discard a completed shared field.");
                    cache.Dispose();
                    Check(memory.PinnedBytes > 0, "UC repair 30: derived-field cache retirement keeps an active consumer pinned.");
                }
                finally { consumer.ReleaseProductMemory(); cache.Dispose(); }
            }
            Check(memory.ManagedBytes == 0 && cache.InFlightCount == 0, "UC repair 30: cancelled follower and final consumer release leave no world-field residency.");
            using (var failed = new WorldDrainageCache(memory: memory))
            {
                Task<bool> refused = Task.Run(() =>
                {
                    try { failed.GetOrComputeProduct("retry", _ => new float[4096]); return false; }
                    catch (InvalidOperationException) { return true; }
                });
                Check(refused.Wait(5000) && refused.Result && failed.InFlightCount == 0 && memory.ManagedBytes == 0,
                    "UC repair 30: derived-field admission failure completes and removes its flight.");
                Task<float[]> retry = Task.Run(() => failed.GetOrComputeProduct("retry", _ => new float[16]));
                Check(retry.Wait(5000) && retry.Result.Length == 16,
                    "UC repair 30: a failed derived-field key can retry with an admissible product.");
            }
            using (var revision = new WorldDrainageCache(memory: memory))
            using (var entered = new System.Threading.ManualResetEventSlim())
            using (var release = new System.Threading.ManualResetEventSlim())
            {
                Task<float[]> owner = Task.Run(() => revision.GetOrComputeProduct("cancelled-world", token =>
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("Revision cancellation barrier timed out.");
                    return new float[16]; // A late completion must be discarded even if the factory ignores cancellation.
                }));
                try
                {
                    if (!entered.Wait(5000)) throw new TimeoutException("Revision field owner did not start.");
                    revision.Dispose();
                }
                finally
                {
                    release.Set();
                    if (!owner.Wait(5000)) throw new TimeoutException("Cancelled revision field owner did not retire.");
                }
                Check(owner.Result == null && revision.InFlightCount == 0 && memory.ManagedBytes == 0,
                    "UC repair 30: world retirement discards a late derived product without orphaned flights or pins.");
            }
        }

        [Serializable]
        private sealed class MemoryViewSourceNode : CreatorNode
        {
            internal int mode;
            internal bool referenceGround;
            public override string NodeName => "Completed drainage memory source";
            public override NodeCategory Category => NodeCategory.Creator;
            /// <inheritdoc />
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                tile.SetCached("temporary-upstream", 0, new float[8192]);
                if (mode == 1) { stop.RequestStop(); return; }
                if (mode == 2) throw new InvalidOperationException("deliberate upstream failure");
                matrix.Fill(0.375f);
                if (referenceGround) { matrix.trenchReference = matrix.Blank(); matrix.trenchReference.Fill(0.5f); }
                tile.AddPublication(new ChannelGraphPublication("completed-view-channel", new float[9], new float[9], 3, 3, 0, 0, 1, 1));
            }
        }

        private static void TestCompletedDrainageViewMemory()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new MemoryViewSourceNode { id = "completed-view-source" };
            var reader = new OwnershipReaderNode { id = "completed-view-reader" };
            graph.nodes.AddRange(new Generator[] { source, reader });
            graph.connections.Add(new Connection(source.id, 0, reader.id, 0));
            try
            {
                for (int mode = 0; mode < 3; mode++)
                {
                    source.mode = mode;
                    var memory = new ProductMemoryBudget(65536);
                    TileData tile = MemoryTestTile(memory); tile.Bind(new GraphEvaluator(graph));
                    try
                    {
                        var stop = new StopToken();
                        // This 16-square uncached fixture is bounded; it needs no waiting worker or UI-thread task join.
                        WorldDrainage result = null;
                        bool failed = false;
                        try { result = WorldDrainage.Solve(reader, 0, tile, 16, 0, true, stop); }
                        catch (InvalidOperationException error) when (mode == 2 && error.Message == "deliberate upstream failure") { failed = true; }
                        if (mode == 0)
                        {
                            ChannelGraphPublication[] publications = tile.ChannelPublications();
                            long expected = ProductMemoryFootprint.Estimate(result);
                            foreach (var publication in publications) expected += ProductMemoryFootprint.Estimate(publication);
                            Check(result.height.arr[0] == 0.375f && result.dir.Length == 256 && publications.Length == 1 &&
                                memory.ManagedBytes == expected && memory.ManagedBytes < memory.PeakBytes,
                                "UC repair 29: completed solve keeps exact source/flow and channel metadata while upstream scratch retires.");
                            using (memory.Reserve(40000)) Check(memory.PinnedBytes <= memory.LimitBytes,
                                "UC repair 29: a subsequent allocation reuses released evaluator capacity without evicting the result.");
                        }
                        else Check(memory.ManagedBytes == 0 && (mode == 1 ? result == null && stop.StopRequested : failed),
                            "UC repair 29: cancelled/failed upstream views retire promptly, mode " + mode);
                    }
                    finally { tile.ReleaseProductMemory(); }
                    Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0,
                        "UC repair 29: result and propagated channel pins retire with the final consumer, mode " + mode);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }

            foreach (int authored in new[] { 256, 512 })
            {
                var root = new GameObject("UC honest saved budget"); root.SetActive(false);
                try
                {
                    var world = root.AddComponent<TerrainSystem.UniverseCreatorObject>(); world.enabled = false;
                    var serialized = new UnityEditor.SerializedObject(world);
                    serialized.FindProperty("productMemoryBudgetMiB").intValue = authored;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    Check(world.ProductMemory.LimitBytes == authored * 1024L * 1024,
                        "UC repair 30: saved payload budget is honored exactly, MiB " + authored);
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
            }
        }

        [Serializable]
        private sealed class MemoryHaloModifierNode : ModifierNode
        {
            public override string NodeName => "Completed halo memory modifier";
            public override NodeCategory Category => NodeCategory.Modifier;
            /// <inheritdoc />
            protected override int RequiredMargin(TileData tile) => 2;
            /// <inheritdoc />
            protected override void Modify(Matrix matrix, TileData tile, StopToken stop)
            {
                for (int i = 0; i < matrix.arr.Length; i++) matrix.arr[i] += 0.125f;
            }
        }

        private static void TestCompletedHaloViewMemory()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new MemoryViewSourceNode { id = "halo-source", referenceGround = true };
            var modifier = new MemoryHaloModifierNode { id = "halo-modifier" };
            graph.nodes.AddRange(new Generator[] { source, modifier });
            graph.connections.Add(new Connection(source.id, 0, modifier.id, 0));
            try
            {
                for (int mode = 0; mode < 3; mode++)
                {
                    source.mode = mode;
                    var memory = new ProductMemoryBudget(65536);
                    var evaluator = new GraphEvaluator(graph);
                    TileData tile = MemoryTestTile(memory); tile.Bind(evaluator);
                    try
                    {
                        var stop = new StopToken();
                        Matrix result = null;
                        bool failed = false;
                        try { result = evaluator.PullNode(modifier, 0, tile, stop) as Matrix; }
                        catch (InvalidOperationException error) when (mode == 2 && error.Message == "deliberate upstream failure") { failed = true; }
                        if (mode == 0)
                        {
                            long expected = ProductMemoryFootprint.Estimate(result);
                            ChannelGraphPublication[] publications = tile.ChannelPublications();
                            foreach (var publication in publications) expected += ProductMemoryFootprint.Estimate(publication);
                            Check(result.rect.size.x == 17 && result.arr[0] == 0.5f && result.trenchReference.arr[0] == 0.625f &&
                                publications.Length == 1 && memory.ManagedBytes == expected && memory.ManagedBytes < memory.PeakBytes,
                                "UC repair 29: completed halo preserves exact cropped terrain/reference and metadata without upstream scratch retention.");
                            using (memory.Reserve(40000)) Check(memory.PinnedBytes <= memory.LimitBytes,
                                "UC repair 29: subsequent work reuses completed halo capacity.");
                        }
                        else Check(memory.ManagedBytes == 0 && (mode == 1 ? result == null && stop.StopRequested : failed),
                            "UC repair 29: cancelled/failed modifier halos retire promptly, mode " + mode);
                    }
                    finally { tile.ReleaseProductMemory(); }
                    Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0,
                        "UC repair 29: final halo consumer releases all payload pins, mode " + mode);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
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
