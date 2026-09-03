// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    ChunkManager.cs
//  Purpose: Virtual chunking controller. The world is one finite continent of
//           chunkCountPerSide^2 cells. In the editor every cell is present, so the
//           terrain reads as a single surface. In play the cells stay put (a finite
//           world has no edge to fall off) but LOD follows the player: the centre
//           square is high-resolution with colliders, the outer ring is low-poly,
//           and one buffer ring is precomputed.
//
//  RECYCLING
//  ---------
//  A cell's GameObject + Terrain + TerrainData are pooled and reused for the life
//  of the world. When a cell's LOD changes we regenerate its heights into the same
//  TerrainData (resolution changed in place) - nothing is ever Destroyed. That is
//  what keeps play stable: no per-frame alloc/free of terrain meshes, so no GC
//  spikes and no hitch when the player crosses a cell boundary.
//
//  THREADING
//  ---------
//  Generation runs on Task.Run (worker). This class only ever touches Unity API on
//  the main thread: it snapshots settings + calls PrepareMainThread before dispatch,
//  and applies the finished OutputBag to TerrainData in PumpCompleted (main thread).
//  It never reads a live component or a UnityEngine.Object off-thread.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Virtual chunking controller. Owns the pool of Terrain cells that make up one
    /// world, drives per-cell async generation, and reassigns LOD as the player moves.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkManager : MonoBehaviour
    {
        // One pooled terrain cell. Reused for the world's whole lifetime.
        private class Chunk
        {
            public GameObject go;
            public Terrain terrain;
            public TerrainData data;
            public Coord cell;            // which virtual grid cell this represents
            public LODLevel lod;
            public int resolution;

            // Worker handoff. Generate fills tile off-thread; PumpCompleted applies it.
            public StopToken stop;
            public Task task;
            public TileData tile;
            public volatile bool workerDone;
            public bool applied;

            // Set when this cell was recovered from the scene by its Terrain_x_z name
            // rather than freshly created, so EnsurePool can put it back on the index it
            // already holds terrain for instead of assigning it a new one.
            public bool adopted;

            // True when this chunk's holes map currently has carved quads, so a
            // chunk that never had a tunnel skips the refill work entirely.
            public bool hasCarves;

            // The heightfield exactly as last written to TerrainData. CaptureManualEdits
            // diffs against this to find what the user sculpted. It is the array the
            // graph already allocated, held by reference rather than copied, so the only
            // cost is keeping it from being collected.
            public float[,] appliedHeights;

            // The graph's own output for this cell, BEFORE any water dig was cut into
            // it. This is the only surviving record of where the ground was, and a
            // re-fit needs it for two things: to re-cut a moved channel without
            // deepening the one it cut last time, and to fill a channel back in when
            // the body that made it is deleted or moves away.
            //
            // Held only while a carver actually reaches this cell, and dropped the
            // moment none does - see ReapplyCarves. A world with no water pays nothing,
            // and a world with a river pays for the cells the river crosses rather than
            // for all 25. That gating is the difference between a quarter of a megabyte
            // and six and a half.
            //
            // NOT merged into appliedHeights: they are different questions. This one is
            // "where was the ground", that one is "what is on screen now", and the whole
            // job here is telling those two apart.
            public float[,] graphHeights;

            // True once ApplyChunk has written this cell in THIS session, which is the
            // only condition under which the live surface can be trusted to be undug
            // ground when no snapshot exists.
            //
            // A reload wipes graphHeights but leaves the terrain standing, and
            // RestoreSurfaces seeds appliedHeights by reading that terrain back - so on
            // a reloaded river cell the live surface already HAS the channel in it.
            // Adopting it as pristine would make the next re-fit cut a channel into the
            // channel, then into that one, deepening the river every time the spline was
            // touched. This flag is what refuses that, at the cost of a re-fit doing
            // nothing on a reloaded cell until it has been baked once more.
            public bool bakedThisSession;

            // This cell's four boundary rows as last baked. A neighbour welds against
            // these rather than reading heights back out of TerrainData, and `ready`
            // stays false until a real bake has filled them - which is the guard that
            // stops a flat placeholder from dragging a real edge down into a trench.
            public EdgeSet edges = new EdgeSet();

            public MeshFilter wireframeFilter;
            public MeshRenderer wireframeRenderer;

            // Object-class scatter this cell spawned into the scene. Trees and
            // details are not listed: the TerrainData owns those and overwriting it
            // releases them, while these are real GameObjects that leak otherwise.
            public List<GameObject> scatterObjects = new List<GameObject>();
        }

        // Signature of the world's Terrain Settings as last mirrored onto the cells.
        // Serialized so a domain reload does not look like a settings change and
        // re-push over hand edits made in the native inspector. 0 means never pushed.
        [SerializeField, HideInInspector] private int pushedRenderSignature;

        [Tooltip("What the LOD grid centres on. Defaults to the main camera if unset.")]
        public Transform focus;

        [Tooltip("Material for tunnel walls and floors. Falls back to the terrain material.")]
        public Material tunnelShellMaterial;

        [Tooltip("Colour of the wireframe stand-in drawn while a chunk generates.")]
        public Color wireframePreviewColour = new Color(0.35f, 0.85f, 1f, 1f);

        // ------------------------------------------------------------------
        //  Slice map - the permanent mapping child
        // ------------------------------------------------------------------

        [Tooltip("Draw the virtual chunk boundaries over the whole terrain.")]
        public bool showSliceMap = false;

        [SerializeField] private bool showWireframeGrid = false;

        private void OnDrawGizmos()
        {
            if (showWireframeGrid)
            {
                Gizmos.color = sliceMapColour;
                UniverseCreatorObject uo = Owner;
                if (uo != null && centreCell != null)
                {
                    float size = uo.GetChunkSize();
                    Vector3 center = WorldMinCorner(uo) + new Vector3(centreCell.x * size + size/2f, uo.maxHeightMeters/2f, centreCell.z * size + size/2f);
                    Gizmos.DrawWireCube(center, new Vector3(size, uo.maxHeightMeters, size));
                }
            }
        }

        [Tooltip("Colour of the slice map lines.")]
        public Color sliceMapColour = new Color(0.15f, 1f, 0.25f, 1f);

        [Tooltip("Metres the map floats above the surface so it is not z-fighting the terrain.")]
        public float sliceMapHeightOffset = 2f;

        [Tooltip("Samples along each cell edge. Higher follows the surface more closely.")]
        [Range(1, 32)] public int sliceMapSamplesPerCell = ChunkSliceMap.DefaultSamplesPerCell;

        [Tooltip("Follow the generated surface. Off draws the map flat at the world base.")]
        public bool sliceMapFollowsSurface = true;

        // The one permanent mapping child. Serialized, so it is found again after a
        // domain reload rather than being respawned - it is never created or destroyed
        // in play mode.
        [SerializeField, HideInInspector] private GameObject sliceMapObject;
        [SerializeField, HideInInspector] private MeshFilter sliceMapFilter;
        [SerializeField, HideInInspector] private MeshRenderer sliceMapRenderer;
        private Material sliceMapMaterial;

        // Shared across every chunk: one material for all previews, built lazily because
        // which unlit shader exists depends on the render pipeline in use.
        private Material wireframeMaterial;

        // Speed-adaptive streaming: scale vegetation density and boost LOD ring when moving fast.
        private float currentVegetationDensityMultiplier = 1f;
        private int lodRingBoost = 0;

        // The boost that the current LOD assignment was actually built with. Compared
        // against lodRingBoost so a band change re-classifies even when the focus has
        // not crossed into a new cell.
        private int appliedRingBoost = 0;

        // Last focus position, for measuring speed when the mover has no physics body.
        private Vector3 lastFocusPos;
        private bool hasLastFocusPos;
        private float measuredSpeedKmh;

        private UniverseCreatorObject owner;
        private readonly List<Chunk> pool = new List<Chunk>();
        private readonly Dictionary<Coord, Chunk> byCell = new Dictionary<Coord, Chunk>();

        private Coord centreCell;
        private bool built;
        private int activeWorkers;

        // Earliest realtime at which the next finished chunk may be written to its
        // TerrainData. Enforces UniverseCreatorObject.ChunkYieldDelaySeconds, which is
        // what keeps a large bake from landing as one long main-thread stall.
        private float nextApplyTime;

        // Cells waiting for a free worker slot, highest priority (nearest player) first.
        private readonly List<Chunk> pending = new List<Chunk>();

        // Cells whose stitch weld has already reported a neighbour resolution mismatch.
        // The weld re-runs on every pump, so an unguarded log would repeat the same
        // mismatch dozens of times before the cell reclassifies; keying the warning to
        // the offending cell coordinate states it exactly once.
        private readonly HashSet<Coord> stitchMismatchWarned = new HashSet<Coord>();

        // Set whenever something invalidates the welded state - a queued bake, a
        // resolution change, a world-size change - and cleared by the settle stitch
        // PumpCompleted runs once the queue drains. Without it a run whose last cell
        // was superseded would retire with nothing written and never re-weld.
        private bool settleStitchPending;

        // A one-shot fired the moment the world next settles (see the drain block in
        // PumpCompleted). The regenerate path arms it via RunAfterSettle so the
        // hydrology spawn reads the FINAL surface instead of the flat baseline the
        // async bake starts from - rivers can only trace a real downhill, and lakes
        // can only find the real low ground, once the heights actually exist.
        private System.Action pendingAfterSettle;

        // The per-cell hysteresis gate on the heightmap pixel error (L-49/L-50). Owns
        // the decision of WHEN to write Terrain.heightmapPixelError - the maths of the
        // value itself lives in Geometry.AngularError. Assigning that property rebuilds
        // the patch LOD tree, so the gate skips writes that have not crossed the
        // hysteresis band, which is what lets a still view settle to zero writes.
        private readonly Geometry.TerrainDensityController _density = new Geometry.TerrainDensityController();

        // ---- ECOLOGY SNAPSHOT -------------------------------------------------
        //
        //  The species tables and sample settings every cell of the current rebuild
        //  scores its vegetation against. Resolved once and then shared, for two
        //  separate reasons.
        //
        //  Cost is the lesser one: BuildFor walks the hierarchy for the vegetation
        //  manager and asks it to resolve its settings, and a 15x15 world would pay
        //  that 225 times over for an answer that cannot differ between cells.
        //
        //  Agreement is the real one. ResolveSettings reads the season phase live, so
        //  resolving per cell would let the tables shift mid-bake if the clock moved
        //  between two dispatches - the far side of the world scored against a later
        //  season than the near side, with a species boundary running down the middle.
        //  Species identity is not a seasonal property: a spruce does not become a
        //  birch in autumn. One snapshot per rebuild is what makes the whole world
        //  agree about what grows where.
        //
        //  Null after resolution is the ordinary answer - the world has not switched
        //  ecology on - which is why the flag is separate: without it that null would
        //  be re-derived for every cell.
        //
        //  Dropped when the world is rebuilt or rebound, and at no other time. A
        //  streaming cell that arrives later is deliberately scored against the same
        //  snapshot its neighbours were, because that is what stops a cell loaded at
        //  dusk from growing a different forest than the one beside it.
        private Ecology.EcoScatterContext bakeEcology;
        private bool bakeEcologyResolved;

        public bool IsGenerating
        { 
            get 
            { 
                if (activeWorkers > 0 || pending.Count > 0 || setupDepth > 0 || IsRebuildingAll)
                    return true;

                for (int i = 0; i < pool.Count; i++)
                    if (pool[i].workerDone && !pool[i].applied) return true;

                return false;
            } 
        }
        public bool IsRebuildingAll { get; private set; }

        /// <summary>
        /// Terrain pixel-error writes performed in the last density pass (G-04 done-when):
        /// a camera holding still crosses no hysteresis band and so reads zero here. Exposed
        /// for the debug HUD to prove the still-camera guarantee rather than assert it.
        /// </summary>
        public int DensityWritesThisFrame => _density.WritesThisFrame;

        /// <summary>Terrain pixel-error writes over this manager's lifetime.</summary>
        public int DensityWritesTotal => _density.WritesTotal;

        /// <summary>
        /// Whether this world owes the main thread anything at all. False means the pump
        /// has nothing to do and can be skipped outright.
        /// </summary>
        /// <remarks>
        /// The reason this is a property and not just "IsGenerating" is the handoff gap:
        /// a worker can be finished (workerDone) while its heights are still waiting for
        /// a pump to write them, and at that moment activeWorkers has already been
        /// decremented. Pumping only while IsGenerating would strand exactly those cells.
        ///
        /// It exists because an idle editor was not idle. The edit-mode driver ran a full
        /// pump on every EditorApplication.update tick, and that tick fires far more often
        /// while the mouse is moving - so the world appeared to be recomputing itself in
        /// response to the cursor. Nothing was actually regenerating, but the cost was
        /// real and so was the impression. One boolean in front of the pump is what makes
        /// an idle world free, which is the only acceptable resting state for a tool that
        /// sits in the editor all day.
        /// </remarks>
        public bool HasWork
        {
            get
            {
                return IsGenerating;
            }
        }

        /// <summary>
        /// Non-zero while a rebuild is still laying out cells and allocating heightfields,
        /// before it has queued anything.
        /// </summary>
        /// <remarks>
        /// RebuildAll and RebuildLayout both await EnsureFlatDataAsync, which yields every
        /// few cells. The editor pump runs inside those yields, and until this existed it
        /// saw an empty queue with no active workers, concluded the world had settled, and
        /// cleared IsRebuildingAll - retiring the progress item before the first chunk was
        /// even queued. That is why committing a field showed no background task: the only
        /// window in which the work was advertised was the one the pump kept closing.
        ///
        /// A depth rather than a bool because a second commit can arrive while the first
        /// is still awaiting, and the inner one finishing must not declare the outer done.
        /// </remarks>
        private int setupDepth;

        /// <summary>
        /// Fraction of the world's cells that are not waiting on a bake, 0-1.
        /// </summary>
        /// <remarks>
        /// Reports a definite fraction during setup rather than 1: the queue is empty
        /// while cells are still being allocated, and a full bar that then jumps back to
        /// zero reads as a finished job restarting. Progress.Report treats a negative
        /// value as indeterminate, which is exactly what that phase is.
        /// </remarks>
        public float GetProgress()
        {
            if (setupDepth > 0) return -1f;
            if (pool.Count == 0) return 1f;
            
            int busy = activeWorkers + pending.Count;
            for (int i = 0; i < pool.Count; i++)
                if (pool[i].workerDone && !pool[i].applied) busy++;
                
            int done = pool.Count - busy;
            if (done < 0) done = 0;
            return (float)done / pool.Count;
        }

        /// <summary>
        /// One line describing what the world is doing, for the editor's background
        /// task area. Names the phase and counts the cells, because the whole point of
        /// the indicator is to answer "did my edit actually take effect".
        /// </summary>
        public string GetProgressLabel()
        {
            if (setupDepth > 0) return "Preparing terrain cells...";

            int outstanding = activeWorkers + pending.Count;
            for (int i = 0; i < pool.Count; i++)
                if (pool[i].workerDone && !pool[i].applied) outstanding++;

            if (outstanding <= 0) return "Finishing terrain...";

            int total = pool.Count > 0 ? pool.Count : outstanding;
            int done = Mathf.Max(0, total - outstanding);
            return "Generating terrain - chunk " + (done + 1) + " of " + total;
        }

        // =====================================================================
        //  CARVES
        //
        //  A carve is a tunnel opening: a world-space box where terrain surface is
        //  removed and replaced with a shell (walls + floor). Both halves live here,
        //  OUTSIDE the graph, on purpose - a graph re-run rebuilds heights from
        //  scratch, and a tunnel must not vanish when a noise slider moves.
        //  ApplyChunk re-stamps them after every generation.
        // =====================================================================

        private class Carve
        {
            public int id;
            public Bounds bounds;
            public TunnelShellSettings settings;

            // The shell that seals the cut. Without it the hole is see-through and,
            // worse, collider-free - anything standing there falls out of the world.
            public GameObject shell;
            public Mesh mesh;
            public MeshCollider collider;

            // Shell geometry follows the terrain surface, so it is stale whenever the
            // heightfield under it changes and must be rebuilt after a regeneration.
            public bool shellDirty;
        }

        private readonly List<Carve> carves = new List<Carve>();

        // Handles are ids, not list indices: releasing one tunnel would otherwise
        // shift every later index and silently repoint the caller's handle at
        // someone else's tunnel.
        private int nextCarveId = 1;

        // Parents every shell so the hierarchy stays readable and one SetActive can
        // hide the lot during debugging.
        private Transform shellRoot;

        // =====================================================================
        //  LIFECYCLE
        // =====================================================================

        /// <summary>
        /// Binds this manager to its world component. The manager lives on the
        /// ChunkManager child; owner is the UniverseCreatorObject on the root.
        /// </summary>
        public void Initialize(UniverseCreatorObject owner)
        {
            this.owner = owner;

            // A different world means different species, so the snapshot taken for the
            // previous one is not merely stale - it belongs to another terrain.
            bakeEcology = null;
            bakeEcologyResolved = false;
        }

        private UniverseCreatorObject Owner
        {
            get
            {
                if (owner == null) owner = GetComponentInParent<UniverseCreatorObject>();
                return owner;
            }
        }

        private void OnEnable()
        {
            // Editor drives PumpCompleted itself (no play loop), so only hook play.
            if (Application.isPlaying) RebuildAll();

            // The map mesh is DontSave, so it does not survive a scene load or a
            // domain reload. The child does; only its contents need refilling, and
            // that is a refill rather than a spawn.
            RefreshSliceMap();
        }

        // Seconds between streaming evaluations at runtime.
        //
        // The editor runs the graph once per change and is driven by an explicit pump, so
        // this is a runtime-only concern. In play the temptation is to re-evaluate every
        // frame, but the LOD grid only changes when the focus crosses a cell boundary -
        // at walking pace that is seconds apart, so a per-frame HandleStreaming spends a
        // full ring-distance sweep over every cell to discover nothing changed.
        //
        // That matters most on the platform least able to afford it. A phone throttles
        // the CPU under sustained load and suspends background work outright, so a
        // per-frame sweep does not just cost frames, it makes the device hot enough that
        // the OS starts taking them away. Mobile therefore evaluates less often, and the
        // interval is wall-clock rather than a frame count so a throttled device gets the
        // same world-space behaviour as a fast one - it just does fewer redundant checks
        // between boundary crossings.
        private const float StreamIntervalDesktop = 0.1f;
        private const float StreamIntervalMobile = 0.35f;

        private float nextStreamTime;

        private void Update()
        {
            if (!Application.isPlaying) return;

            // The apply pump stays per-frame: it is capped at one chunk and it is what
            // gets finished ground onto the screen, so delaying it would only make the
            // world fill in more slowly for no saving.
            PumpCompleted();

            if (Time.unscaledTime < nextStreamTime) return;

            float interval = Application.isMobilePlatform
                ? StreamIntervalMobile
                : StreamIntervalDesktop;

            // Scheduled from now rather than by advancing the previous deadline, so a
            // device that stalls or a game that was paused does not wake up owing a burst
            // of catch-up evaluations.
            nextStreamTime = Time.unscaledTime + interval;

            HandleStreaming();

            // Per-view density maintenance (L-49/L-50). HandleStreaming re-runs the LOD
            // pass only when the focus cell moves, so on its own a still player who zooms,
            // resizes the window or changes field of view would keep a stale pixel error.
            // This tick re-derives every resident cell's acuity budget against the LIVE
            // view at the stream cadence and lets the hysteresis gate absorb the churn: a
            // genuinely still view crosses no band and writes nothing, so it stays free.
            //
            // Play-mode only by construction - it sits past the isPlaying guard at the top
            // of Update, so the editor never runs whole-world per-frame density math on top
            // of the full edit-mode render. The one-shot writes at the apply/classify sites
            // keep the value fresh the instant a cell changes; this only catches view
            // changes that move no cell.
            if (owner != null)
            {
                _density.BeginFrame();
                foreach (Chunk c in byCell.Values)
                    ApplyDensity(owner, c);
            }
        }

        private void OnDisable()
        {
            CancelAll();

            // The one shared water material is HideFlags.DontSave and owned by nothing,
            // so without this it is a leaked material per domain reload. The bodies
            // themselves are UCWaterBody objects with their own lifetimes; nothing here
            // takes those away.
            UCWaterMaterials.ReleaseShared();
        }

        // =====================================================================
        //  BUILD / REBUILD
        // =====================================================================

        /// <summary>
        /// (Re)generates every cell in the world at its current LOD. Called on first
        /// enable and whenever a world setting that changes the grid is committed.
        /// </summary>
        public async void RebuildAll()
        {
            UniverseCreatorObject o = Owner;
            if (o == null || o.graph == null) return;

            IsRebuildingAll = true;
            settleStitchPending = true;

            // A rebuild is the moment the world is allowed to change its mind about
            // ecology: a committed inspector edit, a new preset, useEcology being
            // switched on. Dropped rather than re-resolved here, so a world with no
            // ecology never pays for a lookup and the first cell to dispatch is what
            // decides whether one happens at all.
            bakeEcology = null;
            bakeEcologyResolved = false;

            // The vegetation registry goes with it (V-58). Every cell that survives this
            // rebuild re-registers as its scatter lands, so emptying the registry here
            // costs nothing and closes the one case per-chunk release cannot: a cell
            // whose apply is cancelled mid-rebuild would otherwise keep reporting the
            // counts it had before the graph changed. The panel reads zero and climbs
            // back, which is what a rebuild actually looks like.
            UCVegetationSystemManager rebuildVeg = EnsureVegetationManager();
            if (rebuildVeg != null) rebuildVeg.ClearRegistry();

            // Held across the awaits below. The pump ticks inside them, and without this
            // it would see an empty queue, call the world settled, and close the progress
            // item before the first cell was queued.
            setupDepth++;

            try
            {
                EnsurePool(o);

                // Give every cell a real TerrainData, layers and material BEFORE any worker
                // is dispatched. A Terrain whose terrainData is still null draws nothing and
                // offers no sculpt tools, so until this ran the graph path left the world
                // invisible for the whole time the queue took to drain - the inspector showed
                // a full terrain block over ground that was not there yet. The heights are
                // flat for that window and get overwritten per cell as each bake lands.
                await EnsureFlatDataAsync(o);

                centreCell = FocusCell(o);

                // Edit mode holds the entire world; play mode holds a window around the
                // player. Applied before dispatch so the loop below only queues cells that
                // are actually up - a bake into a disabled object is a worker slot spent on
                // terrain nobody can see.
                ApplyStreamWindow(o);

                for (int i = 0; i < pool.Count; i++)
                {
                    Chunk c = pool[i];

                    // Surplus cells from a larger previous grid are parked inactive and still
                    // carry their old coordinates. Baking them would spend worker slots on
                    // terrain nobody can see and write heights into a disabled object.
                    if (c.go == null || !c.go.activeSelf) continue;

                    int ring = DualLODSystem.RingDistance(c.cell.x, c.cell.z, centreCell.x, centreCell.z);
                    c.lod = o.ShouldRenderWholeWorld() ? LODLevel.High : DualLODSystem.Classify(ring, o.highLODRingSize, o.chunkCountPerSide);
                    QueueGenerate(o, c);
                }

                built = true;

                // Seam the flat baseline together now so the world reads as one surface from
                // the first frame; PumpCompleted re-stitches as each bake changes resolution.
                StitchGrid();

                // Map the new layout immediately. It redraws flat here and is refreshed
                // against the real surface once the generation queue drains.
                RefreshSliceMap();
            }
            finally
            {
                setupDepth = Mathf.Max(0, setupDepth - 1);
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> exactly once, on the first pump after the
        /// world next settles - the queue fully drained and the settle stitch done.
        /// </summary>
        /// <remarks>
        /// RebuildAll is async: the terrains still hold the flat/stale baseline while
        /// its bake is in flight, so anything that must read the final surface (the
        /// hydrology spawn) cannot run inline before RebuildAll. Arming it here defers
        /// it to the drain point in PumpCompleted, which is the moment every cell holds
        /// its final heights. A second arming before the first fires replaces it, so a
        /// burst of regenerates spawns water once, from the last one - not once each.
        /// The callback runs after IsRebuildingAll clears and is fed a synchronous
        /// carve path (ReapplyCarves), which cannot re-enter the async bake, so it can
        /// never re-arm itself into a loop.
        /// </remarks>
        public void RunAfterSettle(System.Action action)
        {
            pendingAfterSettle = action;
        }

        /// <summary>
        /// Rebuilds only the chunk layout - pool placement and the slice map - without
        /// generating anything. This is what a chunk-dimension change in the inspector
        /// needs when the world has no graph to run.
        /// </summary>
        public async void RebuildLayout()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return;

            // Same reason as RebuildAll: EnsureFlatDataAsync yields, the pump ticks in
            // those gaps, and a layout rebuild queues no chunks at all - so without this
            // a graphless world resized its cells with no sign anything was happening.
            setupDepth++;

            try
            {
                EnsurePool(o);
                centreCell = FocusCell(o);
                ApplyStreamWindow(o);
                await EnsureFlatDataAsync(o);

                // Propagates a Terrain Settings edit made on the world root. No-ops when
                // nothing changed, so cells keep any values set by hand in the native
                // Terrain inspector.
                SyncTerrainSettings();

                // The flat cells are a real surface the user can sculpt, so they have to be
                // seamed like any other or hand-sculpting a graphless world shows cracks.
                StitchGrid();

                RefreshSliceMap();
            }
            finally
            {
                setupDepth = Mathf.Max(0, setupDepth - 1);
            }
        }

        /// <summary>
        /// Puts the world back on screen after anything that tore down the managed side
        /// of it - opening the scene, a script recompile, leaving play mode - and
        /// re-bakes only the cells that genuinely have no surface. Returns how many
        /// cells it had to queue.
        /// </summary>
        /// <remarks>
        /// This exists because the pool is plain C# state and the scene is not. A reload
        /// wipes every Chunk while leaving 25 Terrain objects standing, and three
        /// separate pieces of that state do not come back on their own:
        ///
        ///   drawHeightmap. Dispatch switches it off to hide the stale surface behind
        ///   the wireframe stand-in. It is a serialised Terrain field, so a bake that
        ///   was still in flight when the editor closed saved it OFF - and reopening the
        ///   scene showed a world that had disappeared, with the water still floating
        ///   where the ground used to be. That is the bug this method was written for.
        ///
        ///   activeSelf. Play mode parks cells outside the streaming window. Edit mode
        ///   wants the whole world up so a road or a city can be placed anywhere in it.
        ///
        ///   the neighbour links. SetNeighbors is runtime-only state and is dropped by
        ///   every reload, so the seams reopen until it is called again.
        ///
        /// What it deliberately does NOT do is regenerate. A cell whose heightfield
        /// survived is left exactly as it is, including any hand sculpting - the whole
        /// point is that coming back from play mode costs nothing and looks identical to
        /// leaving it. Only a cell with no heights at all is queued, which is the one
        /// case where there is nothing to preserve and nothing else to show.
        /// </remarks>
        public int RestoreSurfaces()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return 0;

            EnsurePool(o);

            // Dispatch dereferences o.graph.nodes with no null check, so a world with no
            // graph must never reach QueueGenerate from here. That is not a workaround:
            // for a graphless world flat IS the correct surface, and it is what the
            // graphless layout path produces too. Such a cell gets a TerrainData so it
            // draws and sculpts, and nothing is queued.
            bool canBake = o.graph != null;

            // Whole world in edit mode, streaming window in play. Re-asserted before the
            // per-cell loop so a cell parked by the last play session is awake again
            // before anything asks whether it needs a bake.
            centreCell = FocusCell(o);
            ApplyStreamWindow(o);

            int queued = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.go == null || !c.go.activeSelf) continue;

                // A stand-in that outlived the run that spawned it. The holder is
                // DontSave so it usually dies with the reload, but entering play mode
                // with scene reload disabled keeps it, and it draws over the surface.
                HideWireframe(c);
                if (c.wireframeFilter == null) DisableOrphanedWireframe(c);

                if (c.terrain == null) continue;

                // The one call that turns the surface back on. Cheap and idempotent -
                // it only writes invariants, never a value the user tunes.
                TerrainBuilder.EnsureRenderable(c.terrain, o);

                if (c.data == null) c.data = c.terrain.terrainData;

                if (c.data == null)
                {
                    if (canBake) { QueueGenerate(o, c); queued++; continue; }
                    RestoreFlatCell(o, c);
                    continue;
                }

                c.resolution = c.data.heightmapResolution;

                // Read the surface back if the managed baseline is gone. This is both the
                // sculpt-diff baseline and the answer to "does this cell have terrain",
                // and getting it from the live heightfield is the only source that
                // survived the reload.
                if (c.appliedHeights == null && c.resolution > 0)
                    c.appliedHeights = c.data.GetHeights(0, 0, c.resolution, c.resolution);

                if (canBake && IsEmptySurface(c.appliedHeights))
                {
                    // No heights anywhere in the cell. Either it was never baked or its
                    // TerrainData did not survive, and in both cases a flat placeholder
                    // is not something to preserve.
                    QueueGenerate(o, c);
                    queued++;
                    continue;
                }

                // A real surface, so it counts as landed. Without this the cell stays
                // flagged as an outstanding job and the idle gate never closes.
                c.applied = true;
                c.workerDone = false;

                ApplyDensity(o, c);
            }

            // After the window, so every cell that should be up is up: SetNeighbors is
            // rejected for a disabled terrain and would drop that edge silently.
            StitchGrid();

            if (queued > 0) settleStitchPending = true;

            return queued;
        }

        // Gives one cell a flat TerrainData, synchronously, for the graphless case in
        // RestoreSurfaces. EnsureFlatDataAsync does the same job for the whole pool but
        // is async and yields, and a restore has to leave the world correct by the time
        // it returns - the caller reports on its result and the pump gate is evaluated
        // immediately afterwards.
        private void RestoreFlatCell(UniverseCreatorObject o, Chunk c)
        {
            if (c.terrain == null) return;

            int res = o.GetChunkResolution();
            Vector3 size = new Vector3(o.GetChunkSize(), o.maxHeightMeters, o.GetChunkSize());

            c.resolution = res;
            c.data = TerrainBuilder.GetOrCreateTerrainData(c.data, res, size);
            c.terrain.terrainData = c.data;

            TerrainCollider col = c.go.GetComponent<TerrainCollider>();
            if (col != null) col.terrainData = c.data;

            TerrainBuilder.ApplyLayers(c.data, o);
            TerrainBuilder.EnsureVisibleLayer(c.data);
            TerrainBuilder.EnsureRenderable(c.terrain, o);

            // Baseline for the sculpt diff, so hand-sculpting a graphless world still
            // survives a reload - CaptureManualEdits skips any cell where this is null.
            c.appliedHeights = new float[res, res];
            c.applied = true;
            c.workerDone = false;

            // Flat, and not a bake: it must not be offered to a neighbour as a weld
            // source, or a real edge gets welded down to placeholder height.
            if (c.edges != null) c.edges.ready = false;
        }

        // True when every sample is zero, i.e. the cell is holding a flat placeholder
        // rather than a generated surface.
        //
        // Sampled on a stride rather than exhaustively. This runs on every cell of every
        // world on every scene load, and 25 cells at 257 is 1.6 million samples to
        // establish something a coarse grid answers just as well: any real heightfield
        // has non-zero samples spread all through it, and a graph that legitimately
        // produced dead-flat zero across a whole cell is indistinguishable from an empty
        // one anyway - re-baking it costs one background pass and produces the same
        // surface.
        private static bool IsEmptySurface(float[,] heights)
        {
            if (heights == null) return true;

            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            if (resZ < 1 || resX < 1) return true;

            int strideZ = Mathf.Max(1, resZ / 16);
            int strideX = Mathf.Max(1, resX / 16);

            for (int z = 0; z < resZ; z += strideZ)
                for (int x = 0; x < resX; x += strideX)
                    if (heights[z, x] > 0.0001f) return false;

            return true;
        }

        // Switches off a stand-in whose Chunk no longer holds a reference to it. The
        // holder is found by name because that reference is exactly what the reload
        // destroyed; it is not destroyed here because this can run during a scene load,
        // where Destroy on a child is not always legal.
        private void DisableOrphanedWireframe(Chunk c)
        {
            if (c.go == null) return;

            Transform t = c.go.transform.Find("WireframePreview");
            if (t == null) return;

            MeshRenderer r = t.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = false;
        }

        /// <summary>
        /// Gives every pooled cell a TerrainData if it has none, leaving the heights at
        /// zero. A Terrain component with a null terrainData draws nothing and offers no
        /// sculpting tools, so a world with no graph would look like the system had
        /// failed rather than like an empty world waiting for one - C-03. Cells that
        /// already carry generated data are left untouched.
        /// </summary>
        private async Task EnsureFlatDataAsync(UniverseCreatorObject o)
        {
            Vector3 size = new Vector3(o.GetChunkSize(), o.maxHeightMeters, o.GetChunkSize());
            int res = o.GetChunkResolution();

            // Live cells only. A shrink parks surplus cells inactive rather than
            // destroying them, and allocating a heightmap for a disabled object is
            // wasted memory.
            int live = Mathf.Min(pool.Count, o.chunkCountPerSide * o.chunkCountPerSide);

            for (int i = 0; i < live; i++)
            {
                Chunk c = pool[i];
                if (c.terrain == null) continue;

                // An adopted cell carries whatever resolution it was baked at, which is
                // not necessarily what this world allocates now - the grid may have grown
                // past the whole-world clamp, or preview quality may have moved since.
                // One odd cell is enough to make SetNeighbors reject its whole
                // neighbourhood, so a stale heightfield is dropped and re-baked rather
                // than kept. Its sculpt survives: CaptureManualEdits has already lifted
                // the deltas into the override layer, and ApplyChunk folds them back in.
                if (c.data != null)
                {
                    if (c.data.heightmapResolution != res)
                    {
                        c.data = null;
                        c.appliedHeights = null;
                        c.applied = false;

                        // The snapshot describes a heightfield at the old resolution.
                        // Kept, it would be cloned into an array of the wrong size on
                        // the next re-fit and every index in the carve would be wrong.
                        c.graphHeights = null;

                        // Its stored boundary belongs to a heightfield that no longer
                        // exists. Leaving it readable would let a neighbour weld to a
                        // resolution the cell has already dropped.
                        if (c.edges != null) c.edges.ready = false;
                    }
                    else
                    {
                        // If the physical chunk size changed (e.g. world size changed), update it immediately.
                        // Otherwise, the chunk maintains its old size while sitting at its new offset,
                        // creating a massive visible gap ("trench") until background generation finishes.
                        if (c.data.size != size) c.data.size = size;
                    }
                }

                if (c.data == null)
                {
                    c.resolution = res;
                    c.data = TerrainBuilder.GetOrCreateTerrainData(c.data, res, size);
                    c.terrain.terrainData = c.data;

                    TerrainCollider col = c.go.GetComponent<TerrainCollider>();
                    if (col != null) col.terrainData = c.data;

                    // Layers first: an unlayered terrain renders untextured, which reads
                    // as broken rather than as empty.
                    TerrainBuilder.ApplyLayers(c.data, o);

                    // Record the flat surface as the baseline so sculpting a graphless
                    // world is still captured: CaptureManualEdits diffs against
                    // appliedHeights and skips any cell where it is null. Without this,
                    // connecting a graph after a hand sculpt would erase it - C-04.
                    c.appliedHeights = new float[res, res];
                    c.applied = true;

                    // Flat, and not a bake. It must not be offered to a neighbour as a
                    // weld source: welding a real edge against a flat placeholder is
                    // exactly what carved a groove along every boundary before.
                    if (c.edges != null) c.edges.ready = false;

                    // A brand-new cell has Unity's constructor defaults, so the world's
                    // Terrain Settings are pushed once here. From now on only
                    // SyncTerrainSettings touches them, which is what lets a hand edit
                    // in the native inspector stick.
                    TerrainBuilder.ConfigureTerrain(c.terrain, o);
                }

                // Re-asserted on every layout pass, cells with existing data included.
                // Both calls only touch render state and neither can disturb generated
                // heights or splatmaps - EnsureVisibleLayer is a no-op once layers
                // exist, EnsureRenderable only decides whether the chunk draws at all.
                // Guarding them behind "data == null" meant a material or layer fix
                // could never reach an already-generated scene without a full
                // regeneration, which is how an invisible surface stayed invisible.
                //
                // The tunable Terrain Settings are deliberately NOT pushed here; that
                // is SyncTerrainSettings, which fires only when the world values change
                // so a hand edit in the native inspector survives.
                TerrainBuilder.EnsureVisibleLayer(c.data);
                TerrainBuilder.EnsureRenderable(c.terrain, o);

                if (c.terrain != null && o.ShouldRenderWholeWorld())
                {
                    // Explicitly render every chunk at full fidelity in edit mode:
                    // ApplyDensity resolves the whole-world preview at zero distance, so the
                    // acuity budget collapses to its finest uniform value for every cell.
                    ApplyDensity(o, c);
                }

                if (i > 0 && i % 4 == 0) await Task.Yield();
            }
        }

        /// <summary>
        /// Whether this cell currently carries a surface worth welding.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT <c>Chunk.applied</c>. That flag means "no bake is in flight
        /// for this cell", which Dispatch clears on every cell RebuildAll queues - so
        /// gating the weld on it made the pre-bake stitch a no-op across the whole grid,
        /// and left SetNeighbors handing out nulls for every neighbour still baking.
        ///
        /// A cell that has an active Terrain and a heightfield has a surface the user can
        /// see, whether or not a newer version of it is on the way, and welding it is
        /// never worse than leaving the seam open: the bake that lands next overwrites
        /// the row, and the drain-time stitch in PumpCompleted welds it again for good.
        /// </remarks>
        private static bool HasSurface(Chunk c)
        {
            return c != null
                && c.terrain != null
                && c.terrain.terrainData != null
                && c.go != null
                && c.go.activeSelf;
        }

        /// <summary>
        /// Links every live cell to its four neighbours so Unity stitches the edges into
        /// one continuous surface. This is what makes the virtual slicing invisible in
        /// game: without it each cell computes its own edge normals and LOD skirt in
        /// isolation, so the seams show as lit cracks and the LOD transition pops at
        /// every boundary. Unity only recomputes that after SetNeighbors, and it must be
        /// re-run whenever a cell's TerrainData or resolution changes - which for a
        /// streaming LOD cell is every reclassification.
        /// </summary>
        public void StitchGrid()
        {
            if (Owner == null) return;

            // ----------------------------------------------------------------
            // PASS 1 — SEAM WELD BY DETERMINISTIC OWNERSHIP
            //
            // Height agreement is normally already exact: every creator samples
            // absolute world position, so a cell's east column and its neighbour's
            // west column are the same world coordinates. This pass exists for the
            // cases where that is not enough - a non-local modifier reaching past
            // the ghost margin, a hand sculpt, a cell adopted from the scene.
            //
            // It does NOT average. One cell owns each seam, chosen by coordinate,
            // and the other copies. Copy is idempotent and order-independent, so
            // running this every pump converges instead of drifting - and a cell
            // that has not baked yet contributes nothing at all, because welding
            // against a flat placeholder is what carved the trenches before.
            // ----------------------------------------------------------------

            foreach (var kvp in byCell)
            {
                Chunk c = kvp.Value;
                if (!HasSurface(c) || c.appliedHeights == null) continue;
                if (!WeldChunkEdges(c)) continue;

                // The welded field is what the cell now shows, so it is also what a
                // later neighbour must weld against and what a manual-edit diff must
                // measure from.
                c.terrain.terrainData.SetHeightsDelayLOD(0, 0, c.appliedHeights);
                SeamWelder.Capture(c.appliedHeights, c.edges);
            }

            // ----------------------------------------------------------------
            // PASS 2 — Unity SetNeighbors + SyncHeightmap + Flush
            // Must happen AFTER all height data is final so Unity bakes the
            // correct LOD skirts, edge normals, and physics heightfields.
            // ----------------------------------------------------------------

            foreach (var kvp in byCell)
            {
                Coord c = kvp.Key;
                Chunk chunk = kvp.Value;

                if (chunk == null || chunk.terrain == null || chunk.terrain.terrainData == null
                    || chunk.go == null || !chunk.go.activeSelf)
                    continue;

                int res = chunk.terrain.terrainData.heightmapResolution;

                Terrain left   = ResolveStitchNeighbour(c, new Coord(c.x - 1, c.z), res);
                Terrain right  = ResolveStitchNeighbour(c, new Coord(c.x + 1, c.z), res);
                Terrain top    = ResolveStitchNeighbour(c, new Coord(c.x, c.z + 1), res);
                Terrain bottom = ResolveStitchNeighbour(c, new Coord(c.x, c.z - 1), res);

                chunk.terrain.SetNeighbors(left, top, right, bottom);
                chunk.terrain.terrainData.SyncHeightmap();
                chunk.terrain.Flush();
            }
        }

        /// <summary>
        /// Resolves one neighbour for the SetNeighbors weld. Returns the neighbour terrain
        /// only when it holds a real bake at the SAME heightmap resolution; an absent,
        /// inactive, still-generating or placeholder cell simply yields null and is not
        /// welded against. A neighbour that HAS a surface but a DIFFERENT resolution is a
        /// genuine misconfiguration - mixed-resolution cells cannot share an edge - so it
        /// is reported once per offending cell coordinate. The weld re-runs on every pump,
        /// so an unguarded log here would repeat the same mismatch dozens of times before
        /// the cell reclassifies; <see cref="stitchMismatchWarned"/> keys the warning to
        /// the cell that owns the seam so it is stated exactly once.
        /// </summary>
        private Terrain ResolveStitchNeighbour(Coord owner, Coord neighbour, int resolution)
        {
            if (!byCell.TryGetValue(neighbour, out Chunk nc) || !HasSurface(nc)) return null;

            int nres = nc.terrain.terrainData.heightmapResolution;
            if (nres == resolution) return nc.terrain;

            if (stitchMismatchWarned.Add(owner))
            {
                Debug.LogWarning(
                    $"[UniverseCreator] Terrain stitch skipped at cell ({owner.x},{owner.z}): "
                    + $"neighbour ({neighbour.x},{neighbour.z}) has heightmap resolution {nres} "
                    + $"but this cell is {resolution}. Mixed-resolution cells cannot share an "
                    + "edge, so the seam is left unwelded until both settle on the same tier.");
            }
            return null;
        }

        /// <summary>
        /// Returns the neighbour at an offset from this cell, but only when it holds a
        /// real bake at the same resolution. Anything else - absent, inactive, still
        /// generating, a flat placeholder, a different resolution - returns null and is
        /// simply not welded against.
        ///
        /// That last part is the correction. The old weld deliberately included cells
        /// that had no bake yet, on the reasoning that a half-weld beats an open seam.
        /// It does not: a cell with no bake reads as the flat baseline, and averaging a
        /// real edge against flat drags it halfway to the floor. Every boundary got a
        /// groove, and because the result was copied into appliedHeights it became the
        /// baseline that the next bake was measured against.
        /// </summary>
        private Chunk WeldSource(Coord cell, int dx, int dz, int res)
        {
            Chunk n;
            if (!byCell.TryGetValue(new Coord(cell.x + dx, cell.z + dz), out n)) return null;
            if (!HasSurface(n)) return null;
            if (n.edges == null || !n.edges.Matches(res)) return null;
            return n;
        }

        /// <summary>
        /// Snaps one cell's four boundaries onto the values its neighbours already hold,
        /// wherever the neighbour owns that seam. Operates on the staged heightfield, so
        /// it must run before the upload.
        ///
        /// Ownership is a pure function of coordinate - lower X owns a vertical seam,
        /// lower Z owns a horizontal one, and the lowest cell of a 2x2 block owns the
        /// corner sample. Both sides therefore reach the same verdict without talking to
        /// each other, which is what makes the result independent of which cell finished
        /// baking first.
        ///
        /// Returns true when the field was touched.
        /// </summary>
        private bool WeldChunkEdges(Chunk c)
        {
            if (c == null) return false;

            float[,] h = (c.tile != null && c.tile.outputs != null && c.tile.outputs.heightmap != null)
                ? c.tile.outputs.heightmap
                : c.appliedHeights;
            if (h == null) return false;

            int res = h.GetLength(0);
            if (h.GetLength(1) != res) return false;

            int feather = SeamWelder.DefaultFeather;
            Coord cell = c.cell;
            bool touched = false;

            // A cell copies the seams its neighbours own, and keeps the ones it owns.
            // West and south are owned by the lower coordinate, so those are the two
            // directions this cell defers on; east and north it is the authority for.
            Chunk w = WeldSource(cell, -1, 0, res);
            if (w != null && SeamWelder.OwnsEastSeam(w.cell.x, cell.x))
            {
                SeamWelder.ApplyWest(h, w.edges.east, feather);
                touched = true;
            }

            Chunk s = WeldSource(cell, 0, -1, res);
            if (s != null && SeamWelder.OwnsNorthSeam(s.cell.z, cell.z))
            {
                SeamWelder.ApplySouth(h, s.edges.north, feather);
                touched = true;
            }

            // An edge pass writes its whole row including both end samples, so the
            // corners are resolved last or the edge would simply overwrite them.
            // Priority order is fixed, so every one of the four cells meeting at a
            // corner picks the same source even when some of them are missing.
            if (ResolveCorner(cell, -1, -1, res, ref h, 0, 0)) touched = true;
            if (ResolveCorner(cell, +1, -1, res, ref h, res - 1, 0)) touched = true;
            if (ResolveCorner(cell, -1, +1, res, ref h, 0, res - 1)) touched = true;

            // The north-east corner is owned by this cell unless a lower-coordinate
            // cell shares it, which for the NE block is never the case - this cell IS
            // the lowest of that block. Nothing to copy.

            if (touched) c.appliedHeights = h;
            return touched;
        }

        /// <summary>
        /// Writes the authoritative value into one corner sample of this cell.
        ///
        /// The corner shared by a 2x2 block belongs to the block's lowest cell. This
        /// looks up that owner and, when it is not resident, falls back through the two
        /// cardinal neighbours in a fixed order so all four cells still agree.
        /// </summary>
        private bool ResolveCorner(Coord cell, int dx, int dz, int res,
                                   ref float[,] h, int cornerX, int cornerZ)
        {
            // Lowest cell of the block this corner belongs to.
            int blockX = dx < 0 ? cell.x - 1 : cell.x;
            int blockZ = dz < 0 ? cell.z - 1 : cell.z;
            if (SeamWelder.OwnsCorner(cell.x, cell.z, blockX, blockZ)) return false;

            // The diagonal owner first, then the two cardinals, in a fixed order.
            Chunk diag = WeldSource(cell, dx, dz, res);
            if (diag != null)
            {
                SeamWelder.ApplyCorner(h, cornerX, cornerZ, CornerOf(diag.edges, -dx, -dz, res));
                return true;
            }

            Chunk horiz = WeldSource(cell, dx, 0, res);
            if (horiz != null && SeamWelder.OwnsEastSeam(Mathf.Min(horiz.cell.x, cell.x), Mathf.Max(horiz.cell.x, cell.x))
                && horiz.cell.x < cell.x)
            {
                SeamWelder.ApplyCorner(h, cornerX, cornerZ, CornerOf(horiz.edges, -dx, dz, res));
                return true;
            }

            Chunk vert = WeldSource(cell, 0, dz, res);
            if (vert != null && vert.cell.z < cell.z)
            {
                SeamWelder.ApplyCorner(h, cornerX, cornerZ, CornerOf(vert.edges, dx, -dz, res));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads one corner sample out of a neighbour's stored edges. sx/sz select which
        /// corner of that neighbour: negative means its west/south end, positive its
        /// east/north end.
        /// </summary>
        private static float CornerOf(EdgeSet e, int sx, int sz, int res)
        {
            float[] column = sx < 0 ? e.west : e.east;
            return column[sz < 0 ? 0 : res - 1];
        }

        /// <summary>
        /// After this cell lands, pushes the seams it owns into neighbours that are
        /// already on screen.
        ///
        /// Without this, a cell arriving late would weld itself correctly but leave the
        /// earlier neighbour holding the edge it computed before this one existed. With
        /// it, the seam ends up at the owner's value no matter which side landed first -
        /// which is the whole guarantee the user is asking for.
        /// </summary>
        private void ReweldNeighbours(Chunk c)
        {
            if (c == null || c.edges == null || !c.edges.ready) return;

            int res = c.edges.resolution;
            int feather = SeamWelder.DefaultFeather;
            Coord cell = c.cell;

            // This cell owns the seam with the cell to its east and the one to its north.
            Chunk east = WeldSource(cell, +1, 0, res);
            if (east != null && east.appliedHeights != null
                && SeamWelder.OwnsEastSeam(cell.x, east.cell.x))
            {
                SeamWelder.ApplyWest(east.appliedHeights, c.edges.east, feather);
                east.terrain.terrainData.SetHeightsDelayLOD(0, 0, east.appliedHeights);
                SeamWelder.Capture(east.appliedHeights, east.edges);
            }

            Chunk north = WeldSource(cell, 0, +1, res);
            if (north != null && north.appliedHeights != null
                && SeamWelder.OwnsNorthSeam(cell.z, north.cell.z))
            {
                SeamWelder.ApplySouth(north.appliedHeights, c.edges.north, feather);
                north.terrain.terrainData.SetHeightsDelayLOD(0, 0, north.appliedHeights);
                SeamWelder.Capture(north.appliedHeights, north.edges);
            }
        }


        // Allocates (or resizes) the pool so there is exactly one Chunk per world cell,
        // positioned at its world-space corner. Pool objects are never destroyed here;
        // a shrink parks the surplus inactive so a later grow can reuse them.
        private void EnsurePool(UniverseCreatorObject o)
        {
            int side = o.chunkCountPerSide;
            int needed = side * side;

            byCell.Clear();

            // Grow. Cells are parented under the world's Terrain child, not under this
            // manager - C-03. The manager is the controller; the native Terrains are
            // terrain, and the hierarchy should say so. This manager's only own child
            // is the slice map.
            Transform host = TerrainRoot();

            // Reclaim the cells already in the scene before growing any new ones.
            if (pool.Count == 0) AdoptExistingCells(host);

            // Put every adopted cell back on the index it was named for, before the
            // grow loop below starts handing out positions by index.
            RebindAdoptedOrder(side, needed);

            while (pool.Count < needed)
            {
                Chunk c = new Chunk();
                c.go = new GameObject("Terrain");
                c.go.transform.SetParent(host, false);
                c.terrain = c.go.AddComponent<Terrain>();
                c.go.AddComponent<TerrainCollider>();
                pool.Add(c);
            }

            // Existing pool objects predating C-03 sit under the manager; move them
            // once so an already-authored scene lands in the same shape as a new one.
            for (int i = 0; i < pool.Count; i++)
                if (pool[i].go != null && pool[i].go.transform.parent != host)
                    pool[i].go.transform.SetParent(host, true);

            // Park surplus (world shrank) without destroying - recycling contract.
            for (int i = needed; i < pool.Count; i++)
                SetChunkLive(pool[i], false);

            float cellSize = o.GetChunkSize();
            Vector3 worldMin = WorldMinCorner(o);

            for (int i = 0; i < needed; i++)
            {
                Chunk c = pool[i];
                int cx = i % side;
                int cz = i / side;
                c.cell = new Coord(cx, cz);

                if (c.go != null)
                {
                    SetChunkLive(c, true);
                    c.go.name = "Terrain_" + cx + "_" + cz;
                    c.go.transform.position = worldMin + new Vector3(cx * cellSize, 0f, cz * cellSize);
                }

                byCell[c.cell] = c;
            }
        }

        // Re-adopts the Terrain cells already sitting under the Terrain child.
        //
        // The pool is plain C# state on a MonoBehaviour, so a script recompile, a scene
        // reload, or entering play wipes it - while the GameObjects it was tracking
        // survive in the scene. Without this the next rebuild sees an empty pool and
        // grows a whole second set of cells beside the first: 25 more Terrain objects
        // every reload, each one z-fighting with the original it was stacked on. That
        // is both the cluttered hierarchy and a surface that renders wrong.
        //
        // Reclaiming them also self-heals a scene that already collected duplicates -
        // everything past the cell count is parked inactive by the caller.
        private void AdoptExistingCells(Transform host)
        {
            if (host == null) return;

            for (int i = 0; i < host.childCount; i++)
            {
                Transform t = host.GetChild(i);

                Terrain terr = t.GetComponent<Terrain>();
                if (terr == null) continue;

                Chunk c = new Chunk();
                c.go = t.gameObject;
                c.terrain = terr;
                c.data = terr.terrainData;

                // The name is the only surviving record of which cell this object was.
                // Everything else the pool knew died with the domain. Adopting in child
                // order instead would work only while the hierarchy happened to be in
                // index order - reparent one cell, or let Unity return the children in
                // any other order, and cell (0,0) inherits (3,1)'s heightmap: the world
                // comes back scrambled, each tile holding a neighbour's terrain.
                int px, pz;
                if (TryParseCellName(t.name, out px, out pz))
                {
                    c.cell = new Coord(px, pz);
                    c.adopted = true;
                }

                // Whatever is on the surface now is the baseline, so nothing already
                // generated gets treated as an outstanding job.
                c.applied = true;

                if (c.data != null)
                {
                    c.resolution = c.data.heightmapResolution;

                    // Read the live heightfield back as the diff baseline. It has to
                    // happen here rather than lazily at capture time: a baseline seeded
                    // once a sculpt already exists would diff the sculpt against itself
                    // and report no change, losing it. Leaving it null instead makes
                    // CaptureManualEdits skip the cell entirely, so a brush stroke made
                    // after a recompile was dropped by the next regeneration.
                    //
                    // Reading the real surface is also what makes this safe - the risk
                    // with a seeded baseline is a zeroed array, which would read as
                    // "the user flattened everything" and push the whole heightfield
                    // into the override layer.
                    int res = c.resolution;
                    if (res > 0) c.appliedHeights = c.data.GetHeights(0, 0, res, res);
                }

                pool.Add(c);
            }
        }

        /// <summary>
        /// Parses "Terrain_&lt;x&gt;_&lt;z&gt;" back into its cell coordinate. Returns false
        /// for any other name, including the bare "Terrain" a cell carries between
        /// creation and its first layout pass.
        /// </summary>
        private static bool TryParseCellName(string name, out int cx, out int cz)
        {
            cx = 0;
            cz = 0;
            if (string.IsNullOrEmpty(name)) return false;
            if (!name.StartsWith("Terrain_")) return false;

            string body = name.Substring(8);
            int split = body.IndexOf('_');
            if (split <= 0 || split >= body.Length - 1) return false;

            return int.TryParse(body.Substring(0, split), out cx) &&
                   int.TryParse(body.Substring(split + 1), out cz);
        }

        /// <summary>
        /// Reorders the pool so index i holds the cell that was already named for
        /// position i, keeping every adopted terrain on the ground it was baked for.
        /// Cells with no adopted match fill the gaps in whatever order they arrive.
        /// </summary>
        private void RebindAdoptedOrder(int side, int needed)
        {
            bool any = false;
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].adopted) { any = true; break; }
            }
            if (!any) return;

            Chunk[] slots = new Chunk[pool.Count];
            List<Chunk> loose = new List<Chunk>();

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (!c.adopted) { loose.Add(c); continue; }

                int idx = c.cell.z * side + c.cell.x;

                // Out of range, or two objects claiming the same cell - a duplicate from
                // a scene that stacked before this existed. It goes in the loose list and
                // is parked inactive by the caller rather than fighting for the slot.
                if (c.cell.x < 0 || c.cell.x >= side || c.cell.z < 0 || c.cell.z >= side ||
                    idx >= needed || slots[idx] != null)
                {
                    c.adopted = false;
                    loose.Add(c);
                    continue;
                }

                slots[idx] = c;
            }

            int next = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null) continue;
                if (next >= loose.Count) break;
                slots[i] = loose[next++];
            }

            pool.Clear();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null) pool.Add(slots[i]);

            // Anything left over (more loose cells than slots) is still pool state and
            // must not be dropped on the floor - it is parked, not destroyed.
            for (; next < loose.Count; next++) pool.Add(loose[next]);
        }

        // =====================================================================
        //  STREAMING (play mode)
        // =====================================================================

        // Recentres LOD on the player. Cells stay put; only their LOD (and therefore
        // resolution + collider) changes, and only the ones that actually changed are
        // regenerated. This is the recycle step - same TerrainData, new heights.
        private void HandleStreaming()
        {
            UniverseCreatorObject o = Owner;
            if (o == null || !built) return;

            // Speed-adaptive logic: scale vegetation density and widen high-LOD ring based
            // on current velocity. 20+ km/h (vehicles) and 80+ km/h (fast vehicles).
            UpdateSpeedAdaptive(o);

            Coord newCentre = FocusCell(o);

            // A band change has to re-classify even when the centre cell has not moved.
            // Accelerating from rest inside one cell widens the ring without crossing any
            // boundary, so returning early here left the wider ring unapplied until the
            // next crossing - which is precisely when it is already too late.
            if (newCentre == centreCell && lodRingBoost == appliedRingBoost) return;

            centreCell = newCentre;
            appliedRingBoost = lodRingBoost;

            // The boost counts rings of radius, but Classify takes the side length of the
            // centre square, so one more ring is two more of side. Adding it raw made the
            // side even and the (size-1)/2 radius truncated straight back to the unboosted
            // width - the 20-80 km/h band was widening nothing at all.
            int highSide = o.highLODRingSize + lodRingBoost * 2;

            // Radius of the streaming window, in cells. Edit mode has no window - the
            // whole world stays up so cities, roads and foliage can be placed anywhere -
            // but at runtime a 40 km world is thousands of cells and holding every
            // heightmap resident is what puts a phone out of memory.
            int keepRadius = o.GetStreamRadius() + lodRingBoost;

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.go == null) continue;

                // Surplus cells from a larger previous grid are parked by EnsurePool and
                // hold stale coordinates; they must stay parked.
                if (!IsLiveCell(o, c)) continue;

                int ring = DualLODSystem.RingDistance(c.cell.x, c.cell.z, centreCell.x, centreCell.z);

                // Outside the window the object is disabled rather than destroyed. The
                // TerrainData stays attached, so walking back into a cell re-enables a
                // surface that is already correct - no bake, no allocation, no hitch.
                // Destroying instead would return the memory but pay a full regeneration
                // to come back, which is the stutter this whole system exists to avoid.
                bool shouldBeLive = ring <= keepRadius;
                if (c.go.activeSelf != shouldBeLive)
                {
                    SetChunkLive(c, shouldBeLive);

                    // Re-linking is only meaningful once the set of live cells settles,
                    // and SetNeighbors on a disabled terrain is rejected.
                    streamWindowChanged = true;
                }

                if (!shouldBeLive) continue;

                LODLevel next = DualLODSystem.Classify(ring, highSide, o.chunkCountPerSide);

                if (next != c.lod)
                {
                    c.lod = next;

                    // Pixel error is the LOD now, and it costs nothing to change - no
                    // re-bake, no reallocation. Applying it here means a cell that only
                    // crossed a fidelity band never queues a worker at all; only a cell
                    // that has no heights yet does.
                    if (c.terrain != null)
                        ApplyDensity(o, c);

                    if (c.terrain != null)
                    {
                        TerrainCollider col = c.go.GetComponent<TerrainCollider>();
                        if (col != null)
                            col.enabled = DualLODSystem.ShouldHaveCollider(next, o.generateCollider);
                    }

                    // Only a cell that was never baked needs the worker. Re-baking on a
                    // band change was pure waste once resolution stopped varying: the
                    // heightfield it would produce is identical to the one already there.
                    if (c.appliedHeights == null) QueueGenerate(o, c);
                }
            }

            if (streamWindowChanged)
            {
                streamWindowChanged = false;
                StitchGrid();
            }
        }

        // Set when a cell entered or left the streaming window this pass, so the grid is
        // re-linked once at the end rather than per cell.
        private bool streamWindowChanged;

        /// <summary>
        /// Sets which cells are resident, by mode.
        ///
        /// Edit mode activates the whole grid: authoring a 10-40 km world means placing
        /// cities, roads and foliage anywhere in it, and a cell that is not loaded has
        /// nothing for them to sit on. Memory is held instead by the resolution clamp in
        /// UniverseCreatorObject.GetChunkResolution and by pixel-error LOD, neither of
        /// which costs a seam.
        ///
        /// Play mode activates only the window around the focus. Distant cells are
        /// disabled rather than destroyed, so their TerrainData stays attached and
        /// walking back into one re-enables a surface that is already correct.
        /// </summary>
        private void ApplyStreamWindow(UniverseCreatorObject o)
        {
            bool wholeWorld = o.ShouldRenderWholeWorld();
            int keepRadius = o.GetStreamRadius();

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.go == null) continue;

                // Surplus cells from a shrunk grid stay parked whatever the mode.
                if (!IsLiveCell(o, c))
                {
                    SetChunkLive(c, false);
                    continue;
                }

                bool live = wholeWorld;
                if (!live)
                {
                    int ring = DualLODSystem.RingDistance(c.cell.x, c.cell.z,
                                                          centreCell.x, centreCell.z);
                    live = ring <= keepRadius;
                }

                SetChunkLive(c, live);
            }
        }

        /// <summary>
        /// Whether a pooled cell belongs to the current grid at all. Surplus cells from a
        /// larger previous world keep their old coordinates and must never be baked,
        /// activated or stitched - they are parked storage until the world grows again.
        /// </summary>
        private static bool IsLiveCell(UniverseCreatorObject o, Chunk c)
        {
            int side = o.chunkCountPerSide;
            return c.cell.x >= 0 && c.cell.x < side &&
                   c.cell.z >= 0 && c.cell.z < side;
        }

        // Adjusts LOD ring width and vegetation multiplier from how fast the focus moves.
        //
        // WHY TWO BANDS AND NOT ONE
        // -------------------------
        // Generation has to be ready before the mover arrives, and the span between the
        // slowest and fastest thing in a world is large - walking is ~5 km/h, a car is
        // 80+. One ring width cannot serve both: sized for the walker it generates too
        // late for the car, and sized for the car it generates far too early for the
        // walker and wastes memory on cells nobody reaches.
        //
        //   under 20 km/h  - on foot. Base ring, full vegetation.
        //   20-80 km/h     - vehicles. +1 ring of radius, density 0.5x.
        //   over 80 km/h   - fast vehicles. +2 rings of radius, density 0.25x.
        //
        // The boost is in rings of RADIUS. HandleStreaming doubles it before handing it
        // to Classify, which counts the centre square's side. Mixing those two units is
        // what made the middle band a no-op.
        //
        // Density drops as the ring widens because the extra ring is more cells to fill:
        // the budget is spent on having ground ready rather than on foliage detail that
        // is a blur at that speed anyway.
        private void UpdateSpeedAdaptive(UniverseCreatorObject o)
        {
            Transform f = ResolveFocus();
            if (f == null)
            {
                currentVegetationDensityMultiplier = 1f;
                lodRingBoost = 0;
                hasLastFocusPos = false;
                return;
            }

            // Rigidbody is the common vehicle case; CharacterController is on-foot.
            // Unity 6 replaced Rigidbody.velocity with linearVelocity.
            Rigidbody rb = f.GetComponent<Rigidbody>();
            CharacterController cc = rb == null ? f.GetComponent<CharacterController>() : null;

            Vector3 vel = Vector3.zero;
            if (rb != null) vel = rb.linearVelocity;
            else if (cc != null) vel = cc.velocity;

            float speedKmh = vel.magnitude * 3.6f;

            // Fall back to the positional delta when neither component reports motion.
            // A vehicle driven straight off its transform - or a kinematic body, or an
            // animation-driven rig - reads 0 km/h from the components above, so it would
            // never earn the wider ring and would outrun generation at exactly the speeds
            // the bands exist to cover. Horizontal only: the grid is 2D, so a lift or a
            // fall should not widen it.
            Vector3 here = f.position;
            if (hasLastFocusPos && Time.deltaTime > 0.0001f)
            {
                Vector3 step = here - lastFocusPos;
                step.y = 0f;

                float moved = step.magnitude / Time.deltaTime * 3.6f;

                // Smoothed, or a single stutter frame would spike the band and thrash the
                // ring width. Fast attack, slow release: widening late is the failure that
                // shows, and narrowing early costs nothing but a little memory.
                float blend = moved > measuredSpeedKmh ? 0.5f : 0.1f;
                measuredSpeedKmh += (moved - measuredSpeedKmh) * blend;

                // A teleport or a recentre is not travel, and would otherwise read as
                // several thousand km/h and pin the ring boost at its maximum.
                if (moved < 2000f) speedKmh = Mathf.Max(speedKmh, measuredSpeedKmh);
            }
            lastFocusPos = here;
            hasLastFocusPos = true;

            if (speedKmh > 80f)
            {
                currentVegetationDensityMultiplier = 0.25f;
                lodRingBoost = 2;
            }
            else if (speedKmh > 20f)
            {
                currentVegetationDensityMultiplier = 0.5f;
                lodRingBoost = 1;
            }
            else
            {
                currentVegetationDensityMultiplier = 1f;
                lodRingBoost = 0;
            }
        }

        /// <summary>
        /// Modules 3 & 4 (vegetation) multiply their density by this when placing foliage
        /// and props. 1.0 at low speed, 0.5 above 20 km/h, 0.25 above 80 km/h.
        /// </summary>
        public float VegetationDensityMultiplier { get { return currentVegetationDensityMultiplier; } }

        // =====================================================================
        //  GENERATION DISPATCH
        // =====================================================================

        // Snapshots per-cell settings on the main thread, primes nodes, then queues
        // the cell. The actual Task.Run is deferred to PumpQueue so maxConcurrentChunks
        // is honoured.
        private void QueueGenerate(UniverseCreatorObject o, Chunk c)
        {
            // Supersede any run already in flight for this cell.
            if (c.stop != null) c.stop.RequestStop();

            // This cell's heights are about to be rewritten, so whatever weld its edges
            // currently hold is provisional. Recorded here rather than inferred at apply
            // time because a superseded run never reaches the apply path at all.
            settleStitchPending = true;

            if (!pending.Contains(c)) pending.Add(c);
            PumpQueue(o);
        }

        private void PumpQueue(UniverseCreatorObject o)
        {
            int limit = Mathf.Max(1, o.maxConcurrentChunks);

            // Nearest-first: closer cells (lower ring distance) generate before far ones.
            pending.Sort((a, b) =>
            {
                int ra = DualLODSystem.RingDistance(a.cell.x, a.cell.z, centreCell.x, centreCell.z);
                int rb = DualLODSystem.RingDistance(b.cell.x, b.cell.z, centreCell.x, centreCell.z);
                return ra.CompareTo(rb);
            });

            while (pending.Count > 0 && activeWorkers < limit)
            {
                Chunk c = pending[0];
                pending.RemoveAt(0);
                Dispatch(o, c);
            }
        }

        private void Dispatch(UniverseCreatorObject o, Chunk c)
        {
            // GetChunkResolution, not the raw field: it is the one accessor that returns
            // the same number for every cell in the world, in both modes. A bake that
            // used anything else would hand back a cell whose heightmapResolution
            // differed from its neighbours - exactly the mismatch SetNeighbors refuses.
            int resolution = DualLODSystem.ResolutionFor(c.lod, o.GetChunkResolution());
            c.resolution = resolution;

            // Prime nodes on the main thread (textures/curves copied to plain arrays).
            if (o.graph.nodes != null)
            {
                for (int i = 0; i < o.graph.nodes.Count; i++)
                {
                    Generator node = o.graph.nodes[i];
                    if (node != null) node.PrepareMainThread();
                }
            }

            float cellSize = o.GetChunkSize();
            Vector3 worldMin = WorldMinCorner(o);
            Vector3 cellPos = worldMin + new Vector3(c.cell.x * cellSize, 0f, c.cell.z * cellSize);
            Vector3 cellSize3 = new Vector3(cellSize, o.maxHeightMeters, cellSize);
            Vector3 fullWorldSize = o.GetWorldSize();

            Graph g = o.graph;
            StopToken stop = new StopToken();
            c.stop = stop;
            c.workerDone = false;
            c.applied = false;

            CoordRect rect = new CoordRect(new Coord(0, 0), new Coord(resolution, resolution));
            Coord cell = c.cell;

            // Resolved here on the main thread for the same reason the nodes are primed
            // above: it reads the vegetation manager, and through it the lighting and
            // weather managers, none of which a worker may touch. The cell gets its own
            // sampler out of it, so two cells baking at once cannot bind each other's
            // height field. Null when the world runs without ecology, which every
            // scatter node reads as "place exactly what Modules 1 and 2 placed".
            //
            // W-05: measured at the cell's centre, not its south corner, so a cell has
            // one latitude and the trees inside it do not each get their own - and so
            // that two neighbouring cells differ by exactly one cell's worth of
            // latitude rather than sharing an edge value.
            Ecology.EcoScatterContext ecology = EcologyForCell(
                o, NorthFractionOf(cellPos.z + cellSize * 0.5f, worldMin.z, fullWorldSize.z));

            // Stand-in goes up before the worker starts, so there is never a frame where
            // the chunk is neither previewed nor finished.
            ShowWireframe(o, c);

            activeWorkers++;
            c.task = Task.Run(() =>
            {
                try
                {
                    TileData tile = new TileData(cell, rect, cellPos, cellSize3, o.maxHeightMeters);
                    tile.SetFullWorld(worldMin, fullWorldSize);
                    tile.ecology = ecology;

                    GraphEvaluator evaluator = new GraphEvaluator(g);
                    evaluator.Evaluate(tile, stop);

                    if (!stop.StopRequested) c.tile = tile;
                }
                catch (System.OperationCanceledException) { }
                catch (System.Exception ex)
                {
                    Debug.LogError("Universe Creator: chunk " + cell + " generation failed - " + ex);
                }
                finally
                {
                    c.workerDone = true;
                }
            });
        }

        /// <summary>
        /// This cell's view of the current rebuild's ecology, resolving the rebuild-level
        /// snapshot on first use. Main thread only.
        /// </summary>
        /// <remarks>
        /// The tables inside the returned context are the shared ones - they are written
        /// once and read for the rest of the bake - while the sampler is this cell's
        /// alone. That split is the whole point of ForChunk: sharing the expensive part
        /// costs nothing, and sharing the sampler would score one cell's forest against
        /// another cell's mountains.
        ///
        /// The rebuild-level snapshot is resolved once and carries the world's authored
        /// latitude; northFraction01 is what turns it into this cell's latitude, in a
        /// world that spans latitude. (W-05)
        /// </remarks>
        private Ecology.EcoScatterContext EcologyForCell(UniverseCreatorObject o,
                                                         float northFraction01)
        {
            if (!bakeEcologyResolved)
            {
                bakeEcology = EcoScatterContextBuilder.BuildFor(o);
                bakeEcologyResolved = true;
            }

            return bakeEcology != null ? bakeEcology.ForChunk(northFraction01) : null;
        }

        /// <summary>
        /// Where a world Z sits between the world's south edge (0) and north edge (1).
        ///
        /// A degenerate world size returns the centre rather than dividing by zero: a
        /// zero-extent world has no north and no south, and 0.5 leaves it on its
        /// authored latitude, which is the only defensible answer. (W-05)
        /// </summary>
        private static float NorthFractionOf(float worldZ, float worldMinZ, float worldSizeZ)
        {
            if (worldSizeZ <= 0f) return 0.5f;
            return Mathf.Clamp01((worldZ - worldMinZ) / worldSizeZ);
        }

        // =====================================================================
        //  MAIN-THREAD APPLY
        // =====================================================================

        /// <summary>
        /// How many finished chunks are written to their TerrainData in one pump.
        ///
        /// The bake itself is off-thread, but the apply is not and cannot be: SetHeights,
        /// SetAlphamaps and the collider rebuild are all main-thread Unity calls, and on a
        /// large heightfield each one is a visible stall. Applying every chunk that
        /// happened to finish since the last pump is what stacked those stalls into one
        /// multi-second freeze - the generation was asynchronous, the landing was not.
        ///
        /// One per pump means the cost is bounded by a single cell no matter how many
        /// completed while the editor was busy, so the world fills in visibly rather than
        /// arriving all at once behind a locked UI. The remainder keeps until next pump;
        /// nothing is dropped.
        /// </summary>
        private const int MaxAppliesPerPump = 1;

        /// <summary>
        /// Applies finished background generations to their TerrainData, at most
        /// MaxAppliesPerPump per call. Runs each frame in play, and is called by the
        /// editor preview driver from EditorApplication.update in edit mode. Main thread
        /// only.
        /// </summary>
        public bool PumpCompleted()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return false;

            bool freed = false;
            bool wrote = false;
            bool firedAfterSettle = false;
            int applies = 0;

            // Wall clock, not frame count. This pump is driven by Update in play mode
            // and by EditorApplication.update in edit mode, and those tick at wildly
            // different rates - a frame-based gap would mean one spacing while playing
            // and another entirely while building. realtimeSinceStartup is the only
            // clock that reads the same from both.
            bool yieldElapsed = Time.realtimeSinceStartup >= nextApplyTime;

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (!c.workerDone || c.applied) continue;

                // Stop handing out work for this pump once the budget is spent, but keep
                // the loop cheap - the remaining cells stay flagged and land next pump.
                if (applies >= MaxAppliesPerPump) break;

                // The delay gates the expensive part only. A superseded or empty run is
                // still retired below, because holding those back would stall the worker
                // slot they occupy without any main-thread cost to justify it.
                bool willWrite = c.tile != null && (c.stop == null || !c.stop.StopRequested);
                if (willWrite && !yieldElapsed) break;

                // Reclaim the worker slot exactly once per dispatch.
                activeWorkers = Mathf.Max(0, activeWorkers - 1);
                c.applied = true;
                freed = true;

                // A superseded run left stale/partial data; skip applying it. It still
                // cost a worker slot, so it does not count against the apply budget.
                //
                // The stand-in has to come down here even though nothing is being
                // written. Dispatch put it up AND set terrain.drawHeightmap = false to
                // hide the stale surface underneath it, and ApplyChunk is the only other
                // place that undoes either - so a run that retires through this line
                // used to leave the cell showing a preview that would never resolve,
                // over ground that was switched off. drawHeightmap is a serialised
                // Terrain field, so that state went into the scene file and came back
                // after a restart as a world that had vanished.
                if (!willWrite) { HideWireframe(c); continue; }

                ApplyChunk(o, c);
                c.tile = null;
                wrote = true;
                applies++;

                nextApplyTime = Time.realtimeSinceStartup + o.ChunkYieldDelaySeconds;
            }

            if (freed) PumpQueue(o);

            // A bake can hand its cell a different resolution than its neighbours were
            // stitched against, and Unity only recomputes edge normals and the LOD skirt
            // when SetNeighbors is called again. Re-stitching only when something was
            // actually written keeps this off idle pumps.
            if (wrote) StitchGrid();

            // The map traces the surface, so it is stale until the last chunk lands.
            // Refreshing once the queue drains costs one line mesh per settle instead
            // of one per chunk.
            // setupDepth keeps a rebuild that is still allocating cells from being read
            // as settled. Its queue is legitimately empty at that point, and treating
            // that as "done" both closed the progress item early and ran the settle
            // stitch against a grid whose bakes had not been dispatched yet.
            bool drained = activeWorkers == 0 && pending.Count == 0 && setupDepth == 0;

            if (drained)
            {
                // SETTLE STITCH.
                //
                // The per-apply stitch above welds a cell against whatever had landed
                // at that moment, and ApplyChunk overwrites the whole heightfield -
                // including edge rows a previous pass had already averaged. So the
                // last cell to land leaves its own seams welded but can have undone
                // an earlier neighbour's, and a run that was superseded retires with
                // wrote == false and skips the stitch entirely. Either way the grid
                // could settle with an unwelded edge, which is the trench.
                //
                // One unconditional pass once nothing is in flight closes that: every
                // cell now holds its final heights, so this weld is the one that
                // sticks. This is what the ChunkManager inspector's Rebuild Cells
                // button was doing by hand.
                if (settleStitchPending || freed)
                {
                    StitchGrid();
                    settleStitchPending = false;
                }

                if (freed) RefreshSliceMap();

                // Water re-fits itself to the ground that just landed, but only when
                // something actually landed. An idle pump drains every tick, and
                // re-solving river courses on each of those would spend the whole edit
                // loop fitting rivers to terrain that has not moved.
                if (wrote || freed) SettleCarvers(o);

                IsRebuildingAll = false;

                // The world has settled - every cell now holds its final heights. Fire
                // the one-shot the regenerate path armed for exactly this moment (the
                // hydrology spawn, which must read the real surface). Cleared BEFORE the
                // call so a rebuild the callback happens to trigger arms the next one
                // cleanly rather than being wiped. The callback carves synchronously
                // (ReapplyCarves), which does not touch the worker queue, so it cannot
                // re-enter this async drain and cannot loop.
                if (pendingAfterSettle != null)
                {
                    System.Action afterSettle = pendingAfterSettle;
                    pendingAfterSettle = null;
                    afterSettle();
                    firedAfterSettle = true;
                }
            }
            else
            {
                // Something is still in flight, so the grid is not final yet and owes
                // itself a settle stitch whenever it does drain.
                settleStitchPending = true;
            }

            // Reported so the edit-mode driver can repaint the Scene view. Writing
            // TerrainData does not itself mark the view dirty, so without a repaint a
            // finished chunk can sit invisible until something else happens to redraw
            // it - which reads as the graph edit having done nothing - C-14. The
            // after-settle callback carves new water into the terrain, so it needs the
            // same repaint or the freshly spawned lakes/rivers would not show until a
            // later nudge.
            return wrote || firedAfterSettle;
        }

        /// <summary>
        /// Tells every carver in this world that the terrain under it has settled, and
        /// re-cuts once if any of them moved.
        /// </summary>
        /// <remarks>
        /// This is the automatic half of the water/procedural contract. The dig itself
        /// is already safe without it - ApplyOutputs folds every registered carve into
        /// each chunk as it is written, so a regeneration cannot erase a channel. What
        /// this adds is placement: a river whose valley has just moved re-fits its
        /// course to the new ground rather than staying where the old valley was.
        ///
        /// Each carver decides for itself whether it moves (see OnTerrainSettled), and
        /// the re-cut is hoisted out of the loop because ReapplyCarves rewrites whole
        /// chunks - doing it per body would rewrite the same chunk once per river.
        /// </remarks>
        private void SettleCarvers(UniverseCreatorObject o)
        {
            List<IUCTerrainCarver> carvers = WaterCarveRegistry.For(o);
            if (carvers.Count == 0) return;

            bool moved = false;

            for (int i = 0; i < carvers.Count; i++)
            {
                UCWaterBody body = carvers[i] as UCWaterBody;
                if (body == null) continue;

                bool refitted;
                try
                {
                    refitted = body.OnTerrainSettled();
                }
                catch (System.Exception e)
                {
                    // A malformed spline must not take the whole pump down with it, or
                    // one bad river stops every chunk in the world from finishing.
                    Debug.LogError("Universe Creator: water re-fit failed on '" +
                                   body.name + "'. " + e.Message, body);
                    continue;
                }

                if (!refitted) continue;

                moved = true;

                // The channel moved, so the surface sitting on it has to move with it.
                // Both read the same solved course, so this is a mesh rebuild and not a
                // second solve.
                body.RebuildSurface();
            }

            if (moved) ReapplyCarves();
        }

        private void ApplyChunk(UniverseCreatorObject o, Chunk c)
        {
            if (c.terrain == null) return;

            Vector3 size = new Vector3(o.GetChunkSize(), o.maxHeightMeters, o.GetChunkSize());
            c.data = TerrainBuilder.GetOrCreateTerrainData(c.data, c.resolution, size);

            c.terrain.terrainData = c.data;
            TerrainCollider col = c.go.GetComponent<TerrainCollider>();

            bool wantCollider = DualLODSystem.ShouldHaveCollider(c.lod, o.generateCollider);
            if (col != null)
            {
                col.terrainData = c.data;
                col.enabled = wantCollider;

                // Friction/bounce for the terrain surface. Unity's default is
                // slippery enough that vehicles slide on mild slopes.
                col.sharedMaterial = o.terrainPhysicsMaterial;
            }

            // Layers must be assigned before splatmaps are written, because splatmap
            // channels index them and a chunk with no layers renders untextured.
            TerrainBuilder.ApplyLayers(c.data, o);

            // Weld this cell's boundaries to its neighbours BEFORE the upload.
            //
            // Order is the whole point. The previous design uploaded raw graph output
            // and repaired the seams afterwards, so the next cell to land overwrote the
            // repair and reopened the seam - the "cells not waiting for each other"
            // symptom. Welding the staged array means the first and only write already
            // carries the shared edge, and nothing later can undo it.
            WeldChunkEdges(c);

            Bounds applyBounds = ChunkWorldBounds(c);

            // PRISTINE GROUND, CAPTURED BEFORE THE DIG.
            //
            // ApplyOutputs cuts the water channel into this very array, in place, and
            // the pump drops c.tile on the next line - so once we return, where the
            // ground USED to be is gone. A re-fit then has nothing to work from: it
            // cannot re-cut a moved river without carving deeper into the trench it
            // already made, and it cannot fill an abandoned channel back in at all.
            // That is what made ReapplyCarves a no-op in every case that mattered.
            //
            // Only for cells a carver actually reaches. On a world with no water this
            // is null everywhere and costs nothing; on a world with a river it holds
            // the handful of cells the river crosses. ReapplyCarves releases it again
            // as soon as no body reaches the cell.
            c.graphHeights = WaterCarveRegistry.CountAffecting(o, applyBounds) > 0
                ? CloneHeights(c.tile.outputs.heightmap)
                : null;

            // From here the live surface and the snapshot are known to agree about this
            // cell, which is what lets a later re-fit trust one to reconstruct the other.
            c.bakedThisSession = true;

            // Manual sculpt edits are folded in here, so they ride on top of whatever
            // the graph just produced instead of being erased by it.
            TerrainBuilder.ApplyOutputs(c.data, c.tile.outputs, o,
                                        o.overrideLayer, applyBounds, o.GetMetersPerPixel());

            // Publish the welded boundary for neighbours that bake later, and re-weld
            // the neighbours already on screen so a late arrival cannot leave a stale
            // edge behind it. Both operate on cells that are already final, so this
            // converges rather than drifting.
            SeamWelder.Capture(c.tile.outputs.heightmap, c.edges);
            ReweldNeighbours(c);

            // Phase 4: SyncHeightmap and Flush are deferred to StitchGrid to happen strictly after SetNeighbors

#if UNITY_EDITOR
            if (c.terrain != null) UnityEditor.EditorUtility.SetDirty(c.terrain);
            if (c.data != null) UnityEditor.EditorUtility.SetDirty(c.data);
#endif

            // Only the visibility invariants. Pushing the full settings block here
            // would revert a hand edit every time the cell regenerated, which for a
            // streaming LOD cell is constantly.
            TerrainBuilder.EnsureRenderable(c.terrain, o);

            // Fidelity for this cell. The LOD mechanism is now the acuity pixel-error
            // budget (L-49/L-50), not resolution: every cell keeps the same
            // heightmapResolution (SetNeighbors rejects any mismatch) and distance is
            // expressed as tolerated screen-space error instead. Changing it is free - no
            // re-bake, no reallocation, and no seam to repair - so a cell can change ring
            // every frame without cost.
            ApplyDensity(o, c);

            // Remember what we just wrote, so a later sculpt can be diffed against it.
            c.appliedHeights = c.tile.outputs.heightmap;

            // Scatter. Released first: this cell may be a recycled pool object that
            // was standing somewhere else a moment ago, and its old props are still
            // parented to it at their old world positions.
            //
            // The owner id is derived once and used for three things that must agree:
            // the GPU instancing owner, the registry entry released here, and the
            // registry entry opened by Apply below. ScatterOwnerOf is the one place
            // it is derived, so a release and an apply cannot disagree about which
            // chunk they mean.
            int scatterOwner = ScatterOwnerOf(c);
            UCVegetationSystemManager veg = EnsureVegetationManager();

            ScatterApplier.Release(c.scatterObjects, veg, scatterOwner);

            // Time the apply so the diagnostics panel can show what a rescatter costs
            // (V-52). Wall clock around the actual main-thread work is the only honest
            // figure - the registry times nothing itself, on purpose. Reported per
            // apply and replaced each time, the same grain as the missing-channel
            // report below.
            // Where spawned props hang in the hierarchy. Per cell by default, which is
            // what lets a cell parking itself take its props with it (V-56). The opt-in
            // escape hatch parents them under the vegetation manager instead - honoured
            // here, at the one place the parent is chosen, so the default path passes the
            // exact same Transform it always did and Apply's signature does not move. The
            // trade is on the field's tooltip: manager-parented props do not park with
            // the cell, so per-chunk recycling no longer hides them.
            Transform scatterParent = c.go != null ? c.go.transform : null;
            if (veg != null && veg.parentUnderVegetationSystem) scatterParent = veg.transform;

            double scatterT0 = Time.realtimeSinceStartupAsDouble;
            ScatterApplier.Apply(c.terrain, c.data, c.tile.outputs, o.scatterLibrary,
                                 scatterParent,
                                 o.enableGPUInstancing ? Instancing() : null,
                                 scatterOwner, veg,
                                 c.scatterObjects);
            if (veg != null)
                veg.ReportBuildMilliseconds(
                    (float)((Time.realtimeSinceStartupAsDouble - scatterT0) * 1000.0));

            // Reconcile the registry's live flag against what the cell actually is.
            // A cell can be parked by the streaming pass between the moment its heights
            // were dispatched to a worker and the moment they land here, and Apply above
            // does not know that - it opens a registry entry either way. Without this,
            // an out-of-window cell would report its instances as drawing. SetChunkLive
            // owns the flag, so the reconciliation goes through it rather than calling
            // Suspend directly, and it is idempotent when nothing changed. (V-58)
            if (veg != null) SetChunkLive(c, c.go != null && c.go.activeSelf);

            // Hand the registry what this cell's graph failed to supply, so the
            // inspector's validation panel can name it (V-51). Read here rather than
            // inside the bake because this is the first main-thread moment the mask
            // exists, and a serialized field would outlive the graph that produced it.
            //
            // The sampler is per chunk and the mask is a property of the graph, so any
            // landed cell answers the question. Gated on IsBound: an unbound sampler
            // has an empty mask that means "never asked", not "nothing missing", and
            // reporting it would show a clean bill of health for a graph with no
            // ecology wired at all.
            if (veg != null && c.tile != null && c.tile.ecology != null &&
                c.tile.ecology.sampler != null && c.tile.ecology.sampler.IsBound)
            {
                veg.ReportMissingChannels(c.tile.ecology.sampler.MissingOutputs);
            }

            // No water step here. Water is not per-chunk any more: a lake that
            // straddles four cells is one UCWaterBody with one surface and one volume,
            // owned by the world's WaterSystem, and it re-fits itself to the ground in
            // SettleCarvers above once every cell has landed. What used to be here
            // built a throwaway slab per chunk per body, which is how a single lake
            // became four rectangles that did not line up.

            // Wind. Uploads whatever Wind Output baked for this cell, which is the
            // step that makes the sway maps visible instead of merely correct.
            ApplyWind(c);

            // Re-stamp tunnels last. A regeneration rewrites the holes map from the
            // graph, so without this every re-run would fill the tunnels back in.
            if (carves.Count > 0 || c.hasCarves) StampCarves(c);

            // The shells over this chunk now sit against a new heightfield, so their
            // wall tops no longer meet the surface. Mark them for rebuild.
            MarkShellsDirtyOver(ChunkWorldBounds(c));
            RebuildDirtyShells();

            // Real surface is up; the stand-in has done its job.
            HideWireframe(c);
        }

        // =====================================================================
        //  WIND
        // =====================================================================

        // The world's wind manager, found once and cached. Null is a legitimate
        // state: a world whose graph has no wind branch never creates one.
        private UCWindManager windManager;

        /// <summary>
        /// Pushes this chunk's baked sway maps into the world's wind manager.
        ///
        /// Sway describes shelter - the lee of a ridge, the inside of a wood - and
        /// the graph bakes it per chunk, but the shader reads one global texture.
        /// So each chunk composites into its own rectangle of that texture rather
        /// than replacing it, which is what SetSwayRegion is for.
        ///
        /// A graph with no Wind Output leaves the arrays null and this returns
        /// immediately, having created nothing. That matters: worlds that predate
        /// the wind system must keep behaving exactly as they did.
        /// </summary>
        private void ApplyWind(Chunk c)
        {
            if (c == null || c.tile == null) return;

            OutputBag bag = c.tile.outputs;
            if (bag == null || bag.windSwayAmplitude == null) return;
            if (bag.windSwaySize.x < 2 || bag.windSwaySize.y < 2) return;

            UniverseCreatorObject o = Owner;
            if (o == null) return;

            UCWindManager wind = EnsureWindManager();
            if (wind == null) return;

            // The chunk's own rectangle, falling back to the cell's footprint when
            // Wind Output did not record one - an older graph, or a bake that ran
            // before SetFullWorld was called.
            Vector2 regionMin = bag.windSwayWorldMin;
            Vector2 regionSize = bag.windSwayWorldSize;

            if (regionSize.x <= 1e-3f || regionSize.y <= 1e-3f)
            {
                float cell = o.GetChunkSize();
                Vector3 min = WorldMinCorner(o);
                regionMin = new Vector2(min.x + c.cell.x * cell, min.z + c.cell.z * cell);
                regionSize = new Vector2(cell, cell);
            }

            Vector3 worldMin3 = WorldMinCorner(o);
            Vector3 worldSize3 = o.GetWorldSize();

            wind.SetSwayRegion(bag.windSwayAmplitude, bag.windSwayPhase,
                               bag.windSwaySize.x, bag.windSwaySize.y,
                               regionMin, regionSize,
                               new Vector2(worldMin3.x, worldMin3.z),
                               new Vector2(worldSize3.x, worldSize3.z));
        }

        /// <summary>
        /// Finds the world's wind manager, or makes one. Created on the world root
        /// and not on a child, so a user who wants to tune the wind finds it on the
        /// object they already have selected instead of hunting through the pool.
        ///
        /// Not hidden and not DontSave: unlike the generated meshes and textures,
        /// this is a settings component the user is meant to see, edit and keep.
        /// </summary>
        private UCWindManager EnsureWindManager()
        {
            if (windManager != null) return windManager;

            UniverseCreatorObject o = Owner;
            if (o == null) return null;

            // A Weather System Manager, when one exists, is the owner of wind: it
            // adopts an existing manager rather than creating a second one, and it
            // is where the wind settings are edited from. Keep that contract - this
            // fallback only fires for a world built before the manager existed.
            UCWeatherSystemManager weather = o.GetComponentInChildren<UCWeatherSystemManager>(true);
            if (weather != null)
            {
                windManager = weather.EnsureWind();
                if (windManager != null) return windManager;
            }

            windManager = o.GetComponentInChildren<UCWindManager>(true);
            if (windManager != null) return windManager;

            windManager = o.gameObject.AddComponent<UCWindManager>();
            return windManager;
        }


        // =====================================================================
        //  VEGETATION
        // =====================================================================

        // The world's vegetation manager, found once and cached. Null is a legitimate
        // state and stays one: a world with no vegetation manager scatters with
        // pre-Module-3 behaviour.
        private UCVegetationSystemManager vegetationManager;

        /// <summary>
        /// Finds the world's vegetation manager, or returns null. (V-54)
        ///
        /// Cached exactly like the wind manager above, and for the same reason: the
        /// lookup walks the hierarchy, the answer is a world fact that cannot differ
        /// from one cell to the next, and this is called every time a cell lands. The
        /// applier used to do this walk itself once per chunk; it now takes the result
        /// as a parameter, so there is one lookup per world instead of one per cell and
        /// exactly one place that knows where the manager lives.
        ///
        /// UNLIKE EnsureWindManager, THIS CREATES NOTHING. Wind has to exist for a
        /// shader to sample, so that one adds a component as a last resort. Vegetation
        /// does not: a world built before Module 3 has no vegetation manager and must
        /// keep scattering exactly as it did, and silently adding a component on the
        /// first regeneration would change every one of those worlds without asking.
        /// The manager is added by the user, or by the setup that builds the hierarchy.
        ///
        /// A null cache keeps searching rather than latching, so a manager added after
        /// the first cell has landed is picked up on the next one. A destroyed manager
        /// compares equal to null and is re-resolved the same way.
        /// </summary>
        private UCVegetationSystemManager EnsureVegetationManager()
        {
            if (vegetationManager != null) return vegetationManager;

            UniverseCreatorObject o = Owner;
            if (o == null) return null;

            vegetationManager = o.GetComponentInChildren<UCVegetationSystemManager>(true);
            return vegetationManager;
        }

        /// <summary>
        /// The identity a chunk is known by outside this manager: its GPU instancing
        /// owner and its vegetation registry key, which are deliberately the same
        /// number. Derived in one place so a release and an apply cannot disagree
        /// about which chunk they mean. (V-58)
        ///
        /// GetEntityId is stable for the lifetime of the GameObject, and this manager
        /// never destroys one, so the id survives the cell being recycled onto other
        /// coordinates - which is exactly what makes it a chunk id rather than a cell
        /// id. Zero for a chunk with no object; nothing registers against it.
        /// </summary>
        private static int ScatterOwnerOf(Chunk c)
        {
            if (c == null || c.go == null) return 0;
            return c.go.GetEntityId().GetHashCode();
        }

        /// <summary>
        /// Activates or parks a cell, and tells the vegetation registry which it was.
        /// THE ONLY PLACE A CHUNK'S OBJECT IS ACTIVATED OR DEACTIVATED. (V-58)
        ///
        /// WHY THIS IS A CHOKE POINT AND NOT A CALL BESIDE EACH SetActive
        /// -------------------------------------------------------------
        /// Six places used to flip a cell: two in EnsurePool as a shrunk grid parks its
        /// surplus, one in HandleStreaming as the window moves, two in ApplyStreamWindow
        /// as the mode decides what is resident, and the mode switch itself. A registry
        /// call written next to each of them would be correct on the day it was written
        /// and wrong the first time a seventh appeared, and the symptom would be a
        /// diagnostics panel that slowly disagrees with the world - the least debuggable
        /// kind of wrong. With one door, the invariant is structural: a cell cannot
        /// change liveness without the registry hearing about it.
        ///
        /// The registry is SUSPENDED rather than released, because this manager destroys
        /// nothing. A parked cell keeps its TerrainData, its trees and its props; what
        /// it stops doing is drawing, colliding and batching. See SuspendChunk.
        ///
        /// The registry is told even when activeSelf already matches, and that is not
        /// waste: EnsurePool activates every live index unconditionally on a rebuild,
        /// and a cell that was parked by a previous smaller grid needs its tallies back
        /// whether or not its object needed touching. Suspend and Resume are both
        /// idempotent, so the redundant calls cost a dictionary lookup and a branch.
        /// </summary>
        private void SetChunkLive(Chunk c, bool live)
        {
            if (c == null || c.go == null) return;

            if (c.go.activeSelf != live) c.go.SetActive(live);

            UCVegetationSystemManager veg = EnsureVegetationManager();
            if (veg == null) return;

            int owner = ScatterOwnerOf(c);

            if (live) veg.ResumeChunk(owner);
            else veg.SuspendChunk(owner);
        }


        // =====================================================================
        //  TUNNEL CARVING  (called by RoadCityMasterLinker, Module 7)
        // =====================================================================

        /// <summary>
        /// Cuts a tunnel opening through the terrain and seals it with generated
        /// geometry. Called automatically when a tunnel prefab is placed on a road -
        /// the user never paints holes by hand.
        ///
        /// Three things happen, and all three are required for the opening to be
        /// usable rather than just visible:
        ///
        ///   1. The surface inside the box is removed from the holes map.
        ///   2. A wall skirt is generated around the cut edge, hanging down to the
        ///      tunnel floor. This is what stops you seeing into the terrain's
        ///      underside and what keeps a bored hill looking bored rather than
        ///      sliced open.
        ///   3. A floor with a collider is laid across the footprint, so the road and
        ///      anything on it has ground to rest on instead of falling through.
        ///
        /// The carve is remembered, so a later graph regeneration re-applies it and
        /// rebuilds the shell against the new surface rather than filling the tunnel
        /// back in. Returns a stable handle for ReleaseTunnelOpening.
        /// </summary>
        public int CarveTunnel(Bounds worldBounds)
        {
            return CarveTunnel(worldBounds, TunnelShellSettings.Default(worldBounds));
        }

        /// <summary>Carve with explicit shell settings (open faces, floor height).</summary>
        public int CarveTunnel(Bounds worldBounds, TunnelShellSettings settings)
        {
            Carve carve = new Carve();
            carve.id = nextCarveId++;
            carve.bounds = worldBounds;
            carve.settings = settings;
            carve.shellDirty = true;

            carves.Add(carve);

            RestampCarvesAll();
            RebuildDirtyShells();

            return carve.id;
        }

        /// <summary>Removes a carve by handle, re-fills the surface and drops its shell.</summary>
        public void ClearCarve(int handle)
        {
            int index = IndexOfCarve(handle);
            if (index < 0) return;

            DestroyShell(carves[index]);
            carves.RemoveAt(index);

            // Holes are subtractive-only: putting surface back means regenerating
            // the affected cells from the graph.
            if (Owner != null) RebuildAll();
        }

        /// <summary>Removes every carve.</summary>
        public void ClearAllCarves()
        {
            if (carves.Count == 0) return;

            for (int i = 0; i < carves.Count; i++) DestroyShell(carves[i]);
            carves.Clear();

            RebuildAll();
        }

        /// <summary>Read-only view of the active carve volumes.</summary>
        public IList<Bounds> CarveVolumes
        {
            get
            {
                List<Bounds> list = new List<Bounds>(carves.Count);
                for (int i = 0; i < carves.Count; i++) list.Add(carves[i].bounds);
                return list.AsReadOnly();
            }
        }

        // =====================================================================
        //  SLICE MAP
        //
        //  One permanent child holding one line mesh that maps the virtual chunk
        //  cuts across the whole terrain. It is created at edit time and then only
        //  ever refilled: play mode toggles and rebuilds it, never spawns or
        //  destroys it, so the hierarchy is identical before and after a play
        //  session and there is no churn on the boundary the player is crossing.
        // =====================================================================

        /// <summary>
        /// Guarantees the mapping child exists. Edit time only by contract - in play
        /// mode a missing child is reported rather than spawned, because a world that
        /// reaches play without one was built wrong and hiding that would only move
        /// the problem.
        /// </summary>
        public void EnsureSliceMap()
        {
            if (sliceMapObject == null && sliceMapFilter != null)
                sliceMapObject = sliceMapFilter.gameObject;

            if (sliceMapObject != null)
            {
                if (sliceMapFilter == null) sliceMapFilter = sliceMapObject.GetComponent<MeshFilter>();
                if (sliceMapRenderer == null) sliceMapRenderer = sliceMapObject.GetComponent<MeshRenderer>();
                if (sliceMapFilter != null && sliceMapRenderer != null) return;
            }

            if (Application.isPlaying)
            {
                Debug.LogWarning("Universe Creator: the chunk slice map child is missing. " +
                                 "It is an edit-time child and is not spawned during play.", this);
                return;
            }

            if (sliceMapObject == null)
            {
                sliceMapObject = new GameObject("SliceMap");
                sliceMapObject.transform.SetParent(transform, false);
            }

            if (sliceMapFilter == null) sliceMapFilter = sliceMapObject.GetComponent<MeshFilter>();
            if (sliceMapFilter == null) sliceMapFilter = sliceMapObject.AddComponent<MeshFilter>();

            if (sliceMapRenderer == null) sliceMapRenderer = sliceMapObject.GetComponent<MeshRenderer>();
            if (sliceMapRenderer == null) sliceMapRenderer = sliceMapObject.AddComponent<MeshRenderer>();

            // A map overlay must not light, shadow, or occlude anything.
            sliceMapRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sliceMapRenderer.receiveShadows = false;
            sliceMapRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            sliceMapRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            sliceMapRenderer.allowOcclusionWhenDynamic = false;
        }

        /// <summary>
        /// Rebuilds the map mesh from the world's current chunk layout. Cheap enough to
        /// call on any settings commit: one line mesh for the whole world.
        /// </summary>
        public void RefreshSliceMap()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return;

            EnsureSliceMap();
            if (sliceMapFilter == null || sliceMapRenderer == null) return;

            sliceMapRenderer.enabled = showSliceMap;
            if (!showSliceMap) return;

            if (sliceMapMaterial == null)
                sliceMapMaterial = ChunkSliceMap.CreateMaterial(sliceMapColour);

            // No unlit shader in this project means nothing to draw the lines with.
            // Fail quiet - the map is diagnostic, not load-bearing.
            if (sliceMapMaterial == null)
            {
                sliceMapRenderer.enabled = false;
                return;
            }

            if (sliceMapMaterial.HasProperty("_BaseColor")) sliceMapMaterial.SetColor("_BaseColor", sliceMapColour);
            if (sliceMapMaterial.HasProperty("_Color")) sliceMapMaterial.SetColor("_Color", sliceMapColour);
            sliceMapRenderer.sharedMaterial = sliceMapMaterial;

            Mesh mesh = sliceMapFilter.sharedMesh;
            if (mesh == null || mesh.name != "UC_SliceMap")
            {
                mesh = new Mesh();
                mesh.name = "UC_SliceMap";
                mesh.hideFlags = HideFlags.DontSave;
                sliceMapFilter.sharedMesh = mesh;
            }

            Vector3 worldMin = WorldMinCorner(o);

            // The holder sits on the world's min corner, so mesh vertices are local and
            // the map does not drift when the world root moves.
            sliceMapObject.transform.position = worldMin;
            sliceMapObject.transform.rotation = Quaternion.identity;
            sliceMapObject.transform.localScale = Vector3.one;

            System.Func<float, float, float> sampler = null;
            if (sliceMapFollowsSurface && built)
                sampler = SampleHeightAt;

            ChunkSliceMap.Build(mesh, worldMin, o.GetChunkSize(), o.chunkCountPerSide,
                                sliceMapSamplesPerCell, sliceMapHeightOffset, sampler);
        }

        // =====================================================================
        //  WIREFRAME PREVIEW
        // =====================================================================

        // Shows the low-poly stand-in over a chunk that is about to generate. The mesh
        // holder is created once per chunk and then reused - the same pooling rule the
        // terrains follow, for the same reason.
        private void ShowWireframe(UniverseCreatorObject o, Chunk c)
        {
            if (!o.showWireframePreview || c.go == null) return;

            if (wireframeMaterial == null)
                wireframeMaterial = ChunkWireframePreview.CreateFallbackMaterial(wireframePreviewColour);

            // No unlit shader in this project, so there is nothing to draw with. Fail
            // quiet rather than spamming: the preview is cosmetic.
            if (wireframeMaterial == null) return;

            if (c.wireframeFilter == null)
            {
                GameObject holder = new GameObject("WireframePreview");
                holder.transform.SetParent(c.go.transform, false);
                holder.hideFlags = HideFlags.DontSave;

                c.wireframeFilter = holder.AddComponent<MeshFilter>();
                c.wireframeRenderer = holder.AddComponent<MeshRenderer>();
                c.wireframeRenderer.sharedMaterial = wireframeMaterial;

                // A placeholder must not contribute to lighting or shadows.
                c.wireframeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                c.wireframeRenderer.receiveShadows = false;
                c.wireframeRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

                Mesh m = new Mesh();
                m.name = "UC_WireframePreview";
                m.hideFlags = HideFlags.DontSave;
                c.wireframeFilter.sharedMesh = m;
            }

            ChunkWireframePreview.Build(c.wireframeFilter.sharedMesh, c.appliedHeights,
                                        o.GetChunkSize(), o.maxHeightMeters);

            c.wireframeRenderer.enabled = true;

            // Hide the stale surface underneath, or the preview is invisible inside it.
            if (c.terrain != null) c.terrain.drawHeightmap = false;
        }

        private void HideWireframe(Chunk c)
        {
            if (c.wireframeRenderer != null) c.wireframeRenderer.enabled = false;
            if (c.terrain != null) c.terrain.drawHeightmap = true;
        }

        // =====================================================================
        //  MANUAL SCULPTING
        // =====================================================================

        /// <summary>
        /// Pulls any hand-sculpted changes out of the live terrains and into the world's
        /// override layer, so the next graph re-run reproduces them instead of erasing
        /// them. UniverseCreatorObject.RebuildChunks calls this for every regeneration
        /// path when captureManualEditsOnRegenerate is on; a tool that drives RebuildAll
        /// directly must call it itself. Returns the number of samples captured.
        /// </summary>
        public int CaptureManualEdits()        {
            UniverseCreatorObject o = Owner;
            if (o == null || o.overrideLayer == null) return 0;

            float metresPerSample = o.GetMetersPerPixel();
            if (metresPerSample <= 0f) return 0;

            int captured = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.data == null || c.appliedHeights == null) continue;

                // A chunk mid-generation has no settled surface to compare against.
                if (!c.applied) continue;

                int resZ = c.appliedHeights.GetLength(0);
                int resX = c.appliedHeights.GetLength(1);

                // Resolution changed under us (LOD switch or a settings edit); the old
                // baseline no longer lines up, so there is nothing meaningful to diff.
                if (c.data.heightmapResolution != resX || resX != resZ) continue;

                float[,] current = c.data.GetHeights(0, 0, resX, resZ);

                captured += o.overrideLayer.CaptureDifference(current, c.appliedHeights,
                                                              ChunkWorldBounds(c), metresPerSample);

                // The captured state is the new baseline. Without this, the same edit
                // would be captured again on the next call and applied twice.
                c.appliedHeights = current;
            }

            return captured;
        }

        /// <summary>
        /// Mirrors the world's Terrain Settings onto every cell, but only when those
        /// world values have actually changed since the last push. Between changes the
        /// cells are left alone, so Pixel Error, tree distances, detail density and the
        /// rest stay editable per cell in Unity's native Terrain inspector instead of
        /// snapping back on the next layout pass. Returns the number of cells written.
        /// </summary>
        public int SyncTerrainSettings(bool force = false)
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return 0;

            int signature = TerrainBuilder.RenderSettingsSignature(o);
            if (!force && signature == pushedRenderSignature) return 0;

            int written = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.terrain == null) continue;

                TerrainBuilder.ConfigureTerrain(c.terrain, o);
                written++;
            }

            pushedRenderSignature = signature;
            return written;
        }

        /// <summary>
        /// Re-cuts every registered water dig into the terrain that is already on
        /// screen, without re-running the graph.
        ///
        /// WHY THIS EXISTS SEPARATELY FROM A FULL REBUILD
        /// ---------------------------------------------
        /// Dragging a river's width slider must show a wider channel now, and a full
        /// regeneration of every chunk to see it would take seconds and fight the
        /// 100ms debounce. This replays the last graph output through the same fold-in
        /// order ApplyChunk uses - dig, then sculpt - so the result is identical to
        /// what a rebuild would have produced, at the cost of one heightmap write per
        /// affected chunk instead of a whole generation pass.
        ///
        /// Chunks the digs cannot reach are skipped, so editing a pond in one corner
        /// of a large world touches one terrain.
        ///
        /// Returns the number of chunks rewritten.
        /// </summary>
        public int ReapplyCarves()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return 0;

            float metresPerSample = o.GetMetersPerPixel();
            if (metresPerSample <= 0f) return 0;

            int rewritten = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.data == null || !c.applied) continue;

                Bounds bounds = ChunkWorldBounds(c);

                // Nothing here now - but a body that used to be here and has just been
                // deleted or moved away still needs its old channel filled back in, and
                // the only record of where that was is c.graphHeights. Comparing the
                // live heights against it is what catches it.
                bool affected = WaterCarveRegistry.CountAffecting(o, bounds) > 0;
                if (!affected && !ChunkDiffersFromGraph(c)) continue;

                // FIRST CUT INTO A CELL NO BODY HAD REACHED BEFORE.
                //
                // A body that has just been dragged into this cell finds no snapshot,
                // because the last bake had nothing to carve here. What is on the
                // surface right now is therefore already undug ground, so it IS the
                // pristine record and is adopted as one. Cloned rather than aliased:
                // appliedHeights must keep describing what is on the terrain until the
                // new write replaces it, and the carve below mutates its argument.
                if (c.graphHeights == null)
                {
                    if (!affected || c.appliedHeights == null) continue;

                    // Refused on a cell this session has not baked. Its surface was read
                    // back off a terrain that may already carry a channel, and adopting a
                    // trench as "undug ground" is how a river digs itself deeper on every
                    // spline nudge. The cell is left alone until a real bake gives it a
                    // snapshot; the next Build restores full re-fit behaviour.
                    if (!c.bakedThisSession) continue;

                    c.graphHeights = CloneHeights(c.appliedHeights);
                }

                // Re-derived from unaltered ground every time, which is what stops each
                // pass from cutting the channel deeper into the channel it cut last
                // time. The snapshot is never written to - only ever copied from.
                float[,] heights = CloneHeights(c.graphHeights);

                WaterCarveRegistry.ApplyAll(o, heights, bounds, metresPerSample);

                if (o.overrideLayer != null && !o.overrideLayer.IsEmpty)
                    o.overrideLayer.ApplyTo(heights, bounds, metresPerSample);

                // Same gate ApplyOutputs applies, because this is the second door into
                // SetHeights and it bypasses ApplyOutputs entirely. Both writers above
                // are ordinary arithmetic on a depth and a distance, so both can produce
                // a NaN from a degenerate spline or a zero-width body - and a NaN here
                // reaches the surface exactly as readily as one from the node graph.
                // A safeguard that only covers the path you remembered is not one.
                HeightGuard.Guard(heights, metresPerSample, o.maxHeightMeters,
                                  "the chunk at cell " + c.cell.x + "," + c.cell.z
                                  + " during a water re-cut", o);

                c.data.SetHeightsDelayLOD(0, 0, heights);
                c.data.SyncHeightmap();

                // The new baseline, so a later CaptureManualEdits does not read the
                // dig itself as something the user sculpted by hand.
                c.appliedHeights = heights;

                // The last body left this cell and its channel has just been filled in,
                // so there is no longer anything to restore and the snapshot is dead
                // weight. Releasing it also settles the loop: with it gone, both tests
                // above are false next time and the cell stops being rewritten on every
                // drain. If a body is dragged back in later, the adopt above rebuilds it.
                if (!affected) c.graphHeights = null;

                rewritten++;
            }

            if (rewritten > 0) StitchGrid();

            return rewritten;
        }

        /// <summary>True when the live terrain no longer matches the undug ground it was
        /// built from, which means a dig is still cut into it.</summary>
        /// <remarks>
        /// Reads the snapshot rather than c.tile. The pump drops the tile immediately
        /// after applying it, so by the time any re-fit runs this test always saw a null
        /// tile and always returned false - which is what made the "fill an abandoned
        /// channel back in" case silently impossible.
        /// </remarks>
        private bool ChunkDiffersFromGraph(Chunk c)
        {
            if (c.appliedHeights == null || c.graphHeights == null) return false;

            float[,] generated = c.graphHeights;

            int resZ = c.appliedHeights.GetLength(0);
            int resX = c.appliedHeights.GetLength(1);

            if (generated.GetLength(0) != resZ || generated.GetLength(1) != resX) return false;

            // Sparse walk: a carve covers many contiguous samples, so checking every
            // fourth row and column finds one without reading a million floats.
            for (int z = 0; z < resZ; z += 4)
            {
                for (int x = 0; x < resX; x += 4)
                {
                    if (Mathf.Abs(c.appliedHeights[z, x] - generated[z, x]) > 1e-5f) return true;
                }
            }

            return false;
        }

        private static float[,] CloneHeights(float[,] source)
        {
            int resZ = source.GetLength(0);
            int resX = source.GetLength(1);

            float[,] copy = new float[resZ, resX];
            System.Array.Copy(source, copy, resZ * resX);

            return copy;
        }

        /// <summary>
        /// Discards every manual edit and rebuilds, returning the world to pure graph
        /// output.
        /// </summary>
        public void ClearManualEdits()
        {
            UniverseCreatorObject o = Owner;
            if (o == null || o.overrideLayer == null) return;
            if (o.overrideLayer.IsEmpty) return;

            o.overrideLayer.ClearAll();

            // Drop the stale baselines too, or the next capture would read the cleared
            // edits back in as fresh differences. The undug-ground snapshot goes with
            // them: it is only ever meaningful alongside the baseline it was taken with,
            // and RebuildAll below retakes both.
            for (int i = 0; i < pool.Count; i++)
            {
                pool[i].appliedHeights = null;
                pool[i].graphHeights = null;
            }

            RebuildAll();
        }

        private int IndexOfCarve(int handle)
        {
            for (int i = 0; i < carves.Count; i++)
                if (carves[i].id == handle) return i;
            return -1;
        }

        // Re-applies every carve to every live chunk. Cheap enough to run on
        // placement because it only touches chunks the volumes actually overlap.
        private void RestampCarvesAll()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.go == null || !c.go.activeSelf || c.data == null) continue;
                StampCarves(c);
            }
        }

        // =====================================================================
        //  TUNNEL SHELLS  (walls + floor that close the cut)
        // =====================================================================

        private void MarkShellsDirtyOver(Bounds region)
        {
            for (int i = 0; i < carves.Count; i++)
                if (carves[i].bounds.Intersects(region)) carves[i].shellDirty = true;
        }

        private void RebuildDirtyShells()
        {
            for (int i = 0; i < carves.Count; i++)
            {
                Carve carve = carves[i];
                if (!carve.shellDirty) continue;
                BuildShell(carve);
                carve.shellDirty = false;
            }
        }

        private Transform ShellRoot()
        {
            if (shellRoot != null) return shellRoot;

            Transform existing = transform.Find("TunnelShells");
            if (existing != null) { shellRoot = existing; return shellRoot; }

            GameObject go = new GameObject("TunnelShells");
            go.transform.SetParent(transform, false);
            shellRoot = go.transform;
            return shellRoot;
        }

        private void BuildShell(Carve carve)
        {
            // Shells are recycled exactly like chunks: the mesh is cleared and
            // refilled in place rather than allocating a new one per regeneration.
            if (carve.shell == null)
            {
                carve.shell = new GameObject("TunnelShell_" + carve.id);
                carve.shell.transform.SetParent(ShellRoot(), false);

                carve.mesh = new Mesh();
                carve.mesh.name = "TunnelShell_" + carve.id;

                // Generated at runtime and never saved; without this it leaks into
                // the scene as an orphaned asset on play-mode exit.
                carve.mesh.hideFlags = HideFlags.DontSave;

                carve.shell.AddComponent<MeshFilter>().sharedMesh = carve.mesh;
                carve.shell.AddComponent<MeshRenderer>();
                carve.collider = carve.shell.AddComponent<MeshCollider>();
            }

            // Sampling the terrain here is safe: shells are only ever built from
            // ApplyChunk or a carve request, both of which are on the main thread.
            TunnelShellBuilder.Build(carve.mesh, carve.bounds, carve.settings,
                                     SampleSurfaceForShell);

            // A MeshCollider caches the mesh, so it has to be re-assigned after the
            // vertices change or the collider keeps the old shape.
            if (carve.collider != null)
            {
                carve.collider.sharedMesh = null;
                carve.collider.sharedMesh = carve.mesh;
            }

            MeshRenderer renderer = carve.shell.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Material mat = tunnelShellMaterial;
                if (mat == null && Owner != null) mat = Owner.terrainMaterial;
                if (mat != null) renderer.sharedMaterial = mat;
            }
        }

        // Terrain height for the shell's top edge. Falls back to the carve ceiling
        // where no chunk is loaded, so a wall is still produced rather than a
        // zero-height sliver.
        private float SampleSurfaceForShell(float worldX, float worldZ)
        {
            Chunk c = ChunkAtWorld(worldX, worldZ);
            if (c == null || c.data == null) return 0f;
            return SampleHeightAt(worldX, worldZ);
        }

        private void DestroyShell(Carve carve)
        {
            if (carve.shell != null)
            {
                if (Application.isPlaying) Destroy(carve.shell);
                else DestroyImmediate(carve.shell);
                carve.shell = null;
            }

            if (carve.mesh != null)
            {
                if (Application.isPlaying) Destroy(carve.mesh);
                else DestroyImmediate(carve.mesh);
                carve.mesh = null;
            }

            carve.collider = null;
        }

        // Writes the holes map for one chunk from the carve volumes overlapping it.
        // Unity stores true = surface, false = hole, and the map is per-quad, so it
        // is one smaller than the heightmap on each axis.
        private void StampCarves(Chunk c)
        {
            if (c.data == null) return;

            int res = c.data.holesResolution;
            if (res < 1) return;

            Bounds chunkBounds = ChunkWorldBounds(c);

            // Collect only the volumes that touch this chunk.
            List<Bounds> hits = null;
            for (int i = 0; i < carves.Count; i++)
            {
                if (!carves[i].bounds.Intersects(chunkBounds)) continue;
                if (hits == null) hits = new List<Bounds>();
                hits.Add(carves[i].bounds);
            }

            // Nothing overlaps: make sure the surface is whole again.
            if (hits == null)
            {
                if (!c.hasCarves) return;
                bool[,] solid = new bool[res, res];
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        solid[z, x] = true;

                c.data.SetHoles(0, 0, solid);
                c.hasCarves = false;
                return;
            }

            Vector3 min = chunkBounds.min;
            float sizeX = chunkBounds.size.x;
            float sizeZ = chunkBounds.size.z;

            bool[,] map = new bool[res, res];

            for (int z = 0; z < res; z++)
            {
                // Sample at quad centres: (i + 0.5) / res across the chunk span.
                float wz = min.z + (z + 0.5f) / res * sizeZ;

                for (int x = 0; x < res; x++)
                {
                    float wx = min.x + (x + 0.5f) / res * sizeX;

                    bool cut = false;
                    for (int i = 0; i < hits.Count; i++)
                    {
                        Bounds b = hits[i];
                        if (wx >= b.min.x && wx <= b.max.x && wz >= b.min.z && wz <= b.max.z)
                        {
                            cut = true;
                            break;
                        }
                    }

                    map[z, x] = !cut;   // true = surface
                }
            }

            c.data.SetHoles(0, 0, map);
            c.hasCarves = true;
        }

        // =====================================================================
        //  WORLD QUERIES  (the IPCGDataProvider surface)
        // =====================================================================

        /// <summary>
        /// World-space terrain height at (x, z), or 0 if the point is outside the
        /// world or its chunk has not generated yet.
        /// </summary>
        public float SampleHeightAt(float worldX, float worldZ)
        {
            Chunk c = ChunkAtWorld(worldX, worldZ);
            if (c == null || c.terrain == null || c.data == null) return 0f;

            return c.terrain.SampleHeight(new Vector3(worldX, 0f, worldZ)) +
                   c.go.transform.position.y;
        }

        /// <summary>World-space surface normal at (x, z), or up if unavailable.</summary>
        public Vector3 SampleNormalAt(float worldX, float worldZ)
        {
            Chunk c = ChunkAtWorld(worldX, worldZ);
            if (c == null || c.data == null) return Vector3.up;

            Bounds b = ChunkWorldBounds(c);
            float u = Mathf.Clamp01((worldX - b.min.x) / Mathf.Max(0.0001f, b.size.x));
            float v = Mathf.Clamp01((worldZ - b.min.z) / Mathf.Max(0.0001f, b.size.z));

            return c.data.GetInterpolatedNormal(u, v);
        }

        /// <summary>World bounds of every live chunk.</summary>
        public List<Bounds> GetLoadedChunkBounds()
        {
            List<Bounds> result = new List<Bounds>();
            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c.go == null || !c.go.activeSelf || c.data == null) continue;
                result.Add(ChunkWorldBounds(c));
            }
            return result;
        }

        // Which chunk covers a world XZ point, or null if outside the world.
        private Chunk ChunkAtWorld(float worldX, float worldZ)
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return null;

            Vector3 worldMin = WorldMinCorner(o);
            float cellSize = Mathf.Max(0.0001f, o.GetChunkSize());

            int cx = Mathf.FloorToInt((worldX - worldMin.x) / cellSize);
            int cz = Mathf.FloorToInt((worldZ - worldMin.z) / cellSize);

            if (cx < 0 || cz < 0 || cx >= o.chunkCountPerSide || cz >= o.chunkCountPerSide)
                return null;

            Chunk c;
            byCell.TryGetValue(new Coord(cx, cz), out c);
            return c;
        }

        private Bounds ChunkWorldBounds(Chunk c)
        {
            UniverseCreatorObject o = Owner;
            float cellSize = o != null ? o.GetChunkSize() : 0f;
            float height = o != null ? o.maxHeightMeters : 0f;

            Vector3 pos = c.go != null ? c.go.transform.position : Vector3.zero;
            Vector3 size = new Vector3(cellSize, height, cellSize);
            Vector3 centre = pos + new Vector3(cellSize * 0.5f, height * 0.5f, cellSize * 0.5f);

            return new Bounds(centre, size);
        }

        /// <summary>
        /// Ground elevation at a world XZ as the GRAPH made it, before any water dig
        /// was cut into it. False when this world holds no undug record of that spot.
        /// </summary>
        /// <remarks>
        /// A river works out its own course by asking how high the ground is under it.
        /// Ask the live terrain and the answer already contains the channel this river
        /// cut on the previous pass, so the water surface it derives comes back one
        /// channel-depth lower, and the bed one depth below that. Repeat per settling
        /// chunk and the river walks itself down into the world.
        ///
        /// Ask here and the answer is the same on every pass until the cell is
        /// genuinely re-baked - which is the only moment a re-fit is actually owed.
        ///
        /// Returns false rather than guessing. A caller that cannot get a pristine
        /// answer must decline to move, not fall back to the dug surface.
        /// </remarks>
        public bool TrySampleGraphHeight(float worldX, float worldZ, out float worldY)
        {
            worldY = 0f;

            UniverseCreatorObject o = Owner;
            if (o == null) return false;

            float cellSize = o.GetChunkSize();
            if (cellSize <= 0f) return false;

            for (int i = 0; i < pool.Count; i++)
            {
                Chunk c = pool[i];
                if (c == null || c.graphHeights == null || c.go == null) continue;

                Vector3 origin = c.go.transform.position;

                float u = (worldX - origin.x) / cellSize;
                float v = (worldZ - origin.z) / cellSize;
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue;

                int resZ = c.graphHeights.GetLength(0);
                int resX = c.graphHeights.GetLength(1);
                if (resZ < 2 || resX < 2) continue;

                float fx = u * (resX - 1);
                float fz = v * (resZ - 1);

                int x0 = (int)fx; if (x0 > resX - 2) x0 = resX - 2;
                int z0 = (int)fz; if (z0 > resZ - 2) z0 = resZ - 2;

                float tx = fx - x0;
                float tz = fz - z0;

                // Bilinear, to match what GetInterpolatedHeight does to the live
                // heightfield - so a pristine answer and a dug answer are measured the
                // same way and can be compared metre for metre.
                float h = Mathf.Lerp(
                    Mathf.Lerp(c.graphHeights[z0, x0], c.graphHeights[z0, x0 + 1], tx),
                    Mathf.Lerp(c.graphHeights[z0 + 1, x0], c.graphHeights[z0 + 1, x0 + 1], tx),
                    tz);

                float span = c.data != null ? c.data.size.y : o.maxHeightMeters;

                worldY = origin.y + h * span;
                return true;
            }

            return false;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        // The "Terrain" child of the world root - where the native Terrain cells live.
        // Created on demand so a hand-built world (or one from an older scene that
        // never had the child) still works instead of throwing.
        // Instanced-draw sink for dense object scatter. Created on the world root the
        // first time something asks for it, so a world with no instanced prototypes
        // never carries the component or its LateUpdate.
        private Optimization.GPUInstancingManager instancing;

        private Optimization.GPUInstancingManager Instancing()
        {
            if (instancing != null) return instancing;

            UniverseCreatorObject o = Owner;
            GameObject host = o != null ? o.gameObject : gameObject;

            instancing = host.GetComponent<Optimization.GPUInstancingManager>();
            if (instancing == null)
                instancing = host.AddComponent<Optimization.GPUInstancingManager>();

            return instancing;
        }

        private Transform TerrainRoot()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return transform;

            Transform existing = o.transform.Find("Terrain");
            if (existing != null) return existing;

            GameObject host = new GameObject("Terrain");
            host.transform.SetParent(o.transform, false);
            return host.transform;
        }

        /// <summary>
        /// The single place a cell's heightmap pixel error is decided (L-49/L-50). Replaces
        /// the old authored x1/2/4 LOD ladder: the tolerated screen error is derived from the
        /// live view distance, camera field of view and render-target height, floored at the
        /// source field's Nyquist limit and the world's hard pixel-error floor, then written
        /// only when it crosses the per-cell hysteresis band. Whole-world render (the
        /// edit-mode preview) resolves at zero distance, which collapses the budget to its
        /// finest uniform value so every cell shows full fidelity.
        /// </summary>
        private void ApplyDensity(UniverseCreatorObject o, Chunk c)
        {
            if (o == null || c == null || c.terrain == null || c.data == null) return;

            ResolveView(out Vector3 viewPos, out float fovDeg, out int screenHeight);

            // Zero distance in whole-world preview => Max(acuity, floor), the finest uniform
            // budget; otherwise the straight-line range from the view to the cell centre.
            float distance = o.ShouldRenderWholeWorld()
                ? 0f
                : Vector3.Distance(viewPos, CellCentreWorld(o, c));

            float texel = Geometry.AngularError.TexelMetres(o.GetChunkSize(), c.data.heightmapResolution);

            if (_density.TryResolve(c.cell.x, c.cell.z, o.acuityPixels, texel,
                                    distance, fovDeg, screenHeight, o.pixelErrorFloor,
                                    o.densityHysteresis, out float pixelError))
            {
                c.terrain.heightmapPixelError = pixelError;
            }
        }

        /// <summary>
        /// Resolves the live view the density budget is computed against: the focus (or main
        /// camera) position, the main camera's vertical field of view, and the render-target
        /// pixel height. Falls back to sane defaults (60 deg, 1080 px, origin) so a scene with
        /// no camera resolves the finest budget rather than a NaN.
        /// </summary>
        private void ResolveView(out Vector3 position, out float fovDeg, out int screenHeightPixels)
        {
            Transform view = ResolveFocus();
            position = view != null ? view.position : Vector3.zero;

            Camera cam = Camera.main;
            fovDeg = cam != null ? cam.fieldOfView : 60f;
            screenHeightPixels = Screen.height > 0 ? Screen.height : 1080;
        }

        /// <summary>World-space centre of a cell, for the view-to-cell distance.</summary>
        private Vector3 CellCentreWorld(UniverseCreatorObject o, Chunk c)
        {
            float cellSize = o.GetChunkSize();
            Vector3 worldMin = WorldMinCorner(o);
            return worldMin + new Vector3((c.cell.x + 0.5f) * cellSize, 0f, (c.cell.z + 0.5f) * cellSize);
        }

        // World-space min corner (the (0,0) cell's corner) of the whole continent.
        private Vector3 WorldMinCorner(UniverseCreatorObject o)
        {
            float span = o.worldSizeKm * 1000f;
            Vector3 p = o.transform.position;
            return new Vector3(
                p.x - span * 0.5f + o.worldOriginOffset.x,
                p.y,
                p.z - span * 0.5f + o.worldOriginOffset.y);
        }

        // Which cell the focus point currently sits over, clamped to the world.
        private Coord FocusCell(UniverseCreatorObject o)
        {
            Transform f = ResolveFocus();
            if (f == null) return new Coord((o.chunkCountPerSide - 1) / 2, (o.chunkCountPerSide - 1) / 2);

            Vector3 worldMin = WorldMinCorner(o);
            float cellSize = Mathf.Max(0.0001f, o.GetChunkSize());

            int cx = Mathf.FloorToInt((f.position.x - worldMin.x) / cellSize);
            int cz = Mathf.FloorToInt((f.position.z - worldMin.z) / cellSize);

            cx = Mathf.Clamp(cx, 0, o.chunkCountPerSide - 1);
            cz = Mathf.Clamp(cz, 0, o.chunkCountPerSide - 1);
            return new Coord(cx, cz);
        }

        private Transform ResolveFocus()
        {
            if (focus != null) return focus;
            if (Camera.main != null) return Camera.main.transform;
            return null;
        }

        /// <summary>Stops every in-flight generation. Safe to call when idle.</summary>
        public void CancelAll()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].stop != null) pool[i].stop.RequestStop();

                // A cancelled chunk never reaches ApplyChunk, so drop the stand-in here
                // or the terrain stays hidden behind a preview that will never resolve.
                HideWireframe(pool[i]);
            }
            pending.Clear();
        }

        /// <summary>
        /// Requests every generation stop and then blocks until the worker threads have
        /// actually left, or until the timeout expires.
        ///
        /// This is the one place a deliberate main-thread block is correct. A domain
        /// reload tears down the managed heap these workers are reading from, and Unity
        /// will not begin one while it cannot prove the reload is safe - it just posts
        /// the "Hold on… Application.Tick" modal and waits, which is the hang the user
        /// sees after a script edit mid-bake. Requesting a stop is not enough on its own,
        /// because RequestStop only sets a flag the evaluator checks between nodes: the
        /// thread is still live for however long the current node takes.
        ///
        /// The timeout is the safety valve. A node that ignores the stop flag entirely
        /// would otherwise trade a reload hang for a permanent one, so after the budget
        /// expires we give up waiting and let the reload proceed - the worker's results
        /// are discarded either way, since StopRequested gates the assignment to c.tile.
        /// </summary>
        public void CancelAndDrain(int timeoutMs = 2000)
        {
            CancelAll();

            List<Task> live = new List<Task>();
            for (int i = 0; i < pool.Count; i++)
            {
                Task t = pool[i].task;
                if (t != null && !t.IsCompleted) live.Add(t);
            }

            if (live.Count > 0)
            {
                try
                {
                    Task.WaitAll(live.ToArray(), timeoutMs);
                }
                catch (System.AggregateException)
                {
                    // A worker that threw already logged it. We are only here to be sure
                    // it is no longer running.
                }
            }

            // Nothing may land after this point: the pool references are about to be
            // wiped by the reload, and a half-applied cell would be adopted next load
            // with generated heights it never actually received.
            for (int i = 0; i < pool.Count; i++)
            {
                pool[i].workerDone = false;
                pool[i].tile = null;
            }

            activeWorkers = 0;
        }
    }
}
