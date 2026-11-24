// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    AdaptiveQuality.cs
//  Purpose: Scales generation and render quality to hold a target frame rate on mobile.
//
//  Module 1 establishes the hooks. Modules 2-4 (vegetation, props, weather) consume
//  the quality tier to scale their own density and effect complexity.
//
//  GOVERNOR GO-LIVE (GEOM-WIRE2, opt-in). The simple averaging evaluator below is the
//  shipped default and is left exactly as it was. When enableGovernors is turned on, this
//  component instead drives the § 9.10 governor set as ONE owner: a rolling-median
//  GeometryBudget decides over/under budget, a monotonic DegradationLadder steps the view
//  down one rung at a time, a RecoveryController steps it back up with hysteresis, a
//  ThermalGovernor adds a complexity-drift pressure, and GovernorStatus publishes the
//  advisory line. The ladder depth then nudges the recommended tier every module already
//  reads, so the governor decision is genuinely consumed - it is not a second, idle path.
//  A pinned tier defeats the auto-reduction (G-66). Default-off reproduces prior behaviour.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Optimization
{
    /// <summary>
    /// Samples frame time and recommends a quality tier so vegetation, props, and
    /// effects can scale down on constrained hardware.
    /// </summary>
    public partial class AdaptiveQuality : MonoBehaviour
    {
        public enum QualityTier
        {
            Ultra = 0,
            High = 1,
            Medium = 2,
            Low = 3
        }

        [Tooltip("Custom frame budget in milliseconds. Presets select 33.33ms (30fps), 16.67ms (60fps) or 8.33ms (120fps).")]
        public float targetFrameTime = 16.67f;

        [Tooltip("How many frames to average before adjusting quality.")]
        public int sampleWindow = 60;

        [Tooltip("Opt-in (GEOM-WIRE2). OFF (default) keeps the simple averaging evaluator - the " +
                 "shipped behaviour, bit-identical. ON drives the §9.10 governor set (rolling-median " +
                 "budget, degradation ladder, recovery hysteresis, thermal drift) as one owner, and " +
                 "the ladder depth nudges the recommended tier modules already read.")]
        public bool enableGovernors = false;

        [Tooltip("Governor seconds between one degradation or recovery step (G-63/G-64).")]
        public float degradeIntervalSeconds = 1.0f;

        [Tooltip("Author pin that defeats the governor's auto-reduction (G-66): when on, the tier " +
                 "stays at pinnedTier regardless of what the governor would do.")]
        public bool pinTier = false;
        public QualityTier pinnedTier = QualityTier.High;

        private float[] frameTimes;
        private int frameIndex;
        private QualityTier currentTier = QualityTier.High;

        // Governor state (only live when enableGovernors). One owner, created in Start.
        private GeometryBudget _budget;
        private DegradationLadder _ladder;
        private RecoveryController _recovery;
        private ThermalGovernor _thermal;
        private float _complexityUnits;   // last frame's post-cull instanced workload (thermal bucket)
        private float _complexityDraws;   // last frame's instanced draw-call count (thermal bucket)
        private float _lastStepTime;
        private ReductionReason _reason = ReductionReason.None;
        private bool _autoReduced;
        private string _statusLine = "Tier: High";
        private QualityTier _statusTier, _statusBaseTier;
        private ReductionReason _statusReason;
        private bool _statusReduced, _statusValid;

        // The concrete view-side knobs the ladder produces (G-67): acuity, overlay ring,
        // impostor/shadow distances, cache range, overlay stages. Rebuilt from the ladder level
        // each evaluation via ViewSideDegradation.Apply, so the ladder is not just a tier nudge -
        // it yields the actual knobs the overlay/impostor/cache consumers read. Default (and the
        // whole state when governors are off) is the undegraded set, so consumers see no change.
        private ViewSideState _viewState = ViewSideState.Default;

        /// <summary>Current recommended tier. Modules 2-4 read this to scale their work.</summary>
        public QualityTier CurrentTier { get { return currentTier; } }

        /// <summary>The governor's advisory status line (G-66). Empty until the governor runs.</summary>
        public string GovernorStatusLine { get { return _statusLine; } }

        /// <summary>UC-T07-11: active view degradation depth; measured mode uses the current tier, legacy governor mode uses its ladder.</summary>
        public int DegradeLevel { get { return adaptToDeviceMeasurements ? (pinTier ? 0 : Mathf.Max(0, (int)currentTier - 1)) : enableGovernors && _ladder != null ? _ladder.Level : 0; } }

        /// <summary>The concrete view-side knobs the governor's ladder currently dictates (G-67).
        /// The undegraded <see cref="ViewSideState.Default"/> when governors are off, so consumers
        /// read it unconditionally and see no change until the author opts in and the budget bites.</summary>
        public ViewSideState CurrentViewState { get { return _viewState; } }

        private void Start()
        {
            EnsureConfiguration(); // UC-T04-11: clamp loaded/scripted controls before allocating or taking a modulo.
        }

        // Build the governor set from the authored budget. Only frame time has a cheap runtime
        // source, so it is the active ceiling; the triangle/draw/memory ceilings are left
        // effectively open (they tighten once a cost source feeds Sample those metrics).
        private void InitGovernors()
        {
            int window = Mathf.Max(1, sampleWindow);
            _budget = new GeometryBudget(window, ActiveFrameBudgetMs,
                float.MaxValue, float.MaxValue, float.MaxValue);
            _ladder = new DegradationLadder();
            _recovery = new RecoveryController();
            _thermal = new ThermalGovernor();
            _lastStepTime = 0f;
            _reason = ReductionReason.None;
            _autoReduced = false;
        }

        private void Update()
        {
            EnsureConfiguration(); // UC-T04-11: mode/window edits reset only their bounded sampling state.

            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (float.IsNaN(frameMs) || float.IsInfinity(frameMs) || frameMs <= 0f) return;
            SamplePerformance(frameMs);
            if (adaptToDeviceMeasurements) { UpdateDeviceAdaptation(frameMs); return; } // UC-T07-11: exactly one quality response path consumes the current device's available measurements.
            frameTimes[frameIndex] = frameMs;
            frameIndex = (frameIndex + 1) % sampleWindow;

            if (enableGovernors)
            {
                if (_budget == null) InitGovernors();
                // Last frame's drawn-geometry workload (GPUInstancingManager publishes it in
                // LateUpdate, after this Update). The thermal governor buckets its frame-time
                // baselines by this, so a frame-time rise at a HEAVIER scene is no longer mistaken
                // for SoC throttling - the matched-complexity requirement the governor was built on.
                _complexityUnits = GeometryComplexity.InstancedUnits;
                _complexityDraws = GeometryComplexity.DrawCalls;
                _budget.Sample(frameMs, _complexityUnits, _complexityDraws, 0f);   // tri/draw ceilings stay MaxValue: the feed informs the thermal bucket, not the budget trigger
                EvaluateGovernors();
            }
            else if (frameIndex == 0)
            {
                EvaluateTier();
                if (pinTier) currentTier = pinnedTier; // UC-T04-11: a deliberate reference-quality pin also applies without governors.
            }
        }

        // The shipped simple evaluator - unchanged, and the only path when governors are off.
        private void EvaluateTier()
        {
            float avg = 0f;
            for (int i = 0; i < frameTimes.Length; i++) avg += frameTimes[i];
            avg /= frameTimes.Length;

            if (avg > ActiveFrameBudgetMs * 1.5f) currentTier = QualityTier.Low;
            else if (avg > ActiveFrameBudgetMs * 1.2f) currentTier = QualityTier.Medium;
            else if (avg > ActiveFrameBudgetMs * 1.05f) currentTier = QualityTier.High;
            else currentTier = QualityTier.Ultra;
        }

        // The governor loop (one owner): median budget -> ladder step down, recovery hold ->
        // ladder step up, thermal drift -> an extra step down. The base tier is the simple
        // evaluator's; the ladder depth then reduces it, and a pin defeats the reduction.
        private void EvaluateGovernors()
        {
            // Base tier from the same thresholds the simple path uses, on the median frame time
            // so one spike cannot move it (the whole point of the rolling median).
            float medianMs = _budget.MedianFrameMs;
            QualityTier baseTier =
                medianMs > ActiveFrameBudgetMs * 1.5f ? QualityTier.Low
                : medianMs > ActiveFrameBudgetMs * 1.2f ? QualityTier.Medium
                : medianMs > ActiveFrameBudgetMs * 1.05f ? QualityTier.High
                : QualityTier.Ultra;

            float now = Time.unscaledTime;
            bool intervalElapsed = DegradationLadder.IntervalElapsed(_lastStepTime, now, degradeIntervalSeconds);
            float dt = Time.unscaledDeltaTime;

            _thermal.Observe(_complexityUnits, _complexityDraws, medianMs);   // bucketed by real drawn workload (GeometryComplexity)

            if (intervalElapsed && _budget.IsOverBudget() && !_ladder.AtBottom)
            {
                if (_ladder.TryStepDown(out _)) { _reason = ReductionReason.Budget; _recovery.Reset(); _lastStepTime = now; }
            }
            else if (intervalElapsed && _thermal.ShouldStepDown(_complexityUnits, _complexityDraws, medianMs) && !_ladder.AtBottom)
            {
                if (_ladder.TryStepDown(out _)) { _reason = ReductionReason.ThermalDrift; _thermal.MarkSteppedDown(); _recovery.Reset(); _lastStepTime = now; }
            }
            else if (_recovery.Observe(dt, medianMs, ActiveFrameBudgetMs) && intervalElapsed && !_ladder.AtTop)
            {
                if (_ladder.TryStepUp(out _)) { _lastStepTime = now; if (_ladder.AtTop) _reason = ReductionReason.None; }
            }

            // The ladder depth nudges the tier down, so the governor decision is actually
            // consumed by every module that reads CurrentTier. Deeper rungs cost more tiers.
            int lvl = _ladder.Level;
            int tierDown = lvl >= 5 ? 3 : lvl >= 3 ? 2 : lvl >= 1 ? 1 : 0;
            QualityTier autoTier = (QualityTier)Mathf.Clamp((int)baseTier + tierDown, (int)QualityTier.Ultra, (int)QualityTier.Low);

            _autoReduced = autoTier != baseTier;
            currentTier = pinTier ? pinnedTier : autoTier;

            // Turn the ladder depth into the concrete view-side knobs (G-67). Rebuilt from Default
            // each time, applying steps 0..level-1 in order, so it is deterministic and a step-up
            // reverses cleanly (ViewSideDegradation.Apply only ever descends, so we rebuild).
            ViewSideState vs = ViewSideState.Default;
            int depth = pinTier ? 0 : _ladder.Level;
            for (int i = 0; i < depth; i++) vs = ViewSideDegradation.Apply(vs, (DegradeStep)i);
            _viewState = vs;

            QualityTier statusBase = pinTier ? pinnedTier : baseTier;
            bool reduced = _autoReduced && !pinTier;
            // UC-T04-11: unchanged diagnostics reuse their string instead of allocating on every governor frame.
            if (!_statusValid || _statusTier != currentTier || _statusBaseTier != statusBase || _statusReason != _reason || _statusReduced != reduced)
            {
                _statusLine = GovernorStatus.Format(ToCacheTier(currentTier), ToCacheTier(statusBase), reduced, _reason, 0f);
                _statusTier = currentTier; _statusBaseTier = statusBase; _statusReason = _reason; _statusReduced = reduced; _statusValid = true;
            }
        }

        // AdaptiveQuality's four-tier scale mapped onto the status readout's tier identity.
        private static SplatCacheTier ToCacheTier(QualityTier t)
        {
            switch (t)
            {
                case QualityTier.Ultra:  return SplatCacheTier.Ultra;
                case QualityTier.High:   return SplatCacheTier.Desktop;
                case QualityTier.Medium: return SplatCacheTier.MobileHigh;
                default:                 return SplatCacheTier.MobileLow;
            }
        }
    }
}
