// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UCCommentGroupView.cs
//  Purpose: GraphView backdrop for a CommentGroupNode - a resizable tinted frame
//           that sits behind the nodes it groups.
//
//  A Node with no ports still draws as a small titled box, which is not what a
//  group frame is for, so this is a plain GraphElement instead: it carries its own
//  size and is pushed to the back of the content container so ordinary nodes draw
//  on top of it and stay clickable.
//
//  The resize grip is built from pointer events rather than the GraphView resizer
//  helper: moving is already handled by the view's SelectionDragger, so the grip
//  is the only piece missing, and hand-rolling it keeps this file on core
//  UIElements types instead of the experimental namespace's internals.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor.Experimental.GraphView;
using UnityEngine.UIElements;
using UniverseCreator.Nodes.Utilities;

namespace UniverseCreator.Editor.GraphUI
{
    public class UCCommentGroupView : GraphElement
    {
        public CommentGroupNode Node { get; private set; }

        private readonly UCGraphView owner;
        private readonly TextField titleField;
        private readonly TextField bodyField;

        // Backing store for SetPosition/GetPosition. Reading it back out of the
        // resolved style would return NaN until the first layout pass, and
        // GraphView calls GetPosition() while framing a selection.
        private Rect frame;

        private bool resizing;
        private bool isSelected;
        private Vector2 resizeStart;
        private Vector2 sizeStart;

        private const float MinWidth = 160f;
        private const float MinHeight = 90f;

        // Matches the selection border the node views use, so a selected frame and a
        // selected node read the same way.
        private static readonly Color SelectedBorder = new Color(0.94f, 0.63f, 0.13f);

        public UCCommentGroupView(CommentGroupNode node, UCGraphView owner)
        {
            this.Node = node;
            this.owner = owner;

            AddToClassList("uc-comment-group");

            // Movable is what lets the view's SelectionDragger drag this frame;
            // Resizable is deliberately absent because the grip below does that job.
            capabilities |= Capabilities.Selectable | Capabilities.Movable |
                            Capabilities.Deletable | Capabilities.Copiable; // UC-T05-07: GraphView forwards selected frames to the shared clipboard serializer.

            ApplyTint();

            titleField = new TextField { value = node.title ?? "Comment" };
            titleField.AddToClassList("uc-comment-title");
            titleField.RegisterValueChangedCallback(evt =>
            {
                Node.title = evt.newValue;
                owner.MarkGraphDirty();
            });
            Add(titleField);

            bodyField = new TextField { value = node.body ?? string.Empty, multiline = true };
            bodyField.AddToClassList("uc-comment-body");
            bodyField.RegisterValueChangedCallback(evt =>
            {
                Node.body = evt.newValue;
                owner.MarkGraphDirty();
            });
            Add(bodyField);

            // Typing in the frame must not drag it, and a click on the text should
            // place a caret rather than start a marquee selection.
            titleField.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            bodyField.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());

            Add(BuildResizeGrip());

            SetPosition(new Rect(node.editorPosition, node.size));
        }

        // Selection has to be drawn from here rather than from a USS :selected rule.
        // ApplyTint writes the border colour as an inline style, and inline styles
        // outrank stylesheet rules, so a rule in the USS would be silently dead and
        // a selected frame would look identical to an unselected one.
        public override void OnSelected()
        {
            base.OnSelected();
            isSelected = true;
            SetBorderColor(SelectedBorder);
        }

        public override void OnUnselected()
        {
            base.OnUnselected();
            isSelected = false;
            ApplyTint();
        }

        private void ApplyTint()
        {
            Color tint = Node.tint;
            style.backgroundColor = new StyleColor(tint);

            style.borderTopWidth = 1f;
            style.borderBottomWidth = 1f;
            style.borderLeftWidth = 1f;
            style.borderRightWidth = 1f;

            // Selection outranks the tint, so re-tinting while selected must not
            // wipe the highlight - Refresh() calls through here after an undo.
            if (isSelected)
            {
                SetBorderColor(SelectedBorder);
                return;
            }

            SetBorderColor(new Color(tint.r, tint.g, tint.b, 0.85f));
        }

        private void SetBorderColor(Color c)
        {
            style.borderTopColor = c;
            style.borderBottomColor = c;
            style.borderLeftColor = c;
            style.borderRightColor = c;
        }

        private VisualElement BuildResizeGrip()
        {
            VisualElement grip = new VisualElement();
            grip.AddToClassList("uc-comment-resizer");

            grip.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;

                resizing = true;
                resizeStart = ContentPoint(evt.position);
                sizeStart = new Vector2(frame.width, frame.height);

                grip.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });

            grip.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!resizing) return;

                // Both points are converted into the parent's local space, which is
                // the same content space the frame is sized in. Taking the delta
                // there cancels the zoom factor out instead of having to divide it
                // back off a panel-space delta.
                Vector2 delta = ContentPoint(evt.position) - resizeStart;

                SetPosition(new Rect(frame.x, frame.y,
                                     sizeStart.x + delta.x,
                                     sizeStart.y + delta.y));
                evt.StopPropagation();
            });

            grip.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!resizing) return;

                resizing = false;
                grip.ReleasePointer(evt.pointerId);
                owner.MarkGraphDirty();
                evt.StopPropagation();
            });

            return grip;
        }

        // Panel space -> the content space this frame's left/top/width/height live
        // in. hierarchy.parent is the GraphView's contentViewContainer, so its local
        // space already has the zoom and pan folded out.
        private Vector2 ContentPoint(Vector3 panelPosition)
        {
            VisualElement space = hierarchy.parent ?? this;
            return space.WorldToLocal(new Vector2(panelPosition.x, panelPosition.y));
        }

        public override void SetPosition(Rect newPos)
        {
            frame = new Rect(newPos.x, newPos.y,
                             Mathf.Max(MinWidth, newPos.width),
                             Mathf.Max(MinHeight, newPos.height));

            style.left = frame.x;
            style.top = frame.y;
            style.width = frame.width;
            style.height = frame.height;

            if (Node == null) return;

            // The frame only ever moves or resizes because the user did it, so the
            // authored values are written straight back - same rule as UCNodeView.
            Node.editorPosition = new Vector2(frame.x, frame.y);
            Node.size = new Vector2(frame.width, frame.height);
        }

        public override Rect GetPosition()
        {
            return frame;
        }

        /// <summary>Re-reads tint, text, and size after an undo or an external edit.</summary>
        public void Refresh()
        {
            if (Node == null) return;

            ApplyTint();
            titleField.SetValueWithoutNotify(Node.title ?? "Comment");
            bodyField.SetValueWithoutNotify(Node.body ?? string.Empty);
            SetPosition(new Rect(Node.editorPosition, Node.size));
        }
    }
}
