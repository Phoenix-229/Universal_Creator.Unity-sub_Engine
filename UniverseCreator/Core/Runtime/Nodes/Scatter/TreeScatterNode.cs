// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TreeScatterNode.cs
//  Purpose: STUB. Places tree instances via Unity's TreeInstance path.
//           Wiring is final; the placement solver lands in Module 3 (Vegetation).
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class TreeScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Tree Scatter"; } }
        public override string Tooltip { get { return "STUB (Module 3). Places tree instances."; } }
        public override string Channel { get { return "trees"; } }

        /// <summary>Index into the terrain's tree prototype array.</summary>
        public int prototypeIndex;

        /// <summary>Trees closer than this to a road are removed by RoadMaster.</summary>
        public float roadClearance = 4f;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 3]: assign prototypeIndex per instance from a weighted
            // species table, apply roadClearance against the RoadMaster spline set
            // exposed through the IPCGDataProvider bridge, and bias density by
            // moisture from the Biome nodes.
        }
    }
}
