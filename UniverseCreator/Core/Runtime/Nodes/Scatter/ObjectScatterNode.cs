// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: per-type rules implemented
//  File:    ObjectScatterNode.cs
//  Purpose: General-purpose prefab scatter for props, ruins, and set dressing.
//           The base solver places the instances; this node chooses which
//           prototype each one uses by weighted roulette.
//
//  THREAD SAFETY: prototypes is a list of Unity object references and is NEVER
//  dereferenced here - only Count is read, and only to bound the index. Touching
//  a GameObject from the worker would be a Unity API call off the main thread.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class ObjectScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Object Scatter"; } }
        public override string Tooltip { get { return "General prefab scatter with weighted prototype selection."; } }
        public override string Channel { get { return "objects"; } }

        /// <summary>Prefabs to choose from. Index maps to ScatterInstance.prototypeIndex.</summary>
        public List<GameObject> prototypes = new List<GameObject>();

        /// <summary>Relative selection weight per prototype. Parallel to prototypes.</summary>
        public List<float> prototypeWeights = new List<float>();

        /// <summary>Align the object's up axis to the terrain normal.</summary>
        public bool alignToNormal;

        protected override float NormalAlignment
        {
            get { return alignToNormal ? 1f : normalAlignment; }
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            int count = prototypes != null ? prototypes.Count : 0;

            // Nothing to choose between. Every instance keeps prototypeIndex 0,
            // which is what the base solver already assigned.
            if (count <= 1 || instances.Count == 0) return;

            // Cumulative weight table, built once. A weight list shorter than the
            // prototype list is padded with 1 rather than treated as an error, so
            // adding a prefab without touching the weights behaves sensibly.
            float[] cumulative = new float[count];
            float total = 0f;

            for (int i = 0; i < count; i++)
            {
                float w = 1f;
                if (prototypeWeights != null && i < prototypeWeights.Count)
                    w = Mathf.Max(0f, prototypeWeights[i]);

                total += w;
                cumulative[i] = total;
            }

            // Every weight zeroed would make the roulette meaningless; leaving the
            // instances on prototype 0 is more predictable than dividing by zero.
            if (total <= 1e-6f) return;

            Noise pick = new Noise(seed + 49979687);

            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];

                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);

                float r = pick.Random(hx, hz) * total;

                // Linear scan. Prototype lists are short - a binary search would
                // cost more in branch misprediction than it saves.
                int chosen = count - 1;
                for (int p = 0; p < count; p++)
                {
                    if (r <= cumulative[p]) { chosen = p; break; }
                }

                inst.prototypeIndex = chosen;
                instances[i] = inst;
            }
        }
    }
}
