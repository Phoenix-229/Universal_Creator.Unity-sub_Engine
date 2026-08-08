// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PreviewNode.cs
//  Purpose: Writes a 1D flattened copy of the input matrix into OutputBag.previews
//           for the Node Graph Editor to render as a live thumbnail beside the node.
//           Pure editor convenience: no effect on the result terrain.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Utilities
{
    [System.Serializable]
    public class PreviewNode : Generator
    {
        public override string NodeName { get { return "Preview"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "Live thumbnail in the editor. Does not affect the result."; } }

        /// <summary>Preview key, displayed as a label. Multiple previews per graph allowed.</summary>
        public string label = "Preview";

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            // Pass through the input unchanged, but also write a preview snapshot.
            float[] preview = new float[src.arr.Length];
            System.Array.Copy(src.arr, preview, src.arr.Length);

            tile.outputs.previews[label] = preview;

            return new Matrix(src);
        }
    }
}
