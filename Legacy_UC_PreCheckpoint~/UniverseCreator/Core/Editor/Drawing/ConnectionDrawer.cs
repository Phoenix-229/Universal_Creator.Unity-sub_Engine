// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    ConnectionDrawer.cs
//  Purpose: Draws cubic bezier curves between ports, including the in-progress drag wire.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;

namespace UniverseCreator.Editor.Drawing
{
    /// <summary>
    /// Draws cubic bezier curves between ports, including the in-progress drag wire.
    /// </summary>
    public static class ConnectionDrawer
    {
        /// <summary>
        /// Draws a cubic bezier from start to end with horizontal tangents.
        /// Tangent length 50 units reads as smooth for most node separations.
        /// </summary>
        public static void DrawBezier(Vector2 start, Vector2 end, Color color, float width = 3f)
        {
            Vector2 startTangent = start + Vector2.right * 50f;
            Vector2 endTangent = end + Vector2.left * 50f;
            Handles.DrawBezier(start, end, startTangent, endTangent, color, null, width);
        }
    }
}
