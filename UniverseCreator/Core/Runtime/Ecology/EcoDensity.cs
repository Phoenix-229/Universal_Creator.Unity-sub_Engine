// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    EcoDensity.cs
//  Purpose: V-13. The one place a scatter channel's density becomes a number, and
//           the one place a site's suitability becomes how much of it stands there.
//
//           stemsPerHa = treesPerHectare x globalDensityScale x suitability x biomeMask
//
//  DENSITY IS A CEILING, NOT A TARGET. The authored figure is what the best site in
//  the world carries; every other site carries less, in proportion to how well it
//  suits the species that grows there. That ordering matters: throwing darts at an
//  average density and then adding more on good ground would need a second solve at
//  a finer spacing, and the two solves would not agree about the same position. So
//  the solve happens once at the ceiling spacing and the surplus is removed. A
//  hectare of ideal ground therefore reaches the authored stems/ha exactly, and
//  nothing anywhere exceeds it.
//
//  THE THINNING IS A HASH, NOT A RANDOM. Which surplus instance goes has to be the
//  same answer on every rebuild and on every machine, so the decision is a hash of
//  the quantised world position compared against the site factor. Sequential
//  Random would make the answer depend on how many instances happened to be
//  processed first, which differs the moment a chunk is solved on another worker.
//
//  THE DIAL MULTIPLIES ONCE. globalDensityScale is already folded into the resolved
//  per-square-metre figures by UCVegetationSystemManager.ResolveSettings, which is
//  why it must not be applied again here - that would square it, and halving the
//  dial would quarter the world.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static class EcoDensity
    {
        /// <summary>
        /// stems/m2 the densest possible site carries on this channel. Trees and
        /// detail come from the vegetation manager's shipped fields; the channels
        /// that manager has no figure for keep the node's own density, scaled by
        /// the global dial so one field still thins an entire world.
        /// </summary>
        /// <param name="channel">Scatter channel. The vocabulary is the one
        /// <see cref="EcoScatterContext.TableFor"/> answers to.</param>
        /// <param name="nodeDensityPerSqKm">The node's authored density, in
        /// instances per square kilometre - the pre-Module-3 figure.</param>
        public static float CeilingPerSquareMetre(string channel, in EcoSampleSettings s,
                                                 float nodeDensityPerSqKm)
        {
            // Zero is an answer, not a missing value: an author who sets
            // treesPerHectare to nothing means a treeless world, and falling back to
            // the node's own figure there would repopulate it. Settings only ever
            // reach a scatter node through a resolved EcoScatterContext, so the
            // field is trustworthy even when it reads zero.
            if (channel == "trees") return Mathf.Max(0f, s.treeDensityPerM2);
            if (channel == "grass") return Mathf.Max(0f, s.grassDensityPerM2);

            // Shrubs, objects, rock and debris. No shipped per-hectare field exists
            // for these, so the node stays in charge of how many - but not of the
            // one dial a whole world is thinned by.
            float perM2 = Mathf.Max(0f, nodeDensityPerSqKm) * 1e-6f;
            return perM2 * Mathf.Max(0f, s.densityScale);
        }

        /// <summary>
        /// The V-13 site term, 0-1. Suitability says how well the species does
        /// here; biome affinity says whether it belongs here at all. Multiplied,
        /// not minimised: a species that is marginal AND out of biome is rarer
        /// than one that is only marginal, and both are still possible.
        /// </summary>
        public static float SiteFactor(float suitability, float biomeAffinity)
        {
            if (suitability <= 0f || biomeAffinity <= 0f) return 0f;
            return Mathf.Clamp01(suitability) * Mathf.Clamp01(biomeAffinity);
        }

        /// <summary>
        /// Whether the instance at a site survives the thinning. A factor of 0.4
        /// keeps four sites in ten, which is what turns a ceiling density into a
        /// realised one.
        /// </summary>
        /// <param name="hash01">A 0-1 value hashed from the site's world position,
        /// never a sequential random draw.</param>
        public static bool KeepAtSite(float siteFactor, float hash01)
        {
            if (siteFactor >= 1f) return true;
            if (siteFactor <= 0f) return false;

            // Strictly-less so a factor that has rounded to zero cannot be kept by
            // a hash that also rounded to zero.
            return hash01 < siteFactor;
        }

        /// <summary>
        /// The best site factor any species in a table achieves here, and nothing
        /// about which one it is. This is the figure a density grid needs - grass
        /// coverage is a number per cell with no instance to name a species for.
        /// Returns 0 when the site suits nothing, which reads as bare ground.
        /// </summary>
        /// <param name="weightBuf">Scratch, at least table.count long. Supplied by
        /// the caller because this runs per cell and must not allocate.</param>
        /// <param name="suitBuf">Scratch for the unsharpened suitabilities, same
        /// length. Sharpening is a selection preference and must not reach density -
        /// an exponent of 3 would darken every imperfect site threefold.</param>
        public static float BestSiteFactor(SpeciesTable table, in EnvironmentSample site,
                                           float suitabilityFloor,
                                           float[] weightBuf, float[] suitBuf)
        {
            if (table == null || !table.IsUsable()) return 0f;
            if (weightBuf == null || suitBuf == null) return 0f;

            int count = table.count;
            if (count > table.envelopes.Length) count = table.envelopes.Length;
            if (count > weightBuf.Length) count = weightBuf.Length;
            if (count > suitBuf.Length) count = suitBuf.Length;
            if (count <= 0) return 0f;

            // Sharpness of one, so weightBuf holds suitability times the authored
            // weight, dispersal and biome affinity. The scores decide only which
            // species is the best one here; the factor is then read from that
            // species' own unsharpened suitability and affinity.
            int admissible = SpeciesSelector.BuildWeights(
                table.envelopes, table.weights, table.dispersal,
                count, in site, suitabilityFloor, 1f, weightBuf, suitBuf);

            if (admissible <= 0) return 0f;

            int best = SpeciesSelector.SelectBest(weightBuf, count);
            if (best < 0) return 0f;

            // The winning species' own density trim, applied to the factor and
            // deliberately NOT folded into weightBuf. A density multiplier must not
            // decide which species wins, for the mirror of the reason sharpening must
            // not reach density: trimming a species to a quarter should thin it where
            // it already grows, not hand its ground over to the runner-up.
            float trim = Mathf.Max(0f, table.envelopes[best].detailDensityScale);

            return SiteFactor(suitBuf[best], BiomeAffinity.Of(table.envelopes[best], in site)) * trim;
        }

        /// <summary>
        /// stems/ha from stems/m2, for the count readout an author reads. Kept here
        /// so the conversion has one home rather than a factor of ten thousand
        /// written out at each display site.
        /// </summary>
        public static float StemsPerHectare(float perSquareMetre)
        {
            return Mathf.Max(0f, perSquareMetre) * EcoConstants.SquareMetresPerHectare;
        }
    }
}
