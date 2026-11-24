// UC-T05-05: real window admission preserves saved assets while rejecting creation in the wrong domain.
using System.Linq;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Editor.Windows;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestDomainWindowAuthoring()
        {
            var mainWindow = ScriptableObject.CreateInstance<NodeGraphWindow>();
            var textureWindow = ScriptableObject.CreateInstance<TextureGraphWindow>();
            var graph = ScriptableObject.CreateInstance<Graph>();
            var view = new UCGraphView();
            try
            {
                var constant = NodeCatalog.All.First(e => e.type == typeof(ConstantNode));
                var height = NodeCatalog.All.First(e => e.type == typeof(HeightOutputNode));
                var surface = NodeCatalog.All.First(e => e.category == NodeCategory.Surface &&
                    !GraphDomainRules.Supports(e.graphDomains, UCGraphDomain.TerrainWater));
                MethodInfo mainAdmission = typeof(NodeGraphWindow).GetMethod("AcceptNode", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo textureAdmission = typeof(TextureGraphWindow).GetMethod("AcceptNode", BindingFlags.Instance | BindingFlags.NonPublic);
                graph.SetDomain(UCGraphDomain.TerrainWater); mainWindow.SetGraph(graph); textureWindow.SetGraph(graph);
                Check((bool)mainAdmission.Invoke(mainWindow, new object[] { height }) &&
                    !(bool)mainAdmission.Invoke(mainWindow, new object[] { surface }) &&
                    !(bool)textureAdmission.Invoke(textureWindow, new object[] { constant }),
                    "UC-T05-05 main authoring admits terrain and rejects surface/foreign-window creation by saved metadata.");
                view.Populate(graph); view.EntryFilter = entry => (bool)mainAdmission.Invoke(mainWindow, new object[] { entry });
                view.AddNodeAt(surface, Vector2.zero); view.AddNodeAt(constant, Vector2.one);
                Check(graph.nodes.Count == 1 && graph.nodes[0] is ConstantNode,
                    "UC-T05-05 direct main-canvas creation uses the real window filter and cannot add an appearance node.");
                graph.SetDomain(UCGraphDomain.TextureMaterialShader); mainWindow.SetGraph(graph); textureWindow.SetGraph(graph);
                Check((bool)textureAdmission.Invoke(textureWindow, new object[] { constant }) &&
                    (bool)textureAdmission.Invoke(textureWindow, new object[] { surface }) &&
                    !(bool)textureAdmission.Invoke(textureWindow, new object[] { height }) &&
                    !(bool)mainAdmission.Invoke(mainWindow, new object[] { constant }),
                    "UC-T05-05 texture authoring includes reusable fields/surfaces while rejecting terrain output and the wrong window.");
                graph.SetDomain(UCGraphDomain.LegacyMixed);
                string before = GraphSerializer.ToJson(graph); mainWindow.SetGraph(graph); textureWindow.SetGraph(graph);
                Check(before == GraphSerializer.ToJson(graph) &&
                    (bool)mainAdmission.Invoke(mainWindow, new object[] { surface }),
                    "UC-T05-05 opening either legacy canvas preserves exact authored nodes, values, IDs and domain for migration.");
                graph.RestoreDomainMetadata((UCGraphDomain)99, 99);
                Check(!(bool)mainAdmission.Invoke(mainWindow, new object[] { constant }) &&
                    !(bool)textureAdmission.Invoke(textureWindow, new object[] { constant }),
                    "UC-T05-05 future domain metadata stays preserved and cannot grant creation admission.");
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(mainWindow); UnityEngine.Object.DestroyImmediate(textureWindow);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
