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
        private const string PrefChunkSize = "UniverseCreator.DefaultChunkSizeMeters";
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

        [MenuItem("GameObject/3D Object/Universe Creator/Water/Swamp", false, 34)]
        private static void CreateSwampMenu() { SpawnWater(WaterPreset.Swamp, null); }

        /// <summary>The water starting points offered by the menu and by the
        /// Water System Manager inspector.</summary>
        public enum WaterPreset
        {
            Ocean = 0,
            Lake = 1,
            River = 2,
            Pool = 3,
            Swamp = 4
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
                case WaterPreset.Swamp: body = PresetSwamp(parentOverride); break;
                default: body = PresetOcean(parentOverride); break;
            }

            // Defaults from the manager are written before the first build, so the
            // body is built once with the settings it will keep rather than built
            // twice with a visible flicker between them.
            WaterPresets.ApplyManagerDefaults(body);

            // §9: read the procedural graph before placing, so the body drops onto the
            // world the graph already made rather than at the raw scene pivot. Done here,
            // before Finish's first Rebuild, so the body builds once at its real level.
            SeatFromGraph(body);

            Finish(body);
            return body;
        }

        /// <summary>
        /// Seats a freshly-spawned body on the elevation the procedural graph implies,
        /// so a hand-placed body starts where the procedural one would (§9).
        ///
        /// OCEAN reads ONLY the ocean node: its surface is sea level, so the body's Y is
        /// set to sea-level metres and the terrain crossing that plane IS the shore -
        /// exactly how the procedural ocean places itself, no basin search. LAKE drops
        /// into a real basin: its Y is lowered to the lowest ground under its own
        /// footprint (a hollow, not the pivot bump), so it fills a basin instead of
        /// sitting proud of one. River and pool keep the real-math width/depth/level
        /// fields the presets already wrote - a river's level comes from the ground its
        /// course falls down, a pool carries its own floor - so neither is touched here.
        /// </summary>
        private static void SeatFromGraph(UCWaterBody body)
        {
            if (body == null) return;

            UniverseCreatorObject world = body.GetComponentInParent<UniverseCreatorObject>();
            if (world == null) return;

            if (body.kind == WaterBodyKind.Ocean)
            {
                // Sea level is a fraction of world height; the ocean's surface stands
                // there and the beach is wherever the ground rises through it.
                Graph graph = world.graph;
                float seaFraction = 0.35f;
                UCWaterSystemManager mgr = body.GetComponentInParent<UCWaterSystemManager>();
                if (mgr != null) seaFraction = mgr.nodeSeaLevel;
                else if (graph != null) seaFraction = OceanSeaLevelFraction(graph, seaFraction);

                float seaY = world.transform.position.y + seaFraction * world.maxHeightMeters;
                Vector3 p = body.transform.position;
                body.transform.position = new Vector3(p.x, seaY - body.surfaceOffsetMetres, p.z);
                return;
            }

            if (body.kind == WaterBodyKind.Lake)
            {
                // Drop to the lowest ground under the seeded outline, so the lake sits in
                // a basin. A tiny fill above that floor gives it a waterline instead of a
                // puddle skimming the bed; the dig then shapes the real bed beneath.
                float floorY;
                if (LowestGroundUnder(body, out floorY))
                {
                    Vector3 p = body.transform.position;
                    body.transform.position = new Vector3(p.x, floorY - body.surfaceOffsetMetres, p.z);
                }
            }
        }

        /// <summary>Sea-level fraction from the first Ocean Level node in the graph.</summary>
        private static float OceanSeaLevelFraction(Graph graph, float fallback)
        {
            if (graph == null || graph.nodes == null) return fallback;
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                UniverseCreator.Nodes.Selectors.OceanLevelNode ocean =
                    graph.nodes[i] as UniverseCreator.Nodes.Selectors.OceanLevelNode;
                if (ocean != null) return ocean.seaLevel;
            }
            return fallback;
        }

        /// <summary>
        /// Lowest active-terrain height under a level body's footprint, world Y. False
        /// when no terrain lies under it (nothing to seat against).
        /// </summary>
        private static bool LowestGroundUnder(UCWaterBody body, out float floorY)
        {
            floorY = float.MaxValue;
            bool any = false;

            Vector3 centre = body.transform.position;
            float hx = Mathf.Max(1f, body.sizeMetres.x * 0.5f);
            float hz = Mathf.Max(1f, body.sizeMetres.y * 0.5f);

            Terrain[] terrains = Terrain.activeTerrains;

            // A coarse 5x5 lattice over the footprint: enough to find the basin floor
            // without a full solve, and this runs once on spawn.
            const int Steps = 4;
            for (int iz = 0; iz <= Steps; iz++)
            {
                for (int ix = 0; ix <= Steps; ix++)
                {
                    float wx = centre.x - hx + (2f * hx) * ix / Steps;
                    float wz = centre.z - hz + (2f * hz) * iz / Steps;

                    for (int t = 0; t < terrains.Length; t++)
                    {
                        Terrain terrain = terrains[t];
                        if (terrain == null || terrain.terrainData == null) continue;

                        Vector3 local = new Vector3(wx, 0f, wz) - terrain.transform.position;
                        Vector3 size = terrain.terrainData.size;
                        if (local.x < 0f || local.z < 0f || local.x > size.x || local.z > size.z) continue;

                        float y = terrain.transform.position.y +
                                  terrain.terrainData.GetInterpolatedHeight(local.x / size.x, local.z / size.z);
                        if (y < floorY) { floorY = y; any = true; }
                        break;
                    }
                }
            }

            return any;
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

        private static UCWaterBody PresetSwamp(Transform parentOverride)
        {
            UCWaterBody body = CreateWaterBody("Swamp", WaterBodyKind.Swamp,
                                               UCWaterBody.ShapeMode.Polygon, parentOverride);

            WaterPresets.ApplyDefaults(body);

            // Seeded like a lake so "I added a swamp and nothing happened" never occurs; the
            // author reshapes the outline afterwards, or the procedural path sets its own.
            body.SeedOutline(12);
            body.SnapCourseToGround();

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
        /// One system child of a world root: the name the hierarchy contract fixes,
        /// and the manager that belongs on it. A null manager is an organising
        /// object that carries no component.
        /// </summary>
        private struct SystemChild
        {
            public string name;
            public System.Type manager;

            /// <summary>
            /// A name this child used to be created under, or null. EnsureChild looks
            /// for it when <see cref="name"/> is not found and renames the object it
            /// finds instead of creating a second one.
            ///
            /// This exists because the child name is a lookup key: EnsureChild resolves
            /// by Transform.Find, so changing a name without an alias would leave every
            /// world authored before the change carrying an orphan under the old name
            /// and a fresh, unwired object under the new one. RM-00 renamed the road
            /// bridge, so it is the first entry that needs one.
            /// </summary>
            public string legacyName;

            public SystemChild(string name, System.Type manager)
            {
                this.name = name;
                this.manager = manager;
                this.legacyName = null;
            }

            public SystemChild(string name, System.Type manager, string legacyName)
            {
                this.name = name;
                this.manager = manager;
                this.legacyName = legacyName;
            }
        }

        /// <summary>
        /// What a complete world is made of, in hierarchy order.
        ///
        /// THE ONLY LIST. It is read by BuildHierarchy, which creates them, and by
        /// MissingManagers, which reports the ones an older world never got. Two
        /// hand-written lists would have been the real bug here: the world inspector
        /// would keep reporting "complete" for a manager it had never heard of, which
        /// is precisely the failure - a blank inspector and nothing saying why - that
        /// the repair path exists to end. Adding a manager to this array teaches the
        /// builder and the checker at once.
        /// </summary>
        private static readonly SystemChild[] SystemChildren =
        {
            // Terrain deliberately gets no component. Its cells are created and owned
            // by ChunkManager and parented here rather than under it, so the hierarchy
            // reads terrain-under-Terrain - C-03. Every setting a terrain manager
            // would hold already lives on the world root or on the graph, and a
            // component here would be a second place to change the same number.
            new SystemChild("Terrain",          null),

            // Module 3, live: drives both hand-placed and graph-built water.
            new SystemChild("WaterSystem",      typeof(UCWaterSystemManager)),

            // Module 3, live: resolves the world's climate, site geometry and density
            // budget into the snapshot the scatter pass places from.
            new SystemChild("VegetationSystem", typeof(UCVegetationSystemManager)),

            // Module 3, live: wind, plus the climate and season half of the world
            // clock that vegetation reads its temperature and rainfall from.
            new SystemChild("WeatherSystem",    typeof(UCWeatherSystemManager)),

            // Module 3, live: Time of Day, which owns the day half of the world clock
            // and the latitude and tilt solar position follows from. The sun rig,
            // ambient, shadow and atmosphere groups are declared only.
            new SystemChild("LightingSystem",   typeof(UCLightingSystemManager)),

            new SystemChild("ChunkManager",     typeof(ChunkManager)),

            new SystemChild("RoadCityMasterLinker", typeof(RoadCityMasterLinker),
                            "RoadMasterLinker"),   // Module 6 bridge; legacy alias per RM-00
        };

        /// <summary>
        /// Where <paramref name="manager"/> sits in <see cref="SystemChildren"/>, so
        /// the one-time wiring below addresses a child by what it is rather than by a
        /// hard-coded index that a reorder of the array would silently break.
        /// </summary>
        private static int IndexOf(System.Type manager)
        {
            for (int i = 0; i < SystemChildren.Length; i++)
                if (SystemChildren[i].manager == manager) return i;

            return -1;
        }

        /// <summary>
        /// Creates every child in <see cref="SystemChildren"/> and attaches its
        /// manager. Returns how many managers it had to attach, so a caller can
        /// report whether it changed anything.
        ///
        /// IDEMPOTENT BY DESIGN, and that is load-bearing rather than tidy:
        /// RepairHierarchy runs this same method on a world that already exists, so
        /// a world authored before a manager shipped gains it in place. One
        /// description of what a world is made of, instead of a builder and a
        /// repairer that drift apart. Nothing here replaces a component that is
        /// already present, and nothing re-runs one-time wiring.
        /// </summary>
        private static int BuildHierarchy(GameObject root, UniverseCreatorObject creator)
        {
            int attached = 0;

            GameObject[] child = new GameObject[SystemChildren.Length];
            bool[] added = new bool[SystemChildren.Length];

            for (int i = 0; i < SystemChildren.Length; i++)
            {
                child[i] = EnsureChild(root, SystemChildren[i].name,
                                       SystemChildren[i].manager,
                                       SystemChildren[i].legacyName, out added[i]);
                if (added[i]) attached++;
            }

            // -----------------------------------------------------------------
            //  One-time wiring. Guarded by added[] rather than run every pass,
            //  because a repair must not overwrite what the author has since set:
            //  a WeatherSystem they moved, a ChunkManager they configured, a
            //  linker they pointed at a different world.
            // -----------------------------------------------------------------

            // Above the terrain so the WindZone icon doesn't end up underground.
            int weather = IndexOf(typeof(UCWeatherSystemManager));
            if (added[weather])
                child[weather].transform.localPosition = new Vector3(0f, 500f, 0f);

            int chunks = IndexOf(typeof(ChunkManager));
            ChunkManager cm = child[chunks].GetComponent<ChunkManager>();
            if (added[chunks]) cm.Initialize(creator);

            // The mapping child is made here, at authoring time, so it exists before
            // play ever starts - it is never created or destroyed during play. Run
            // unconditionally: it is itself an Ensure, and a world repaired from an
            // older version may have the manager but not the map.
            cm.EnsureSliceMap();

            // Wired here rather than left for the user to drag: the linker is useless
            // until it knows which world it answers height queries for, and there is
            // exactly one candidate at this point. Filling a null rather than
            // redirecting a linker that already points somewhere.
            RoadCityMasterLinker linker =
                child[IndexOf(typeof(RoadCityMasterLinker))].GetComponent<RoadCityMasterLinker>();
            if (linker.universeCreator == null) linker.universeCreator = creator;

            return attached;
        }

        /// <summary>
        /// The named child of this root, created if absent, carrying
        /// <paramref name="manager"/>, also attached if absent.
        ///
        /// Found by name rather than by type because the name is what the hierarchy
        /// contract in Section 3.1 fixes, and because a repair has to recognise the
        /// bare GameObject an older version left behind. <paramref name="added"/>
        /// reports whether the component was attached by this call, which is how the
        /// caller runs one-time wiring exactly once instead of re-running it over a
        /// world that was already complete.
        /// </summary>
        private static GameObject EnsureChild(GameObject parent, string name,
                                              System.Type manager, out bool added)
        {
            return EnsureChild(parent, name, manager, null, out added);
        }

        private static GameObject EnsureChild(GameObject parent, string name,
                                              System.Type manager, string legacyName,
                                              out bool added)
        {
            Transform existing = parent.transform.Find(name);

            // A renamed child is a migration, not a new child. Without this, a world
            // authored before the rename keeps its old object - components, references
            // and author edits included - while a second, empty one appears beside it.
            if (existing == null && !string.IsNullOrEmpty(legacyName))
            {
                Transform legacy = parent.transform.Find(legacyName);
                if (legacy != null)
                {
                    Undo.RecordObject(legacy.gameObject, "Rename " + legacyName);
                    legacy.gameObject.name = name;
                    existing = legacy;
                    Debug.Log("Universe Creator: renamed child '" + legacyName + "' to '" +
                              name + "' (RM-00 name sweep).", legacy.gameObject);
                }
            }

            GameObject child;
            if (existing != null)
            {
                child = existing.gameObject;
            }
            else
            {
                child = new GameObject(name);
                child.transform.SetParent(parent.transform, false);
            }

            if (manager == null)
            {
                added = false;
                return child;
            }

            added = child.GetComponent(manager) == null;

            // Undo.AddComponent rather than AddComponent: on a repair this lands in
            // a scene the author already has open, and an edit they cannot undo is
            // not an edit they will thank us for. On the creation path the whole root
            // is registered anyway, so it costs nothing there.
            if (added) Undo.AddComponent(child, manager);

            return child;
        }

        // =====================================================================
        //  HIERARCHY REPAIR
        //
        //  A world authored before a manager shipped keeps the system child but not
        //  the component: the GameObject is there, carries nothing but a Transform,
        //  and so its inspector is blank. That reads as a broken inspector when what
        //  is actually missing is the component, and there was no way to tell the
        //  difference or to fix it short of deleting a world that by then has a
        //  graph, hand-placed water and painted terrain on it.
        //
        //  So the missing halves are attached in place, by the same BuildHierarchy
        //  the creation path runs - which is exactly why that method is idempotent.
        // =====================================================================

        /// <summary>
        /// Attaches any system manager missing from this world. Returns how many were
        /// attached; 0 means the world was already complete. Safe to run repeatedly.
        /// </summary>
        public static int RepairHierarchy(UniverseCreatorObject creator)
        {
            if (creator == null) return 0;

            Undo.SetCurrentGroupName("Repair Universe Creator Hierarchy");
            return BuildHierarchy(creator.gameObject, creator);
        }

        /// <summary>
        /// The system managers this world does not have, comma-separated; empty when
        /// the world is complete. A read-only check, safe to call from OnInspectorGUI.
        ///
        /// Searched with GetComponentInChildren rather than by child name, so a
        /// manager the author reparented or renamed still counts as present and the
        /// world inspector does not nag about a world that works.
        /// </summary>
        public static string MissingManagers(UniverseCreatorObject creator)
        {
            if (creator == null) return "";

            string missing = "";

            for (int i = 0; i < SystemChildren.Length; i++)
            {
                System.Type manager = SystemChildren[i].manager;
                if (manager == null) continue;

                if (creator.GetComponentInChildren(manager, true) != null) continue;

                if (missing.Length > 0) missing += ", ";
                missing += manager.Name;
            }

            return missing;
        }

        [MenuItem("Tools/Universe Creator/Diagnostics/Repair World Hierarchy", false, 212)]
        private static void RepairHierarchyMenu()
        {
            // Inactive included, and no sort mode: Unity 6 deprecated the sort-mode overload outright, and the
            // instance-id order it used to promise is going away with it. Nothing here depends on the order -- the
            // repair is per-world and the count is a total -- so asking for one only bought a warning.
            UniverseCreatorObject[] worlds = Object.FindObjectsByType<UniverseCreatorObject>(
                FindObjectsInactive.Include);

            if (worlds.Length == 0)
            {
                Debug.Log("[UC] Repair World Hierarchy: no Universe Creator world in the open scenes.");
                return;
            }

            int attached = 0;
            for (int i = 0; i < worlds.Length; i++) attached += RepairHierarchy(worlds[i]);

            // Logged either way. "Nothing to do" is the useful answer when an author
            // ran this because an inspector looked wrong: it rules the hierarchy out.
            Debug.Log(attached == 0
                ? "[UC] Repair World Hierarchy: nothing to do. " + worlds.Length +
                  (worlds.Length == 1 ? " world is complete." : " worlds are complete.")
                : "[UC] Repair World Hierarchy: attached " + attached +
                  (attached == 1 ? " missing manager across " : " missing managers across ") +
                  worlds.Length + (worlds.Length == 1 ? " world." : " worlds."));
        }

        // Seeds a new world from the Project Settings defaults.
        private static void ApplyProjectDefaults(UniverseCreatorObject creator)
        {
            creator.resolution = EditorPrefs.GetInt(PrefResolution, 257);
            creator.worldSizeKm = EditorPrefs.GetFloat(PrefWorldSizeKm, 4f);
            creator.maxHeightMeters = EditorPrefs.GetFloat(PrefMaxHeight, 600f);
            creator.chunkSizeMeters = EditorPrefs.GetFloat(PrefChunkSize, 1000f);
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

        [MenuItem("Window/Universe Creator/Lighting Day Cycle", false, 2003)]
        private static void OpenLightingDayCycleWindow()
        {
            LightingDayCycleWindow.OpenWindow();
        }

        [MenuItem("Window/Universe Creator/Geometry Debug", false, 2004)]
        private static void OpenGeometryDebugWindow()
        {
            GeometryDebugWindow.OpenWindow();
        }

        [MenuItem("Window/Universe Creator/Open Manual", false, 2019)]
        private static void OpenManual()
        {
            UCManualLauncher.Open();
        }

        [MenuItem("Window/Universe Creator/About", false, 2020)]
        private static void OpenAboutWindow()
        {
            AboutWindow.OpenWindow();
        }

        // =====================================================================
        //  IMPOSTOR BAKING
        // =====================================================================

        [MenuItem("Tools/Universe Creator/Bake Impostor (Selection)", false, 910)]
        private static void BakeImpostorForSelection()
        {
            var go = Selection.activeGameObject;
            UniverseCreator.Editor.Baking.ImpostorBaker.Enqueue(go, go != null ? go.name : "");
        }

        [MenuItem("Tools/Universe Creator/Bake Impostor (Selection)", true)]
        private static bool BakeImpostorForSelectionValidate()
        {
            return Selection.activeGameObject != null;
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
                    PrefFloatField(PrefChunkSize, "Chunk Size (m)", 1000f, 50f, 10000f,
                                   "Metres per tile. Chunk count derives from world size / this. 1000 = 1 km tiles.");

                    EditorGUILayout.Space(8);

                    // ---- Behaviour ----
                    EditorGUILayout.LabelField("Behaviour", EditorStyles.boldLabel);

                    PrefToggle(PrefAutoRegen, "Auto Regenerate",
                               "New worlds regenerate when a setting is committed.");
                    PrefToggle(PrefBuildHierarchy, "Build Full Hierarchy",
                               "Create the Terrain / Water / Vegetation / Weather / Lighting / " +
                               "ChunkManager / RoadCityMasterLinker children. Off gives a bare root.");

                    EditorGUILayout.Space(12);

                    if (GUILayout.Button("Reset Project Defaults", GUILayout.Height(22)))
                    {
                        EditorPrefs.DeleteKey(PrefTemplate);
                        EditorPrefs.DeleteKey(PrefResolution);
                        EditorPrefs.DeleteKey(PrefWorldSizeKm);
                        EditorPrefs.DeleteKey(PrefMaxHeight);
                        EditorPrefs.DeleteKey(PrefChunkSize);
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
