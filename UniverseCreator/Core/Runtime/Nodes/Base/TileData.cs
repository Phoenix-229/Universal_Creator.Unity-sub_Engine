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
    public class TileData
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
        /// Main-thread output bag. Output nodes record what they want to write
        /// (heightmap, splat, holes, collision) here, and the chunking system applies
        /// them after the background eval finishes. This keeps TerrainData.SetHeights
        /// off the worker thread.
        /// </summary>
        public OutputBag outputs = new OutputBag();

        // Set by GraphEvaluator.Evaluate before any node runs. Nodes reach it
        // indirectly through Generator.GetInputMatrix; they never touch it directly.
        private GraphEvaluator evaluator;

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

        /// <summary>Stores a product in the cache.</summary>
        public void SetCached(string nodeId, int portIndex, object product)
        {
            string key = nodeId + ":" + portIndex;
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
    /// One scattered instance produced by a Scatter node. Plain data so worker
    /// threads can build these without touching the Unity object model.
    /// </summary>
    public struct ScatterInstance
    {
        public Vector3 position;      // world space
        public Quaternion rotation;
        public Vector3 scale;
        public int prototypeIndex;    // index into the node's prototype list

        public ScatterInstance(Vector3 position, Quaternion rotation, Vector3 scale, int prototypeIndex)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
            this.prototypeIndex = prototypeIndex;
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
    /// Accumulator for main-thread writes. Each Output node records its data here,
    /// and the chunking system applies them all in one batch after the background
    /// eval completes.
    /// </summary>
    public class OutputBag
    {
        /// <summary>Heightmap to write via TerrainData.SetHeights, or null.</summary>
        public float[,] heightmap;

        /// <summary>Splatmap layers to write via TerrainData.SetAlphamaps, or null.</summary>
        public float[,,] splatmap;

        /// <summary>Hole map to write via TerrainData.SetHoles, or null.</summary>
        public bool[,] holes;

        /// <summary>Detail (grass) layers keyed by detail prototype index.</summary>
        public Dictionary<int, int[,]> detailLayers = new Dictionary<int, int[,]>();

        /// <summary>Per-pixel normals, for nodes that bake a normal map.</summary>
        public Vector3[] normals;

        /// <summary>Scattered instances keyed by output channel name.</summary>
        public Dictionary<string, List<ScatterInstance>> scatter =
            new Dictionary<string, List<ScatterInstance>>();

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

        /// <summary>Appends instances to a named scatter channel.</summary>
        public void AddScatter(string channel, List<ScatterInstance> instances)
        {
            if (string.IsNullOrEmpty(channel) || instances == null) return;

            List<ScatterInstance> existing;
            if (scatter.TryGetValue(channel, out existing)) existing.AddRange(instances);
            else scatter[channel] = new List<ScatterInstance>(instances);
        }

        public void Clear()
        {
            heightmap = null;
            splatmap = null;
            holes = null;
            normals = null;
            requestColliderRebuild = false;
            detailLayers.Clear();
            scatter.Clear();
            meshes.Clear();
            previews.Clear();
        }
    }
}
