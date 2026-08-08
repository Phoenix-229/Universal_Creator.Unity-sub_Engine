// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    WeightedAverageNode.cs
//  Purpose: Averages the two inputs with explicit per-input weights, normalised
//           so the result stays in range regardless of the weight magnitudes.
//           Unlike Blend, both branches always contribute in fixed proportion.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class WeightedAverageNode : CombinerNode
    {
        public override string NodeName { get { return "Weighted Average"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip { get { return "Average A and B in fixed proportion."; } }

        public float weightA = 1f;
        public float weightB = 1f;

        protected override Matrix Combine(Matrix a, Matrix b, Matrix mask, TileData tile, StopToken stop)
        {
            Matrix result = new Matrix(a);

            float wa = Mathf.Max(0f, weightA);
            float wb = Mathf.Max(0f, weightB);
            float total = wa + wb;

            if (total < 1e-7f)
            {
                // Both weights zero: fall back to an even split so the node does
                // not silently blank the branch.
                wa = 0.5f;
                wb = 0.5f;
                total = 1f;
            }

            float na = wa / total;
            float nb = wb / total;

            for (int z = 0; z < a.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * a.rect.size.x;
                for (int x = 0; x < a.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float avg = a.arr[i] * na + b.arr[i] * nb;

                    if (mask != null)
                    {
                        result.arr[i] = a.arr[i] + (avg - a.arr[i]) * InterpolationMath.Clamp01(mask.arr[i]);
                    }
                    else
                    {
                        result.arr[i] = avg;
                    }
                }
            }

            return result;
        }
    }
}
