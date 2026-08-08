// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GroupDrawer.cs
//  Purpose: Draws comment group frames behind the nodes they contain.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;

namespace UniverseCreator.Editor.Drawing
{
    /// <summary>
    /// Draws comment group frames behind the nodes they contain.
    /// </summary>
    public static class GroupDrawer
    {
        /// <summary>
        /// Draws a CommentGroupNode as a tinted backdrop frame with a title bar.
        /// Drawn before nodes so it sits behind the graph.
        /// </summary>
        public static void DrawGroup(UniverseCreator.Nodes.Utilities.CommentGroupNode group, Vector2 position)
        {
            if (group == null) return;

            Rect frame = new Rect(position, group.size);
            Color tint = group.tint;

            // Frame backdrop
            EditorGUI.DrawRect(frame, tint);

            // Title bar darker
            Rect titleBar = new Rect(frame.x, frame.y, frame.width, 24f);
            EditorGUI.DrawRect(titleBar, new Color(tint.r * 0.7f, tint.g * 0.7f, tint.b * 0.7f, tint.a * 2f));

            // Title text
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel);
            titleStyle.normal.textColor = Color.white;
            titleStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(titleBar, group.title, titleStyle);
        }
    }
}
