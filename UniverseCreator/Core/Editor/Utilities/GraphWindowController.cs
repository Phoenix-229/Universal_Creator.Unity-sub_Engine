// UC-T05-02: one editor binding/edit dispatcher; evaluation and world pumping retain their existing owners.
using System;
using System.Collections.Generic;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.GraphUI;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>Synchronizes canvases and coalesces committed main-graph edits without applying companion graphs.</summary>
    /// <remarks>Main thread only. The owning window must dispose its canvas/controller on disable.</remarks>
    public sealed class GraphWindowController : IDisposable
    {
        private static readonly List<GraphWindowController> controllers = new List<GraphWindowController>();
        private static readonly Dictionary<Graph, GraphWindowController> edited = new Dictionary<Graph, GraphWindowController>();
        private static readonly Dictionary<Graph, double> automatic = new Dictionary<Graph, double>();
        private static readonly Dictionary<Graph, int> dirtyCounts = new Dictionary<Graph, int>();
        private readonly UCGraphView view;
        private bool disposed;

        /// <summary>Whether commits may request the bound main world's existing automatic rebuild; companions set false.</summary>
        public bool AutomaticGeneration { get; set; } = true;

        /// <summary>Creates an editor binding for an existing shared canvas, without another evaluator or world.</summary>
        /// <param name="view">Non-null canvas owned by a window or diagnostic fixture.</param>
        /// <exception cref="ArgumentNullException">The canvas is null.</exception>
        public GraphWindowController(UCGraphView view)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            if (controllers.Count == 0)
            {
                Undo.undoRedoPerformed += OnUndoRedo;
                EditorApplication.update += Pump;
            }
            controllers.Add(this);
        }

        internal static int BindingCount => controllers.Count;
        internal static int PendingAutomaticGraphCount => automatic.Count;

        internal void ObserveBinding()
        {
            if (view.Graph != null) dirtyCounts[view.Graph] = EditorUtility.GetDirtyCount(view.Graph);
            RetireUnusedBindings();
        }

        internal void AuthoredEdit()
        {
            if (disposed || view.Graph == null) return;
            view.PreviewScheduler.AuthoredEdit(view.Graph); // UC-T05-08: only the explicitly requested preview is debounced again.
            edited[view.Graph] = this;
            dirtyCounts[view.Graph] = EditorUtility.GetDirtyCount(view.Graph);
        }

        internal void Commit()
        {
            if (disposed || view.Graph == null) return;
            view.RefreshValidation(); // Explicit-run domains still show invalid/pending authoring immediately.
            if (!AutomaticGeneration) return;
            automatic[view.Graph] = EditorApplication.timeSinceStartup + 0.2;
        }

        private static void OnUndoRedo()
        {
            // Dirty counts distinguish this asset's Undo from unrelated terrain/object Undo without serializing a large canvas.
            foreach (var controller in controllers)
            {
                Graph graph = controller.view.Graph;
                if (graph == null) continue;
                int now = EditorUtility.GetDirtyCount(graph);
                if (dirtyCounts.TryGetValue(graph, out int before) && now == before) continue;
                edited[graph] = null;
                if (controller.AutomaticGeneration) automatic[graph] = EditorApplication.timeSinceStartup + 0.2;
            }
            foreach (var graph in edited.Keys) if (graph != null) dirtyCounts[graph] = EditorUtility.GetDirtyCount(graph);
        }

        private static void Pump()
        {
            FlushPendingEdits();
            if (automatic.Count == 0) return;
            var ready = new List<Graph>();
            foreach (var item in automatic) if (item.Key == null || EditorApplication.timeSinceStartup >= item.Value) ready.Add(item.Key);
            foreach (Graph graph in ready)
            {
                automatic.Remove(graph);
                if (graph == null || !GraphValidationReport.Analyze(graph).IsValid) continue;
                var worlds = UnityEngine.Object.FindObjectsByType<UniverseCreatorObject>(UnityEngine.FindObjectsInactive.Exclude);
                foreach (var world in worlds)
                    if (world != null && ReferenceEquals(world.graph, graph) && world.autoRegenerate) world.RebuildChunks();
            }
        }

        internal static void FlushPendingEdits()
        {
            if (edited.Count == 0) return;
            // Snapshot only the bindings, because a canvas callback may close/rebind a window during notification.
            var listeners = controllers.ToArray();
            var changes = new Dictionary<Graph, GraphWindowController>(edited); edited.Clear();
            foreach (var item in changes)
                foreach (var listener in listeners)
                    if (!listener.disposed && ReferenceEquals(listener.view.Graph, item.Key))
                        listener.view.RefreshSharedEdit(!ReferenceEquals(listener, item.Value));
        }

        private static void RetireUnusedBindings()
        {
            var retired = new List<Graph>();
            foreach (Graph graph in dirtyCounts.Keys)
            {
                bool bound = false;
                foreach (var controller in controllers) if (ReferenceEquals(controller.view.Graph, graph)) { bound = true; break; }
                if (!bound || graph == null) retired.Add(graph);
            }
            foreach (Graph graph in retired) { dirtyCounts.Remove(graph); edited.Remove(graph); automatic.Remove(graph); }
        }

        /// <summary>Releases editor callbacks and pending work owned only by this binding; safe to call repeatedly.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; controllers.Remove(this); RetireUnusedBindings();
            if (controllers.Count != 0) return;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= Pump;
            edited.Clear(); automatic.Clear(); dirtyCounts.Clear();
        }
    }
}
