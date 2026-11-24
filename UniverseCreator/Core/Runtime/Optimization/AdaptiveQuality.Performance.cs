// UC-T04-11: opt-in device evidence uses Unity's delayed frame timings, without saving fetched data or allocating each frame.
using System;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Optimization
{
    public partial class AdaptiveQuality
    {
        [Tooltip("Exclude this many seconds before retaining reference timings. 120 seconds is a starting warm-up; sustained thermal testing still needs a longer run.")]
        public float captureWarmupSeconds = 120f;
        [Range(128, 8192), Tooltip("Latest frames retained in the report. Older frames are evicted; duration/count remain recorded.")]
        public int captureWindowFrames = 2048;
        [Tooltip("Reference scene, visible extent, device variant/RAM and traversal conditions. Keep this identical between comparisons.")]
        public string captureLabel = "";

        private FrameTimeWindow performanceWindow;
        private FrameTiming[] timingBuffer;
        private bool capturing, collectNativeTimings;
        private double captureStarted, captureEnded, warmupUntil, nextMemorySample;
        private ulong lastTimingTimestamp;
        private long allocationBaseline, mainThreadAllocatedBytes, peakAllocatedMemory;
        private PerformanceReport captureMetadata;

        /// <summary>True between BeginPerformanceCapture and stop/configuration change/disable; sampling is inactive otherwise.</summary>
        public bool IsCapturingPerformance => capturing;
        /// <summary>Number of post-warm-up frames retained for export; zero before valid samples arrive.</summary>
        public int CapturedWindowFrames => performanceWindow != null ? performanceWindow.Count : 0;

        /// <summary>Starts a fresh bounded capture after the configured warm-up, discarding the prior in-memory capture. Call on Unity's main thread.</summary>
        /// <remarks>CPU/GPU readings require a graphics Player/Play session and a supported Frame Timing backend. Release builds may require Frame Timing Stats; no build settings are changed. UC-T09-01 snapshots the selected PCG world once; asset names and roster counts do not certify identical content.</remarks>
        public void BeginPerformanceCapture()
        {
            EnsureConfiguration();
            captureWindowFrames = Mathf.Clamp(captureWindowFrames, 128, 8192);
            captureWarmupSeconds = float.IsNaN(captureWarmupSeconds) || float.IsInfinity(captureWarmupSeconds) ? 120f : Mathf.Clamp(captureWarmupSeconds, 0f, 3600f);
            performanceWindow = new FrameTimeWindow(captureWindowFrames);
            timingBuffer = new FrameTiming[1];
            captureStarted = Time.realtimeSinceStartupAsDouble; captureEnded = captureStarted;
            warmupUntil = captureStarted + captureWarmupSeconds; nextMemorySample = warmupUntil;
            mainThreadAllocatedBytes = allocationBaseline = peakAllocatedMemory = 0; lastTimingTimestamp = 0;
            collectNativeTimings = Application.isPlaying && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && FrameTimingManager.IsFeatureEnabled();
            captureMetadata = new PerformanceReport {
                label = BoundedCaptureText(captureLabel), unityVersion = Application.unityVersion, deviceModel = SystemInfo.deviceModel,
                operatingSystem = SystemInfo.operatingSystem, systemMemoryMiB = SystemInfo.systemMemorySize,
                graphicsDevice = SystemInfo.graphicsDeviceName, graphicsBackend = SystemInfo.graphicsDeviceType.ToString(),
                width = Screen.width, height = Screen.height, refreshHz = Screen.currentResolution.refreshRateRatio.value,
                requestedFps = (int)frameRateMode, requestedBudgetMs = targetFrameTime, displayBudgetMs = ActiveFrameBudgetMs,
                applicationTargetFps = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount,
                qualityAtStart = CurrentTier.ToString(), qualityPinned = pinTier, governorsEnabled = enableGovernors,
                measuredAdaptationEnabled = adaptToDeviceMeasurements, minimumMeasuredTier = minimumDeviceTier.ToString(),
                maximumUnityMemoryMiB = maximumUnityMemoryMiB, // UC-T07-12: matched-quality captures record the active measurement controller and authored constraints.
                warmupSeconds = captureWarmupSeconds, nativeTimingEnabled = collectNativeTimings,
                isEditor = Application.isEditor, isPlaying = Application.isPlaying,
                platform = Application.platform.ToString(), developmentBuild = Debug.isDebugBuild,
                graphicsMemoryMiB = SystemInfo.graphicsMemorySize,
                pcgWorldAtStart = CapturePerformanceWorld() }; // UC-T09-01: read-only subengine context, separate from whole-game device certification.
            captureMetadata.scenario = BoundedCaptureText(captureScenario); captureMetadata.cameraPath = BoundedCaptureText(captureCameraPath);
            captureMetadata.thermalTelemetryStatus = "Unavailable: no thermal telemetry collector; sustained device observation is required.";
            captureMetadata.gpuAllocationStatus = "Unavailable: graphicsMemoryMiB is device metadata, not measured GPU allocations.";
            CaptureGenerationEvidence(captureMetadata, true); // UC-T09-02: existing world metrics, with no implicit collection, cache or generation changes.
            capturing = true;
        }

        /// <summary>Stops sampling and preserves the last capture for export; safe when already stopped. Call on Unity's main thread.</summary>
        public void EndPerformanceCapture()
        {
            if (!capturing) return;
            captureEnded = Time.realtimeSinceStartupAsDouble;
            if (captureMetadata != null)
            {
                captureMetadata.qualityAtEnd = CurrentTier.ToString();
                captureMetadata.deviceObservationStatus = DeviceAdaptationStatus;
                CaptureGenerationEvidence(captureMetadata, false); // UC-T09-02: stopped evidence cannot be rewritten by later world activity.
            }
            capturing = false;
            captureMetricsWorld = null; // UC-T09-02: the detached stopped report no longer needs a live world reference.
        }

        private void SamplePerformance(float frameMs)
        {
            if (!capturing) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (collectNativeTimings) FrameTimingManager.CaptureFrameTimings();
            if (now < warmupUntil) return;
            float cpuMs = float.NaN, gpuMs = float.NaN;
            if (collectNativeTimings && FrameTimingManager.GetLatestTimings(1, timingBuffer) > 0)
            {
                FrameTiming timing = timingBuffer[0];
                // Timings arrive four frames late. Repeated backend results cannot count as additional measurements.
                if (timing.frameStartTimestamp != lastTimingTimestamp)
                {
                    lastTimingTimestamp = timing.frameStartTimestamp;
                    cpuMs = (float)timing.cpuFrameTime; gpuMs = (float)timing.gpuFrameTime;
                }
            }
            performanceWindow.Add(frameMs, cpuMs, gpuMs);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            if (allocationBaseline > 0 && allocated >= allocationBaseline) mainThreadAllocatedBytes += allocated - allocationBaseline;
            allocationBaseline = allocated;
            if (now >= nextMemorySample)
            {
                peakAllocatedMemory = Math.Max(peakAllocatedMemory, Profiler.GetTotalAllocatedMemoryLong());
                nextMemorySample = now + 1;
            }
        }

        /// <summary>Exports metadata and nearest-rank statistics for the latest retained window; returns empty before a capture. Call on the main thread outside the frame loop.</summary>
        /// <returns>JSON with unavailable metrics indicated by sampleCount zero. CPU/GPU timings are delayed and unpaired with the current wall-frame sample. UC-T09-02 includes detached cumulative generation histories; these are not timings attributed to an exact frame interval.</returns>
        public string GetPerformanceReportJson()
        {
            if (performanceWindow == null || captureMetadata == null) return "";
            var report = captureMetadata;
            performanceWindow.Summarize(report.requestedBudgetMs, out report.wallFrame, out report.cpuFrame, out report.gpuWork);
            report.totalSampledFrames = performanceWindow.TotalFrames; report.retainedFrames = performanceWindow.Count;
            report.durationSeconds = Math.Max(0, (capturing ? Time.realtimeSinceStartupAsDouble : captureEnded) - captureStarted);
            report.mainThreadAllocatedBytes = mainThreadAllocatedBytes; report.peakUnityAllocatedBytes = peakAllocatedMemory;
            report.instancedDrawsAtExport = GeometryComplexity.DrawCalls; report.instancedUnitsAtExport = GeometryComplexity.InstancedUnits;
            if (capturing)
            {
                report.qualityAtEnd = CurrentTier.ToString();
                report.deviceObservationStatus = DeviceAdaptationStatus;
                CaptureGenerationEvidence(report, false);
            }
            return JsonUtility.ToJson(report, true);
        }

        // Private serialized report fields are developer evidence, not a product/runtime dependency.
        [Serializable]
        private sealed class PerformanceReport
        {
            public string label, unityVersion, deviceModel, operatingSystem, graphicsDevice, graphicsBackend, qualityAtStart, qualityAtEnd;
            public int systemMemoryMiB, width, height, requestedFps, applicationTargetFps, vSyncCount, retainedFrames;
            public double refreshHz, durationSeconds;
            public float requestedBudgetMs, displayBudgetMs, warmupSeconds, instancedDrawsAtExport, instancedUnitsAtExport;
            public bool qualityPinned, governorsEnabled, nativeTimingEnabled, isEditor, isPlaying;
            public bool measuredAdaptationEnabled;
            public string minimumMeasuredTier, deviceObservationStatus;
            public int maximumUnityMemoryMiB;
            public string platform;
            public bool developmentBuild;
            public int graphicsMemoryMiB;
            public PerformanceWorldReport pcgWorldAtStart;
            public string scenario, cameraPath, generationScenarioAtStart, generationScenarioAtEnd, generationEvidenceStatus, thermalTelemetryStatus, gpuAllocationStatus;
            public bool generationCollectionEnabledAtStart, generationCollectionEnabledAtEnd;
            public TerrainSystem.UCGenerationMetrics.Report[] generationAtStart, generationAtEnd;
            public long totalSampledFrames, mainThreadAllocatedBytes, peakUnityAllocatedBytes;
            public FrameMetricSummary wallFrame, cpuFrame, gpuWork;
        }
    }
}
