using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T02-01: real worker barrier deliberately finishes old computation after its replacement has committed.
        [Serializable]
        private sealed class RevisionBarrierNode : CreatorNode
        {
            internal static ManualResetEventSlim entered, release;
            public override string NodeName => "Revision barrier";
            // UC-T02-01: the captured fixture must implement every required generator property.
            public override NodeCategory Category => NodeCategory.Creator;
            /// <inheritdoc />
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("UC-T02-01 revision barrier timed out.");
                matrix.Fill(0.9f);
            }
        }

        private static void TestGenerationScheduling()
        {
            TestGenerationRevisionAuthority();
            TestGenerationRevisionRestart();
            TestGenerationRevisionConsumers();
            TestGenerationWorkerAdmission(); // UC-T02-02: obsolete bodies retain real global/per-world admissions until exit.
            TestDrainageScheduling(); // UC-T02-03: actual shared solves, caller cancellation, main-thread async admission and retry.
            TestStagedChunkCommits(); // UC-T02-04: native upload cancellation, collider readiness and measured soft-budget yielding.
            TestChunkRestore(); // UC-T08-15: partial saved grids, repeat layouts, duplicates and shrink/grow retain native identity.
            TestChunkReadiness(); // UC-T09-03: native initial/flat readiness and bounded host landing/cancel/restart contracts.
            TestChunkRegionDemand(); // UC-T09-04: bounded overlapping demand, accepted readiness, expiry and world isolation.
            TestTerrainResidency(); // UC-T09-04: actual sculpt/paint/hole/tree/detail recovery and cancellation ownership.
            TestFarTerrain(); // UC-T09-04: distant native geometry, filtered paint, cancellation and neighbour retirement.
            TestExecutionDomains(); // UC-T02-05: actual-thread guards, headless CPU masks, repaint rejection and cancellation.
            TestProductMemoryBudget(); // UC-T02-06: byte pressure, live pins, native reservations and deterministic retained recomputation.
            TestGraphPublications(); // UC-T02-07: retained ancestors, once-only ecology replay, world isolation and cancelled/empty commits.
            TestGenerationDemand(); // UC-T02-08: actual admission order, camera reversal, aging and halo contracts.
            TestGenerationMetrics(); // UC-T02-09: cold/warm/edit measurements, bounded series and opt-in overhead.
        }

        private static void TestGenerationRevisionAuthority()
        {
            var first = new WorldGenerationScheduler();
            var second = new WorldGenerationScheduler();
            GenerationRunId old = first.CurrentRun;
            StopToken local = first.CreateStopToken(old);
            StopToken sibling = first.CreateStopToken(old);
            local.RequestStop();
            Check(!sibling.StopRequested && first.IsCurrent(old), "UC-T02-01 local cancellation leaves sibling work admitted.");
            GenerationRunId current = first.BeginRun();
            int publications = 0;
            Check(current.WorldId == old.WorldId && current.Revision > old.Revision && sibling.StopRequested,
                "UC-T02-01 world restart advances revision and cancels every old-domain token.");
            sibling.Reset();
            Check(sibling.StopRequested, "UC-T02-01 resetting local state cannot revive a cancelled parent revision.");
            Check(!first.TryPublish(old, () => publications++) && !first.TryPublish(second.CurrentRun, () => publications++) &&
                  !first.TryPublish(default, () => publications++) && publications == 0,
                "UC-T02-01 old, foreign and unowned results cannot publish.");
            first.Cancel(old);
            Check(first.TryPublish(current, () => publications++) && publications == 1 && second.IsCurrent(second.CurrentRun),
                "UC-T02-01 retiring an old run does not cancel its replacement or another world.");
            first.Cancel(current);
            Check(!first.TryPublish(current, () => publications++) && first.CaptureRun().Revision > current.Revision,
                "UC-T02-01 explicit new work after cancellation receives a fresh identity.");
        }

        private static void TestGenerationRevisionRestart()
        {
            var root = new GameObject("UC revision restart"); root.SetActive(false);
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            UniverseCreatorObject world = root.AddComponent<UniverseCreatorObject>();
            world.graph = graph; world.resolution = 33;
            Task obsolete = null;
            int originalLimit = WorldGenerationScheduler.GlobalWorkerLimit;
            if (originalLimit < 2) WorldGenerationScheduler.ConfigureGlobalWorkerLimit(2); // The barrier fixture requires two admitted bodies.
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            try
            {
                RevisionBarrierNode.entered = entered; RevisionBarrierNode.release = release;
                var source = new ConstantNode { id = "source", value = 0.2f };
                var output = new HeightOutputNode { id = "height", enforceSeaLevelFloor = false,
                    enforceContinentalRise = false, enforceCoastalShelf = false };
                graph.nodes.Add(source); graph.nodes.Add(output);
                graph.connections.Add(new Connection(source.id, 0, output.id, 0));
                if (!world.RegenerateAsync()) { Check(false, "UC-T02-01 initial native worker starts."); return; }
                Check(world.CurrentTask.Wait(5000) && world.LastTile != null, "UC-T02-01 initial world publication completes.");
                TileData accepted = world.LastTile;
                graph.nodes[0] = new RevisionBarrierNode { id = "source" };
                if (!world.RegenerateAsync()) { Check(false, "UC-T02-01 obsolete native worker starts."); return; }
                obsolete = world.CurrentTask;
                Check(entered.Wait(5000) && ReferenceEquals(world.LastTile, accepted),
                    "UC-T02-01 the last accepted world remains available while new work waits.");
                graph.nodes[0] = new ConstantNode { id = "source", value = 0.7f };
                if (!world.RegenerateAsync()) { Check(false, "UC-T02-01 replacement native worker starts."); return; }
                Check(world.CurrentTask.Wait(5000), "UC-T02-01 replacement completes before obsolete work is released.");
                TileData replacement = world.LastTile;
                Check(replacement != null && !ReferenceEquals(replacement, accepted) &&
                      world.GenerationScheduler.IsCurrent(replacement.RunId) && replacement.outputs.RunId.Equals(replacement.RunId) &&
                      Mathf.Abs(replacement.outputs.heightmap[0, 0] - 0.7f) < 1e-6f,
                    "UC-T02-01 replacement publishes matching field/output identity and current graph values.");
                release.Set();
                Check(obsolete.Wait(5000) && ReferenceEquals(replacement, world.LastTile),
                    "UC-T02-01 late obsolete completion cannot replace the newer world.");
                world.CancelGeneration();
                Check(ReferenceEquals(replacement, world.LastTile), "UC-T02-01 cancellation preserves the last accepted publication.");
            }
            finally
            {
                release.Set(); world.CancelGeneration();
                if (obsolete != null) obsolete.Wait(5000);
                if (world.CurrentTask != null) world.CurrentTask.Wait(5000);
                if (world.LastTile != null) world.LastTile.ReleaseGenerationInputs();
                RevisionBarrierNode.entered = RevisionBarrierNode.release = null;
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph);
                if (originalLimit < 2) WorldGenerationScheduler.ConfigureGlobalWorkerLimit(originalLimit);
            }
        }

        private static void TestGenerationRevisionConsumers()
        {
            var root = new GameObject("UC revision consumers"); root.SetActive(false);
            var foreignRoot = new GameObject("UC foreign revision"); foreignRoot.SetActive(false);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
            try
            {
                UniverseCreatorObject world = root.AddComponent<UniverseCreatorObject>();
                UniverseCreatorObject foreign = foreignRoot.AddComponent<UniverseCreatorObject>();
                var child = new GameObject("ChunkManager"); child.transform.SetParent(root.transform, false);
                ChunkManager manager = child.AddComponent<ChunkManager>(); manager.Initialize(world);
                var ground = new GameObject("Terrain"); ground.transform.SetParent(child.transform, false);
                var terrain = ground.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = data;
                var heights = new float[33, 33]; heights[0, 0] = 0.25f; data.SetHeights(0, 0, heights);
                var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, data.size, 100f);
                tile.AssignRun(world.GenerationScheduler.CurrentRun);
                tile.outputs.heightmap = new float[33, 33]; tile.outputs.heightmap[0, 0] = 0.8f;
                tile.outputs.surface = new SurfaceProduct();
                int environmentCalls = 0;
                manager.RunAfterSettle(() => environmentCalls++);
                world.GenerationScheduler.BeginRun();
                Check(!TerrainBuilder.ApplyOutputs(data, tile.outputs, world) && !UCTextureGraphApplier.Apply(tile.outputs, terrain),
                    "UC-T02-01 direct height and material consumers reject old revisions before native writes.");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var pending = (Queue<Action>)typeof(ChunkManager).GetField("pendingAfterSettle", flags).GetValue(manager);
                pending.Dequeue()();
                Check(environmentCalls == 0, "UC-T02-01 deferred environment work rejects its obsolete revision.");
                Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                object chunk = Activator.CreateInstance(chunkType, true);
                chunkType.GetField("go").SetValue(chunk, ground); chunkType.GetField("terrain").SetValue(chunk, terrain);
                chunkType.GetField("data").SetValue(chunk, data); chunkType.GetField("tile").SetValue(chunk, tile);
                chunkType.GetField("workerDone").SetValue(chunk, true); chunkType.GetField("inFlight").SetValue(chunk, true);
                ((IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager)).Add(chunk);
                typeof(ChunkManager).GetField("activeWorkers", flags).SetValue(manager, 1);
                manager.PumpCompleted();
                Check(chunkType.GetField("tile").GetValue(chunk) == null && Mathf.Abs(data.GetHeights(0, 0, 1, 1)[0, 0] - 0.25f) < 1e-4f,
                    "UC-T02-01 native chunk pump discards stale output and preserves the accepted terrain.");
                tile.AssignRun(foreign.GenerationScheduler.CurrentRun);
                Check(!TerrainBuilder.ApplyOutputs(data, tile.outputs, world) && !UCTextureGraphApplier.Apply(tile.outputs, terrain),
                    "UC-T02-01 foreign-world fields and materials cannot target this terrain.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(foreignRoot);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
            }
        }

        private static void TestGenerationWorkerAdmission()
        {
            int originalLimit = WorldGenerationScheduler.GlobalWorkerLimit;
            WorldGenerationScheduler.ConfigureGlobalWorkerLimit(2);
            var first = new WorldGenerationScheduler(); var second = new WorldGenerationScheduler();
            var tasks = new List<Task>();
            int bodies = 0, retired = 0, peak = 0, active = 0;
            using (var firstGate = new ManualResetEventSlim(false))
            using (var secondGate = new ManualResetEventSlim(false))
            using (var entered = new CountdownEvent(2))
            try
            {
                Action<ManualResetEventSlim> body = gate =>
                {
                    int count = Interlocked.Increment(ref active);
                    int observed;
                    do { observed = Volatile.Read(ref peak); if (observed >= count) break; }
                    while (Interlocked.CompareExchange(ref peak, count, observed) != observed);
                    try
                    {
                        if (!WorldGenerationScheduler.IsWorkerThread) throw new InvalidOperationException("Admitted body lost its nested-parallelism guard.");
                        entered.Signal();
                        if (!gate.Wait(5000)) throw new TimeoutException("UC-T02-02 worker admission barrier timed out.");
                    }
                    finally { Interlocked.Decrement(ref active); }
                };
                GenerationRunId firstRun = first.CurrentRun, secondRun = second.CurrentRun;
                Task firstTask = first.RunWorkerAsync(firstRun, 1, first.CreateStopToken(firstRun), () => body(firstGate), () => Interlocked.Increment(ref retired));
                Task secondTask = second.RunWorkerAsync(secondRun, 1, second.CreateStopToken(secondRun), () => body(secondGate), () => Interlocked.Increment(ref retired));
                tasks.Add(firstTask); tasks.Add(secondTask);
                Check(entered.Wait(5000), "UC-T02-02 two worlds occupy exactly two controlled admissions.");
                for (int i = 0; i < 20; i++)
                {
                    firstRun = first.BeginRun();
                    tasks.Add(first.RunWorkerAsync(firstRun, 1, first.CreateStopToken(firstRun),
                        () => Interlocked.Increment(ref bodies), () => Interlocked.Increment(ref retired)));
                }
                Check(WorldGenerationScheduler.GlobalRunningWorkers == 2 && first.RunningWorkers == 1 &&
                      first.CancelledRunningWorkers == 1 && first.QueuedWorkers == 1 && bodies == 0 && retired == 19,
                    "UC-T02-02 twenty restarts retire queued predecessors once while the cancelled running body keeps its slot.");
                secondGate.Set();
                Check(secondTask.Wait(5000) && WorldGenerationScheduler.GlobalRunningWorkers == 1 && first.QueuedWorkers == 1 && bodies == 0,
                    "UC-T02-02 free global capacity cannot bypass a world's still-running cancelled predecessor.");
                firstGate.Set();
                Check(Task.WaitAll(tasks.ToArray(), 5000) && bodies == 1 && retired == 22 && peak == 2 &&
                      WorldGenerationScheduler.GlobalRunningWorkers == 0 && first.QueuedWorkers == 0,
                    "UC-T02-02 only the latest replacement runs; every queued/running resource retires exactly once without exceeding the cap.");
                GenerationRunId faultRun = second.BeginRun();
                Task fault = second.RunWorkerAsync(faultRun, 1, second.CreateStopToken(faultRun),
                    () => throw new InvalidOperationException("UC-T02-02 deliberate worker failure"), () => Interlocked.Increment(ref retired));
                bool faulted = false;
                try { fault.Wait(5000); }
                catch (AggregateException error) { faulted = error.InnerException is InvalidOperationException; }
                Check(faulted && WorldGenerationScheduler.GlobalRunningWorkers == 0 && retired == 23,
                    "UC-T02-02 exceptions propagate and release their admission and resource owner.");
                Task retry = second.RunWorkerAsync(faultRun, 1, second.CreateStopToken(faultRun), () => Interlocked.Increment(ref bodies));
                Check(retry.Wait(5000) && bodies == 2 && WorldGenerationScheduler.GlobalRunningWorkers == 0,
                    "UC-T02-02 a worker failure does not poison the next request.");
            }
            finally
            {
                firstGate.Set(); secondGate.Set(); first.Cancel(first.CurrentRun); second.Cancel(second.CurrentRun);
                Task.WaitAll(tasks.ToArray(), 5000);
                WorldGenerationScheduler.ConfigureGlobalWorkerLimit(originalLimit);
            }
        }
    }
}
