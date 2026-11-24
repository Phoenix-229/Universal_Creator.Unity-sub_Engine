// UC-T04-11: focused complete water acceptance after the first package gate exposed inherited draft defects.
using System;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Runs registered Topic 4 water contracts in isolated batch mode, exiting zero on success or one on failure.</summary>
        /// <remarks>Creates temporary native/editor fixtures only; graphics appearance, audible playback and device timing remain separate acceptance.</remarks>
        /// <exception cref="InvalidOperationException">Called outside Unity batch mode.</exception>
        public static void RunWaterBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Water acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            TestWaterTopicContracts();
            TestWaterSurfaceWaves();
            TestWaterQualityTierTable();
            TestWaterOctaveCap();
            TestWaterWeatherCouplingMath();
            bool ok = failures.Count == 0;
            Debug.Log(Report(ok)); Console.WriteLine(Report(ok)); EditorApplication.Exit(ok ? 0 : 1);
        }

        private static void TestWaterTopicContracts()
        {
            // UC-T04-11: a broken fixture remains a named failure, while independent contracts still execute.
            foreach (Action fixture in new Action[] { TestRiverTerrainModes, TestRiverReadOnlyOwnership, TestManualRiverCourseOwnership,
                TestRiverSurfaceFitting, TestWaterJunctionRecords, TestProgressiveRiverAuthoring, TestRiverShorelineContact,
                TestWaterJunctionGeometry, TestWaterJunctionLifecycle, TestAcceptedWaterQueries, TestWaterContactEvents,
                TestFramePerformanceContracts }) // UC-T04-11: FPS/capture/allocation and actual streamed quality consumers join both focused and package gates.
            {
                try { fixture(); }
                catch (Exception error) { Check(false, "UC-T04-11 " + fixture.Method.Name + " exception: " + error); }
            }
        }
    }
}
