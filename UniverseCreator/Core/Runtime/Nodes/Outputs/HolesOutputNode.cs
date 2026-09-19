// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HolesOutputNode.cs
//  Purpose: Writes a boolean holes mask into OutputBag.holes for TerrainData.
//           Unity renders the terrain surface only where the mask is TRUE, which is
//           how you cut tunnel mouths, cave entrances and bridge footings.
//
//  UNITY SEMANTICS
//  ---------------
//  TerrainData.SetHoles takes true = surface, false = hole. That is the inverse of
//  how a "hole mask" reads, so the incoming mask is inverted here: a mask value
//  ABOVE the threshold means "cut a hole", which is written as false.
//
//  RESOLUTION
//  ----------
//  The holes map is per-quad, not per-vertex, so it is one smaller than the
//  heightmap on each axis: a 257-sample heightmap has a 256x256 holes map. Writing
//  a res x res array here would throw at SetHoles.
//
//  TUNNEL WALLS
//  ------------
//  A terrain hole removes surface; it does not create wall geometry. The tunnel
//  shell that closes the opening is supplied by the road system (Module 6) through
//  RoadCityMasterLinker, which reads this same mask so its mesh lands exactly on
//  the cut edge. Module 1's job is to make the cut authoritative and reproducible.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class HolesOutputNode : OutputNode
    {
        public override string NodeName { get { return "Holes Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Cut holes in the terrain mesh from a mask."; } }

        /// <summary>Mask values above this threshold cut a hole.</summary>
        public float threshold = 0.5f;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            // Per-quad map: one smaller than the heightmap on each axis.
            int hw = Mathf.Max(1, w - 1);
            int hh = Mathf.Max(1, h - 1);

            bool[,] holes = new bool[hh, hw];

            for (int z = 0; z < hh; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;
                for (int x = 0; x < hw; x++)
                {
                    // A quad is cut only if the mask clears the threshold. Inverted
                    // on write because Unity stores true = surface.
                    bool cut = src.arr[rowStart + x] > threshold;
                    holes[z, x] = !cut;
                }
            }

            tile.outputs.holes = holes;
            tile.outputs.requestColliderRebuild = true;
        }
    }
}
