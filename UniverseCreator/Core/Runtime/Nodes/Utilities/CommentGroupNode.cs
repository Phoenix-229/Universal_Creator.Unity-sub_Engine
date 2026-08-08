// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CommentGroupNode.cs
//  Purpose: A resizable labelled frame drawn behind other nodes. Pure editor
//           annotation: no ports, no product, never evaluated.
//
//  It lives in the node list rather than a separate collection so it moves,
//  serialises, undoes, and copies with the rest of the graph for free.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Utilities
{
    [System.Serializable]
    public class CommentGroupNode : Generator
    {
        public override string NodeName { get { return "Comment Group"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "A labelled frame for organising the graph. Not evaluated."; } }

        public string title = "Comment";
        public string body = string.Empty;

        /// <summary>Frame size in graph units. Position comes from the base node rect.</summary>
        // Hidden from the reflected node body: the frame is resized by dragging its
        // corner in the graph, and a second numeric copy of the same value would
        // fight the handle for authority.
        [HideInInspector] public Vector2 size = new Vector2(320f, 200f);

        public Color tint = new Color(0.25f, 0.55f, 0.85f, 0.15f);

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            return null;
        }
    }
}
