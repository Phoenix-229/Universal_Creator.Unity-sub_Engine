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
                    float v = src.arr[rowStart + x];

                    // NaN and infinity are tested for by name, not clamped. Clamp01 is
                    // two inequalities and an else, and NaN fails both inequalities, so
                    // a clamp here passed every NaN straight through to SetHeights - the
                    // needle through the terrain. This runs off-thread on a buffer the
                    // graph already owns, so it is the cheapest place to stop one: the
                    // whole terrain path downstream then only ever sees real numbers.
                    //
                    // Zero rather than a neighbour: at this point there is no context to
                    // say what the surface was meant to do, and HeightGuard runs the
                    // neighbourhood repair on the main thread where there is.
                    if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;

                    heightmap[z, x] = v < 0f ? 0f : (v > 1f ? 1f : v);
                }
            }

            tile.outputs.heightmap = heightmap;
            tile.outputs.requestColliderRebuild = true;
        }
    }
}
