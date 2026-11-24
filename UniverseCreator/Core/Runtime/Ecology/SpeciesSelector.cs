// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesSelector.cs
//  Purpose: Decide WHICH species lands at a point that has already been chosen.
//
//  PLACEMENT AND IDENTITY ARE TWO SEPARATE QUESTIONS, and this file answers only
//  the second. Something upstream decides that a plant belongs at (ix, iz) - a
//  Poisson disc, a detail cell, a density mask. This file decides what it is.
//  Keeping the two apart is what lets one scatter pass emit a mixed stand instead
//  of one pass per species, which is the defect being closed here: a node that
//  carries a single prototype index can only ever emit a monoculture.
//
//  THE ENVIRONMENT VETOES, THE WEIGHTS ONLY DIVIDE WHAT IS LEFT. Suitability
//  decides admissibility, and only among the survivors does relative abundance
//  choose. A high weight cannot put a species anywhere it could not already live,
//  because the weight is multiplied BY the suitability rather than added to it -
//  so a weight of eight on a species scoring zero here is still zero.
//
//  FOUR SETTINGS FROM THE VEGETATION MANAGER STEER THIS, and all four already
//  shipped with an inspector field. This file is where they finally do something:
//     suitabilityThreshold  the score below which a species is absent rather than
//                           sparse. Note that this is NOT maskThreshold - that one
//                           is the floor on a graph mask value, a different
//                           quantity that lives beside useGraphMasks. Applying the
//                           mask floor to a suitability score would leave the
//                           ecology threshold inert and silently change what the
//                           inspector's own label promises.
//     suitabilitySharpness  an exponent on the score. At one, weights follow
//                           suitability directly. Above one, a slightly better
//                           suited species wins far more often, which is what
//                           crisps a boundary from a wide blend into a visible
//                           edge. Applied here rather than inside the suitability
//                           model, because it is a presentation choice about how
//                           decisively to break a tie, not a fact about a plant.
//     allowMixedStands      off gives the single best species at every site, which
//                           is a plantation. On gives a weighted draw, which is a
//                           forest. This is the switch the whole file exists for.
//     maxSpeciesPerSite     how many may compete once mixing is on. Two admissible
//                           species out of thirty is a stand; thirty out of thirty
//                           is a botanical garden, and no real hillside looks like
//                           one.
//
//  WHY TWO WEIGHT FIELDS REACH THIS FILE AS ONE NUMBER. There are genuinely two,
//  at different scopes, and they multiply:
//     the prototype slot's weight   the author's dial in the library inspector.
//                                   Shipped, and it survives a preset being
//                                   re-applied over the top.
//     the envelope's weight         the species' own relative abundance, part of
//                                   the biological description and therefore part
//                                   of the preset checksum. Re-applying a preset
//                                   resets it to one.
//  This file takes their product as a single authored weight per prototype and
//  never learns that it was two, which keeps Ecology free of any dependency on
//  the prototype library types.
//
//  DETERMINISM. The only randomness is a caller-supplied 0-1 value, which is
//  expected to come from Noise.Random(ix, iz) - a pure hash of the integer cell,
//  so the same cell yields the same species on every machine and every rebuild.
//  UnityEngine.Random is never touched here, and there is no state of any kind
//  between calls: every pick sums the weights fresh, so a candidate cannot be
//  influenced by which candidates happened to be evaluated before it. That
//  matters more than it sounds - a running sum carried between points would make
//  the result depend on tile traversal order, and two tiles meeting at a seam
//  would disagree about the same tree.
//
//  Every comparison that could tie is broken by the lower prototype index, in one
//  place: Precedes. That is what makes the outcome reproducible when two species
//  score identically, which is not a rare case - two equally weighted prototypes
//  on ground that suits both is the normal state of a hand-authored library.
//
//  Noise.Random returns one of permutationCount distinct values, 16384 by
//  default, so a weight share below about 0.006 per cent is unreachable. Nothing
//  in a hand-authored species list comes close to that floor.
//
//  ARRAYS RATHER THAN OBJECTS, and a scratch buffer supplied by the caller. This
//  runs once per placed instance, which is hundreds of thousands of times for a
//  single terrain, so it allocates nothing at all. It is also why the factor
//  arrays are plain floats: a worker thread must never read a MonoBehaviour, and
//  a float array it was handed is safe to read from anywhere.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static class SpeciesSelector
    {
        /// <summary>
        /// Scores every prototype at one site and fills <paramref name="weights"/> with
        /// suitability raised to <paramref name="sharpness"/>, times the authored weight,
        /// dispersal and biome affinity. Leaves zero for anything at or below
        /// <paramref name="suitabilityThreshold"/>. Returns how many came out admissible.
        /// UC-T06-04: invalid sites or selection controls clear the active weights and admit nothing;
        /// nonfinite authored/dispersal/biome weights veto that slot. Threshold is clamped to [0,1].
        /// </summary>
        public static int BuildWeights(EcoEnvelope[] envelopes,
                                       float[] authoredWeights,
                                       float[] dispersalFactors,
                                       int count,
                                       in EnvironmentSample site,
                                       float suitabilityThreshold,
                                       float sharpness,
                                       float[] weights,
                                       float[] suitabilities = null)
        {
            if (envelopes == null || weights == null) return 0;

            // Clamped rather than trusted. A caller whose parallel arrays disagree in
            // length has an authoring bug, but throwing here would abort a bake
            // half-written, which is a far worse outcome than scattering fewer species.
            if (count > envelopes.Length) count = envelopes.Length;
            if (count > weights.Length) count = weights.Length;
            if (count < 0) count = 0;

            // UC-T06-04: one validation per candidate; never reinterpret NaN as an unconstrained site.
            bool valid = site.IsValidForHabitat() && Finite(suitabilityThreshold) && Finite(sharpness) && sharpness >= 0;
            suitabilityThreshold = valid ? Mathf.Clamp01(suitabilityThreshold) : 1f;

            bool sharpen = sharpness > 0f && Mathf.Abs(sharpness - 1f) > 1e-4f;
            int admissible = 0;

            for (int i = 0; i < count; i++)
            {
                weights[i] = 0f;
                if (suitabilities != null && i < suitabilities.Length) suitabilities[i] = 0f;
                if (!valid) continue;

                // Evaluate already returns zero for a null or disabled envelope, so the
                // enabled flag needs no second test and cannot fall out of step with it.
                float suitability = SpeciesSuitability.EvaluateValidSite(envelopes[i], in site);

                if (suitabilities != null && i < suitabilities.Length)
                    suitabilities[i] = suitability;

                if (suitability <= suitabilityThreshold) continue;

                // Only paid for when it changes the answer. An exponent of one is the
                // common case and Mathf.Pow is not free at a few hundred thousand
                // instances per terrain.
                float score = sharpen ? Mathf.Pow(suitability, sharpness) : suitability;

                // Biome affinity is resolved here rather than passed in, because it
                // depends on the site as much as on the species - the same spruce is
                // at home in one biome and a stranger a hundred metres away, and a
                // per-slot number cannot say that.
                float w = score
                          * Factor(authoredWeights, i)
                          * Factor(dispersalFactors, i)
                          * BiomeAffinity.Of(envelopes[i], in site);

                // A zero in any one factor is a veto by that factor, and it is meant to
                // be: no seed has reached this point, or the biome does not contain the
                // species. Such a prototype is admissible by climate and still must not
                // be picked.
                if (!(w > 0f) || !Finite(w)) continue;

                weights[i] = w;
                admissible++;
            }

            return admissible;
        }

        /// <summary>
        /// Picks one index in proportion to <paramref name="weights"/> using a single
        /// 0-1 value. Returns -1 when nothing has any weight, which the caller should
        /// read as "place nothing here" rather than as an error.
        /// </summary>
        public static int SelectWeighted(float[] weights, int count, float random01)
        {
            // UC-T06-04: invalid draws admit nothing; double accumulation keeps finite large weights proportional.
            if (weights == null || !Finite(random01)) return -1;
            if (count > weights.Length) count = weights.Length;
            if (count <= 0) return -1;

            // Summed fresh on every call. This is the "no running sum between
            // candidates" rule, and it is cheap: a species list is a handful of
            // entries, so the second pass costs nothing measurable next to the
            // suitability evaluation that produced the weights.
            double total = 0;
            int lastPositive = -1;

            for (int i = 0; i < count; i++)
            {
                float w = weights[i];
                if (!(w > 0f) || !Finite(w)) continue;
                total += w;
                lastPositive = i;
            }

            if (total <= 0f) return -1;

            // Clamped just below one so a hash that returned exactly 1.0 cannot walk
            // off the end of the accumulation and fall through to the guard below.
            if (random01 < 0f) random01 = 0f;
            else if (random01 > 0.9999999f) random01 = 0.9999999f;

            double target = random01 * total;
            double running = 0;

            // Ascending index order, so which slice of the interval a species owns is
            // fixed by its position in the list and by nothing else.
            for (int i = 0; i < count; i++)
            {
                float w = weights[i];
                if (!(w > 0f) || !Finite(w)) continue;

                running += w;
                if (target < running) return i;
            }

            // Only reachable when the second pass accumulates to slightly less than
            // the first did, which single-precision addition can do. The last
            // positively weighted entry is the honest answer, not a failure.
            return lastPositive;
        }

        /// <summary>
        /// The single best-weighted index, ties going to the lower index. This is what
        /// a site produces when mixed stands are switched off.
        /// </summary>
        public static int SelectBest(float[] weights, int count)
        {
            if (weights == null) return -1;
            if (count > weights.Length) count = weights.Length;

            int best = -1;

            for (int i = 0; i < count; i++)
            {
                if (!(weights[i] > 0f) || !Finite(weights[i])) continue; // UC-T06-04: direct callers cannot select poisoned weights.
                if (best < 0 || Precedes(weights[i], i, weights[best], best)) best = i;
            }

            return best;
        }

        /// <summary>
        /// Zeroes all but the <paramref name="keep"/> best-weighted entries so only the
        /// dominant few compete at a site. No effect when keep is zero or negative,
        /// which reads as "no limit".
        /// </summary>
        public static void KeepTopN(float[] weights, int count, int keep)
        {
            if (weights == null || keep <= 0) return;
            if (count > weights.Length) count = weights.Length;
            if (count <= keep) return;

            // Walk down the ordering keep times to find the cutoff entry, comparing
            // against the previous winner rather than removing it. Mutating during the
            // search would change the ranks of everything not yet examined, which is
            // the obvious implementation and the wrong one.
            float cutoffW = 0f;
            int cutoffI = -1;

            for (int k = 0; k < keep; k++)
            {
                float bestW = 0f;
                int bestI = -1;

                for (int i = 0; i < count; i++)
                {
                    float w = weights[i];
                    if (!(w > 0f) || !Finite(w)) continue;

                    // Must sit strictly below the previous winner in the ordering, or
                    // the same entry would be selected on every pass.
                    if (cutoffI >= 0 && !Precedes(cutoffW, cutoffI, w, i)) continue;
                    if (bestI < 0 || Precedes(w, i, bestW, bestI)) { bestW = w; bestI = i; }
                }

                // Fewer admissible entries than the cap allows, so there is nothing to
                // trim and the cutoff from the previous pass must not be applied.
                if (bestI < 0) return;

                cutoffW = bestW;
                cutoffI = bestI;
            }

            for (int i = 0; i < count; i++)
            {
                float w = weights[i];
                if (!(w > 0f) || !Finite(w)) { weights[i] = 0f; continue; } // UC-T06-04: retain only finite ranked weights.
                if (i == cutoffI) continue;
                if (Precedes(w, i, cutoffW, cutoffI)) continue;
                weights[i] = 0f;
            }
        }

        // The one place a tie is broken, so every method above orders species
        // identically. Lower index wins, which makes a result reproducible when two
        // prototypes score exactly the same - the normal case for two equally
        // weighted species on ground that suits both.
        private static bool Precedes(float weightA, int indexA, float weightB, int indexB)
        {
            if (weightA > weightB) return true;
            if (weightA < weightB) return false;
            return indexA < indexB;
        }

        // Absent, short and negative arrays all mean "this factor has nothing to say
        // about prototype i". A caller that has computed dispersal for the first three
        // species and no further is served correctly by that reading.
        private static float Factor(float[] values, int index)
        {
            if (values == null || index >= values.Length) return 1f;
            return Finite(values[index]) ? Mathf.Max(0f, values[index]) : 0f; // UC-T06-04: invalid authored factors veto placement.
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
