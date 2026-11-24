// UC-T05-03: parent world bindings expose validated, saved companion inputs and explicit working controls.
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>Edits versioned companion bindings without applying authored changes until the Run action.</summary>
    [CustomEditor(typeof(UCWorldGraphBindings))]
    public sealed class UCWorldGraphBindingsInspector : UnityEditor.Editor
    {
        /// <summary>Shows the authoritative main graph, vegetation binding, accepted revision and pending/error state.</summary>
        public override void OnInspectorGUI()
        {
            var bindings = (UCWorldGraphBindings)target;
            bindings.RefreshVegetationDependencies(); // UC-T05-03: expose stale terrain before another Run is attempted.
            EditorGUILayout.LabelField("Bindings Schema", bindings.SchemaVersion.ToString());
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Main Node Graph", bindings.Owner.graph, typeof(Graph), false);
            DrawVegetation(bindings); // UC-T06-15: manager and binding inspector share the same saved authoring controls.

            DrawLighting(bindings); // UC-T08-15.11: lighting manager uses this same binding owner and actions.
            DrawTexture(bindings);
        }

        /// <summary>Draws shared vegetation binding/status and explicit application controls without an implicit Run.</summary>
        /// <param name="bindings">Existing non-null world binding; authoring uses Undo and the shared graph controller.</param>
        internal static void DrawVegetation(UCWorldGraphBindings bindings)
        {
            bindings.RefreshVegetationDependencies();
            using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Shared UC World", bindings.Owner, typeof(UniverseCreatorObject), true);
            var selected = (Graph)EditorGUILayout.ObjectField(new GUIContent("Vegetation Graph", "Edits are pending until Run / Apply."),
                bindings.VegetationGraph, typeof(Graph), false);
            if (selected != bindings.VegetationGraph)
            {
                if (selected != null && (!selected.HasSupportedDomainMetadata || selected.Domain != UCGraphDomain.Vegetation))
                    EditorGUILayout.HelpBox("Select an explicit Vegetation Graph; legacy assets require a migration preview.", MessageType.Error);
                else
                {
                    UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.Bind(bindings, selected);
                }
            }
            EditorGUILayout.LabelField("Accepted Vegetation Revision", bindings.VegetationRevision.ToString());
            EditorGUILayout.HelpBox(bindings.VegetationStatus, MessageType.Info);
            if (UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.HasPendingChanges(bindings))
                EditorGUILayout.HelpBox("Current authoring needs Run / Apply. Accepted streaming keeps its captured settings; partial application updates only its selected regions.", MessageType.Info);
            if (GUILayout.Button("Open Vegetation Graph"))
            {
                var window = EditorWindow.GetWindow<UniverseCreator.Editor.Windows.VegetationGraphWindow>("Universe Creator - Vegetation Graph");
                window.SetGraph(bindings.VegetationGraph); window.SetWorld(bindings.Owner); window.Show();
            }
            using (new EditorGUI.DisabledScope(bindings.IsVegetationRunning))
                if (GUILayout.Button("Run / Apply Vegetation")) _ = UniverseCreator.Editor.Utilities.VegetationGraphAuthoring.Run(bindings);
            using (new EditorGUI.DisabledScope(!bindings.IsVegetationRunning))
                if (GUILayout.Button("Cancel Pending Run")) bindings.CancelVegetation();
        }

        /// <summary>Draws the world's saved Lighting Graph and explicit baseline/environment actions without an implicit Run.</summary>
        /// <param name="bindings">Non-null world binding; edits use Undo and the existing environment execution owner.</param>
        internal static void DrawLighting(UCWorldGraphBindings bindings)
        {
            EditorGUILayout.Space(); // UC-T05-04: saved environment binding and explicit baseline/dynamic stages share the parent inspector.
            var lighting = (Graph)EditorGUILayout.ObjectField(new GUIContent("Lighting Graph", "Climate, lighting and weather; edits require explicit Run / Apply."),
                bindings.LightingGraph, typeof(Graph), false);
            if (lighting != bindings.LightingGraph)
            {
                if (lighting != null && (!lighting.HasSupportedDomainMetadata || lighting.Domain != UCGraphDomain.LightingClimateWeather))
                    EditorGUILayout.HelpBox("Select an explicit Lighting Graph; retain mixed assets for migration.", MessageType.Error);
                else
                { Undo.RecordObject(bindings, "Bind Lighting Graph"); bindings.LightingGraph = lighting; EditorUtility.SetDirty(bindings); }
            }
            var sampleTarget = (Transform)EditorGUILayout.ObjectField(new GUIContent("Environment Sample Target", "Camera/player position for spatial fields; empty uses the world origin."),
                bindings.EnvironmentSampleTarget, typeof(Transform), true);
            if (sampleTarget != bindings.EnvironmentSampleTarget)
            { Undo.RecordObject(bindings, "Environment sample target"); bindings.EnvironmentSampleTarget = sampleTarget; EditorUtility.SetDirty(bindings); }
            EditorGUILayout.LabelField("Accepted Baseline Climate", bindings.BaselineClimateRevision.ToString());
            EditorGUILayout.LabelField("Accepted Dynamic Environment", bindings.LightingRevision.ToString());
            EditorGUILayout.HelpBox(bindings.LightingStatus, MessageType.Info);
            if (GUILayout.Button("Open Lighting Graph"))
            {
                var window = EditorWindow.GetWindow<UniverseCreator.Editor.Windows.LightingGraphWindow>("Universe Creator - Lighting Graph");
                window.SetGraph(bindings.LightingGraph); window.SetWorld(bindings.Owner); window.Show();
            }
            using (new EditorGUI.DisabledScope(bindings.IsLightingRunning))
            {
                if (GUILayout.Button("Run Baseline Climate")) _ = bindings.RunLightingAsync(true);
                if (GUILayout.Button("Run / Apply Lighting and Weather")) _ = bindings.RunLightingAsync();
            }
            using (new EditorGUI.DisabledScope(!bindings.IsLightingRunning))
                if (GUILayout.Button("Cancel Pending Environment")) bindings.CancelLighting();
        }

        private static void DrawTexture(UCWorldGraphBindings bindings)
        {
            EditorGUILayout.Space(); // UC-T05-06: all four saved references and the independent texture revision share this world inspector.
            var texture = (Graph)EditorGUILayout.ObjectField(new GUIContent("Texture Graph", "Apply through Texture Graph's target-terrain control; accepted terrain stays unchanged."),
                bindings.TextureGraph, typeof(Graph), false);
            if (texture != bindings.TextureGraph)
            {
                if (texture != null && (!texture.HasSupportedDomainMetadata || texture.Domain != UCGraphDomain.TextureMaterialShader))
                    EditorGUILayout.HelpBox("Select an explicit Texture Graph.", MessageType.Error);
                else
                { Undo.RecordObject(bindings, "Bind Texture Graph"); bindings.TextureGraph = texture; EditorUtility.SetDirty(bindings); }
            }
            EditorGUILayout.LabelField("Accepted Texture Revision", bindings.TextureRevision.ToString());
            if (GUILayout.Button("Open Texture Graph"))
            {
                var window = EditorWindow.GetWindow<UniverseCreator.Editor.Windows.TextureGraphWindow>("Universe Creator - Texture Graph");
                window.SetGraph(bindings.TextureGraph); window.SetWorld(bindings.Owner); window.Show();
            }
        }
    }
}
