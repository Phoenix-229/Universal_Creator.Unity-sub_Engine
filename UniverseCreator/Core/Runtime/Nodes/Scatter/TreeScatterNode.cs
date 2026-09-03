// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: shared placement solver active
//                     Module 3: species selection active
//  File:    TreeScatterNode.cs
//  Purpose: Places tree instances via Unity's TreeInstance path. Placement,
//           spacing, and height/slope rejection come from the base solver; this
//           file decides which species each accepted position becomes.
//
//  TWO PATHS, AND THE OLD ONE IS NOT DEAD CODE. When the tile carries no ecology
//  context, or the prototype library has no species described in it, every
//  instance takes the node's own prototypeIndex exactly as it did before Module 3.
//  That is what keeps a graph authored against Modules 1 and 2 rendering the same
//  forest it always did. When a real species table IS present, the index is
//  chosen per instance from the environment at that position.
//
//  A SITE THAT SUITS NOTHING LOSES ITS INSTANCE. Once species are described, an
//  accepted position where no species clears the suitability floor is a position
//  where no tree can live, and the instance is removed rather than filled with a
//  fallback. This is the one behavioural change a graph will notice on switching
//  ecology on: the treeline stops being a contour the solver was told about and
//  starts being the place where the last species runs out of warmth.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class TreeScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Tree Scatter"; } }

        public override string Tooltip
        {
            get
            {
                return "Places tree instances. Species come from the prototype library's " +
                       "ecology when one is described, otherwise from Prototype Index.";
            }
        }

        public override string Channel { get { return "trees"; } }

        /// <summary>
        /// Index into the terrain's tree prototype array, used for every instance when
        /// no species table is available. Ignored once the library describes species.
        /// </summary>
        public int prototypeIndex;

        /// <summary>Trees closer than this to a road are removed by Road &amp; City Master.</summary>
        public float roadClearance = 4f;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // Trees stay upright regardless of slope, so the base solver's normal
            // alignment is deliberately left at zero for this type.
            if (!SpeciesAssignment.TryAssign(tile, height, Channel, seed, instances, stop))
                AssignFixedSpecies(instances, stop);

            // Density is NOT applied here, and that is the design rather than an
            // omission. It is applied twice upstream, once at each scale it belongs
            // to: the base solver throws darts at CeilingDensityPerSqKm, which routes
            // this channel to the manager's treesPerHectare, and TryAssign then thins
            // per site by suitability times biome affinity - the moisture-biased term.
            // Repeating either step in this file would square the dial.
            //
            // TODO [roads]: apply roadClearance against the road spline set exposed
            // through the IPCGDataProvider bridge. Clearance is a cut-region question,
            // not a density one, and it is the only work still outstanding here.
        }

        // The pre-ecology behaviour, kept because an unwired graph must still produce
        // the forest its author configured.
        private void AssignFixedSpecies(List<ScatterInstance> instances, StopToken stop)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];
                inst.prototypeIndex = prototypeIndex;
                instances[i] = inst;
            }
        }
    }
}
