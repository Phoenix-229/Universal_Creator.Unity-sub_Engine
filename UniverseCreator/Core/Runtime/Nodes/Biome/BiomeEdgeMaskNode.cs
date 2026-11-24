// -----------------------------------------------------------------------------
//  Universe Creator - Biomes
//  File:    BiomeEdgeMaskNode.cs   (BIO-3)
//  Purpose: A 0-1 mask that lights up along BIOME CLASS BOUNDARIES, so transition
//           content - scrub at a forest margin, reeds at a lake edge - can be
//           scattered or splatted in the band where one biome meets another.
//
//  It reads the Biome index field the classifier bakes and, per pixel, measures
//  how much of a small window around it belongs to a DIFFERENT biome. Deep inside
//  a biome that fraction is zero; straddling a boundary it rises toward one. This
//  is the spatial companion to BIO-1's per-pixel climate blend: BIO-1 says "this
//  climate is near a threshold", this says "this GROUND is near a class edge".
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    [System.Serializable]
    public class BiomeEdgeMaskNode : SelectorNode
    {
        public override string NodeName { get { return "Biome Edge Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip
        {
            get { return "A 0-1 mask along biome class boundaries, for transition content."; }
        }

        [Tooltip("Half-width of the edge band in pixels. Larger lights up a wider transition band.")]
        public int edgeWidthPixels = 2;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            ComputeBiomeEdge(src, mask, edgeWidthPixels, stop);
        }

        /// <summary>
        /// Fills <paramref name="mask"/> with, per pixel, the fraction of a (2R+1)^2 window whose
        /// decoded biome differs from the centre's - 0 inside a biome, rising at a boundary. Static
        /// and side-effect-free (bar writing the mask) so the edge detection is directly testable.
        /// </summary>
        internal static void ComputeBiomeEdge(Matrix src, Matrix mask, int radius, StopToken stop)
        {
            if (src == null || mask == null || src.arr.Length != mask.arr.Length) return;

            int sx = src.rect.size.x, sz = src.rect.size.z;
            int R = Mathf.Max(1, radius);

            for (int z = 0; z < sz; z++)
            {
                if (stop != null && stop.StopRequested) return;
                int row = z * sx;
                for (int x = 0; x < sx; x++)
                {
                    int center = Ecology.WhittakerBiome.Decode(src.arr[row + x]);
                    int diff = 0, total = 0;

                    for (int dz = -R; dz <= R; dz++)
                    {
                        int nz = Mathf.Clamp(z + dz, 0, sz - 1);
                        int nrow = nz * sx;
                        for (int dx = -R; dx <= R; dx++)
                        {
                            int nx = Mathf.Clamp(x + dx, 0, sx - 1);
                            total++;
                            if (Ecology.WhittakerBiome.Decode(src.arr[nrow + nx]) != center) diff++;
                        }
                    }

                    mask.arr[row + x] = total > 0 ? (float)diff / total : 0f;
                }
            }
        }
    }
}
