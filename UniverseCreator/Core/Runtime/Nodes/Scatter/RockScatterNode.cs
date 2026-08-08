// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    RockScatterNode.cs
//  Purpose: STUB. Places rock/boulder prefabs, biased toward steep and exposed
//           ground. Wiring is final; the solver lands in Module 2 (Object Scatter).
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class RockScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Rock Scatter"; } }
        public override string Tooltip { get { return "STUB (Module 2). Places boulders on steep ground."; } }
        public override string Channel { get { return "rocks"; } }

        /// <summary>Sink each rock this far into the surface so it reads as embedded.</summary>
        public float embedDepth = 0.2f;

        /// <summary>Random tilt away from surface normal, in degrees.</summary>
        public float randomTilt = 15f;

        /// <summary>Align the rock's up axis to the terrain normal before tilting.</summary>
        public bool alignToNormal = true;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 2]: derive the surface normal per accepted candidate via
            // MatrixOperations.SobelNormals, build the rotation from it when
            // alignToNormal, apply randomTilt, and offset position by -embedDepth
            // along that normal.
        }
    }
}
