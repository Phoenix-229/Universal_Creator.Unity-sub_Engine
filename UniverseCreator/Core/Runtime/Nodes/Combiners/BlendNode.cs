// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    BlendNode.cs
//  Purpose: The general-purpose two-input combine. Eleven blend modes matching
//           standard image-compositing operators, with opacity and an optional
//           mask that gates where the blend applies.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class BlendNode : CombinerNode
    {
        public override string NodeName { get { return "Blend"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip { get { return "Combine two fields with a blend mode and opacity."; } }

        public BlendMode mode = BlendMode.Add;
        public float opacity = 1f;

        protected override Matrix Combine(Matrix a, Matrix b, Matrix mask, TileData tile, StopToken stop)
        {
            float op = Mathf.Clamp01(opacity);

            Matrix result = new Matrix(a);

            for (int z = 0; z < a.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * a.rect.size.x;
                for (int x = 0; x < a.rect.size.x; x++)
                {
                    int i = rowStart + x;

                    float blended = MatrixOperations.BlendValue(a.arr[i], b.arr[i], mode);

                    // Mask gates the blend on top of opacity.
                    float t = op;
                    if (mask != null) t *= InterpolationMath.Clamp01(mask.arr[i]);

                    result.arr[i] = a.arr[i] + (blended - a.arr[i]) * t;
                }
            }

            return result;
        }
    }
}
