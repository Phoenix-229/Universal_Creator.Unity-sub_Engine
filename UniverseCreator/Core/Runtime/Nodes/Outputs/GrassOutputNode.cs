// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    GrassOutputNode.cs
//  Purpose: Writes grass/detail density from a mask into OutputBag.detailLayers.
//           Distinct from GrassDetailScatterNode: this writes one detail layer
//           from an explicit mask, while scatter nodes generate their own masks
//           procedurally.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class GrassOutputNode : OutputNode
    {
        public override string NodeName { get { return "Grass Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Write a detail layer from a mask."; } }

        /// <summary>Index into the terrain's detail prototype array.</summary>
        public int detailLayerIndex;

        /// <summary>Maximum per-cell density written into the detail grid.</summary>
        public int maxDensityPerCell = 8;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            // Unity's detail resolution is usually higher than heightmap resolution,
            // but for Module 1 we assume 1:1 and scale in Module 3 if needed.
            int[,] detailLayer = new int[h, w];

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;
                for (int x = 0; x < w; x++)
                {
                    float v = InterpolationMath.Clamp01(src.arr[rowStart + x]);
                    detailLayer[z, x] = Mathf.RoundToInt(v * maxDensityPerCell);
                }
            }

            tile.outputs.detailLayers[detailLayerIndex] = detailLayer;
        }
    }
}
