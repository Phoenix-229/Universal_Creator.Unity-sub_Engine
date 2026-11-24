// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    DispersalField.cs
//  Purpose: Answers "has a seed of this species reached this spot?" (V-17, L-21).
//           This is the single field that decides whether a wood reads as grown
//           or as sprinkled.
//
//  WHY A LATTICE AND NOT A SIMULATION. Real clumping comes from parents: an oak
//  drops acorns within a few tens of metres and the grove spreads outward from
//  where an oak already stood. Simulating that needs generations, and section 1.2
//  forbids iterative succession. So the parents are placed by construction - a
//  deterministic lattice, one parent per species per cell, each jittered inside
//  its own cell by a hash of the cell - and every candidate is weighted by its
//  distance to the nearest parent of the species being considered.
//
//  The result is indistinguishable from the outcome of the process it replaces,
//  because it is the same geometry: seed sources at a characteristic spacing, and
//  an exponential fall-off around each. What it is not is random. Ask twice and
//  the same parents are in the same places.
//
//  ONE PARENT PER SPECIES PER CELL, NOT ONE PARENT PER CELL. Giving each cell a
//  single species chosen by hash is the obvious shortcut and it fails in a way
//  that is easy to miss: in country where only two of ten species are viable,
//  eight cells in ten would host a parent that cannot breed, and the forest comes
//  out sparse for a reason nothing in the inspector explains. Every species gets
//  its own jittered lattice instead, and an unviable one simply scores zero on
//  suitability as it already would.
//
//  WHY THERE IS A FLOOR. Seeds travel further than the kernel says, rarely - on
//  birds, in floods, in mud on a hoof. Without a floor the kernel is a hard mask
//  and a species is absolutely absent more than a few lambda from a parent, which
//  produces visible circular grove boundaries. The floor is small enough that it
//  reads as the occasional outlier it is.
//
//  COST. Nine lattice cells times the species count per candidate, one exponential
//  each. The nearest parent of a species is always inside the candidate's own 3x3
//  neighbourhood because the jitter never leaves its cell, so a wider search would
//  find nothing a narrower one missed.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public sealed class DispersalField
    {
        // Its own stream. Sharing the placement or species stream would tie where a
        // parent stands to where a dart landed, and groves would then line up with
        // the blue-noise pattern instead of cutting across it.
        private const int LatticeHashOffset = 49979687;

        private Noise lattice;
        private float cellM = EcoConstants.ParentLatticeCellMetres;

        public void Bind(int seed)
        {
            lattice = new Noise(seed + LatticeHashOffset);
            cellM = Mathf.Max(1f, EcoConstants.ParentLatticeCellMetres);
        }

        public bool IsBound { get { return lattice != null; } }

        /// <summary>
        /// Fills <paramref name="factors"/> with each species' 0-1 chance of having
        /// been seeded at this position. SpeciesSelector multiplies these into the
        /// weights, so a species with no parent nearby loses to one that has.
        /// </summary>
        public void FillFactors(float worldX, float worldZ, EcoEnvelope[] envelopes,
                                int count, float[] factors)
        {
            if (factors == null || count <= 0) return;

            if (lattice == null)
            {
                // Unbound means the dispersal model is off. Every species is treated as
                // seeded everywhere, which is exactly the pre-V-17 behaviour.
                for (int i = 0; i < count; i++) factors[i] = 1f;
                return;
            }

            if (count > factors.Length) count = factors.Length;

            int baseX = Mathf.FloorToInt(worldX / cellM);
            int baseZ = Mathf.FloorToInt(worldZ / cellM);

            for (int i = 0; i < count; i++)
            {
                float lambda = LambdaFor(envelopes, i);

                // A species that disperses over kilometres is everywhere by definition.
                // Skipping the search for it is not an optimisation for its own sake -
                // it avoids reporting spurious structure from lattice geometry at a
                // scale where the kernel is flat anyway.
                if (lambda >= EcoConstants.ParentLatticeCellMetres
                            * EcoConstants.ParentLatticeSearchCells * 2f)
                {
                    factors[i] = 1f;
                    continue;
                }

                float nearest2 = float.MaxValue;

                for (int oz = -1; oz <= 1; oz++)
                {
                    int cz = baseZ + oz;

                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int cx = baseX + ox;

                        // Two independent draws inside the cell. Using one for both axes
                        // would put every parent on the cell's diagonal, and the lattice
                        // would show through as diagonal banding across the world.
                        float jx = lattice.Random(cx, cz, i);
                        float jz = lattice.Random(cz + 7919, cx + 104729, i);

                        float px = (cx + jx) * cellM;
                        float pz = (cz + jz) * cellM;

                        float dx = worldX - px;
                        float dz = worldZ - pz;

                        float d2 = dx * dx + dz * dz;
                        if (d2 < nearest2) nearest2 = d2;
                    }
                }

                float d = Mathf.Sqrt(nearest2);

                float kernel = EcoMath.DispersalWeight(d, lambda);

                factors[i] = Mathf.Max(EcoConstants.DispersalBaselineFraction, kernel);
            }
        }

        private static float LambdaFor(EcoEnvelope[] envelopes, int i)
        {
            if (envelopes == null || i < 0 || i >= envelopes.Length)
                return EcoConstants.DispersalDefaultLambdaM;

            EcoEnvelope eco = envelopes[i];
            if (eco == null || eco.dispersalLambdaM <= 0f)
                return EcoConstants.DispersalDefaultLambdaM;

            return eco.dispersalLambdaM;
        }
    }
}
