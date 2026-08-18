// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UnderwaterScatterNode.cs
//  Purpose: STUB. Places kelp, coral, and seabed props below a water plane.
//           Wiring is final; the solver lands in Module 4 (Water).
//
//  The water level is a parameter here rather than a query, because Module 1 has
//  no water system to ask. Module 4 will replace it with a read from the water
//  surface through the PCG bridge.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class UnderwaterScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Underwater Scatter"; } }
        public override string Tooltip { get { return "Places seabed props within a depth band below water level."; } }
        public override string Channel { get { return "underwater"; } }

        /// <summary>Water plane in normalised height. Candidates above this are rejected.</summary>
        public float waterLevel = 0.3f;

        /// <summary>Minimum submersion depth, in normalised height, below waterLevel.</summary>
        public float minDepth = 0.01f;

        /// <summary>Maximum submersion depth. Deeper seabed is left bare.</summary>
        public float maxDepth = 0.15f;

        protected override List<ScatterCandidate> SolvePlacement(
            Matrix height, Matrix mask, TileData tile, StopToken stop)
        {
            // Depth below the water plane IS a height band, just expressed from the
            // surface downward: depth d means height (waterLevel - d). Mapping it
            // onto the inherited band lets the existing filter do the rejection
            // rather than adding a second pass that removes instances after they
            // have already been built.
            float lo = Mathf.Min(minDepth, maxDepth);
            float hi = Mathf.Max(minDepth, maxDepth);

            minHeight = Mathf.Clamp01(waterLevel - hi);
            maxHeight = Mathf.Clamp01(waterLevel - lo);

            // A band inverted by bad parameters would reject everything silently.
            if (maxHeight < minHeight) maxHeight = minHeight;

            return base.SolvePlacement(height, mask, tile, stop);
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 4]: once the water system exists, take waterLevel from
            // the actual water surface rather than this parameter, so lakes sitting
            // at differing elevations each scatter against their own plane instead
            // of one global level.
        }
    }
}
