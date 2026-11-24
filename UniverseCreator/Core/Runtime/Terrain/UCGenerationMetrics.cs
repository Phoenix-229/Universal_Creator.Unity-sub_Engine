using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>UC-T02-09: optional, bounded measurements for one world's generation; disabled collectors allocate no run records.</summary>
    /// <remarks>Thread-safe. Retains 16 runs and the most recent 256 samples per timing series. Percentiles describe those samples,
    /// not a device certification. Snapshot/JSON export allocates outside hot paths. Native figures are the world's admitted payload estimates.</remarks>
    public sealed partial class UCGenerationMetrics // UC-T05-08: bounded opt-in node diagnostics share the existing run owner.
    {
        internal const int MaximumRuns = 16, MaximumSamples = 256;
        private readonly object gate = new object();
        private readonly List<RunRecord> runs = new List<RunRecord>();
        private volatile bool enabled;
        private string scenario = "Unlabelled";

        /// <summary>Opt-in collection; switching off prevents new records while already captured workers finish their measurements.</summary>
        public bool Enabled { get => enabled; set => enabled = value; }
        /// <summary>Caller label, for example Cold, Warm, Edit or Cancellation; use matched profile keys when comparing runs.</summary>
        public string Scenario { get { lock (gate) return scenario; } set { lock (gate) scenario = string.IsNullOrWhiteSpace(value) ? "Unlabelled" : value.Substring(0, Math.Min(64, value.Length)); } }

        /// <summary>One timing series, with nearest-rank percentiles in milliseconds.</summary>
        [Serializable]
        public sealed class Timing
        {
            /// <summary>Number of retained samples, capped at 256.</summary>
            public int samples;
            /// <summary>Median, 95th, 99th percentile and maximum of retained samples, in milliseconds.</summary>
            public double p50, p95, p99, maximum;
        }

        /// <summary>Detached, JSON-exportable measurements for one captured run.</summary>
        [Serializable]
        public sealed class Report
        {
            /// <summary>World/run identity, caller scenario and exact captured content/quality key.</summary>
            public string run, scenario, profile;
            /// <summary>Effective child resolutions seen in this run, sorted; mixed grids do not overwrite the profile nondeterministically.</summary>
            public int[] resolutions;
            /// <summary>Retired worker requests (including cancelled-before-start), cancelled/faulted requests and expensive drainage solves.</summary>
            public long workers, cancelled, failed, drainageSolves;
            /// <summary>Node compute attempts, local cache hits and retained product hits.</summary>
            public long computes, localHits, retainedHits;
            /// <summary>Worker-thread GC bytes, or -1 where the installed runtime cannot expose that counter.</summary>
            public long workerAllocatedBytes;
            /// <summary>Upload bytes, maximum observed resident payload and world-lifetime admitted peak bytes.</summary>
            public long uploadBytes, observedResidentBytes, worldPeakBytes;
            /// <summary>Elapsed time between the first request and the latest completed worker/stage, in milliseconds.</summary>
            public double elapsedMilliseconds;
            /// <summary>Queue wait, worker body, commit stage and complete pump timings.</summary>
            public Timing queue, generation, commit, pump;
            /// <summary>One timing series for each ChunkCommitStage value, in enum order.</summary>
            public Timing[] stages;
        }

        private sealed class Samples
        {
            private readonly double[] values = new double[MaximumSamples];
            private int next, count;
            internal void Add(double value) { values[next] = Math.Max(0, value); next = (next + 1) % values.Length; count = Math.Min(count + 1, values.Length); }
            internal Timing Snapshot()
            {
                if (count == 0) return new Timing();
                var sorted = new double[count]; Array.Copy(values, sorted, count); Array.Sort(sorted);
                return new Timing { samples = count, p50 = sorted[(int)Math.Ceiling(count * 0.5) - 1],
                    p95 = sorted[(int)Math.Ceiling(count * 0.95) - 1], p99 = sorted[(int)Math.Ceiling(count * 0.99) - 1], maximum = sorted[count - 1] };
            }
        }

        internal sealed partial class RunRecord
        {
            internal readonly GenerationRunId Id;
            private readonly string label;
            private readonly object sync = new object();
            private readonly Samples queue = new Samples(), generation = new Samples(), commit = new Samples(), pump = new Samples();
            private readonly Samples[] stages = new Samples[(int)ChunkCommitStage.Rescatter + 1];
            private readonly long started = Stopwatch.GetTimestamp();
            private long ended, workers, cancelled, failed, allocated, upload, resident, peak;
            internal long Computes, LocalHits, RetainedHits, DrainageSolves;
            private string profile = "Unconfigured";
            private readonly SortedSet<int> resolutions = new SortedSet<int>();
            internal RunRecord(GenerationRunId id, string label)
            { Id = id; this.label = label; for (int i = 0; i < stages.Length; i++) stages[i] = new Samples(); }
            internal void SetProfile(string value, int resolution) { lock (sync) { profile = value; if (resolutions.Count < 32) resolutions.Add(resolution); } }
            internal void ObserveMemory(ProductMemoryBudget memory)
            {
                if (memory == null) return;
                lock (sync) { resident = Math.Max(resident, memory.ManagedBytes + memory.NativeBytes); peak = Math.Max(peak, memory.PeakBytes); }
            }
            internal void RecordWorker(double wait, double milliseconds, long bytes, bool wasCancelled, bool wasFailed)
            {
                lock (sync)
                {
                    queue.Add(wait); generation.Add(milliseconds); workers++;
                    if (wasCancelled) cancelled++; if (wasFailed) failed++;
                    allocated = bytes < 0 || allocated < 0 ? -1 : allocated + bytes;
                    ended = Stopwatch.GetTimestamp();
                }
            }
            internal void RecordStage(ChunkCommitStep step, double milliseconds, ProductMemoryBudget memory)
            {
                lock (sync)
                {
                    commit.Add(milliseconds); stages[(int)step.Stage].Add(milliseconds); upload += step.UploadBytes;
                    ObserveMemory(memory);
                    ended = Stopwatch.GetTimestamp();
                }
            }
            internal void RecordPump(double milliseconds) { lock (sync) pump.Add(milliseconds); }
            internal Report Snapshot()
            {
                lock (sync)
                {
                    var perStage = new Timing[stages.Length]; for (int i = 0; i < stages.Length; i++) perStage[i] = stages[i].Snapshot();
                    var seenResolutions = new int[resolutions.Count]; resolutions.CopyTo(seenResolutions);
                    return new Report { run = Id.ToString(), scenario = label, profile = profile, resolutions = seenResolutions, workers = workers, cancelled = cancelled,
                        failed = failed, drainageSolves = Interlocked.Read(ref DrainageSolves), computes = Interlocked.Read(ref Computes),
                        localHits = Interlocked.Read(ref LocalHits), retainedHits = Interlocked.Read(ref RetainedHits), workerAllocatedBytes = allocated,
                        uploadBytes = upload, observedResidentBytes = resident, worldPeakBytes = peak,
                        elapsedMilliseconds = Math.Max(0, ended - started) * 1000.0 / Stopwatch.Frequency,
                        queue = queue.Snapshot(), generation = generation.Snapshot(), commit = commit.Snapshot(), pump = pump.Snapshot(), stages = perStage };
                }
            }
        }

        internal RunRecord Capture(GenerationRunId run)
        {
            if (!enabled || !run.IsAssigned) return null;
            lock (gate)
            {
                foreach (RunRecord record in runs) if (record.Id.Equals(run)) return record;
                if (runs.Count == MaximumRuns) runs.RemoveAt(0);
                var added = new RunRecord(run, scenario); runs.Add(added); return added;
            }
        }

        // Profile construction happens once per captured tile, outside pixel/node loops; content hashes already include prepared inputs.
        internal RunRecord Capture(GenerationRunId run, Graph graph, GenerationSettings settings, int resolution)
        {
            RunRecord record = Capture(run);
            if (record == null) return null;
            ulong hash = 14695981039346656037UL;
            foreach (Generator node in graph.nodes) if (node != null) hash = unchecked((hash ^ node.contentHash) * 1099511628211UL);
            string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
            record.SetProfile("graph=" + hash.ToString("X16") + ";seed=" + settings.seed + ";worldRes=" + settings.resolution +
                ";origin=" + Number(settings.worldPos.x) + "," + Number(settings.worldPos.z) + ";size=" + Number(settings.worldSize.x) + "," +
                Number(settings.worldSize.z) + ";height=" + Number(settings.maxHeightMeters), resolution);
            return record;
        }

        /// <summary>Copies bounded reports for presentation/export; call from diagnostics, not Update or a graph loop.</summary>
        /// <returns>Oldest-to-newest run reports; an empty array before collection.</returns>
        public Report[] Snapshot()
        {
            lock (gate) { var result = new Report[runs.Count]; for (int i = 0; i < runs.Count; i++) result[i] = runs[i].Snapshot(); return result; }
        }

        // Some Unity profiles omit the allocation API; report unavailable rather than inventing zero allocation.
        private static readonly Func<long> allocationCounter = FindAllocationCounter();
        private static Func<long> FindAllocationCounter()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            return method == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        }
        internal static long AllocatedBytes() => allocationCounter != null ? allocationCounter() : -1;
    }
}
