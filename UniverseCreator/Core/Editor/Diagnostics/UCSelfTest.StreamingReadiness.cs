// UC-T09-01..04: focused existing-owner capture, native publication, landing and region-demand acceptance.
using System;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Runs the implemented Topic 9 capture and chunk-readiness contracts in an isolated Unity project, exiting zero only on success.</summary>
        /// <remarks>Main-thread batch entry; creates temporary native fixtures. Host gameplay, rendered appearance, Player/device timings and later Topic 9 tasks are outside this gate.</remarks>
        /// <exception cref="InvalidOperationException">Unity is not running in batch mode.</exception>
        public static void RunStreamingReadinessBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Streaming readiness acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            foreach (Action fixture in new Action[] { TestProductMemoryBudget, // UC repair 30: shared river identity, byte admission and cancellation.
                TestFramePerformanceContracts, TestStagedChunkCommits,
                TestChunkRestore, TestClimateRestoreContracts, TestRiverMemoryOutputCoexistence,
                TestChunkReadiness, TestChunkRegionDemand, TestTerrainResidency, TestFarTerrain, TestGenerationDemand, TestGenerationMetrics })
            {
                try { fixture(); }
                catch (Exception error) { Check(false, "UC-T09-01..04 " + fixture.Method.Name + " exception: " + error); }
            }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }
    }
}
