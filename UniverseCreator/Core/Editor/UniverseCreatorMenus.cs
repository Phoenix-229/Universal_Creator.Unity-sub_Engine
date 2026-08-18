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

        // =====================================================================
        //  WATER MENU
        // =====================================================================
        //
        //  These four are the only way water appears in a scene by hand, and they
        //  are the reason nothing creates water on its own. A project that wants
        //  no water never opens this menu and never gets any: the graph's water
        //  output nodes are opt-in too, so with neither, the water code does not
        //  run at all.
        //
        //  Each entry only presets the component. Every value it sets is a field
        //  the user can then change, which is the difference between a starting
        //  point and an automatic decision.
        //
        //  The presets themselves live in SpawnWater rather than in the menu
        //  callbacks, because the Water System Manager's inspector offers the same
        //  four buttons. Two copies of "what an ocean starts as" drift apart the
        //  first time one of them is tuned.

        [MenuItem("GameObject/3D Object/Universe Creator/Water/Ocean", false, 30)]
        private static void CreateOceanMenu() { SpawnWater(WaterPreset.Ocean, null); }

        [MenuItem("GameObject/3D Object/Universe Creator/Water/Lake", false, 31)]
        private static void CreateLakeMenu() { SpawnWater(WaterPreset.Lake, null); }

        [MenuItem("GameObject/3D Object/Universe Creator/Water/River (Spline)", false, 32)]
        private static void CreateRiverMenu() { SpawnWater(WaterPreset.River, null); }

        [MenuItem("GameObject/3D Object/Universe Creator/Water/Pool", false, 33)]
        private static void CreatePoolMenu() { SpawnWater(WaterPreset.Pool, null); }

        /// <summary>The four water starting points offered by the menu and by the
        /// Water System Manager inspector.</summary>
        public enum WaterPreset
        {
            Ocean = 0,
            Lake = 1,
            River = 2,
            Pool = 3
        }

        /// <summary>
        /// Creates one preset water body, registers the undo, selects it and builds
        /// it once. parentOverride places it under a specific WaterSystem; null
        /// falls back to the selected world's, which is what the menu wants.
        /// </summary>
        public static UCWaterBody SpawnWater(WaterPreset preset, Transform parentOverride)
        {
            UCWaterBody body;

            switch (preset)
            {
                case WaterPreset.Lake: body = PresetLake(parentOverride); break;
                case WaterPreset.River: body = PresetRiver(parentOverride); break;
                case WaterPreset.Pool: body = PresetPool(parentOverride); break;
                default: body = PresetOcean(parentOverride); break;
            }

            // Defaults from the manager are written before the first build, so the
            // body is built once with the settings it will keep rather than built
            // twice with a visible flicker between them.
            WaterPresets.ApplyManagerDefaults(body);

            Finish(body);
            return body;
        }

        private static UCWaterBody PresetOcean(Transform parentOverride)
        {
            UCWaterBody body = CreateWaterBody("Ocean", WaterBodyKind.Ocean,
                                               UCWaterBody.ShapeMode.Rectangle, parentOverride);

            // Defaults live in the runtime WaterPresets so the graph path uses the
            // same numbers. Only authoring extras stay in the menu preset.
            WaterPresets.ApplyDefaults(body);
            return body;
        }

        private static UCWaterBody PresetLake(Transform parentOverride)
        {
            UCWaterBody body = CreateWaterBody("Lake", WaterBodyKind.Lake,
                                               UCWaterBody.ShapeMode.Polygon, parentOverride);

            WaterPresets.ApplyDefaults(body);

            // Seeded rather than left empty: an empty outline builds nothing, and
            // "I added a lake and nothing happened" is not a useful first minute.
            // Authoring convenience only - the procedural path sets its own outline.
            body.SeedOutline(12);
            body.SnapCourseToGround();

            return body;
        }

        private static UCWaterBody PresetRiver(Transform parentOverride)
        {
            UCWaterBody body = CreateWaterBody("River", WaterBodyKind.River,
                                               UCWaterBody.ShapeMode.RiverSpline, parentOverride);

            WaterPresets.ApplyDefaults(body);

            // A river starts with NO course, deliberately, and unlike a lake. A lake's
            // seeded 12-gon is a shape you then adjust; a straight seeded river is a line
            // through ground it has nothing to do with, and dragging five points off it is
            // more work than drawing five. So the inspector asks for CTRL + Click instead.
            return body;
        }

        private static UCWaterBody PresetPool(Transform parentOverride)
        {
            UCWaterBody body = CreateWaterBody("Pool", WaterBodyKind.Pool,
                                               UCWaterBody.ShapeMode.Rectangle, parentOverride);

            WaterPresets.ApplyDefaults(body);
            return body;
        }

        /// <summary>
        /// Creates the object and the component with nothing preset. Public so a
        /// test can build one without going through a menu callback.
        ///
        /// parentOverride is the WaterSystem to parent under. Null means "work it
        /// out from the selection", which is what a menu click needs; the manager
        /// inspector passes its own transform because the selection at that moment
        /// is the manager itself, not a world.
        /// </summary>
        public static UCWaterBody CreateWaterBody(string name, WaterBodyKind kind,
                                                  UCWaterBody.ShapeMode shape,
                                                  Transform parentOverride)
        {
            GameObject go = new GameObject(name);

            // Parented under the selected world's WaterSystem child when there is
            // one, so a hand-placed body lands where the hierarchy already says
            // water belongs instead of at the root of the scene.
            Transform parent = parentOverride != null ? parentOverride : FindWaterSystem();
            if (parent != null) go.transform.SetParent(parent, false);

            go.transform.localPosition = GroundedSpawnPoint(parent);

            UCWaterBody body = go.AddComponent<UCWaterBody>();

            // The one place a kind is ever set. SetKindOnce locks it, so the
            // inspector can show it as text and no later script can re-kind a body
            // whose other fields were authored against what it already was.
            body.SetKindOnce(kind);
            body.shape = shape;

            return body;
        }

        /// <summary>Selection-parented overload, kept so existing callers and tests
        /// still compile against the original three-argument signature.</summary>
        public static UCWaterBody CreateWaterBody(string name, WaterBodyKind kind,
                                                  UCWaterBody.ShapeMode shape)
        {
            return CreateWaterBody(name, kind, shape, null);
        }


        private static void Finish(UCWaterBody body)
        {
            Undo.RegisterCreatedObjectUndo(body.gameObject, "Create " + body.name);
            Selection.activeGameObject = body.gameObject;

            // AddComponent already ran OnEnable, before any of the presets above
            // were written, so the first build has to be asked for explicitly.
            body.Rebuild();

            EditorUtility.SetDirty(body);
        }

        /// <summary>The WaterSystem child of the selected world, or null.</summary>
        private static Transform FindWaterSystem()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null) return null;

            UniverseCreatorObject world = selected.GetComponentInParent<UniverseCreatorObject>();
            if (world == null) return null;

            return world.transform.Find("WaterSystem");
        }

        /// <summary>
        /// Where a new body starts: under the scene view pivot, at the height of
        /// the terrain there. Dropping it at the origin puts it kilometres from
        /// what the user is looking at, and dropping it at the pivot's own Y
        /// leaves it floating above or buried under the ground.
        /// </summary>
        private static Vector3 GroundedSpawnPoint(Transform parent)
        {
            Vector3 point = Vector3.zero;

            if (SceneView.lastActiveSceneView != null)
            {
                Vector3 pivot = SceneView.lastActiveSceneView.pivot;
                point = new Vector3(pivot.x, 0f, pivot.z);
            }

            Terrain[] terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null) continue;

                Vector3 local = point - terrain.transform.position;
                Vector3 size = terrain.terrainData.size;

                if (local.x < 0f || local.z < 0f || local.x > size.x || local.z > size.z) continue;

                point.y = terrain.transform.position.y +
                          terrain.terrainData.GetInterpolatedHeight(local.x / size.x, local.z / size.z);
                break;
            }

            // Local, because the object is parented before this is assigned.
            if (parent != null) point = parent.InverseTransformPoint(point);

            return point;
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
            // The user explicitly requested all terrains start exactly at (0,0,0) global coordinates,
            // not at a random raycast hit from the Scene View.
            root.transform.position = Vector3.zero;

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
        /// Creates the child objects listed in the design document, Section 3.1,
        /// and attaches each one's manager.
        ///
        /// Terrain deliberately gets nothing. Its cells are created and owned by
        /// ChunkManager, and every setting that would go on a terrain manager is
        /// already on the world root or on the graph. A component there would be a
        /// second place to change the same number, and the two would disagree.
        /// </summary>
        private static void BuildHierarchy(GameObject root, UniverseCreatorObject creator)
        {
            // Terrain is the parent of every native Unity Terrain cell - C-03.
            // ChunkManager is the controller and parents them here rather than under
            // itself, so the hierarchy reads terrain-under-Terrain. TerrainBuilder is
            // a static helper, not a component, so nothing is attached here.
            MakeChild(root, "Terrain");

            // Module 3. Live: this drives both hand-placed and graph-built water.
            MakeChild(root, "WaterSystem").AddComponent<UCWaterSystemManager>();

            // Module 4. Declared only - the fields describe the module to come.
            MakeChild(root, "VegetationSystem").AddComponent<UCVegetationSystemManager>();

            // Module 5. Wind is live here; the rest of weather is declared only.
            // Placed above the terrain at Y=500 as requested, so the WindZone icon doesn't end up underground.
            GameObject weatherSystem = MakeChild(root, "WeatherSystem");
            weatherSystem.transform.localPosition = new Vector3(0f, 500f, 0f);
            weatherSystem.AddComponent<UCWeatherSystemManager>();

            // Module 5. Declared only.
            MakeChild(root, "LightingSystem").AddComponent<UCLightingSystemManager>();

            GameObject chunkManager = MakeChild(root, "ChunkManager");
            ChunkManager cm = chunkManager.AddComponent<ChunkManager>();
            cm.Initialize(creator);

            // The mapping child is made here, at authoring time, so it exists before
            // play ever starts - it is never created or destroyed during play.
            cm.EnsureSliceMap();

            GameObject roadLink = MakeChild(root, "RoadMasterLinker");
            RoadMasterLinker linker = roadLink.AddComponent<RoadMasterLinker>();   // Module 7 bridge

            // Wired here rather than left for the user to drag: the linker is useless
            // until it knows which world it answers height queries for, and there is
            // exactly one candidate at this point.
            linker.universeCreator = creator;
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
