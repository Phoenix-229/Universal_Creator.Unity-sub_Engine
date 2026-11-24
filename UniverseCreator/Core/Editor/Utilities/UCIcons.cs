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
//  EditorGUIUtility.IconContent. The Opt_/Pin_/Ramp_ glyphs in the same folders
//  are rasterised from an MIT-licensed PCG icon set - see ATTRIBUTION.md beside
//  the icons, and Tools~/IconPipeline for the converter that produced them.
//  Everything is cached; nothing loads per repaint.
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

        // ------------------------------------------------------------------
        //  Option glyphs - Resources/Icons/<skin>/Opt_<name>.png
        //
        //  These are the small pictograms that belong beside an enum popup or
        //  in a toolbar of mutually exclusive choices, where a word like
        //  "Ceil" is slower to read than the shape it means. Kenney has no art
        //  for any of this; see ATTRIBUTION.md next to the icons.
        // ------------------------------------------------------------------

        // Rounding and ordering.
        public const string OptCeil        = "Opt_Ceil";
        public const string OptFloor       = "Opt_Floor";
        public const string OptRound       = "Opt_Round";
        public const string OptAscending   = "Opt_Ascending";
        public const string OptDescending  = "Opt_Descending";

        // Axes and winding.
        public const string OptX           = "Opt_X";
        public const string OptY           = "Opt_Y";
        public const string OptZ           = "Opt_Z";
        public const string OptCW          = "Opt_CW";
        public const string OptCCW         = "Opt_CCW";

        // Bounds and reference points.
        public const string OptBounds        = "Opt_Bounds";
        public const string OptScaledBounds  = "Opt_ScaledBounds";
        public const string OptDensityBounds = "Opt_DensityBounds";
        public const string OptCenter        = "Opt_Center";
        public const string OptDistCenter    = "Opt_Dist_Center";
        public const string OptDistBox       = "Opt_Dist_BoxBounds";
        public const string OptDistSphere    = "Opt_Dist_SphereBounds";

        // Fitting.
        public const string OptFitNone     = "Opt_Fit_None";
        public const string OptFitFill     = "Opt_Fit_Fill";
        public const string OptFitMin      = "Opt_Fit_Min";
        public const string OptFitMax      = "Opt_Fit_Max";
        public const string OptFitAverage  = "Opt_Fit_Average";

        // Snapping.
        public const string OptSnap        = "Opt_Snap";
        public const string OptNoSnapping  = "Opt_NoSnapping";
        public const string OptSnapOffset  = "Opt_SnapOffset";
        public const string OptSnapResult  = "Opt_SnapResult";

        // Spline / range endpoint inclusivity.
        public const string OptClosedClosed = "Opt_ClosedClosed";
        public const string OptClosedOpen   = "Opt_ClosedOpen";
        public const string OptOpenClosed   = "Opt_OpenClosed";
        public const string OptOpenOpen     = "Opt_OpenOpen";

        // Value sources and states.
        public const string OptAttribute     = "Opt_Attribute";
        public const string OptDataAttribute = "Opt_DataAttribute";
        public const string OptConstant      = "Opt_Constant";
        public const string OptNumeric       = "Opt_Numeric";
        public const string OptText          = "Opt_Text";
        public const string OptEnabled       = "Opt_Enabled";
        public const string OptDisabled      = "Opt_Disabled";
        public const string OptDefault       = "Opt_Default";
        public const string OptUnchanged     = "Opt_Unchanged";
        public const string OptAll           = "Opt_All";
        public const string OptInclude       = "Opt_Include";
        public const string OptExclude       = "Opt_Exclude";

        // Missing-data policy.
        public const string OptMissingPass   = "Opt_MissingData_Pass";
        public const string OptMissingFail   = "Opt_MissingData_Fail";
        public const string OptMissingError  = "Opt_MissingData_Error";

        // Materials, weighting and rebuild actions.
        public const string OptSingleMat      = "Opt_SingleMat";
        public const string OptMultiMat       = "Opt_MultiMat";
        public const string OptNormalizeWeight = "Opt_NormalizeWeight";
        public const string OptRebuild        = "Opt_Rebuild";
        public const string OptSettings       = "Opt_Settings";
        public const string OptScene          = "Opt_Scene";

        // ---- Gradient / curve widget parts ----
        public const string RampKey        = "Ramp_Key";
        public const string RampSlider     = "Ramp_Slider";

        private const string OptPrefix     = "Opt_";
        private const string PinPrefix     = "Pin_";

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

        /// <summary>
        /// Loads the product's own brand mark, the 128 px square cut of the banner.
        /// Skin-independent: the mark is artwork, not a glyph, so there is no light
        /// and dark variant to choose between.
        ///
        /// Cached in the same dictionary as everything else under its own key. This
        /// is loaded by an inspector header that repaints several times a second
        /// while the mouse moves over it, and a Resources.Load per repaint is a cost
        /// paid forever for a value that never changes. A null is cached too, so a
        /// missing brand file costs one failed lookup rather than one per frame.
        /// </summary>
        public static Texture2D Brand()
        {
            const string key = "brand:UniverseCreator_Icon";
            Texture2D tex;
            if (cache.TryGetValue(key, out tex)) return tex;

            tex = Resources.Load<Texture2D>(UCBrandImages.UniverseCreatorIconResourceName);
            cache[key] = tex;
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

        // ------------------------------------------------------------------
        //  Port type glyphs
        // ------------------------------------------------------------------

        /// <summary>
        /// Icon stem for a port's data type, or null when the type has no art.
        /// </summary>
        /// <remarks>
        /// Kept as a switch on PortType rather than a name-derived lookup so a
        /// new port type is a compile-time prompt to decide what it looks like,
        /// instead of silently resolving to a missing file at runtime.
        /// </remarks>
        public static string PinNameFor(PortType type, PortDirection direction)
        {
            string side = direction == PortDirection.Input ? "In_" : "Out_";
            switch (type)
            {
                // Matrix is the heightfield itself. It flows through nearly every
                // connection, so marking it would add ink to every port without
                // distinguishing anything - the unmarked port IS the height port.
                case PortType.Matrix: return null;
                default:              return null;
            }
        }

        /// <summary>Port type glyph, or null when the type is drawn unmarked.</summary>
        public static Texture2D PinIcon(PortType type, PortDirection direction)
        {
            string stem = PinNameFor(type, direction);
            return stem == null ? null : Get(PinPrefix + stem);
        }

        /// <summary>
        /// Option glyph by bare name, e.g. Opt("Fit_Fill"). Prefer the constants
        /// above; this overload exists for names built from enum values.
        /// </summary>
        public static Texture2D Opt(string bareName)
        {
            if (string.IsNullOrEmpty(bareName)) return null;
            return Get(bareName.StartsWith(OptPrefix) ? bareName : OptPrefix + bareName);
        }

        /// <summary>
        /// Content for an enum value drawn as an icon-plus-tooltip toggle. Falls
        /// back to the value's own name when no glyph exists for it, so adding an
        /// enum case never produces an invisible button.
        /// </summary>
        public static GUIContent OptContent(string bareName, string tooltip)
        {
            string stem = bareName != null && bareName.StartsWith(OptPrefix)
                ? bareName
                : OptPrefix + bareName;
            return Content(stem, tooltip, bareName);
        }

        /// <summary>Clears both caches. Called on domain reload and skin change.</summary>
        public static void ClearCache()
        {
            cache.Clear();
            contentCache.Clear();
        }
    }
}
