// UC-T05-01: expose saved domain provenance without silently migrating authored graphs.
using UnityEditor;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>Shows read-only graph-domain metadata and retains Unity's existing authored node/wire inspector.</summary>
    [CustomEditor(typeof(Graph))]
    public sealed class UCGraphAssetInspector : UnityEditor.Editor
    {
        /// <summary>Draws the saved domain/schema and supported or recovery state without rewriting metadata.</summary>
        public override void OnInspectorGUI()
        {
            var graph = (Graph)target;
            EditorGUILayout.LabelField("Graph Domain", graph.Domain.ToString());
            EditorGUILayout.LabelField("Domain Schema", graph.DomainSchemaVersion.ToString());
            if (!graph.HasSupportedDomainMetadata) EditorGUILayout.HelpBox(graph.DomainDiagnostic, MessageType.Error);
            else if (graph.Domain == UCGraphDomain.LegacyMixed)
                EditorGUILayout.HelpBox("Legacy mixed graph. Its original nodes and connections remain unchanged until explicit migration.", MessageType.Info);
            DrawDefaultInspector(); // UC-T05-01: preserve the existing SerializeReference fields, Undo and saved values.
        }
    }
}
