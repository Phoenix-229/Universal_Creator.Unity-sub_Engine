// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    NodeCategories.cs
//  Purpose: Thin base classes for the standard node shapes. Step 4 defines ~66
//           nodes; without these each would re-implement identical port wiring,
//           input pulling, copy-on-write and disabled handling.
//
//  All of them deep-copy before mutating: an upstream Matrix may fan out to
//  several consumers, so writing to a pulled input corrupts sibling branches.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>Category tag, used for node colouring and menu grouping.</summary>
    public enum NodeCategory
    {
        Creator = 0,
        Modifier = 1,
        Selector = 2,
        Combiner = 3,
        Scatter = 4,
        Output = 5,
        Utility = 6,
        Biome = 7,

        // Module 2. Appended, never inserted: the numeric value is what a saved
        // graph stores, so renumbering would recolour and re-file every existing
        // node in every graph on disk.
        Texture = 8,
        Surface = 9,
        Water = 10
    }

    /// <summary>Header colours per category, matching the graph editor legend.</summary>
    public static class NodeCategoryColors
    {
        public static Color Get(NodeCategory category)
        {
            switch (category)
            {
                case NodeCategory.Creator:  return new Color(0.24f, 0.52f, 0.78f);  // blue
                case NodeCategory.Modifier: return new Color(0.78f, 0.51f, 0.22f);  // amber
                case NodeCategory.Selector: return new Color(0.42f, 0.66f, 0.31f);  // green
                case NodeCategory.Combiner: return new Color(0.55f, 0.38f, 0.72f);  // violet
                case NodeCategory.Scatter:  return new Color(0.79f, 0.36f, 0.55f);  // pink
                case NodeCategory.Output:   return new Color(0.72f, 0.27f, 0.27f);  // red
                case NodeCategory.Utility:  return new Color(0.40f, 0.44f, 0.48f);  // slate
                case NodeCategory.Biome:    return new Color(0.20f, 0.62f, 0.60f);  // teal
                case NodeCategory.Texture:  return new Color(0.80f, 0.45f, 0.28f);  // rust
                case NodeCategory.Surface:  return new Color(0.30f, 0.58f, 0.80f);  // steel
                case NodeCategory.Water:    return new Color(0.18f, 0.55f, 0.72f);  // deep cyan
                default:                    return new Color(0.35f, 0.35f, 0.38f);
            }
        }

        /// <summary>
        /// True for the categories the Texture Graph window offers. The height
        /// graph and the texture graph share one node catalog, so each window
        /// filters it - otherwise the terrain search window would offer a Snow
        /// Cover node that cannot connect to anything it can see.
        /// </summary>
        public static bool IsTextureGraph(NodeCategory category)
        {
            return category == NodeCategory.Texture
                || category == NodeCategory.Surface
                || category == NodeCategory.Water;
        }
    }

    /// <summary>Zero inputs, one height output. Noise and shape generators.</summary>
    [System.Serializable]
    public abstract class CreatorNode : Generator
    {
        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix m = tile.CreateMatrix();
            Create(m, tile, stop);
            return m;
        }

        protected abstract void Create(Matrix m, TileData tile, StopToken stop);
    }

    /// <summary>One height in, one height out. Erosion, blur, curves, terracing.</summary>
    /// <remarks>
    /// Every modifier is pulled through a ghost margin, because most of them read a
    /// neighbourhood - a blur, an erosion pass, a slope - and at a chunk seam the
    /// neighbours are simply absent. Sixteen pixels is what the widest stock kernel
    /// reaches; see TileWindows for why a margin is the right fix here and not for
    /// drainage.
    /// </remarks>
    [System.Serializable]
    public abstract class ModifierNode : Generator
    {
        /// <summary>Ghost pixels pulled either side of the chunk before modifying.</summary>
        protected const int GhostMargin = 16;

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TileData padded = TileWindows.Expand(tile, GhostMargin);

            // No graph to re-pull through - a node driven directly, as a test does.
            // The tile-local path is still right in the interior; it only loses the
            // seam, and there is no seam when there is only one tile.
            if (padded == null)
            {
                Matrix bare = GetInputMatrixCopy(tile, 0, stop);
                if (stop != null && stop.StopRequested) return null;
                Modify(bare, tile, stop);
                return bare;
            }

            object pulled = padded.PullInput(this, 0, stop);
            if (pulled == null || !(pulled is Matrix)) return tile.CreateMatrix();

            // Copy before modifying: the pulled product may be feeding other branches.
            Matrix paddedSrc = new Matrix((Matrix)pulled);
            if (stop != null && stop.StopRequested) return null;

            Modify(paddedSrc, padded, stop);
            if (stop != null && stop.StopRequested) return null;

            return TileWindows.Crop(paddedSrc, tile, GhostMargin);
        }

        protected abstract void Modify(Matrix m, TileData tile, StopToken stop);
    }

    /// <summary>One height in, one mask out. Slope, height range, curvature selectors.</summary>
    [System.Serializable]
    public abstract class SelectorNode : Generator
    {
        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix mask = src.Blank();
            Select(src, mask, tile, stop);
            return mask;
        }

        // src is read-only here; write results into mask.
        protected abstract void Select(Matrix src, Matrix mask, TileData tile, StopToken stop);
    }

    /// <summary>Two heights plus optional mask in, one height out. Blend and layer nodes.</summary>
    [System.Serializable]
    public abstract class CombinerNode : Generator
    {
        public override Port[] GetInputs() { return SharedPorts.TwoHeightMaskIn; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix a = GetInputMatrix(tile, 0, stop);
            Matrix b = GetInputMatrix(tile, 1, stop);
            Matrix mask = GetInputMatrix(tile, 2, stop);

            if (stop != null && stop.StopRequested) return null;

            // A single connected side passes straight through rather than
            // collapsing the branch to flat.
            if (a == null && b == null) return tile.CreateMatrix();
            if (a == null) return new Matrix(b);
            if (b == null) return new Matrix(a);

            return Combine(a, b, mask, tile, stop);
        }

        protected abstract Matrix Combine(Matrix a, Matrix b, Matrix mask, TileData tile, StopToken stop);
    }

    /// <summary>
    /// Terminal node. Consumes a height/mask and writes into tile.outputs for the
    /// main thread to apply. Produces no downstream product.
    /// </summary>
    [System.Serializable]
    public abstract class OutputNode : Generator, IOutputNode
    {
        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return null;
            if (stop != null && stop.StopRequested) return null;

            WriteOutput(src, tile, stop);
            return null;
        }

        protected abstract void WriteOutput(Matrix src, TileData tile, StopToken stop);
    }
}
