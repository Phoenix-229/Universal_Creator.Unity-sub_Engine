// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: shared placement solver active
//  File:    TreeScatterNode.cs
//  Purpose: Places tree instances via Unity's TreeInstance path. Placement,
//           spacing, and height/slope rejection come from the base solver;
//           species selection and road clearance land in Module 3.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class TreeScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Tree Scatter"; } }
        public override string Tooltip { get { return "Places tree instances. Species tables land in Module 3."; } }
        public override string Channel { get { return "trees"; } }

        /// <summary>Index into the terrain's tree prototype array.</summary>
        public int prototypeIndex;

        /// <summary>Trees closer than this to a road are removed by RoadMaster.</summary>
        public float roadClearance = 4f;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // Trees stay upright regardless of slope, so the base solver's normal
            // alignment is deliberately left at zero for this type.
            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];
                inst.prototypeIndex = prototypeIndex;
                instances[i] = inst;
            }

            // TODO [Module 3]: replace the single prototypeIndex with a weighted
            // species table, apply roadClearance against the RoadMaster spline set
            // exposed through the IPCGDataProvider bridge, and bias density by
            // moisture from the Biome nodes.
        }
    }
}
