// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesAssignment.cs
//  Purpose: The one place a scatter node's accepted positions are turned into
//           species, and - because the answer is already computed here - the one
//           place the ceiling density becomes the density a site actually carries.
//           Every scatter node calls this; none of them implements it.
//
//  WHY THIS IS NOT IN EACH NODE. Trees, shrubs, grass and objects differ in what
//  they place and in nothing about how the choice is made: the same suitability
//  scores, the same Liebig minimum, the same weighted pick, the same rule that a
//  site suiting nothing keeps nothing. Four copies of that loop would be four
//  places to fix the day the formula gains a term, and three of them would be
//  missed. So the loop lives here and the nodes supply only what actually differs -
//  their channel, their seed, and their own fallback index.
//
//  A NULL ANSWER IS THE PRE-MODULE-3 ANSWER. Every reason this can decline -
//  no context, no table for the channel, a library nobody has described species
//  in, a sampler that could not bind - returns false, and the caller then does
//  exactly what it did before ecology existed. That is what keeps a graph authored
//  against Modules 1 and 2 producing the forest its author configured.
//
//  THE CHANNEL SALTS THE HASH, AND IT HAS TO. Every scatter node ships with the
//  same default seed, so two nodes left at their defaults draw from one stream. A
//  single species offset would then make shrub species track tree species site by
//  site - slot two under slot two across the whole world, which reads as an
//  invisible hand rather than as an ecosystem. Folding the channel in separates
//  the streams whether or not the author ever touches a seed field.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    public static class SpeciesAssignment
    {
        // A stream of its own. The base solver spends seed + 7919 on scale and yaw,
        // and drawing species from that stream would tie species to size - every oak
        // large, every birch small.
        private const int SpeciesHashOffset = 15485863;

        // A third stream, for the V-13 density thinning. It must be independent of
        // both placement and species: sharing the species stream would tie "is this
        // site thinned out" to "which species won here", so the rarer species would
        // be the ones removed on marginal ground and a mixed stand would purify
        // itself as conditions worsened. They are separate questions and they get
        // separate streams.
        private const int DensityHashOffset = 32452843;

        /// <summary>
        /// Assigns a species index to every instance, dropping the ones standing where
        /// nothing can live. Returns false when there is no ecology to decide with, in
        /// which case the list is untouched and the caller applies its own index.
        /// </summary>
        public static bool TryAssign(TileData tile, Matrix height, string channel,
                                     int seed, List<ScatterInstance> instances,
                                     StopToken stop)
        {
            if (instances == null || instances.Count == 0) return false;

            Ecology.EcoScatterContext ctx = tile != null ? tile.ecology : null;
            if (ctx == null) return false;

            Ecology.SpeciesTable table = ctx.TableFor(channel);
            if (table == null || !table.IsUsable()) return false;

            // Bound here and not when the context was built, because the height field is
            // a graph product that did not exist at that point. Bound only after the
            // table is known to say something, so a graph with no species described never
            // pays for derivatives nothing would read.
            ctx.EnsureBound(height);

            if (!ctx.CanSelect()) return false;

            Select(ctx, table, channel, seed, instances, tile.outputs, stop);
            return true;
        }

        private static void Select(Ecology.EcoScatterContext ctx, Ecology.SpeciesTable table,
                                   string channel, int seed, List<ScatterInstance> instances,
                                   OutputBag outputs, StopToken stop)
        {
            int count = table.count;
            if (count > table.envelopes.Length) count = table.envelopes.Length;
            if (count <= 0) return;

            // One buffer for the whole chunk. Allocating per instance would be a hundred
            // thousand garbage arrays on a single terrain.
            float[] weights = new float[count];

            // The unsharpened scores, kept because density must not read a sharpened
            // one. Sharpness is a selection preference - it decides how decisively the
            // best-suited species wins - and an exponent of three applied to density
            // would empty every site that is merely good.
            float[] suitabilities = new float[count];

            Noise pick = new Noise(seed + SpeciesHashOffset + ChannelSalt(channel));
            Noise thin = new Noise(seed + DensityHashOffset + ChannelSalt(channel));

            float floor = ctx.SuitabilityFloor;
            float sharpness = ctx.Sharpness;
            bool mixed = ctx.MixedStands;
            int cap = ctx.MaxSpeciesPerSite;

            // Survivors are packed toward the front so the rejected positions can be
            // trimmed in one call at the end rather than by removing from the middle of
            // the list, which would be quadratic.
            int kept = 0;

            for (int i = 0; i < instances.Count; i++)
            {
                // Break rather than return, so the list is left coherent - compacted to
                // what was actually processed - instead of half rewritten.
                if (stop != null && (i & 255) == 0 && stop.StopRequested) break;

                ScatterInstance inst = instances[i];

                Ecology.EnvironmentSample site =
                    ctx.sampler.Sample(inst.position.x, inst.position.z);

                int admissible = Ecology.SpeciesSelector.BuildWeights(
                    table.envelopes, table.weights, table.dispersal,
                    count, in site, floor, sharpness, weights, suitabilities);

                // No species clears the floor here, so nothing is planted. The position
                // was accepted on slope and height alone; the environment overrules it.
                if (admissible <= 0) continue;

                // Quantised to centimetres before hashing, for the same reason the
                // base solver does it: a position differing in the last float bit
                // between two runs must not change either the species or whether the
                // site survives thinning.
                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);

                int chosen;

                if (!mixed)
                {
                    chosen = Ecology.SpeciesSelector.SelectBest(weights, count);
                }
                else
                {
                    Ecology.SpeciesSelector.KeepTopN(weights, count, cap);

                    chosen = Ecology.SpeciesSelector.SelectWeighted(
                        weights, count, pick.Random(hx, hz));
                }

                if (chosen < 0) continue;

                // V-13. The dart solve threw at the ceiling density, so the stand that
                // remains here is the ceiling times how well this ground suits the
                // species that won it. This is the step that makes a forest thin out
                // toward its limits instead of stopping along a contour.
                float siteFactor = Ecology.EcoDensity.SiteFactor(
                    suitabilities[chosen],
                    Ecology.BiomeAffinity.Of(table.envelopes[chosen], in site));

                if (!Ecology.EcoDensity.KeepAtSite(siteFactor, thin.Random(hx, hz)))
                    continue;

                inst.prototypeIndex = chosen;

                // Carried so the downstream budget evicts by fitness rather than by
                // emission order (V-34). Unsharpened, for the same reason density is.
                inst.suitability = suitabilities[chosen];

                // V-39. A wind-flagged stem above its exposure limit lies down instead
                // of being absent, which is what gives a treeline dwarfed scrub rather
                // than a hard edge. Folded into the instance scale because the applier
                // already multiplies that into the authored size range - adding a second
                // scaling path there would be the duplication that silently drifts.
                if (ctx.settings.krummholz)
                {
                    float kHeight, kWidth, kBend;
                    if (Ecology.Allometry.Krummholz(table.envelopes[chosen],
                                                    site.windExposure,
                                                    out kHeight, out kWidth, out kBend))
                    {
                        Vector3 s = inst.scale;
                        s.x *= kWidth;
                        s.y *= kHeight;
                        s.z *= kWidth;
                        inst.scale = s;
                    }
                }

                instances[kept] = inst;
                kept++;

                // V-52. Tally where this survivor landed, from the same sample the
                // choice was made against - so the histogram is the admitted planting,
                // not a second classification that could disagree with the first. Cheap:
                // Classify is a handful of comparisons on fields the sample already holds.
                if (outputs != null)
                    outputs.AddZone((int)Ecology.LandformAssemblage.Classify(in site));
            }

            if (kept < instances.Count)
                instances.RemoveRange(kept, instances.Count - kept);
        }

        // FNV-1a over the channel name, written out rather than delegated to
        // string.GetHashCode. That method's result is not guaranteed to be the same
        // between runtimes or even between two runs of the same process, and a seed
        // built from it would make the species layout unreproducible - the one thing
        // C-06 does not permit. This is arithmetic on bytes and gives the same number
        // everywhere, forever.
        private static int ChannelSalt(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return 0;

            const uint FnvOffsetBasis = 2166136261u;
            const uint FnvPrime = 16777619u;

            uint hash = FnvOffsetBasis;

            for (int i = 0; i < channel.Length; i++)
            {
                hash ^= channel[i];
                hash *= FnvPrime;
            }

            // Masked to the low 31 bits so the value is positive whatever the string,
            // because it is added to an authored seed and a negative salt would let two
            // different channels land on the same stream by cancelling it out.
            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
