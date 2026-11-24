using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>UC-T02-09: opt-in timing/cache/memory diagnostics for a selected world; exports bounded reports as JSON.</summary>
    public sealed class UCGenerationMetricsWindow : EditorWindow
    {
        private UniverseCreatorObject world;
        private UCGenerationMetrics.Report[] reports = Array.Empty<UCGenerationMetrics.Report>();
        private Vector2 scroll;
        private double nextRefresh;
        private string scenario = "Cold";

        [MenuItem("Window/Universe Creator/Generation Metrics")]
        private static void Open() => GetWindow<UCGenerationMetricsWindow>("Generation Metrics");

        private void OnGUI()
        {
            world = (UniverseCreatorObject)EditorGUILayout.ObjectField("World", world, typeof(UniverseCreatorObject), true);
            if (world == null) { EditorGUILayout.HelpBox("Select a world to measure generation. Capture matched seed, graph and quality settings for comparisons.", MessageType.Info); return; }
            UCGenerationMetrics metrics = world.GenerationScheduler.Metrics;
            metrics.Enabled = EditorGUILayout.Toggle("Record measurements", metrics.Enabled);
            scenario = EditorGUILayout.TextField("Next run label", scenario);
            metrics.Scenario = scenario;
            EditorGUILayout.HelpBox("Label cold, warm, edit and cancellation runs before generating. Percentiles use the last 256 samples; memory is admitted payload, not the full Unity process.", MessageType.None);
            if (EditorApplication.timeSinceStartup >= nextRefresh)
            { reports = metrics.Snapshot(); nextRefresh = EditorApplication.timeSinceStartup + 1; }
            if (GUILayout.Button("Copy reports as JSON")) EditorGUIUtility.systemCopyBuffer = JsonUtility.ToJson(new Export { reports = metrics.Snapshot() }, true);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = reports.Length - 1; i >= 0; i--)
            {
                UCGenerationMetrics.Report report = reports[i];
                EditorGUILayout.LabelField(report.scenario + " — " + report.run, EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(report.profile, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(32));
                EditorGUILayout.LabelField("Elapsed / workers / cancelled / failed", report.elapsedMilliseconds.ToString("F1") + " ms / " + report.workers + " / " + report.cancelled + " / " + report.failed);
                long pulls = report.computes + report.localHits + report.retainedHits;
                EditorGUILayout.LabelField("Retained hit rate / computes / solves", (pulls == 0 ? 0 : report.retainedHits * 100.0 / pulls).ToString("F1") + "% / " + report.computes + " / " + report.drainageSolves);
                ShowTiming("Queue", report.queue); ShowTiming("Worker", report.generation); ShowTiming("Commit", report.commit); ShowTiming("Pump", report.pump);
                for (int stage = 0; stage < report.stages.Length; stage++) if (report.stages[stage].samples > 0) ShowTiming(((ChunkCommitStage)stage).ToString(), report.stages[stage]);
                EditorGUILayout.LabelField("Worker allocations", report.workerAllocatedBytes < 0 ? "Unavailable" : EditorUtility.FormatBytes(report.workerAllocatedBytes));
                EditorGUILayout.LabelField("Observed / world peak payload", EditorUtility.FormatBytes(report.observedResidentBytes) + " / " + EditorUtility.FormatBytes(report.worldPeakBytes));
                EditorGUILayout.LabelField("Uploaded", EditorUtility.FormatBytes(report.uploadBytes));
                EditorGUILayout.Space();
            }
            EditorGUILayout.EndScrollView();
        }

        private static void ShowTiming(string label, UCGenerationMetrics.Timing timing) => EditorGUILayout.LabelField(label + " p50 / p95 / p99",
            timing.p50.ToString("F2") + " / " + timing.p95.ToString("F2") + " / " + timing.p99.ToString("F2") + " ms (" + timing.samples + ")");
        [Serializable] private sealed class Export { public UCGenerationMetrics.Report[] reports; }
    }
}
