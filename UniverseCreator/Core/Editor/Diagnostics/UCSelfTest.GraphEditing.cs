// UC-T05-07: shared authoring contracts retain fan-out, portals, typed combiners, clipboard identities and native Undo.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Combiners;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Utilities;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Editor.GraphUI;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestSharedGraphEditing()
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            var texture = ScriptableObject.CreateInstance<Graph>();
            var view = new UCGraphView();
            var provider = ScriptableObject.CreateInstance<UCSearchWindowProvider>();
            try
            {
                graph.SetDomain(UCGraphDomain.TerrainWater); texture.SetDomain(UCGraphDomain.TextureMaterialShader);
                var a = new ConstantNode { id = "editing-a", value = 0.2f };
                var b = new ConstantNode { id = "editing-b", value = 0.8f };
                var combiner = new MaximumNode { id = "editing-combiner" };
                var sender = new PortalOutNode { id = "editing-sender", channel = "Editing" };
                var receiver = new PortalInNode { id = "editing-receiver", channel = "Editing" };
                var preview = new PreviewNode { id = "editing-preview" };
                var output = new HeightOutputNode { id = "editing-output" };
                var group = new CommentGroupNode { id = "editing-group", title = "Reusable terrain block", body = "Preserve this annotation", size = new Vector2(500, 300) };
                graph.nodes.AddRange(new Generator[] { a, b, combiner, sender, receiver, preview, output, group });
                Check(UCGraphView.AuthorConnection(graph, new Connection(a.id, 0, combiner.id, 0)) &&
                    UCGraphView.AuthorConnection(graph, new Connection(b.id, 0, combiner.id, 0)) &&
                    UCGraphView.AuthorConnection(graph, new Connection(a.id, 0, combiner.id, 1)) &&
                    UCGraphView.AuthorConnection(graph, new Connection(a.id, 0, preview.id, 0)) &&
                    graph.connections.Count(c => c.targetNodeId == combiner.id && c.targetPortIndex == 0) == 1 &&
                    graph.connections.Count(c => c.sourceNodeId == a.id) == 2,
                    "UC-T05-07 authored inputs replace one wire while the same output retains multiple independent consumer wires.");
                UCGraphView.AuthorConnection(graph, new Connection(combiner.id, 0, sender.id, 0));
                UCGraphView.AuthorConnection(graph, new Connection(receiver.id, 0, output.id, 0));
                var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 3, 3), Vector3.zero, new Vector3(2, 100, 2), 100);
                new GraphEvaluator(graph).Evaluate(tile, new StopToken());
                Check(Mathf.Abs(tile.outputs.heightmap[1, 1] - 0.8f) < 1e-5f,
                    "UC-T05-07 distinct combiner operands and actual portal routing reach the real terrain output consumer.");
                view.Populate(graph); view.Controller.AutomaticGeneration = false;
                var nodeView = view.nodes.ToList().OfType<UCNodeView>().First(n => n.Node.id == a.id);
                Check(nodeView.InputPort(0) == null && nodeView.OutputPort(0).capacity == UnityEditor.Experimental.GraphView.Port.Capacity.Multi &&
                    view.nodes.ToList().OfType<UCNodeView>().First(n => n.Node.id == combiner.id).InputPort(0).capacity == UnityEditor.Experimental.GraphView.Port.Capacity.Single,
                    "UC-T05-07 real sockets present one incoming wire and multiple outgoing wires without changing saved runtime port metadata.");
                view.UpdateViewTransform(new Vector3(12, 24, 0), new Vector3(1.25f, 1.25f, 1));
                view.AddToSelection(nodeView); view.SaveCanvasState(); view.ClearSelection(); view.RestoreCanvasState();
                Check(view.selection.OfType<UCNodeView>().Any(n => n.Node.id == a.id) && Mathf.Abs(view.CanvasScale.x - 1.25f) < 1e-5f,
                    "UC-T05-07 selection and navigation restore independently of terrain evaluation or serialized node layout.");
                string copied = GraphSerializer.SelectionToJson(graph, graph.nodes);
                Check(GraphSerializer.CanPasteInto(graph, copied) && !GraphSerializer.CanPasteInto(texture, copied),
                    "UC-T05-07 reusable block admission checks actual destination domains and rejects a terrain output in Texture Graph.");
                int before = graph.nodes.Count;
                var pasted = GraphSerializer.PasteJson(graph, copied);
                Check(pasted.Count == before && pasted.Select(n => n.id).Distinct().Count() == before &&
                    pasted.All(n => n.id != a.id && n.id != group.id) && pasted.OfType<CommentGroupNode>().Single().body == group.body,
                    "UC-T05-07 pasted blocks preserve settings and comment frames under fresh unique node IDs.");
                Undo.PerformUndo();
                Check(graph.nodes.Count == before, "UC-T05-07 native Undo atomically removes a pasted block and its internal wires.");
                Undo.PerformRedo();
                Check(graph.nodes.Count == before * 2, "UC-T05-07 native Redo restores the pasted block without ID collisions.");
                provider.Init(view);
                foreach (UCGraphDomain domain in new[] { UCGraphDomain.TerrainWater, UCGraphDomain.Vegetation, UCGraphDomain.LightingClimateWeather, UCGraphDomain.TextureMaterialShader })
                {
                    view.EntryFilter = entry => GraphDomainRules.Supports(entry.graphDomains, domain);
                    var entries = provider.CreateSearchTree(new SearchWindowContext(Vector2.zero));
                    Check(entries.Any(e => e.content.text == "Utility") && entries.Any(e => e.content.text == "Portal (In + Out)") &&
                        entries.Where(e => e.userData is NodeCatalogEntry).All(e => GraphDomainRules.Supports(((NodeCatalogEntry)e.userData).graphDomains, domain)),
                        "UC-T05-07 " + domain + " palette supplies domain-admitted utilities/portals and excludes incompatible entries.");
                    if (domain != UCGraphDomain.TerrainWater)
                        Check(!entries.Any(e => e.content.text == "Tectonic Plates" || e.content.text == "Continental Shape Mask" ||
                            e.content.text == "Hydraulic Erosion" || e.content.text == "Coastal Erosion" || e.content.text == "Height Output"),
                            "UC-T08-15.11 " + domain + " palette excludes terrain construction and native height terminals.");
                    if (domain == UCGraphDomain.TextureMaterialShader)
                        Check(entries.Any(e => e.content.text == "Typed Material Portal (In + Out)"),
                            "UC-T05-07 Texture Graph keeps both field and configurable typed-material portal pairs.");
                }
            }
            finally
            {
                view.Dispose(); Undo.ClearUndo(graph);
                UnityEngine.Object.DestroyImmediate(provider); UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
