// UC-T07-11: bounded current-device observations drive the existing quality owner; display waits and absent metrics are not GPU headroom.
using System;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace UniverseCreator.Optimization
{
    public partial class AdaptiveQuality
    {
        /// <summary>Uses measured CPU/GPU work when available; missing native timings allow conservative wall-frame downshifts only.</summary>
        [Tooltip("Adapt to this device's current measured work. Native CPU/GPU recovery requires supported Frame Timing Stats; model names and benchmark estimates are not measurements.")]
        public bool adaptToDeviceMeasurements = true;
        /// <summary>Lowest permitted quality tier; the measured controller never lowers quality past this authored floor.</summary>
        [Tooltip("Lowest acceptable quality. Reaching this floor does not prove the requested FPS; retain target-device acceptance captures.")]
        public QualityTier minimumDeviceTier = QualityTier.Medium;
        /// <summary>Seconds of sustained reliable headroom required before restoring one quality tier, 1…120.</summary>
        [Tooltip("Seconds of measured CPU/GPU headroom before restoring one tier. This prevents visible quality oscillation.")]
        public float deviceRecoverySeconds = 10;
        /// <summary>Optional Unity allocated-memory ceiling in MiB; zero disables this metric, whose readings exclude other apps and driver memory.</summary>
        [Tooltip("Optional Unity allocated-memory ceiling in MiB. Zero leaves the hard world product budgets in charge; total phone RAM is not free RAM.")]
        public int maximumUnityMemoryMiB;
        /// <summary>Current observation availability and limiting condition; no measured thermal/GPU values are fabricated.</summary>
        public string DeviceAdaptationStatus => deviceStatus;

        private readonly FrameTiming[] deviceTiming = new FrameTiming[1];
        private MeasuredQualityResponse deviceResponse;
        private ulong deviceTimestamp;
        private double nextDeviceMemorySample, deviceMemoryPressureUntil;
        private long deviceAllocatedMemory;
        private string deviceStatus = "Waiting for measured device frames; thermal telemetry is unavailable.";
        private int deviceWindow;

        private void OnEnable() { Application.lowMemory += OnDeviceLowMemory; }
        private void OnDeviceLowMemory() { deviceMemoryPressureUntil = Time.realtimeSinceStartupAsDouble + 30; }
        private void RetireDeviceAdaptation()
        {
            Application.lowMemory -= OnDeviceLowMemory;
            deviceResponse = null; deviceTimestamp = 0; deviceAllocatedMemory = 0;
            deviceMemoryPressureUntil = nextDeviceMemorySample = 0;
        }

        private void UpdateDeviceAdaptation(float wallMs)
        {
            minimumDeviceTier = (QualityTier)Mathf.Clamp((int)minimumDeviceTier, 0, 3);
            deviceRecoverySeconds = float.IsNaN(deviceRecoverySeconds) || float.IsInfinity(deviceRecoverySeconds) ? 10 : Mathf.Clamp(deviceRecoverySeconds, 1, 120);
            maximumUnityMemoryMiB = Mathf.Clamp(maximumUnityMemoryMiB, 0, 1048576);
            int window = Mathf.Clamp(sampleWindow, 30, 600);
            if (deviceResponse == null || deviceWindow != window)
            { deviceResponse = new MeasuredQualityResponse(window); deviceWindow = window; }
            float workMs = float.NaN; bool completeWork = false;
            bool native = Application.isPlaying && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && FrameTimingManager.IsFeatureEnabled();
            if (native)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, deviceTiming) > 0 && deviceTiming[0].frameStartTimestamp != deviceTimestamp)
                {
                    var timing = deviceTiming[0]; deviceTimestamp = timing.frameStartTimestamp;
                    // Present waits are pacing, not work. Other main/render work still participates in the maximum.
                    double main = timing.cpuMainThreadFrameTime - timing.cpuMainThreadPresentWaitTime;
                    double cpu = Math.Max(main, timing.cpuRenderThreadFrameTime), gpu = timing.gpuFrameTime;
                    bool validCpu = cpu > 0 && !double.IsNaN(cpu) && !double.IsInfinity(cpu);
                    bool validGpu = gpu > 0 && !double.IsNaN(gpu) && !double.IsInfinity(gpu);
                    if (validCpu || validGpu) workMs = (float)Math.Max(validCpu ? cpu : 0, validGpu ? gpu : 0);
                    completeWork = validCpu && validGpu;
                }
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextDeviceMemorySample)
            { deviceAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong(); nextDeviceMemorySample = now + 1; }
            bool memoryPressure = now < deviceMemoryPressureUntil ||
                (maximumUnityMemoryMiB > 0 && deviceAllocatedMemory > (long)maximumUnityMemoryMiB * 1024 * 1024);
            QualityTier before = currentTier;
            currentTier = pinTier ? pinnedTier : deviceResponse.Observe(currentTier, minimumDeviceTier, wallMs, workMs, completeWork,
                Time.unscaledDeltaTime, ActiveFrameBudgetMs, degradeIntervalSeconds, deviceRecoverySeconds, memoryPressure);
            if (before != currentTier || pinTier) deviceResponse.Reset();
            deviceStatus = pinTier ? "Quality pinned; measured adaptation is held." : memoryPressure ? "Measured memory pressure; quality floor and hard world budgets remain active." :
                deviceResponse.HasRecoveryMetrics ? "Measured CPU/GPU adaptation active; thermal telemetry is unavailable." :
                "CPU/GPU work unavailable or stale: wall frames allow downshifts; recovery waits for measured headroom. Thermal telemetry is unavailable.";
            _statusLine = deviceStatus;
            _viewState = TerrainSystem.Geometry.ViewSideState.Default; // UC-T07-11: partial files require their own namespace qualification.
            if (!pinTier) for (int i = 0; i < Mathf.Max(0, (int)currentTier - 1); i++)
                _viewState = TerrainSystem.Geometry.ViewSideDegradation.Apply(_viewState, (TerrainSystem.Geometry.DegradeStep)i);
        }
    }

    // One bounded response state, driven by AdaptiveQuality.Update. Arrays are allocated only on configuration changes.
    internal sealed class MeasuredQualityResponse
    {
        private readonly float[] walls, work;
        private readonly bool[] reliable;
        private int index, count, workCount, reliableCount;
        private double wallSum, workSum;
        private float hold, recovery;
        internal bool HasRecoveryMetrics => count == walls.Length && reliableCount >= walls.Length * 0.75f;
        internal MeasuredQualityResponse(int window)
        {
            if (window < 1 || window > 600) throw new ArgumentOutOfRangeException(nameof(window));
            walls = new float[window]; work = new float[window]; reliable = new bool[window];
        }
        internal void Reset() { index = count = workCount = reliableCount = 0; wallSum = workSum = 0; hold = recovery = 0; }
        internal AdaptiveQuality.QualityTier Observe(AdaptiveQuality.QualityTier current, AdaptiveQuality.QualityTier floor,
            float wallMs, float workMs, bool completeWork, float dt, float budgetMs, float downHold, float upHold, bool memoryPressure)
        {
            if (!Valid(wallMs) || !Valid(dt) || !Valid(budgetMs)) return current;
            if ((int)current > (int)floor) { Reset(); return floor; }
            if (count == walls.Length)
            {
                wallSum -= walls[index];
                if (Valid(work[index])) { workSum -= work[index]; workCount--; }
                if (reliable[index]) reliableCount--;
            }
            else count++;
            walls[index] = wallMs; work[index] = workMs; reliable[index] = completeWork && Valid(workMs);
            wallSum += wallMs;
            if (Valid(workMs)) { workSum += workMs; workCount++; }
            if (reliable[index]) reliableCount++;
            index = (index + 1) % walls.Length;
            if (count < walls.Length) return current;
            double workAverage = workCount > 0 ? workSum / workCount : 0;
            bool over = memoryPressure || wallSum / count > budgetMs * 1.08 || workAverage > budgetMs * 1.08;
            hold = over ? hold + dt : 0;
            recovery = !over && HasRecoveryMetrics && workAverage < budgetMs * 0.8 && wallSum / count <= budgetMs * 1.05 ? recovery + dt : 0;
            if (hold >= downHold && (int)current < (int)floor) { Reset(); return (AdaptiveQuality.QualityTier)((int)current + 1); }
            if (recovery >= upHold && current != AdaptiveQuality.QualityTier.Ultra) { Reset(); return (AdaptiveQuality.QualityTier)((int)current - 1); }
            return current;
        }
        private static bool Valid(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
