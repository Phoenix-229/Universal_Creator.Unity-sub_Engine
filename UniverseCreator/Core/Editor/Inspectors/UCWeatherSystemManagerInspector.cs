// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWeatherSystemManagerInspector.cs
//  Purpose: Draws the weather manager, separating the part that works from the
//           part that does not.
//
//  WHY THE SPLIT IS DRAWN SO LOUDLY
//  --------------------------------
//  Wind is live. Precipitation, cloud, fog, storms and season are declared and
//  read by nothing. An inspector that presented all six identically would be
//  telling the user that dragging the rain slider does something, and the twenty
//  minutes they then spend working out why it does not is the cost.
//
//  So the placeholder sections are collapsed by default, drawn disabled, and say
//  what they are. The wind section is open, live, and links straight to the
//  component that actually does the work.
//
//  NO DIALOGS.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(UCWeatherSystemManager))]
    public class UCWeatherSystemManagerInspector : UnityEditor.Editor
    {
        private static bool windFoldout = true;
        private static bool plannedFoldout;

        private UnityEditor.Editor windEditor;

        private UCWeatherSystemManager Manager { get { return (UCWeatherSystemManager)target; } }

        private void OnDisable()
        {
            // The nested editor is a real Object; leaking one per selection change
            // shows up as a slow drip of leaked editors in the profiler.
            if (windEditor != null) DestroyImmediate(windEditor);
            windEditor = null;
        }

        public override void OnInspectorGUI()
        {
            UCWeatherSystemManager manager = Manager;
            if (manager == null) return;

            UCInspectorUI.HeaderBar("Weather System", "d_CloudConnect",
                                    UCInspectorUI.HeaderTeal, StatusText(manager));

            EditorGUILayout.Space(4);

            serializedObject.Update();

            windFoldout = Group(windFoldout, "Wind  (live)", UCInspectorUI.HeaderGreen);
            if (windFoldout) DrawWind(manager);

            EditorGUILayout.Space(4);

            plannedFoldout = Group(plannedFoldout, "Planned  (not implemented)",
                                   UCInspectorUI.HeaderGrey);
            if (plannedFoldout) DrawPlanned();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawWind(UCWeatherSystemManager manager)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("windEnabled"));

            UCWindManager wind = manager.WindOrNull;

            if (wind == null)
            {
                EditorGUILayout.HelpBox(
                    "No wind manager yet. It is created the moment wind is needed - " +
                    "either by enabling it here, or by the first chunk whose graph has " +
                    "a Wind Output node.", MessageType.Info);

                if (ActionButton("Create Wind Manager", UCInspectorUI.ButtonGreen))
                {
                    Undo.RegisterCompleteObjectUndo(manager.gameObject, "Create wind manager");
                    manager.EnsureWind();
                    EditorUtility.SetDirty(manager.gameObject);
                }
                return;
            }

            if (wind.gameObject != manager.gameObject)
                EditorGUILayout.HelpBox(
                    "The wind manager lives on '" + wind.gameObject.name + "' rather than " +
                    "here, because this world was built before the weather system existed. " +
                    "It still works and it is still edited from here; moving it would " +
                    "discard the sway map the graph has already baked into it.",
                    MessageType.Info);

            if (!manager.windEnabled)
                EditorGUILayout.HelpBox(
                    "Wind is off, so grass, trees and water are still. The settings below " +
                    "are kept - turning wind back on restores them.", MessageType.None);

            EditorGUILayout.Space(4);

            // The wind manager's own inspector, drawn inline. Better than a list of
            // proxy fields: whatever the wind system gains later shows up here with
            // no change to this file, and there is exactly one place the values live.
            CreateCachedEditor(wind, null, ref windEditor);
            if (windEditor != null)
            {
                using (new EditorGUI.DisabledScope(!manager.windEnabled))
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    windEditor.OnInspectorGUI();
                    EditorGUILayout.EndVertical();
                }
            }
        }

        private void DrawPlanned()
        {
            EditorGUILayout.HelpBox(
                "These describe the weather module that is not written yet. They are " +
                "saved on the component and read by nothing, so changing them has no " +
                "effect on the scene. They are here so the module inherits the names, " +
                "units and ranges the rest of the system already assumes.",
                MessageType.Warning);

            // Disabled rather than hidden. Hiding them would leave a later reader
            // wondering whether weather exists at all; showing them greyed says
            // "planned, and this is the shape it will take".
            using (new EditorGUI.DisabledScope(true))
            {
                DrawPropertiesExcluding(serializedObject, "m_Script", "windEnabled", "wind");
            }
        }

        private static string StatusText(UCWeatherSystemManager manager)
        {
            if (!manager.windEnabled) return "wind off";
            return manager.WindOrNull != null ? "wind on" : "no wind yet";
        }

        private static bool Group(bool state, string title, Color accent)
        {
            bool before = GUI.changed;
            bool next = UCInspectorUI.Section(state, title, accent);
            GUI.changed = before;
            return next;
        }

        private static bool ActionButton(string label, Color tint, float height = 22f)
        {
            bool before = GUI.changed;
            bool clicked = UCInspectorUI.TintedButton(label, tint, height);
            if (clicked) GUI.changed = before;
            return clicked;
        }
    }
}
