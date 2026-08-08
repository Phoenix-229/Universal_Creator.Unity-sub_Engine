// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    MaskMixNode.cs
//  Purpose: Straight mask-driven interpolation. Mask 0 reads A, mask 1 reads B.
//           The workhorse for biome transitions and any "use this here, that there"
//           decision.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class MaskMixNode : CombinerNode
    {
        public override string NodeName { get { return "Mask Mix"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip { get { return "Interpolate A to B by the mask. 0 = A, 1 = B."; } }

        /// <summary>Read the mask through a smoothstep, softening hard mask edges.</summary>
        public bool smoothMask;

        protected override Matrix Combine(Matrix a, Matrix b, Matrix mask, TileData tile, StopToken stop)
        {
            Matrix result = new Matrix(a);

            // No mask means an even split, which is more useful than silently
            // returning A and making the B branch look disconnected.
            if (mask == null)
            {
                for (int i = 0; i < result.arr.Length; i++)
                {
                    result.arr[i] = (a.arr[i] + b.arr[i]) * 0.5f;
                }
                return result;
            }

            for (int z = 0; z < a.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * a.rect.size.x;
                for (int x = 0; x < a.rect.size.x; x++)
                {
                    int i = rowStart + x;

                    float t = InterpolationMath.Clamp01(mask.arr[i]);
                    if (smoothMask) t = InterpolationMath.SmoothStep(t);

                    result.arr[i] = a.arr[i] + (b.arr[i] - a.arr[i]) * t;
                }
            }

            return result;
        }
    }
}
