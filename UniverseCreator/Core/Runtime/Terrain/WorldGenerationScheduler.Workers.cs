using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public sealed partial class WorldGenerationScheduler
    {
        // UC-T02-02: one process-wide admission owner, including old revisions and every world/domain.
        private static readonly object workerSync = new object();
        private static readonly List<WorkerRequest> queuedWorkers = new List<WorkerRequest>();
        private static readonly List<WorkerRequest> runningWorkers = new List<WorkerRequest>();
        private static int globalWorkerLimit = Math.Max(1, Math.Min(4, System.Environment.ProcessorCount - 1));
        private int runningWorkerCount;
        [ThreadStatic] private static bool isWorkerThread;

        private sealed class WorkerRequest
        {
            internal WorldGenerationScheduler owner;
            internal int limit;
            internal StopToken stop, runStop;
            internal bool IsCancelled => stop.StopRequested || runStop.StopRequested;
            internal Action work, retired;
            internal GenerationDemand demand; // UC-T02-08: captured visibility/collision demand and monotonic wait age.
            internal UCGenerationMetrics.RunRecord metrics;
            internal long started, allocationStart;
            internal double queueMilliseconds;
            internal readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>True inside an admitted generation worker; nested scatter must stay serial to respect its admission.</summary>
        public static bool IsWorkerThread => isWorkerThread;
        /// <summary>Process-wide CPU worker cap; defaults to at most four and leaves one reported processor available.</summary>
        public static int GlobalWorkerLimit { get { lock (workerSync) return globalWorkerLimit; } }
        /// <summary>Real admitted workers across all worlds, including cancelled workers that have not exited.</summary>
        public static int GlobalRunningWorkers { get { lock (workerSync) return runningWorkers.Count; } }
        /// <summary>This world's real admitted worker count, including superseded revisions.</summary>
        public int RunningWorkers { get { lock (workerSync) return runningWorkerCount; } }
        /// <summary>Queued requests for this world; these occupy no thread-pool workers.</summary>
        public int QueuedWorkers
        {
            get { lock (workerSync) { int count = 0; foreach (var request in queuedWorkers) if (ReferenceEquals(request.owner, this)) count++; return count; } }
        }
        /// <summary>This world's cancelled workers that still own their admissions until they exit.</summary>
        public int CancelledRunningWorkers
        {
            get { lock (workerSync) { int count = 0; foreach (var request in runningWorkers) if (ReferenceEquals(request.owner, this) && request.IsCancelled) count++; return count; } }
        }

        /// <summary>Sets a measured platform CPU cap while the generation pool is idle.</summary>
        /// <param name="limit">Positive number of real worker bodies allowed process-wide.</param>
        /// <exception cref="ArgumentOutOfRangeException">Limit is not positive.</exception>
        /// <exception cref="InvalidOperationException">Any request is queued or running; its admission cannot be silently revoked.</exception>
        public static void ConfigureGlobalWorkerLimit(int limit)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
            lock (workerSync)
            {
                if (queuedWorkers.Count != 0 || runningWorkers.Count != 0) throw new InvalidOperationException("Change the generation worker cap only while idle.");
                globalWorkerLimit = limit;
            }
        }

        /// <summary>Queues CPU work under both the world and global cap without blocking the caller or reserving a waiting thread.</summary>
        /// <param name="run">Captured world revision; stale/foreign requests retire without running their body.</param>
        /// <param name="worldLimit">Positive per-world cap, also counting its cancelled predecessors.</param>
        /// <param name="stopToken">Non-null local cancellation token; the scheduler also observes the captured world run.</param>
        /// <param name="work">CPU body; no Unity resource access. Null is rejected.</param>
        /// <param name="retired">Optional resource-release callback, called exactly once even when cancelled before admission.</param>
        /// <returns>Task completing after body and retirement; normal cancellation completes without a fault, body/retirement exceptions fault it.</returns>
        /// <exception cref="ArgumentOutOfRangeException">World limit is not positive.</exception>
        /// <exception cref="ArgumentNullException">Token or work is null.</exception>
        public Task RunWorkerAsync(GenerationRunId run, int worldLimit, StopToken stopToken, Action work, Action retired = null)
            => RunWorkerAsync(run, worldLimit, stopToken, work, retired,
                new GenerationDemand(false, 1, 0, System.Diagnostics.Stopwatch.GetTimestamp()));

        // UC-T02-08: production chunk dispatch supplies its demand; other CPU consumers retain fair FIFO admission.
        internal Task RunWorkerAsync(GenerationRunId run, int worldLimit, StopToken stopToken, Action work, Action retired, GenerationDemand demand)
        {
            if (worldLimit < 1) throw new ArgumentOutOfRangeException(nameof(worldLimit));
            if (stopToken == null) throw new ArgumentNullException(nameof(stopToken));
            if (work == null) throw new ArgumentNullException(nameof(work));
            var request = new WorkerRequest { owner = this, runStop = CreateStopToken(run), limit = worldLimit, stop = stopToken, work = work, retired = retired, demand = demand };
            request.metrics = Metrics.Capture(run); // UC-T02-09: one optional record shared by workers of the captured revision.
            lock (workerSync) queuedWorkers.Add(request);
            PumpWorkers();
            return request.completion.Task;
        }

        // UC-T02-08: a camera turn can reclassify queued work without resetting its age or touching already-running bodies.
        internal bool UpdateQueuedDemand(StopToken token, GenerationDemand demand)
        {
            lock (workerSync)
            {
                foreach (WorkerRequest request in queuedWorkers)
                    if (ReferenceEquals(request.owner, this) && ReferenceEquals(request.stop, token))
                    {
                        request.demand = new GenerationDemand(demand.CollisionCritical, demand.Band, demand.Distance, request.demand.QueuedAt);
                        return true;
                    }
            }
            return false;
        }

        /// <summary>Captures every outstanding task owned by this world, including superseded workers.</summary>
        /// <returns>Independent array for bounded shutdown/reload waits; never wait from a publication callback.</returns>
        public Task[] GetOutstandingWorkerTasks()
        {
            lock (workerSync)
            {
                var result = new List<Task>();
                foreach (var request in runningWorkers) if (ReferenceEquals(request.owner, this)) result.Add(request.completion.Task);
                foreach (var request in queuedWorkers) if (ReferenceEquals(request.owner, this)) result.Add(request.completion.Task);
                return result.ToArray();
            }
        }

        private static void PumpWorkers()
        {
            List<WorkerRequest> start = null, cancelled = null;
            lock (workerSync)
            {
                for (int i = 0; i < queuedWorkers.Count;)
                {
                    WorkerRequest request = queuedWorkers[i];
                    if (request.IsCancelled)
                    {
                        queuedWorkers.RemoveAt(i);
                        if (cancelled == null) cancelled = new List<WorkerRequest>();
                        cancelled.Add(request);
                    }
                    else i++;
                }
                // UC-T02-08: select only eligible worlds; a saturated world cannot block another world's demand.
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                while (runningWorkers.Count < globalWorkerLimit)
                {
                    int best = -1;
                    for (int i = 0; i < queuedWorkers.Count; i++)
                    {
                        WorkerRequest request = queuedWorkers[i];
                        if (request.owner.runningWorkerCount >= request.limit || request.IsCancelled) continue;
                        if (best < 0 || GenerationDemand.Compare(request.demand, queuedWorkers[best].demand, now) < 0) best = i;
                    }
                    if (best < 0) break;
                    WorkerRequest selected = queuedWorkers[best];
                    queuedWorkers.RemoveAt(best);
                    runningWorkers.Add(selected); selected.owner.runningWorkerCount++;
                    if (start == null) start = new List<WorkerRequest>();
                    start.Add(selected);
                }
            }
            // Never call consumers while holding workerSync: retirement may release native snapshots on the main thread.
            if (cancelled != null) foreach (var request in cancelled) FinishWorker(request, null, false);
            if (start != null) foreach (var request in start)
            {
                try { Task.Run(() => ExecuteWorker(request)); }
                catch (Exception exception) { FinishWorker(request, exception, true); }
            }
        }

        private static void ExecuteWorker(WorkerRequest request)
        {
            Exception failure = null;
            bool previous = isWorkerThread;
            isWorkerThread = true;
            if (request.metrics != null)
            {
                request.started = System.Diagnostics.Stopwatch.GetTimestamp();
                request.queueMilliseconds = (request.started - request.demand.QueuedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                request.allocationStart = UCGenerationMetrics.AllocatedBytes();
            }
            try { if (!request.IsCancelled) request.work(); }
            catch (OperationCanceledException) when (request.IsCancelled) { }
            catch (Exception exception) { failure = exception; }
            finally
            {
                isWorkerThread = previous;
                FinishWorker(request, failure, true);
            }
        }

        private static void FinishWorker(WorkerRequest request, Exception failure, bool admitted)
        {
            try { request.retired?.Invoke(); }
            catch (Exception exception) { failure = failure == null ? exception : new AggregateException(failure, exception); }
            if (request.metrics != null)
            {
                long ended = System.Diagnostics.Stopwatch.GetTimestamp();
                long allocated = admitted && request.started > 0 && request.allocationStart >= 0 ? UCGenerationMetrics.AllocatedBytes() - request.allocationStart : -1;
                double duration = admitted && request.started > 0 ? (ended - request.started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : 0;
                double wait = request.started > 0 ? request.queueMilliseconds : (ended - request.demand.QueuedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                request.metrics.RecordWorker(wait, duration, allocated, request.IsCancelled, failure != null);
            }
            if (admitted)
            {
                lock (workerSync)
                {
                    runningWorkers.Remove(request);
                    request.owner.runningWorkerCount--;
                }
            }
            if (failure == null) request.completion.TrySetResult(true);
            else request.completion.TrySetException(failure);
            if (admitted) PumpWorkers();
        }
    }
}
