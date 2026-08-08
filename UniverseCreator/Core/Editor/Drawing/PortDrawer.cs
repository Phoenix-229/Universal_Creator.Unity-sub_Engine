// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    PortDrawer.cs
//  Purpose: Draws port circles and resolves their colour from the port data type.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Drawing
{
    /// <summary>
    /// Draws port circles and resolves their colour from the port data type.
    /// </summary>
    public static class PortDrawer
    {
        /// <summary>
        /// Returns the colour for a given port type. PDF Section 4.2: Green (heightmap),
        /// Blue (mask), Orange (texture), Purple (scatter), White (generic). Module 1
        /// has only Matrix, so green is returned for everything in this phase.
        /// </summary>
        public static Color GetPortColor(PortType type)
        {
            return new Color(0.4f, 0.8f, 0.4f);
        }

        /// <summary>
        /// Draws a filled circle for an inlet or outlet port.
        /// </summary>
        public static void DrawPort(Rect portRect, PortType type)
        {
            EditorGUI.DrawRect(portRect, GetPortColor(type));
        }
    }
}
