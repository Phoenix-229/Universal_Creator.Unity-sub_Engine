// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    MaximumNode.cs
//  Purpose: Per-pixel maximum of two fields. Union of two shapes — whichever
//           branch is higher wins.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class MaximumNode : CombinerNode
    {
        public override string NodeName { get { return "Maximum"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip { get { return "Per-pixel max of A and B. Union of shapes."; } }

        /// <summary>
        /// Smooth-max blend width. 0 gives a hard crease at the intersection,
        /// higher values round it off.
        /// </summary>
        public float smoothness;

        protected override Matrix Combine(Matrix a, Matrix b, Matrix mask, TileData tile, StopToken stop)
        {
            Matrix result = new Matrix(a);
            float k = Mathf.Max(0f, smoothness);

            for (int z = 0; z < a.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * a.rect.size.x;
                for (int x = 0; x < a.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float va = a.arr[i];
                    float vb = b.arr[i];

                    float v;
                    if (k < 1e-5f)
                    {
                        v = va > vb ? va : vb;
                    }
                    else
                    {
                        // Polynomial smooth-max: blends the crease over width k.
                        float h = Mathf.Clamp01(0.5f + (vb - va) / (2f * k));
                        v = Mathf.Lerp(va, vb, h) + k * h * (1f - h);
                    }

                    if (mask != null)
                    {
                        result.arr[i] = va + (v - va) * InterpolationMath.Clamp01(mask.arr[i]);
                    }
                    else
                    {
                        result.arr[i] = v;
                    }
                }
            }

            return result;
        }
    }
}
