// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TextureOutputNode.cs
//  Purpose: Writes a splatmap into OutputBag.splatmap for Unity's
//           TerrainData.SetAlphamaps. Maps up to four masks (Height + three
//           optional Mask inputs) onto texture layers.
//
//  Unity's splatmap stores weights, not indices, and normalises them so the sum
//  at each pixel is 1. The texture blend is done by the terrain shader, not
//  by this node.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class TextureOutputNode : OutputNode
    {
        public override string NodeName { get { return "Texture Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Write a four-layer splatmap from masks."; } }

        // Cached: GetInputs is called every editor repaint, so it must not allocate.
        private static readonly Port[] SplatInputs =
        {
            new Port("Layer 0", PortType.Matrix, PortDirection.Input),
            new Port("Layer 1", PortType.Matrix, PortDirection.Input),
            new Port("Layer 2", PortType.Matrix, PortDirection.Input),
            new Port("Layer 3", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return SplatInputs; }

        /// <summary>Unity terrain layer indices these masks write to.</summary>
        public int layerIndex0;
        public int layerIndex1 = 1;
        public int layerIndex2 = 2;
        public int layerIndex3 = 3;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            Matrix mask1 = GetInputMatrix(tile, 1, stop);
            Matrix mask2 = GetInputMatrix(tile, 2, stop);
            Matrix mask3 = GetInputMatrix(tile, 3, stop);

            int w = src.rect.size.x;
            int h = src.rect.size.z;

            // Unity's splatmap is [z, x, layer]. We write all four layers, even if
            // some inputs are disconnected (they default to zero weight).
            float[,,] splatmap = new float[h, w, 4];

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = rowStart + x;

                    float w0 = InterpolationMath.Clamp01(src.arr[i]);
                    float w1 = mask1 != null ? InterpolationMath.Clamp01(mask1.arr[i]) : 0f;
                    float w2 = mask2 != null ? InterpolationMath.Clamp01(mask2.arr[i]) : 0f;
                    float w3 = mask3 != null ? InterpolationMath.Clamp01(mask3.arr[i]) : 0f;

                    float sum = w0 + w1 + w2 + w3;
                    if (sum < 1e-6f)
                    {
                        // All weights zero: default to layer 0 fully opaque so the
                        // terrain does not disappear.
                        w0 = 1f;
                        sum = 1f;
                    }

                    float invSum = 1f / sum;
                    splatmap[z, x, 0] = w0 * invSum;
                    splatmap[z, x, 1] = w1 * invSum;
                    splatmap[z, x, 2] = w2 * invSum;
                    splatmap[z, x, 3] = w3 * invSum;
                }
            }

            tile.outputs.splatmap = splatmap;
        }
    }
}
