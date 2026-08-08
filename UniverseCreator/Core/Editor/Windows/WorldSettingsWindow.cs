// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    WorldSettingsWindow.cs
//  Purpose: Global Universe Creator settings window.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Inspectors;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>
    /// Global Universe Creator settings window.
    /// </summary>
    /// <remarks>
    /// SKELETON (Module 1, Step 1). Implemented in Module 1, Step 6.
    /// </remarks>
    public class WorldSettingsWindow : EditorWindow
    {
        // TODO (Module 1, Step 6): Project-wide defaults and template selection.

        public static void OpenWindow()
        {
            WorldSettingsWindow window = GetWindow<WorldSettingsWindow>("Universe Creator - World Settings");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnGUI()
        {
            UCInspectorUI.HeaderBar("World Settings", "d_SettingsIcon",
                                    UCInspectorUI.HeaderBlue, "not yet implemented");

            EditorGUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "World-wide generation settings, LOD configuration, performance tuning, and " +
                "debug visualisation will live here.\n\n" +
                "Until then, per-world values are on the Universe Creator World component and " +
                "project-wide defaults are in Project Settings > Universe Creator.",
                MessageType.Info
            );

            EditorGUILayout.Space(8);

            // Both destinations already exist, so the placeholder points at them
            // rather than leaving the window a dead end.
            if (UCInspectorUI.TintedButton("Open Project Settings", UCInspectorUI.ButtonBlue))
                SettingsService.OpenProjectSettings("Project/Universe Creator");

            if (UCInspectorUI.TintedButton("Select World In Scene", UCInspectorUI.ButtonGreen))
            {
                // FindAnyObjectByType, not FindFirstObjectByType: the latter is obsolete
                // because it depends on instance-ID ordering, and "any world" is all this
                // needs - a scene with two worlds has no defined "first" anyway.
                UniverseCreatorObject world =
                    Object.FindAnyObjectByType<UniverseCreatorObject>();

                if (world != null)
                {
                    Selection.activeGameObject = world.gameObject;
                    EditorGUIUtility.PingObject(world.gameObject);
                }
                else
                {
                    Debug.LogWarning("Universe Creator: no world in the open scene. " +
                                     "GameObject > 3D Object > Universe Creator > Create New World.");
                }
            }
        }
    }
}
