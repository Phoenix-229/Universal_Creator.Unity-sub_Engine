// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CombineNode.cs
//  Purpose: N-ary fan-in combine. One input port that accepts ANY number of
//           fields and folds them into a single height by max / min / average /
//           sum - the data-flow answer to blending more than two sources without
//           chaining a tower of two-input Blend / Maximum nodes.
//
//  WHY A SEPARATE NODE FROM Maximum / Minimum / Blend
//  --------------------------------------------------
//  Those are two-input combiners (CombinerNode, TwoHeightMaskIn). Blending five
//  noise layers with them means four nodes wired in series, and the author then
//  reasons about an order that does not matter for a commutative op. This node
//  takes one multi-connection port (SharedPorts.ManyHeightIn) and merges all of
//  it at once, which is what the SE-1 graph fan-in exists to make possible.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class CombineNode : Generator
    {
        public override string NodeName { get { return "Combine (Fan-in)"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip
        {
            get { return "Merge any number of fields into one by max / min / average / sum."; }
        }

        /// <summary>
        /// How the several wired inputs fold into one. Max is the sensible default:
        /// the union of shapes, which is what stacking terrain features usually
        /// wants. Serialized, so a saved graph keeps the author's choice.
        /// </summary>
        public MergeOp op = MergeOp.Max;

        public override Port[] GetInputs() { return SharedPorts.ManyHeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix merged = GetInputMatrixMerged(tile, 0, stop, op);
            if (stop != null && stop.StopRequested) return null;

            // Nothing wired yet: hand back a blank field shaped to the chunk, the
            // same shape an unconnected creator would, so the graph keeps running
            // rather than meeting a null hole mid-pull.
            return merged != null ? merged : tile.CreateMatrix();
        }
    }
}
