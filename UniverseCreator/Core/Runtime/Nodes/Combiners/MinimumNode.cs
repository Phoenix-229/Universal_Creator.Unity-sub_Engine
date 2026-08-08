// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    MinimumNode.cs
//  Purpose: Per-pixel minimum of two fields. Intersection of two shapes — the
//           lower branch wins, which is how you carve one form out of another.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class MinimumNode : CombinerNode
    {
        public override string NodeName { get { return "Minimum"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip { get { return "Per-pixel min of A and B. Intersection of shapes."; } }

        /// <summary>Smooth-min blend width. 0 gives a hard crease.</summary>
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
                        v = va < vb ? va : vb;
                    }
                    else
                    {
                        float h = Mathf.Clamp01(0.5f + (vb - va) / (2f * k));
                        v = Mathf.Lerp(vb, va, h) - k * h * (1f - h);
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
