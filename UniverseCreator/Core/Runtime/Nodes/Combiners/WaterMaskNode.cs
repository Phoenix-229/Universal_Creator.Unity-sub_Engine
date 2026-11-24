// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    WaterMaskNode.cs
//  Purpose: N3 of the river suite. ONE reusable "where is water" mask - the union of
//           the river, lake and ocean masks - so a vegetation or texture node can
//           subtract water in a single wire, the way an Unreal PCG graph routes a
//           Difference node to cut water zones out of foliage. Today the water guard
//           lives only inside the scatter solver; this exposes the same fact as a
//           first-class port any node can read.
//
//  WHY A UNION (max), NOT A SUM
//  ----------------------------
//  Each input is already a 0..1 presence mask. A cell that is both near a river mouth
//  AND in the sea is not "twice as wet"; it is simply water. Max keeps the mask a
//  clean 0..1 occupancy and never blows past 1 where two bodies overlap at a mouth.
//
//  Every input is OPTIONAL. An unwired mask contributes 0, so a graph that only has a
//  river still gets a correct water mask from the one wire it bothered to make - a
//  missing input must never turn into a hole that reads as "water everywhere".
//
//  Deterministic, allocation-light, no Unity API beyond the Matrix container.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Combiners
{
    /// <summary>
    /// Unions the river, lake and ocean masks into one Water Mask (0 dry, 1 water) for
    /// any downstream node to exclude or select against. See the file header for why it
    /// is a max of optional inputs.
    /// </summary>
    [System.Serializable]
    public class WaterMaskNode : Generator
    {
        public override string NodeName { get { return "Water Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }

        public override string Tooltip
        {
            get
            {
                return "Combines river, lake and ocean masks into one Water Mask (the union). " +
                       "Wire it into a scatter or texture node to keep growth and cover out " +
                       "of water in a single connection.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("River Mask", PortType.Matrix, PortDirection.Input),
            new Port("Lake Mask",  PortType.Matrix, PortDirection.Input),
            new Port("Ocean Mask", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix river = GetInputMatrix(tile, 0, stop);
            Matrix lake  = GetInputMatrix(tile, 1, stop);
            Matrix ocean = GetInputMatrix(tile, 2, stop);
            if (stop != null && stop.StopRequested) return null;

            // The template for size/placement is the first input that is actually wired.
            Matrix template = river != null ? river : (lake != null ? lake : ocean);
            if (template == null) return tile.CreateMatrix();   // nothing wired: a dry world

            Matrix dst = template.Blank();
            int n = dst.arr.Length;

            for (int i = 0; i < n; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return dst;
                dst.arr[i] = Mathf.Clamp01(Union(river, lake, ocean, i));
            }

            return dst;
        }

        /// <summary>
        /// Union of up to three optional masks at one index: the maximum presence, with a
        /// missing mask contributing nothing. Pure, so the "a missing input reads as dry,
        /// never as water everywhere" rule can be self-tested without the graph.
        /// </summary>
        internal static float Union(Matrix river, Matrix lake, Matrix ocean, int i)
        {
            float v = 0f;
            if (river != null && i < river.arr.Length && river.arr[i] > v) v = river.arr[i];
            if (lake  != null && i < lake.arr.Length  && lake.arr[i]  > v) v = lake.arr[i];
            if (ocean != null && i < ocean.arr.Length && ocean.arr[i] > v) v = ocean.arr[i];
            return v;
        }
    }
}
