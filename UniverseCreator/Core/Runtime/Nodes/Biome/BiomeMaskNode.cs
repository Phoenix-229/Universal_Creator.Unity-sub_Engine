// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    BiomeMaskNode.cs
//  Purpose: Decodes a biome index field from BiomeClassifierNode and outputs a
//           mask marking one biome. Feeds texture, scatter, and blend nodes that
//           should only act inside that biome.
//
//  Classification is a hard decision, so a raw mask has stair-step edges. The
//  blur radius softens the boundary, which is what you want for texture
//  transitions where two biomes meet.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    [System.Serializable]
    public class BiomeMaskNode : SelectorNode
    {
        public override string NodeName { get { return "Biome Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Mask marking one biome from a classified field."; } }

        public BiomeType biome = BiomeType.TemperateForest;

        /// <summary>Blur radius in pixels applied to the mask. 0 leaves hard edges.</summary>
        public int softenRadius = 2;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            // Decode: the classifier encoded index / (count - 1).
            int target = (int)biome;
            float step = 1f / (BiomeClassifierNode.BiomeCount - 1);
            float halfStep = step * 0.5f;
            float targetValue = target * step;

            for (int z = 0; z < src.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * src.rect.size.x;
                for (int x = 0; x < src.rect.size.x; x++)
                {
                    int i = rowStart + x;

                    // Match within half a step, so float round-trip error through
                    // the Matrix pipeline does not drop pixels.
                    mask.arr[i] = Mathf.Abs(src.arr[i] - targetValue) < halfStep ? 1f : 0f;
                }
            }

            if (softenRadius > 0)
            {
                if (stop != null && stop.StopRequested) return;

                // sigma = radius / 3 matches the kernel width the other blur nodes use.
                Matrix softened = MatrixOperations.GaussianBlur(mask, softenRadius / 3f);
                System.Array.Copy(softened.arr, mask.arr, mask.arr.Length);
            }
        }
    }
}
