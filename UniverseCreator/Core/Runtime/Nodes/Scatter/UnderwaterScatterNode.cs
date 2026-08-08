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

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class UnderwaterScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Underwater Scatter"; } }
        public override string Tooltip { get { return "STUB (Module 4). Places seabed props below water level."; } }
        public override string Channel { get { return "underwater"; } }

        /// <summary>Water plane in normalised height. Candidates above this are rejected.</summary>
        public float waterLevel = 0.3f;

        /// <summary>Minimum submersion depth, in normalised height, below waterLevel.</summary>
        public float minDepth = 0.01f;

        /// <summary>Maximum submersion depth. Deeper seabed is left bare.</summary>
        public float maxDepth = 0.15f;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 4]: reject candidates whose depth below waterLevel falls
            // outside [minDepth, maxDepth], and once the water system exists take
            // waterLevel from the actual water surface rather than this parameter,
            // so lakes at differing elevations each scatter correctly.
        }
    }
}
