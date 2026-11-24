// UC-T04-11: optional frame pacing shares the existing quality owner and preserves custom serialized budgets.
using System;
using UnityEngine;
using UniverseCreator.TerrainSystem.Geometry; // UC-T04-11: shared view-state ownership stays in the existing governor namespace.

namespace UniverseCreator.Optimization
{
    public partial class AdaptiveQuality
    {
        /// <summary>Requested FPS mode; Custom preserves the existing millisecond budget without controlling presentation.</summary>
        public enum FrameRateMode { Custom = 0, Fps30 = 30, Fps60 = 60, Fps120 = 120 }

        [Tooltip("30 FPS is the first reference target; 60 and 120 remain selectable. Custom preserves an existing millisecond budget.")]
        public FrameRateMode frameRateMode = FrameRateMode.Custom;

        [Tooltip("Opt in to application frame pacing in Play mode. Only one active AdaptiveQuality may own it; mobile display divisors can lower the actual cadence.")]
        public bool controlFrameRate;

        private static AdaptiveQuality frameRateOwner;
        private int priorTarget, priorVSync, appliedTarget, appliedVSync;
        private double appliedRefresh = -1;
        private int configuredWindow;
        private float configuredBudget;
        private bool configuredGovernors;
        private bool configuredDeviceAdaptation; // UC-T07-11: changing response modes cannot retain an incompatible sampling window.

        /// <summary>Budget used for quality decisions, in milliseconds; includes a known display limit only while this component owns pacing.</summary>
        public float ActiveFrameBudgetMs { get; private set; } = 16.67f;

        /// <summary>True when this enabled component currently owns application frame pacing; false outside Play mode or after a conflicting owner.</summary>
        public bool IsFrameRateOwner => frameRateOwner == this;

        private void Reset()
        {
            // Newly added authoring starts at 30; existing saved components retain Custom and their original target.
            frameRateMode = FrameRateMode.Fps30;
            targetFrameTime = 1000f / 30f;
        }

        private void OnValidate() => ValidateConfiguration();
        private void OnDisable() { ReleaseFrameRate(); EndPerformanceCapture(); RetireDeviceAdaptation(); } // UC-T07-11: remove the low-memory subscription and delayed measurement history.

        private void ValidateConfiguration()
        {
            if (frameRateMode != FrameRateMode.Custom && frameRateMode != FrameRateMode.Fps30 &&
                frameRateMode != FrameRateMode.Fps60 && frameRateMode != FrameRateMode.Fps120)
                frameRateMode = FrameRateMode.Custom;
            sampleWindow = Mathf.Clamp(sampleWindow, 1, 600);
            targetFrameTime = frameRateMode == FrameRateMode.Custom ?
                (float.IsNaN(targetFrameTime) || float.IsInfinity(targetFrameTime) ? 1000f / 30f : Mathf.Clamp(targetFrameTime, 1f, 1000f)) :
                1000f / (int)frameRateMode;
            if (float.IsNaN(degradeIntervalSeconds) || float.IsInfinity(degradeIntervalSeconds)) degradeIntervalSeconds = 1f;
            degradeIntervalSeconds = Mathf.Clamp(degradeIntervalSeconds, 0.1f, 60f);
            pinnedTier = (QualityTier)Mathf.Clamp((int)pinnedTier, 0, 3);
        }

        private void EnsureConfiguration()
        {
            ValidateConfiguration();
            if (pinTier) currentTier = pinnedTier;
            ApplyFrameRate();
            float budget = targetFrameTime;
            if (IsFrameRateOwner && appliedRefresh > 0)
            {
                double cadence = Application.isMobilePlatform ? ResolveMobileCadence((int)frameRateMode, appliedRefresh) :
                    (appliedVSync > 0 ? appliedRefresh / appliedVSync : Math.Min((int)frameRateMode, appliedRefresh));
                if (cadence > 0) budget = Mathf.Max(budget, (float)(1000.0 / cadence));
            }
            ActiveFrameBudgetMs = budget;
            if (frameTimes != null && configuredWindow == sampleWindow && configuredBudget == budget && configuredGovernors == enableGovernors && configuredDeviceAdaptation == adaptToDeviceMeasurements) return;
            frameTimes = new float[sampleWindow];
            deviceResponse = null; deviceTimestamp = 0; // UC-T07-11: never mix measurements across target/window changes.
            frameIndex = 0;
            configuredWindow = sampleWindow;
            configuredBudget = budget;
            configuredGovernors = enableGovernors;
            configuredDeviceAdaptation = adaptToDeviceMeasurements;
            _viewState = ViewSideState.Default;
            _statusValid = false;
            InitGovernors();
            // Changing the measurement envelope starts a new capture instead of mixing FPS modes in one report.
            EndPerformanceCapture();
        }

        private void ApplyFrameRate()
        {
            if (!Application.isPlaying || !isActiveAndEnabled || !controlFrameRate || frameRateMode == FrameRateMode.Custom)
            { ReleaseFrameRate(); return; }
            if (frameRateOwner != null && frameRateOwner != this) return;
            bool acquiring = frameRateOwner == null;
            if (acquiring)
            {
                frameRateOwner = this;
                priorTarget = Application.targetFrameRate;
                priorVSync = QualitySettings.vSyncCount;
            }
            int requested = (int)frameRateMode;
            double refresh = Screen.currentResolution.refreshRateRatio.value;
            int sync = ResolveVSyncCount(requested, refresh, Application.isMobilePlatform);
            if (!acquiring && requested == appliedTarget && refresh == appliedRefresh && sync == appliedVSync) return;
            appliedTarget = requested; appliedRefresh = refresh; appliedVSync = sync;
            QualitySettings.vSyncCount = sync;
            Application.targetFrameRate = requested;
        }

        private void ReleaseFrameRate()
        {
            if (!IsFrameRateOwner) return;
            // Do not restore over a newer application's explicit presentation setting.
            if (Application.targetFrameRate == appliedTarget && QualitySettings.vSyncCount == appliedVSync)
            { Application.targetFrameRate = priorTarget; QualitySettings.vSyncCount = priorVSync; }
            frameRateOwner = null;
            appliedRefresh = -1;
        }

        /// <summary>Resolves desktop hardware VSync when the refresh/request ratio is an integer from 1 to 4; otherwise uses the software cap.</summary>
        /// <param name="requestedFps">Positive requested frames per second, normally 30, 60 or 120.</param>
        /// <param name="refreshHz">Display refresh in Hz; nonfinite/nonpositive values have no known divisor.</param>
        /// <param name="mobile">True for Unity mobile platforms, which ignore vSyncCount.</param>
        /// <returns>VSync count 1–4 when supported, otherwise 0.</returns>
        public static int ResolveVSyncCount(int requestedFps, double refreshHz, bool mobile)
        {
            if (mobile || requestedFps <= 0 || double.IsNaN(refreshHz) || double.IsInfinity(refreshHz) || refreshHz <= 0) return 0;
            double ratio = refreshHz / requestedFps;
            int divisor = (int)Math.Round(Math.Min(5, ratio));
            return divisor >= 1 && divisor <= 4 && Math.Abs(ratio - divisor) < 0.01 ? divisor : 0;
        }

        /// <summary>Computes Unity mobile's refresh-divisor cadence so display waiting does not falsely force a lower quality tier.</summary>
        /// <param name="requestedFps">Positive requested frames per second.</param>
        /// <param name="refreshHz">Positive finite current display refresh in Hz.</param>
        /// <returns>Expected display-divisor FPS; 0 for invalid values. This calculation is not a measured FPS result.</returns>
        public static double ResolveMobileCadence(int requestedFps, double refreshHz)
        {
            if (requestedFps <= 0 || double.IsNaN(refreshHz) || double.IsInfinity(refreshHz) || refreshHz <= 0) return 0;
            return refreshHz / Math.Max(1, Math.Ceiling(refreshHz / requestedFps - 0.001));
        }
    }
}
