// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    TextureGraphWindow.cs
//  Purpose: Texture graph editor. Stub window reserved for Module 2.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>
    /// Texture graph editor. Stub window reserved for Module 2.
    /// </summary>
    /// <remarks>
    /// SKELETON (Module 1, Step 1). Implemented in Module 1, Step 6.
    /// </remarks>
    public class TextureGraphWindow : EditorWindow
    {
        // TODO (Module 1, Step 6): MODULE 1 STUB - opens and shows a placeholder message only.

        public static void OpenWindow()
        {
            TextureGraphWindow window = GetWindow<TextureGraphWindow>("Universe Creator - Texture Graph");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(20);
            EditorGUILayout.HelpBox(
                "Texture Graph Editor\n\n" +
                "This window is reserved for Module 2 (Texturing & Materials). " +
                "The texture graph system will allow you to author multi-layer terrain " +
                "materials with height-based blending, slope masks, and biome-aware splatmaps.",
                MessageType.Info
            );
        }
    }
}
