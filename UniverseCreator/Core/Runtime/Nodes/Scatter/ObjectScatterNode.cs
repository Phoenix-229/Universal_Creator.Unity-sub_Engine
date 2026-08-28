// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: per-type rules implemented
//                     Module 3: species selection active, geomorphology active (V-44)
//  File:    ObjectScatterNode.cs
//  Purpose: General-purpose prefab scatter for props, ruins and set dressing, and -
//           with geomorphology on - the node that places all stone by process.
//
//  WHY STONE LIVES HERE AND NOT IN ITS OWN NODE. Objects are the only prototype kind
//  that can carry arbitrary meshes and solid colliders, and a boulder needs both. So
//  rather than a fourth prototype kind, the deposit model routes through this node's
//  existing index space. THREE AUTHORITIES CAN DECIDE A PROTOTYPE HERE, and they are
//  tried in order of how strong a claim each has on the question:
//
//    1. geomorphology   Transport. Answers "did a process put stone here", which is
//                       the only honest question to ask about stone.
//    2. species table   Habitability. The right authority for anything alive.
//    3. weighted roulette  Authored preference, and the pre-Module-3 behaviour.
//
//  THREAD SAFETY: prototypes is a list of Unity object references and is NEVER
//  dereferenced here - only Count is read, and only to bound the index. Touching
//  a GameObject from the worker would be a Unity API call off the main thread.
//
//  TWO INDEX SPACES MEET HERE, AND THEY PREDATE MODULE 3. The roulette below
//  chooses an index into this node's own prototypes list, while ScatterApplier
//  resolves prototypeIndex against the world library's object array. They agree
//  only as long as the two lists are kept in the same order by hand. The species
//  table addresses the library array directly, which is why it takes precedence
//  when one is described rather than being folded into the roulette as one more
//  weight. Left as it stands otherwise: reindexing the node's list is a change to
//  serialised graph data and belongs nowhere near a placement task.
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

        /// <summary>
        /// V-44. Turns this node into the geomorphology node: every position is tested
        /// against the V-19 deposit model and keeps only the stone a real process
        /// delivered - talus below cliffs at repose, cobble in channels, erratics on
        /// glaciated ground, shingle at the strandline.
        ///
        /// Off by default, because an author's ruins-and-crates node must not silently
        /// become a scree field on upgrade.
        /// </summary>
        public bool geomorphology;

        /// <summary>
        /// Which object prototype represents each DebrisKind. Nine slots, indexed by
        /// the enum, and -1 means this world has no mesh for that kind so none is
        /// placed. This is asset binding, not placement authoring: no position, size,
        /// orientation or abundance is authored here - the process decides all four.
        /// </summary>
        public int[] debrisPrototypes = NoDebrisBinding();

        // All unbound. A world that has not been told which mesh is a boulder places
        // no boulders, which is the honest answer rather than substituting prototype 0
        // and calling a crate a glacial erratic.
        private static int[] NoDebrisBinding()
        {
            int[] b = new int[DebrisShaping.KindCount];
            for (int i = 0; i < b.Length; i++) b[i] = -1;
            return b;
        }

        protected override float NormalAlignment
        {
            get { return alignToNormal ? 1f : normalAlignment; }
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // V-44, and it runs FIRST for a physical reason. A plant is where it is
            // because it can live there, so the species table is the right authority
            // for a plant. A stone is where it is because something carried it there
            // and it stopped - a suitability score cannot express that, and scoring
            // stone on an envelope produces an even dusting of rock over all steep
            // ground, the most recognisable tell of procedural terrain. So when this
            // node is the geomorphology node, transport settles it and the species
            // table is not consulted at all.
            if (geomorphology
                && DebrisShaping.Apply(tile, height, seed, instances, debrisPrototypes, stop))
                return;

            // A described species table settles it and the roulette is skipped, for the
            // index-space reason in the header: the table names slots in the array the
            // applier reads. It also removes the positions where nothing described can
            // stand, which a roulette cannot do because it has no opinion about place.
            if (SpeciesAssignment.TryAssign(tile, height, Channel, seed, instances, stop))
                return;

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
