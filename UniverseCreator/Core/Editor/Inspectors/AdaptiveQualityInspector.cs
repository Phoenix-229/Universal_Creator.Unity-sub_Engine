// UC-T04-11: FPS and reference-capture controls use serialized edits and the existing quality component.
using UnityEditor;
using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>Authoring controls for the shared FPS/quality owner and bounded device evidence; exports only when the user presses Copy.</summary>
    [CustomEditor(typeof(AdaptiveQuality))]
    public sealed class AdaptiveQualityInspector : UnityEditor.Editor
    {
        /// <summary>Draws saved FPS choices, custom-budget validation, quality settings and explicit Play-mode capture controls.</summary>
        public override void OnInspectorGUI()
        {
            var quality = (AdaptiveQuality)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("frameRateMode"), new GUIContent("FPS Mode"));
            using (new EditorGUI.DisabledScope(serializedObject.FindProperty("frameRateMode").intValue != 0))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("targetFrameTime"), new GUIContent("Frame Budget (ms)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("controlFrameRate"), new GUIContent("Control Frame Pacing"));
            DrawPropertiesExcluding(serializedObject, "m_Script", "frameRateMode", "targetFrameTime", "controlFrameRate");
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Capture profiles the selected Universe Creator PCG world. Total frame timings also include your host scene/game. Keep content revision, world settings and camera path matched; 30 FPS is a proposed reference until measured on a device. Display refresh can limit cadence.", MessageType.Info); // UC-T09-01: subengine evidence does not certify whole-game performance.
            if (Application.isPlaying && quality.controlFrameRate && quality.frameRateMode != AdaptiveQuality.FrameRateMode.Custom && !quality.IsFrameRateOwner)
                EditorGUILayout.HelpBox("Another active quality component owns frame pacing, or this component is disabled.", MessageType.Warning);
            if (Application.isPlaying)
                EditorGUILayout.LabelField("Quality / active budget", quality.CurrentTier + " / " + quality.ActiveFrameBudgetMs.ToString("F2") + " ms");
            if (Application.isPlaying && quality.adaptToDeviceMeasurements)
                EditorGUILayout.HelpBox(quality.DeviceAdaptationStatus, MessageType.Info); // UC-T07-11: missing metrics and quality floors are visible without model-name assumptions.
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !quality.isActiveAndEnabled))
            {
                if (GUILayout.Button(quality.IsCapturingPerformance ? "Restart Timing Capture" : "Start Timing Capture")) quality.BeginPerformanceCapture();
                if (quality.IsCapturingPerformance && GUILayout.Button("Stop Timing Capture")) quality.EndPerformanceCapture();
            }
            EditorGUILayout.LabelField("Retained post-warm-up frames", quality.CapturedWindowFrames.ToString());
            using (new EditorGUI.DisabledScope(quality.CapturedWindowFrames == 0))
                if (GUILayout.Button("Copy Timing Report JSON")) EditorGUIUtility.systemCopyBuffer = quality.GetPerformanceReportJson();
        }
    }
}
