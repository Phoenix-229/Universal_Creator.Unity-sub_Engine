// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    NoiseMaskSelectorNode.cs
//  Purpose: Generates a noise-based mask procedurally rather than deriving it
//           from the input. The input is read but ignored; the mask comes from
//           a fresh noise field. Useful for injecting random variation into
//           texture/scatter operations that would otherwise be purely geometric.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class NoiseMaskSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Noise Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Generate a noise-based mask independent of the input."; } }

        public int seed = 54321;
        public float size = 100f;
        public int octaves = 4;
        public float detail = 0.5f;
        public NoiseType noiseType = NoiseType.Perlin;
        public float intensity = 1f;
        public float offset;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);
            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 8);

            Vector2 pixelSize = mask.PixelSize;
            float originX = mask.worldPos.x;
            float originZ = mask.worldPos.z;

            for (int z = 0; z < mask.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * pixelSize.y;
                int rowStart = z * mask.rect.size.x;

                for (int x = 0; x < mask.rect.size.x; x++)
                {
                    float wx = originX + x * pixelSize.x;
                    float v = noise.Fractal(wx, wz, safeSize, iterations, detail, 0f, noiseType);
                    mask.arr[rowStart + x] = InterpolationMath.Clamp01(v * intensity + offset);
                }
            }
        }
    }
}
