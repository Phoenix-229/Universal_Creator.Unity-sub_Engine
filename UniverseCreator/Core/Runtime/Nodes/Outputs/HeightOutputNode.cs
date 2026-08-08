// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HeightOutputNode.cs
//  Purpose: Writes the height matrix into OutputBag.heightmap for Unity's
//           TerrainData.SetHeights. The canonical output node every working
//           graph must have.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class HeightOutputNode : OutputNode
    {
        public override string NodeName { get { return "Height Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Write the height matrix to TerrainData."; } }

        public float heightScale = 600f;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            // Unity's SetHeights expects [z, x] with z running north.
            float[,] heightmap = new float[h, w];

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;
                for (int x = 0; x < w; x++)
                {
                    // FINAL SAFETY NET: Strict [0.0, 1.0] mathematical wall.
                    // This explicitly prevents any Node overflows from causing Terrain spikes.
                    heightmap[z, x] = Mathf.Clamp01(src.arr[rowStart + x]);
                }
            }

            tile.outputs.heightmap = heightmap;
            tile.outputs.requestColliderRebuild = true;
        }
    }
}
