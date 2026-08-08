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

            public MeshFilter wireframeFilter;
            public MeshRenderer wireframeRenderer;
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

        public bool IsGenerating { get { return activeWorkers > 0 || pending.Count > 0; } }
        
        public float GetProgress()
        {
            if (pool.Count == 0) return 1f;
            int done = pool.Count - (activeWorkers + pending.Count);
            return (float)done / pool.Count;
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
        }

        private void OnDisable()
        {
            CancelAll();
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

        /// <summary>
        /// Rebuilds only the chunk layout - pool placement and the slice map - without
        /// generating anything. This is what a chunk-dimension change in the inspector
        /// needs when the world has no graph to run.
        /// </summary>
        public async void RebuildLayout()
        {
            UniverseCreatorObject o = Owner;
            if (o == null) return;

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
                if (c.data != null && c.data.heightmapResolution != res)
                {
                    c.data = null;
                    c.appliedHeights = null;
                    c.applied = false;
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
                    // This is to explicitly make all chunks render at High Fidelity in Edit Mode.
                    c.terrain.heightmapPixelError = DualLODSystem.PixelErrorFor(LODLevel.High, o.pixelError);
                }

                if (i > 0 && i % 4 == 0) await Task.Yield();
            }
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
            // PASS 1 — BIDIRECTIONAL AVERAGED EDGE WELD
            // For every pair of adjacent chunks we read both shared edges,
            // average them, and write the averaged row back into BOTH chunks.
            // This is the production-quality approach: neither tile "wins" —
            // they converge to the mathematical midpoint so the seam value is
            // identical on both sides.
            // ----------------------------------------------------------------

            var sortedCoords = new System.Collections.Generic.List<Coord>(byCell.Keys);
            sortedCoords.Sort((a, b) => {
                int zc = a.z.CompareTo(b.z);
                return zc != 0 ? zc : a.x.CompareTo(b.x);
            });

            foreach (Coord c in sortedCoords)
            {
                Chunk chunkA = byCell[c];
                if (chunkA == null || !chunkA.applied || chunkA.terrain == null || chunkA.terrain.terrainData == null) continue;

                int res = chunkA.terrain.terrainData.heightmapResolution;

                // --- RIGHT neighbour (c.x+1) ---
                Chunk chunkR;
                if (byCell.TryGetValue(new Coord(c.x + 1, c.z), out chunkR)
                    && chunkR.applied
                    && chunkR.terrain != null && chunkR.terrain.terrainData != null
                    && chunkR.terrain.terrainData.heightmapResolution == res)
                {
                    float[,] hA_edge = chunkA.terrain.terrainData.GetHeights(res - 1, 0, 1, res);
                    float[,] hR_edge = chunkR.terrain.terrainData.GetHeights(0, 0, 1, res);
                    for (int y = 0; y < res; y++)
                    {
                        float avg = Mathf.Clamp01((hA_edge[y, 0] + hR_edge[y, 0]) * 0.5f);
                        hA_edge[y, 0] = avg;
                        hR_edge[y, 0] = avg;
                        if (chunkA.appliedHeights != null) chunkA.appliedHeights[y, res - 1] = avg;
                        if (chunkR.appliedHeights != null) chunkR.appliedHeights[y, 0] = avg;
                    }
                    chunkA.terrain.terrainData.SetHeightsDelayLOD(res - 1, 0, hA_edge);
                    chunkR.terrain.terrainData.SetHeightsDelayLOD(0, 0, hR_edge);
                }

                // --- TOP neighbour (c.z+1) ---
                Chunk chunkT;
                if (byCell.TryGetValue(new Coord(c.x, c.z + 1), out chunkT)
                    && chunkT.applied
                    && chunkT.terrain != null && chunkT.terrain.terrainData != null
                    && chunkT.terrain.terrainData.heightmapResolution == res)
                {
                    float[,] hA_edge = chunkA.terrain.terrainData.GetHeights(0, res - 1, res, 1);
                    float[,] hT_edge = chunkT.terrain.terrainData.GetHeights(0, 0, res, 1);
                    for (int x = 0; x < res; x++)
                    {
                        float avg = Mathf.Clamp01((hA_edge[0, x] + hT_edge[0, x]) * 0.5f);
                        hA_edge[0, x] = avg;
                        hT_edge[0, x] = avg;
                        if (chunkA.appliedHeights != null) chunkA.appliedHeights[res - 1, x] = avg;
                        if (chunkT.appliedHeights != null) chunkT.appliedHeights[0, x] = avg;
                    }
                    chunkA.terrain.terrainData.SetHeightsDelayLOD(0, res - 1, hA_edge);
                    chunkT.terrain.terrainData.SetHeightsDelayLOD(0, 0, hT_edge);
                }
            }

            // ----------------------------------------------------------------
            // PASS 2 — 4-WAY CORNER SNAP
            // After the 4 edge strips are averaged, the corner pixels (where 4
            // chunks meet) may still disagree because each edge pass only
            // touched one edge at a time. We must average all 4 corners of
            // each 2×2 block and write the result into all 4 tiles.
            // ----------------------------------------------------------------

            foreach (Coord c in sortedCoords)
            {
                Chunk cA, cB, cC, cD;
                // cA = (c.x,   c.z),   cB = (c.x+1, c.z)
                // cC = (c.x,   c.z+1), cD = (c.x+1, c.z+1)
                if (!byCell.TryGetValue(c,                          out cA) || cA == null || !cA.applied || cA.terrain == null || cA.terrain.terrainData == null) continue;
                if (!byCell.TryGetValue(new Coord(c.x+1, c.z),     out cB) || cB == null || !cB.applied || cB.terrain == null || cB.terrain.terrainData == null) continue;
                if (!byCell.TryGetValue(new Coord(c.x,   c.z+1),   out cC) || cC == null || !cC.applied || cC.terrain == null || cC.terrain.terrainData == null) continue;
                if (!byCell.TryGetValue(new Coord(c.x+1, c.z+1),   out cD) || cD == null || !cD.applied || cD.terrain == null || cD.terrain.terrainData == null) continue;

                int res = cA.terrain.terrainData.heightmapResolution;

                // cA is bottom-left chunk -> corner is Top-Right
                float[,] hA = cA.terrain.terrainData.GetHeights(res - 1, res - 1, 1, 1);
                // cB is bottom-right chunk -> corner is Top-Left
                float[,] hB = cB.terrain.terrainData.GetHeights(0,       res - 1, 1, 1);
                // cC is top-left chunk -> corner is Bottom-Right
                float[,] hC = cC.terrain.terrainData.GetHeights(res - 1, 0,       1, 1);
                // cD is top-right chunk -> corner is Bottom-Left
                float[,] hD = cD.terrain.terrainData.GetHeights(0,       0,       1, 1);

                float corner = Mathf.Clamp01((hA[0,0] + hB[0,0] + hC[0,0] + hD[0,0]) * 0.25f);

                hA[0,0] = corner; cA.terrain.terrainData.SetHeightsDelayLOD(res - 1, res - 1, hA);
                hB[0,0] = corner; cB.terrain.terrainData.SetHeightsDelayLOD(0,       res - 1, hB);
                hC[0,0] = corner; cC.terrain.terrainData.SetHeightsDelayLOD(res - 1, 0,       hC);
                hD[0,0] = corner; cD.terrain.terrainData.SetHeightsDelayLOD(0,       0,       hD);

                if (cA.appliedHeights != null) cA.appliedHeights[res - 1, res - 1] = corner;
                if (cB.appliedHeights != null) cB.appliedHeights[res - 1, 0] = corner;
                if (cC.appliedHeights != null) cC.appliedHeights[0, res - 1] = corner;
                if (cD.appliedHeights != null) cD.appliedHeights[0, 0] = corner;
            }

            // ----------------------------------------------------------------
            // PASS 3 — Unity SetNeighbors + SyncHeightmap + Flush
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

                Chunk lc, rc, tc, bc;
                Terrain left   = null, right  = null, top    = null, bottom = null;

                if (byCell.TryGetValue(new Coord(c.x - 1, c.z), out lc) && lc != null && lc.applied && lc.terrain != null && lc.terrain.terrainData != null && lc.terrain.terrainData.heightmapResolution == res) left   = lc.terrain;
                if (byCell.TryGetValue(new Coord(c.x + 1, c.z), out rc) && rc != null && rc.applied && rc.terrain != null && rc.terrain.terrainData != null && rc.terrain.terrainData.heightmapResolution == res) right  = rc.terrain;
                if (byCell.TryGetValue(new Coord(c.x, c.z + 1), out tc) && tc != null && tc.applied && tc.terrain != null && tc.terrain.terrainData != null && tc.terrain.terrainData.heightmapResolution == res) top    = tc.terrain;
                if (byCell.TryGetValue(new Coord(c.x, c.z - 1), out bc) && bc != null && bc.applied && bc.terrain != null && bc.terrain.terrainData != null && bc.terrain.terrainData.heightmapResolution == res) bottom = bc.terrain;

                chunk.terrain.SetNeighbors(left, top, right, bottom);
                chunk.terrain.terrainData.SyncHeightmap();
                chunk.terrain.Flush();
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
                if (pool[i].go != null) pool[i].go.SetActive(false);

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
                    c.go.SetActive(true);
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
                    c.go.SetActive(shouldBeLive);

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
                        c.terrain.heightmapPixelError = DualLODSystem.PixelErrorFor(next, o.pixelError);

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
                    if (c.go.activeSelf) c.go.SetActive(false);
                    continue;
                }

                bool live = wholeWorld;
                if (!live)
                {
                    int ring = DualLODSystem.RingDistance(c.cell.x, c.cell.z,
                                                          centreCell.x, centreCell.z);
                    live = ring <= keepRadius;
                }

                if (c.go.activeSelf != live) c.go.SetActive(live);
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
                if (!willWrite) continue;

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
            if (freed && activeWorkers == 0 && pending.Count == 0)
                RefreshSliceMap();

            // Reported so the edit-mode driver can repaint the Scene view. Writing
            // TerrainData does not itself mark the view dirty, so without a repaint a
            // finished chunk can sit invisible until something else happens to redraw
            // it - which reads as the graph edit having done nothing - C-14.
            return wrote;
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

            // Manual sculpt edits are folded in here, so they ride on top of whatever
            // the graph just produced instead of being erased by it.
            TerrainBuilder.ApplyOutputs(c.data, c.tile.outputs, o,
                                        o.overrideLayer, ChunkWorldBounds(c), o.GetMetersPerPixel());

            // Phase 4: SyncHeightmap and Flush are deferred to StitchGrid to happen strictly after SetNeighbors

#if UNITY_EDITOR
            if (c.terrain != null) UnityEditor.EditorUtility.SetDirty(c.terrain);
            if (c.data != null) UnityEditor.EditorUtility.SetDirty(c.data);
#endif

            // Only the visibility invariants. Pushing the full settings block here
            // would revert a hand edit every time the cell regenerated, which for a
            // streaming LOD cell is constantly.
            TerrainBuilder.EnsureRenderable(c.terrain, o);

            // Fidelity for this cell's ring. This is the whole LOD mechanism now:
            // every cell keeps the same heightmapResolution (SetNeighbors rejects any
            // pair that does not) and distance is expressed as tolerated screen-space
            // error instead. Changing it is free - no re-bake, no reallocation, and no
            // seam to repair - so a cell can change ring every frame without cost.
            c.terrain.heightmapPixelError = DualLODSystem.PixelErrorFor(c.lod, o.pixelError);

            // Remember what we just wrote, so a later sculpt can be diffed against it.
            c.appliedHeights = c.tile.outputs.heightmap;

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
        //  TUNNEL CARVING  (called by RoadMasterLinker, Module 7)
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
            // edits back in as fresh differences.
            for (int i = 0; i < pool.Count; i++) pool[i].appliedHeights = null;

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

        // =====================================================================
        //  HELPERS
        // =====================================================================

        // The "Terrain" child of the world root - where the native Terrain cells live.
        // Created on demand so a hand-built world (or one from an older scene that
        // never had the child) still works instead of throwing.
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
