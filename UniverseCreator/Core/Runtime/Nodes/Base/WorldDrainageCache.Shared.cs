using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes
{
    public sealed partial class WorldDrainageCache : IDisposable
    {
        // UC-T02-03: native graph equality is illegal on workers; these are identities of captured managed objects.
        private sealed class ReferenceIdentity : IEqualityComparer<object>
        {
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private sealed class SolveEntry
        {
            internal readonly TaskCompletionSource<WorldDrainage> completion = new TaskCompletionSource<WorldDrainage>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Func<StopToken, WorldDrainage> compute;
            internal StopToken stop;
            // UC-T02-03: zero is unclaimed, positive is the computing thread, -1 is retired before execution.
            internal int computingThread;
        }
        private readonly Dictionary<object, long> identities = new Dictionary<object, long>(new ReferenceIdentity());
        private readonly Dictionary<string, SolveEntry> inFlight = new Dictionary<string, SolveEntry>();
        private readonly StopToken lifetimeStop;
        private long nextIdentity, solveCount, waitTicks, retainedBytes, peakRetainedBytes;
        private int waitingRequesters;
        // UC-T02-06: shared coarse products are pinned for this cache lifetime, then individually by tile consumers.
        private readonly ProductMemoryBudget memory;
        private readonly Dictionary<string, ProductMemoryBudget.Lease> memoryPins = new Dictionary<string, ProductMemoryBudget.Lease>();

        /// <summary>Creates a solve cache whose shared computations belong to one world revision.</summary>
        /// <param name="lifetimeStop">World revision cancellation; null creates an independently owned cache. Never pass a single requester's token.</param>
        /// <param name="memory">Optional world byte budget; pins shared completed payloads until cache disposal.</param>
        public WorldDrainageCache(StopToken lifetimeStop = null, ProductMemoryBudget memory = null)
        { this.lifetimeStop = lifetimeStop ?? new StopToken(); this.memory = memory; }

        /// <summary>UC-T02-06: cancels this solve owner and releases its completed payload references; tile consumer pins remain valid.</summary>
        /// <remarks>Thread-safe and idempotent. In-flight computations observe cancellation and retire through their existing completion path.</remarks>
        public void Dispose()
        {
            lifetimeStop.RequestStop();
            lock (gate)
            {
                map.Clear(); identities.Clear(); retainedBytes = 0;
                foreach (ProductMemoryBudget.Lease pin in memoryPins.Values) pin.Dispose();
                memoryPins.Clear();
            }
        }

        /// <summary>Number of actually executed solve factories, including failed attempts.</summary>
        public long SolveCount => Interlocked.Read(ref solveCount);
        /// <summary>Total seconds spent by synchronous worker followers waiting for another owner; excludes solve CPU time.</summary>
        public double WaitSeconds => Interlocked.Read(ref waitTicks) / (double)Stopwatch.Frequency;
        /// <summary>Current number of owned in-flight computations, independent of how many requesters share them.</summary>
        public int InFlightCount { get { lock (gate) return inFlight.Count; } }
        /// <summary>Current synchronous worker followers; the single computing owner is excluded.</summary>
        public int WaitingRequesters => Volatile.Read(ref waitingRequesters);
        /// <summary>Peak retained coarse-grid payload estimate in bytes; counts shared aliases conservatively and excludes solver scratch/object overhead.</summary>
        public long PeakRetainedPayloadBytes { get { lock (gate) return peakRetainedBytes; } }

        private void StoreCompleted(string key, WorldDrainage value)
        {
            if (lifetimeStop.StopRequested) return;
            if (memory != null)
            {
                ProductMemoryBudget.Lease pin = memory.Acquire(value, ProductMemoryFootprint.Estimate(value), 0, key);
                if (memoryPins.TryGetValue(key, out ProductMemoryBudget.Lease previousPin)) previousPin.Dispose();
                memoryPins[key] = pin;
            }
            if (map.TryGetValue(key, out WorldDrainage previous)) retainedBytes -= PayloadBytes(previous);
            map[key] = value;
            retainedBytes += PayloadBytes(value);
            peakRetainedBytes = Math.Max(peakRetainedBytes, retainedBytes);
        }

        private static long PayloadBytes(WorldDrainage value) => value == null ? 0L :
            4L * ((value.height?.arr?.LongLength ?? 0L) + (value.filled?.LongLength ?? 0L) +
                  (value.dir?.LongLength ?? 0L) + (value.accum?.LongLength ?? 0L));

        private long Identity(object value)
        {
            if (ReferenceEquals(value, null)) return 0;
            lock (gate)
            {
                if (!identities.TryGetValue(value, out long identity)) identities.Add(value, identity = ++nextIdentity);
                return identity;
            }
        }

        // UC-T02-03: ordinal identities avoid both Unity APIs and hash collisions; content hashes also catch supported input edits.
        internal string BuildInputKey(Generator consumer, int inputIndex, TileData tile, int resolution, float epsilon, bool flow)
        {
            Graph graph = tile.Graph;
            Connection connection = graph.GetInputConnection(consumer.id, inputIndex);
            Generator source = string.IsNullOrEmpty(connection.sourceNodeId) ? null : graph.GetNodeById(connection.sourceNodeId);
            var culture = CultureInfo.InvariantCulture;
            return tile.RunId + "|g" + Identity(graph) + "|s" + Identity(source) + "|p" + connection.sourcePortIndex +
                "|h" + (source != null ? source.contentHash : 0UL) + "|r" + resolution + "|e" + epsilon.ToString("R", culture) + "|f" + flow +
                "|x" + tile.fullWorldPos.x.ToString("R", culture) + "|y" + tile.worldPos.y.ToString("R", culture) +
                "|z" + tile.fullWorldPos.z.ToString("R", culture) + "|sx" + tile.fullWorldSize.x.ToString("R", culture) +
                "|sy" + tile.worldSize.y.ToString("R", culture) + "|sz" + tile.fullWorldSize.z.ToString("R", culture) +
                "|height" + tile.worldHeight.ToString("R", culture);
        }

        private SolveEntry Join(string key, Func<StopToken, WorldDrainage> compute, StopToken stop, out bool owner)
        {
            lock (gate)
            {
                if (inFlight.TryGetValue(key, out SolveEntry entry)) { owner = false; return entry; }
                entry = new SolveEntry { compute = compute, stop = stop };
                if (map.TryGetValue(key, out WorldDrainage value))
                {
                    owner = false; entry.completion.SetResult(value); return entry;
                }
                inFlight.Add(key, entry); owner = true; return entry;
            }
        }

        /// <summary>Computes one shared key on its first caller's worker; followers reuse that computation.</summary>
        /// <param name="key">Non-null full input key.</param>
        /// <param name="compute">Pure managed factory observing the supplied world-lifetime token; null is rejected.</param>
        /// <param name="requester">Local cancellation, or null; cancellation discards only this requester's result.</param>
        /// <param name="memoryConsumer">Optional tile receiving a payload pin atomically with the cache-lifetime check.</param>
        /// <returns>Immutable shared result, or null after cancellation/absent input. Failures propagate and the next request may retry.</returns>
        /// <exception cref="InvalidOperationException">An uncached call is made on a synchronization-context thread, or a factory recursively requests itself.</exception>
        /// <remarks>Never call a synchronous cache miss from the Editor/UI; use GetOrScheduleSolve instead.
        /// A worker joining a queued solve claims its factory inline under its existing admission to avoid starvation.
        /// A local cancellation may leave a bounded solve running for the owning world and its other requesters.</remarks>
        public WorldDrainage GetOrCompute(string key, Func<StopToken, WorldDrainage> compute, StopToken requester = null, TileData memoryConsumer = null)
        {
            if (compute == null) throw new ArgumentNullException(nameof(compute));
            if (lifetimeStop.StopRequested || (requester != null && requester.StopRequested)) return null;
            if (TryGet(key, out WorldDrainage ready)) return Borrow(ready, memoryConsumer, requester);
            if (SynchronizationContext.Current != null)
                throw new InvalidOperationException("Await GetOrScheduleSolve on the main thread; a drainage cache miss cannot block it.");
            SolveEntry entry = Join(key, compute, lifetimeStop, out _);
            if (Volatile.Read(ref entry.computingThread) == Thread.CurrentThread.ManagedThreadId && !entry.completion.Task.IsCompleted)
                throw new InvalidOperationException("A drainage factory recursively requested its own input key.");
            // UC-T02-03: a queued async solve must not wait for the admission this caller already holds.
            bool owner = ComputeEntry(key, entry);
            long started = Stopwatch.GetTimestamp();
            if (!owner) Interlocked.Increment(ref waitingRequesters);
            try
            {
                while (!entry.completion.Task.IsCompleted)
                {
                    if (entry.stop.StopRequested || (requester != null && requester.StopRequested)) return null;
                    try { entry.completion.Task.Wait(25); }
                    catch (AggregateException) { break; } // GetAwaiter below preserves the actual factory exception.
                }
                return entry.stop.StopRequested || (requester != null && requester.StopRequested) ? null :
                    Borrow(entry.completion.Task.GetAwaiter().GetResult(), memoryConsumer, requester);
            }
            finally
            {
                if (!owner)
                {
                    Interlocked.Decrement(ref waitingRequesters);
                    Interlocked.Add(ref waitTicks, Stopwatch.GetTimestamp() - started);
                }
            }
        }

        private WorldDrainage Borrow(WorldDrainage value, TileData consumer, StopToken requester)
        {
            lock (gate)
            {
                if (lifetimeStop.StopRequested || (requester != null && requester.StopRequested)) return null;
                consumer?.TrackProductMemory(value, "drainage");
                return value;
            }
        }

        /// <summary>Returns a shared solve task without blocking the main thread, scheduling only the first request through the global worker authority.</summary>
        /// <param name="key">Non-null full input key, identical for equivalent captured input/geometry/options.</param>
        /// <param name="compute">Pure managed factory; caller keeps its captured inputs alive until the returned task completes.</param>
        /// <param name="scheduler">Non-null owning world's shared worker authority.</param>
        /// <param name="run">Owning revision captured with the inputs.</param>
        /// <param name="worldLimit">Positive per-world worker limit.</param>
        /// <param name="requester">Optional local cancellation; does not cancel a sibling's task.</param>
        /// <returns>Shared result or null for cancellation; factory failures propagate and are evicted for retry.</returns>
        /// <remarks>Await this task; do not synchronously block an admitted worker waiting for another admission.</remarks>
        public Task<WorldDrainage> GetOrScheduleSolve(string key, Func<StopToken, WorldDrainage> compute,
            WorldGenerationScheduler scheduler, GenerationRunId run, int worldLimit, StopToken requester = null)
        {
            if (compute == null) throw new ArgumentNullException(nameof(compute));
            if (scheduler == null) throw new ArgumentNullException(nameof(scheduler));
            if (worldLimit < 1) throw new ArgumentOutOfRangeException(nameof(worldLimit));
            if (lifetimeStop.StopRequested || (requester != null && requester.StopRequested) || !scheduler.IsCurrent(run))
                return Task.FromResult<WorldDrainage>(null);
            StopToken sharedStop = new StopToken(lifetimeStop, scheduler.CreateStopToken(run));
            SolveEntry entry = Join(key, compute, sharedStop, out bool owner);
            if (owner)
            {
                try
                {
                    scheduler.RunWorkerAsync(run, worldLimit, sharedStop, () => ComputeEntry(key, entry),
                        () => RetireUnclaimed(key, entry, null));
                }
                catch (Exception error) { RetireUnclaimed(key, entry, error); }
            }
            return AwaitResult(entry.completion.Task, requester, sharedStop);
        }

        private static async Task<WorldDrainage> AwaitResult(Task<WorldDrainage> task, StopToken requester, StopToken sharedStop)
        {
            WorldDrainage value = await task.ConfigureAwait(false);
            return sharedStop.StopRequested || (requester != null && requester.StopRequested) ? null : value;
        }

        private void RetireUnclaimed(string key, SolveEntry entry, Exception failure)
        {
            // UC-T02-03: queued retirement cannot publish null over a solve already claimed by another worker.
            if (Interlocked.CompareExchange(ref entry.computingThread, -1, 0) == 0)
                CompleteEntry(key, entry, null, failure);
        }

        private bool ComputeEntry(string key, SolveEntry entry)
        {
            if (entry.completion.Task.IsCompleted ||
                Interlocked.CompareExchange(ref entry.computingThread, Thread.CurrentThread.ManagedThreadId, 0) != 0) return false;
            StopToken computationStop = entry.stop;
            Interlocked.Increment(ref solveCount);
            try
            {
                WorldDrainage result = computationStop.StopRequested ? null : entry.compute(computationStop);
                CompleteEntry(key, entry, result, null, computationStop);
            }
            catch (OperationCanceledException) when (computationStop.StopRequested) { CompleteEntry(key, entry, null, null); }
            catch (Exception error) { CompleteEntry(key, entry, null, error); }
            return true;
        }

        private void CompleteEntry(string key, SolveEntry entry, WorldDrainage value, Exception failure, StopToken computationStop = null)
        {
            lock (gate)
            {
                if (!inFlight.TryGetValue(key, out SolveEntry current) || !ReferenceEquals(current, entry)) return;
                if (lifetimeStop.StopRequested || (computationStop != null && computationStop.StopRequested)) value = null;
                // UC-T02-06: admission failure must complete every follower with the error, never orphan its task.
                try { if (value != null && failure == null) StoreCompleted(key, value); }
                catch (Exception error) { failure = error; value = null; }
                inFlight.Remove(key);
            }
            if (failure == null) entry.completion.TrySetResult(value);
            else entry.completion.TrySetException(failure);
        }
    }
}
