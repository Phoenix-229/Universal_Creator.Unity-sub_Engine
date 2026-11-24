// UC-T04-11: actual Play-mode native disable and application-wide FPS ownership, not simulated Editor callbacks.
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private const string FramePlayPending = "UC.T04.FramePerformance.PlayPending";

        /// <summary>Enters real Play mode in the isolated validation project, checks FPS ownership/capture teardown, then exits zero on success or one on failure.</summary>
        /// <remarks>SessionState resumes after the actual domain reload. This gate does not measure rendered/device FPS or visual water quality.</remarks>
        public static void RunFramePerformancePlayBatch()
        {
            SessionState.SetInt(FramePlayPending, 1);
            ResumeFramePerformancePlay();
            if (!EditorApplication.isPlaying) EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void ResumeFramePerformancePlay()
        {
            if (SessionState.GetInt(FramePlayPending, 0) != 1) return;
            EditorApplication.playModeStateChanged -= OnFramePerformancePlayState;
            EditorApplication.playModeStateChanged += OnFramePerformancePlayState;
            if (EditorApplication.isPlaying) EditorApplication.delayCall += RunFramePerformancePlayAcceptance;
        }

        private static void OnFramePerformancePlayState(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) RunFramePerformancePlayAcceptance();
        }

        private static void RunFramePerformancePlayAcceptance()
        {
            if (SessionState.GetInt(FramePlayPending, 0) != 1) return;
            SessionState.SetInt(FramePlayPending, 2);
            checks = 0; failures.Clear();
            int previousTarget = Application.targetFrameRate, previousSync = QualitySettings.vSyncCount;
            var first = new GameObject("UC-T04-11 Play FPS owner");
            var second = new GameObject("UC-T04-11 Play competing owner");
            try
            {
                Check(EditorApplication.isPlaying, "UC-T04-11 FPS native lifecycle acceptance actually runs in Play mode.");
                var a = first.AddComponent<AdaptiveQuality>();
                var b = second.AddComponent<AdaptiveQuality>();
                var configureA = (Action)Delegate.CreateDelegate(typeof(Action), a,
                    typeof(AdaptiveQuality).GetMethod("EnsureConfiguration", BindingFlags.Instance | BindingFlags.NonPublic));
                var configureB = (Action)Delegate.CreateDelegate(typeof(Action), b,
                    typeof(AdaptiveQuality).GetMethod("EnsureConfiguration", BindingFlags.Instance | BindingFlags.NonPublic));
                a.controlFrameRate = true; a.frameRateMode = AdaptiveQuality.FrameRateMode.Fps30;
                a.pinTier = true; a.pinnedTier = AdaptiveQuality.QualityTier.High; configureA();
                Check(a.IsFrameRateOwner && Application.targetFrameRate == 30 && a.CurrentTier == AdaptiveQuality.QualityTier.High,
                    "UC-T04-11 first Play owner applies 30 FPS and the explicit high-detail quality pin.");
                b.controlFrameRate = true; b.frameRateMode = AdaptiveQuality.FrameRateMode.Fps60; configureB();
                Check(!b.IsFrameRateOwner && Application.targetFrameRate == 30,
                    "UC-T04-11 competing world cannot overwrite an active application's frame-pacing owner.");
                foreach (var mode in new[] { AdaptiveQuality.FrameRateMode.Fps60, AdaptiveQuality.FrameRateMode.Fps120, AdaptiveQuality.FrameRateMode.Fps30 })
                {
                    a.frameRateMode = mode; configureA();
                    // Float frame budgets round at storage; the comparison permits 0.1 microsecond of arithmetic error only.
                    Check(Application.targetFrameRate == (int)mode && a.ActiveFrameBudgetMs + 0.0001f >= 1000f / (int)mode,
                        "UC-T04-11 native Play " + mode + " cap and known display envelope remain coherent. target=" + Application.targetFrameRate +
                        ", budgetMs=" + a.ActiveFrameBudgetMs.ToString("R") + ", requestedMs=" + (1000f / (int)mode).ToString("R"));
                }
                a.captureWarmupSeconds = 0; a.BeginPerformanceCapture(); a.enabled = false;
                Check(!a.IsCapturingPerformance && !a.IsFrameRateOwner && Application.targetFrameRate == previousTarget && QualitySettings.vSyncCount == previousSync,
                    "UC-T04-11 native Play disable stops capture and restores the prior presentation settings.");
                configureB();
                Check(b.IsFrameRateOwner && Application.targetFrameRate == 60,
                    "UC-T04-11 waiting Play owner acquires pacing only after the first owner retires.");
                b.enabled = false;
                a.enabled = true; configureA();
                Application.targetFrameRate = 77; QualitySettings.vSyncCount = 0; a.enabled = false;
                Check(Application.targetFrameRate == 77 && QualitySettings.vSyncCount == 0,
                    "UC-T04-11 native owner retirement preserves a newer external presentation setting.");
            }
            catch (Exception error) { Check(false, "UC-T04-11 native Play frame ownership exception: " + error); }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
                Application.targetFrameRate = previousTarget; QualitySettings.vSyncCount = previousSync;
                SessionState.EraseInt(FramePlayPending);
                EditorApplication.playModeStateChanged -= OnFramePerformancePlayState;
            }
            bool ok = failures.Count == 0; Debug.Log(Report(ok)); Console.WriteLine(Report(ok)); EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
