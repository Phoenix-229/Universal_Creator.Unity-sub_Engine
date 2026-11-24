// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GraphAssetUtility.cs
//  Purpose: Creates, saves, and opens Graph assets. Single place that knows how a
//           graph reaches disk, so the inspector, the GameObject menu, the Project
//           Settings page and the Assets/Create menu all behave identically.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.Windows;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    public static class GraphAssetUtility
    {
        /// <summary>Folder new graphs land in unless the caller says otherwise.</summary>
        public const string DefaultFolder = "Assets/UniverseCreator/Graphs";

        // =====================================================================
        //  CREATE
        // =====================================================================

        /// <summary>
        /// Builds a template, writes it to a unique path under DefaultFolder, and
        /// returns the saved asset. Returns null only if the write failed.
        /// </summary>
        public static Graph CreateAsset(GraphTemplates.Template template, string fileName = null)
        {
            Graph graph = GraphTemplates.Create(template);
            if (graph == null) return null;

            if (string.IsNullOrEmpty(fileName))
            {
                int index = (int)template;
                fileName = (index >= 0 && index < GraphTemplates.FileNames.Length)
                    ? GraphTemplates.FileNames[index]
                    : "New Graph";
            }

            return SaveAsset(graph, DefaultFolder, fileName);
        }

        /// <summary>
        /// Writes an in-memory graph to disk. The name is uniquified, so calling
        /// this twice gives "New Graph.asset" and "New Graph 1.asset" rather than
        /// silently overwriting the first one.
        /// </summary>
        public static Graph SaveAsset(Graph graph, string folder, string fileName)
        {
            if (graph == null) return null;

            EnsureFolder(folder);

            string safeName = SanitizeFileName(fileName);
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safeName + ".asset");

            AssetDatabase.CreateAsset(graph, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            // Re-load rather than reusing the instance: CreateAsset rebinds the
            // managed object to the file, and the loaded reference is the one whose
            // GUID the object picker and prefab serialisation will agree on.
            return AssetDatabase.LoadAssetAtPath<Graph>(path);
        }

        /// <summary>Flags a graph dirty and flushes it. Call after editing nodes.</summary>
        public static void MarkDirty(Graph graph)
        {
            if (graph == null) return;
            EditorUtility.SetDirty(graph);
        }

        /// <summary>Writes every pending graph edit to disk.</summary>
        public static void SavePending()
        {
            AssetDatabase.SaveAssets();
        }

        // =====================================================================
        //  OPEN
        // =====================================================================

        /// <summary>
        /// Opens (or focuses) the Node Graph window and loads the given graph. This
        /// is the single entry point every "show me this graph" path routes through.
        /// </summary>
        public static NodeGraphWindow Open(Graph graph)
        {
            NodeGraphWindow window = EditorWindow.GetWindow<NodeGraphWindow>("Universe Creator - Node Graph");
            window.minSize = new Vector2(150, 150);
            window.SetGraph(graph);
            window.Show();
            window.Focus();
            return window;
        }

        /// <summary>
        /// Opens (or focuses) the Texture Graph window and loads the given graph.
        /// </summary>
        public static TextureGraphWindow OpenTexture(Graph graph)
        {
            TextureGraphWindow window =
                EditorWindow.GetWindow<TextureGraphWindow>("Universe Creator - Texture Graph");
            window.minSize = new Vector2(400, 300);
            window.SetGraph(graph);
            window.Show();
            window.Focus();
            return window;
        }

        /// <summary>
        /// Sends a graph to whichever of the two windows can actually edit it.
        ///
        /// One asset type, two canvases, so something has to choose. Opening a
        /// surface graph in the height window is not broken - the nodes draw and
        /// edit fine - but the toolbar has no Apply and the add-node menu offers
        /// sixty nodes none of which can connect to anything on screen. That reads
        /// as a bug even though nothing failed.
        /// </summary>
        public static void OpenAuto(Graph graph)
        {
            if (graph == null) return;

            if (LooksLikeTextureGraph(graph)) OpenTexture(graph);
            else Open(graph);
        }

        /// <summary>
        /// True when explicit supported metadata selects Texture Graph, or a legacy asset's nodes fit its old texture workflow.
        ///
        /// For legacy assets the test is "every node it has is a texture-graph node", not "it has a
        /// Shader Output". A half-built surface graph with three layer nodes and no
        /// terminal yet is still a surface graph, and that is exactly the state a
        /// user is in when they close the window and come back to it. An empty
        /// legacy graph answers false. New assets use their saved domain even before nodes exist.
        /// </summary>
        public static bool LooksLikeTextureGraph(Graph graph)
        {
            if (graph == null || !graph.HasSupportedDomainMetadata) return false;
            if (graph.DomainSchemaVersion != 0) return graph.Domain == UCGraphDomain.TextureMaterialShader; // UC-T05-01: metadata takes precedence over contents or asset names.
            if (graph.nodes == null || graph.nodes.Count == 0) return false;

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null) continue;
                if (!NodeCategoryColors.IsTextureGraph(node.Category)) return false;
            }

            return true;
        }

        /// <summary>Creates a template asset, selects it in the Project window, and opens it.</summary>
        public static Graph CreateAndOpen(GraphTemplates.Template template, string fileName = null)
        {
            Graph graph = CreateAsset(template, fileName);
            if (graph == null) return null;

            EditorGUIUtility.PingObject(graph);
            Open(graph);
            return graph;
        }

        // Double-clicking a .asset in the Project window routes here. Returning true
        // tells Unity we handled it, which suppresses the default inspector-ping.
        //
        // The (int instanceID, int line) signature is the one [OnOpenAsset] is
        // guaranteed to invoke, so it stays. Unity 6.5 is mid-migration from int
        // ids to EntityId: InstanceIDToObject and GetAssetPath(int) are already
        // obsolete-as-error, while EntityIdToObject still accepts our int through an
        // implicit conversion that only warns (CS0618). We silence that one line
        // rather than adopt an unproven EntityId-parameter overload that, if the
        // reflection does not recognise it, would silently kill double-click.
        [OnOpenAsset(1)]
        private static bool OnOpenGraphAsset(int instanceID, int line)
        {
#pragma warning disable 618
            Graph graph = EditorUtility.EntityIdToObject(instanceID) as Graph;
#pragma warning restore 618
            if (graph == null) return false;

            OpenAuto(graph);
            return true;
        }

        // =====================================================================
        //  ASSETS/CREATE MENU
        // =====================================================================

        [MenuItem("Assets/Create/Universe Creator/Graph (Empty)", false, 300)]
        private static void CreateEmptyGraphMenu()
        {
            CreateAndOpen(GraphTemplates.Template.Empty);
        }

        [MenuItem("Assets/Create/Universe Creator/Graph (Simple Terrain)", false, 301)]
        private static void CreateSimpleTerrainGraphMenu()
        {
            CreateAndOpen(GraphTemplates.Template.SimpleTerrain);
        }

        [MenuItem("Assets/Create/Universe Creator/Texture Graph (Empty)", false, 310)]
        private static void CreateEmptyTextureGraphMenu()
        {
            CreateTextureAndOpen(TextureGraphTemplates.Template.Empty);
        }

        [MenuItem("Assets/Create/Universe Creator/Texture Graph (Basic Surface)", false, 311)]
        private static void CreateBasicSurfaceMenu()
        {
            CreateTextureAndOpen(TextureGraphTemplates.Template.BasicSurface);
        }

        [MenuItem("Assets/Create/Universe Creator/Texture Graph (Full PBR Surface)", false, 312)]
        private static void CreateFullPbrSurfaceMenu()
        {
            CreateTextureAndOpen(TextureGraphTemplates.Template.FullPbrSurface);
        }

        private static void CreateTextureAndOpen(TextureGraphTemplates.Template template)
        {
            Graph graph = TextureGraphTemplates.CreateAsset(template);
            if (graph == null) return;

            EditorGUIUtility.PingObject(graph);
            OpenTexture(graph);
        }

        // =====================================================================
        //  MISSING-TYPE REPAIR
        // =====================================================================

        /// <summary>
        /// UC-T01-11: compatibility entry point that opens the selected-graph recovery preview.
        ///
        /// It never repairs on invocation. The author chooses saved graph assets and
        /// explicitly confirms the preview; only that path discards missing data.
        /// </summary>
        /// <param name="logResult">Whether to explain the deferred action in the Console.</param>
        /// <returns>Zero; mutations happen only through the preview's confirmed action.</returns>
        [System.Obsolete("Select Graph assets and use the Graph Recovery preview; project-wide immediate repair is no longer supported.")]
        public static int RepairAll(bool logResult = true)
        {
            if (logResult) Debug.Log("[UC] No graph data was removed. Select saved graphs and review the recovery preview.");
            GraphRepairWindow.OpenSelected();
            return 0;
        }

        [MenuItem("Tools/Universe Creator/Diagnostics/Repair Graphs", priority = 211)]
        private static void RepairAllMenu()
        {
            // UC-T01-11: a menu click previews only the selected graphs; it never sweeps the project.
            GraphRepairWindow.OpenSelected();
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        /// <summary>
        /// Creates a project folder and any missing parents. AssetDatabase has no
        /// recursive create, so the path is walked one segment at a time.
        /// </summary>
        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Replace('\\', '/').Split('/');
            if (parts.Length == 0 || parts[0] != "Assets") return;

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;

                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);

                current = next;
            }
        }

        // Strips characters the OS will reject so a user-typed graph name cannot
        // produce an unwritable path.
        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "New Graph";

            char[] invalid = Path.GetInvalidFileNameChars();
            string result = raw;
            for (int i = 0; i < invalid.Length; i++)
                result = result.Replace(invalid[i], '_');

            result = result.Trim();
            return string.IsNullOrEmpty(result) ? "New Graph" : result;
        }
    }
}
