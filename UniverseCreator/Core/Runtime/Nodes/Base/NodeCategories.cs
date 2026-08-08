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
        Biome = 7
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
                default:                    return new Color(0.35f, 0.35f, 0.38f);
            }
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
    [System.Serializable]
    public abstract class ModifierNode : Generator
    {
        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            int margin = 16;
            int paddedResX = tile.rect.size.x + margin * 2;
            int paddedResZ = tile.rect.size.z + margin * 2;
            
            float scaleX = tile.rect.size.x > 1 ? tile.worldSize.x / (tile.rect.size.x - 1) : tile.worldSize.x;
            float scaleZ = tile.rect.size.z > 1 ? tile.worldSize.z / (tile.rect.size.z - 1) : tile.worldSize.z;

            CoordRect paddedRect = new CoordRect(
                new Coord(tile.rect.offset.x - margin, tile.rect.offset.z - margin),
                new Coord(paddedResX, paddedResZ)
            );

            Vector3 paddedPos = new Vector3(
                tile.worldPos.x - scaleX * margin,
                tile.worldPos.y,
                tile.worldPos.z - scaleZ * margin
            );
            Vector3 paddedSize = new Vector3(
                tile.worldSize.x + scaleX * margin * 2,
                tile.worldSize.y,
                tile.worldSize.z + scaleZ * margin * 2
            );

            TileData paddedTile = new TileData(
                tile.chunkCoord, paddedRect, paddedPos, paddedSize, tile.worldHeight
            );
            paddedTile.SetFullWorld(tile.fullWorldPos, tile.fullWorldSize);
            
            GraphEvaluator ev = new GraphEvaluator(tile.Graph);
            paddedTile.Bind(ev);

            object pulled = ev.PullInput(this, 0, paddedTile, stop);
            if (pulled == null || !(pulled is Matrix)) return tile.CreateMatrix();
            Matrix paddedSrc = new Matrix((Matrix)pulled);
            
            if (stop != null && stop.StopRequested) return null;

            // Run modifier on the padded matrix
            Modify(paddedSrc, paddedTile, stop);
            
            // Extract the center array back out to discard ghost margins
            Matrix finalCenter = tile.CreateMatrix();
            for (int z = 0; z < finalCenter.rect.size.z; z++)
            {
                int pz = z + margin;
                int centerRow = z * finalCenter.rect.size.x;
                int paddedRow = pz * paddedSrc.rect.size.x;
                
                for (int x = 0; x < finalCenter.rect.size.x; x++)
                {
                    finalCenter.arr[centerRow + x] = paddedSrc.arr[paddedRow + x + margin];
                }
            }
            
            return finalCenter;
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
