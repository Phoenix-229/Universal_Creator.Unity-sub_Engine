// UC-T04-11: bounded acceptance samples; sorting and report creation stay outside the frame sampling path.
using System;

namespace UniverseCreator.Optimization
{
    /// <summary>Milliseconds for one retained frame metric. SampleCount zero means unavailable, never a zero-cost timing claim.</summary>
    [Serializable]
    public struct FrameMetricSummary
    {
        /// <summary>Number of finite positive samples represented by these statistics.</summary>
        public int sampleCount;
        /// <summary>Nearest-rank median in milliseconds.</summary>
        public float p50Ms;
        /// <summary>Nearest-rank 95th percentile in milliseconds.</summary>
        public float p95Ms;
        /// <summary>Nearest-rank 99th percentile in milliseconds.</summary>
        public float p99Ms;
        /// <summary>Largest retained sample in milliseconds.</summary>
        public float maxMs;
        /// <summary>Fraction from 0 to 1 exceeding the requested positive frame budget.</summary>
        public float overBudgetFraction;
    }

    /// <summary>A thread-confined, fixed-capacity window of wall, CPU and GPU timings. It retains the latest frames without allocating during Add.</summary>
    public sealed class FrameTimeWindow
    {
        private readonly float[] wall, cpu, gpu, scratch;
        private int head, count;

        /// <summary>Lifetime accepted frame count, including frames evicted from the retained window.</summary>
        public long TotalFrames { get; private set; }
        /// <summary>Number of retained frames, bounded by Capacity.</summary>
        public int Count => count;
        /// <summary>Retained frame ceiling, from 1 to 8192.</summary>
        public int Capacity => wall.Length;

        /// <summary>Allocates reusable sampling and sorting storage once.</summary>
        /// <param name="capacity">Requested retained frames; clamped to 1–8192.</param>
        public FrameTimeWindow(int capacity)
        {
            capacity = Math.Max(1, Math.Min(8192, capacity));
            wall = new float[capacity]; cpu = new float[capacity]; gpu = new float[capacity]; scratch = new float[capacity];
        }

        /// <summary>Records a frame; invalid wall time rejects the frame, while unsupported CPU/GPU timings remain unavailable.</summary>
        /// <param name="wallMs">Finite positive elapsed wall-frame milliseconds.</param>
        /// <param name="cpuMs">Finite positive CPU-frame milliseconds, or NaN/zero for unavailable.</param>
        /// <param name="gpuMs">Finite positive GPU-work milliseconds, or NaN/zero for unavailable.</param>
        public void Add(float wallMs, float cpuMs, float gpuMs)
        {
            if (!Valid(wallMs)) return;
            wall[head] = wallMs; cpu[head] = Valid(cpuMs) ? cpuMs : float.NaN; gpu[head] = Valid(gpuMs) ? gpuMs : float.NaN;
            head = (head + 1) % wall.Length;
            count = Math.Min(count + 1, wall.Length);
            TotalFrames++;
        }

        /// <summary>Computes nearest-rank statistics for the latest retained window using reused scratch; does not mutate samples.</summary>
        /// <param name="budgetMs">Positive finite requested frame budget in milliseconds.</param>
        /// <param name="wallSummary">Wall-frame timing output; unavailable when no frames were accepted.</param>
        /// <param name="cpuSummary">CPU-frame output; unavailable when the backend supplied no valid timings.</param>
        /// <param name="gpuSummary">GPU-work output; unavailable when the backend supplied no valid timings.</param>
        /// <exception cref="ArgumentOutOfRangeException">The budget is nonfinite or nonpositive.</exception>
        public void Summarize(float budgetMs, out FrameMetricSummary wallSummary, out FrameMetricSummary cpuSummary, out FrameMetricSummary gpuSummary)
        {
            if (!Valid(budgetMs)) throw new ArgumentOutOfRangeException(nameof(budgetMs));
            wallSummary = SummarizeMetric(wall, budgetMs); cpuSummary = SummarizeMetric(cpu, budgetMs); gpuSummary = SummarizeMetric(gpu, budgetMs);
        }

        private FrameMetricSummary SummarizeMetric(float[] values, float budgetMs)
        {
            int valid = 0, over = 0;
            for (int i = 0; i < count; i++)
            {
                float value = values[i];
                if (!Valid(value)) continue;
                scratch[valid++] = value;
                if (value > budgetMs) over++;
            }
            if (valid == 0) return default;
            Array.Sort(scratch, 0, valid);
            return new FrameMetricSummary { sampleCount = valid,
                p50Ms = scratch[(int)Math.Ceiling(valid * 0.50) - 1], p95Ms = scratch[(int)Math.Ceiling(valid * 0.95) - 1],
                p99Ms = scratch[(int)Math.Ceiling(valid * 0.99) - 1], maxMs = scratch[valid - 1], overBudgetFraction = (float)over / valid };
        }

        private static bool Valid(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
