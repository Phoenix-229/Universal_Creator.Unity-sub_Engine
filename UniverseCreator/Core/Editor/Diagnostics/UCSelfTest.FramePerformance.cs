// UC-T04-11: real configuration/streamed quality consumers and independent retained-window/allocation acceptance.
using System;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Geometry;
using Object = UnityEngine.Object;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestFramePerformanceContracts()
        {
            var window = new FrameTimeWindow(100);
            for (int i = 1; i <= 100; i++) window.Add(i, i / 2f, 0);
            window.Summarize(60, out var wall, out var cpu, out var gpu);
            Check(wall.sampleCount == 100 && wall.p50Ms == 50 && wall.p95Ms == 95 && wall.p99Ms == 99 && wall.maxMs == 100,
                "UC-T04-11 independent ordered 100-frame oracle verifies exact nearest-rank wall percentiles.");
            Check(Math.Abs(wall.overBudgetFraction - 0.4f) < 0.00001f && cpu.p95Ms == 47.5f && gpu.sampleCount == 0,
                "UC-T04-11 over-budget fraction, separate CPU distribution and unavailable GPU are reported honestly.");
            for (int i = 0; i < 100; i++) window.Add(10, float.NaN, float.PositiveInfinity);
            window.Add(float.NaN, 1, 1); window.Add(0, 1, 1); window.Add(float.PositiveInfinity, 1, 1);
            window.Summarize(20, out wall, out cpu, out gpu);
            Check(window.TotalFrames == 200 && window.Count == 100 && wall.p99Ms == 10 && wall.overBudgetFraction == 0 && cpu.sampleCount == 0 && gpu.sampleCount == 0,
                "UC-T04-11 ring replacement evicts old spikes; invalid samples cannot fabricate timing or count evidence.");
            Check(new FrameTimeWindow(int.MaxValue).Capacity == 8192 && new FrameTimeWindow(0).Capacity == 1,
                "UC-T04-11 scripted frame-window requests remain bounded.");
            bool invalidBudgetRejected = false;
            try { window.Summarize(float.NaN, out wall, out cpu, out gpu); }
            catch (ArgumentOutOfRangeException) { invalidBudgetRejected = true; }
            Check(invalidBudgetRejected, "UC-T04-11 nonfinite report budget is rejected explicitly.");

            var median = new RollingMedian(61);
            for (int i = 0; i < 61; i++) median.Add(16);
            median.Add(200);
            Check(median.Median() == 16 && median.Median() == 16,
                "UC-T04-11 reused median scratch preserves spike immunity and repeated-read equality.");
            // Warm comparer/JIT paths before measuring the actual Add/Median/Summarize consumers.
            window.Summarize(20, out wall, out cpu, out gpu);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { median.Add(16); median.Median(); median.Median(); window.Add(10, 5, 0); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check(allocated == 0, "UC-T04-11 warmed median reads and capture sampling allocate zero managed bytes (observed " + allocated + ").");

            Check(AdaptiveQuality.ResolveVSyncCount(30, 120, false) == 4 && AdaptiveQuality.ResolveVSyncCount(60, 120, false) == 2 &&
                AdaptiveQuality.ResolveVSyncCount(120, 120, false) == 1 && AdaptiveQuality.ResolveVSyncCount(60, 90, false) == 0,
                "UC-T04-11 desktop hardware pacing selects only supported integral display divisors.");
            Check(AdaptiveQuality.ResolveVSyncCount(60, 120, true) == 0 && AdaptiveQuality.ResolveVSyncCount(0, double.NaN, false) == 0 &&
                AdaptiveQuality.ResolveMobileCadence(30, 120) == 30 && AdaptiveQuality.ResolveMobileCadence(60, 90) == 45 &&
                AdaptiveQuality.ResolveMobileCadence(120, 60) == 60 && AdaptiveQuality.ResolveMobileCadence(60, double.PositiveInfinity) == 0,
                "UC-T04-11 mobile refresh limits remain separate from requested 30/60/120 modes; invalid display data has no invented cadence.");

            TestFramePerformanceConfiguration();
            TestFramePerformanceWorldContext(); // UC-T09-01: selected PCG world, detached settings and explicit evidence gaps.
            TestStreamedWaterQualityRevision();
        }

        private static void TestFramePerformanceConfiguration()
        {
            var go = new GameObject("UC-T04-11 frame reference");
            try
            {
                var quality = go.AddComponent<AdaptiveQuality>();
                var configure = (Action)Delegate.CreateDelegate(typeof(Action), quality,
                    typeof(AdaptiveQuality).GetMethod("EnsureConfiguration", BindingFlags.Instance | BindingFlags.NonPublic));
                quality.frameRateMode = AdaptiveQuality.FrameRateMode.Custom; quality.targetFrameTime = 22.5f;
                quality.sampleWindow = 0; quality.degradeIntervalSeconds = float.NaN;
                configure();
                Check(quality.ActiveFrameBudgetMs == 22.5f && quality.sampleWindow == 1 && quality.degradeIntervalSeconds == 1,
                    "UC-T04-11 existing custom budgets survive; invalid window/interval controls cannot throw or allocate unbounded buffers.");
                foreach (var mode in new[] { AdaptiveQuality.FrameRateMode.Fps30, AdaptiveQuality.FrameRateMode.Fps60, AdaptiveQuality.FrameRateMode.Fps120 })
                {
                    quality.frameRateMode = mode; configure();
                    Check(Math.Abs(quality.ActiveFrameBudgetMs - 1000f / (int)mode) < 0.0001f,
                        "UC-T04-11 production " + mode + " selection sets its frame envelope.");
                }
                quality.frameRateMode = AdaptiveQuality.FrameRateMode.Custom; quality.targetFrameTime = float.PositiveInfinity;
                quality.sampleWindow = int.MaxValue; quality.pinTier = true; quality.pinnedTier = AdaptiveQuality.QualityTier.High;
                configure();
                Check(quality.sampleWindow == 600 && quality.ActiveFrameBudgetMs > 33 && quality.ActiveFrameBudgetMs < 34 && quality.CurrentTier == AdaptiveQuality.QualityTier.High,
                    "UC-T04-11 invalid custom budget has finite 30-FPS fallback; high-detail pin applies before governor sampling.");
                quality.captureWarmupSeconds = 0; quality.captureWindowFrames = 128;
                quality.BeginPerformanceCapture();
                var sample = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), quality,
                    typeof(AdaptiveQuality).GetMethod("SamplePerformance", BindingFlags.Instance | BindingFlags.NonPublic));
                sample(10); sample(20);
                string report = quality.GetPerformanceReportJson();
                Check(report.Contains("\"retainedFrames\": 2") && report.Contains("\"nativeTimingEnabled\": false") && report.Contains("\"gpuWork\""),
                    "UC-T04-11 headless capture exports actual retained frames with unavailable native timing, not a fabricated GPU result.");
                quality.frameRateMode = AdaptiveQuality.FrameRateMode.Fps60; configure();
                Check(!quality.IsCapturingPerformance && quality.CapturedWindowFrames == 2,
                    "UC-T04-11 changing the FPS envelope stops capture while preserving its original report.");
                quality.BeginPerformanceCapture(); quality.EndPerformanceCapture();
                Check(!quality.IsCapturingPerformance, "UC-T04-11 explicit capture stop is safe outside Play mode; native disable is verified by the separate Play gate.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Serializable] private sealed class FrameWorldEvidence
        {
            public bool worldAvailable, terrainLayersTruncated, vegetationManagerAvailable, lightingManagerAvailable, waterManagerAvailable;
            public string worldName, worldSelection, contentRevision, deviceNotes, contentIdentityStatus, scope;
            public int seed, terrainLayerCount, treeRosterSlots;
            public float vegetationDensityScale;
            public FrameLayerEvidence[] terrainLayers;
            public FrameLightingEvidence lightingTierSettings;
        }
        [Serializable] private sealed class FrameLightingEvidence { public int cloudLayerCount, cloudNoiseResolution; }
        [Serializable] private sealed class FrameLayerEvidence { public FrameTextureEvidence diffuse; }
        [Serializable] private sealed class FrameTextureEvidence { public int width, height; }
        [Serializable] private sealed class FrameCaptureEvidence
        {
            public FrameWorldEvidence pcgWorldAtStart;
            public string scenario, cameraPath, thermalTelemetryStatus, gpuAllocationStatus, generationEvidenceStatus;
            public bool generationCollectionEnabledAtStart, generationCollectionEnabledAtEnd;
            public UCGenerationMetrics.Report[] generationAtStart, generationAtEnd;
        }

        private static void TestFramePerformanceWorldContext()
        {
            var root = new GameObject("UC-T09-01 PCG A"); root.SetActive(false);
            var nested = new GameObject("UC-T09-01 PCG B"); nested.SetActive(false); nested.transform.SetParent(root.transform, false);
            var unowned = new GameObject("UC-T09-01 unowned capture"); unowned.SetActive(false);
            var layer = new TerrainLayer(); // UC-T09-01: TerrainLayer is a native Unity object, not a ScriptableObject.
            var texture = new Texture2D(8, 4);
            try
            {
                var worldA = root.AddComponent<UniverseCreatorObject>(); worldA.enabled = false; worldA.seed = 701;
                var worldB = nested.AddComponent<UniverseCreatorObject>(); worldB.enabled = false; worldB.seed = 902;
                nested.AddComponent<UCLightingSystemManager>(); nested.AddComponent<UCWaterSystemManager>();
                var vegetationObject = new GameObject("own vegetation"); vegetationObject.transform.SetParent(root.transform, false);
                var vegetation = vegetationObject.AddComponent<UCVegetationSystemManager>(); vegetation.globalDensityScale = 0.75f;
                var probe = new GameObject("own capture"); probe.transform.SetParent(root.transform, false);
                var quality = probe.AddComponent<AdaptiveQuality>(); quality.captureWarmupSeconds = 0;
                quality.captureContentRevision = "GreenCoast-v1"; quality.captureDeviceNotes = "Owner-declared device notes";
                worldA.terrainLayers = new TerrainLayer[66]; worldA.terrainLayers[0] = layer; layer.diffuseTexture = texture;
                worldA.scatterLibrary.trees.Add(null); // Roster count deliberately differs from valid/placed content.
                quality.BeginPerformanceCapture(); quality.EndPerformanceCapture();
                var captured = JsonUtility.FromJson<FrameCaptureEvidence>(quality.GetPerformanceReportJson()).pcgWorldAtStart;
                Check(captured.worldAvailable && captured.seed == 701 && captured.worldSelection == "Parent hierarchy" &&
                    captured.vegetationManagerAvailable && captured.vegetationDensityScale == 0.75f &&
                    !captured.lightingManagerAvailable && !captured.waterManagerAvailable,
                    "UC-T09-01 capture uses its own parent PCG world and sibling manager, excluding a nested world's effects.");
                Check(captured.terrainLayerCount == 66 && captured.terrainLayers.Length == 64 && captured.terrainLayersTruncated &&
                    captured.terrainLayers[0].diffuse.width == 8 && captured.terrainLayers[0].diffuse.height == 4 && captured.treeRosterSlots == 1,
                    "UC-T09-01 loaded texture dimensions and roster slots are recorded with explicit bounded-layer truncation.");
                Check(captured.scope.Contains("PCG subengine") && captured.contentRevision == "GreenCoast-v1" &&
                    captured.contentIdentityStatus.Contains("not verified"),
                    "UC-T09-01 author revision and settings describe a subengine capture without claiming verified matching content or phone support.");
                var initialEvidence = JsonUtility.FromJson<FrameCaptureEvidence>(quality.GetPerformanceReportJson());
                Check(!initialEvidence.generationCollectionEnabledAtStart && !worldA.GenerationScheduler.Metrics.Enabled &&
                    initialEvidence.generationAtStart.Length == 0 && initialEvidence.thermalTelemetryStatus.StartsWith("Unavailable") &&
                    initialEvidence.gpuAllocationStatus.StartsWith("Unavailable"),
                    "UC-T09-02 capture does not enable generation instrumentation or invent unavailable thermal/GPU-allocation measurements.");
                worldA.seed = 702; worldA.terrainLayers = null; vegetation.globalDensityScale = 2;
                quality.captureContentRevision = "changed";
                var retained = JsonUtility.FromJson<FrameCaptureEvidence>(quality.GetPerformanceReportJson()).pcgWorldAtStart;
                Check(retained.seed == 701 && retained.terrainLayerCount == 66 && retained.vegetationDensityScale == 0.75f && retained.contentRevision == "GreenCoast-v1",
                    "UC-T09-01 exported start context survives later world, layer, manager and author-label changes without aliasing live settings.");
                quality.captureWorld = worldB; quality.BeginPerformanceCapture(); quality.EndPerformanceCapture();
                captured = JsonUtility.FromJson<FrameCaptureEvidence>(quality.GetPerformanceReportJson()).pcgWorldAtStart;
                Check(captured.seed == 902 && captured.worldSelection == "Explicit" && !captured.vegetationManagerAvailable && captured.lightingManagerAvailable,
                    "UC-T09-01 explicit second-world selection does not capture outer-world vegetation.");
                Check(captured.lightingTierSettings != null && captured.lightingTierSettings.cloudLayerCount > 0 && captured.lightingTierSettings.cloudNoiseResolution > 0,
                    "UC-T09-01 the actual resolved lighting envelope survives JSON serialization, including cloud count and texture size.");
                var standalone = unowned.AddComponent<AdaptiveQuality>(); standalone.captureContentRevision = new string('r', 300);
                standalone.captureDeviceNotes = new string('d', 300); standalone.BeginPerformanceCapture(); standalone.EndPerformanceCapture();
                captured = JsonUtility.FromJson<FrameCaptureEvidence>(standalone.GetPerformanceReportJson()).pcgWorldAtStart;
                Check(!captured.worldAvailable && captured.worldSelection.StartsWith("Unavailable") && captured.contentRevision.Length == 128 && captured.deviceNotes.Length == 128,
                    "UC-T09-01 unowned capture never selects a global world and bounds caller context without inventing configuration.");
                Check(worldA.seed == 702 && vegetation.globalDensityScale == 2 && worldB.seed == 902,
                    "UC-T09-01 profiling leaves both worlds' authored generation and quality settings unchanged.");

                var metrics = worldA.GenerationScheduler.Metrics; metrics.Enabled = true; metrics.Scenario = "Cold";
                var cold = metrics.Capture(worldA.GenerationScheduler.BeginRun());
                cold.RecordStage(new ChunkCommitStep(ChunkCommitStage.UploadHeights, 4), 1, null);
                quality.captureWorld = worldA; quality.captureScenario = "Cold-to-Warm"; quality.captureCameraPath = "CoastalLoop-speed10";
                quality.BeginPerformanceCapture();
                cold.RecordStage(new ChunkCommitStep(ChunkCommitStage.UploadHeights, 8), 2, null);
                metrics.Scenario = "Warm"; metrics.Capture(worldA.GenerationScheduler.BeginRun());
                quality.captureWorld = worldB; quality.captureScenario = "changed"; quality.captureCameraPath = "changed";
                quality.EndPerformanceCapture();
                var interval = JsonUtility.FromJson<FrameCaptureEvidence>(quality.GetPerformanceReportJson());
                Check(interval.generationAtStart.Length == 1 && interval.generationAtStart[0].uploadBytes == 4 &&
                    interval.generationAtEnd.Length == 2 && interval.generationAtEnd[0].uploadBytes == 12 &&
                    interval.generationAtEnd[1].scenario == "Warm" && interval.generationEvidenceStatus.Contains("not an exact"),
                    "UC-T09-02 detached start/end histories retain independent counters and real run labels from the original capture world.");
                Check(interval.scenario == "Cold-to-Warm" && interval.cameraPath == "CoastalLoop-speed10" &&
                    interval.generationCollectionEnabledAtStart && interval.generationCollectionEnabledAtEnd,
                    "UC-T09-02 capture labels and enabled-state evidence survive later inspector changes without relabelling another world's measurements.");
                metrics.Scenario = "Edit"; metrics.Capture(worldA.GenerationScheduler.BeginRun());
                var stopped = JsonUtility.FromJson<FrameCaptureEvidence>(quality.GetPerformanceReportJson());
                Check(stopped.generationAtEnd.Length == 2 && stopped.generationAtEnd[0].uploadBytes == 12,
                    "UC-T09-02 later generation cannot rewrite the stopped capture's retained end evidence.");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(unowned); Object.DestroyImmediate(layer); Object.DestroyImmediate(texture); }
        }

        private static void TestStreamedWaterQualityRevision()
        {
            var rootA = new GameObject("UC-T04-11 quality world A"); rootA.SetActive(false);
            var rootB = new GameObject("UC-T04-11 quality world B"); rootB.SetActive(false);
            try
            {
                var worldA = rootA.AddComponent<UniverseCreatorObject>(); worldA.enabled = false;
                var worldB = rootB.AddComponent<UniverseCreatorObject>(); worldB.enabled = false;
                var manager = rootA.AddComponent<UCWaterSystemManager>(); manager.qualityMode = UCWaterSystemManager.WaterQualityMode.Low;
                var a = new GameObject("local ocean"); a.transform.SetParent(rootA.transform, false);
                var volume = a.AddComponent<UCWaterVolume>(); volume.kind = UCWaterVolume.BodyKind.Ocean; volume.surface.octaves = 99;
                var b = new GameObject("foreign ocean"); b.transform.SetParent(rootB.transform, false);
                var foreign = b.AddComponent<UCWaterVolume>(); foreign.kind = UCWaterVolume.BodyKind.Ocean; foreign.surface.octaves = 99;
                rootA.SetActive(true); rootB.SetActive(true);
                var apply = (Action)Delegate.CreateDelegate(typeof(Action), manager,
                    typeof(UCWaterSystemManager).GetMethod("ApplyQualityIfChanged", BindingFlags.Instance | BindingFlags.NonPublic));
                apply();
                float expected = Application.isMobilePlatform ? 1 : 2;
                Check(volume.surface.octaves == expected && foreign.surface.octaves == 99,
                    "UC-T04-11 production water-quality cap reaches local physics without overwriting a foreign world's volume.");
                int count = UCWaterVolume.All.Count; ulong revision = UCWaterVolume.RegistryRevision;
                Object.DestroyImmediate(a);
                var replacement = new GameObject("streamed replacement"); replacement.transform.SetParent(rootA.transform, false);
                volume = replacement.AddComponent<UCWaterVolume>(); volume.kind = UCWaterVolume.BodyKind.Ocean; volume.surface.octaves = 99;
                apply();
                Check(UCWaterVolume.All.Count == count && UCWaterVolume.RegistryRevision != revision && volume.surface.octaves == expected && foreign.surface.octaves == 99,
                    "UC-T04-11 same-count streamed replacement receives the existing quality tier through the registry witness.");
            }
            finally { Object.DestroyImmediate(rootA); Object.DestroyImmediate(rootB); }
        }
    }
}
