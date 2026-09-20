using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    /// <summary>
    /// Menu toggle for the water debug bounds boxes (see <see cref="UCWaterGizmos"/>).
    /// The choice is remembered in EditorPrefs and pushed into the runtime flag on load
    /// so it survives a domain reload and matches the checkmark.
    /// </summary>
    [InitializeOnLoad]
    public static class UCWaterGizmoMenu
    {
        private const string Pref = "UniverseCreator.Water.ShowDebugBounds";
        private const string Path = "Tools/Universe Creator/Water/Show Debug Bounds";

        static UCWaterGizmoMenu()
        {
            UCWaterGizmos.ShowDebugBounds = EditorPrefs.GetBool(Pref, false);
        }

        [MenuItem(Path, false, 930)]
        private static void Toggle()
        {
            bool on = !UCWaterGizmos.ShowDebugBounds;
            UCWaterGizmos.ShowDebugBounds = on;
            EditorPrefs.SetBool(Pref, on);
            SceneView.RepaintAll();
        }

        [MenuItem(Path, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(Path, UCWaterGizmos.ShowDebugBounds);
            return true;
        }
    }
}
