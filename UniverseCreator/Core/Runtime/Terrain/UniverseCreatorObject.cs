// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    UniverseCreatorObject.cs
//  Purpose: Root component of a Universe Creator world. Owns the graph reference,
//           every world-level setting, and the async generation lifecycle.
//
//  THREADING CONTRACT
//  ------------------
//  RegenerateAsync() runs on the main thread. It null-checks the graph, calls
//  PrepareMainThread() on every node, snapshots all settings into a plain struct,
//  then hands that struct to Task.Run. The worker touches no UnityEngine.Object:
//  Graph's == operator and .name both marshal into native code, so they stay on
//  this side of the boundary.
//
//  This file must never reference UnityEditor - it ships in player builds. The
//  progress UI lives in AsyncProgressUI (editor assembly) and polls IsGenerating.
// -----------------------------------------------------------------------------

using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>How much resolution an interactive regeneration uses.</summary>
    public enum PreviewQuality
    {
        Draft = 0,   // quarter resolution - fastest feedback while dragging sliders
        Medium = 1,  // half resolution
        Full = 2     // full resolution
    }

    /// <summary>
    /// Immutable copy of everything the worker thread needs. Built on the main
    /// thread so the background evaluation never reads a live component.
    /// </summary>
    public struct GenerationSettings
    {
        public int seed;
        public int resolution;
        public float maxHeightMeters;
        public Vector3 worldSize;
        public Vector3 worldPos;
        public Coord chunkCoord;
    }

    /// <summary>
    /// Root component of a Universe Creator world. Owns the graph reference,
    /// world settings, chunk configuration, and triggers regeneration.
    /// </summary>
    [AddComponentMenu("Universe Creator/Universe Creator World")]
    [DisallowMultipleComponent]
    public class UniverseCreatorObject : MonoBehaviour
    {
        // =====================================================================
        //  GRAPH
        // =====================================================================

        [Tooltip("The node graph that defines this world's terrain generation.")]
        public Graph graph;

        // =====================================================================
        //  WORLD SETTINGS
        // =====================================================================

        [Tooltip("Master seed. Every noise node offsets from this, so changing it " +
                 "reshuffles the whole world while keeping its character.")]
        public int seed = 12345;

        [Tooltip("World size in kilometres (per side).")]
        public float worldSizeKm = 4f;

        [Tooltip("Maximum world height in metres. A Matrix value of 1.0 maps to this.")]
        public float maxHeightMeters = 600f;

        [Tooltip("Heightmap resolution per chunk. Must be 2^n+1 for Unity terrain.")]
        public int resolution = 257;

        [Tooltip("Shifts the sampling window across the noise field without moving " +
                 "the world in the scene. Use it to hunt for a landscape you like.")]
        public Vector2 worldOriginOffset = Vector2.zero;

        // =====================================================================
        //  CHUNK SETTINGS (25+1 Virtual System)
        // =====================================================================

        [Tooltip("Chunks per side in the virtual grid. 5 gives the 25-chunk layout.")]
        public int chunkCountPerSide = 5;

        [Tooltip("High-LOD ring size (centre square). 3 gives 9 high-fidelity chunks.")]
        public int highLODRingSize = 3;

        [Tooltip("Buffer ring width. 1 precomputes one extra ring so streaming in " +
                 "never shows a seam or lets the player fall through.")]
        public int bufferRingWidth = 1;

        [Tooltip("Metres of player travel toward a chunk edge before the grid recentres.")]
        public float recenterThreshold = 0.5f;

        // =====================================================================
        //  TERRAIN SETTINGS
        // =====================================================================

        [Tooltip("Material applied to every generated terrain chunk. Leave null for Unity's default.")]
        public Material terrainMaterial;

        // ---------------------------------------------------------------
        //  ANGULAR-ERROR DENSITY (visual acuity, L-49 / Nyquist, L-50)
        //
        //  These replace an authored LOD curve. The terrain's heightmap pixel error
        //  is derived from the live camera and screen through Geometry/AngularError
        //  rather than read off a hand-tuned ramp, so the on-screen silhouette error
        //  it permits is the same on a phone and a monitor. See Geometry/AngularError
        //  and Geometry/TerrainDensityController.
        // ---------------------------------------------------------------

        [Tooltip("How many pixels of terrain silhouette error is acceptable. This is the one honest " +
                 "density dial: resolution- and FOV-independent. Lower = more triangles.")]
        [Range(0.25f, 8f)]
        public float acuityPixels = 1f;

        [Tooltip("Hard floor on the derived pixel error, in pixels. The derived value is never allowed " +
                 "below this even when the source heightmap could carry finer detail.")]
        [Range(0.25f, 20f)]
        public float pixelErrorFloor = 1f;

        [Tooltip("Only re-write the terrain pixel error when it moves more than this many pixels. " +
                 "Writing it rebuilds the patch LOD tree, so a still camera should write it zero times.")]
        [Range(0f, 4f)]
        public float densityHysteresis = 0.75f;

        [Tooltip("Metres from the world origin past which the world root is clamped back. A root far " +
                 "from the origin degrades float precision into jitter, z-fighting and cracks.")]
        public float maxWorldOriginDistance = 20000f;

        // ---------------------------------------------------------------
        //  DERIVED DRAW DISTANCES
        //
        //  basemapDistance, detailObjectDistance, treeDistance and treeBillboardDistance
        //  are NOT authored. Each is the range at which its own real-world feature drops
        //  below the shared acuityPixels budget, resolved once against the reference view
        //  in Geometry/AngularError. One dial - acuityPixels - therefore moves the whole
        //  level-of-detail envelope together, instead of four hand-tuned magic numbers
        //  drifting apart. RecomputeDerivedDistances() applies the model and
        //  ValidateSettings() calls it on every change. The sizes below are the finest
        //  detail each system is responsible for keeping resolvable; calibrated so the
        //  desktop default (acuityPixels = 1) reproduces the long-standing distances.
        // ---------------------------------------------------------------

        // ~1 m of ground-texture detail: past where a metre of splat detail falls under the
        // pixel budget, the composited layers are indistinguishable from the baked basemap.
        private const float BasemapDetailMetres = 1.07f;
        // A grass clump, ~8.5 cm across.
        private const float DetailFeatureMetres = 0.085f;
        // ~1 m of tree silhouette: below the budget the whole tree reads as a smudge, so cull it.
        private const float TreeSilhouetteMetres = 0.96f;
        // ~5 cm of tree depth relief - the parallax that separates a 3D mesh from a flat card.
        private const float TreeReliefMetres = 0.053f;


        [Tooltip("Derived (read-only): range past which the composited splat layers fall below the " +
                 "acuity budget and the low-res basemap takes over. Moves with Acuity Budget.")]
        public float basemapDistance = 1000f;

        [Tooltip("Terrain casts shadows. Disable on mobile for a large fill-rate win.")]
        public bool castShadows = true;

        [Tooltip("Render terrain via instancing. Requires a material that supports it.")]
        public bool drawInstanced = true;

        [Tooltip("Build a collider for generated chunks. Off = visual-only preview.")]
        public bool generateCollider = true;

        // ---------------------------------------------------------------
        //  NATIVE UNITY TERRAIN SETTINGS
        //
        //  These map one-to-one onto properties of the Terrain component and its
        //  TerrainData. They live here rather than being left at Unity's defaults
        //  because a chunk is created from script: nobody ever opens the terrain
        //  inspector to set them, so if this system does not drive them, they are
        //  silently whatever Unity happened to construct.
        //
        //  TerrainBuilder.ConfigureTerrain applies every one of them on each chunk
        //  after generation, so a change here reaches all 25 chunks at once.
        // ---------------------------------------------------------------

        [Tooltip("Physics material for the terrain collider — controls friction and bounce. Leave empty for Unity's default.")]
        public PhysicsMaterial terrainPhysicsMaterial;

        [Tooltip("Draw terrain detail meshes and trees. Off strips all foliage.")]
        public bool drawTreesAndFoliage = true;

        [Tooltip("Derived (read-only): range past which a grass clump falls below the acuity budget " +
                 "and detail meshes stop drawing. Moves with Acuity Budget.")]
        public float detailObjectDistance = 80f;

        [Tooltip("Fraction of authored detail density actually drawn. Lower is the single " +
                 "cheapest grass optimisation.")]
        [Range(0f, 1f)]
        public float detailObjectDensity = 1f;

        [Tooltip("Derived (read-only): range past which a tree's silhouette falls below the acuity " +
                 "budget and it stops drawing entirely. Moves with Acuity Budget.")]
        public float treeDistance = 900f;

        [Tooltip("Derived (read-only): range past which a tree's depth relief falls below the acuity " +
                 "budget and the mesh is swapped for a billboard. Moves with Acuity Budget.")]
        public float treeBillboardDistance = 50f;

        [Tooltip("Metres over which the mesh-to-billboard swap is blended. 0 pops visibly.")]
        public float treeCrossFadeLength = 5f;

        [Tooltip("How many trees stay full-mesh near the camera before billboarding.")]
        [Range(0, 500)]
        public int treeMaximumFullLODCount = 50;

        [Tooltip("Grass sway strength.")]
        [Range(0f, 1f)]
        public float wavingGrassStrength = 0.5f;

        [Tooltip("How much of the grass bends per gust.")]
        [Range(0f, 1f)]
        public float wavingGrassAmount = 0.5f;

        [Tooltip("Gust speed.")]
        [Range(0f, 1f)]
        public float wavingGrassSpeed = 0.5f;

        [Tooltip("Colour multiplied into grass as it waves.")]
        public Color wavingGrassTint = new Color(0.7f, 0.6f, 0.5f, 1f);

        [Tooltip("How generated chunks take reflection probes.")]
        public UnityEngine.Rendering.ReflectionProbeUsage reflectionProbeUsage =
            UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;

        [Tooltip("Terrain layers painted by Texture Output nodes, in splatmap channel order.")]
        public TerrainLayer[] terrainLayers;

        // =====================================================================
        //  SCATTER PROTOTYPES
        // =====================================================================

        [Tooltip("What the prototypeIndex on a scattered instance means. Scatter nodes " +
                 "run off-thread and cannot touch a prefab, so they emit an index and " +
                 "this resolves it into trees, detail meshes and props.")]
        public TerrainSystem.ScatterPrototypeLibrary scatterLibrary =
            new TerrainSystem.ScatterPrototypeLibrary();

        // =====================================================================
        //  WATER
        // =====================================================================

        [Tooltip("Layer the generated water surfaces are put on. Water usually gets " +
                 "its own layer so a camera can exclude it, an underwater volume can " +
                 "find it, and character raycasts can pass through it.")]
        public int waterLayer;

        // =====================================================================
        //  PERFORMANCE
        // =====================================================================

        [Tooltip("Enable GPU instancing for scattered vegetation and objects.")]
        public bool enableGPUInstancing = true;

        [Tooltip("Maximum draw distance in metres.")]
        public float drawDistance = 1500f;

        [Tooltip("Lower transitions to low-poly sooner (mobile); higher delays it (desktop).")]
        [Range(0.5f, 2f)]
        public float lodBias = 1f;

        [Tooltip("How many chunks may generate concurrently. Keep below core count " +
                 "so the editor stays responsive.")]
        [Range(1, 16)]
        public int maxConcurrentChunks = 4;

        [Tooltip("Resolution used for interactive regeneration. Draft is fastest.")]
        public PreviewQuality previewQuality = PreviewQuality.Full;

        [Tooltip("Milliseconds to leave between writing one finished chunk to its " +
                 "terrain and the next. Raise it if the editor stutters while a large " +
                 "world bakes; lower it to fill the world in faster.")]
        [Range(0f, 500f)]
        [SerializeField] private float chunkYieldDelayMs = 100f;

        /// <summary>
        /// Gap enforced between chunk applications, in seconds.
        ///
        /// Generation is off-thread, but applying a finished chunk is not and cannot be:
        /// the height write, the splatmap write and the collider rebuild are all
        /// main-thread Unity calls. Landing them back to back is what turned an
        /// asynchronous bake into a frozen editor - the work was spread out, the
        /// consequences were not. Spacing the applies gives the editor whole frames
        /// between stalls, so a 56 km world fills in visibly while the UI stays live.
        /// </summary>
        public float ChunkYieldDelaySeconds
        {
            get { return Mathf.Max(0f, chunkYieldDelayMs) / 1000f; }
        }

        // =====================================================================
        //  GENERATION BEHAVIOUR
        // =====================================================================

        [Tooltip("Regenerate automatically when a setting is committed. Off means " +
                 "changes only apply when you press Regenerate.")]
        public bool autoRegenerate = true;

        [Tooltip("Show a low-poly wireframe stand-in while the real chunk generates.")]
        public bool showWireframePreview = true;

        // =====================================================================
        //  MANUAL SCULPTING
        // =====================================================================

        /// <summary>
        /// Sparse, world-space record of every manual height edit. Re-applied after
        /// each regeneration so hand-sculpted detail survives an arbitrary number of
        /// graph re-runs. Storing it here means edits belong to the world, not to a
        /// particular chunk — the world can recentre and the edits stay put.
        /// </summary>
        [SerializeReference] public TerrainOverrideLayer overrideLayer = new TerrainOverrideLayer();

        /// <summary>
        /// Capture hand sculpts into the override layer before each regeneration.
        /// On by default: losing sculpting work to a background regeneration is the
        /// worse failure. Turn it off when the graph is the only source of truth and
        /// the diff scan per cell is not worth paying for.
        /// </summary>
        [Tooltip("Capture hand-sculpted height changes before regenerating, so they " +
                 "survive the graph re-run. Off means the graph output replaces them.")]
        public bool captureManualEditsOnRegenerate = false;

        // =====================================================================
        //  GENERATION STATE
        // =====================================================================

        private volatile bool isGenerating;
        private StopToken currentStopToken;
        private Task currentTask;
        private TileData lastTile;

        /// <summary>True while a background generation is running.</summary>
        public bool IsGenerating { get { return isGenerating; } }

        /// <summary>The current background task, or null if none is running.</summary>
        public Task CurrentTask { get { return currentTask; } }

        /// <summary>
        /// The tile the last completed generation filled in. The chunking system
        /// reads TileData.outputs from here on the main thread.
        /// </summary>
        public TileData LastTile { get { return lastTile; } }

        // =====================================================================
        //  CHUNKING
        // =====================================================================

        // Cached so we do not GetComponentInChildren every regeneration.
        private ChunkManager chunkManager;

        /// <summary>
        /// The chunk manager driving this world's virtual grid, or null if the
        /// hierarchy was built without one.
        /// </summary>
        public ChunkManager Chunks
        {
            get
            {
                if (chunkManager == null) chunkManager = GetComponentInChildren<ChunkManager>();
                return chunkManager;
            }
        }

        /// <summary>
        /// Regenerates the world through the chunking system: every cell of the
        /// virtual grid, each at its own LOD, recycling the pooled TerrainData.
        /// This is what the inspector's Regenerate button and any committed setting
        /// route to. Returns false if there was nothing to generate.
        /// </summary>
        public bool RebuildChunks()
        {
            ChunkManager cm = Chunks;

            // No graph is a legitimate state, not an error: it is what a world looks
            // like before one is connected. Lay the cells out flat so the terrain is
            // present and Unity's own sculpting tools work, and say nothing - a warning
            // here fired on every new world and made an empty world look broken - C-03.
            if (graph == null)
            {
                if (cm == null) return false;

                cm.Initialize(this);
                cm.RebuildLayout();
                return false;
            }

            if (cm == null)
            {
                // No chunk manager in the hierarchy: fall back to the single-tile
                // path so a bare root still previews something.
                return RegenerateAsync();
            }

            cm.Initialize(this);

            // C-04. Pull any hand sculpting out of the live terrains before the graph
            // overwrites them. Every regeneration path funnels through here - the
            // Regenerate button, a committed setting, a graph edit, world creation - so
            // this is the one place that can guarantee a sculpt is never silently lost.
            // The captured deltas are re-applied per cell in ChunkManager.ApplyChunk,
            // so the edit rides on top of the new surface instead of under it.
            // Safe when nothing has ever been generated: CaptureManualEdits skips any
            // cell whose appliedHeights baseline is null.
            if (captureManualEditsOnRegenerate) cm.CaptureManualEdits();

            // Evaluate graph for HydrologyNodes and auto-place missing rivers/lakes
            // Only generate if the creator node is actually connected to the output!
            if (graph != null)
            {
                // Water is procedural ONLY where the graph asks for it. A Hydrology
                // Spawner must be present AND wired through to an Output before any
                // river or lake is drawn - a disconnected node, or no node at all,
                // means no procedural water, full stop. This is the user's rule:
                // "make sure the lake do not just create themselves but until their
                // nodes are created." (Bodies orphaned by a since-disconnected node
                // are torn down by the graph-reactive removal pass, not here.)
                //
                // The reach-an-Output walk lives on the water manager rather than being
                // written out here, because UCWaterBody.IsGraphLinked asks the same
                // question and two copies of "is it connected" would eventually give
                // two answers. Switching the manager's Source away from the graph is a
                // deliberate no as well: no spawn, and the sweep below takes down what
                // a previous run left standing.
                UCWaterSystemManager water =
                    GetComponentInChildren<UCWaterSystemManager>(true);
                bool spawnAllowed = water == null || water.NodeWaterEnabled;

                System.Collections.Generic.List<UniverseCreator.Nodes.Creators.HydrologyNode> spawners =
                    UCWaterSystemManager.ConnectedHydrology(graph);

                UniverseCreator.Nodes.Creators.HydrologyNode connectedHydrology =
                    spawnAllowed && spawners.Count > 0 ? spawners[0] : null;

                // DEFERRED, not called here. RebuildAll below is async, so at this line
                // the terrains still hold the flat/stale baseline - spawning now traced
                // every river across dead-flat ground (they collapsed to a single
                // invisible point) and stacked every lake at one height. Arming it on the
                // settle hook lets the spawn sample the real surface once the bake has
                // landed; the closure then calls ReapplyCarves so the new digs - and the
                // fill-back of any lake the reconcile-down cull removed - are folded into
                // the settled terrain in one synchronous pass.
                if (connectedHydrology != null)
                {
                    UniverseCreator.Nodes.Creators.HydrologyNode hydrologyToRun = connectedHydrology;
                    cm.RunAfterSettle(() =>
                    {
                        UniverseCreator.TerrainSystem.HydrologyGenerator.GenerateFromNode(this, hydrologyToRun);
                        cm.ReapplyCarves();

                        // The sea's level has only just been derived, from the settled
                        // surface, in metres. Every splatmap on the terrain was painted
                        // before that number existed - from whatever fraction the graph
                        // happened to hold - so the beach can be sitting at an elevation
                        // the water never reached, which is the untextured shoreline.
                        // Locking the two together and re-baking puts the sand on the real
                        // waterline within the same Generate.
                        //
                        // Bounded, and not a loop. The re-bake below does not re-arm this
                        // callback, so hydrology does not run a second time; the sync is a
                        // no-op once the two agree, and they stay agreed because the ocean
                        // holds its placement and no splatmap can move a height. A world
                        // with no sea, or one already in sync, re-bakes nothing.
                        UniverseCreator.TerrainSystem.UCWaterSystemManager water =
                            GetComponentInChildren<UniverseCreator.TerrainSystem.UCWaterSystemManager>(true);
                        if (water != null && water.SyncSeaLevelFromOcean(this))
                            cm.RebuildAll();
                    });
                }
                else
                {
                    // No Hydrology node reaches an Output any more (disconnected or
                    // deleted), or the water manager's Source says the graph may not
                    // produce water, so any river/lake a previously-connected node
                    // spawned is now orphaned. Sweep them on the SAME settle hook the
                    // spawn uses: deferred so the destroy runs on an editor update tick
                    // (DestroyImmediate is illegal mid-OnValidate) and so ReapplyCarves
                    // folds the freed basins back into the settled terrain in one
                    // synchronous pass - the exact mirror of the spawn path. Only generated
                    // bodies are removed; hand-placed water and the UniverseCreator root are
                    // untouched. The re-carve is skipped when nothing was swept, so a world
                    // that never had procedural water sees no behaviour change.
                    cm.RunAfterSettle(() =>
                    {
                        int removed = UniverseCreator.TerrainSystem.HydrologyGenerator.RemoveAllGenerated(this);
                        if (removed > 0) cm.ReapplyCarves();
                    });
                }
            }

            cm.RebuildAll();
            return true;
        }

        /// <summary>
        /// Applies any finished chunk generations to their TerrainData. Play mode
        /// pumps this from ChunkManager.Update; edit mode has no play loop, so the
        /// editor preview driver calls this from EditorApplication.update.
        /// </summary>
        /// <returns>True if a finished chunk was written to its TerrainData this pump,
        /// so the caller can repaint the Scene view - C-14.</returns>
        public bool PumpChunks()
        {
            ChunkManager cm = Chunks;
            return cm != null && cm.PumpCompleted();
        }

        // =====================================================================
        //  PUBLIC API
        // =====================================================================

        /// <summary>
        /// Kicks off async terrain regeneration. Called by the inspector when the
        /// user commits an input field (Enter or focus-out) or changes the graph.
        /// Returns false if there was nothing to generate.
        /// </summary>
        public bool RegenerateAsync()
        {
            // Real Unity null check, on the main thread where it is legal. This also
            // catches a graph asset that was deleted out from under us.
            if (graph == null)
            {
                Debug.LogWarning("Universe Creator: cannot regenerate, no graph assigned.", this);
                return false;
            }

            if (graph.nodes == null || graph.nodes.Count == 0)
            {
                Debug.LogWarning("Universe Creator: graph '" + graph.name + "' has no nodes.", this);
                return false;
            }

            // Supersede any run already in flight. The old worker sees the flag on
            // its next ThrowIfStopRequested and unwinds.
            if (currentStopToken != null) currentStopToken.RequestStop();

            // Nodes that need to read a Unity object (textures, curves, terrain data)
            // copy it into plain arrays here, while we are still on the main thread.
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node != null) node.PrepareMainThread();
            }

            GenerationSettings settings = BuildSettings();

            // Resolved here, on the main thread, for the same reason PrepareMainThread
            // ran above: it reads the lighting and weather managers live and the worker
            // may not. Null when ecology is switched off, which every scatter node
            // reads as "place exactly what Modules 1 and 2 placed".
            Ecology.EcoScatterContext ecology = EcoScatterContextBuilder.BuildFor(this);

            // Local copies so the closure captures values, not fields that the next
            // RegenerateAsync call is about to overwrite.
            Graph localGraph = graph;
            StopToken stop = new StopToken();
            currentStopToken = stop;
            isGenerating = true;

            currentTask = Task.Run(() => GenerateOnWorkerThread(localGraph, settings, stop, ecology));
            return true;
        }

        /// <summary>Requests the current generation stop. Safe to call when idle.</summary>
        public void CancelGeneration()
        {
            if (currentStopToken != null) currentStopToken.RequestStop();
        }

        /// <summary>
        /// Snapshots the settings the worker needs. Public so the chunking system can
        /// build a per-chunk variant without duplicating the maths.
        /// </summary>
        public GenerationSettings BuildSettings()
        {
            Vector3 worldSize = GetWorldSize();

            GenerationSettings s = new GenerationSettings();
            s.seed = seed;
            s.resolution = GetEffectiveResolution();
            s.maxHeightMeters = maxHeightMeters;
            s.worldSize = worldSize;

            // Centre the world on the transform, then apply the sampling offset.
            s.worldPos = new Vector3(
                transform.position.x - worldSize.x * 0.5f + worldOriginOffset.x,
                transform.position.y,
                transform.position.z - worldSize.z * 0.5f + worldOriginOffset.y);

            s.chunkCoord = new Coord(0, 0);
            return s;
        }

        // =====================================================================
        //  WORKER THREAD
        // =====================================================================

        /// <summary>
        /// The north fraction of a bake that covers the whole world in one tile: its
        /// centre, because a single tile spanning every latitude has no one edge to
        /// belong to. Named rather than written as a bare 0.5 so the reason travels
        /// with it. (W-05)
        /// </summary>
        private const float WholeWorldNorthFraction = 0.5f;

        // Static-in-spirit: takes everything by parameter and touches no Unity API
        // beyond Debug.Log, which is thread-safe.
        private void GenerateOnWorkerThread(Graph g, GenerationSettings s, StopToken stop,
                                            Ecology.EcoScatterContext ecology)
        {
            try
            {
                CoordRect rect = new CoordRect(new Coord(0, 0), new Coord(s.resolution, s.resolution));
                TileData tile = new TileData(s.chunkCoord, rect, s.worldPos, s.worldSize, s.maxHeightMeters);

                // ForChunk rather than the context itself, so this tile gets its own
                // sampler. There is only one tile on this path today, and taking the
                // shortcut is what would break the day there are two.
                //
                // W-05: 0.5 because this path bakes the world as a single tile, so it
                // spans the whole latitude range and its one latitude can only be the
                // centre one. Chunked bakes go through ChunkManager, which knows where
                // each cell sits and passes its real fraction.
                tile.ecology = ecology != null ? ecology.ForChunk(WholeWorldNorthFraction) : null;

                GraphEvaluator evaluator = new GraphEvaluator(g);
                evaluator.Evaluate(tile, stop);

                if (stop.StopRequested) return;

                // Hand the filled tile to the main thread. The chunking system reads
                // tile.outputs and calls TerrainData.SetHeights from there.
                lastTile = tile;
            }
            catch (System.OperationCanceledException)
            {
                // Normal path when the user supersedes a run. Not an error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Universe Creator: generation failed - " + ex);
            }
            finally
            {
                // Only the newest run owns the flag. An older, superseded worker
                // finishing late must not report the current one as idle.
                if (ReferenceEquals(currentStopToken, stop)) isGenerating = false;
            }
        }

        // =====================================================================
        //  DERIVED VALUES  (the maths the inspector displays back to the user)
        // =====================================================================

        /// <summary>World size in world-space units. 1 unit = 1 metre.</summary>
        public Vector3 GetWorldSize()
        {
            float sizeMeters = worldSizeKm * 1000f;
            return new Vector3(sizeMeters, maxHeightMeters, sizeMeters);
        }

        /// <summary>Size of one chunk in metres.</summary>
        public float GetChunkSize()
        {
            if (chunkCountPerSide < 1) return worldSizeKm * 1000f;
            return (worldSizeKm * 1000f) / chunkCountPerSide;
        }

        /// <summary>Metres covered by one heightmap sample. Drives erosion step size.</summary>
        public float GetMetersPerPixel()
        {
            // GetChunkResolution, because this must describe the heightfield the cells
            // actually allocate. On a grid past the clamp the two differ, and this value
            // maps world-space sculpt deltas onto sample indices - reading the unclamped
            // number would land every manual edit at the wrong sample.
            int res = GetChunkResolution();
            if (res < 2) return GetChunkSize();
            return GetChunkSize() / (res - 1);
        }

        /// <summary>Chunks held at full fidelity: the centre square of the grid.</summary>
        public int GetHighLODChunkCount()
        {
            return highLODRingSize * highLODRingSize;
        }

        /// <summary>Chunks held at low fidelity: everything in the grid that is not high-LOD.</summary>
        public int GetLowLODChunkCount()
        {
            int total = chunkCountPerSide * chunkCountPerSide;
            return Mathf.Max(0, total - GetHighLODChunkCount());
        }

        /// <summary>Chunks in the precomputed buffer ring beyond the visible grid.</summary>
        public int GetBufferChunkCount()
        {
            int outer = chunkCountPerSide + bufferRingWidth * 2;
            return Mathf.Max(0, outer * outer - chunkCountPerSide * chunkCountPerSide);
        }

        /// <summary>Resolution actually used, after the preview-quality divisor.</summary>
        public int GetEffectiveResolution()
        {
            int divisor = 1;
            if (previewQuality == PreviewQuality.Draft) divisor = 4;
            else if (previewQuality == PreviewQuality.Medium) divisor = 2;

            // Keep the 2^n+1 shape: divide the span, not the sample count.
            int span = Mathf.Max(1, (resolution - 1) / divisor);
            return span + 1;
        }

        /// <summary>Heightmap samples held in memory across the whole grid.</summary>
        public long GetEstimatedSampleCount()
        {
            long perChunk = (long)GetChunkResolution() * GetChunkResolution();
            int chunks = chunkCountPerSide * chunkCountPerSide + GetBufferChunkCount();
            return perChunk * chunks;
        }

        /// <summary>Rough heightmap memory footprint in megabytes (4 bytes per sample).</summary>
        public float GetEstimatedMemoryMB()
        {
            return GetEstimatedSampleCount() * 4f / (1024f * 1024f);
        }

        // =====================================================================
        //  RESOURCE PROTECTION
        //
        //  A 56 km world is the stress ceiling, and the grid maths lets the two
        //  worst inputs multiply: 15x15 cells at 4097 resolution is 225 heightfields
        //  of 16.8 M samples each - about 15 GB of managed float plus a native
        //  TerrainData and its GPU heightmap texture per cell. Unity does not refuse
        //  that, it allocates until the machine dies, and on a phone it dies early.
        //
        //  So resolution is treated as the give. Cell count is a design decision the
        //  user made (it sets streaming granularity and collider layout); resolution
        //  is a quality dial that can drop to the next 2^n+1 step down without
        //  changing the shape of the world.
        // =====================================================================

        /// <summary>
        /// Heightmap megabytes the world is allowed to hold resident across every cell.
        /// Deliberately a plain constant rather than a setting: it exists to stop the
        /// grid maths from producing an allocation that takes the editor down, and a
        /// budget the user can raise to any number would not do that.
        /// </summary>
        public const float HeightmapBudgetMB = 512f;

        /// <summary>
        /// Same budget, scaled for the device actually running. Mobile GPUs hold the
        /// heightmap as a render texture per cell and have far less headroom, so they
        /// get a quarter of the desktop allowance.
        /// </summary>
        public static float PlatformHeightmapBudgetMB
        {
            get
            {
                if (Application.isMobilePlatform) return HeightmapBudgetMB * 0.25f;
                return HeightmapBudgetMB;
            }
        }

        /// <summary>
        /// Radius, in cells, of the window kept loaded around the player at runtime.
        ///
        /// Derived from the visible grid rather than hard-coded to 2, so a world authored
        /// with a 7x7 or 9x9 grid streams the window its author asked for. The default
        /// 5x5 grid gives radius 2 - the 25-chunk window - and the buffer ring beyond it
        /// is pre-generated so the edge never streams in visibly.
        ///
        /// Edit mode does not use this: the whole world stays resident there, which is
        /// what makes global placement of cities, roads and foliage possible.
        /// </summary>
        public int GetStreamRadius()
        {
            int radius = (Mathf.Max(1, chunkCountPerSide) - 1) / 2;
            return Mathf.Max(1, radius) + Mathf.Max(0, bufferRingWidth);
        }

        /// <summary>
        /// Resolution every cell in this world allocates. One number for the whole grid,
        /// deliberately: Terrain.SetNeighbors refuses to stitch two cells whose
        /// heightmapResolution differs, so anything that varies it per cell, per ring, or
        /// per mode reintroduces the seam warnings and the cracks that come with them.
        ///
        /// It is mode-independent for that reason. Clamping only in edit mode would mean
        /// a large world allocated 257 while authoring and a finer resolution on entering
        /// play, and the already-adopted cells would not match the newly baked ones -
        /// the same mismatch in a new costume.
        /// </summary>
        public int GetChunkResolution()
        {
            int res = GetEffectiveResolution();
            if (chunkCountPerSide <= WholeWorldClampGridSide) return res;

            // 257 is the working floor for an authoring surface: coarse enough that a
            // large grid fits in memory, fine enough that terrain still reads as terrain.
            // What is given up here is bought back by heightmapPixelError at view time,
            // which costs no memory at all.
            return Mathf.Min(res, 257);
        }

        /// <summary>
        /// Whether the world should hold every cell resident instead of streaming a
        /// window. True in the editor while not playing.
        ///
        /// The two modes want opposite things and cannot share one policy. Authoring
        /// needs the whole 10-40 km world on screen at once, because a road or a city
        /// placed against a cell that is not loaded has nothing to sit on. Runtime needs
        /// the opposite: the same world resident in full is thousands of heightmaps, and
        /// on a phone that is an out-of-memory kill rather than a slow frame.
        /// </summary>
        public bool ShouldRenderWholeWorld()
        {
            return !Application.isPlaying;
        }

        /// <summary>
        /// Grid side length past which per-cell resolution gives way to keep the whole
        /// world affordable. Sample count scales with the square of the grid side, so
        /// doubling the side quadruples the bill.
        /// </summary>
        public const int WholeWorldClampGridSide = 16;

        /// <summary>
        /// Highest 2^n+1 resolution whose full grid fits inside the platform budget.
        /// Never returns below 33: past that the terrain stops being a surface, and a
        /// world that is coarse is still better than a world that will not load.
        /// </summary>
        public int GetBudgetedResolution()
        {
            int chunks = chunkCountPerSide * chunkCountPerSide + GetBufferChunkCount();
            if (chunks < 1) return resolution;

            float budgetBytes = PlatformHeightmapBudgetMB * 1024f * 1024f;
            float bytesPerCell = budgetBytes / chunks;

            // 4 bytes per float sample, and a cell is res*res samples.
            float maxSamples = bytesPerCell / 4f;
            if (maxSamples < 4f) return 33;

            int maxRes = Mathf.FloorToInt(Mathf.Sqrt(maxSamples));

            // Snap DOWN to 2^n+1. SnapToPowerOfTwoPlusOne rounds to nearest, which can
            // round up and put us straight back over budget.
            int span = Mathf.Max(1, maxRes - 1);
            int power = Mathf.FloorToInt(Mathf.Log(span, 2f));
            int snapped = (int)Mathf.Pow(2f, power) + 1;

            return Mathf.Clamp(snapped, 33, 4097);
        }

        /// <summary>
        /// True when the current settings would allocate more than the platform budget.
        /// The inspector surfaces this so the user sees why the resolution moved.
        /// </summary>
        public bool ExceedsResourceBudget()
        {
            return GetEstimatedMemoryMB() > PlatformHeightmapBudgetMB;
        }

        // =====================================================================
        //  VALIDATION
        // =====================================================================

        // Clamps everything the maths cannot survive being fed. Unity calls this
        // after any inspector edit and after deserialisation.
        private void OnValidate()
        {
            ValidateSettings();
        }

        /// <summary>
        /// Clamps every setting into a range the maths can survive. Public because
        /// the custom inspector writes fields directly (it has to, for focus-out
        /// commit semantics) and OnValidate does not fire for those writes.
        /// </summary>
        public void ValidateSettings()
        {
            // 1 km to 56 km. The ceiling is the stress target the resource guard below
            // is sized against, not an arbitrary cap.
            worldSizeKm = Mathf.Clamp(worldSizeKm, 1f, 56f);
            maxHeightMeters = Mathf.Clamp(maxHeightMeters, 1f, 10000f);
            resolution = SnapToPowerOfTwoPlusOne(resolution, 33, 4097);

            // Odd counts only: an even grid has no single centre chunk to keep the
            // player in, which the 25+1 recentring logic depends on.
            chunkCountPerSide = Mathf.Clamp(chunkCountPerSide, 1, 15);
            if (chunkCountPerSide % 2 == 0) chunkCountPerSide++;

            highLODRingSize = Mathf.Clamp(highLODRingSize, 1, chunkCountPerSide);
            if (highLODRingSize % 2 == 0) highLODRingSize++;
            highLODRingSize = Mathf.Min(highLODRingSize, chunkCountPerSide);

            bufferRingWidth = Mathf.Clamp(bufferRingWidth, 0, 3);
            recenterThreshold = Mathf.Clamp01(recenterThreshold);

            acuityPixels = Mathf.Clamp(acuityPixels, 0.25f, 8f);
            pixelErrorFloor = Mathf.Clamp(pixelErrorFloor, 0.25f, 20f);
            densityHysteresis = Mathf.Clamp(densityHysteresis, 0f, 4f);
            maxWorldOriginDistance = Mathf.Clamp(maxWorldOriginDistance, 1000f, 1000000f);

            // basemap/detail/tree/billboard distances are not clamped here: they are not
            // authored. RecomputeDerivedDistances resolves all four from the (now clamped)
            // acuity budget, so a change to acuityPixels moves the whole LOD envelope.
            RecomputeDerivedDistances();

            drawDistance = Mathf.Clamp(drawDistance, 50f, 20000f);
            lodBias = Mathf.Clamp(lodBias, 0.5f, 2f);
            maxConcurrentChunks = Mathf.Clamp(maxConcurrentChunks, 1, 16);

            // Resource guard, last so it sees the clamped grid. A 56 km world at a large
            // cell count multiplies into an allocation Unity will attempt and the machine
            // will not survive, so resolution gives way. Only ever lowers: raising the
            // user's setting to fill the budget would be this system overriding a choice
            // it was not asked to make.
            int budgeted = GetBudgetedResolution();
            if (budgeted < resolution) resolution = budgeted;
        }

        /// <summary>
        /// Resolves the four native draw distances from the shared acuity budget. Each
        /// feature (a metre of splat detail, a grass clump, a tree silhouette, a tree's
        /// depth relief) drops below <see cref="acuityPixels"/> pixels at its own range,
        /// computed against the reference view in Geometry/AngularError - so raising the
        /// one budget pulls all four distances in together. This is the whole point: one
        /// dial for the level-of-detail envelope, not four numbers that drift apart.
        /// </summary>
        public void RecomputeDerivedDistances()
        {
            basemapDistance       = Geometry.AngularError.FeatureCullDistanceReference(BasemapDetailMetres, acuityPixels);
            detailObjectDistance  = Geometry.AngularError.FeatureCullDistanceReference(DetailFeatureMetres, acuityPixels);
            treeDistance          = Geometry.AngularError.FeatureCullDistanceReference(TreeSilhouetteMetres, acuityPixels);
            treeBillboardDistance = Geometry.AngularError.FeatureCullDistanceReference(TreeReliefMetres, acuityPixels);
        }

        /// <summary>
        /// Sets the acuity budget from the product quality tier (Mobile Low 4, Mobile
        /// High 2, Desktop 1, flagship 1) and re-derives the four draw distances, so
        /// choosing a device class moves the whole level-of-detail envelope through the
        /// one dial rather than through a second, per-class distance ladder. A one-shot
        /// author or boot action - never per-frame, so it is safe in edit mode.
        /// </summary>
        public void ApplyAcuityTier(UniverseCreator.Optimization.AdaptiveQuality.QualityTier tier)
        {
            acuityPixels = Geometry.AcuityTier.Resolve(tier);
            RecomputeDerivedDistances();
        }

        /// <summary>
        /// Horizontal distance in metres from the world origin to this root. Height is
        /// deliberately ignored: lifting a world on the Y axis keeps full float precision,
        /// because it is the horizontal spread of the heightfield samples that pushes them
        /// into the coarse part of the float range where the surface jitters, z-fights and
        /// cracks between cells. This is the one number the origin clamp compares and the
        /// inspector reports, so both read the same value instead of each rolling its own.
        /// </summary>
        public float OriginDistanceMetres()
        {
            Vector3 p = transform.position;
            return new Vector2(p.x, p.z).magnitude;
        }

        /// <summary>
        /// True when the root sits farther from the origin than <see cref="maxWorldOriginDistance"/>,
        /// i.e. far enough that float precision visibly degrades across the terrain. Pure and
        /// side-effect-free so the self-test and the inspector agree on the threshold.
        /// </summary>
        public bool IsFarFromOrigin()
        {
            return OriginDistanceMetres() > Mathf.Max(0f, maxWorldOriginDistance);
        }

        /// <summary>
        /// Snaps a resolution to the nearest 2^n+1 inside [min,max]. Unity's
        /// TerrainData rejects anything else, and the mipped Matrix downsampling in
        /// the LOD system assumes the same shape.
        /// </summary>
        public static int SnapToPowerOfTwoPlusOne(int value, int min, int max)
        {
            value = Mathf.Clamp(value, min, max);

            int span = Mathf.Max(1, value - 1);
            int power = Mathf.RoundToInt(Mathf.Log(span, 2f));
            int snapped = (int)Mathf.Pow(2f, power) + 1;

            return Mathf.Clamp(snapped, min, max);
        }

        private void OnDisable()
        {
            // A domain reload or scene close must not leave a worker running against
            // a graph that is about to be unloaded.
            CancelGeneration();

            ChunkManager cm = Chunks;
            if (cm != null) cm.CancelAll();
        }
    }
}
