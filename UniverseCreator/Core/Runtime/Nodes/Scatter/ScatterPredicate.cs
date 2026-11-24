// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    ScatterPredicate.cs   (VEG-9)
//  Purpose: A composable filter stack for scatter placement - several typed
//           predicates on the per-candidate quantities (height, slope, mask,
//           noise), combined by AND or OR. The data-flow answer to "scatter only
//           where it is high AND steep", or "near water OR on a south face",
//           instead of the single height-band + slope-band the solver ships with.
//
//  Pure data + a pure evaluator so the whole thing is deterministic and directly
//  self-testable; ScatterNodeBase holds the list and feeds it the resolved
//  candidate values. Default-empty, so a node with no stack placed is unchanged.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>Which per-candidate quantity a predicate tests.</summary>
    public enum ScatterField { Height = 0, SlopeDeg = 1, Mask = 2, Noise = 3 }

    /// <summary>How a predicate compares a value against its bounds.</summary>
    public enum PredicateOp { Inside = 0, Outside = 1, Greater = 2, Less = 3 }

    /// <summary>How a stack of predicates combines.</summary>
    public enum FilterGroupOp { And = 0, Or = 1 }

    /// <summary>One typed test on a scatter candidate's resolved value.</summary>
    [System.Serializable]
    public struct ScatterPredicate
    {
        public ScatterField field;
        public PredicateOp op;

        /// <summary>Low bound (Inside/Outside), or the threshold (Greater/Less).</summary>
        public float a;

        /// <summary>High bound (Inside/Outside); ignored by Greater/Less.</summary>
        public float b;

        public bool Evaluate(float value)
        {
            switch (op)
            {
                case PredicateOp.Inside:  return value >= a && value <= b;
                case PredicateOp.Outside: return value < a || value > b;
                case PredicateOp.Greater: return value > a;
                case PredicateOp.Less:    return value < a;
                default:                  return true;
            }
        }
    }

    /// <summary>Evaluates a composable predicate stack over one candidate's values.</summary>
    public static class ScatterFilterStack
    {
        /// <summary>
        /// True when the candidate passes the stack. An empty stack keeps everything. AND requires
        /// every predicate; OR requires any. The caller supplies the resolved per-candidate values.
        /// </summary>
        public static bool Keep(System.Collections.Generic.List<ScatterPredicate> stack,
                                FilterGroupOp groupOp,
                                float height01, float slopeDeg, float mask01, float noise01)
        {
            if (stack == null || stack.Count == 0) return true;

            bool andAcc = true, orAcc = false;
            for (int i = 0; i < stack.Count; i++)
            {
                ScatterPredicate p = stack[i];
                float v = p.field == ScatterField.Height ? height01
                        : p.field == ScatterField.SlopeDeg ? slopeDeg
                        : p.field == ScatterField.Mask ? mask01 : noise01;

                bool r = p.Evaluate(v);
                andAcc = andAcc && r;
                orAcc = orAcc || r;
            }
            return groupOp == FilterGroupOp.Or ? orAcc : andAcc;
        }
    }
}
