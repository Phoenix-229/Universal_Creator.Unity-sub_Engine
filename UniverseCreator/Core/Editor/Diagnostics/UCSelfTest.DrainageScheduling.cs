using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T02-03: holds the actual upstream pull, so duplicate solves are visible before any hydrology result exists.
        [Serializable]
        private sealed class DrainageSourceNode : CreatorNode
        {
            internal static ManualResetEventSlim entered, release;
            internal static int pulls;
            public float value = 0.2f;
            public override string NodeName => "Drainage source barrier";
            // UC-T02-03: fixtures satisfy the same required metadata contract as production nodes.
            public override NodeCategory Category => NodeCategory.Creator;
            /// <inheritdoc />
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                Interlocked.Increment(ref pulls); entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("UC-T02-03 upstream drainage barrier timed out.");
                stop?.ThrowIfStopRequested(); matrix.Fill(value);
            }
        }

        private static void TestDrainageScheduling()
        {
            TestSharedDrainageProduction();
            TestSharedDrainageAsyncAndRetry();
            TestQueuedDrainageWorkerJoin();
        }

        private static void TestSharedDrainageProduction()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var lifetime = new StopToken();
            var cache = new WorldDrainageCache(lifetime);
            Task<WorldDrainage> first = null, second = null;
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            try
            {
                DrainageSourceNode.entered = entered; DrainageSourceNode.release = release; DrainageSourceNode.pulls = 0;
                var source = new DrainageSourceNode { id = "drainage-source" };
                var left = new OwnershipReaderNode { id = "left" }; var right = new OwnershipReaderNode { id = "right" };
                graph.nodes.AddRange(new Generator[] { source, left, right });
                graph.connections.Add(new Connection(source.id, 0, left.id, 0));
                graph.connections.Add(new Connection(source.id, 0, right.id, 0));
                GraphContentHasher.ComputeHashes(graph);
                TileData leftTile = SnapshotTile(graph), rightTile = SnapshotTile(graph);
                leftTile.drainageCache = rightTile.drainageCache = cache;
                StopToken firstRequest = new StopToken();
                first = Task.Run(() => WorldDrainage.Solve(left, 0, leftTile, 16, 0f, false, firstRequest));
                Check(entered.Wait(5000), "UC-T02-03 actual upstream drainage pull reaches the sharing barrier.");
                second = Task.Run(() => WorldDrainage.Solve(right, 0, rightTile, 16, 0f, false, new StopToken()));
                Check(SpinWait.SpinUntil(() => cache.WaitingRequesters == 1, 2000) && cache.SolveCount == 1 && DrainageSourceNode.pulls == 1,
                    "UC-T02-03 equivalent inputs at different consumers join one in-flight solve before pulling upstream twice.");
                firstRequest.RequestStop(); release.Set();
                Check(Task.WaitAll(new Task[] { first, second }, 5000) && first.Result == null && second.Result != null && cache.InFlightCount == 0,
                    "UC-T02-03 cancelling the first requester leaves the shared world solve available to its sibling.");
                WorldDrainage shared = second.Result;
                Check(ReferenceEquals(shared, WorldDrainage.Solve(left, 0, leftTile, 16, 0f, false, null)) && cache.SolveCount == 1,
                    "UC-T02-03 completed results are reused without another solve, including a main-thread cache hit.");
                Task<WorldDrainage> flow = Task.Run(() => WorldDrainage.Solve(left, 0, leftTile, 16, 0f, true, null));
                Check(flow.Wait(5000) && flow.Result.dir != null && flow.Result.accum != null && shared.dir == null && cache.SolveCount == 2,
                    "UC-T02-03 flow requests cannot alias a fill-only result.");
                rightTile.fullWorldPos.x += 100f;
                Task<WorldDrainage> moved = Task.Run(() => WorldDrainage.Solve(right, 0, rightTile, 16, 0f, false, null));
                Check(moved.Wait(5000) && moved.Result.height.worldPos.x == rightTile.fullWorldPos.x && !ReferenceEquals(moved.Result, shared),
                    "UC-T02-03 different captured world geometry receives its own spatially correct solve.");
                source.value = 0.7f; GraphContentHasher.ComputeHashes(graph);
                Task<WorldDrainage> edited = Task.Run(() => WorldDrainage.Solve(left, 0, leftTile, 16, 0f, false, null));
                Check(edited.Wait(5000) && Mathf.Abs(edited.Result.height.arr[0] - 0.7f) < 1e-6f && Mathf.Abs(shared.height.arr[0] - 0.2f) < 1e-6f,
                    "UC-T02-03 an upstream-content-only edit invalidates the key without mutating the previous result.");
                Task<WorldDrainage> epsilon = Task.Run(() => WorldDrainage.Solve(left, 0, leftTile, 16, 0.001f, false, null));
                Check(epsilon.Wait(5000) && !ReferenceEquals(epsilon.Result, edited.Result), "UC-T02-03 epsilon alone changes solve identity.");
                Task<WorldDrainage> finer = Task.Run(() => WorldDrainage.Solve(left, 0, leftTile, 32, 0f, false, null));
                Check(finer.Wait(5000) && finer.Result.resX == 32 && shared.resX == 16, "UC-T02-03 resolution alone selects the correct coarse grid.");
                leftTile.AssignRun(new WorldGenerationScheduler().CurrentRun);
                Task<WorldDrainage> foreign = Task.Run(() => WorldDrainage.Solve(left, 0, leftTile, 16, 0f, false, null));
                Check(foreign.Wait(5000) && !ReferenceEquals(foreign.Result, edited.Result), "UC-T02-03 identical geometry in another world cannot alias a captured run.");
                Check(cache.PeakRetainedPayloadBytes > 0 && cache.WaitSeconds >= 0,
                    "UC-T02-03 reports retained coarse payload and shared-wait cost separately from unmeasured solver scratch.");
                lifetime.RequestStop();
                Check(WorldDrainage.Solve(left, 0, leftTile, 16, 0f, false, null) == null,
                    "UC-T02-03 replacing the owning world retires even completed cache results from further requests.");
            }
            finally
            {
                release.Set(); lifetime.RequestStop();
                if (first != null) first.Wait(5000); if (second != null) second.Wait(5000);
                DrainageSourceNode.entered = DrainageSourceNode.release = null;
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        // UC-T02-03: reproduce admission starvation with one worker; joining must execute the original queued factory once.
        private static void TestQueuedDrainageWorkerJoin()
        {
            var scheduler = new WorldGenerationScheduler();
            GenerationRunId run = scheduler.CurrentRun;
            var cache = new WorldDrainageCache(scheduler.CreateStopToken(run));
            Matrix source = new Matrix(new CoordRect(0, 0, 16, 16)); source.Fill(0.4f);
            WorldDrainage expected = WorldDrainage.CreateForTests(source, null);
            Task worker = null;
            Task<WorldDrainage> queued = null;
            WorldDrainage joined = null;
            int factoryCalls = 0, duplicateCalls = 0;
            using (var entered = new ManualResetEventSlim(false))
            using (var join = new ManualResetEventSlim(false))
            try
            {
                worker = scheduler.RunWorkerAsync(run, 1, scheduler.CreateStopToken(run), () =>
                {
                    entered.Set();
                    if (!join.Wait(5000)) throw new TimeoutException("UC-T02-03 queued join barrier timed out.");
                    joined = cache.GetOrCompute("queued", stop => { Interlocked.Increment(ref duplicateCalls); return null; });
                });
                Check(entered.Wait(5000), "UC-T02-03 one admitted worker holds the world's only slot.");
                queued = cache.GetOrScheduleSolve("queued", stop =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    Check(WorldGenerationScheduler.IsWorkerThread && scheduler.RunningWorkers == 1,
                        "UC-T02-03 inline shared solve retains its caller's existing worker admission.");
                    return expected;
                }, scheduler, run, 1);
                Check(scheduler.QueuedWorkers == 1 && !queued.IsCompleted, "UC-T02-03 asynchronous solve is queued behind the synchronous consumer.");
                join.Set();
                bool finished = Task.WaitAll(new Task[] { worker, queued }, 5000);
                Check(finished && ReferenceEquals(joined, expected) && ReferenceEquals(queued.Result, expected) &&
                      factoryCalls == 1 && duplicateCalls == 0 && cache.SolveCount == 1,
                    "UC-T02-03 synchronous joining of a queued async solve completes once without admission deadlock.");
            }
            finally
            {
                join.Set(); scheduler.Cancel(run);
                if (worker != null) worker.Wait(5000);
                if (queued != null) queued.Wait(5000);
                Task.WaitAll(scheduler.GetOutstandingWorkerTasks(), 5000);
            }
        }

        private static void TestSharedDrainageAsyncAndRetry()
        {
            var scheduler = new WorldGenerationScheduler();
            GenerationRunId run = scheduler.CurrentRun;
            var cache = new WorldDrainageCache(scheduler.CreateStopToken(run));
            Matrix source = new Matrix(new CoordRect(0, 0, 16, 16)); source.Fill(0.3f);
            WorldDrainage expected = WorldDrainage.CreateForTests(source, null);
            Task<WorldDrainage> first = null, second = null;
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            try
            {
                var requester = new StopToken();
                first = cache.GetOrScheduleSolve("async", stop =>
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("UC-T02-03 asynchronous solve barrier timed out.");
                    stop.ThrowIfStopRequested(); return expected;
                }, scheduler, run, 1, requester);
                Check(!first.IsCompleted && entered.Wait(5000), "UC-T02-03 asynchronous main-thread requests return while their solve is still blocked.");
                second = cache.GetOrScheduleSolve("async", stop => throw new InvalidOperationException("A duplicate solve ran."), scheduler, run, 1);
                requester.RequestStop(); release.Set();
                Check(Task.WaitAll(new Task[] { first, second }, 5000) && first.Result == null && ReferenceEquals(second.Result, expected) && cache.SolveCount == 1,
                    "UC-T02-03 asynchronous requesters share one admission and retain independent cancellation.");
                Task<WorldDrainage> failed = cache.GetOrScheduleSolve("retry", stop => throw new InvalidOperationException("UC-T02-03 deliberate solve failure"), scheduler, run, 1);
                bool faulted = false;
                try { failed.Wait(5000); } catch (AggregateException error) { faulted = error.InnerException is InvalidOperationException; }
                Task<WorldDrainage> retry = cache.GetOrScheduleSolve("retry", stop => expected, scheduler, run, 1);
                Check(faulted && retry.Wait(5000) && ReferenceEquals(retry.Result, expected) && cache.InFlightCount == 0 && cache.SolveCount == 3,
                    "UC-T02-03 failed in-flight entries are removed so an identical request can retry successfully.");
                bool refused = false;
                try { cache.GetOrCompute("uncached-main", stop => expected); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "UC-T02-03 synchronous main-thread misses refuse to wait or compute; callers must schedule and await.");
            }
            finally
            {
                release.Set(); scheduler.Cancel(run);
                if (first != null) first.Wait(5000); if (second != null) second.Wait(5000);
                Task.WaitAll(scheduler.GetOutstandingWorkerTasks(), 5000);
            }
        }
    }
}
