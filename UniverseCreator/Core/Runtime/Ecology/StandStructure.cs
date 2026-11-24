// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    StandStructure.cs
//  Purpose: Decides which of the stems a site could carry actually fit, by crown
//           geometry (V-14, L-19/L-20) and then by how much basal area a hectare
//           can support (V-15, L-18).
//
//  WHY SPACING CANNOT BE DECIDED IN THE SOLVER. The blue-noise solve runs before
//  any species is known, so it cannot know a crown radius - a 0.6 m oak wants
//  nine metres of room and a dwarf willow wants one. The solve therefore throws
//  at the tightest spacing the stand could need and this file removes the pairs
//  that turn out to overlap once each stem has a species and a size. Solving
//  twice was the alternative and two solves do not agree about a position.
//
//  DOMINANCE ORDER, NOT ARRIVAL ORDER. The stem that keeps its ground is the one
//  that would win it: best suited, largest. Walking the list in the order the
//  darts happened to land would let a struggling seedling hold a clearing against
//  a mature tree, and the visible result is a stand with holes in the good ground
//  and crowding on the bad. The order is a total order on (dominance, x, z), so
//  it does not depend on the sort being stable - Array.Sort is not.
//
//  CROWNS ARE ALLOWED TO TOUCH. Real canopies interlace at the edges; demanding
//  a clear gap between every pair produces a stand with visible air around each
//  tree, which reads as an orchard. The pair test allows CrownInterlaceFraction
//  of overlap before one of them is dropped.
//
//  NO GRAPH TYPES HERE. This file takes plain arrays and returns a count, for the
//  same reason the Poisson solver takes a predicate instead of a StopToken: the
//  ecology layer must stay testable without building a tile.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// One candidate stem handed to the structural passes. Carries its own index
    /// back into the caller's instance list, because the passes reorder and the
    /// caller has to be able to follow.
    /// </summary>
    public struct StandSite
    {
        public float x;
        public float z;

        public int species;
        public float suitability;

        public StemForm form;

        /// <summary>Index into the caller's own instance list.</summary>
        public int sourceIndex;
    }

    /// <summary>What the structural passes did, for the density readout and the self-test.</summary>
    public struct StandReport
    {
        public int offered;
        public int afterSpacing;
        public int afterReineke;

        public float meanDbhM;
        public float reinekeCapStemsPerHa;
        public float realisedStemsPerHa;

        /// <summary>
        /// Which rule actually spaced this stand. Worth reporting rather than
        /// inferring: "sparse" looks the same whether it came from water competition
        /// or from a density ceiling, and only one of those is a bug.
        /// </summary>
        public SpacingRegime regime;
    }

    public static class StandStructure
    {
        // The inequality being solved is N <= k * meanDbh^-1.605, and meanDbh is
        // itself a function of which stems survive: dropping the smallest raises the
        // mean, which lowers the cap. Four passes is far more than enough to settle
        // it - each pass removes strictly fewer stems than the last - and a fixed
        // bound means the cost is bounded too. This is one inequality being solved,
        // not the iterative succession model that section 1.2 forbids.
        private const int ReinekePasses = 4;

        /// <summary>
        /// Runs both structural passes over <paramref name="sites"/> and packs the
        /// survivors into the front of the array, in descending dominance order.
        /// Returns the number kept.
        /// </summary>
        /// <param name="areaM2">
        /// Ground area the stand occupies. Reineke is a per-hectare law, so the cap
        /// is meaningless without knowing how much ground these stems are spread over.
        /// </param>
        /// <param name="regime">
        /// What these plants compete for (V-21). Crown radius is the right measure only
        /// under <see cref="SpacingRegime.CrownLight"/>; a desert spaces by root
        /// catchment and a reed bed does not space at all.
        /// </param>
        public static int Resolve(StandSite[] sites, int count, EcoEnvelope[] envelopes,
                                  float spacingFloorM, float areaM2,
                                  SpacingRegime regime, float aridity,
                                  out StandReport report)
        {
            report = new StandReport();

            if (sites == null || count <= 0)
                return 0;

            if (count > sites.Length) count = sites.Length;

            report.offered = count;
            report.regime = regime;

            SortByDominance(sites, count);

            int kept = SeparateCrowns(sites, count, spacingFloorM, regime, aridity);
            report.afterSpacing = kept;

            // Reineke describes a closed even-aged stand thinning itself by shading.
            // Neither a water-limited shrubland nor a clonal mat is that, and capping
            // them by stem diameter would impose a constraint that is not acting on
            // them - the spacing pass above already applied the one that is.
            if (ZoneSpacing.AppliesReinekeCap(regime))
                kept = ApplyReinekeCap(sites, kept, envelopes, areaM2, ref report);

            report.afterReineke = kept;

            float hectares = Mathf.Max(1e-6f, areaM2) / EcoConstants.SquareMetresPerHectare;
            report.realisedStemsPerHa = kept / hectares;

            return kept;
        }

        // ---- V-14 -----------------------------------------------------------

        /// <summary>
        /// Drops every stem whose crown overlaps an already-accepted crown by more
        /// than the interlace allowance, or that stands closer than the authored
        /// spacing floor. Survivors are packed forward, order preserved.
        /// </summary>
        public static int SeparateCrowns(StandSite[] sites, int count, float spacingFloorM,
                                         SpacingRegime regime, float aridity)
        {
            if (sites == null || count <= 0) return 0;

            float floor = Mathf.Max(0f, spacingFloorM);

            // The grid cell is the largest separation any pair here could require, so a
            // pair that can possibly conflict is always within the 3x3 neighbourhood.
            // Sizing it to the mean instead would be faster on average and wrong
            // occasionally, which is the worse trade: a missed overlap is a visible
            // interpenetration. Note this is the largest SEPARATION and not the largest
            // crown - under the water regime the root catchment is several times the
            // crown, and sizing cells to crowns there would miss most conflicts.
            float maxRadius = 0f;
            for (int i = 0; i < count; i++)
                if (sites[i].form.crownRadiusM > maxRadius) maxRadius = sites[i].form.crownRadiusM;

            float cell = Mathf.Max(
                Mathf.Max(1f, floor),
                ZoneSpacing.RequiredSeparation(regime, maxRadius, maxRadius, aridity, floor));

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                if (sites[i].x < minX) minX = sites[i].x;
                if (sites[i].x > maxX) maxX = sites[i].x;
                if (sites[i].z < minZ) minZ = sites[i].z;
                if (sites[i].z > maxZ) maxZ = sites[i].z;
            }

            int gw = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / cell) + 1);
            int gh = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / cell) + 1);

            // A dense chunk can offer a hundred thousand stems, so the grid is a flat
            // head/next linked list rather than a jagged array of Lists - one int per
            // cell and one per stem, allocated once.
            int cells = gw * gh;
            int[] head = new int[cells];
            int[] next = new int[count];

            for (int i = 0; i < cells; i++) head[i] = -1;

            int kept = 0;

            for (int i = 0; i < count; i++)
            {
                StandSite s = sites[i];

                int cx = Mathf.Clamp((int)((s.x - minX) / cell), 0, gw - 1);
                int cz = Mathf.Clamp((int)((s.z - minZ) / cell), 0, gh - 1);

                bool blocked = false;

                for (int oz = -1; oz <= 1 && !blocked; oz++)
                {
                    int nz = cz + oz;
                    if (nz < 0 || nz >= gh) continue;

                    for (int ox = -1; ox <= 1 && !blocked; ox++)
                    {
                        int nx = cx + ox;
                        if (nx < 0 || nx >= gw) continue;

                        for (int j = head[nz * gw + nx]; j != -1; j = next[j])
                        {
                            float dx = s.x - sites[j].x;
                            float dz = s.z - sites[j].z;
                            float d2 = dx * dx + dz * dz;

                            float need = ZoneSpacing.RequiredSeparation(
                                regime, s.form.crownRadiusM, sites[j].form.crownRadiusM,
                                aridity, floor);

                            if (d2 < need * need) { blocked = true; break; }
                        }
                    }
                }

                if (blocked) continue;

                // Accepted. Written to the front of the same array; because kept <= i
                // always, this never overwrites a site not yet examined.
                sites[kept] = s;

                int hk = cz * gw + cx;

                next[kept] = head[hk];
                head[hk] = kept;

                kept++;
            }

            return kept;
        }

        /// <summary>
        /// Centre-to-centre distance two stems need: their two crown radii less the
        /// interlace allowance, but never less than the authored floor.
        /// </summary>
        public static float RequiredSeparation(float radiusA, float radiusB, float floorM)
        {
            float crowns = (Mathf.Max(0f, radiusA) + Mathf.Max(0f, radiusB))
                         * (1f - EcoConstants.CrownInterlaceFraction);

            return Mathf.Max(crowns, Mathf.Max(0f, floorM));
        }

        // ---- V-15 -----------------------------------------------------------

        /// <summary>
        /// Truncates the stand to the stems per hectare Reineke's law allows at the
        /// mean diameter the stand actually carries. The array is already in
        /// descending dominance order, so the stems dropped are the lowest-suitability
        /// smallest ones - which is both what self-thinning does and what looks right.
        /// </summary>
        public static int ApplyReinekeCap(StandSite[] sites, int count, EcoEnvelope[] envelopes,
                                          float areaM2, ref StandReport report)
        {
            if (sites == null || count <= 0) return 0;

            float hectares = Mathf.Max(1e-6f, areaM2) / EcoConstants.SquareMetresPerHectare;

            for (int pass = 0; pass < ReinekePasses; pass++)
            {
                float sumDbh = 0f;
                float sumK = 0f;

                for (int i = 0; i < count; i++)
                {
                    sumDbh += sites[i].form.dbhM;
                    sumK += SelfThinningK(envelopes, sites[i].species);
                }

                float meanDbh = sumDbh / count;
                float meanK = sumK / count;

                report.meanDbhM = meanDbh;

                // Reineke's coefficient is a species property, so a mixed stand is
                // capped at the mean of its members' coefficients rather than at one
                // species' figure. A pine-and-oak stand tolerates something between
                // what either would alone, which is the observed answer.
                float capPerHa = EcoMath.ReinekeMaxStemsPerHa(meanDbh, meanK);
                report.reinekeCapStemsPerHa = capPerHa;

                int allowed = Mathf.FloorToInt(capPerHa * hectares);

                // At least one stem survives wherever one was offered. A hectare whose
                // cap rounds to zero is still not bare ground in reality - it carries
                // one tree - and returning zero here would punch holes in a forest at
                // the chunk scale rather than thin it.
                if (allowed < 1) allowed = 1;

                if (count <= allowed) return count;

                count = allowed;

                // Dropping the smallest stems raised the mean diameter, which lowered
                // the cap. Re-solve. Each pass removes strictly fewer than the last,
                // so this settles rather than oscillating.
            }

            return count;
        }

        private static float SelfThinningK(EcoEnvelope[] envelopes, int species)
        {
            if (envelopes == null || species < 0 || species >= envelopes.Length)
                return EcoConstants.ReinekeDefaultK;

            EcoEnvelope eco = envelopes[species];
            if (eco == null || eco.selfThinningK <= 0f) return EcoConstants.ReinekeDefaultK;

            return eco.selfThinningK;
        }

        // ---- ordering -------------------------------------------------------

        /// <summary>
        /// Dominance: how much claim this stem has on its ground. Suitability times
        /// vigour, so a well-suited big tree outranks a well-suited seedling and both
        /// outrank anything struggling.
        /// </summary>
        public static float Dominance(in StandSite s)
        {
            return Mathf.Clamp01(s.suitability) * Mathf.Clamp01(s.form.vigour);
        }

        private static void SortByDominance(StandSite[] sites, int count)
        {
            // Array.Sort over a sub-range, with a comparison that is a strict total
            // order. Position breaks dominance ties, so two stems that score
            // identically still have a defined winner and the same one every rebuild.
            Array.Sort(sites, 0, count, DominanceComparer.Instance);
        }

        private sealed class DominanceComparer : System.Collections.Generic.IComparer<StandSite>
        {
            public static readonly DominanceComparer Instance = new DominanceComparer();

            public int Compare(StandSite a, StandSite b)
            {
                float da = Dominance(in a);
                float db = Dominance(in b);

                if (da > db) return -1;
                if (da < db) return 1;

                if (a.x < b.x) return -1;
                if (a.x > b.x) return 1;

                if (a.z < b.z) return -1;
                if (a.z > b.z) return 1;

                // Two stems at one position cannot both be kept anyway; ordering by
                // source index keeps the comparison a total order, which Array.Sort
                // requires and will otherwise throw over.
                return a.sourceIndex.CompareTo(b.sourceIndex);
            }
        }
    }
}
