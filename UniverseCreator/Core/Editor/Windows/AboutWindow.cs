// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    AboutWindow.cs
//  Purpose: Version, module status, and documentation entry point.
//
//  Counts are computed by reflection rather than hard-coded, so adding a node
//  cannot leave a stale number on screen.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.Inspectors;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>
    /// Shows toolkit version, which modules are present, and a button that opens
    /// the documentation markdown.
    /// </summary>
    public class AboutWindow : EditorWindow
    {
        public const string Version = "1.0.0";
        public const string DocumentationPath = "Assets/UniverseCreator/UniverseCreator_Documentation.md";

        // Resolved once on open: counting types walks every loaded assembly, which
        // is far too slow to repeat per OnGUI repaint.
        private int nodeCount = -1;

        public static void OpenWindow()
        {
            AboutWindow window = GetWindow<AboutWindow>(true, "About Universe Creator");
            window.minSize = new Vector2(420f, 340f);
            window.maxSize = new Vector2(620f, 560f);
            window.ShowUtility();
        }

        private void OnEnable()
        {
            nodeCount = CountNodeTypes();
        }

        private void OnGUI()
        {
            UCInspectorUI.HeaderBar("Universe Creator", "d_UnityLogo",
                                    UCInspectorUI.HeaderPurple, "v" + Version);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Procedural Content Generation Toolkit",
                                       EditorStyles.miniLabel);

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Version", Version);
            EditorGUILayout.LabelField("Unity", Application.unityVersion);
            EditorGUILayout.LabelField("Node types", nodeCount >= 0 ? nodeCount.ToString() : "-");

            EditorGUILayout.Space(12);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Modules", EditorStyles.boldLabel);
                DrawModule("Terrain Core", true);
                DrawModule("Texturing & Materials", true);
                DrawModule("Vegetation & Props", true);
                DrawModule("Weather & Atmosphere", true);
                DrawModule("Lighting", true);
                DrawModule("Geometry & Streaming", true);
                DrawModule("Road & City Master Bridge", false);
            }

            EditorGUILayout.Space(14);

            if (UCInspectorUI.TintedButton("Open Documentation", UCInspectorUI.ButtonBlue))
            {
                UnityEngine.Object doc =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(DocumentationPath);

                if (doc != null) AssetDatabase.OpenAsset(doc);
                else Debug.LogWarning("Universe Creator: documentation not found at " + DocumentationPath);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(
                "Terrain Core is complete. Later modules consume its graph engine and bridge contract.",
                EditorStyles.wordWrappedMiniLabel);
        }

        // The status used to be a GUI.color-tinted label. Tinting label text is the
        // one thing that does not survive a skin change - a light-green word on the
        // light skin is close to invisible. A filled badge carries the colour in its
        // background instead, so the text stays at full contrast on both skins.
        private static void DrawModule(string label, bool available)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label);
            UCInspectorUI.Badge(available ? "Installed" : "Not installed",
                                available ? UCInspectorUI.HeaderGreen : UCInspectorUI.HeaderGrey,
                                100f);
            EditorGUILayout.EndHorizontal();
        }

        // Mirrors NodeCatalog's discovery rule: concrete Generator subclasses only,
        // so abstract bases are not counted as usable nodes.
        private static int CountNodeTypes()
        {
            int count = 0;
            System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException)
                {
                    continue;   // a partially loaded assembly must not break the window
                }

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                    if (typeof(Generator).IsAssignableFrom(type)) count++;
                }
            }

            return count;
        }
    }
}
