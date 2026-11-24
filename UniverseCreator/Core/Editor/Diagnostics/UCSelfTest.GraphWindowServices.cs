// UC-T05-02: exercise actual canvases, native Undo, captured inputs and resource retirement.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestGraphWindowServices()
        {
            int bindings = GraphWindowController.BindingCount;
            int pending = GraphWindowController.PendingAutomaticGraphCount;
            var graph = ScriptableObject.CreateInstance<Graph>();
            var other = ScriptableObject.CreateInstance<Graph>();
            var source = new ConstantNode { id = "window-source", value = 0.2f };
            var output = new HeightOutputNode { id = "window-output" };
            graph.nodes.Add(source); graph.nodes.Add(output);
            graph.connections.Add(new Connection(source.id, 0, output.id, 0));
            UCGraphView first = null, second = null, third = null;
            IPanel navigationPanel = null;
            try
            {
                first = new UCGraphView(); second = new UCGraphView(); third = new UCGraphView();
                first.Populate(graph); second.Populate(graph); third.Populate(other);
                first.Controller.AutomaticGeneration = false; second.Controller.AutomaticGeneration = false;
                // UC-T08-15: navigation survives rebinding without modifying authored nodes, selection or evaluation scheduling.
                var navigation = first.CreateNavigationToolbar();
                // UC-T08-15: the installed editor-panel factory supplies real change-event dispatch without a visible window.
                var panelFactory = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.Panel", true).GetMethod(
                    "CreateEditorPanel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                navigationPanel = (IPanel)panelFactory.Invoke(null, new object[] { graph });
                navigationPanel.visualTree.Add(first); navigationPanel.visualTree.Add(second); navigationPanel.visualTree.Add(third);
                navigationPanel.visualTree.Add(navigation);
                Check(navigation.panel == navigationPanel, "UC-T08-15 toolbar is attached to an owned native Editor panel before navigation events.");
                var modes = navigation.Query<ToolbarToggle>().ToList();
                var selected = first.nodes.ToList().OfType<UCNodeView>().First();
                first.AddToSelection(selected);
                first.UpdateViewTransform(new Vector3(91, -37, 0), new Vector3(1.25f, 1.25f, 1));
                // UC-T08-15: restore the native canvas transform, which may quantize the requested pan at this zoom.
                var savedTranslation = first.contentViewContainer.style.translate.value;
                int sourceVersion = source.version;
                modes[1].value = true;
                Check(first.HandNavigation && !modes[0].value && modes[1].value && first.selection.Contains(selected) &&
                    graph.connections.Count == 1 && source.version == sourceVersion && GraphWindowController.PendingAutomaticGraphCount == pending,
                    "UC-T08-15 Hand selects only navigation while retaining selection, wiring, node versions and pending generation.");
                second.RestoreCanvasState();
                var restoredTranslation = second.contentViewContainer.style.translate.value;
                Check(second.HandNavigation && Mathf.Approximately(restoredTranslation.x.value, savedTranslation.x.value) &&
                    Mathf.Approximately(restoredTranslation.y.value, savedTranslation.y.value) && Mathf.Approximately(second.CanvasScale.x, 1.25f),
                    "UC-T08-15 another canvas restores the saved Hand mode, pan and zoom for the same graph: hand=" + second.HandNavigation +
                    " pan=" + restoredTranslation + " zoom=" + second.CanvasScale + ".");
                modes[0].value = true;
                Check(!first.HandNavigation && modes[0].value && !modes[1].value,
                    "UC-T08-15 Pointer restores editing with mutually exclusive toolbar state.");
                third.RestoreCanvasState();
                Check(!third.HandNavigation, "UC-T08-15 unrelated graph retains its own Pointer navigation state.");
                int notifications = 0, unrelated = 0;
                second.StructureChanged += () => notifications++;
                third.StructureChanged += () => unrelated++;
                source.value = 0.73f; source.version++; first.MarkGraphDirty(); first.QueueRegenerate(source);
                graph.nodes.Add(new ConstantNode { id = "window-added", value = 0.4f }); first.MarkGraphDirty();
                GraphWindowController.FlushPendingEdits();
                Check(second.nodes.ToList().OfType<UCNodeView>().Count() == 3 && notifications == 1 && unrelated == 0,
                    "UC-T05-02 another canvas refreshes once per edit burst while an unrelated asset remains untouched.");
                Check(GraphWindowController.PendingAutomaticGraphCount == pending,
                    "UC-T05-02 explicit companion commits validate but cannot queue terrain generation.");
                first.Controller.AutomaticGeneration = true; second.Controller.AutomaticGeneration = true;
                first.QueueRegenerate(source); second.QueueRegenerate(source); first.QueueRegenerate(source);
                Check(GraphWindowController.PendingAutomaticGraphCount == pending + 1,
                    "UC-T05-02 two main canvases coalesce repeated commits into one pending graph rebuild.");

                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(graph, "Graph window node edit");
                graph.nodes.Add(new ConstantNode { id = "window-undo", value = 0.8f }); first.MarkGraphDirty();
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); Undo.IncrementCurrentGroup();
                GraphWindowController.FlushPendingEdits(); Undo.PerformUndo(); GraphWindowController.FlushPendingEdits();
                Check(graph.nodes.Count == 3 && first.nodes.ToList().OfType<UCNodeView>().Count() == 3 &&
                    second.nodes.ToList().OfType<UCNodeView>().Count() == 3,
                    "UC-T05-02 native Undo synchronizes both canvases to the restored authored graph.");
                Undo.PerformRedo(); GraphWindowController.FlushPendingEdits();
                Check(graph.nodes.Count == 4 && first.nodes.ToList().OfType<UCNodeView>().Count() == 4 &&
                    second.nodes.ToList().OfType<UCNodeView>().Count() == 4,
                    "UC-T05-02 native Redo synchronizes both canvases without a second generation pump.");

                var probe = new ValidationProbeNode { id = "window-domain-probe", domain = NodeExecutionDomain.MainThread };
                other.nodes.Add(probe);
                bool workerRejected = false;
                try { var unsupported = GenerationSnapshot.Capture(other); unsupported.Release(); }
                catch (InvalidOperationException) { workerRejected = true; }
                var captured = GenerationSnapshot.CaptureForDomains(other, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread);
                try
                {
                    probe.domain = NodeExecutionDomain.Worker;
                    Check(workerRejected && ((ValidationProbeNode)captured.Graph.nodes[0]).domain == NodeExecutionDomain.MainThread,
                        "UC-T05-02 explicit companion capture preserves admitted main-thread nodes while the original worker-only contract rejects them.");
                }
                finally { captured.Release(); }

                var provider = (UnityEngine.Object)typeof(UCGraphView).GetField("searchProvider",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(first);
                first.Dispose(); first.Dispose(); first = null;
                Check(provider == null, "UC-T05-02 canvas disposal destroys its native search provider and is idempotent.");
                second.Populate(other); third.Populate(null);
                Check(GraphWindowController.PendingAutomaticGraphCount == pending,
                    "UC-T05-02 closing/rebinding the last main-asset canvas retires its pending rebuild.");
            }
            finally
            {
                navigationPanel?.Dispose();
                first?.Dispose(); second?.Dispose(); third?.Dispose();
                Undo.ClearUndo(graph); UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(other);
            }
            Check(GraphWindowController.BindingCount == bindings,
                "UC-T05-02 shared editor subscriptions return to the pre-fixture binding count.");
        }
    }
}
