using System;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>UC-T02-04: safe main-thread yield boundaries before a replacement chunk becomes visible.</summary>
    public enum ChunkCommitStage
    {
        /// <summary>Allocate a detached native terrain; an indivisible Unity operation.</summary>
        AllocateTerrain,
        /// <summary>Fold water, road and sculpt inputs into managed final heights.</summary>
        PrepareHeights,
        /// <summary>Upload one bounded rectangle of height samples.</summary>
        UploadHeights,
        /// <summary>Reconcile native texture layers and managed weight data.</summary>
        PrepareTextures,
        /// <summary>Upload one bounded rectangle of texture weights.</summary>
        UploadTextures,
        /// <summary>Upload one bounded rectangle of hole flags.</summary>
        UploadHoles,
        /// <summary>Flush native heights before collider attachment; indivisible.</summary>
        SyncHeightmap,
        /// <summary>Attach final terrain/collision, apply dependent content, then reveal; indivisible.</summary>
        PublishContent,
        /// <summary>Reconcile accepted neighbouring terrain; indivisible.</summary>
        Stitch,
        /// <summary>Advance one existing world-settle callback; indivisible.</summary>
        Settle,
        /// <summary>Refresh vegetation on already accepted terrain; indivisible.</summary>
        Rescatter
    }

    /// <summary>Describes one completed stage and its managed upload payload, excluding native driver overhead.</summary>
    public readonly struct ChunkCommitStep
    {
        /// <summary>Completed stage.</summary>
        public ChunkCommitStage Stage { get; }
        /// <summary>Bytes handed to the native upload, or zero for operations without a measurable array payload.</summary>
        public long UploadBytes { get; }
        /// <summary>Creates a completed-stage record.</summary>
        /// <param name="stage">Executed stage.</param>
        /// <param name="uploadBytes">Nonnegative payload bytes; excludes driver allocation.</param>
        /// <exception cref="ArgumentOutOfRangeException">Payload is negative.</exception>
        public ChunkCommitStep(ChunkCommitStage stage, long uploadBytes = 0)
        {
            if (uploadBytes < 0) throw new ArgumentOutOfRangeException(nameof(uploadBytes));
            Stage = stage; UploadBytes = uploadBytes;
        }
    }

    /// <summary>UC-T02-04: bounded, allocation-free main-thread counters for actual commit costs; not device performance certification.</summary>
    public sealed class ChunkCommitMetrics
    {
        private readonly double[] maximumMilliseconds = new double[(int)ChunkCommitStage.Rescatter + 1];
        private readonly long[] stageCounts = new long[(int)ChunkCommitStage.Rescatter + 1];
        /// <summary>Largest complete pump duration in milliseconds, including its settle work.</summary>
        public double MaximumPumpMilliseconds { get; private set; }
        /// <summary>Largest individually measured stage in milliseconds.</summary>
        public double MaximumStageMilliseconds { get; private set; }
        /// <summary>Number of stages exceeding the configured soft pump budget; Unity calls cannot be preempted.</summary>
        public long OverBudgetOperations { get; private set; }
        /// <summary>Largest known native array upload in bytes, excluding Unity/driver allocations.</summary>
        public long PeakUploadBytes { get; private set; }
        /// <summary>Most recently completed stage, useful when diagnosing an overrun.</summary>
        public ChunkCommitStage LastStage { get; private set; }
        /// <summary>Returns the largest duration observed for a stage.</summary>
        /// <param name="stage">Defined stage enum value.</param>
        /// <returns>Milliseconds; zero before the stage executes.</returns>
        public double MaximumMilliseconds(ChunkCommitStage stage) => maximumMilliseconds[(int)stage];
        /// <summary>Returns how many times a stage executed.</summary>
        /// <param name="stage">Defined stage enum value.</param>
        /// <returns>Number of completed operations, including separate upload strips.</returns>
        public long Count(ChunkCommitStage stage) => stageCounts[(int)stage];
        internal void Record(ChunkCommitStep step, double milliseconds, double budgetMilliseconds)
        {
            LastStage = step.Stage;
            stageCounts[(int)step.Stage]++;
            maximumMilliseconds[(int)step.Stage] = Math.Max(maximumMilliseconds[(int)step.Stage], milliseconds);
            MaximumStageMilliseconds = Math.Max(MaximumStageMilliseconds, milliseconds);
            PeakUploadBytes = Math.Max(PeakUploadBytes, step.UploadBytes);
            if (milliseconds > budgetMilliseconds) OverBudgetOperations++;
        }
        internal void RecordPump(double milliseconds) => MaximumPumpMilliseconds = Math.Max(MaximumPumpMilliseconds, milliseconds);
    }
}
