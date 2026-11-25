// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UCInspectorUI.cs
//  Purpose: Shared inspector chrome: coloured section headers, a header bar, and
//           tinted action buttons.
//
//  Correction C-12. Colour is carried by a thin accent strip and a tinted
//  background rather than by coloured label text, because label colour is the one
//  thing that changes between the dark and light editor skins - an accent strip
//  reads the same on both.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    public static class UCInspectorUI
    {
        // Section accents. Muted on purpose: an inspector full of saturated bars
        // is harder to scan than one with a few quiet ones.
        public static readonly Color HeaderTeal   = new Color(0.20f, 0.65f, 0.62f);
        public static readonly Color HeaderGreen  = new Color(0.28f, 0.70f, 0.32f);
        public static readonly Color HeaderBlue   = new Color(0.27f, 0.52f, 0.85f);
        public static readonly Color HeaderAmber  = new Color(0.85f, 0.62f, 0.18f);
        public static readonly Color HeaderPurple = new Color(0.55f, 0.42f, 0.80f);
        public static readonly Color HeaderGrey   = new Color(0.50f, 0.52f, 0.55f);

        // Button tints multiply GUI.backgroundColor, so they stay light enough for
        // the button label to remain readable on both skins.
        public static readonly Color ButtonBlue  = new Color(0.62f, 0.78f, 1.00f);
        public static readonly Color ButtonGreen = new Color(0.62f, 1.00f, 0.68f);
        public static readonly Color ButtonAmber = new Color(1.00f, 0.85f, 0.55f);
        public static readonly Color ButtonRed   = new Color(1.00f, 0.62f, 0.58f);

        private static Texture2D pixel;

        /// <summary>UC repair 31: shows assignment guidance when any selected component belongs to an unconfigured world.</summary>
        /// <param name="selection">Inspector targets; standalone managers and worlds with an assigned empty graph remain available.</param>
        /// <returns>True when normal settings must be withheld for this GUI event; false when no attached world is missing its graph.</returns>
        /// <remarks>A single selection shares parent assignment/Undo/dirty/auto-generation. Mixed selections never assign multiple worlds implicitly.</remarks>
        internal static bool DrawMissingTerrainGraph(Object[] selection)
        {
            UniverseCreatorObject missing = null;
            foreach (Object selected in selection)
            {
                if (!(selected is Component component)) continue;
                var world = component.GetComponentInParent<UniverseCreatorObject>(true);
                if (world != null && world.graph == null) { missing = world; break; }
            }
            if (missing == null) return false;
            HeaderBar("Universe Creator", "d_Terrain Icon", HeaderTeal, "terrain graph required");
            if (selection.Length != 1)
            {
                EditorGUILayout.HelpBox("This selection includes a world with no terrain graph. Select one world to assign its graph before editing these settings.", MessageType.Info);
                return true;
            }
            EditorGUILayout.HelpBox("Assign the parent world's terrain graph to reveal these settings. Saved settings and terrain are retained.", MessageType.Info);
            Graph picked = (Graph)EditorGUILayout.ObjectField(new GUIContent("Terrain Graph", "Shared parent graph. An assigned empty graph reveals controls; generation follows Auto Regenerate."),
                missing.graph, typeof(Graph), false);
            if (picked != missing.graph) UniverseCreatorInspector.AssignGraph(missing, picked);
            return true;
        }

        /// <summary>Title strip for the top of an inspector.</summary>
        public static void HeaderBar(string title, string builtinIcon, Color accent, string status)
        {
            Rect row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.Height(24));
            AccentStrip(row, accent);

            Texture2D icon = UCIcons.Builtin(builtinIcon);
            if (icon != null) GUILayout.Label(icon, GUILayout.Width(20), GUILayout.Height(20));

            GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
            style.fontSize = 12;
            GUILayout.Label(title, style);

            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(status))
                GUILayout.Label(status, EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Foldout header with a colour accent down its left edge.</summary>
        public static bool Section(bool state, string title, Color accent)
        {
            Rect row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            AccentStrip(row, accent);

            GUILayout.Space(4);
            bool next = EditorGUILayout.Foldout(state, title, true, EditorStyles.foldoutHeader);

            EditorGUILayout.EndHorizontal();
            return next;
        }

        /// <summary>Small filled status pill. Carries its colour in the background
        /// rather than in the text, so it reads on both editor skins.</summary>
        public static void Badge(string label, Color fill, float width)
        {
            GUIStyle style = new GUIStyle(EditorStyles.miniLabel);
            style.alignment = TextAnchor.MiddleCenter;

            Rect rect = GUILayoutUtility.GetRect(new GUIContent(label), style,
                                                 GUILayout.Width(width), GUILayout.Height(16f));

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                EnsurePixel();
                Color previous = GUI.color;
                // Alpha rather than a solid fill: the badge tints the panel it sits
                // on instead of punching a flat block through it, which keeps it
                // legible whichever skin supplies the backdrop.
                GUI.color = new Color(fill.r, fill.g, fill.b, 0.35f);
                GUI.DrawTexture(rect, pixel);
                GUI.color = previous;
            }

            GUI.Label(rect, label, style);
        }

        /// <summary>Action button tinted by multiplying the GUI background colour.</summary>
        public static bool TintedButton(string label, Color tint, float height = 22f)
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            bool clicked = GUILayout.Button(label, GUILayout.Height(height));
            GUI.backgroundColor = previous;
            return clicked;
        }

        /// <summary>Tinted button with an icon.</summary>
        public static bool TintedButton(GUIContent content, Color tint, float height = 22f)
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            bool clicked = GUILayout.Button(content, GUILayout.Height(height));
            GUI.backgroundColor = previous;
            return clicked;
        }

        /// <summary>Draws the 3px colour bar at the left edge of a row rect. Public
        /// so an inspector with its own header layout still gets the same strip.
        /// Repaint only: during layout the rect has no valid size yet.</summary>
        public static void AccentStrip(Rect row, Color accent)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (row.height <= 0f) return;

            EnsurePixel();
            Color previous = GUI.color;
            GUI.color = accent;
            GUI.DrawTexture(new Rect(row.x, row.y, 3f, row.height), pixel);
            GUI.color = previous;
        }

        private static void EnsurePixel()
        {
            if (pixel != null) return;

            pixel = new Texture2D(1, 1);
            pixel.hideFlags = HideFlags.HideAndDontSave;
            pixel.SetPixel(0, 0, Color.white);
            pixel.Apply();
        }
    }
}
