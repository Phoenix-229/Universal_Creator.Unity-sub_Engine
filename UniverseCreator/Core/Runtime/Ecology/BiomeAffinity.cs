// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    BiomeAffinity.cs
//  Purpose: How much a species belongs to the biome it is standing in, as a 0-1
//           factor on its selection weight.
//
//  WHY A BOUNDARY MUST NOT BE A LINE. Membership on its own is a step function:
//  inside the biome a species is allowed, one metre outside it is gone. Bake that
//  and a world grows two forests meeting along a seam you can trace with a finger,
//  which no real landscape has. Real assemblages interdigitate - tongues of one
//  running into the other across tens of metres - because the climate does not
//  change at a line either, and the classification either side of the break is
//  genuinely uncertain.
//
//  THE MIX IS BETWEEN THE TWO BIOMES PRESENT, AND ONLY THOSE TWO. The obvious
//  softening is to relax every membership test near a boundary, and it is wrong: it
//  admits species from all ten biomes into the band, so a tundra-boreal seam grows
//  a stripe of mangrove. This model instead asks the sampler which two biomes meet
//  here and mixes those, weighting each by its share of the site:
//
//      affinity = member(primary) * share + member(neighbour) * (1 - share)
//
//  Deep inside a biome the share is one, the neighbour term vanishes, and the test
//  is as hard as it ever was. On the break the share is a half and both assemblages
//  score 0.5, so the weighted draw interleaves them. A species belonging to neither
//  scores zero at every point of the band, which is the third-species artefact this
//  formula exists to make impossible.
//
//  SILENCE IS PERMISSION. An envelope with no mask and no per-biome weights returns
//  one, and so does a site the sampler could not classify. Biome affinity is a
//  restriction an author opts into; a library that has never heard of it must place
//  exactly what it placed before, or switching a graph to Module 3 would empty it.
//
//  TWO FIELDS, TWO JOBS. allowedBiomes is a veto - a species is eligible or it is
//  not - while perBiomeWeight says a species is commoner in one of its biomes than
//  another. They multiply, so an author can express "oak: temperate forest and
//  grassland, but three times commoner in the forest" without either field having to
//  encode the other's meaning.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static class BiomeAffinity
    {
        /// <summary>
        /// True when this envelope has anything at all to say about biomes. Lets a
        /// caller skip the whole model for a library that never opted in.
        /// </summary>
        public static bool Describes(EcoEnvelope eco)
        {
            if (eco == null) return false;

            // A mask of every biome is the same statement as a mask of none, and an
            // author who ticked all ten boxes has expressed no restriction.
            if (eco.allowedBiomes != 0 && eco.allowedBiomes != WhittakerBiome.AllMask)
                return true;

            return eco.perBiomeWeight != null && eco.perBiomeWeight.Length > 0;
        }

        /// <summary>
        /// True when any envelope in the table describes a biome preference.
        /// </summary>
        public static bool AnyDescribes(EcoEnvelope[] envelopes, int count)
        {
            if (envelopes == null) return false;
            if (count > envelopes.Length) count = envelopes.Length;

            for (int i = 0; i < count; i++)
                if (Describes(envelopes[i])) return true;

            return false;
        }

        /// <summary>
        /// This species' 0-1 affinity for the site, blending across a boundary between
        /// the two biomes the site actually lies between. One means no restriction
        /// applies; zero is a veto.
        /// </summary>
        public static float Of(EcoEnvelope eco, in EnvironmentSample site)
        {
            if (!Describes(eco)) return 1f;

            // Unclassified ground. Scoring a preference against a biome nobody
            // determined would silently delete every species that named one, so an
            // absent classification withdraws the test instead of failing it.
            if (site.biomeIndex < 0) return 1f;

            float share = Mathf.Clamp01(site.biomeBlend);

            // Remapped from confidence to share. Confidence one is the interior of a
            // biome and the site is wholly its own; confidence zero is the break
            // itself, where the site belongs half to each - never less, because
            // "primary" is by definition the nearer of the two.
            share = 0.5f + 0.5f * share;

            float affinity = MembershipOf(eco, site.biomeIndex) * share;

            // Only the far side of the break has a neighbour to mix with. Everywhere
            // else the sampler leaves it unset and the primary term stands alone.
            if (site.biomeIndexAlt >= 0 && site.biomeIndexAlt != site.biomeIndex)
                affinity += MembershipOf(eco, site.biomeIndexAlt) * (1f - share);

            return Mathf.Clamp01(affinity);
        }

        /// <summary>
        /// How strongly this species belongs to one specific biome: zero when the mask
        /// excludes it, otherwise its per-biome weight, or one when it has none.
        /// </summary>
        public static float MembershipOf(EcoEnvelope eco, int biomeIndex)
        {
            if (eco == null) return 1f;
            if (biomeIndex < 0 || biomeIndex >= WhittakerBiome.Count) return 0f;

            // Zero and all-set both mean "eligible everywhere", so the veto is skipped
            // rather than tested against a mask that would pass anyway.
            if (eco.allowedBiomes != 0 && eco.allowedBiomes != WhittakerBiome.AllMask)
            {
                if ((eco.allowedBiomes & WhittakerBiome.MaskOf(biomeIndex)) == 0) return 0f;
            }

            float[] w = eco.perBiomeWeight;

            // A short array is not an error: an author who weighted the first four
            // biomes and stopped has said nothing about the rest, and nothing means one.
            if (w == null || biomeIndex >= w.Length) return 1f;

            return Mathf.Max(0f, w[biomeIndex]);
        }
    }
}
