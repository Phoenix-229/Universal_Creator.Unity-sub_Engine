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

        /// <summary>
        /// Which node categories the add-node search offers. Null means all of
        /// them, which is what the height-graph window wants.
        /// </summary>
        /// <remarks>
        /// The two graph windows share this view, this serializer and this asset
        /// type, because a texture node and a height node are both Generators and
        /// duplicating the canvas to filter a menu would be a second copy of every
        /// bug. The window sets this instead.
        ///
        /// Filtering the SEARCH is enough on its own: the port-compatibility rules
        /// already stop a Surface output reaching a Matrix input, so a node that
        /// slipped through could not be wired to anything anyway. What the filter
        /// buys is that the texture window's menu does not offer twenty noise
        /// generators whose output nothing in that graph can accept.
        /// </remarks>
        public System.Func<NodeCategory, bool> CategoryFilter;

        /// <summary>
        /// The EditorWindow hosting this view, set by the window right after it is
        /// constructed. Used only to turn a panel-space point into a screen point so
        /// the add-node SearchWindow opens ON this window instead of drifting off to
        /// the side: the search window wants OS-screen coordinates, and the window's
        /// own <c>position</c> is the only reliable screen origin a GraphView can get.
        /// </summary>
        public EditorWindow HostWindow;

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

            // Clipboard. GraphView already owns the Ctrl+X / Ctrl+C / Ctrl+V / Ctrl+D
            // commands, the OS copy buffer and the menu wiring; the one thing it cannot
            // know is how a Generator becomes text, which is what these three hooks ask.
            // Left unassigned - which is how this view shipped - Copy produced nothing
            // and Paste was greyed out for ever.
            serializeGraphElements = OnSerializeSelection;
            canPasteSerializedData = OnCanPaste;
            unserializeAndPaste = OnPaste;

            BuildTrashBin();

            // Drop detection lives on the view, not the bin. A dragged node sits under
            // the pointer, so the bin never receives the mouse events itself - watching
            // for the release here and testing the pointer against the bin's rect is
            // what makes the gesture work at all.
            //
            // TrickleDown on both: SelectionDragger captures the pointer for the whole
            // drag, so during a capture these events are routed to the captor and a
            // normal bubble-phase handler on the view would never run.
            RegisterCallback<MouseMoveEvent>(OnMouseMove, TrickleDown.TrickleDown);
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

        // Arms the bin as a drag passes over it, and remembers where the pointer is.
        // Registered as a TrickleDown handler so it still sees the move while
        // SelectionDragger has the pointer captured.
        //
        // The remembered position is what a paste needs: Ctrl+V is a keyboard command
        // with no position of its own, and the pointer is the only honest answer to
        // "where did the user mean this to land".
        private void OnMouseMove(MouseMoveEvent evt)
        {
            RememberPointer(evt.mousePosition);

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

            // Portals pair by channel-name equality: an In and the Out that carry the
            // same channel are one link. Left on the shared "default" channel, a
            // second pair silently overlaps the first - every PortalIn then pulls the
            // same source and the graph reads a collapsed field (the "terrain goes
            // flat" bug). Give each freshly dropped portal the lowest "Channel N" free
            // FOR ITS DIRECTION, so the first In and first Out both land on Channel 1
            // and pair up, the next pair takes Channel 2, and two Ins never share.
            AssignUniquePortalChannel(node);

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

        // A portal (In or Out) resolves its partner purely by channel name, and an
        // In is meant to share its channel with an Out - that shared name IS the
        // pair. So a freshly dropped portal takes the lowest "Channel N" not already
        // used by another portal OF THE SAME DIRECTION: the first In and first Out
        // both get "Channel 1" and pair up, a second pair gets "Channel 2", and two
        // Ins can never collide onto one channel (which is what made both pull the
        // same feed and flattened the terrain). The opposite direction is ignored on
        // purpose. Renameable afterwards.
        void AssignUniquePortalChannel(Generator node)
        {
            UniverseCreator.Nodes.Utilities.PortalInNode pin = node as UniverseCreator.Nodes.Utilities.PortalInNode;
            UniverseCreator.Nodes.Utilities.PortalOutNode pout = node as UniverseCreator.Nodes.Utilities.PortalOutNode;
            if (pin == null && pout == null) return; // not a portal

            string current = pin != null ? pin.channel : pout.channel;
            // Only re-channel the shared default; respect a channel the user already set.
            if (!string.IsNullOrEmpty(current) && current != "default") return;

            bool wantIn = pin != null;

            // Channels already taken by portals of the SAME direction only. An In
            // sharing a channel with an Out is the desired pairing, so the other
            // direction must not block the number.
            System.Collections.Generic.HashSet<string> used =
                new System.Collections.Generic.HashSet<string>();
            foreach (Generator g in Graph.nodes)
            {
                if (object.ReferenceEquals(g, node)) continue;
                if (wantIn)
                {
                    UniverseCreator.Nodes.Utilities.PortalInNode gi = g as UniverseCreator.Nodes.Utilities.PortalInNode;
                    if (gi != null && !string.IsNullOrEmpty(gi.channel)) used.Add(gi.channel);
                }
                else
                {
                    UniverseCreator.Nodes.Utilities.PortalOutNode go = g as UniverseCreator.Nodes.Utilities.PortalOutNode;
                    if (go != null && !string.IsNullOrEmpty(go.channel)) used.Add(go.channel);
                }
            }

            int n = 1;
            string candidate = "Channel 1";
            while (used.Contains(candidate)) { n++; candidate = "Channel " + n; }

            if (pin != null) pin.channel = candidate; else pout.channel = candidate;
        }

        // ------------------------------------------------------------------
        //  Portal pair - one menu click drops a matched In + Out
        // ------------------------------------------------------------------

        /// <summary>
        /// Drops a Portal Out and a Portal In already paired on one fresh channel, so
        /// the tedious "add In, then add Out, then match their channels by hand" is a
        /// single action. <paramref name="texture"/> picks the texture-graph pair
        /// (Layer default) over the height-graph pair (Matrix default). The pair is
        /// unwired on arrival, so - like any node drop - it does NOT regenerate the
        /// world: a portal only carries data once BOTH ends are wired, and an unwired
        /// or half-wired portal evaluates to a typed blank without running any node
        /// math (see PortalRouting). Channel is the lowest "Channel N" no existing
        /// portal uses, so a second pair can never cross-talk with the first.
        /// </summary>
        public void AddPortalPair(bool texture, Vector2 contentPos)
        {
            if (Graph == null) return;

            Generator outNode = texture
                ? (Generator)new UniverseCreator.Nodes.Texturing.TexturePortalOutNode()
                : new PortalOutNode();
            Generator inNode = texture
                ? (Generator)new UniverseCreator.Nodes.Texturing.TexturePortalInNode()
                : new PortalInNode();

            string channel = NextFreePairChannel();
            SetPortalChannel(outNode, channel);
            SetPortalChannel(inNode, channel);

            // Out on the left feeding In on the right, matching the read direction so
            // the pair reads the way data flows even before a wire is drawn.
            outNode.editorPosition = contentPos;
            inNode.editorPosition = contentPos + new Vector2(300f, 0f);

            Graph.AddNode(outNode);
            Graph.AddNode(inNode);

            UCNodeView outView = new UCNodeView(outNode, this);
            views[outNode.id] = outView;
            AddElement(outView);

            UCNodeView inView = new UCNodeView(inNode, this);
            views[inNode.id] = inView;
            AddElement(inView);

            ClearSelection();
            AddToSelection(outView);
            AddToSelection(inView);

            MarkGraphDirty();
            OnStructureChanged();
            // Deliberately no QueueRegenerate: creating the pair changes nothing the
            // world can see until the user wires both ends.
        }

        // The channel a portal (any of the four kinds) carries, or null if the node
        // is not a portal. Read/write pair kept together so a new portal kind only
        // has to be added in one place.
        private static string GetPortalChannel(Generator node)
        {
            PortalInNode pin = node as PortalInNode;
            if (pin != null) return pin.channel;
            PortalOutNode pout = node as PortalOutNode;
            if (pout != null) return pout.channel;
            UniverseCreator.Nodes.Texturing.TexturePortalInNode tin =
                node as UniverseCreator.Nodes.Texturing.TexturePortalInNode;
            if (tin != null) return tin.channel;
            UniverseCreator.Nodes.Texturing.TexturePortalOutNode tout =
                node as UniverseCreator.Nodes.Texturing.TexturePortalOutNode;
            if (tout != null) return tout.channel;
            return null;
        }

        private static void SetPortalChannel(Generator node, string channel)
        {
            PortalInNode pin = node as PortalInNode;
            if (pin != null) { pin.channel = channel; return; }
            PortalOutNode pout = node as PortalOutNode;
            if (pout != null) { pout.channel = channel; return; }
            UniverseCreator.Nodes.Texturing.TexturePortalInNode tin =
                node as UniverseCreator.Nodes.Texturing.TexturePortalInNode;
            if (tin != null) { tin.channel = channel; return; }
            UniverseCreator.Nodes.Texturing.TexturePortalOutNode tout =
                node as UniverseCreator.Nodes.Texturing.TexturePortalOutNode;
            if (tout != null) { tout.channel = channel; return; }
        }

        // Lowest "Channel N" no existing portal of ANY kind or direction uses: a pair
        // shares one channel, so the whole number must be free for the pair to be its
        // own private link and never pull another pair's feed.
        private string NextFreePairChannel()
        {
            HashSet<string> used = new HashSet<string>();
            foreach (Generator g in Graph.nodes)
            {
                string ch = GetPortalChannel(g);
                if (!string.IsNullOrEmpty(ch)) used.Add(ch);
            }

            int n = 1;
            string candidate = "Channel 1";
            while (used.Contains(candidate)) { n++; candidate = "Channel " + n; }
            return candidate;
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

                // Wiring into (or through) a portal changes the type that portal - and
                // every Portal In sharing its channel - now carries. Reload so those
                // derived sockets recolour off the live wiring. Deferred so the edge
                // being created is not torn down mid-callback.
                if (src.Node is IPortalTyped || tgt.Node is IPortalTyped)
                    schedule.Execute(() => ReloadView());
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

            // Unwiring a portal drops the type it carried back to the fallback; the
            // Portal In on its channel must recolour to match. Deferred reload, as above.
            if (src.Node is IPortalTyped || tgt.Node is IPortalTyped)
                schedule.Execute(() => ReloadView());
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
        //  Right-click menu, add-node search window, clipboard
        // ------------------------------------------------------------------

        // Last pointer position in panel space, and whether one has been seen yet.
        // A menu-driven Add Node and a keyboard paste both need a "here", and this is
        // the only place that knows it.
        private Vector2 lastPointerPosition;
        private bool havePointer;

        private void RememberPointer(Vector2 panelPosition)
        {
            lastPointerPosition = panelPosition;
            havePointer = true;
        }

        /// <summary>
        /// The one canvas context menu: Add Node, then the ordinary editing commands
        /// with their keyboard equivalents spelled out.
        /// </summary>
        /// <remarks>
        /// This deliberately does NOT call base. GraphView's own menu offers Cut, Copy,
        /// Delete and Duplicate but no add-node entry, and it appends Paste only when
        /// the click missed every node - so right-clicking a node gave Cut and Copy with
        /// no way to paste, and calling both menus would list every shared command twice.
        ///
        /// The actions themselves ARE the base's: CutSelectionCallback and friends are
        /// exactly what Ctrl+X/C/V/D run, so the menu and the keyboard cannot diverge.
        /// </remarks>
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            // The click point is taken from the event that opened the menu rather than
            // from a separate ContextClickEvent handler, so the menu and the node it
            // creates cannot disagree about where the user clicked.
            IMouseEvent trigger = evt.triggerEvent as IMouseEvent;
            Vector2 viewPoint = trigger != null ? trigger.mousePosition : worldBound.center;
            pendingAddPosition = contentViewContainer.WorldToLocal(viewPoint);
            RememberPointer(viewPoint);

            Vector2 screenPoint = PanelToScreen(viewPoint);
            evt.menu.AppendAction("Add Node", (a) => OpenSearchWindow(screenPoint));
            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Cut  Ctrl+X", (a) => CutSelectionCallback(),
                                  (a) => StatusOf(canCutSelection));
            evt.menu.AppendAction("Copy  Ctrl+C", (a) => CopySelectionCallback(),
                                  (a) => StatusOf(canCopySelection));
            evt.menu.AppendAction("Paste  Ctrl+V", (a) => PasteCallback(),
                                  (a) => StatusOf(canPaste));
            evt.menu.AppendAction("Duplicate  Ctrl+D", (a) => DuplicateSelectionCallback(),
                                  (a) => StatusOf(canDuplicateSelection));

            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Delete  Del", (a) => DeleteSelection(),
                                  (a) => StatusOf(HasDeletableMenuSelection()));
        }

        private static DropdownMenuAction.Status StatusOf(bool enabled)
        {
            return enabled ? DropdownMenuAction.Status.Normal
                           : DropdownMenuAction.Status.Disabled;
        }

        // The menu's Delete covers wires as well as nodes and frames, which the trash
        // bin deliberately does not: an edge cannot be picked up and dragged, so the
        // bin has no gesture for one.
        private bool HasDeletableMenuSelection()
        {
            if (HasDeletableSelection()) return true;

            for (int i = 0; i < selection.Count; i++)
                if (selection[i] is Edge) return true;

            return false;
        }

        // GraphView hands this the elements it considers copyable and expects text back.
        // Only node views carry a Generator; edges are not serialised here because
        // SelectionToJson re-derives every connection running between two copied nodes
        // from the graph itself, which is right whether or not the user lassoed the wires.
        //
        // Comment frames are absent for a structural reason worth stating: GraphView's
        // copyable set is Node-and-friends, and UCCommentGroupView is a bare GraphElement,
        // so the keyboard path could never include one. Filtering the same way here keeps
        // the menu honest instead of copying frames that Ctrl+C would silently drop.
        private string OnSerializeSelection(IEnumerable<GraphElement> elements)
        {
            if (Graph == null || elements == null) return null;

            List<Generator> nodes = new List<Generator>();
            foreach (GraphElement element in elements)
            {
                UCNodeView view = element as UCNodeView;
                if (view != null && view.Node != null) nodes.Add(view.Node);
            }

            return nodes.Count == 0 ? null : GraphSerializer.SelectionToJson(Graph, nodes);
        }

        private bool OnCanPaste(string data)
        {
            return Graph != null && GraphSerializer.IsPasteableJson(data);
        }

        // Serves Paste, Duplicate and a paste of an exported .ucgraph alike - the base
        // routes all of them here, so there is one arrival path for new nodes.
        //
        // operationName is the label GraphView would put on an undo group. This view has
        // no undo stack of its own (edits are written straight through the Graph API and
        // saved by MarkGraphDirty), so there is nothing here to label.
        private void OnPaste(string operationName, string data)
        {
            if (Graph == null) return;

            List<Generator> pasted = GraphSerializer.PasteJson(Graph, data);
            if (pasted.Count == 0) return;

            MovePastedBlock(pasted);

            // Populate is the single canvas-building path; re-running it is what gives
            // the pasted nodes their views and restores the wires between them, rather
            // than a second copy of that loop living here.
            Populate(Graph);

            ClearSelection();
            for (int i = 0; i < pasted.Count; i++)
            {
                UCNodeView view;
                if (views.TryGetValue(pasted[i].id, out view)) { AddToSelection(view); continue; }

                UCCommentGroupView frame;
                if (groupViews.TryGetValue(pasted[i].id, out frame)) AddToSelection(frame);
            }

            MarkGraphDirty();
            OnStructureChanged();
        }

        // Lands the pasted block with its top-left corner under the pointer, keeping the
        // internal layout of the copied nodes intact. Pasting at the stored positions
        // would drop the copy exactly on top of the original, and a fixed nudge would
        // stack every repeated paste on the one before it.
        private void MovePastedBlock(List<Generator> pasted)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            for (int i = 0; i < pasted.Count; i++)
            {
                min.x = Mathf.Min(min.x, pasted[i].editorPosition.x);
                min.y = Mathf.Min(min.y, pasted[i].editorPosition.y);
            }
            if (min.x == float.MaxValue) return;

            Vector2 anchor = contentViewContainer.WorldToLocal(
                havePointer ? lastPointerPosition : worldBound.center);

            Vector2 shift = anchor - min;
            for (int i = 0; i < pasted.Count; i++)
                pasted[i].editorPosition += shift;
        }

        /// <summary>Toolbar path: opens the search window over the top of the view.</summary>
        public void ShowAddNodeMenu()
        {
            // A node dropped from the toolbar lands in the middle of what the user is
            // looking at.
            Vector2 panelCentre = worldBound.center;
            pendingAddPosition = contentViewContainer.WorldToLocal(panelCentre);

            // The picker itself opens just under the top edge of THIS window, centred
            // horizontally - the "drops down from the toolbar" placement the user asked
            // for - instead of the old worldBound-as-screen-point maths that flung it
            // off to the side of the display. Screen coords come from the host window.
            Vector2 panelTop = new Vector2(panelCentre.x, worldBound.yMin + 24f);
            OpenSearchWindow(PanelToScreen(panelTop));
        }

        // Turn a panel-space (window-local) point into the OS-screen point the
        // SearchWindow expects. The host window's own screen rect is the dependable
        // origin; GUIUtility.GUIToScreenPoint needs a live IMGUI context that a UI
        // Toolkit callback does not always have, so it is only the fallback.
        private Vector2 PanelToScreen(Vector2 panelPoint)
        {
            if (HostWindow != null) return HostWindow.position.position + panelPoint;
            return GUIUtility.GUIToScreenPoint(panelPoint);
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

        /// <summary>
        /// Rebuild every node view and its edges from the current graph model. Used
        /// when a node changes something structural about its own ports - a portal
        /// switching the type its socket carries - so the port colour, capacity and
        /// any now-stale edges are re-evaluated against the new type. Edges reconnect
        /// by stored port index, so a same-index type change keeps its wiring.
        /// Cheap enough for an occasional structural change; never called per keystroke.
        /// </summary>
        public void ReloadView()
        {
            if (Graph != null) Populate(Graph);
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
