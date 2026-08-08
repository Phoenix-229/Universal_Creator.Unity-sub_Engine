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
using UnityEngine;

namespace UniverseCreator.Nodes
{
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
        /// If true, this node is currently disabled and Generate() will not be called.
        /// Disabled nodes return null for all outputs.
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

        /// <summary>Header colour derived from the category.</summary>
        public Color HeaderColor { get { return NodeCategoryColors.Get(Category); } }

        /// <summary>
        /// Called on the main thread immediately before a generation run is
        /// dispatched to a worker. Nodes that read Unity objects (Texture2D pixels,
        /// AnimationCurve, TerrainData) must snapshot them here into plain arrays,
        /// because Generate() runs off-thread where those APIs are illegal.
        /// </summary>
        public virtual void PrepareMainThread() { }

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
