// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    DebrisScatterNode.cs
//  Purpose: STUB. Small-scale surface litter — pebbles, twigs, bone, rubble.
//           Wiring is final; the solver lands in Module 2.
//
//  Debris differs from rocks mainly in scale and in that it clusters rather than
//  distributing evenly, so it carries a clumping parameter the others don't.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class DebrisScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Debris Scatter"; } }
        public override string Tooltip { get { return "STUB (Module 2). Clustered surface litter."; } }
        public override string Channel { get { return "debris"; } }

        /// <summary>0 spreads evenly, 1 concentrates into tight clumps.</summary>
        public float clumping = 0.5f;

        /// <summary>Average clump radius in world metres.</summary>
        public float clumpRadius = 3f;

        /// <summary>Bias placement toward hollows where debris naturally collects.</summary>
        public bool preferCavities = true;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 2]: replace uniform Poisson sampling with a two-level
            // process — sample clump centres at clumpRadius spacing, then sample
            // members around each centre with a falloff set by clumping. When
            // preferCavities, weight clump centres by MatrixOperations.Cavity.
        }
    }
}
