// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    MathOperationsNode.cs
//  Purpose: Per-pixel arithmetic between two fields, or between a field and a
//           constant when only one side is wired. The generic escape hatch for
//           anything the dedicated combiners don't cover.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Utilities
{
    public enum MathOp
    {
        Add = 0,
        Subtract = 1,
        Multiply = 2,
        Divide = 3,
        Modulo = 4,
        PowerOf = 5,
        AbsoluteDifference = 6,
        Minimum = 7,
        Maximum = 8
    }

    [System.Serializable]
    public class MathOperationsNode : Generator
    {
        public override string NodeName { get { return "Math"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip { get { return "Per-pixel arithmetic between two fields or a field and a constant."; } }

        public MathOp operation = MathOp.Add;

        /// <summary>Used in place of B when the B port is unconnected.</summary>
        public float constant = 1f;

        public override Port[] GetInputs() { return SharedPorts.TwoHeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix a = GetInputMatrix(tile, 0, stop);
            Matrix b = GetInputMatrix(tile, 1, stop);

            if (stop != null && stop.StopRequested) return null;

            // Nothing wired at all: the constant broadcast over the tile is more
            // useful than a blank field, and makes the node usable as a source.
            if (a == null && b == null)
            {
                Matrix flat = tile.CreateMatrix();
                flat.Fill(constant);
                return flat;
            }

            // Only B wired: treat A as the constant so operand order is preserved.
            Matrix result;
            if (a == null)
            {
                result = new Matrix(b);
                for (int i = 0; i < result.arr.Length; i++)
                {
                    result.arr[i] = Apply(constant, b.arr[i]);
                }
                return result;
            }

            result = new Matrix(a);

            for (int z = 0; z < a.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return result;

                int rowStart = z * a.rect.size.x;
                for (int x = 0; x < a.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float rhs = b != null ? b.arr[i] : constant;
                    result.arr[i] = Apply(a.arr[i], rhs);
                }
            }

            return result;
        }

        private float Apply(float lhs, float rhs)
        {
            switch (operation)
            {
                case MathOp.Add:      return lhs + rhs;
                case MathOp.Subtract: return lhs - rhs;
                case MathOp.Multiply: return lhs * rhs;

                // Guard the divisors rather than emitting NaN/Inf, which would
                // propagate silently through the rest of the graph.
                case MathOp.Divide:
                    return Mathf.Abs(rhs) < 1e-7f ? 0f : lhs / rhs;

                case MathOp.Modulo:
                    return Mathf.Abs(rhs) < 1e-7f ? 0f : lhs % rhs;

                case MathOp.PowerOf:
                    return Mathf.Pow(Mathf.Max(0f, lhs), rhs);

                case MathOp.AbsoluteDifference: return Mathf.Abs(lhs - rhs);
                case MathOp.Minimum:            return Mathf.Min(lhs, rhs);
                case MathOp.Maximum:            return Mathf.Max(lhs, rhs);
            }
            return lhs;
        }
    }
}
