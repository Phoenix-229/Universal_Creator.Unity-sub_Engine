// -----------------------------------------------------------------------------
//  Universe Creator - Plugin System  (PLG-6)
//  File:    UCConnectedPluginsWindow.cs
//  Purpose: The window that answers "is the plugin connected?" - our equivalent of
//           Unreal's Plugin Browser (Edit > Plugins), modernised for Unity.
//
//           Window > Universe Creator > Connected Plugins
//
//  WHAT IT SHOWS, AND WHY EACH COLUMN EARNS ITS PLACE
//  --------------------------------------------------
//    * Identity (name / version / author / category) - straight off the descriptor,
//      so what you read here is what the plugin declared about itself.
//    * Which CONTRACTS it implements (Nodes / Shapes / Bake / Data) - this is the
//      real answer to "how is it plugged in", and it is derived from the registered
//      implementations, not from anything the plugin merely claims.
//    * CONNECTED state - a green dot means registered AND enabled in this session,
//      which is exactly the set the bake iterates. Grey means discovered but
//      disabled, so its code is loaded and contributing nothing.
//    * An Enabled toggle per plugin, mirroring Unreal's per-plugin checkbox.
//
//  The EMPTY STATE is a first-class message, not an error: with no plugin connected
//  Universe Creator runs as the standalone sub-engine, which is the supported
//  default, and the window says so rather than looking broken.
//
//  Editor-only, read-mostly: it reads UCPluginRegistry and writes nothing a bake
//  depends on except the enable toggle the author sets deliberately. The toggle is
//  persisted in EditorPrefs (per machine, not in the scene), so turning a plugin off
//  does not edit anyone's world.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UniverseCreator.Plugins;

namespace UniverseCreator.Editor.Windows
{
    public class UCConnectedPluginsWindow : EditorWindow
    {
        private const string PrefPrefix = "UniverseCreator.Plugin.Enabled.";

        private Vector2 scroll;
        private string search = string.Empty;

        [MenuItem("Window/Universe Creator/Connected Plugins", false, 320)]
        public static void Open()
        {
            UCConnectedPluginsWindow w = GetWindow<UCConnectedPluginsWindow>("UC Plugins");
            w.minSize = new Vector2(460f, 240f);
            w.ApplyPersistedToggles();
            w.Show();
        }

        /// <summary>
        /// EditorPrefs key for one plugin's enabled state. Keyed by the plugin's id (its
        /// assembly or type name), so a plugin keeps its state across domain reloads.
        /// </summary>
        public static string PrefKeyFor(string pluginId) { return PrefPrefix + pluginId; }

        /// <summary>
        /// Pushes each discovered plugin's persisted toggle into the registry, defaulting to the
        /// descriptor's own EnabledByDefault when the author has never set one. Public and static
        /// so the editor can restore the author's choices without the window being open.
        /// </summary>
        public static void ApplyPersistedTogglesStatic()
        {
            foreach (UCPluginInfo p in UCPluginRegistry.All)
            {
                bool on = EditorPrefs.GetBool(PrefKeyFor(p.Id), p.EnabledByDefault);
                UCPluginRegistry.SetEnabled(p.Id, on);
            }
        }

        private void ApplyPersistedToggles() { ApplyPersistedTogglesStatic(); }

        private void OnGUI()
        {
            DrawToolbar();

            var all = UCPluginRegistry.All;
            int shown = 0;

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < all.Count; i++)
            {
                UCPluginInfo p = all[i];
                if (!Matches(p, search)) continue;
                shown++;
                DrawPluginRow(p);
            }
            EditorGUILayout.EndScrollView();

            if (all.Count == 0) DrawEmptyState();
            else if (shown == 0) EditorGUILayout.HelpBox("No plugin matches \"" + search + "\".", MessageType.None);

            DrawFooter(all.Count);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(62f)))
            {
                // A rescan is how a plugin added since this session started becomes visible
                // without restarting the editor (Unreal asks for a restart; a Unity domain
                // reload already happened, so a cache drop is enough).
                UCPluginRegistry.Invalidate();
                UniverseCreator.Editor.Utilities.NodeCatalog.Invalidate();
                ApplyPersistedToggles();
            }
            GUILayout.FlexibleSpace();
            search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.Width(180f));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPluginRow(UCPluginInfo p)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();

            // CONNECTED dot. Green = registered and enabled this session, which IS the set the
            // bake iterates; grey = discovered but switched off.
            Color prev = GUI.color;
            GUI.color = p.Enabled ? new Color(0.35f, 0.85f, 0.40f) : new Color(0.45f, 0.45f, 0.48f);
            GUILayout.Label("●", GUILayout.Width(16f));
            GUI.color = prev;

            string title = string.IsNullOrEmpty(p.FriendlyName) ? p.Id : p.FriendlyName;
            if (!string.IsNullOrEmpty(p.Version)) title += "  " + p.Version;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            bool on = EditorGUILayout.ToggleLeft("Enabled", p.Enabled, GUILayout.Width(74f));
            if (on != p.Enabled)
            {
                UCPluginRegistry.SetEnabled(p.Id, on);
                EditorPrefs.SetBool(PrefKeyFor(p.Id), on);

                // A node-providing plugin changes what the add-node menu offers, so the catalog
                // must be rebuilt or the menu would keep offering a disabled plugin's nodes.
                if (p.ProvidesNodes) UniverseCreator.Editor.Utilities.NodeCatalog.Invalidate();
            }

            EditorGUILayout.EndHorizontal();

            // What it plugs into - derived from registered implementations, not from claims.
            EditorGUILayout.LabelField("Connects via", ContractSummary(p));

            if (!string.IsNullOrEmpty(p.Category))
                EditorGUILayout.LabelField("Category", p.Category);
            if (!string.IsNullOrEmpty(p.Author))
                EditorGUILayout.LabelField("Author", p.Author);
            if (!string.IsNullOrEmpty(p.Description))
                EditorGUILayout.LabelField("Description", p.Description);

            EditorGUILayout.LabelField("Id", p.Id);

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2f);
        }

        /// <summary>
        /// Human summary of the contracts a plugin actually registered. Exposed for the gate,
        /// which pins that a plugin implementing nothing reads as such rather than as connected.
        /// </summary>
        public static string ContractSummary(UCPluginInfo p)
        {
            if (p == null) return "nothing";

            string s = string.Empty;
            if (p.ProvidesNodes) s += "Nodes, ";
            if (p.ProvidesTerrainShapes) s += "Terrain shapes, ";
            if (p.HasBakeHooks) s += "Bake hooks, ";
            if (p.ConsumesWorldData) s += "World data, ";

            if (s.Length == 0) return "nothing (descriptor only)";
            return s.Substring(0, s.Length - 2);
        }

        private static bool Matches(UCPluginInfo p, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            string q = query.ToLowerInvariant();
            return (p.FriendlyName != null && p.FriendlyName.ToLowerInvariant().Contains(q))
                || (p.Category != null && p.Category.ToLowerInvariant().Contains(q))
                || (p.Author != null && p.Author.ToLowerInvariant().Contains(q))
                || (p.Id != null && p.Id.ToLowerInvariant().Contains(q));
        }

        private static void DrawEmptyState()
        {
            EditorGUILayout.HelpBox(
                "No plugins connected.\n\n" +
                "Universe Creator is running as the standalone sub-engine, which is the normal " +
                "default - nothing is wrong.\n\n" +
                "A system connects by implementing a Universe Creator plugin contract and marking " +
                "itself with [UCPlugin] (on its assembly, or on one class if it has no assembly " +
                "definition of its own). It needs no changes to Universe Creator, and it is not a " +
                "child object in the world and not a procedural node.",
                MessageType.Info);
        }

        private static void DrawFooter(int count)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            int enabled = 0;
            foreach (UCPluginInfo p in UCPluginRegistry.All) if (p.Enabled) enabled++;
            EditorGUILayout.LabelField(count + " discovered, " + enabled + " connected", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }
    }
}
