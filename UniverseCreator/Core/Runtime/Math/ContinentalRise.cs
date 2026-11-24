// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    ContinentalRise.cs
//  Purpose: The always-on rule that the interior of a continent STANDS ABOVE the
//           sea by an amount that grows with how far inland it is - so the world
//           can never contain a flat plain sitting at the waterline. Runs in the
//           CORE of height output, on the COMPOSED heightmap of every creator and
//           combiner, so no graph wiring and no procedural node can bypass it.
//
//  THE PROBLEM IT SOLVES (author report, 2026-10-05)
//  -------------------------------------------------
//  A river fought "climbing uphill" for two weeks. The deepest cause was not the
//  router: it was the GROUND. Noise creators happily produce a broad interior
//  plain at (or a hair above) sea level. A river crossing such a plain has NO
//  HYDRAULIC HEAD - there is no lower ground between it and the sea - so any
//  router, however correct, is asked for a descending path that does not exist.
//  It then either stalls, or a later "enforce downhill" correction shoves it
//  through whatever is nearest, which reads as the river climbing.
//
//  SeaLevelFloor is NOT this rule. It lifts land that has fallen BELOW sea, and
//  it deliberately skips anything already at-or-above sea:
//      if (h >= sea || ocean[i]) continue;
//  So a plain sitting exactly AT sea level is passed over untouched - legal by
//  that pass, and fatal to a river. This pass closes that hole.
//
//  THE RULE (distance-driven, the way a real coastal plain behaves)
//  ---------------------------------------------------------------
//      minimum(d) = sea + freeboard + maxRise * smoothstep(d / fullRiseDistance)
//
//  where d is the horizontal distance in METRES from the cell to the nearest
//  OCEAN cell. At the waterline the minimum is barely above sea (so a beach is
//  still a beach and CoastalShelf can still grade it); a kilometre inland the
//  ground is obliged to stand well above the sea. Every inland point therefore
//  has a downhill route to the ocean by CONSTRUCTION, which is the structural
//  form of Guarantee A - the river cannot be asked to climb because the terrain
//  it is given always falls toward the sea.
//
//  Smoothstep rather than linear on purpose: a linear ramp creases visibly at
//  the coast, and the author's description is that land "starts to increase" -
//  gentle near the water, firming up inland.
//
//  WHAT IT NEVER TOUCHES
//  ---------------------
//    - Genuine ocean (sub-sea and flood-reachable from a world edge): skipped,
//      so the sea keeps its own floor and depth. Raising it would erase the sea.
//    - Closed lake basins (pooled deeper than basinDepth): skipped, so a basin
//      the lake system is going to fill is not back-filled to a hillside.
//    - It is RAISE-ONLY. It can lift ground that is too low; it can never lower
//      ground, so it cannot undo a carve, flatten a mountain, or build a wall
//      across a valley by cutting one side down.
//
//  WHY IT NEEDS THE WHOLE WORLD
//  ----------------------------
//  "How far is the ocean" is a question the size of the map, and "is this the
//  ocean" is a connectivity question. Both are solved ONCE on the coarse
//  whole-world solve (WorldDrainage) and read back per chunk, which is also what
//  makes the result seam-free: a per-chunk distance field would restart at every
//  chunk border and step at the seams. Reuses the EXACT ocean classification
//  SeaLevelFloor owns and the EXACT coastline SDF CoastalShelf owns, so all three
//  passes agree on where the shore is - one definition, not three drifting ones.
//
//  Deterministic (pure function of the coarse height field - no RNG, no clock).
//  Managed C# over float[]/bool[]. No Unity API beyond Mathf; safe on a worker Task.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Obliges continental interior to stand above sea level by an amount that grows
    /// with distance from the ocean, so the world can never hold a plain at the
    /// waterline and every inland cell has a descending path to the sea. Raise-only;
    /// genuine ocean and closed lake basins are exempt. Applied in place.
    /// </summary>
    public static class ContinentalRise
    {
        /// <summary>
        /// Smallest surface area, in SQUARE METRES, a pooled depression must span before it
        /// counts as a LAKE basin rather than a dry hollow.
        ///
        /// It exists because of the author's correction "where there is no lake is not a
        /// basin": the pooled-depth test alone exempts every hollow from the dry-land floor,
        /// and an exempted dry hollow is free to settle level with the sea - the artifact the
        /// whole pass exists to prevent. One hectare is about the smallest standing water a
        /// world at this scale would render as a lake; anything smaller is treated as ground
        /// and held above the waterline like any other dry land.
        /// </summary>
        public const float MinLakeAreaM2 = 10000f;

        /// <summary>
        /// How much deeper than <c>basinDepth</c> a depression's floor must sit below its
        /// own spill level before it is treated as a LAKE basin rather than a flat plain in
        /// a shallow dip. A multiple of the hydrological basin threshold, so it tracks the
        /// same knob instead of inventing a second absolute depth.
        ///
        /// This is the author's "where there is no lake is not a basin" made quantitative:
        /// a concave basin deep enough to hold visible water is exempt from the dry-land
        /// floor (a lake will occupy it); a barely-pooled plain is NOT, so it is lifted
        /// clear of the sea and textured like ordinary ground instead of being left as a
        /// flat untextured pan at sea level.
        /// </summary>
        public const float LakeMinReliefFactor = 2f;

        /// <summary>
        /// §A6. Fraction of a basin's coarse cells that must fall under a real pushed lake
        /// footprint before the basin is treated as a lake and exempted from the dry-land floor.
        /// Below it, the depression is a dry pan with no lake over it and is lifted to a plain.
        /// A quarter is tolerant of the coarse grid and the footprint ring not lining up cell for
        /// cell, while still rejecting a basin a lake merely clips the edge of.
        /// </summary>
        public const float LakeCoverageFraction = 0.25f;

        /// <summary>
        /// Applies the inland rise to <paramref name="src"/> in place.
        /// <paramref name="wd"/> is a whole-world drainage solve of the same height
        /// branch (the one SeaLevelFloor already built); when it is null the pass is
        /// skipped and the height is left exactly as it was. Runs AFTER
        /// <see cref="SeaLevelFloor"/> and BEFORE <see cref="CoastalShelf"/> in the
        /// same output post-pass, on the same working copy.
        /// </summary>
        /// <param name="seaLevel">Normalised sea height (0.35 is the system default).</param>
        /// <param name="freeboard">
        /// Normalised clearance every dry cell keeps above sea even AT the waterline, so
        /// dry ground is never left sitting exactly at sea. Matches the same-named guard
        /// in <see cref="SeaLevelFloor"/>; keeping the two equal means the floor and this
        /// ramp meet at the shore instead of stepping.
        /// </param>
        /// <param name="maxRise">
        /// Normalised height the interior is obliged to stand above sea once it is
        /// <paramref name="fullRiseDistanceMetres"/> or more from the ocean. This is the
        /// head the whole river system descends through, so it must not be zero on a
        /// world that wants rivers.
        /// </param>
        /// <param name="fullRiseDistanceMetres">
        /// Distance inland, in METRES, over which the obligation grows from freeboard to
        /// the full <paramref name="maxRise"/>. Larger = a broader, gentler coastal plain.
        /// </param>
        /// <param name="basinDepth">
        /// Pooled depth above which a non-ocean depression counts as a real basin and is
        /// exempt (a lake will fill it). Pass the same value the floor uses.
        /// </param>
        /// <param name="strength">0..1 blend of the lift, 1 = full.</param>
        public static void Apply(WorldDrainage wd, Matrix src, float seaLevel,
                                 float freeboard, float maxRise,
                                 float fullRiseDistanceMetres, float basinDepth,
                                 float shoreBlendMetres, float shoreDeadzoneMetres,
                                 float[] lakeMask,
                                 float strength, StopToken stop)
        {
            if (wd == null || src == null || src.arr == null || strength <= 1e-5f) return;
            if (wd.height == null || wd.height.arr == null || wd.filled == null) return;

            float sea = Mathf.Clamp01(seaLevel);
            if (sea <= 1e-5f) return;                       // no sea: nothing to measure from

            float amt  = Mathf.Clamp01(strength);
            float free = Mathf.Max(0f, freeboard);
            float rise = Mathf.Max(0f, maxRise);
            float span = Mathf.Max(1f, fullRiseDistanceMetres);   // metres, never divide by 0
            if (rise <= 1e-6f && free <= 1e-6f) return;     // nothing asked for

            int resX = wd.resX, resZ = wd.resZ;
            int count = resX * resZ;
            if (count <= 0 || wd.height.arr.Length < count) return;
            if (wd.filled.Length < count) return;

            // 1) Ocean mask - the EXACT same classification SeaLevelFloor and CoastalShelf
            //    use, so no third definition of "the sea" can drift away from theirs.
            bool[] ocean = SeaLevelFloor.FloodOceanFromEdges(wd.height.arr, resX, resZ, sea);
            if (stop != null && stop.StopRequested) return;

            // 2) PER-BASIN LIFT - the universal form of the rule, and the reason a lake
            //     basin is exempted from the floor rather than from the pass.
            //
            //     WHY ANY OF THIS MATTERS (author, 2026-10-06): when the river trenches are
            //     cut procedurally and water is added, a reach that crosses ground sitting
            //     at sea level has NO HEAD. The trench then reads as climbing the hill, and
            //     at the coast the water looks like it is coming OUT of the ocean rather
            //     than feeding it. Guaranteeing dry land stands above the sea, and that a
            //     source lake stands above its own outflow, is what makes the river descend
            //     source -> mouth and feed the sea.
            //
            //     Author requirement, verbatim intent: the guard must hold "no matter where
            //     it finds it in the world" - not at one patch - and "it can move the lake
            //     basin up ... if there is a river drain and the lake is the source, the
            //     lake water should be at the same elevation as the river water so the
            //     water can move from high region to low to the ocean, not just the lake to
            //     be unnaturally low".
            //
            //     So a basin whose POOL SURFACE sits below sea+freeboard is translated
            //     upward as a rigid body until that surface clears it. Every cell of the
            //     basin moves by the SAME delta, so the basin keeps its exact shape and
            //     internal depth - no flattening, no terracing - while its water surface
            //     ends up above the sea and therefore able to feed a river that descends
            //     to the ocean. A basin already high enough moves by zero.
            //
            //     No cliff is created at the rim: the lifted surface lands exactly on
            //     sea+freeboard, and the dry land around it is held at or above that same
            //     figure by the floor below, so the two meet instead of stepping.
            float[] basinLift = BuildBasinLift(wd, ocean, sea, free, basinDepth,
                                               resX, resZ, count, MinLakeAreaM2, lakeMask,
                                               out bool[] isLakeBasin, stop);
            if (stop != null && stop.StopRequested) return;

            // NO BLIND BASINS (author 2026-10-10): without pushed lake footprints the terrain
            // keeps NO basin of its own guess. See SuppressBlindBasins for the full why.
            SuppressBlindBasins(lakeMask, isLakeBasin, basinLift, count);
            // UC-T08-15: accepted planes have fixed elevations. A terrain-only rigid lift cannot move their water or outlet.
            if (lakeMask != null) System.Array.Clear(basinLift, 0, basinLift.Length);

            // FIX 2026-10-19 (cliff ring around lakes): a basin is lifted as a rigid body by a
            // uniform delta, but the dry ground beside it is not. The lift field therefore steps
            // from `delta` to 0 at the basin rim - over one coarse cell once read onto the chunk -
            // which prints a vertical wall of `delta` metres around the lake (and a sunken-looking
            // lake against the unlifted hills). Blurring the coarse lift field spreads that step
            // into a ramp several cells wide. The rim cells sit at or above the spill level, so
            // easing the lift there cannot drown a bank; the lake interior keeps its full lift.
            basinLift = SmoothField(basinLift, resX, resZ, 8);

            // 2c) EXEMPT field: 1 where the dry-land floor must keep its hands off. Genuine
            //     ocean (or the sea is erased) and real LAKE basins (or a lake bed is
            //     clamped into a terraced shelf). Carried as a float so the per-chunk read
            //     feathers across the boundary instead of stepping cell to cell.
            //
            //     Note what is NOT exempt: a pooled hollow too small to hold a lake. That
            //     distinction is the author's - "where there is no lake is not a basin" -
            //     and without it a dry dip inherits the lake exemption and settles level
            //     with the sea, which is the artifact being chased.
            float[] exempt = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (ocean[i] || isLakeBasin[i] || lakeMask != null && i < lakeMask.Length && lakeMask[i] > 0.5f) exempt[i] = 1f;
            }
            if (stop != null && stop.StopRequested) return;
            if (stop != null && stop.StopRequested) return;

            // 3) Horizontal distance in METRES from every coarse cell to the nearest ocean
            //    cell, by the two-pass chamfer transform CoastalShelf already owns. Solved
            //    on the whole-world grid, which is what keeps the ramp continuous across
            //    chunk seams rather than restarting at every border.
            Vector2 pix = wd.height.PixelSize;
            float mppX = Mathf.Max(1e-3f, pix.x);
            float mppZ = Mathf.Max(1e-3f, pix.y);
            float[] dist = CoastalShelf.ChamferDistanceToOcean(ocean, resX, resZ,
                                                               mppX, mppZ, span);
            if (stop != null && stop.StopRequested) return;

            // 4) Read both coarse fields onto this chunk's fine lattice.
            //
            // SMOOTHED FIRST. Bilinear interpolation of a 256-cell field onto a 1025-pixel
            // chunk is piecewise-linear, so every coarse cell boundary is a slope
            // discontinuity - and because the lift below is driven by these fields, those
            // discontinuities print into the ground as the SERRATED, scalloped terrace ring
            // the author photographed around the whole coast (2026-10-06). A couple of
            // box-blur passes on the coarse grid (256x256, once per bake, not per pixel)
            // turn the field C1-smooth before it is ever sampled, which removes the facets
            // at their source rather than trying to sand them off the heightmap afterwards.
            float[] distS   = SmoothField(dist,   resX, resZ, 2);
            float[] exemptS = SmoothField(exempt, resX, resZ, 2);
            if (stop != null && stop.StopRequested) return;

            float[] chunkDist   = wd.ReadAtChunk(distS,   src, WorldRead.Bilinear);
            float[] chunkExempt = wd.ReadAtChunk(exemptS, src, WorldRead.Bilinear);
            float[] chunkBasin  = wd.ReadAtChunk(basinLift, src, WorldRead.Bilinear);
            // UC-T08-15: smoothing the transition must not dilute ownership inside an accepted lake.
            float[] chunkLake = lakeMask == null ? null : wd.ReadAtChunk(lakeMask, src, WorldRead.Bilinear);
            if (stop != null && stop.StopRequested) return;

            // The blend width of the smooth maximum below, in normalised height. Tied to
            // the freeboard so it scales with the guard itself: a 20 m freeboard eases over
            // roughly 20 m of relief instead of snapping.
            float softK = Mathf.Max(1e-5f, free);

            // SHORE RELEASE (author requirement 2026-10-07, from four screenshots of the
            // coast). The freeboard must NOT apply at the waterline.
            //
            // Before this, `hardMin` was sea + freeboard everywhere, so every coastal cell
            // was pushed a full 20 m above the sea - a 20 m wall around the entire world,
            // quantised by the coarse grid into the serrated cliff the author photographed.
            // The author's own statement of the rule: "at the shoreline the math does not
            // apply but gently meet the ground, but as for inner ground the math is okay".
            //
            // So the guard is released to ZERO at the water and ramps to its full value
            // inland over `shoreBlendMetres` - the same coastal band CoastalShelf grades as
            // beach and coastal plain, which is why the figure is passed in rather than
            // invented here: the two passes then agree on where the coast is. Land can
            // therefore meet the sea AT sea level (a beach), while the interior keeps the
            // head a river needs. Genuine sea cliffs remain possible because the pass is
            // raise-only and never lowers whatever the terrain already did.
            float shoreBlend = Mathf.Max(1f, shoreBlendMetres);

            // §A5 SHORE DEADZONE (author 2026-10-10, whole-world beach screenshots). Within
            // this many metres of the ocean the guard does NOT act at all - the whole-world
            // "unnatural beach / depression ring" was the near-shore band being lifted toward
            // the waterline and quantised by the coarse grid into a terrace. Land must meet the
            // sea exactly as the terrain drew it (a real beach slopes UNDER the water), so the
            // first ~80 m is released entirely; the head a river needs is an inland obligation
            // and loses nothing by it.
            float shoreDeadzone = Mathf.Max(0f, shoreDeadzoneMetres);

            // 5) Oblige the minimum, raise-only. A world with no ocean at all produces an
            //    all-"far" distance field, which would lift the entire map by maxRise; that
            //    is still internally consistent (everything rises together, so no false
            //    head appears), and a world that wants rivers without a sea has no shoreline
            //    to measure from in the first place.
            int n = src.arr.Length;
            for (int i = 0; i < n && i < chunkDist.Length && i < chunkExempt.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return;

                if (chunkLake != null && chunkLake[i] >= 0.999f) continue;
                float keepOff;

                // §A5 DEADZONE FIRST (author 2026-10-10). Within the near-shore band the rise
                // guard does NOTHING AT ALL - not the dry-land floor and not the basin rigid-
                // lift - so land meets the sea exactly as CoastalShelf graded it: a gentle beach
                // sloping into the water, never a lifted lip, step or depression. (Previously the
                // basin lift was applied before this check and could raise a near-shore basin
                // into a wall at the very shoreline the author wanted kept gentle.)
                float dM = chunkDist[i];
                if (dM <= shoreDeadzone) continue;

                keepOff = Mathf.Clamp01(chunkExempt[i]);

                // A basin moves as a rigid body: add its own lift and leave the shape alone.
                // Raise-only, and additive rather than clamped, so the basin keeps its internal
                // relief instead of terracing.
                if (i < chunkBasin.Length && chunkBasin[i] > 0f)
                    src.arr[i] = Mathf.Min(1f, src.arr[i] + chunkBasin[i]);

                if (keepOff >= 0.999f) continue;            // ocean / lake basin: no dry-land floor

                float h = src.arr[i];

                // SHORE RELEASE as a single band factor: 0 at the deadzone edge, 1 once we are
                // shoreBlend metres further inland, smoothstepped so the release itself creases
                // nothing. It scales the WHOLE obligation below - the freeboard AND the
                // distance-driven head - so the floor COLLAPSES TO THE BARE WATERLINE at the
                // coast, not to sea+freeboard.
                //
                // This is what makes the pass a GUARD at the shore and not a force (author
                // 2026-10-07/09, from the beach screenshots: "as long as the ground is above
                // the sea level do not touch ... it is a guard, not forced ... currently it
                // is forcing even the shoreline and beaches"). With the floor at sea near the
                // coast, any cell already standing above the sea satisfies `h >= floor` and is
                // skipped outright - a beach keeps exactly the slope the terrain gave it - and
                // only genuinely sub-sea ground is lifted, up toward the waterline and never
                // past it. The full head a river needs returns inland, beyond this band, where
                // raising ground cannot build a wall at the shore.
                float shoreT = Mathf.Clamp01((dM - shoreDeadzone) / shoreBlend);
                shoreT = shoreT * shoreT * (3f - 2f * shoreT);

                // Distance -> obligation. smoothstep(t) is gentle at the shore so the beach
                // and CoastalShelf's grade survive, and firm inland where the head matters.
                float t = Mathf.Clamp01(dM / span);
                float s = t * t * (3f - 2f * t);

                // The freeboard that applies HERE, and the floor, both scaled by the shore
                // release so they vanish at the water and reach full value inland.
                float freeHere = free * shoreT;
                float floor = Mathf.Min(1f, sea + shoreT * (free + rise * s));

                // THE GUARD, MADE LITERAL (author 2026-10-09: "as long as the ground is
                // above the sea level do not touch ... it is a guard, not forced"). Ground
                // that already stands above the sea must not be pushed HIGHER while it is in
                // the shore band - that upward push on dry shore is what built the raised lip
                // and cliff ring. So for an above-sea cell the floor is eased from the cell's
                // OWN height at the waterline (lerp t = 0 => floor = h => skipped) up to the
                // full obligation inland (t = 1). A sub-sea cell is untouched by this cap and
                // is still guarded up toward the waterline below, so a beach slopes into the
                // sea while the interior keeps the head a river needs.
                if (h >= sea)
                    floor = Mathf.Min(floor, Mathf.Lerp(h, floor, shoreT));

                // UC-T08-15: allow the compact blend to finish ABOVE the floor, avoiding the old ln(2)*k height jump.
                if (h >= floor + softK) continue;

                // Two-part lift. The HARD part guarantees dry land never stays below the
                // sea + (released) freeboard minimum no matter the strength dial. The SOFT
                // part is only the extra rise ABOVE that minimum, eased by strength and by
                // nearness to an exempt body so the slope doesn't wall a lake shore.
                //
                // SMOOTH MAXIMUM, not `h < min ? min : h`. A hard max is C0-discontinuous:
                // it flattens everything under the floor into a dead-level shelf and leaves
                // a crease where the terrain crosses it, which is the cliff/step ring the
                // author reported. The blend width shrinks with the released freeboard, so
                // at the waterline (freeHere = 0) it collapses to a plain max against `sea`
                // and adds no bump to the beach - softplus(0) would otherwise lift the
                // shore by k*ln2 and re-create the wall this release exists to remove.
                float hardMin = Mathf.Min(1f, sea + freeHere);
                float softKHere = Mathf.Max(1e-7f, Mathf.Min(softK, freeHere * 0.5f));
                float h2 = SoftMax(h, hardMin, softKHere);

                float lifted = h2 + (floor - h2) * amt * (1f - keepOff);
                float target = lifted > h2 ? lifted : h2;
                src.arr[i] = h + Mathf.Max(0, target - h) * (1 - keepOff); // accepted shore release applies to the whole lift.
            }
        }

        /// <summary>
        /// Compact C1 smooth maximum of normalized heights h and floor over a nonnegative blend width k.
        ///
        /// It asymptotes to `h` well above the floor and to `floor` well below it, but
        /// It equals the larger input outside the blend and adds a quadratic bridge inside it.
        /// Unlike the former softplus, its support is finite: Apply can skip satisfied high ground
        /// without a discontinuity. No exponentials or logarithms occur in the per-height hot path.
        /// </summary>
        internal static float SoftMax(float h, float floor, float k)
        {
            if (k <= 1e-7f) return h > floor ? h : floor;

            float bridge = Mathf.Max(0, k - Mathf.Abs(h - floor));
            return Mathf.Max(h, floor) + bridge * bridge / (4 * k);
        }

        /// <summary>
        /// How far each closed basin must rise for its WATER SURFACE to clear
        /// sea + freeboard, as a per-cell delta that is uniform within each basin.
        ///
        /// Why a rigid-body lift and not a clamp: a clamp flattens a basin into a shelf
        /// and creases its rim. Translating every cell of the basin by the same amount
        /// preserves its depth and shape exactly, so a lake stays a lake - it just sits
        /// high enough that its outflow can run downhill to the sea, which is the
        /// author's "the lake water should be at the same elevation as the river water"
        /// requirement. A basin whose surface already clears the mark gets zero.
        ///
        /// The pool surface is read from `filled`, which priority-flood sets to the
        /// basin's spill level - the elevation standing water in it actually reaches -
        /// so this is the real water surface and not an estimate. Basins are found by
        /// 4-neighbour flood fill over pooled, non-ocean cells, so EVERY basin in the
        /// world is handled wherever it happens to be: there is no special-cased region.
        /// </summary>
        internal static float[] BuildBasinLift(WorldDrainage wd, bool[] ocean, float sea,
                                               float free, float basinDepth,
                                               int resX, int resZ, int count,
                                               float minLakeAreaM2, float[] lakeMask,
                                               out bool[] isLakeBasin,
                                               StopToken stop)
        {
            float[] lift = new float[count];
            isLakeBasin = new bool[count];
            if (wd == null || wd.filled == null || wd.height == null) return lift;
            if (wd.height.arr == null || ocean == null) return lift;

            float target = Mathf.Min(1f, sea + Mathf.Max(0f, free));

            // How many cells a depression must span before it is a LAKE rather than a dry
            // dip. Author correction 2026-10-06: "where there is no lake is not a basin" -
            // a pooled cell on its own is not a lake bed, and exempting it from the floor
            // is exactly how a dry hollow ends up level with the sea. Converting a real
            // area to cells keeps this correct at every world size and solve resolution.
            float cellArea = wd.cellArea > 1e-6f ? wd.cellArea : 1f;
            int minLakeCells = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(0f, minLakeAreaM2) / cellArea));

            bool[] seen = new bool[count];
            int[] stack = new int[count];
            List<int> members = new List<int>(64);

            for (int start = 0; start < count; start++)
            {
                if (stop != null && (start & 8191) == 0 && stop.StopRequested) return lift;

                if (seen[start] || ocean[start]) continue;
                if (wd.filled[start] - wd.height.arr[start] <= basinDepth) continue;

                members.Clear();
                int top = 0;
                stack[top++] = start;
                seen[start] = true;

                // The basin's spill level, and its DEEPEST floor. Spill is the max filled
                // value across the region (priority-flood makes `filled` constant within one
                // basin). The deepest floor is the min raw height - it is what separates a
                // real lake basin from a flat plain sitting in a shallow dip.
                float surface = wd.filled[start];
                float deepestFloor = wd.height.arr[start];

                while (top > 0)
                {
                    int i = stack[--top];
                    members.Add(i);
                    if (wd.filled[i] > surface) surface = wd.filled[i];
                    if (wd.height.arr[i] < deepestFloor) deepestFloor = wd.height.arr[i];

                    int x = i % resX, z = i / resX;
                    if (x > 0)        TryBasin(wd, ocean, seen, stack, ref top, i - 1, basinDepth);
                    if (x < resX - 1) TryBasin(wd, ocean, seen, stack, ref top, i + 1, basinDepth);
                    if (z > 0)        TryBasin(wd, ocean, seen, stack, ref top, i - resX, basinDepth);
                    if (z < resZ - 1) TryBasin(wd, ocean, seen, stack, ref top, i + resX, basinDepth);
                }

                // Too small to hold a lake: NOT a basin. Left unmarked, so the dry-land
                // floor treats it as the hollow it is and it cannot settle at sea level.
                if (members.Count < minLakeCells) continue;

                // ⭐ THE "NO LAKE = NOT A BASIN" TEST (author report, the 3-week bug: a
                // depression is exempted as a lake basin so the floor skips it, but no lake
                // ever spawns there, so it is left as a dry pan at/below sea level - dragging
                // the area under the waterline and making a river crossing it read as climbing).
                //
                // When real lake footprints have been pushed in (lakeMask != null) they are
                // AUTHORITATIVE: a region is a lake basin ONLY where a spawned/placed lake
                // actually covers it. A sub-sea pit with no lake over it is not a basin - it
                // falls through unmarked and the dry-land floor below lifts it clear of the sea
                // as a plain, which is exactly the author's "where there is no lake is not a
                // basin ... move them up". Without footprints (a world's first bake before lakes
                // spawn, or no water manager) we fall back to the old concavity heuristic, so
                // behaviour is byte-unchanged until footprints exist.
                if (lakeMask != null)
                {
                    int hit = 0;
                    for (int m = 0; m < members.Count; m++)
                        if (members[m] < lakeMask.Length && lakeMask[members[m]] > 0.5f) hit++;

                    if ((float)hit / members.Count < LakeCoverageFraction) continue;
                }
                else
                {
                    // A real lake basin is CONCAVE: its deepest floor sits well below the level
                    // its water would stand at. A flat plain that merely lies in a broad shallow
                    // dip is not a lake - its floor is barely below its spill - so it falls
                    // through unmarked and is lifted by the floor like any other dry ground.
                    float relief = surface - deepestFloor;
                    if (relief < basinDepth * LakeMinReliefFactor) continue;
                }

                for (int m = 0; m < members.Count; m++) isLakeBasin[members[m]] = true;

                if (surface >= target) continue;            // already drains to the sea

                float delta = target - surface;
                for (int m = 0; m < members.Count; m++) lift[members[m]] = delta;
            }

            return lift;
        }

        /// <summary>
        /// NO BLIND BASINS (author 2026-10-10). The terrain height pass must NEVER keep a
        /// sub-sea depression as a "lake basin" on its own guess - that guess, left below the
        /// sea, is the "dry lake basin" artifact. A lake's bed comes from the LAKE OBJECT's
        /// carve (folded into every chunk by ChunkManager.SettleCarvers), not from the floor
        /// exempting a concavity it hopes a lake will fill.
        ///
        /// So when NO lake footprints have been pushed (lakeMask == null - the lake node is not
        /// connected / nothing placed) this clears every basin mark and lift, and the dry-land
        /// floor then raises every pan to a plain. When footprints ARE present the classification
        /// is already footprint-authoritative inside <see cref="BuildBasinLift"/>, so this is a
        /// no-op and a real lake keeps its bed exactly where it spawns.
        ///
        /// Why it lives HERE and not in BuildBasinLift: that routine is shared with the river
        /// node's "where is water already" query (RiverNetworkNodes.cs), which still wants the
        /// concavity classification. Suppressing in the terrain pass alone keeps ONE shared lake
        /// routine while letting the floor decline to invent a basin from it.
        /// </summary>
        internal static void SuppressBlindBasins(float[] lakeMask, bool[] isLakeBasin,
                                                 float[] basinLift, int count)
        {
            if (lakeMask != null) return;                       // real footprints: keep them
            if (isLakeBasin == null || basinLift == null) return;
            for (int i = 0; i < count && i < isLakeBasin.Length && i < basinLift.Length; i++)
            {
                isLakeBasin[i] = false;
                basinLift[i] = 0f;
            }
        }

        /// <summary>Flood-fill step: claims a pooled, non-ocean, unvisited neighbour.</summary>
        private static void TryBasin(WorldDrainage wd, bool[] ocean, bool[] seen,
                                     int[] stack, ref int top, int i, float basinDepth)
        {
            if (seen[i] || ocean[i]) return;
            if (wd.filled[i] - wd.height.arr[i] <= basinDepth) return;
            seen[i] = true;
            stack[top++] = i;
        }

        /// <summary>
        /// In-place-safe box blur of a coarse field, <paramref name="passes"/> times,
        /// returning a new array. Separable 3-tap per axis with clamped edges: cheap
        /// (O(cells) per pass on a 256x256 grid, once per bake) and enough to make a
        /// binary or chamfer field C1-smooth before it is sampled onto a fine chunk.
        ///
        /// This exists because sampling a coarse field bilinearly onto a much finer
        /// lattice reproduces every coarse-cell slope break as a visible facet; blurring
        /// the source is the cheapest place to remove them.
        /// </summary>
        internal static float[] SmoothField(float[] field, int resX, int resZ, int passes)
        {
            if (field == null || resX <= 2 || resZ <= 2) return field;

            int count = resX * resZ;
            if (field.Length < count) return field;

            float[] a = (float[])field.Clone();
            float[] b = new float[count];

            for (int p = 0; p < Mathf.Max(1, passes); p++)
            {
                // Horizontal 3-tap.
                for (int z = 0; z < resZ; z++)
                {
                    int row = z * resX;
                    for (int x = 0; x < resX; x++)
                    {
                        int xm = x > 0 ? x - 1 : 0;
                        int xp = x < resX - 1 ? x + 1 : resX - 1;
                        b[row + x] = (a[row + xm] + a[row + x] + a[row + xp]) * (1f / 3f);
                    }
                }

                // Vertical 3-tap.
                for (int z = 0; z < resZ; z++)
                {
                    int zm = z > 0 ? z - 1 : 0;
                    int zp = z < resZ - 1 ? z + 1 : resZ - 1;
                    int row = z * resX, rowM = zm * resX, rowP = zp * resX;
                    for (int x = 0; x < resX; x++)
                        a[row + x] = (b[rowM + x] + b[row + x] + b[rowP + x]) * (1f / 3f);
                }
            }

            return a;
        }
    }
}
