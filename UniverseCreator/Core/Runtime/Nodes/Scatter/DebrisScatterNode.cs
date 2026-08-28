// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: per-type rules implemented
//  File:    DebrisScatterNode.cs
//  Purpose: Small-scale surface litter - pebbles, twigs, bone, rubble.
//
//  Debris differs from rocks mainly in scale and in that it clusters rather than
//  distributing evenly, so it carries a clumping parameter the others don't.
//
//  Clumping is expressed through the base solver's cluster bias rather than a
//  separate two-level sampler: the bias already drops points in sparse
//  neighbourhoods, which leaves the clumps the blue-noise field naturally
//  contained. A second sampling level would place clump members without spacing
//  awareness of the neighbouring clump, reintroducing the overlap the solver
//  exists to prevent.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class DebrisScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Debris Scatter"; } }
        public override string Tooltip { get { return "Clustered surface litter, biased toward hollows."; } }
        public override string Channel { get { return "debris"; } }

        /// <summary>0 spreads evenly, 1 concentrates into tight clumps.</summary>
        public float clumping = 0.5f;

        /// <summary>Average clump radius in world metres.</summary>
        public float clumpRadius = 3f;

        /// <summary>Bias placement toward hollows where debris naturally collects.</summary>
        public bool preferCavities = true;

        /// <summary>
        /// Which prototype represents each DebrisKind, indexed by the enum; -1 means
        /// unbound and nothing is placed for that kind. Asset binding, not placement
        /// authoring - see DebrisShaping.
        /// </summary>
        public int[] debrisPrototypes;

        protected override List<ScatterCandidate> SolvePlacement(
            Matrix height, Matrix mask, TileData tile, StopToken stop)
        {
            // Debris exposes its clumping through its own two parameters, so they
            // are mapped onto the base solver's cluster controls before it runs.
            clusterStrength = Mathf.Clamp01(clumping);
            clusterRadius = Mathf.Max(0.5f, clumpRadius);

            if (!preferCavities || height == null)
                return base.SolvePlacement(height, mask, tile, stop);

            // Cavity is high in hollows and low on ridges. Folding it into the
            // mask means the existing weighting path handles the bias - the solver
            // needs no knowledge of what the extra factor represents.
            Matrix cavity = MatrixOperations.Cavity(height);

            Matrix combined;
            if (mask != null)
            {
                combined = new Matrix(mask);
                for (int i = 0; i < combined.arr.Length && i < cavity.arr.Length; i++)
                    combined.arr[i] *= Mathf.Clamp01(cavity.arr[i]);
            }
            else
            {
                combined = cavity;
            }

            return base.SolvePlacement(height, combined, tile, stop);
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            if (instances.Count == 0) return;

            // V-44. Cavity bias above says where litter COLLECTS; the deposit model
            // says whether any arrived and what it is - deadfall under a canopy,
            // driftwood at the strandline, frost rubble on level ground above the
            // frost line. The two are complementary rather than redundant: the mask
            // shapes the dart field, this grades and orients what the darts became.
            //
            // Nothing is removed to make room for it, because this method had no body
            // before: clumping and cavity bias were, and still are, resolved upstream.
            DebrisShaping.Apply(tile, height, seed, instances,
                                HasBinding() ? debrisPrototypes : null, stop);
        }

        private bool HasBinding()
        {
            return debrisPrototypes != null && debrisPrototypes.Length > 0;
        }
    }
}
