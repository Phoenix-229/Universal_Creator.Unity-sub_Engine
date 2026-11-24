// UC-T08-14: one Topic 8 gate reuses the registered graph, environment and lifetime fixtures.
using System;
using UnityEditor;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Runs the Lighting Graph's code/native contracts and exits Unity with zero only when all checks pass.</summary>
        /// <remarks>Main-thread isolated batch entry point. Exceptions are reported as failures using the existing suite counters.
        /// Covers baseline/dynamic acceptance, retained clock, regional consumers, camera ownership and paced refresh.
        /// Headless execution does not certify rendered dawn/noon/night/weather, GPU histories, Player builds or phone performance.</remarks>
        public static void RunLightingBatch()
        {
            checks = 0; failures.Clear();
            var groups = new (string id, Action run)[] {
                ("graph domains/stages", TestCompiledGraphPlans), ("execution admission", TestCompiledExecutionDomains),
                ("graph editing", TestSharedGraphEditing), ("graph migration", TestGraphMigrationExecution),
                ("saved graph/window integration", TestFourGraphSample),
                ("retained weather output", TestWeatherOutputNodeCapturesBiases), ("retained lighting output", TestLightingOutputNodeCapturesBiases),
                ("UC-T08-02 shared clock", TestWorldClockContinuation), ("UC-T08-03/04 baseline and terrain correction", TestClimateBaselineContracts),
                ("UC-T08-05 regional consumers", TestRegionalEnvironmentConsumers), ("UC-T08-06 moving weather", TestWeatherRegionTransitions),
                ("UC-T08-07 coherent lighting", TestEnvironmentLightingSamples), ("continuous sky and moon", TestContinuousSkyLighting),
                ("metered daylight and seasons", TestMeteredDaylightAndSeasons), // UC midday repair: run independent photometric and motion regressions in this gate too.
                ("catalog stars", TestCatalogStars), // UC celestial repair: the full and focused gates exercise the same production owner.
                ("UC-T08-08 bounded effects", TestBoundedEnvironmentEffects),
                ("UC-T08-09 surface stores", TestSurfaceWeatherStores), ("UC-T08-10 geography", TestEnvironmentGeography),
                ("UC-T08-12 budgets/retirement", TestLightingRefreshBudgets),
#if UC_URP
                ("UC-T08-11 camera histories", TestCameraLightingHistories),
#endif
                ("clock math", TestClockIsDeterministic), ("solar geography", TestSolarTimeCorrection),
                ("weather field", TestWeatherCellField), ("exposure", TestExposureModel), ("sun/moon handover", TestSunMoonHandover),
                ("sky/ambient", TestSkyAmbientProbe), ("irradiance consumer", TestIrradianceGIWire),
                ("probe scheduler", TestProbeRefreshScheduler), ("probe cadence", TestRefreshCadence),
                ("probe uploads", TestIrradianceUpload), ("temporal math", TestTemporalReprojection),
                ("quality tiers", TestLightingTier), ("shared clock authority", TestLightingTimeIsWeatherOwned) };
            foreach (var group in groups)
                try { group.run(); } catch (Exception error) { Check(false, group.id + " lighting acceptance exception: " + error); }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }
    }
}
