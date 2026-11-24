// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TextureNodeBase.cs
//  Purpose: Base classes for the texture graph, mirroring what NodeCategories.cs
//           does for the height graph.
//
//  WHY A SEPARATE FAMILY
//  ---------------------
//  The height graph's bases all assume the product is a Matrix: CreatorNode
//  allocates one, ModifierNode pads one with ghost margins, CombinerNode blends
//  two. None of that means anything for a material description. Reusing them
//  would mean every texture node overriding Generate() and ignoring the base's
//  entire body, which is inheritance that pays nothing.
//
//  What IS shared is everything below Generate(): Generator's id, position,
//  disabled flag, serialization callbacks, and the whole pull/cache machinery in
//  GraphEvaluator and TileData. Those work on `object`, so they carry a
//  SurfaceProduct exactly as happily as a Matrix. That is why the texture graph
//  needed no change to Module 1's evaluator - only new bases and new products.
//
//  THREADING
//  ---------
//  These run on the MAIN thread. They hold Texture2D references, and the terminal
//  node writes assets. The typed pull helpers below therefore make no attempt at
//  the ReferenceEquals-instead-of-== dance the height nodes need; here `== null`
//  on a UnityEngine.Object is both legal and correct.
//
//  The exception is a node that bakes a real per-pixel map - occlusion, noise.
//  Those do their pixel work through Matrix and float[] like any height node, and
//  hand the result over as a baked map for the output node to upload.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Common base for every texture-graph node. Adds typed pull helpers over
    /// Generator's untyped PullInput, so a node body reads as intent rather than
    /// as a cast.
    /// </summary>
    [System.Serializable]
    public abstract class TextureNode : Generator
    {
        public override NodeCategory Category { get { return NodeCategory.Texture; } }

        /// <summary>UC-T07-10: native texture/material authoring defaults to the main thread; immutable CPU image operations explicitly declare Worker.</summary>
        /// <remarks>GPU submission requires an explicit MainThreadGpu override and supported fallback/resource contracts.</remarks>
        public override NodeExecutionDomain ExecutionDomain { get { return NodeExecutionDomain.MainThread; } }

        // ------------------------------------------------------------------
        //  Typed pulls
        //
        //  Each returns null when the port is unconnected, which every caller
        //  treats as "use my own inspector values". That is deliberate: a texture
        //  node with nothing wired in must still produce something sensible, or a
        //  half-built graph shows nothing and the user cannot tell whether the
        //  node is broken or merely unconnected.
        // ------------------------------------------------------------------

        protected SurfaceProduct GetInputSurface(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as SurfaceProduct;
        }

        protected LayerProduct GetInputLayer(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as LayerProduct;
        }

        protected SplatProduct GetInputSplat(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as SplatProduct;
        }

        protected GradientProduct GetInputGradient(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as GradientProduct;
        }

        protected TextureRef GetInputTexture(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as TextureRef;
        }

        /// <summary>UC-T07-02: borrows immutable CPU image pixels without dereferencing a Unity object.</summary>
        /// <param name="tile">Bound evaluation tile.</param>
        /// <param name="index">Zero-based image input port.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Image input or null for missing/incompatible input; no image allocation occurs.</returns>
        protected TextureImageProduct GetInputImage(TileData tile, int index, StopToken stop)
        { return tile.PullInput(this, index, stop) as TextureImageProduct; }

        protected UVTransform GetInputUV(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as UVTransform;
        }

        protected WaterProduct GetInputWater(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as WaterProduct;
        }

        /// <summary>
        /// Colour ports carry a boxed Color. Boxing a 16-byte struct once per node
        /// per evaluation is nothing next to the dictionary the cache already
        /// allocates, and it keeps the product cache a single Dictionary&lt;string,
        /// object&gt; instead of one per currency.
        /// </summary>
        protected bool TryGetInputColor(TileData tile, int index, StopToken stop, out Color value)
        {
            object o = tile.PullInput(this, index, stop);
            if (o is Color) { value = (Color)o; return true; }
            value = Color.white;
            return false;
        }

        protected Color GetInputColorOr(TileData tile, int index, StopToken stop, Color fallback)
        {
            Color c;
            return TryGetInputColor(tile, index, stop, out c) ? c : fallback;
        }

        protected bool TryGetInputScalar(TileData tile, int index, StopToken stop, out float value)
        {
            object o = tile.PullInput(this, index, stop);
            if (o is float) { value = (float)o; return true; }
            value = 0f;
            return false;
        }

        protected float GetInputScalarOr(TileData tile, int index, StopToken stop, float fallback)
        {
            float f;
            return TryGetInputScalar(tile, index, stop, out f) ? f : fallback;
        }

        /// <summary>
        /// Pulls a Matrix from a texture-graph port. Used by the handful of nodes
        /// that consume real per-pixel data - the occlusion baker reading the
        /// heightmap is the whole of it today.
        /// </summary>
        protected Matrix GetInputMap(TileData tile, int index, StopToken stop)
        {
            return tile.PullInput(this, index, stop) as Matrix;
        }
    }

    /// <summary>
    /// The dominant shape in this graph: take the surface built so far, add one
    /// feature, hand it on.
    ///
    /// Copy-then-mutate, never mutate-in-place. A graph is a graph, and nothing
    /// stops a user fanning one surface into two chains - snow up one, wetness up
    /// the other. Mutating the shared instance would silently put snow on both.
    ///
    /// An unconnected Surface input starts a fresh one rather than failing, so a
    /// single feature node dropped on an empty canvas already produces a valid
    /// material.
    /// </summary>
    [System.Serializable]
    public abstract class SurfaceFeatureNode : TextureNode
    {
        public override NodeCategory Category { get { return NodeCategory.Surface; } }

        public override Port[] GetInputs() { return TexturePorts.SurfaceChainIn; }
        public override Port[] GetOutputs() { return TexturePorts.SurfaceOut; }

        /// <summary>UC-T07-10: builds a copied material description after admitting the node's declared execution thread.</summary>
        /// <param name="outputIndex">The sole surface output, zero.</param>
        /// <param name="tile">Bound evaluation tile with typed upstream products and resource ownership.</param>
        /// <param name="stop">Optional cooperative cancellation token.</param>
        /// <returns>A fresh surface description, or null when cancelled.</returns>
        /// <exception cref="System.InvalidOperationException">The calling thread cannot satisfy the declared native/CPU execution domain.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.Require(this, ExecutionDomain, HasCpuFallback); // UC-T07-10: direct surface calls preserve the declared native/CPU boundary.
            SurfaceProduct upstream = GetInputSurface(tile, 0, stop);
            SurfaceProduct surface = upstream != null ? upstream.Copy() : new SurfaceProduct();

            if (stop != null && stop.StopRequested) return null;

            Apply(surface, tile, stop);
            return surface;
        }

        /// <summary>Write this node's contribution into the surface.</summary>
        protected abstract void Apply(SurfaceProduct surface, TileData tile, StopToken stop);
    }

    /// <summary>
    /// A node producing one material layer. Zero or more inputs, one Layer out.
    /// </summary>
    [System.Serializable]
    public abstract class LayerNode : TextureNode
    {
        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return TexturePorts.LayerOut; }

        /// <summary>UC-T07-10: builds a layer after admitting the native/CPU execution domain, preserving borrowed resource ownership.</summary>
        /// <param name="outputIndex">The sole layer output, zero.</param>
        /// <param name="tile">Bound evaluation tile with upstream layer/texture inputs.</param>
        /// <param name="stop">Optional cooperative cancellation token.</param>
        /// <returns>A layer description, or null when cancelled.</returns>
        /// <exception cref="System.InvalidOperationException">The calling thread cannot satisfy the declared execution domain.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.Require(this, ExecutionDomain, HasCpuFallback); // UC-T07-10: reject unsupported threads before native layer fields are examined.
            if (stop != null && stop.StopRequested) return null;
            return BuildLayer(tile, stop);
        }

        protected abstract LayerProduct BuildLayer(TileData tile, StopToken stop);
    }

    /// <summary>
    /// Terminal node of the texture graph. Consumes a Surface and records what to
    /// apply into the tile's output bag; produces nothing downstream.
    ///
    /// Tagged IOutputNode so GraphEvaluator starts its pull here, exactly as it
    /// does for the height graph's terrain outputs.
    /// </summary>
    [System.Serializable]
    public abstract class SurfaceOutputNode : TextureNode, IOutputNode
    {
        public override NodeCategory Category { get { return NodeCategory.Surface; } }

        public override Port[] GetInputs() { return TexturePorts.SurfaceIn; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        /// <summary>UC-T07-10: records an admitted surface output for later explicit Apply without mutating a renderer.</summary>
        /// <param name="outputIndex">Terminal pull slot, zero.</param>
        /// <param name="tile">Bound evaluation tile whose output bag receives the material description.</param>
        /// <param name="stop">Optional cooperative cancellation token.</param>
        /// <returns>Null; absent/cancelled inputs publish no output.</returns>
        /// <exception cref="System.InvalidOperationException">The calling thread cannot satisfy the declared execution domain.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.Require(this, ExecutionDomain, HasCpuFallback); // UC-T07-10: output consumers keep the same execution guard as evaluator calls.
            SurfaceProduct surface = GetInputSurface(tile, 0, stop);
            if (surface == null) return null;
            if (stop != null && stop.StopRequested) return null;

            WriteSurface(surface, tile, stop);
            return null;
        }

        protected abstract void WriteSurface(SurfaceProduct surface, TileData tile, StopToken stop);
    }
}
