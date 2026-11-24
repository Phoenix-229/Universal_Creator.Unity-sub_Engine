// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Generator.cs
//  Purpose: Abstract base for all node types. Each subclass declares its ports
//           via GetInputs()/GetOutputs() and implements Generate() to produce
//           outputs from inputs in a pull-based lazy model.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T01-08: execution requirements for Generate; preparation always uses Unity's main thread.</summary>
    /// <remarks>A node declares exactly one value. Executors may combine values to describe the domains they admit.</remarks>
    [Flags]
    public enum NodeExecutionDomain
    {
        /// <summary>Consumes prepared managed data; may execute on a CPU worker or synchronously on the main thread.</summary>
        Worker = 1,
        /// <summary>Requires Unity object/resource APIs on the main thread.</summary>
        MainThread = 2,
        /// <summary>Requires the main thread and graphics support for GPU submission/readback.</summary>
        MainThreadGpu = 4
    }

    /// <summary>
    /// Base class for all nodes in the procedural content graph. Unity serializes
    /// a flat list of Generator subclasses, and the Graph holds the Connection[]
    /// that wires them together.
    /// </summary>
    [Serializable]
    public abstract class Generator
    {
        /// <summary>
        /// Unique id for this node instance. Used in Connection.sourceNodeId /
        /// targetNodeId and as the cache key. Generated once on creation and never
        /// changes, even if the node is duplicated (the duplicate gets a new id).
        /// </summary>
        public string id = Guid.NewGuid().ToString();

        /// <summary>Editor position in the node graph window, in canvas pixels.</summary>
        public Vector2 editorPosition;

        /// <summary>
        /// Optional display name. If empty, the graph editor shows the class name.
        /// </summary>
        public string displayName = "";

        /// <summary>
        /// Internal version stamp incremented whenever a parameter changes. The
        /// evaluator uses this to invalidate cached products downstream.
        /// </summary>
        public int version;

        /// <summary>
        /// T1: a content hash of this node's whole serialized state COMBINED with every
        /// upstream node's hash, filled on the main thread by <see cref="GraphContentHasher"/>
        /// before a bake dispatches. It changes if and only if this node's params - or
        /// anything feeding it - changed, so a per-chunk retained cache can forward an
        /// unchanged node's product and recompute only what the edit actually touched.
        /// Not serialized: it is derived state, recomputed each bake. 0 = not computed
        /// (the retained cache then always recomputes, i.e. today's behaviour).
        /// </summary>
        [NonSerialized] public ulong contentHash;

        // UC-T01-06/07: copied bake context affects only nodes that consume it, before upstream hashing.
        [NonSerialized] internal ulong preparedContextHash;

        /// <summary>
        /// If true, this node is currently disabled and Generate() will not be called.
        /// UC-T03-03: disabled nodes forward the first connected type-compatible input;
        /// they return null when none matches, and terminal nodes write no output.
        /// </summary>
        public bool disabled;

        // ------------------------------------------------------------------
        //  Identity - abstract so a new node cannot forget to declare them
        // ------------------------------------------------------------------

        /// <summary>Display name shown in the node header and add-node menu.</summary>
        public abstract string NodeName { get; }

        /// <summary>Category, driving header colour and menu grouping.</summary>
        public abstract NodeCategory Category { get; }

        /// <summary>Optional one-line description for the add-node menu tooltip.</summary>
        public virtual string Tooltip { get { return string.Empty; } }

        /// <summary>UC-T01-08: declares the execution domain captured by a compiled plan; defaults to prepared CPU work.</summary>
        /// <remarks>
        /// Override for Generate implementations that touch live Unity objects or submit GPU work.
        /// This computed metadata is not serialized and does not itself schedule or authorize execution.
        /// Output nodes that only fill a managed output bag remain Worker; the later Unity commit is separate.
        /// </remarks>
        public virtual NodeExecutionDomain ExecutionDomain { get { return NodeExecutionDomain.Worker; } }

        /// <summary>Authoring domains admitted by runtime validation and creation palettes; plugins may declare additional supported domains.</summary>
        /// <remarks>UC-T05-03: LegacyMixed preserves existing nodes; domain support never changes the node's execution-thread requirements.</remarks>
        public virtual UCGraphDomainMask GraphDomains => GraphDomainRules.DefaultFor(this);

        /// <summary>UC-T02-05: whether a MainThreadGpu node can produce a supported result without graphics.</summary>
        /// <remarks>Defaults to false. A fallback still runs on the main thread and must reject inputs it cannot read;
        /// it must never silently replace missing GPU data with an unrelated result.</remarks>
        public virtual bool HasCpuFallback => false;

        /// <summary>Header colour derived from the category.</summary>
        public Color HeaderColor { get { return NodeCategoryColors.Get(Category); } }

        /// <summary>
        /// Called on the main thread immediately before a generation run is
        /// dispatched to a worker. Nodes that read Unity objects (Texture2D pixels,
        /// AnimationCurve, TerrainData) must snapshot them here into plain arrays,
        /// because Generate() runs off-thread where those APIs are illegal.
        /// </summary>
        public virtual void PrepareMainThread() { }
        /// <summary>UC-T07-07: actionable error for invalid authored node settings, or null when settings are supported.</summary>
        /// <remarks>Read-only and worker-safe; do not query live Unity objects or mutate preparation/evaluation state here.</remarks>
        public virtual string SettingsDiagnostic => null;

        /// <summary>
        /// Called EXACTLY ONCE, only when a node is first created by the user in the editor
        /// (<c>NodeCatalog.Instantiate</c>) - never on deserialize/load and never on the
        /// catalog's throwaway probe. Lets a node adopt a first-time default that must NOT
        /// change nodes that already exist in a saved graph: the serialized field keeps its
        /// legacy value (so every existing world deserializes byte-for-byte unchanged), and
        /// only a genuinely new node runs this. Base is a no-op.
        /// </summary>
        public virtual void OnCreatedFresh() { }

        // ------------------------------------------------------------------
        //  Port metadata - subclasses override to declare their interface
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the input ports this node can receive. For most nodes this is
        /// a static readonly array. The graph editor and evaluator call this to
        /// build the connection UI and type-check links.
        /// </summary>
        public abstract Port[] GetInputs();

        /// <summary>Returns the output ports this node can produce.</summary>
        public abstract Port[] GetOutputs();

        // ------------------------------------------------------------------
        //  Generation
        // ------------------------------------------------------------------

        /// <summary>
        /// Produces the output at the given port index. The evaluator calls this
        /// exactly once per chunk per output port, caches the result in TileData,
        /// and returns the cached product on subsequent pulls.
        ///
        /// Implementations must:
        /// - Check stopToken.StopRequested at regular intervals (per-iteration, per-
        ///   erosion pass, etc.) and return early if true.
        /// - Pull inputs via GetInputMatrix(tile, inputPortIndex), which recursively
        ///   evaluates upstream nodes.
        /// - Allocate and return a new product; never mutate a pulled input in place
        ///   unless you deep-copy it first (fan-out means the same Matrix may feed
        ///   multiple nodes).
        /// - Avoid touching UnityEngine API on the worker thread. Use TileData fields
        ///   (worldPos, worldSize, worldHeight) for spatial context instead.
        /// </summary>
        public abstract object Generate(int outputIndex, TileData tile, StopToken stopToken);

        /// <summary>
        /// Marks this node's parameters dirty, forcing re-evaluation. The graph
        /// window or custom inspector calls this when a serialized field changes.
        /// </summary>
        public void MarkDirty()
        {
            version++;
        }

        // ------------------------------------------------------------------
        //  Input helpers - subclasses call these inside Generate()
        // ------------------------------------------------------------------

        /// <summary>
        /// Pulls the Matrix connected to the given input port, or returns null if
        /// no connection exists. The evaluator recursively walks upstream and caches
        /// the result, so calling this twice on the same input in the same chunk
        /// returns the same instance without recomputing.
        /// </summary>
        protected Matrix GetInputMatrix(TileData tile, int inputIndex, StopToken stop)
        {
            if (tile == null) return null;
            return tile.PullInput(this, inputIndex, stop) as Matrix;
        }

        /// <summary>
        /// Pulls the Matrix connected to the given input port, or returns a fallback
        /// if no connection exists.
        /// </summary>
        protected Matrix GetInputMatrixOrDefault(TileData tile, int inputIndex, StopToken stop, Matrix fallback)
        {
            Matrix m = GetInputMatrix(tile, inputIndex, stop);
            return m != null ? m : fallback;
        }

        /// <summary>
        /// Pulls the Matrix connected to the given input port, or a blank Matrix
        /// shaped to the chunk if the port is unconnected.
        /// </summary>
        protected Matrix GetInputMatrixOrBlank(TileData tile, int inputIndex, StopToken stop)
        {
            Matrix m = GetInputMatrix(tile, inputIndex, stop);
            return m != null ? m : tile.CreateMatrix();
        }

        /// <summary>
        /// Pulls an input and returns a deep copy safe to mutate in place. Fan-out
        /// means the upstream Matrix may feed several nodes, so modifiers must never
        /// write to what they pulled.
        /// </summary>
        protected Matrix GetInputMatrixCopy(TileData tile, int inputIndex, StopToken stop)
        {
            Matrix m = GetInputMatrix(tile, inputIndex, stop);
            return m != null ? new Matrix(m) : tile.CreateMatrix();
        }

        /// <summary>
        /// Fan-in: folds EVERY product wired into a multi-input port into one
        /// Matrix by <paramref name="op"/>, instead of the single-connection
        /// last-write-wins that <see cref="GetInputMatrix"/> resolves. Use this on
        /// a port declared with <see cref="Port.allowMultiple"/> = true.
        ///
        /// Returns null when nothing is wired; the ONE input by reference when
        /// exactly one is wired (the fold would only allocate a copy identical to
        /// it, and this product is shared/retained so the caller must treat it as
        /// read-only, exactly as GetInputMatrix already requires); and a freshly
        /// allocated fold otherwise. It never writes into a pulled input - fan-out
        /// means the same Matrix may feed other nodes. Inputs whose shape does not
        /// match the first are skipped rather than throwing, so a mis-sized wire
        /// cannot take the whole bake down.
        /// </summary>
        protected Matrix GetInputMatrixMerged(TileData tile, int inputIndex, StopToken stop, MergeOp op)
        {
            if (tile == null) return null;

            List<object> products = tile.PullInputAll(this, inputIndex, stop);
            if (products == null || products.Count == 0) return null;

            // Count the Matrix-typed products; a single one is handed straight back.
            Matrix firstMatrix = null;
            int matrixCount = 0;
            for (int i = 0; i < products.Count; i++)
            {
                Matrix m = products[i] as Matrix;
                if (m == null) continue;
                if (firstMatrix == null) firstMatrix = m;
                matrixCount++;
            }
            if (firstMatrix == null) return null;
            if (matrixCount == 1) return firstMatrix;

            Matrix result = firstMatrix.Blank();
            // UC-T03-02: fold the reference with the same operation/count as the height.
            // One extra field only when at least one connected input carries river relief.
            for (int i = 0; i < products.Count; i++)
                if (products[i] is Matrix input && input.arr.Length == result.arr.Length && input.trenchReference != null)
                { result.trenchReference = result.Blank(); break; }
            bool seeded = false;
            int folded = 0;

            for (int i = 0; i < products.Count; i++)
            {
                Matrix m = products[i] as Matrix;
                if (m == null || m.arr.Length != result.arr.Length) continue;

                if (!seeded)
                {
                    Array.Copy(m.arr, result.arr, result.arr.Length);
                    if (result.trenchReference != null)
                        Array.Copy((m.trenchReference ?? m).arr, result.trenchReference.arr, result.arr.Length);
                    seeded = true;
                    folded = 1;
                    continue;
                }

                folded++;
                if (result.trenchReference != null)
                {
                    float[] reference = result.trenchReference.arr;
                    float[] next = (m.trenchReference ?? m).arr;
                    for (int k = 0; k < reference.Length; k++)
                    {
                        if (op == MergeOp.Max) reference[k] = Math.Max(reference[k], next[k]);
                        else if (op == MergeOp.Min) reference[k] = Math.Min(reference[k], next[k]);
                        else reference[k] += next[k];
                    }
                }
                switch (op)
                {
                    case MergeOp.Max:
                        for (int k = 0; k < result.arr.Length; k++)
                        { float v = m.arr[k]; if (v > result.arr[k]) result.arr[k] = v; }
                        break;
                    case MergeOp.Min:
                        for (int k = 0; k < result.arr.Length; k++)
                        { float v = m.arr[k]; if (v < result.arr[k]) result.arr[k] = v; }
                        break;
                    case MergeOp.Sum:
                    case MergeOp.Average:
                        for (int k = 0; k < result.arr.Length; k++) result.arr[k] += m.arr[k];
                        break;
                }
            }

            if (op == MergeOp.Average && folded > 1)
            {
                float inv = 1f / folded;
                for (int k = 0; k < result.arr.Length; k++) result.arr[k] *= inv;
                if (result.trenchReference != null)
                    for (int k = 0; k < result.arr.Length; k++) result.trenchReference.arr[k] *= inv;
            }

            return result;
        }

        // ------------------------------------------------------------------
        //  Lifecycle callbacks - subclasses can override for validation
        // ------------------------------------------------------------------

        /// <summary>
        /// Called after deserialization or when the node is first created. Subclasses
        /// can override to clamp parameters or initialize transient state.
        /// </summary>
        public virtual void OnAfterDeserialize() { }

        /// <summary>
        /// Called before the graph window draws the node. Subclasses can override to
        /// validate connections or update cached metadata.
        /// </summary>
        public virtual void OnBeforeSerialize() { }
    }
}
