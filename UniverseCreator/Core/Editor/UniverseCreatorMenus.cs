// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UniverseCreatorMenus.cs
//  Purpose: Every Unity menu path Universe Creator owns, in one file so the
//           ordering stays consistent: GameObject creation, window launchers, and
//           the Project Settings page.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Nodes;
using UniverseCreator.Integration;
// Windows and utilities live in child namespaces. C# only searches enclosing
// namespaces implicitly, never nested ones, so these usings are required.
using UniverseCreator.Editor.Windows;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor
{
    public static class UniverseCreatorMenus
    {
        // EditorPrefs keys for the Project Settings page. Project-scoped defaults
        // that new worlds inherit; per-world values live on the component.
        private const string PrefTemplate = "UniverseCreator.DefaultTemplate";
        private const string PrefResolution = "UniverseCreator.DefaultResolution";
        private const string PrefWorldSizeKm = "UniverseCreator.DefaultWorldSizeKm";
        private const string PrefMaxHeight = "UniverseCreator.DefaultMaxHeight";
        private const string PrefChunksPerSide = "UniverseCreator.DefaultChunksPerSide";
        private const string PrefAutoRegen = "UniverseCreator.DefaultAutoRegenerate";
        private const string PrefBuildHierarchy = "UniverseCreator.BuildFullHierarchy";

        // =====================================================================
        //  GAMEOBJECT MENU
        // =====================================================================

        [MenuItem("GameObject/3D Object/Universe Creator/Create New World", false, 10)]
        private static void CreateNewWorldWithGraph()
        {
            GraphTemplates.Template template =
                (GraphTemplates.Template)EditorPrefs.GetInt(PrefTemplate, (int)GraphTemplates.Template.SimpleTerrain);

            CreateWorld(template, true);
        }

        [MenuItem("GameObject/3D Object/Universe Creator/Create New World (Empty Graph)", false, 11)]
        private static void CreateNewWorldEmpty()
        {
            CreateWorld(GraphTemplates.Template.Empty, true);
        }

        [MenuItem("GameObject/3D Object/Universe Creator/Create New World (No Graph)", false, 12)]
        private static void CreateNewWorldNoGraph()
        {
            CreateWorld(GraphTemplates.Template.Empty, false);
        }

        /// <summary>
        /// Builds a world root, its child hierarchy, and optionally a graph asset.
        /// Returns the created component so tests and other menu paths can reuse it.
        /// </summary>
        public static UniverseCreatorObject CreateWorld(GraphTemplates.Template template, bool createGraph)
        {
            GameObject root = new GameObject("Universe Creator World");
            UniverseCreatorObject creator = root.AddComponent<UniverseCreatorObject>();

            ApplyProjectDefaults(creator);

            // A world is kilometres across, so dropping it 50m in front of the camera
            // put a 4km surface edge-on and mostly behind the near plane - it read as
            // "nothing was created". Sit it under the scene view's pivot at ground
            // level instead, and frame it once the cells are laid out.
            //
            // Only when the pivot is somewhere sane, though. The pivot follows whatever
            // was last framed, so after visiting a distant object a new world lands
            // hundreds of kilometres out, where float precision is coarse enough that
            // Unity itself warns about it and the surface jitters and z-fights. Past
            // that distance the pivot is not a deliberate placement, so origin wins.
            if (SceneView.lastActiveSceneView != null)
            {
                Vector3 pivot = SceneView.lastActiveSceneView.pivot;
                Vector3 ground = new Vector3(pivot.x, 0f, pivot.z);

                float worldSpan = Mathf.Max(1f, creator.worldSizeKm * 1000f);
                float limit = Mathf.Max(10000f, worldSpan * 4f);

                root.transform.position = ground.magnitude <= limit ? ground : Vector3.zero;
            }

            if (EditorPrefs.GetBool(PrefBuildHierarchy, true))
                BuildHierarchy(root, creator);

            // Undo has to be registered before the graph assignment so one Ctrl+Z
            // removes the whole world rather than leaving a stripped root behind.
            Undo.RegisterCreatedObjectUndo(root, "Create Universe Creator World");
            Selection.activeGameObject = root;

            // Lay the flat terrain out before any graph exists, so the "No Graph" path
            // still produces a world with real, sculptable Unity terrain in it rather
            // than an empty Terrain child - C-03. With a graph, RebuildChunks below
            // overwrites these heights; the layout call is cheap and idempotent.
            ChunkManager chunks = creator.Chunks;
            if (chunks != null)
            {
                chunks.Initialize(creator);
                chunks.RebuildLayout();
            }

            if (createGraph)
            {
                Graph graph = GraphAssetUtility.CreateAsset(template);
                if (graph != null)
                {
                    creator.graph = graph;
                    EditorUtility.SetDirty(creator);

                    // Both templates open the node editor: an Empty graph is a
                    // starting point, not a dead end.
                    GraphAssetUtility.Open(graph);
                    EditorGUIUtility.PingObject(graph);

                    // RebuildChunks, not RegenerateAsync. RegenerateAsync evaluates the
                    // graph into lastTile but writes no TerrainData and creates no
                    // terrain objects, so a brand-new world generated into nothing and
                    // read as "the graph is assigned but no terrain appeared" - C-03.
                    // RebuildChunks allocates the cell pool under the Terrain child and
                    // routes each cell through the same path the Regenerate button uses.
                    if (creator.RebuildChunks())
                        AsyncProgressUI.Track(creator, creator.CurrentTask, "New world - generating terrain");
                }
            }

            FrameInSceneView(creator);

            return creator;
        }

        /// <summary>
        /// Points the scene view at the whole world. A new world is kilometres wide and
        /// starts flat, so it is easy to be standing inside it with the surface out of
        /// frame - which reads as the terrain never having been created.
        /// </summary>
        private static void FrameInSceneView(UniverseCreatorObject creator)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null || creator == null) return;

            float span = Mathf.Max(1f, creator.worldSizeKm * 1000f);
            Vector3 centre = creator.transform.position +
                             new Vector3(creator.worldOriginOffset.x, 0f, creator.worldOriginOffset.y);

            view.Frame(new Bounds(centre, new Vector3(span, Mathf.Max(1f, creator.maxHeightMeters), span)), false);
        }

        /// <summary>
        /// Creates the child objects listed in the design document, Section 3.1.
        /// Modules 2-9 fill these in; Module 1 only needs Terrain and ChunkManager
        /// to be live, so the rest are named placeholders that later modules attach
        /// their components to rather than restructuring the hierarchy.
        /// </summary>
        private static void BuildHierarchy(GameObject root, UniverseCreatorObject creator)
        {
            // Terrain is the parent of every native Unity Terrain cell - C-03.
            // ChunkManager is the controller and parents them here rather than under
            // itself, so the hierarchy reads terrain-under-Terrain. TerrainBuilder is
            // a static helper, not a component, so nothing is attached here.
            MakeChild(root, "Terrain");

            MakeChild(root, "WaterSystem");        // Module 3
            MakeChild(root, "VegetationSystem");   // Module 4
            MakeChild(root, "WeatherSystem");      // Module 5
            MakeChild(root, "LightingSystem");     // Module 5

            GameObject chunkManager = MakeChild(root, "ChunkManager");
            ChunkManager cm = chunkManager.AddComponent<ChunkManager>();
            cm.Initialize(creator);

            // The mapping child is made here, at authoring time, so it exists before
            // play ever starts - it is never created or destroyed during play.
            cm.EnsureSliceMap();

            GameObject roadLink = MakeChild(root, "RoadMasterLinker");
            roadLink.AddComponent<RoadMasterLinker>();   // Module 7 bridge
        }

        private static GameObject MakeChild(GameObject parent, string name)
        {
            GameObject child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            return child;
        }

        // Seeds a new world from the Project Settings defaults.
        private static void ApplyProjectDefaults(UniverseCreatorObject creator)
        {
            creator.resolution = EditorPrefs.GetInt(PrefResolution, 257);
            creator.worldSizeKm = EditorPrefs.GetFloat(PrefWorldSizeKm, 4f);
            creator.maxHeightMeters = EditorPrefs.GetFloat(PrefMaxHeight, 600f);
            creator.chunkCountPerSide = EditorPrefs.GetInt(PrefChunksPerSide, 5);
            creator.autoRegenerate = EditorPrefs.GetBool(PrefAutoRegen, true);
            creator.ValidateSettings();
        }

        // =====================================================================
        //  WINDOW MENU
        // =====================================================================

        [MenuItem("Window/Universe Creator/Node Graph Editor", false, 2000)]
        private static void OpenNodeGraphWindow()
        {
            NodeGraphWindow.OpenWindow();
        }

        [MenuItem("Window/Universe Creator/Texture Graph", false, 2001)]
        private static void OpenTextureGraphWindow()
        {
            TextureGraphWindow.OpenWindow();
        }

        [MenuItem("Window/Universe Creator/World Settings", false, 2002)]
        private static void OpenWorldSettingsWindow()
        {
            WorldSettingsWindow.OpenWindow();
        }

        [MenuItem("Window/Universe Creator/About", false, 2020)]
        private static void OpenAboutWindow()
        {
            AboutWindow.OpenWindow();
        }

        // =====================================================================
        //  PROJECT SETTINGS
        // =====================================================================

        [SettingsProvider]
        private static SettingsProvider CreateUniverseCreatorSettingsProvider()
        {
            SettingsProvider provider = new SettingsProvider("Project/Universe Creator", SettingsScope.Project)
            {
                label = "Universe Creator",

                guiHandler = (searchContext) =>
                {
                    EditorGUILayout.Space(10);
                    EditorGUILayout.LabelField("Universe Creator", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Project-wide defaults. Per-world values live on the component.",
                                               EditorStyles.miniLabel);
                    EditorGUILayout.Space(10);

                    // ---- New graph ----
                    EditorGUILayout.LabelField("New Graph", EditorStyles.boldLabel);

                    int templateIndex = EditorPrefs.GetInt(PrefTemplate, (int)GraphTemplates.Template.SimpleTerrain);
                    EditorGUI.BeginChangeCheck();
                    templateIndex = EditorGUILayout.Popup(
                        new GUIContent("Default Template", "Used by GameObject > Create New World."),
                        templateIndex, GraphTemplates.DisplayNames);
                    if (EditorGUI.EndChangeCheck())
                        EditorPrefs.SetInt(PrefTemplate, templateIndex);

                    EditorGUILayout.Space(4);
                    EditorGUILayout.BeginHorizontal();

                    // Both buttons create a graph asset AND open the node editor, so
                    // an Empty graph behaves exactly like Simple Terrain here.
                    if (GUILayout.Button("Create Empty Graph", GUILayout.Height(24)))
                        GraphAssetUtility.CreateAndOpen(GraphTemplates.Template.Empty);

                    if (GUILayout.Button("Create Simple Terrain Graph", GUILayout.Height(24)))
                        GraphAssetUtility.CreateAndOpen(GraphTemplates.Template.SimpleTerrain);

                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.LabelField(
                        "Graphs are written to " + GraphAssetUtility.DefaultFolder,
                        EditorStyles.miniLabel);

                    EditorGUILayout.Space(12);

                    // ---- World defaults ----
                    EditorGUILayout.LabelField("Default World Settings", EditorStyles.boldLabel);

                    PrefIntField(PrefResolution, "Resolution", 257, 33, 4097,
                                 "Heightmap samples per chunk side. Snaps to 2^n+1.");
                    PrefFloatField(PrefWorldSizeKm, "World Size (km)", 4f, 0.1f, 32f,
                                   "Side length of a new world.");
                    PrefFloatField(PrefMaxHeight, "Max Height (m)", 600f, 10f, 5000f,
                                   "Metres a heightmap value of 1.0 maps to.");
                    PrefIntField(PrefChunksPerSide, "Chunks Per Side", 5, 1, 15,
                                 "Virtual grid size. 5 gives the 25-chunk layout.");

                    EditorGUILayout.Space(8);

                    // ---- Behaviour ----
                    EditorGUILayout.LabelField("Behaviour", EditorStyles.boldLabel);

                    PrefToggle(PrefAutoRegen, "Auto Regenerate",
                               "New worlds regenerate when a setting is committed.");
                    PrefToggle(PrefBuildHierarchy, "Build Full Hierarchy",
                               "Create the Terrain / Water / Vegetation / Weather / Lighting / " +
                               "ChunkManager / RoadMasterLinker children. Off gives a bare root.");

                    EditorGUILayout.Space(12);

                    if (GUILayout.Button("Reset Project Defaults", GUILayout.Height(22)))
                    {
                        EditorPrefs.DeleteKey(PrefTemplate);
                        EditorPrefs.DeleteKey(PrefResolution);
                        EditorPrefs.DeleteKey(PrefWorldSizeKm);
                        EditorPrefs.DeleteKey(PrefMaxHeight);
                        EditorPrefs.DeleteKey(PrefChunksPerSide);
                        EditorPrefs.DeleteKey(PrefAutoRegen);
                        EditorPrefs.DeleteKey(PrefBuildHierarchy);
                    }
                },

                keywords = new System.Collections.Generic.HashSet<string>(
                    new[] { "Universe", "Creator", "PCG", "Terrain", "Graph", "Chunk" })
            };

            return provider;
        }

        // Project Settings uses EditorGUI.BeginChangeCheck rather than the inspector's
        // focus-out pattern: nothing here triggers a regeneration, so a per-keystroke
        // write costs one EditorPrefs entry and no work.
        private static void PrefIntField(string key, string label, int fallback, int min, int max, string tooltip)
        {
            int current = EditorPrefs.GetInt(key, fallback);
            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.IntField(new GUIContent(label, tooltip), current);
            if (!EditorGUI.EndChangeCheck()) return;

            EditorPrefs.SetInt(key, Mathf.Clamp(next, min, max));
        }

        private static void PrefFloatField(string key, string label, float fallback, float min, float max, string tooltip)
        {
            float current = EditorPrefs.GetFloat(key, fallback);
            EditorGUI.BeginChangeCheck();
            float next = EditorGUILayout.FloatField(new GUIContent(label, tooltip), current);
            if (!EditorGUI.EndChangeCheck()) return;

            EditorPrefs.SetFloat(key, Mathf.Clamp(next, min, max));
        }

        private static void PrefToggle(string key, string label, string tooltip)
        {
            bool current = EditorPrefs.GetBool(key, true);
            EditorGUI.BeginChangeCheck();
            bool next = EditorGUILayout.Toggle(new GUIContent(label, tooltip), current);
            if (!EditorGUI.EndChangeCheck()) return;

            EditorPrefs.SetBool(key, next);
        }
    }
}
