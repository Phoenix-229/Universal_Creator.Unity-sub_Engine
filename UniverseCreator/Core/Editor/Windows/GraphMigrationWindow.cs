// UC-T05-09: exact migration preview is an explicit owner action; opening this window changes no assets.
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Windows
{
    internal sealed class GraphMigrationWindow : EditorWindow
    {
        private Graph source;
        private UniverseCreatorObject world;
        private GraphMigrationPlan plan;
        private Vector2 scroll;

        [MenuItem("Tools/Universe Creator/Graphs/Analyze Legacy Graph Migration")]
        internal static void Open() => GetWindow<GraphMigrationWindow>("Graph Migration");

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            world = (UniverseCreatorObject)EditorGUILayout.ObjectField("World", world, typeof(UniverseCreatorObject), true);
            source = (Graph)EditorGUILayout.ObjectField("Legacy main graph", source, typeof(Graph), false);
            if (EditorGUI.EndChangeCheck()) plan = null;
            if (GUILayout.Button("Analyze without changing assets")) plan = GraphMigrationPlan.Analyze(source, world);
            if (plan == null) return;
            EditorGUILayout.HelpBox(plan.CanApply ? "This partition preserves settings and complete consumer chains. Review the replacement and reference list below." :
                "Resolve the reported unsafe boundaries before applying this migration; original assets and references are unchanged.",
                plan.CanApply ? MessageType.Info : MessageType.Warning);
            using (new EditorGUI.DisabledScope(!plan.CanApply))
                if (GUILayout.Button("Create replacement assets and switch references with Undo"))
                {
                    try
                    {
                        var result = GraphMigrationExecutor.Apply(plan, "Assets/UniverseCreator"); // UC-T05-10: new unique folder, original legacy asset retained.
                        ShowNotification(new GUIContent("Migration saved in " + result.Folder + ". Run each graph explicitly to apply its output."));
                        plan = null;
                    }
                    catch (System.Exception error) { ShowNotification(new GUIContent(error.Message)); }
                }
            if (plan == null) return;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var error in plan.Errors) EditorGUILayout.HelpBox(error, MessageType.Error);
            foreach (var detail in plan.Details) EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }
    }
}
