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

        // A fourth stream, for the age/size draw (§4O item 4). Independent of placement,
        // species and thinning so a tree's AGE does not track which species won or whether
        // the site was thinned - a young oak and an old oak must be able to stand together.
        private const int AgeHashOffset = 49979687;

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

            // UC-T02-07: retain the cross-layer effects separately from the immutable instance list.
            int[] histogram = (int[])tile.outputs.zoneHistogram.Clone();
            var canopy = new List<CanopyGraphStamp>();
            int candidates = instances.Count;
            Select(ctx, table, channel, seed, instances, tile.outputs, stop, canopy, out int[] rejections); // UC-T06-17: bounded counters, separate from instance selection/randomness.
            if (stop == null || !stop.StopRequested)
            {
                string[] stages = { "species terrain", "species cut regions", "species suitability", "species water", "species canopy", "species density" };
                int remaining = candidates;
                for (int stage = 0; stage < stages.Length; stage++)
                { VegetationPlacementReport.Publish(tile, stages[stage], remaining, remaining - rejections[stage]); remaining -= rejections[stage]; }
            }
            for (int i = 0; i < histogram.Length; i++) histogram[i] = tile.outputs.zoneHistogram[i] - histogram[i];
            tile.AddPublication(new EcologyGraphPublication(tile.CurrentPublicationKey + ":ecology:" + channel,
                height, canopy.ToArray(), histogram), alreadyApplied: true);
            return true;
        }

        private static void Select(Ecology.EcoScatterContext ctx, Ecology.SpeciesTable table,
                                   string channel, int seed, List<ScatterInstance> instances,
                                   OutputBag outputs, StopToken stop, List<CanopyGraphStamp> canopyPublications, out int[] rejections)
        {
            rejections = new int[6]; // UC-T06-17: one exclusive rejection bucket per processed site, allocated once per pass.
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
            Noise age  = new Noise(seed + AgeHashOffset + ChannelSalt(channel));

            float floor = ctx.SuitabilityFloor;
            float sharpness = ctx.Sharpness;
            bool mixed = ctx.MixedStands;
            int cap = ctx.MaxSpeciesPerSite;

            // Survivors are packed toward the front so the rejected positions can be
            // trimmed in one call at the end rather than by removing from the middle of
            // the list, which would be quadratic.
            int kept = 0;

            // VEG-8 anti-repeat. When mixed stands are on and a radius is set, track each placed
            // species in a chunk-local spatial hash and bias the next nearby site AGAINST it, so
            // neighbouring plants vary instead of repeating one slot. Off (radius 0) builds nothing
            // and the per-site pick is unchanged. Bounds come from the candidate positions.
            float antiR = (mixed && ctx.settings.antiRepeatRadiusM > 0f) ? ctx.settings.antiRepeatRadiusM : 0f;
            float antiStrength = Mathf.Clamp01(ctx.settings.antiRepeatStrength);
            SpatialHash placedHash = null;
            System.Collections.Generic.List<int> placedSpecies = null;
            System.Collections.Generic.List<int> arQuery = null;
            if (antiR > 0f && instances.Count > 0)
            {
                float mnX = float.MaxValue, mnZ = float.MaxValue, mxX = float.MinValue, mxZ = float.MinValue;
                for (int i = 0; i < instances.Count; i++)
                {
                    Vector3 p = instances[i].position;
                    if (p.x < mnX) mnX = p.x; if (p.x > mxX) mxX = p.x;
                    if (p.z < mnZ) mnZ = p.z; if (p.z > mxZ) mxZ = p.z;
                }
                placedHash = new SpatialHash(mnX - antiR, mnZ - antiR,
                    Mathf.Max(1f, mxX - mnX) + 2f * antiR, Mathf.Max(1f, mxZ - mnZ) + 2f * antiR,
                    Mathf.Max(0.5f, antiR));
                placedSpecies = new System.Collections.Generic.List<int>(instances.Count);
                arQuery = new System.Collections.Generic.List<int>(16);
            }

            for (int i = 0; i < instances.Count; i++)
            {
                // Break rather than return, so the list is left coherent - compacted to
                // what was actually processed - instead of half rewritten.
                if (stop != null && (i & 255) == 0 && stop.StopRequested) break;

                ScatterInstance inst = instances[i];

                Ecology.EnvironmentSample site =
                    ctx.sampler.Sample(inst.position.x, inst.position.z);

                // T-PLACE. The world-level placement law, applied here once per
                // candidate rather than folded into suitability: rock (the slope the
                // vegetation system reads as bare stone), the altitude backstop, and
                // the ground a road has cut all rule a site out for every species at
                // once. Kept out of the suitability score on purpose - a highly
                // suitable species must not be able to buy its way onto a cliff face
                // or a carriageway - and tested before the weights are built so a
                // rejected site never pays for the species solve.
                if (!Ecology.PlacementClearance.TerrainAdmits(in ctx.settings, in site))
                { rejections[0]++; continue; }

                if (!Ecology.PlacementClearance.CutRegionsAdmit(
                        ctx.cutRegions, ctx.cutRegionCount,
                        inst.position.x, inst.position.z, inst.position.y,
                        ctx.settings.avoidCutRegions, ctx.settings.clearanceM))
                { rejections[1]++; continue; }

                int admissible = Ecology.SpeciesSelector.BuildWeights(
                    table.envelopes, table.weights, table.dispersal,
                    count, in site, floor, sharpness, weights, suitabilities);

                // No species clears the floor here, so nothing is planted. The position
                // was accepted on slope and height alone; the environment overrules it.
                if (admissible <= 0) { rejections[2]++; continue; }

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

                    // VEG-8: penalise the species of any instance already placed within the
                    // anti-repeat radius, so the draw favours something different next door.
                    if (placedHash != null)
                    {
                        arQuery.Clear();
                        placedHash.QueryRadius(inst.position.x, inst.position.z, antiR, arQuery);
                        for (int k = 0; k < arQuery.Count; k++)
                            AntiRepeatPenalize(weights, count, placedSpecies[arQuery[k]], antiStrength);
                    }

                    chosen = Ecology.SpeciesSelector.SelectWeighted(
                        weights, count, pick.Random(hx, hz));
                }

                if (chosen < 0) { rejections[2]++; continue; }

                // T-PLACE. Now the winning species is known, the water half of the
                // law: a land plant is kept out of standing water and off the
                // waterline clearance band, while a species flagged marine or lake -
                // one with a stated submerged depth range - is admitted into the depth
                // it belongs in. This is where "no growth in a river or lake unless it
                // is an aquatic species" is enforced, and where the global avoidWater
                // switch can override a merely flood-tolerant species near the edge.
                if (!Ecology.PlacementClearance.WaterAdmits(
                        table.envelopes[chosen], in site,
                        ctx.settings.avoidWater, ctx.settings.clearanceM, channel == "underwater")) // UC-T06-11: explicit aquatic production retains the depth law under the global land guard.
                { rejections[3]++; continue; }

                // VEG-1 cross-layer occupancy. A crown placed by a HIGHER layer (an earlier
                // channel - trees run before shrubs before grass) has deposited leaf area into
                // the chunk canopy grid. A species flagged Deny is carved out of that footprint
                // (a hard hole under the crown), Thin is reduced in proportion to the leaf area
                // overhead, DontExclude (the default) ignores it. GEOMETRIC occupancy on top of
                // the soft shade scoring. Only acts when the canopy grid is bound (ecology canopy
                // on); otherwise skipped and placement is unchanged. Decision in MicrositeDenies.
                float occlusionThin = 1f;
                if (ctx.settings.canopyCompetition && ctx.canopy != null && ctx.canopy.IsBound) // UC-T08-15: no hidden geometric canopy exclusion when disabled.
                {
                    float laiOver = ctx.canopy.LaiAbove(inst.position.x, inst.position.z,
                                                        table.envelopes[chosen].canopyLayer);
                    if (MicrositeDenies(table.envelopes[chosen].micrositeExclusion, laiOver, out occlusionThin))
                    { rejections[4]++; continue; } // UC-T06-17: hard canopy rejection has its own observed bucket.
                }

                // V-13. The dart solve threw at the ceiling density, so the stand that
                // remains here is the ceiling times how well this ground suits the
                // species that won it. This is the step that makes a forest thin out
                // toward its limits instead of stopping along a contour.
                float siteFactor = Ecology.EcoDensity.SiteFactor(
                    suitabilities[chosen],
                    Ecology.BiomeAffinity.Of(table.envelopes[chosen], in site));

                // VEG-1 Thin: fewer survive the denser the leaf area overhead (1 when DontExclude
                // or no crown above, so the default path is unchanged).
                siteFactor *= occlusionThin;

                // V-24. Bare ground is asserted, not inferred: where a named cause
                // strips the ground - snow, scree, salt, scour, burn - it suppresses
                // the stand over a stated width, so the edge is a line you can point
                // at rather than a soft fade. Opt-in; off leaves placement untouched.
                if (ctx.settings.assertBareGround)
                {
                    Ecology.BareGroundState bare = Ecology.BareGround.Resolve(
                        in site, ctx.settings.maxSlopeDeg, thin);
                    siteFactor *= Ecology.BareGround.VegetationFactor(in bare);
                }

                if (!Ecology.EcoDensity.KeepAtSite(siteFactor, thin.Random(hx, hz)))
                { rejections[5]++; continue; }

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

                // §4O item 4. Age structure. A single-generation stand - every tree the same
                // size - is the synthetic look the author reported. Draw an age from a
                // reverse-J distribution (many young, few old) and fold it into the instance
                // scale, so a wood reads as saplings growing up among mature trees. Opt-in:
                // ageSizeVariation 0 (the default) multiplies by 1 and changes nothing.
                float ageMul = AgeSizeMultiplier(
                    ctx.settings.ageSizeVariation, ctx.settings.seedlingSizeFraction,
                    age.Random(hx, hz));
                if (ageMul < 0.999f)
                {
                    Vector3 sc = inst.scale;
                    sc.x *= ageMul; sc.y *= ageMul; sc.z *= ageMul;
                    inst.scale = sc;
                }

                instances[kept] = inst;
                kept++;

                // VEG-8: remember this placement so nearby later sites can avoid repeating it.
                if (placedHash != null)
                {
                    placedHash.Insert(inst.position.x, inst.position.z);
                    placedSpecies.Add(chosen);
                }

                // V-52. Tally where this survivor landed, from the same sample the
                // choice was made against - so the histogram is the admitted planting,
                // not a second classification that could disagree with the first. Cheap:
                // Classify is a handful of comparisons on fields the sample already holds.
                if (outputs != null)
                    outputs.AddZone((int)Ecology.LandformAssemblage.Classify(in site));
            }

            if (kept < instances.Count)
                instances.RemoveRange(kept, instances.Count - kept);

            // §4O item 3 (cross-layer occupancy / canopy occlusion). Now that selection is
            // finished, lay every survivor's CROWN into the chunk's shared canopy grid, so a
            // later pass (shrubs, then grass) that samples this same chunk reads the shade and
            // thins beneath the trees - the mechanism that stops layers interpenetrating.
            //
            // WHY A SECOND LOOP, NOT INLINE. Depositing inside the selection loop would let a
            // tree shade the trees placed after it in the SAME pass, coupling this layer's
            // layout to its own processing order. Doing it here, after every site in this pass
            // was already sampled, keeps the effect strictly CROSS-layer: this pass is
            // unchanged, only the passes that run after it feel the canopy. Empty/again-unbound
            // grids are no-ops, so a world with the canopy off is untouched.
            if (ctx.settings.canopyCompetition && ctx.canopy != null && ctx.canopy.IsBound)
                DepositCanopy(ctx, table, instances, canopyPublications);
        }

        /// <summary>
        /// Lays each kept instance's crown into the canopy grid. Crown radius comes from the
        /// same allometry the stems are built with (so the shade footprint matches the tree),
        /// sized by the instance's own suitability, which both are already carried on the
        /// instance. Wind exposure is passed as 0 here: krummholz dwarfing changes a stem's
        /// height far more than its shade footprint, and re-sampling the site just for that
        /// edge case is not worth a second environment read.
        /// </summary>
        private static void DepositCanopy(Ecology.EcoScatterContext ctx,
                                          Ecology.SpeciesTable table,
                                          List<ScatterInstance> instances, List<CanopyGraphStamp> publications)
        {
            int envCount = table.envelopes != null ? table.envelopes.Length : 0;

            for (int i = 0; i < instances.Count; i++)
            {
                ScatterInstance inst = instances[i];
                int sp = inst.prototypeIndex;
                if (sp < 0 || sp >= envCount) continue;

                Ecology.EcoEnvelope eco = table.envelopes[sp];
                if (eco == null || eco.laiContribution <= 0f) continue;

                // Position-hashed draw so the crown size is reproducible and uncorrelated
                // with scale/yaw, exactly as the base solver quantises before hashing.
                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);
                float draw = new Noise(sp + 2749).Random(hx, hz);

                Ecology.StemForm form = Ecology.Allometry.Resolve(
                    eco, draw, Mathf.Clamp01(inst.suitability), 0f);

                // VEG-4: stamp the TRUE crown radius onto the instance (world metres), so the
                // spacing/overlap/distance filters (VEG-2/VEG-3) use the real footprint of what
                // was placed. Reuses the form already resolved here - no extra allometry cost -
                // and writes it back onto the kept instance. Non-canopy instances keep 0, which
                // those filters read as "fall back to the node radius".
                inst.footprintRadius = form.crownRadiusM;

                // DEF-6: and the OTHER half of the same form. `form.heightM` is the species'
                // real mature height in metres, already resolved on the line above for the crown
                // radius - it used to be dropped here, which left the applier with nothing to
                // size a tree by and sent it back to the prefab's unit scale. Stamping it costs
                // one float and no extra allometry. Krummholz dwarfing is already folded into
                // `form.heightM` by Resolve, so a wind-flagged stem carries its reduced height.
                inst.targetHeightM = form.heightM;
                instances[i] = inst;

                ctx.canopy.Deposit(inst.position.x, inst.position.z,
                                   form.crownRadiusM, eco.laiContribution, eco.canopyLayer);
                publications.Add(new CanopyGraphStamp(inst.position.x, inst.position.z, form.crownRadiusM,
                    eco.laiContribution, eco.canopyLayer)); // UC-T02-07: reuse resolved allometry; warm evaluation must not rerun species selection.
            }
        }

        /// <summary>
        /// The age-driven size multiplier for one instance, as a pure function so the age
        /// structure can be self-tested. 0 variation returns 1 (every plant mature - the
        /// previous behaviour, so an existing world is untouched). Above 0, the age is drawn
        /// reverse-J (u squared → more young than old, the shape a real stand's size
        /// distribution takes), mapped from the seedling fraction up to full size, then blended
        /// in by the variation strength so the author dials how much spread a stand shows.
        /// </summary>
        internal static float AgeSizeMultiplier(float ageVariation, float seedlingFraction,
                                                float hash01)
        {
            if (ageVariation <= 0f) return 1f;

            float u = Mathf.Clamp01(hash01);
            float age01 = u * u;                                    // reverse-J: many young
            float sizeMul = Mathf.Lerp(Mathf.Clamp01(seedlingFraction), 1f, age01);
            return Mathf.Lerp(1f, sizeMul, Mathf.Clamp01(ageVariation));
        }

        /// <summary>
        /// VEG-1 cross-layer occupancy decision, as a pure function so it can be self-tested.
        /// Returns true to DENY placement (hard carve under a crown) and, for a Thin policy under
        /// a crown, sets <paramref name="thin"/> to a &lt;1 density multiplier; otherwise thin is 1
        /// and the result is false. DontExclude, or leaf area overhead at/below the threshold,
        /// always keeps placement unchanged.
        /// </summary>
        internal static bool MicrositeDenies(Ecology.MicrositeExclusion excl, float laiOver, out float thin)
        {
            thin = 1f;
            if (excl == Ecology.MicrositeExclusion.DontExclude) return false;
            if (laiOver <= Ecology.EcoConstants.MicrositeExclusionLaiThreshold) return false;
            if (excl == Ecology.MicrositeExclusion.Deny) return true;

            // Thin: reduce in proportion to how much light the overhead leaf area removes.
            thin = Ecology.EcoMath.BeerLambertTransmittance(
                Mathf.Min(laiOver, Ecology.EcoConstants.LeafAreaIndexMax));
            return false;
        }

        /// <summary>
        /// VEG-8 anti-repeat: scales down one species' selection weight because an instance of it
        /// was just placed nearby, so the next draw favours a different species. strength 1 excludes
        /// it entirely, 0 leaves it. Out-of-range indices are a safe no-op. Pure/self-testable.
        /// </summary>
        internal static void AntiRepeatPenalize(float[] weights, int count, int speciesIndex, float strength)
        {
            if (weights == null) return;
            if (speciesIndex < 0 || speciesIndex >= count || speciesIndex >= weights.Length) return;
            weights[speciesIndex] *= 1f - Mathf.Clamp01(strength);
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
