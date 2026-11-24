using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T02-09: exercise the actual evaluator/cache hooks and worker admission, with fixed content and isolated memory.
        private static void TestGenerationMetrics()
        {
            var scheduler = new WorldGenerationScheduler();
            Check(scheduler.Metrics.Capture(scheduler.CurrentRun) == null && scheduler.Metrics.Snapshot().Length == 0,
                "UC-T02-09 disabled diagnostics allocate no run record.");
            scheduler.Metrics.Enabled = true;
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new MemoryCountingNode { id = "metrics-source", contentHash = 17 };
            graph.nodes.Add(source);
            var evaluator = new GraphEvaluator(graph);
            var memory = new ProductMemoryBudget(16384);
            var settings = new GenerationSettings { seed = 12, resolution = 17, maxHeightMeters = 100,
                worldPos = Vector3.zero, worldSize = new Vector3(16, 100, 16) };
            Dictionary<string, RetainedProduct> retained = null;
            TileData tile = null;
            try
            {
                foreach (string scenario in new[] { "Cold", "Warm", "Edit" })
                {
                    scheduler.Metrics.Scenario = scenario;
                    GenerationRunId run = scheduler.BeginRun();
                    if (scenario == "Edit") source.contentHash = 18;
                    tile = MemoryTestTile(memory); tile.retained = retained ?? tile.retained;
                    tile.metrics = scheduler.Metrics.Capture(run, graph, settings, 17);
                    TileData workerTile = tile;
                    Task work = scheduler.RunWorkerAsync(run, 1, new StopToken(), () =>
                    {
                        evaluator.PullNode(source, 0, workerTile, new StopToken());
                        evaluator.PullNode(source, 0, workerTile, new StopToken());
                    });
                    Check(work.Wait(5000), "UC-T02-09 " + scenario + " measurement worker completes.");
                    tile.metrics.ObserveMemory(memory);
                    retained = new Dictionary<string, RetainedProduct>(tile.retained);
                    tile.AcceptRetainedProducts(); tile.ReleaseProductMemory();
                }
                UCGenerationMetrics.Report[] reports = scheduler.Metrics.Snapshot();
                Check(reports.Length == 3 && reports[0].computes == 1 && reports[0].retainedHits == 0 &&
                      reports[1].computes == 0 && reports[1].retainedHits == 1 && reports[1].localHits == 1 && reports[2].computes == 1,
                    "UC-T02-09 cold/warm/edit reports distinguish actual computation, local reuse and retained cache hits.");
                Check(reports[0].profile == reports[1].profile && reports[1].profile != reports[2].profile &&
                      reports[0].worldPeakBytes > 0 && reports[0].generation.samples == 1 && reports[0].queue.samples == 1,
                    "UC-T02-09 matched content retains its profile while an edit changes it, with real timing and payload observations.");
                var record = scheduler.Metrics.Capture(scheduler.CurrentRun);
                for (int i = 1; i <= 400; i++) record.RecordStage(new ChunkCommitStep(ChunkCommitStage.UploadHeights, 4), i, memory);
                var distribution = scheduler.Metrics.Snapshot()[2];
                Check(distribution.commit.samples == 256 && distribution.commit.p50 == 272 && distribution.commit.p95 == 388 &&
                      distribution.commit.p99 == 398 && distribution.uploadBytes == 1600,
                    "UC-T02-09 bounded timing series report nearest-rank percentiles of the retained samples and total uploads.");

                // Measure instrumentation overhead without asserting a hardware-dependent speed ratio.
                const int repeats = 10000;
                tile = MemoryTestTile(memory);
                var timer = Stopwatch.StartNew(); for (int i = 0; i < repeats; i++) tile.RecordCompute();
                timer.Stop(); double disabledMs = timer.Elapsed.TotalMilliseconds;
                tile.metrics = record;
                timer.Restart(); for (int i = 0; i < repeats; i++) tile.RecordCompute();
                timer.Stop(); double enabledMs = timer.Elapsed.TotalMilliseconds;
                UnityEngine.Debug.Log("UC-T02-09 instrumentation calibration: calls=" + repeats + "; disabledMs=" + disabledMs.ToString("F4") +
                    "; enabledMs=" + enabledMs.ToString("F4") + ". Microbenchmark only; no device performance certification.");
                for (int i = 0; i < 24; i++) scheduler.Metrics.Capture(scheduler.BeginRun());
                Check(scheduler.Metrics.Snapshot().Length == UCGenerationMetrics.MaximumRuns,
                    "UC-T02-09 repeated restarts retain only the bounded run history.");
            }
            finally
            {
                tile?.ReleaseProductMemory(); memory.ClearRetained();
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
