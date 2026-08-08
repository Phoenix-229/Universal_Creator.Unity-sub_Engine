// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UCGraphView.cs
//  Purpose: The GraphView canvas. Owns the UCNodeView instances, translates
//           GraphView edges back into runtime Connections, and issues debounced
//           async regeneration after parameter commits.
//
//  Correction C-06/C-11/C-13. The runtime graph engine is untouched - this view
//  mirrors Graph.nodes / Graph.connections and writes every edit back through
//  the Graph API, so cycle checks and single-input rules live exactly where
//  they always did.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Utilities;   // CommentGroupNode - nested, so not implicit
using UniverseCreator.Editor.Utilities;
using UniverseCreator.TerrainSystem;
using GVPort = UnityEditor.Experimental.GraphView.Port;

// GraphUI, not Graph: a namespace named Graph is a member of UniverseCreator.Editor,
// and namespace members are resolved before using-directives - so every bare "Graph"
// in this assembly bound to the namespace instead of the Nodes.Graph type (CS0118).
// Not GraphView either, which would shadow UnityEditor's GraphView the same way.
namespace UniverseCreator.Editor.GraphUI
{
    public class UCGraphView : GraphView
    {
        public Graph Graph { get; private set; }

        /// <summary>Content-space position where the next menu-created node lands.</summary>
        public Vector2 PendingAddPosition { get { return pendingAddPosition; } }

        /// <summary>Raised when the graph structure changes, so the window's
        /// node/link counter stays honest without polling.</summary>
        public event System.Action StructureChanged;

        private readonly Dictionary<string, UCNodeView> views =
            new Dictionary<string, UCNodeView>();

        // Comment frames are kept apart from `views` because they are backdrops, not
        // nodes: they have no ports, so nothing here may ever be asked for a port
        // index, and the edge-restore loop must never find one.
        private readonly Dictionary<string, UCCommentGroupView> groupViews =
            new Dictionary<string, UCCommentGroupView>();

        private readonly UCSearchWindowProvider searchProvider;
        private Vector2 pendingAddPosition;

        // True while Populate() rebuilds the canvas: edge events that fire during
        // the restore loop describe connections that already exist in Graph, so
        // they must not round-trip back into it.
        private bool restoring;

        // ------------------------------------------------------------------
        //  Regeneration - C-13
        //
        //  Commits are debounced. A slider drag fires a value change per pixel;
        //  QueueRegenerate coalesces the burst into one run that fires DebounceMs
        //  after the last change, and the world supersedes any run still in
        //  flight through its StopToken.
        // ------------------------------------------------------------------

        private const int DebounceMs = 200;
        private readonly HashSet<string> pendingRegenerate = new HashSet<string>();
        private IVisualElementScheduledItem regenJob;

        public UCGraphView()
        {
            searchProvider = ScriptableObject.CreateInstance<UCSearchWindowProvider>();
            searchProvider.Init(this);

            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/UniverseCreator/Core/Editor/Graph/UCGraphStyles.uss");
            if (sheet != null) styleSheets.Add(sheet);

            SetupZoom(0.3f, 2.5f);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());

            GridBackground grid = new GridBackground();
            grid.StretchToParentSize();
            Insert(0, grid);

            graphViewChanged = OnGraphViewChanged;
            RegisterCallback<ContextClickEvent>(OnContextClick);

            BuildTrashBin();

            // Drop detection lives on the view, not the bin. A dragged node sits under
            // the pointer, so the bin never receives the mouse events itself - watching
            // for the release here and testing the pointer against the bin's rect is
            // what makes the gesture work at all.
            //
            // TrickleDown on both: SelectionDragger captures the pointer for the whole
            // drag, so during a capture these events are routed to the captor and a
            // normal bubble-phase handler on the view would never run.
            RegisterCallback<MouseMoveEvent>(OnMouseMoveForTrash, TrickleDown.TrickleDown);
            RegisterCallback<MouseUpEvent>(OnMouseUpOverTrash, TrickleDown.TrickleDown);
        }

        // ------------------------------------------------------------------
        //  Drag-to-delete bin
        // ------------------------------------------------------------------

        private VisualElement trashBin;

        // Parented to this view rather than to contentViewContainer, so it holds its
        // corner while the canvas pans and zooms underneath it.
        private void BuildTrashBin()
        {
            trashBin = new VisualElement();
            trashBin.name = "uc-trash-bin";

            trashBin.style.position = Position.Absolute;
            trashBin.style.top = 12f;
            trashBin.style.right = 12f;
            trashBin.style.width = 46f;
            trashBin.style.height = 46f;
            trashBin.style.alignItems = Align.Center;
            trashBin.style.justifyContent = Justify.Center;

            trashBin.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 0.9f);
            trashBin.style.borderTopLeftRadius = 6f;
            trashBin.style.borderTopRightRadius = 6f;
            trashBin.style.borderBottomLeftRadius = 6f;
            trashBin.style.borderBottomRightRadius = 6f;

            SetTrashBorder(new Color(0.38f, 0.38f, 0.42f, 1f), 1f);

            Label glyph = new Label("\U0001F5D1");
            glyph.style.fontSize = 22f;
            glyph.pickingMode = PickingMode.Ignore;
            trashBin.Add(glyph);

            trashBin.tooltip = "Drag nodes here to delete them";

            // Never let the bin intercept a drag. GraphView's SelectionDragger needs an
            // unbroken stream of mouse events on the node being dragged; a bin that
            // captured the pointer on hover would drop the node mid-gesture.
            trashBin.pickingMode = PickingMode.Ignore;

            Add(trashBin);
            trashBin.BringToFront();
        }

        private void SetTrashBorder(Color colour, float width)
        {
            trashBin.style.borderTopColor = colour;
            trashBin.style.borderRightColor = colour;
            trashBin.style.borderBottomColor = colour;
            trashBin.style.borderLeftColor = colour;

            trashBin.style.borderTopWidth = width;
            trashBin.style.borderRightWidth = width;
            trashBin.style.borderBottomWidth = width;
            trashBin.style.borderLeftWidth = width;
        }

        // Highlighted while a dragged selection is over the bin, so the gesture reads as
        // "this will delete" before the user commits to letting go.
        private void SetTrashArmed(bool armed)
        {
            if (trashBin == null) return;

            if (armed)
            {
                trashBin.style.backgroundColor = new Color(0.55f, 0.16f, 0.16f, 0.95f);
                SetTrashBorder(new Color(1f, 0.45f, 0.45f, 1f), 2f);
            }
            else
            {
                trashBin.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 0.9f);
                SetTrashBorder(new Color(0.38f, 0.38f, 0.42f, 1f), 1f);
            }
        }

        private bool PointerOverTrash(Vector2 viewLocalPos)
        {
            return trashBin != null && trashBin.layout.Contains(viewLocalPos);
        }

        // Arms the bin as a drag passes over it. Registered as a TrickleDown handler so
        // it still sees the move while SelectionDragger has the pointer captured.
        private void OnMouseMoveForTrash(MouseMoveEvent evt)
        {
            if (trashBin == null) return;

            bool dragging = evt.pressedButtons != 0 && HasDeletableSelection();
            SetTrashArmed(dragging && PointerOverTrash(evt.localMousePosition));
        }

        private void OnMouseUpOverTrash(MouseUpEvent evt)
        {
            if (trashBin == null) return;

            bool over = PointerOverTrash(evt.localMousePosition);
            SetTrashArmed(false);

            if (!over || !HasDeletableSelection()) return;

            // Deferred by one tick, and the event is deliberately left to propagate.
            // SelectionDragger is still finishing this drag: it has the pointer captured
            // and will write final positions back to the elements it was moving. Deleting
            // them from under it here - or swallowing the mouse-up it needs to release the
            // capture - leaves the dragger holding destroyed views and the canvas stuck in
            // a drag that never ended.
            schedule.Execute(() =>
            {
                if (!HasDeletableSelection()) return;

                // DeleteSelection is the single deletion path: it strips the views,
                // removes the nodes from Graph, and drops the connections that touched
                // them. Routing the bin through it rather than reimplementing removal is
                // what keeps the gesture and the Delete key from ever diverging.
                DeleteSelection();
            });
        }

        private bool HasDeletableSelection()
        {
            for (int i = 0; i < selection.Count; i++)
            {
                if (selection[i] is UCNodeView) return true;
                if (selection[i] is UCCommentGroupView) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        //  Population
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the whole canvas from a graph asset. Node views first, then
        /// edges, because an edge needs both endpoint views to exist.
        /// </summary>
        public void Populate(Graph graph)
        {
            Graph = graph;
            views.Clear();
            groupViews.Clear();
            contentViewContainer.Clear();
            if (graph == null) return;

            restoring = true;
            try
            {
                for (int i = 0; i < graph.nodes.Count; i++)
                {
                    Generator node = graph.nodes[i];
                    if (node == null) continue;

                    CommentGroupNode comment = node as CommentGroupNode;
                    if (comment != null)
                    {
                        AddGroupElement(comment);
                        continue;
                    }

                    UCNodeView view = new UCNodeView(node, this);
                    views[node.id] = view;
                    AddElement(view);
                }

                for (int i = 0; i < graph.connections.Count; i++)
                {
                    Connection conn = graph.connections[i];

                    UCNodeView src, tgt;
                    if (!views.TryGetValue(conn.sourceNodeId, out src)) continue;
                    if (!views.TryGetValue(conn.targetNodeId, out tgt)) continue;

                    GVPort output = src.OutputPort(conn.sourcePortIndex);
                    GVPort input = tgt.InputPort(conn.targetPortIndex);
                    if (output == null || input == null) continue;

                    Edge edge = output.ConnectTo(input);
                    StyleEdge(edge, src, tgt, conn.sourcePortIndex);
                    AddElement(edge);
                }
            }
            finally
            {
                restoring = false;
            }
        }

        // ------------------------------------------------------------------
        //  Node creation
        // ------------------------------------------------------------------

        public void AddNodeAt(NodeCatalogEntry entry, Vector2 contentPos)
        {
            if (Graph == null || entry == null) return;

            Generator node = NodeCatalog.Instantiate(entry);
            if (node == null) return;

            node.editorPosition = contentPos;
            Graph.AddNode(node);

            CommentGroupNode comment = node as CommentGroupNode;
            if (comment != null)
            {
                UCCommentGroupView frame = AddGroupElement(comment);
                ClearSelection();
                AddToSelection(frame);

                MarkGraphDirty();
                OnStructureChanged();
                return;
            }

            UCNodeView view = new UCNodeView(node, this);
            views[node.id] = view;
            AddElement(view);

            ClearSelection();
            AddToSelection(view);

            MarkGraphDirty();
            OnStructureChanged();
        }

        // A comment frame is added at the back of the content container so every
        // node draws over it. Added on top it would swallow the clicks meant for the
        // nodes it groups, which is the whole reason a backdrop is not just a node.
        private UCCommentGroupView AddGroupElement(CommentGroupNode comment)
        {
            UCCommentGroupView frame = new UCCommentGroupView(comment, this);
            groupViews[comment.id] = frame;
            AddElement(frame);
            frame.SendToBack();
            return frame;
        }

        // ------------------------------------------------------------------
        //  View <-> runtime graph bridge
        // ------------------------------------------------------------------

        public override List<GVPort> GetCompatiblePorts(GVPort startPort, NodeAdapter nodeAdapter)
        {
            List<GVPort> compatible = new List<GVPort>();
            ports.ForEach((port) =>
            {
                // Cannot connect to itself, cannot connect to another port on the same node,
                // and cannot connect Input-to-Input or Output-to-Output.
                if (startPort != port && startPort.node != port.node && startPort.direction != port.direction)
                {
                    compatible.Add(port);
                }
            });
            return compatible;
        }

        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (restoring) return change;

            if (change.edgesToCreate != null)
                for (int i = 0; i < change.edgesToCreate.Count; i++)
                    OnEdgeCreated(change.edgesToCreate[i]);

            // GraphViewChange has no edgesToRemove - removals of every kind arrive in
            // elementsToRemove, so the edges have to be picked out of it. Nodes in that
            // same list are handled by DeleteSelection, which reaches the graph itself.
            if (change.elementsToRemove != null)
                for (int i = 0; i < change.elementsToRemove.Count; i++)
                {
                    Edge edge = change.elementsToRemove[i] as Edge;
                    if (edge != null) OnEdgeRemoved(edge);
                }

            if (change.movedElements != null)
            {
                bool movedNode = false;
                for (int i = 0; i < change.movedElements.Count && !movedNode; i++)
                    movedNode = change.movedElements[i] is UCNodeView ||
                                change.movedElements[i] is UCCommentGroupView;
                if (movedNode) MarkGraphDirty();
            }

            return change;
        }

        private void OnEdgeCreated(Edge edge)
        {
            UCNodeView src = edge.output != null ? edge.output.node as UCNodeView : null;
            UCNodeView tgt = edge.input != null ? edge.input.node as UCNodeView : null;
            if (src == null || tgt == null) return;

            int srcPort = src.IndexOfPort(edge.output);
            int tgtPort = tgt.IndexOfPort(edge.input);
            if (srcPort < 0 || tgtPort < 0) return;

            Connection conn = new Connection(src.Node.id, srcPort, tgt.Node.id, tgtPort);

            if (Graph.AddConnection(conn))
            {
                StyleEdge(edge, src, tgt, srcPort);
                MarkGraphDirty();
                QueueRegenerate(tgt.Node);
                OnStructureChanged();
            }
            else
            {
                // Rejected by the runtime gate (a cycle, or self-loop). Unwind the
                // visual edge so the canvas does not claim a link the engine refused.
                RemoveElement(edge);
                Debug.LogWarning("Universe Creator: connection rejected (it would create a cycle).");
            }
        }

        private void OnEdgeRemoved(Edge edge)
        {
            UCNodeView src = edge.output != null ? edge.output.node as UCNodeView : null;
            UCNodeView tgt = edge.input != null ? edge.input.node as UCNodeView : null;
            if (src == null || tgt == null) return;

            Connection conn = new Connection(src.Node.id, src.IndexOfPort(edge.output),
                                             tgt.Node.id, tgt.IndexOfPort(edge.input));
            Graph.RemoveConnection(conn);
            MarkGraphDirty();
            QueueRegenerate(tgt.Node);
            OnStructureChanged();
        }

        /// <summary>
        /// Delete key. The base implementation removes the elements from the view;
        /// the nodes are then stripped from the Graph, which also drops any
        /// connections that touched them.
        /// </summary>
        public override EventPropagation DeleteSelection()
        {
            List<UCNodeView> doomed = new List<UCNodeView>();
            List<UCCommentGroupView> doomedGroups = new List<UCCommentGroupView>();
            for (int i = 0; i < selection.Count; i++)
            {
                UCNodeView view = selection[i] as UCNodeView;
                if (view != null) { doomed.Add(view); continue; }

                // A frame is a node in Graph.nodes even though it is not a node in
                // the view, so deleting one has to reach the graph too or it comes
                // back on the next Populate.
                UCCommentGroupView frame = selection[i] as UCCommentGroupView;
                if (frame != null) doomedGroups.Add(frame);
            }

            // Base returns whether it consumed the event; the graph-side cleanup below
            // does not change that verdict, so its result is passed straight through.
            EventPropagation propagation = base.DeleteSelection();

            for (int i = 0; i < doomedGroups.Count; i++)
            {
                Graph.RemoveNode(doomedGroups[i].Node.id);
                groupViews.Remove(doomedGroups[i].Node.id);
            }

            if (doomedGroups.Count > 0)
            {
                MarkGraphDirty();
                OnStructureChanged();
            }

            if (doomed.Count > 0)
            {
                for (int i = 0; i < doomed.Count; i++)
                {
                    Graph.RemoveNode(doomed[i].Node.id);
                    views.Remove(doomed[i].Node.id);
                }
                MarkGraphDirty();
                OnStructureChanged();
            }

            return propagation;
        }

        private void StyleEdge(Edge edge, UCNodeView src, UCNodeView tgt, int srcPort)
        {
            PortType type = src.Node.GetOutputs()[srcPort].type;
            Color color = tgt.Node is IOutputNode
                ? UCNodeStyle.TerminalEdgeColor
                : UCNodeStyle.EdgeColor(type);

            // EdgeControl draws the gradient between these two; same colour on both
            // ends gives a uniform wire that reads as one data flow.
            edge.edgeControl.inputColor = color;
            edge.edgeControl.outputColor = color;
        }

        // ------------------------------------------------------------------
        //  Add-node search window
        // ------------------------------------------------------------------

        private void OnContextClick(ContextClickEvent evt)
        {
            // evt.mousePosition is local to this GraphView; mapping it through the
            // content container undoes the zoom/pan so the node lands where the
            // user actually clicked.
            pendingAddPosition = contentViewContainer.WorldToLocal(evt.mousePosition);
            OpenSearchWindow(evt.mousePosition + worldBound.position);
            evt.StopPropagation();
        }

        /// <summary>Toolbar path: opens the search window centred on the view.</summary>
        public void ShowAddNodeMenu()
        {
            Vector2 centre = new Vector2(layout.width * 0.5f, layout.height * 0.5f);
            if (centre.x < 1f) centre = worldBound.size * 0.5f;
            pendingAddPosition = contentViewContainer.WorldToLocal(centre);
            OpenSearchWindow(worldBound.position + centre);
        }

        private void OpenSearchWindow(Vector2 screenPos)
        {
            SearchWindow.Open(new SearchWindowContext(screenPos, 0, 0), searchProvider);
        }

        // ------------------------------------------------------------------
        //  Persistence and regeneration
        // ------------------------------------------------------------------

        public void MarkGraphDirty()
        {
            GraphAssetUtility.MarkDirty(Graph);
        }

        public void QueueRegenerate(Generator node)
        {
            if (Graph == null) return;
            if (node != null) pendingRegenerate.Add(node.id);
            if (regenJob != null) return;      // a flush is already scheduled

            if (panel == null)
            {
                FlushRegenerate();             // unattached: no scheduler, run now
                return;
            }
            // ExecuteLater returns void, so the item has to be captured first - the
            // guard above reads regenJob to tell "already scheduled" from "idle".
            regenJob = schedule.Execute(FlushRegenerate);
            regenJob.ExecuteLater(DebounceMs);
        }

        private void FlushRegenerate()
        {
            regenJob = null;
            if (pendingRegenerate.Count == 0) return;
            pendingRegenerate.Clear();

            // Worlds driven by this graph are asked to rebuild, honouring their own
            // auto-regenerate setting. RegenerateAsync supersedes any run in flight.
            UniverseCreatorObject[] worlds =
                Object.FindObjectsByType<UniverseCreatorObject>(FindObjectsInactive.Exclude);

            for (int i = 0; i < worlds.Length; i++)
            {
                UniverseCreatorObject world = worlds[i];
                if (world == null) continue;
                if (!ReferenceEquals(world.graph, Graph)) continue;
                if (!world.autoRegenerate) continue;
                world.RebuildChunks();
            }
        }

        private void OnStructureChanged()
        {
            if (StructureChanged != null) StructureChanged();
        }
    }
}
