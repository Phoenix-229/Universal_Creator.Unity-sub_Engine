// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    TileData.cs
//  Purpose: Per-chunk evaluation context. Owns the product cache (so nodes only
//           compute once per chunk) and the main-thread output bag (so worker
//           threads never touch UnityEngine API). Passed to every Generate().
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Evaluation context for one chunk. The GraphEvaluator creates one TileData
    /// per chunk, runs the graph, then applies the outputs bag to TerrainData on
    /// the main thread.
    /// </summary>
    public partial class TileData
    {
        /// <summary>Chunk coordinate in the virtual grid.</summary>
        public Coord chunkCoord;

        /// <summary>
        /// CoordRect this chunk covers in global lattice space. The chunking system
        /// sets this from chunkCoord and chunk resolution.
        /// </summary>
        public CoordRect rect;

        /// <summary>
        /// World-space position and size of this chunk. Output nodes use these to
        /// configure Matrix.worldPos/worldSize so the erosion and scatter systems
        /// can convert between pixels and metres.
        /// </summary>
        public Vector3 worldPos;
        public Vector3 worldSize;

        /// <summary>
        /// World-space min corner and full extent of the WHOLE world (continent),
        /// not just this chunk. World-aware nodes - the edge falloff that flattens
        /// the outer band to sea level - need the continent bounds to know how far a
        /// pixel is from the world edge. Defaults to this chunk's own bounds so a
        /// single whole-world tile behaves correctly without the chunking system.
        /// </summary>
        public Vector3 fullWorldPos;
        public Vector3 fullWorldSize;

        /// <summary>
        /// World height scale: a Matrix value of 1.0 maps to this many metres.
        /// Typically equals TerrainData.size.y.
        /// </summary>
        public float worldHeight;

        /// <summary>
        /// Product cache: keyed by (nodeId, portIndex) so if two nodes pull the same
        /// output it's only computed once. Cleared at the start of each chunk eval.
        /// </summary>
        private Dictionary<string, object> cache = new Dictionary<string, object>();

        /// <summary>
        /// T1 cross-bake retained cache (OPT-IN): survives between bakes, owned by the
        /// Chunk, keyed "nodeId:port" → (content hash, product). The evaluator forwards a
        /// retained product only when the node's current <see cref="Generator.contentHash"/>
        /// matches the stored one, so an edit elsewhere recomputes only the touched
        /// subtree while unchanged nodes are reused. Null = today's behaviour (every bake
        /// recomputes), so callers can still opt out of cross-bake reuse. UC-T02-06:
        /// production entries hold byte-budget tokens, whose payload becomes null after
        /// eligible eviction. In-use tile pins protect a reused result through its final consumer.
        /// </summary>
        public Dictionary<string, RetainedProduct> retained;

        /// <summary>
        /// Main-thread output bag. Output nodes record what they want to write
        /// (heightmap, splat, holes, collision) here, and the chunking system applies
        /// them after the background eval finishes. This keeps TerrainData.SetHeights
        /// off the worker thread.
        /// </summary>
        public OutputBag outputs = new OutputBag();

        /// <summary>UC-T02-01: world/revision owning this tile; default for standalone node tests and unowned previews.</summary>
        public TerrainSystem.GenerationRunId RunId { get; private set; }

        // UC-T02-01: keep the field context and the independently consumed output bag on the same publication identity.
        internal void AssignRun(TerrainSystem.GenerationRunId run)
        {
            RunId = run;
            outputs.RunId = run;
        }

        /// <summary>
        /// Ecology snapshot for this chunk: sample settings, a bound environment
        /// sampler, and the species tables scatter nodes choose from. Built on the
        /// main thread before evaluation because a worker must never read the
        /// vegetation manager, and read-only from then on.
        ///
        /// Null is a valid and common state - it means this graph was evaluated
        /// without ecology, and every node must then behave exactly as it did
        /// before Module 3 rather than placing nothing.
        /// </summary>
        public Ecology.EcoScatterContext ecology;
        // UC-T05-03: immutable accepted terrain input; the parent tile pins its snapshot through final companion consumption.
        internal WorldGraphGround worldGraphGround;

        /// <summary>
        /// Per-bake memo of whole-world drainage solves, shared by every chunk of one
        /// bake so a chunk-independent solve is computed once, not once per chunk (Step
        /// 42). Set by the chunking system in Dispatch. Null on the single-tile path and
        /// in tests, where <see cref="WorldDrainage.Solve"/> falls back to no caching -
        /// exactly the original behaviour.
        /// </summary>
        public WorldDrainageCache drainageCache;

        // Set by GraphEvaluator.Evaluate before any node runs. Nodes reach it
        // indirectly through Generator.GetInputMatrix; they never touch it directly.
        private GraphEvaluator evaluator;
        // UC-T01-06: a published tile keeps lazy input pulls valid after its worker finishes.
        private GenerationSnapshot.Lease generationLease;

        internal void RetainGenerationInputs(GenerationSnapshot snapshot)
        {
            if (generationLease != null) throw new InvalidOperationException("Tile already owns generation inputs.");
            generationLease = snapshot.Acquire();
        }

        /// <summary>UC-T01-06: releases copied graph inputs after the tile's final application/consumer.</summary>
        /// <remarks>Outputs remain readable; further lazy inlet pulls are unavailable. Call after worker completion; repeated release is harmless.</remarks>
        public void ReleaseGenerationInputs()
        {
            GenerationSnapshot.Lease previous = System.Threading.Interlocked.Exchange(ref generationLease, null);
            if (previous == null) return;
            evaluator = null;
            previous.Dispose();
        }

        /// <summary>The graph being evaluated for this tile. Exposed for portal lookups.</summary>
        public Graph Graph { get { return evaluator != null ? evaluator.Graph : null; } }
        public TileData(Coord chunkCoord, CoordRect rect,
                        Vector3 worldPos, Vector3 worldSize, float worldHeight)
        {
            this.chunkCoord = chunkCoord;
            this.rect = rect;
            this.worldPos = worldPos;
            this.worldSize = worldSize;
            this.worldHeight = worldHeight;

            // Default the continent bounds to this chunk's own bounds. The chunking
            // system overrides them via SetFullWorld; a standalone whole-world tile
            // leaves them as-is, which is exactly right - the chunk IS the world.
            this.fullWorldPos = worldPos;
            this.fullWorldSize = worldSize;
        }

        /// <summary>
        /// Records the bounds of the whole world (continent) this chunk belongs to.
        /// Called by the chunking system so edge-aware nodes can measure a pixel's
        /// distance to the true world edge rather than the chunk edge.
        /// </summary>
        public void SetFullWorld(Vector3 fullWorldPos, Vector3 fullWorldSize)
        {
            this.fullWorldPos = fullWorldPos;
            this.fullWorldSize = fullWorldSize;
        }

        /// <summary>Attaches the evaluator that will resolve inlet pulls for this chunk.</summary>
        public void Bind(GraphEvaluator evaluator)
        {
            this.evaluator = evaluator;
        }

        /// <summary>
        /// Resolves the product feeding one of a node's input ports. Returns null if
        /// the port is unconnected or the tile was never bound to an evaluator.
        /// </summary>
        public object PullInput(Generator consumer, int inputIndex, StopToken stop)
        {
            if (evaluator == null) return null;
            return evaluator.PullInput(consumer, inputIndex, this, stop);
        }

        /// <summary>
        /// Resolves EVERY product feeding one of a node's input ports (fan-in), in
        /// connection-list order, or null if the port is unconnected or the tile was
        /// never bound. Used by <see cref="Generator.GetInputMatrixMerged"/>; the
        /// single-connection <see cref="PullInput"/> above is unchanged.
        /// </summary>
        public List<object> PullInputAll(Generator consumer, int inputIndex, StopToken stop)
        {
            if (evaluator == null) return null;
            return evaluator.PullInputAll(consumer, inputIndex, this, stop);
        }

        /// <summary>
        /// UC-T01-12: resolves a borrowed input; writers copy it before mutation, including bypass/retained aliases.
        /// </summary>
        /// <param name="consumer">Receiving node; null yields null.</param>
        /// <param name="inputIndex">Zero-based input socket.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <param name="mayMutate">Always false; cached products remain read-only after publication.</param>
        /// <returns>Borrowed input, or null when unbound, unwired or cancelled.</returns>
        public object PullInputForMutation(Generator consumer, int inputIndex, StopToken stop,
                                           out bool mayMutate)
        {
            mayMutate = false;
            if (evaluator == null) return null;
            return evaluator.PullInputForMutation(consumer, inputIndex, this, stop, out mayMutate);
        }

        /// <summary>Allocates a Matrix matching this chunk's rect and world placement.</summary>
        public Matrix CreateMatrix()
        {
            Matrix m = new Matrix(rect);
            m.worldPos = worldPos;
            m.worldSize = worldSize;
            m.worldHeight = worldHeight;
            return m;
        }

        /// <summary>
        /// Retrieves a cached product, or null if not yet computed. The key is
        /// "nodeId:portIndex" so each output port on a node gets its own slot.
        /// </summary>
        public object GetCached(string nodeId, int portIndex)
        {
            string key = nodeId + ":" + portIndex;
            object val;
            cache.TryGetValue(key, out val);
            return val;
        }

        /// <summary>UC-T01-12: publishes a product as read-only for every downstream consumer and retained store.</summary>
        /// <param name="nodeId">Producer's stable ID.</param>
        /// <param name="portIndex">Zero-based output index.</param>
        /// <param name="product">Produced object; subsequent writers must copy its mutable data.</param>
        public void SetCached(string nodeId, int portIndex, object product)
        {
            string key = nodeId + ":" + portIndex;
            TrackProductMemory(product, key); // UC-T02-06: cache references keep a consumer pin until explicit owner retirement.
            cache[key] = product;
        }

        /// <summary>Clears the cache, typically at the start of each chunk eval.</summary>
        public void ClearCache()
        {
            cache.Clear();
        }

        /// <summary>Clears the outputs bag, typically at the start of each chunk eval.</summary>
        public void ClearOutputs()
        {
            outputs.Clear();
        }
    }

    /// <summary>
    /// One entry in the T1 cross-bake retained cache: a node output and the content
    /// hash it was computed under. The evaluator reuses the product only while the
    /// node's current hash still equals this one.
    /// </summary>
    public struct RetainedProduct
    {
        public ulong hash;
        private object unbudgetedProduct;
        internal GraphPublication[] unbudgetedPublications;
        internal ProductMemoryBudget memoryOwner;
        internal ProductMemoryBudget.Entry memoryEntry;
        /// <summary>UC-T02-06: borrowed payload, or null after byte eviction; production reuse acquires a pin atomically.</summary>
        public object product
        {
            get => memoryEntry != null ? (memoryEntry.Product is RetainedGraphPayload payload ? payload.Product : memoryEntry.Product) : unbudgetedProduct;
            set { unbudgetedProduct = value; memoryEntry = null; memoryOwner = null; unbudgetedPublications = null; }
        }
        internal GraphPublication[] Publications => memoryEntry?.Product is RetainedGraphPayload payload ? payload.Publications : unbudgetedPublications;
    }

    /// <summary>
    /// One scattered instance produced by a Scatter node. Plain data so worker
    /// threads can build these without touching the Unity object model.
    /// </summary>
    [System.Serializable] // UC-T06-18: native serialization must retain saved manual poses and their full identities.
    public struct ScatterInstance
    {
        public Vector3 position;      // world space
        public Quaternion rotation;
        public Vector3 scale;
        public int prototypeIndex;    // index into the node's prototype list

        /// <summary>
        /// 0-1 how well this site suited the species that took it, or 0 when nothing
        /// scored it. Carried on the instance because the budgets downstream have to
        /// drop instances, and dropping by iteration order means the retained set
        /// changes whenever the emission order does - which is what makes a rebuild
        /// move colliders around for no reason the author can see (V-34).
        ///
        /// Zero reads as unscored rather than as worthless: a graph that places props
        /// geometrically has no suitability to report, and the budget must not treat
        /// that as "least fit" and evict all of it.
        /// </summary>
        public float suitability;

        /// <summary>
        /// VEG-4: the instance's true horizontal footprint radius in WORLD METRES - the crown/
        /// canopy radius for a plant, the collision/visual radius for an object - so the spacing,
        /// overlap-pruning and distance filters (VEG-2/VEG-3) reason about the real size of what
        /// was placed instead of a flat guess. 0 = unknown/unstamped, which those filters read as
        /// "fall back to the node's own radius", so an instance that never set it behaves exactly
        /// as before. Worker-safe plain data, like every other field here.
        /// </summary>
        public float footprintRadius;

        /// <summary>
        /// DEF-6: the height in WORLD METRES this instance is supposed to stand, as the species'
        /// own allometry resolved it. 0 = unknown, which the applier reads as "use the prototype's
        /// unit scale range exactly as before", so a geometric scatter with no ecology behind it is
        /// untouched.
        ///
        /// WHY THIS FIELD HAD TO EXIST AT ALL, because its absence was the whole defect.
        /// `Allometry.Resolve` already computed a real mature height in metres for every canopy
        /// instance, and `SpeciesAssignment` already held that `StemForm` in its hand - but it read
        /// only `crownRadiusM` off it and let `heightM` fall on the floor. The applier therefore had
        /// nothing to size a tree BY, and fell back to `TreePrototypeDef.heightScale` (0.8-1.25) on
        /// the prefab's native mesh size. So a species authored at 20-35 m and one authored at 2 m
        /// produced the SAME tree, and every tree was whatever size its prefab happened to be
        /// modelled at. That is the author's "see also the trees how little they are".
        ///
        /// Carrying it on the instance costs one float and no extra allometry: the form is already
        /// resolved at that point for the crown radius. This is the same channel VEG-4 opened for
        /// `footprintRadius`, used for the other half of the same form.
        /// </summary>
        public float targetHeightM;

        /// <summary>
        /// VEG-13: a STABLE per-instance id, derived from the instance's quantised world position so
        /// it is the same across re-bakes. It is what lets a harvested/erased instance stay gone after
        /// a chunk reloads (PERF-2's persistent delta keys on it) and what an editor erase brush marks.
        /// 0 = unassigned (an instance the scatter pass did not stamp), which reads as "no identity".
        /// </summary>
        public int instanceId;

        /// <summary>UC-T06-06: full persisted identity; default for legacy/unowned placement. Retain instanceId for explicit legacy edit migration.</summary>
        public TerrainSystem.VegetationInstanceKey identity;

        /// <summary>UC-T06-07: procedural contact data in world metres; default leaves an unowned/manual transform unchanged.</summary>
        public TerrainSystem.ScatterGroundBinding ground;
        /// <summary>UC-T06-18: explicit saved manual pose bypasses procedural variation/allometry and automatic ground movement.</summary>
        public bool manualPose;

        public ScatterInstance(Vector3 position, Quaternion rotation, Vector3 scale, int prototypeIndex)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
            this.prototypeIndex = prototypeIndex;
            this.suitability = 0f;
            this.footprintRadius = 0f;
            this.instanceId = 0;
            this.identity = default; // UC-T06-06: only a captured world/prototype owner may stamp a full key.
            this.ground = default; // UC-T06-07: only a placement producer may opt into final-ground seating.
            this.manualPose = false; // UC-T06-18: only the persistent intent owner may lock a transform.
            this.targetHeightM = 0f;
        }

        public ScatterInstance(Vector3 position, Quaternion rotation, Vector3 scale,
                               int prototypeIndex, float suitability)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
            this.prototypeIndex = prototypeIndex;
            this.suitability = suitability;
            this.footprintRadius = 0f;
            this.instanceId = 0;
            this.identity = default; // UC-T06-06
            this.ground = default; // UC-T06-07
            this.manualPose = false; // UC-T06-18
            this.targetHeightM = 0f;
        }

        public ScatterInstance(Vector3 position, Quaternion rotation, Vector3 scale,
                               int prototypeIndex, float suitability, float footprintRadius)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
            this.prototypeIndex = prototypeIndex;
            this.suitability = suitability;
            this.footprintRadius = footprintRadius;
            this.instanceId = 0;
            this.identity = default; // UC-T06-06
            this.ground = default; // UC-T06-07
            this.manualPose = false; // UC-T06-18
            this.targetHeightM = 0f;
        }
    }

    /// <summary>
    /// Heightfield mesh built off-thread. The main thread turns this into a
    /// UnityEngine.Mesh; constructing Mesh on a worker is not permitted.
    /// </summary>
    public class MeshBuildData
    {
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector2[] uv;
        public int[] triangles;
    }

    /// <summary>
    /// Textures for ONE splat channel that an output node reserves but the world has
    /// not authored a TerrainLayer for (biome-splat snow / road / grass / forest, …).
    /// The channel is addressed by INDEX, never by a layer name, so renaming a layer
    /// can never break which texture lands where. Every field is empty/neutral by
    /// default: the tool ships no assumption that any texture file exists, and any
    /// texture FORMAT Unity can import is accepted (Texture2D is format-agnostic).
    /// An empty albedo paints a flat <see cref="tint"/> stand-in, never a checkerboard.
    /// </summary>
    [Serializable]
    public struct SplatLayerTexture
    {
        /// <summary>Splat channel (terrain layer index) these textures dress.</summary>
        public int layer;

        /// <summary>Display-only name for the slot (e.g. "Grass", "Rock"). Guides the
        /// user to the right channel; it is NOT compared when caching a minted layer
        /// and never keys any behaviour - the <see cref="layer"/> index is the contract.</summary>
        public string label;

        /// <summary>Base colour map, or null for a flat <see cref="tint"/> layer.</summary>
        public Texture2D albedo;

        /// <summary>Tangent-space normal map, or null for a flat-lit layer.</summary>
        public Texture2D normal;

        /// <summary>Ambient-occlusion map, packed into the mask's occlusion channel when present.</summary>
        public Texture2D ao;

        /// <summary>Flat colour shown until an albedo is assigned, and the diffuse tint after.</summary>
        public Color tint;

        /// <summary>Metres per texture repeat. Zero falls back to a sane default.</summary>
        public Vector2 tileSizeMetres;

        /// <summary>Surface smoothness used when no packed mask is supplied, 0..1.</summary>
        public float smoothness;

        /// <summary>Normal map strength. Zero falls back to 1.</summary>
        public float normalScale;

        /// <summary>
        /// Whether the author has supplied anything for this layer (a map or a tint).
        /// </summary>
        /// <remarks>
        /// THREAD SAFETY - read this before "simplifying" the null tests back.
        /// This property is evaluated on a WORKER thread: BiomeSplatOutputNode.WriteOutput
        /// runs inside the ChunkManager Task.Run dispatch. In Unity 6 the overloaded
        /// UnityEngine.Object `!=` operator is NOT thread-safe - op_Inequality calls
        /// CompareBaseObjects -> IsNativeObjectAlive -> GetEntityId -> EnsureRunningOnMainThread,
        /// which THROWS "EnsureRunningOnMainThread can only be called from the main thread".
        /// Written as `albedo != null` this killed every chunk bake with
        /// InvalidOperationException and the world came back as "N cells missing their surface".
        ///
        /// ReferenceEquals compares the managed reference only - no native call, no thread
        /// assertion - so it is safe here. The difference is that a DESTROYED texture reads
        /// as present rather than as null; that is acceptable (and strictly better than
        /// throwing), because the apply path is main-thread and re-validates the texture
        /// before it reaches TerrainData. Never use `==`/`!=` against a UnityEngine.Object
        /// anywhere that can run off the main thread.
        /// </remarks>
        public bool HasContent
        {
            get
            {
                return !ReferenceEquals(albedo, null) ||
                       !ReferenceEquals(normal, null) ||
                       !ReferenceEquals(ao, null) ||
                       tint.r > 0f || tint.g > 0f || tint.b > 0f;
            }
        }
    }

    /// <summary>
    /// Accumulator for main-thread writes. Each Output node records its data here,
    /// and the chunking system applies them all in one batch after the background
    /// eval completes.
    /// </summary>
    public class OutputBag
    {
        /// <summary>UC-T06-05: immutable Poisson sampling work/rejection counts by producer publication ID, replayed on cache hits and cleared with this bag.</summary>
        /// <remarks>Read after evaluation completes. These counts precede later cluster/habitat filters and native application.</remarks>
        public readonly Dictionary<string, PoissonSamplingReport> scatterSamplingReports = new Dictionary<string, PoissonSamplingReport>(StringComparer.Ordinal);
        /// <summary>UC-T06-17: immutable completed placement-stage counts/units by producer, replayed with outputs and cleared on replacement.</summary>
        /// <remarks>Read after evaluation/acceptance; sampler attempts and native/rendered counts have separate owners.</remarks>
        public readonly Dictionary<string, Scatter.VegetationPlacementReport> vegetationPlacementReports = new Dictionary<string, Scatter.VegetationPlacementReport>(StringComparer.Ordinal);
        /// <summary>UC-T03-05: immutable achieved/requested river counts keyed by producing node publication ID.
        /// Filled during evaluation, including retained replay; cleared with the output bag. Read after evaluation completes.</summary>
        public readonly Dictionary<string, TerrainSystem.RiverHierarchyReport> riverHierarchyReports =
            new Dictionary<string, TerrainSystem.RiverHierarchyReport>(StringComparer.Ordinal);
        /// <summary>UC-T03-07: world/producer-owned lake spill diagnostics, including closed and unselected lakes.
        /// Replayed with retained channels, cleared with this output bag; read after evaluation completes.</summary>
        public readonly Dictionary<string, TerrainSystem.LakeOutletSnapshot> lakeOutletReports =
            new Dictionary<string, TerrainSystem.LakeOutletSnapshot>(StringComparer.Ordinal);
        /// <summary>UC-T03-08: immutable eligible/requested/selected lake counts and area distributions.
        /// Replayed with retained masks and cleared on reset; read after generation completes.</summary>
        public readonly Dictionary<string, TerrainSystem.LakeDistributionReport> lakeDistributionReports =
            new Dictionary<string, TerrainSystem.LakeDistributionReport>(StringComparer.Ordinal);
        // UC-T02-07: current-run scene publications accompany the accepted native output, including an empty replacement.
        internal ChannelGraphPublication[] channelPublications;
        internal Rect publicationBounds;
        internal string publicationOwner;
        internal float publicationOriginY, publicationHeight = 1; // UC-T03-09: captured native height units for final cross-sections.
        internal float normalStrength = 1; // UC-T03-10: authored custom-normal strength survives final-ground reconciliation.
        internal float[,] trenchRelief; // UC-T03-10: surviving normalized procedural cut; null means no height-branch provenance.
        /// <summary>UC-T02-01: owning world/revision; consumers reject obsolete assigned identities before applying outputs.</summary>
        public TerrainSystem.GenerationRunId RunId { get; internal set; }
        /// <summary>Heightmap to write via TerrainData.SetHeights, or null.</summary>
        public float[,] heightmap;

        /// <summary>Splatmap layers to write via TerrainData.SetAlphamaps, or null.</summary>
        public float[,,] splatmap;

        // UC-T07-03: immutable classifier labels accompany the accepted ground; image/material consumers never evaluate the main graph again.
        internal Matrix biomeClassification;

        /// <summary>
        /// Textures for splat channels an output node reserves but the world has not
        /// authored a TerrainLayer for (e.g. biome-splat snow at index 4). Read by
        /// TerrainBuilder.ReconcileSplatLayers, which mints a real layer per entry
        /// instead of the neutral stand-in. Null on any graph that reserves no such
        /// channel, so it changes nothing for those graphs.
        /// </summary>
        public List<SplatLayerTexture> splatLayerTextures;

        /// <summary>Hole map to write via TerrainData.SetHoles, or null.</summary>
        public bool[,] holes;

        /// <summary>Detail (grass) layers keyed by detail prototype index.</summary>
        public Dictionary<int, int[,]> detailLayers = new Dictionary<int, int[,]>();

        /// <summary>Per-pixel normals, for nodes that bake a normal map.</summary>
        public Vector3[] normals;

        /// <summary>Scattered instances keyed by output channel name.</summary>
        public Dictionary<string, List<ScatterInstance>> scatter =
            new Dictionary<string, List<ScatterInstance>>();

        /// <summary>
        /// Tally of ecology-admitted scatter placements per <see cref="Ecology.LandformZone"/>,
        /// accumulated by species assignment as it keeps each instance. It counts what
        /// the ecology model ADMITTED, before the render budget thins a crowded chunk,
        /// and only the discrete channels that pass through species assignment - trees,
        /// shrubs, objects, underwater. Grass is not here on purpose: it is a rasterised
        /// density grid, never a per-site species decision, so it has no zone to tally.
        /// The applier flushes this to the vegetation manager's registry (V-52) so the
        /// diagnostics panel can show where a world's planting actually landed.
        /// </summary>
        public int[] zoneHistogram = new int[Ecology.LandformAssemblage.ZoneCount];

        /// <summary>Meshes to construct on the main thread, keyed by channel name.</summary>
        public Dictionary<string, MeshBuildData> meshes =
            new Dictionary<string, MeshBuildData>();

        /// <summary>Preview thumbnails produced by Preview nodes, keyed by node id.</summary>
        public Dictionary<string, float[]> previews = new Dictionary<string, float[]>();

        /// <summary>
        /// If true, the chunking system rebuilds the terrain collider after applying
        /// outputs. Set by HeightOutput when it writes a new heightmap.
        /// </summary>
        public bool requestColliderRebuild;

        // ---- wind ------------------------------------------------------------
        //
        //  Written by Wind Output, read by the applier, which uploads them to
        //  UCWindManager as a texture. Null on a graph with no wind branch, and
        //  everything downstream treats null as "uniform wind", so nothing here
        //  changes the behaviour of a graph that does not use it.

        /// <summary>Baked sway amplitude, 0..1, row-major, or null.</summary>
        public float[] windSwayAmplitude;

        /// <summary>Baked sway phase, 0..1, same layout as amplitude, or null.</summary>
        public float[] windSwayPhase;

        /// <summary>Sample dimensions of the sway maps.</summary>
        public Vector2Int windSwaySize;

        /// <summary>World XZ of the sway map's minimum corner, in metres.</summary>
        public Vector2 windSwayWorldMin;

        /// <summary>World XZ extent the sway map covers, in metres.</summary>
        public Vector2 windSwayWorldSize;

        // ---- weather ---------------------------------------------------------
        //
        //  Written by Weather Output, read by the weather driver as biases on the
        //  climate the physics already computed - added or scaled onto it, never a
        //  replacement for it. Null on a graph with no weather branch, and the driver
        //  reads a null field as "no bias here", so a world that wires none behaves
        //  exactly as one with no weather output. All eight share the one sample grid.

        /// <summary>Precipitation bias field, row-major, or null.</summary>
        public float[] weatherPrecipBias;

        /// <summary>Temperature bias field, same layout, or null.</summary>
        public float[] weatherTemperatureBias;

        /// <summary>Wind bias field, same layout, or null.</summary>
        public float[] weatherWindBias;

        /// <summary>Fog bias field, same layout, or null.</summary>
        public float[] weatherFogBias;

        /// <summary>Cloud bias field, same layout, or null.</summary>
        public float[] weatherCloudBias;

        /// <summary>Snow bias field, same layout, or null.</summary>
        public float[] weatherSnowBias;

        /// <summary>Storm bias field, same layout, or null.</summary>
        public float[] weatherStormBias;

        /// <summary>Turbidity bias field, same layout, or null.</summary>
        public float[] weatherTurbidityBias;

        /// <summary>Sample dimensions of the bias fields.</summary>
        public Vector2Int weatherBiasSize;

        /// <summary>World XZ of the bias grid's minimum corner, in metres.</summary>
        public Vector2 weatherBiasWorldMin;

        /// <summary>World XZ extent the bias grid covers, in metres.</summary>
        public Vector2 weatherBiasWorldSize;

        // ---- lighting biases (§ 8.12) ---------------------------------------
        //
        //  Written by Lighting Output, read by the lighting driver as biases on the
        //  physically-based sky the atmosphere model already produced - a graph may
        //  push one region's air hazier or its sky warmer, but it never decouples the
        //  sky from the sun's position. Null on a graph with no lighting branch, read
        //  as "no bias here", so a world that wires none behaves exactly as one with no
        //  lighting output. The first three keep the retired M4 § 4.7 names so a saved
        //  graph reconnects; the last two are appended. All five share one sample grid.

        /// <summary>Sky tint bias field, row-major, or null.</summary>
        public float[] lightingSkyTintBias;

        /// <summary>Ambient scale bias field, same layout, or null.</summary>
        public float[] lightingAmbientScaleBias;

        /// <summary>Turbidity bias field, same layout, or null.</summary>
        public float[] lightingTurbidityBias;

        /// <summary>Cloud-cover bias field, same layout, or null.</summary>
        public float[] lightingCloudCoverBias;

        /// <summary>Exposure bias field, same layout, or null.</summary>
        public float[] lightingExposureBias;

        /// <summary>Sample dimensions of the lighting bias fields.</summary>
        public Vector2Int lightingBiasSize;

        /// <summary>World XZ of the lighting bias grid's minimum corner, in metres.</summary>
        public Vector2 lightingBiasWorldMin;

        /// <summary>World XZ extent the lighting bias grid covers, in metres.</summary>
        public Vector2 lightingBiasWorldSize;

        // ---- climate normals -------------------------------------------------
        //
        //  The chunk's twelve-month climate (W-09), computed once per chunk and
        //  recorded here so the ecology layer reads the SAME climate the weather
        //  module derived rather than a second one of its own. Null on a graph with
        //  no weather branch, and the ecology sampler reads null as "use the resolved
        //  settings", so a world that never produces normals behaves exactly as it
        //  did before. A reference, not a copy: ClimateNormals owns six month arrays,
        //  so sharing the one instance is both cheaper and the only way two readers
        //  cannot disagree about a month.

        /// <summary>The chunk's climate normals, or null when no weather branch produced them.</summary>
        public Weather.ClimateNormals climateNormal;

        /// <summary>UC-T08-03: accepted immutable early climate recipe, independent of final ground and live weather.</summary>
        public Weather.ClimateBaselineProduct climateBaseline;

        /// <summary>UC-T08-06: immutable authored weather regions, independent of early terrain climate and owned by the accepted snapshot.</summary>
        public Weather.WeatherRegionProduct weatherRegions;

        // ---- texture graph (Module 2) ---------------------------------------
        //
        //  The texture graph terminates in Shader Output, which - like every other
        //  output node - describes what to write rather than writing it. These four
        //  fields are that description. They are additive: nothing above reads them,
        //  and a height-only graph leaves them null exactly as before.

        /// <summary>
        /// Finished material description from Shader Output, or null when the graph
        /// has no texture chain. UCTextureGraphApplier turns this into a Material,
        /// a TerrainLayer array and any baked maps.
        /// </summary>
        public SurfaceProduct surface;

        /// <summary>Asset name for the generated material.</summary>
        public string surfaceMaterialName;

        /// <summary>Whether Shader Output was asked to rewrite the TerrainLayer array.</summary>
        public bool surfaceWriteLayers;

        /// <summary>Project folder the generated material, layers and maps land in.</summary>
        public string surfaceOutputFolder;

        /// <summary>
        /// Which shader Shader Output targeted, as the integer value of its
        /// ShaderTarget enum. Stored as an int and not as the enum because
        /// ShaderTarget lives in UniverseCreator.Nodes.Texturing, which is a child
        /// of this namespace - a parent cannot name a type in a child namespace
        /// without a using directive, and this file predates that namespace.
        ///
        /// Stored here rather than as a key in SurfaceProduct.floats on purpose:
        /// UCSelfTest asks the shader whether it has every float key the graph
        /// wrote, and a bookkeeping key that is deliberately not a shader property
        /// would fail a test that was working correctly.
        /// </summary>
        public int surfaceShaderTarget;

        /// <summary>Appends instances to a named scatter channel.</summary>
        public void AddScatter(string channel, List<ScatterInstance> instances)
        {
            if (string.IsNullOrEmpty(channel) || instances == null) return;

            List<ScatterInstance> existing;
            if (scatter.TryGetValue(channel, out existing)) existing.AddRange(instances);
            else scatter[channel] = new List<ScatterInstance>(instances);
        }

        /// <summary>
        /// Records one kept instance against its landform zone. Called by species
        /// assignment for the survivor of each site, so the tally follows the same
        /// admit/reject decision the world was built from. Out-of-range ids are
        /// ignored rather than throwing: a zone the classifier cannot return is a
        /// bug to find in the classifier, not a reason to lose a whole chunk's build.
        /// </summary>
        public void AddZone(int zone)
        {
            if ((uint)zone < (uint)zoneHistogram.Length) zoneHistogram[zone]++;
        }

        public void Clear()
        {
            scatterSamplingReports.Clear(); // UC-T06-05: removed/replaced samplers cannot retain stale diagnostics.
            vegetationPlacementReports.Clear(); // UC-T06-17: replaced producers cannot leave sparse-placement evidence.
            riverHierarchyReports.Clear(); // UC-T03-05: a replaced graph cannot retain an old count diagnostic.
            lakeOutletReports.Clear(); // UC-T03-07: a removed lake producer cannot leave old source records.
            lakeDistributionReports.Clear(); // UC-T03-08: replaced masks cannot retain old target/area reports.
            channelPublications = null; publicationOwner = null; publicationBounds = default; // UC-T02-07: an output reset cannot keep old commit metadata.
            normalStrength = 1; publicationOriginY = 0; publicationHeight = 1; // UC-T03-10: no stale units or normal controls on reuse.
            trenchRelief = null;
            heightmap = null;
            splatmap = null;
            biomeClassification = null; // UC-T07-03: a reused output bag cannot publish a retired biome field.
            holes = null;
            normals = null;
            requestColliderRebuild = false;
            surface = null;
            surfaceMaterialName = null;
            surfaceOutputFolder = null;
            surfaceWriteLayers = false;
            surfaceShaderTarget = 0;
            detailLayers.Clear();
            scatter.Clear();
            meshes.Clear();
            previews.Clear();
            Array.Clear(zoneHistogram, 0, zoneHistogram.Length);

            // Wind. A recycled bag whose graph has no wind branch would otherwise
            // keep the previous chunk's sway map and stamp it over ground that was
            // never baked - a patch of still air in the middle of a windy field.
            windSwayAmplitude = null;
            windSwayPhase = null;
            windSwaySize = Vector2Int.zero;
            windSwayWorldMin = Vector2.zero;
            windSwayWorldSize = Vector2.zero;

            // Weather. Same reason as the wind maps: a recycled bag must not carry one
            // chunk's authored biases onto the next, or a painted wet coast would smear
            // inland across ground the author never touched.
            weatherPrecipBias = null;
            weatherTemperatureBias = null;
            weatherWindBias = null;
            weatherFogBias = null;
            weatherCloudBias = null;
            weatherSnowBias = null;
            weatherStormBias = null;
            weatherTurbidityBias = null;
            weatherBiasSize = Vector2Int.zero;
            weatherBiasWorldMin = Vector2.zero;
            weatherBiasWorldSize = Vector2.zero;

            // Lighting. Same reason: a recycled bag must not carry one chunk's authored
            // sky/air biases onto the next, or a painted patch of hazy air would drift
            // across ground the author never touched.
            lightingSkyTintBias = null;
            lightingAmbientScaleBias = null;
            lightingTurbidityBias = null;
            lightingCloudCoverBias = null;
            lightingExposureBias = null;
            lightingBiasSize = Vector2Int.zero;
            lightingBiasWorldMin = Vector2.zero;
            lightingBiasWorldSize = Vector2.zero;

            // Climate normals. A recycled bag must not hand the next chunk the last
            // chunk's year, or a valley's climate would be read on the ridge above it.
            climateNormal = null;
            climateBaseline = null; // UC-T08-03: recycled tiles cannot retain another region's authored baseline.
            weatherRegions = null; // UC-T08-06: retire region recipes through the same output ownership lifecycle.
        }
    }
}
