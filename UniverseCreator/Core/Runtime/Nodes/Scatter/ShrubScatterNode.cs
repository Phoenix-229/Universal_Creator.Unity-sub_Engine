// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//                     Module 3: species selection active
//  File:    ShrubScatterNode.cs
//  Purpose: Places bushes, shrubs and ferns. Its own node, and not a setting on
//           Tree Scatter or Grass Detail Scatter, because a shrub is its own
//           thing in every way that matters downstream.
//
//  WHY NOT JUST USE TREE SCATTER OR GRASS SCATTER
//  ----------------------------------------------
//  The three classes diverge at every stage of the pipeline, not just visually:
//
//    placement   Trees go into TerrainData.treePrototypes and get billboarded at
//                distance. Grass goes into a per-cell density grid. A shrub is a
//                real GameObject, because it is the only one of the three that
//                needs a collider.
//    collision   A tree is solid, grass has nothing, a shrub has a trigger you
//                can walk through. Three different states.
//    wind        A tree holds its trunk and moves only the canopy, grass bends
//                from the root, a shrub holds its lower quarter and gives above
//                it. Three different motion models.
//
//  Sharing a node between any two of those would mean a flag that changes all
//  three behaviours at once, which is a separate node wearing a disguise.
//
//  Emits on the "shrubs" channel, which ScatterApplier routes to the shrub path.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class ShrubScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Shrub Scatter"; } }

        public override string Tooltip
        {
            get
            {
                return "Places bushes and shrubs as walk-through GameObjects with trigger "
                     + "colliders and shrub-class wind. Use Tree Scatter for trees and "
                     + "Grass Detail Scatter for ground cover.";
            }
        }

        public override string Channel { get { return "shrubs"; } }

        [Tooltip("Index into the world's shrub prototype list. Not the tree or object list - "
               + "shrubs have their own section because they behave differently from both.")]
        public int prototypeIndex;

        [Tooltip("Shrubs cluster in real terrain: a thicket, then bare ground, then another "
               + "thicket. 0 places them evenly, 1 pulls them into tight clumps.")]
        [Range(0f, 1f)]
        public float clumping = 0.45f;

        [Tooltip("Scale of one clump in metres. Below the scatter spacing this has no effect.")]
        [Range(1f, 200f)]
        public float clumpSizeMetres = 18f;

        [Tooltip("Random lean off vertical, in degrees. A shrub is rarely perfectly upright.")]
        [Range(0f, 30f)]
        public float randomLean = 8f;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // Species first, for two reasons. It drops the positions where no shrub can
            // live, so the clumping and lean work below is only spent on instances that
            // will actually stand there. And it is the authority on prototypeIndex when
            // it succeeds, which is why the assignment in the loop became conditional
            // rather than staying unconditional and overwriting the choice a line later.
            bool speciesChosen =
                SpeciesAssignment.TryAssign(tile, height, Channel, seed, instances, stop);

            float clumpScale = 1f / Mathf.Max(0.01f, clumpSizeMetres);

            // Offset streams so the clump field and the lean angle do not correlate
            // with each other or with the base solver's placement hash.
            Noise clumpNoise = new Noise(seed + 7717);
            Noise leanNoise = new Noise(seed + 4451);

            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];

                // The node's own index is the pre-ecology answer, and still the answer
                // for a library nobody has described species in.
                if (!speciesChosen) inst.prototypeIndex = prototypeIndex;

                // Quantised to centimetres before hashing, matching the base solver:
                // a world position differing in its last float bit between two runs
                // must not pick a different result.
                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);

                if (clumping > 0.001f)
                {
                    // Absolute world coordinates, so a thicket straddling a chunk
                    // boundary is one thicket and not two half-thickets.
                    float clump = clumpNoise.Perlin(inst.position.x * clumpScale,
                                                    inst.position.z * clumpScale);
                    clump = Mathf.Clamp01(clump * 0.5f + 0.5f);

                    float keep = Mathf.Lerp(1f, clump, clumping);
                    float roll = clumpNoise.Random(hx, hz);

                    if (roll > keep)
                    {
                        // Zero scale marks a rejected instance; ScatterApplier's
                        // shrub path skips it. Thinning rather than relocating keeps
                        // the spacing the base solver already guaranteed, and gaps
                        // are exactly what the edge of a thicket is.
                        inst.scale = Vector3.zero;
                        instances[i] = inst;
                        continue;
                    }
                }

                if (randomLean > 0.01f)
                {
                    float a = leanNoise.Random(hx, hz) * 360f;
                    float t = leanNoise.Random(hz + 104729, hx + 1299709) * randomLean;

                    Vector3 axis = new Vector3(Mathf.Cos(a * Mathf.Deg2Rad), 0f,
                                               Mathf.Sin(a * Mathf.Deg2Rad));
                    inst.rotation = inst.rotation * Quaternion.AngleAxis(t, axis);
                }

                instances[i] = inst;
            }
        }
    }
}
