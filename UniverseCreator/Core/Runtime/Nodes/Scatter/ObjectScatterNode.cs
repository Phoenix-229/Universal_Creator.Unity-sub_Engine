// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ObjectScatterNode.cs
//  Purpose: STUB. General-purpose prefab scatter for props, ruins, and set
//           dressing. Wiring is final; the solver lands in Module 2.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class ObjectScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Object Scatter"; } }
        public override string Tooltip { get { return "STUB (Module 2). General prefab scatter."; } }
        public override string Channel { get { return "objects"; } }

        /// <summary>Prefabs to choose from. Index maps to ScatterInstance.prototypeIndex.</summary>
        public List<GameObject> prototypes = new List<GameObject>();

        /// <summary>Relative selection weight per prototype. Parallel to prototypes.</summary>
        public List<float> prototypeWeights = new List<float>();

        /// <summary>Align the object's up axis to the terrain normal.</summary>
        public bool alignToNormal;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 2]: pick a prototype per candidate by weighted roulette
            // over prototypeWeights, and record the chosen index in
            // ScatterInstance.prototypeIndex. The prefab list itself is a Unity
            // object reference, so any inspection of it must happen in
            // PrepareMainThread rather than here.
        }
    }
}
