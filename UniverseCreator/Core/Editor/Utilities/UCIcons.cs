// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    UCIcons.cs
//  Purpose: Central icon lookup for every Universe Creator editor surface.
//
//  MIXED SOURCES
//  -------------
//  Kenney game-icons supply the toolbar/action glyphs (Resources/Icons/Dark and
//  /Light, picked by editor skin). Kenney is a game-UI pack with no terrain art,
//  so node-category icons come from Unity's built-in editor icon set via
//  EditorGUIUtility.IconContent. Both are cached; nothing loads per repaint.
//
//  Every lookup degrades to a null texture rather than throwing, so a missing
//  file shows a text-only button instead of breaking the window.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor
{
    public static class UCIcons
    {
        // ---- Kenney action glyph names (Resources/Icons/<skin>/<name>.png) ----
        public const string Generate       = "Action_Generate";
        public const string GenerateAll    = "Action_GenerateAll";
        public const string Stop           = "Action_Stop";
        public const string Pause          = "Action_Pause";
        public const string Reset          = "Action_Reset";
        public const string StepBack       = "Action_StepBack";
        public const string StepForward    = "Action_StepForward";
        public const string ToggleEnabled  = "Action_ToggleEnabled";

        public const string Save           = "Action_Save";
        public const string Load           = "Action_Load";
        public const string Import         = "Action_Import";
        public const string Export         = "Action_Export";
        public const string GraphIcon      = "Action_Graph";
        public const string CombineIcon    = "Action_Combine";

        public const string Add            = "Action_Add";
        public const string Remove         = "Action_Remove";
        public const string Close          = "Action_Close";
        public const string Apply          = "Action_Apply";
        public const string Delete         = "Action_Delete";
        public const string DeleteHover    = "Action_DeleteHover";
        public const string Lock           = "Action_Lock";
        public const string Unlock         = "Action_Unlock";
        public const string Favorite       = "Action_Favorite";
        public const string Seed           = "Action_Seed";
        public const string Undo           = "Action_Undo";

        public const string Settings       = "Action_Settings";
        public const string Tools          = "Action_Tools";
        public const string NodeList       = "Action_NodeList";
        public const string NodePalette    = "Action_NodePalette";
        public const string Menu           = "Action_Menu";
        public const string PreviewMode    = "Action_PreviewMode";

        public const string Warning        = "Status_Warning";
        public const string Error          = "Status_Error";
        public const string Info           = "Status_Info";
        public const string Help           = "Status_Help";

        public const string ZoomIn         = "View_ZoomIn";
        public const string ZoomOut        = "View_ZoomOut";
        public const string ZoomReset      = "View_ZoomReset";
        public const string Zoom           = "View_Zoom";
        public const string Expand         = "View_Expand";
        public const string Collapse       = "View_Collapse";
        public const string Frame          = "View_Frame";
        public const string PanH           = "View_PanH";
        public const string PanV           = "View_PanV";
        public const string ArrowUp        = "View_ArrowUp";
        public const string ArrowDown      = "View_ArrowDown";
        public const string ArrowLeft      = "View_ArrowLeft";
        public const string ArrowRight     = "View_ArrowRight";
        public const string TriUp          = "View_TriUp";
        public const string TriDown        = "View_TriDown";
        public const string TriLeft        = "View_TriLeft";
        public const string LodLow         = "View_LOD_Low";
        public const string LodMed         = "View_LOD_Med";
        public const string LodHigh        = "View_LOD_High";

        // ---- Cursors (Resources/Icons/Cursors/<name>.png) ----
        public const string CursorPan      = "Cursor_Pan";
        public const string CursorPanning  = "Cursor_Panning";
        public const string CursorSelect   = "Cursor_Select";
        public const string CursorInvalid  = "Cursor_Invalid";
        public const string CursorReroute  = "Cursor_Reroute";
        public const string CursorBusy     = "Cursor_Busy";
        public const string CursorPicker   = "Cursor_Picker";
        public const string CursorTarget   = "Cursor_Target";
        public const string CursorCross    = "Cursor_Crosshair";

        private const string IconRoot   = "Icons/";
        private const string CursorRoot = "Icons/Cursors/";

        private static readonly Dictionary<string, Texture2D> cache =
            new Dictionary<string, Texture2D>();

        private static readonly Dictionary<string, GUIContent> contentCache =
            new Dictionary<string, GUIContent>();

        private static bool lastSkinWasPro = EditorGUIUtility.isProSkin;

        /// <summary>
        /// Loads a Kenney action glyph for the active editor skin. White art on the
        /// dark skin, black art on the light skin.
        /// </summary>
        public static Texture2D Get(string iconName)
        {
            if (string.IsNullOrEmpty(iconName)) return null;

            // Editor skin can change mid-session; drop the cache when it does.
            if (EditorGUIUtility.isProSkin != lastSkinWasPro)
            {
                lastSkinWasPro = EditorGUIUtility.isProSkin;
                cache.Clear();
                contentCache.Clear();
            }

            Texture2D tex;
            if (cache.TryGetValue(iconName, out tex)) return tex;

            string skin = EditorGUIUtility.isProSkin ? "Dark/" : "Light/";
            tex = Resources.Load<Texture2D>(IconRoot + skin + iconName);

            // Fall back to the other skin so a half-populated set still renders.
            if (tex == null)
                tex = Resources.Load<Texture2D>(IconRoot + (EditorGUIUtility.isProSkin ? "Light/" : "Dark/") + iconName);

            cache[iconName] = tex;
            return tex;
        }

        /// <summary>Loads a cursor texture. Skin-independent.</summary>
        public static Texture2D GetCursor(string cursorName)
        {
            if (string.IsNullOrEmpty(cursorName)) return null;

            string key = "cursor:" + cursorName;
            Texture2D tex;
            if (cache.TryGetValue(key, out tex)) return tex;

            tex = Resources.Load<Texture2D>(CursorRoot + cursorName);
            cache[key] = tex;
            return tex;
        }

        /// <summary>
        /// Icon button content with tooltip. Falls back to a text label when the
        /// texture is missing, so the control stays usable and identifiable.
        /// </summary>
        public static GUIContent Content(string iconName, string tooltip, string fallbackText = null)
        {
            string key = iconName + "|" + tooltip + "|" + (fallbackText ?? "");
            GUIContent gc;
            if (contentCache.TryGetValue(key, out gc)) return gc;

            Texture2D tex = Get(iconName);
            gc = tex != null
                ? new GUIContent(tex, tooltip)
                : new GUIContent(fallbackText ?? iconName, tooltip);

            contentCache[key] = gc;
            return gc;
        }

        // ------------------------------------------------------------------
        //  Node category icons - Unity built-ins
        // ------------------------------------------------------------------

        /// <summary>
        /// Built-in Unity editor icon name per node category. These carry the
        /// terrain/PCG meaning Kenney's game-UI pack does not have.
        /// </summary>
        public static string BuiltinNameFor(NodeCategory category)
        {
            switch (category)
            {
                case NodeCategory.Creator:  return "d_TerrainInspector.TerrainToolRaise";
                case NodeCategory.Modifier: return "d_TerrainInspector.TerrainToolSmoothHeight";
                case NodeCategory.Selector: return "d_FilterByType";
                case NodeCategory.Combiner: return "d_Grid.MoveTool";
                case NodeCategory.Scatter:  return "d_TerrainInspector.TerrainToolPlants";
                case NodeCategory.Output:   return "d_Terrain Icon";
                case NodeCategory.Utility:  return "d_CustomTool";
                case NodeCategory.Biome:    return "d_TerrainInspector.TerrainToolSplat";
                default:                    return "d_CustomTool";
            }
        }

        /// <summary>
        /// Loads a Unity built-in editor icon. Built-in names differ across Unity
        /// versions, so a miss returns null and callers draw text instead.
        /// </summary>
        public static Texture2D Builtin(string builtinName)
        {
            if (string.IsNullOrEmpty(builtinName)) return null;

            string key = "builtin:" + builtinName;
            Texture2D tex;
            if (cache.TryGetValue(key, out tex)) return tex;

            // IconContent logs an error for unknown names; suppress so a version
            // difference doesn't spam the console every repaint.
            bool prevLogging = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                GUIContent content = EditorGUIUtility.IconContent(builtinName);
                tex = content != null ? content.image as Texture2D : null;
            }
            catch
            {
                tex = null;
            }
            finally
            {
                Debug.unityLogger.logEnabled = prevLogging;
            }

            // Retry without the dark-skin "d_" prefix on the light skin.
            if (tex == null && builtinName.StartsWith("d_"))
                return Builtin(builtinName.Substring(2));

            cache[key] = tex;
            return tex;
        }

        /// <summary>Category icon for node headers and the add-node menu.</summary>
        public static Texture2D CategoryIcon(NodeCategory category)
        {
            return Builtin(BuiltinNameFor(category));
        }

        /// <summary>Clears both caches. Called on domain reload and skin change.</summary>
        public static void ClearCache()
        {
            cache.Clear();
            contentCache.Clear();
        }
    }
}
