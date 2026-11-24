// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverNetworkExtractor.cs
//  Purpose: Turn the whole-world drainage solve into an actual RIVER NETWORK -
//           where channels begin, which channel flows into which, where they
//           join, and where they reach a lake or the sea.
//
//  WHY THIS FILE EXISTS (author report 2026-10-06)
//  ----------------------------------------------
//  The river rebuild shipped a MANUAL river: the author lays spline nodes and the
//  solver carves them. That was step 5's stated scope ("manual-first ... validates,
//  does NOT auto-route"). But the requirement is BOTH: rivers also placed
//  procedurally - trenches and networks that run until they drain into a lake or
//  the ocean, trenches that JOIN each other, and a floodplain - with the result
//  still editable by hand, which is how Unreal's water system behaves.
//
//  This is the missing half, and it deliberately adds NO new drainage algorithm.
//  WorldDrainage already solves the expensive parts once per bake and caches them:
//    - `filled`  : priority-flood depression-filled surface
//    - `dir`     : D8 downstream neighbour INDEX per cell (-1 = pit / flat)
//    - `accum`   : number of cells draining through each cell
//    - `cellArea`: square metres per cell
//  So everything here is graph bookkeeping over those arrays. Re-deriving flow
//  directions or accumulation would duplicate work the solve already did.
//
//  THE REAL-WORLD MATH (every step cited; nothing fitted by eye)
//  ------------------------------------------------------------
//  1. CHANNEL INITIATION - Montgomery & Dietrich (1992) slope-area law.
//     A channel head appears where contributing area and local slope together pass
//     a threshold:   A * S^theta >= C
//     Steep ground needs less catchment to cut a channel than flat ground does,
//     which is why headwater channels climb high on hillslopes but a flat plain
//     needs a large catchment before a channel forms. With theta = 0 this reduces
//     exactly to the classic critical-support-area criterion (Tarboton et al.
//     1991), so one implementation serves both and neither is guessed.
//
//  2. STREAM ORDERING - Strahler (1952). A reach with no channel upstream is
//     order 1; where two reaches meet, equal orders give order+1 and unequal
//     orders give the maximum. This is what makes a trunk river read as a trunk
//     and a headwater as a headwater, and it is the standard definition.
//
//  3. DISCHARGE - the rational method, Q = C * i * A, in its physical form
//     Q = runoff * A  where `runoff` is depth of water per second shed by the
//     catchment and A is drainage area in square metres. Deliberately NOT a
//     fitted regional "Q = k*A^0.75" curve: that hides climate inside a constant,
//     while runoff depth is a real quantity the climate/weather layer can supply.
//
//  4. CHANNEL SIZE - Leopold & Maddock (1953) downstream hydraulic geometry, with
//     the Shields grain-stability floor. NOT re-derived here: it is delegated to
//     RiverCourseSolver.HydraulicGeometry, which already owns those coefficients.
//
//  5. FLOODPLAIN WIDTH - scaled from bankfull width. A real river's active
//     floodplain / meander belt is a multiple of its bankfull width (Leopold's
//     meander-belt scaling, ~10-20x), so the belt is expressed as that multiple
//     rather than as an absolute metre figure that would be wrong at every other
//     river size.
//
//  Managed C# over float[]/int[]. No Unity API beyond Mathf; safe on a worker Task.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Authored thresholds for procedural river extraction. All real-world units.
    /// </summary>
    [System.Serializable]
    public struct RiverNetworkOptions
    {
        /// <summary>
        /// Minimum drainage area, in SQUARE METRES, before a channel may begin at all.
        /// A hard floor under the slope-area law so a single steep cell cannot spawn a
        /// river. 1 km^2 (1e6 m^2) is a reasonable small-stream catchment.
        /// </summary>
        public float minDrainageAreaM2;

        /// <summary>
        /// Slope exponent `theta` in the Montgomery-Dietrich law A * S^theta >= C.
        /// 0 disables the slope term and leaves a pure critical-support-area rule.
        /// Field studies cluster around 1..2 for debris-flow headwaters.
        /// </summary>
        public float slopeExponent;

        /// <summary>
        /// Threshold `C` of the slope-area law, in the same units as A * S^theta.
        /// Larger = fewer, longer channels; smaller = a denser network.
        /// </summary>
        public float slopeAreaThreshold;

        /// <summary>
        /// Runoff depth shed by the catchment, in METRES PER SECOND, for the rational
        /// method Q = runoff * A. 1e-8 m/s is ~315 mm/year of effective runoff, a
        /// temperate-catchment figure; an arid world uses less, a monsoon world more.
        /// </summary>
        public float runoffMetresPerSecond;

        /// <summary>
        /// Floodplain / meander-belt width as a MULTIPLE of bankfull width (Leopold's
        /// meander-belt scaling is roughly 10-20x). Used for the floodplain band.
        /// </summary>
        public float floodplainWidthMultiple;

        /// <summary>Reaches shorter than this many cells are discarded as noise.</summary>
        public int minReachCells;

        /// <summary>
        /// Re-derive <see cref="slopeAreaThreshold"/> from THIS world's own slope scale
        /// instead of using it as an absolute number.
        ///
        /// WHY. `C` is an area: at the world's own mean along-flow slope it is the
        /// catchment that just begins to cut a channel. The absolute default (1e5 m^2) is
        /// an Earth figure, chosen against a 20 km+ landscape. Applied literally to a 4 km
        /// game world with a mean slope of 0.05 it demands A >= 2e6 m^2 - an eighth of the
        /// entire world - for a single channel head, so a perfectly good heightfield
        /// produces no network at all. The same reason the node scales the initiation area
        /// and the runoff applies here; it was simply left out of `C`.
        ///
        /// WHEN ON, `C` is replaced by `minDrainageAreaM2 * meanSlope^theta`. A catchment
        /// equal to the initiation area at the world's mean slope then just qualifies,
        /// steeper ground qualifies with less catchment and flat ground with more - which
        /// is exactly the Montgomery-Dietrich behaviour, expressed relative to the world
        /// being built instead of to one particular planet.
        /// </summary>
        public bool scaleThresholdToWorld;

        /// <summary>
        /// A cell draining at least this MULTIPLE of the minimum area is a channel whatever its
        /// local slope. 0 = off (the pure slope-area law). FIX 2026-10-19: on the lowland between
        /// the mountains and the coast the slope is small, so A x S never reached C and every river
        /// died at the foothills - leaving trenches only where nobody looks, or none at all. A big
        /// catchment is a river on a flat floor too.
        /// </summary>
        public float trunkAreaMultiple;

        /// <summary>Sensible defaults for a temperate world. Every value is authorable.</summary>
        public static RiverNetworkOptions Default()
        {
            RiverNetworkOptions o = new RiverNetworkOptions();
            o.minDrainageAreaM2        = 1e6f;    // 1 km^2
            o.slopeExponent            = 1f;
            o.slopeAreaThreshold       = 1e5f;
            o.runoffMetresPerSecond    = 1e-8f;   // ~315 mm/yr effective runoff
            o.floodplainWidthMultiple  = 12f;
            o.minReachCells            = 3;
            // OFF by default: the absolute law stays the reference behaviour for direct
            // callers and tests. The graph node turns it on with `scaleToWorld`, because
            // that is the mode in which the other absolute numbers are replaced too.
            o.scaleThresholdToWorld    = false;
            return o;
        }
    }

    /// <summary>How a reach ends. Drives what the carve does at its last cell.</summary>
    public enum RiverReachEnd
    {
        /// <summary>Flows into another reach: a confluence. Bed must match at the join.</summary>
        Confluence = 0,
        /// <summary>Reaches a lake or the sea: a mouth. Bed blends to that body's surface.</summary>
        Water = 1,
        /// <summary>Ends in a pit with no outlet: an endorheic sink (a lake site).</summary>
        Sink = 2,
        /// <summary>Runs off the edge of the solved world.</summary>
        WorldEdge = 3
    }

    /// <summary>
    /// One channel link: an unbroken run of cells from a head or a confluence down to
    /// the next confluence, mouth or sink. Cell indices are into the coarse solve grid.
    /// </summary>
    public sealed class RiverReach
    {
        /// <summary>Flow-ordered cell indices, upstream first.</summary>
        public List<int> cells = new List<int>();

        /// <summary>Strahler order. 1 is a headwater.</summary>
        public int order = 1;

        /// <summary>Index into the network's reach list this one flows into, or -1.</summary>
        public int downstream = -1;

        /// <summary>What terminates this reach.</summary>
        public RiverReachEnd end = RiverReachEnd.Sink;

        /// <summary>Drainage area at the downstream end, square metres.</summary>
        public float outletAreaM2;

        /// <summary>Discharge at the downstream end, cubic metres per second.</summary>
        public float outletDischargeM3PerS;

        /// <summary>Bankfull HALF-width at the outlet, metres (Leopold, via the solver).</summary>
        public float outletHalfWidthM;

        /// <summary>Bankfull depth at the outlet, metres (Leopold + Shields floor).</summary>
        public float outletDepthM;

        /// <summary>Floodplain half-width at the outlet, metres.</summary>
        public float outletFloodplainHalfWidthM;

        // UC-T03-05: identities refer to continuous courses rather than confluence-split links.
        /// <summary>Selected hierarchy: 0 trunk, 1 river, 2 stream; -1 when selection is disabled.</summary>
        public int hierarchyLevel = -1;
        /// <summary>Stable course-head cell in this solve, or -1 without hierarchy selection.</summary>
        public int courseId = -1;
        /// <summary>Receiving course-head cell in this solve, or -1 for a trunk/unselected network.</summary>
        public int parentCourseId = -1;
    }

    /// <summary>
    /// The extracted network: reaches plus the grid they were measured on. Pure data -
    /// carving, meshing and authoring all consume it, none of them live in here.
    /// </summary>
    public sealed class RiverNetwork
    {
        public List<RiverReach> reaches = new List<RiverReach>();
        public int resX;
        public int resZ;

        /// <summary>UC-T03-05: authored target resolution and achieved counts; null when disabled.</summary>
        public RiverHierarchyReport hierarchy;

        /// <summary>Highest Strahler order present, 0 when the network is empty.</summary>
        public int MaxOrder()
        {
            int m = 0;
            for (int i = 0; i < reaches.Count; i++)
                if (reaches[i].order > m) m = reaches[i].order;
            return m;
        }
    }

    /// <summary>
    /// Extracts a river network from an existing whole-world drainage solve. See the
    /// file header for the cited math; this class adds no new drainage algorithm.
    /// </summary>
    public static partial class RiverNetworkExtractor // UC-T03-05: selection shares the existing extraction owner.
    {
        /// <summary>
        /// Builds the network. <paramref name="wd"/> MUST have been solved with
        /// needFlow: true, or `dir`/`accum` are null and there is nothing to trace -
        /// in which case this returns null rather than silently inventing a network.
        ///
        /// <paramref name="waterCells"/> marks cells already covered by a lake or the
        /// sea, so a reach that enters one terminates as a MOUTH instead of running on
        /// through the water. Pass null to treat the whole grid as land.
        /// </summary>
        public static RiverNetwork Extract(WorldDrainage wd, RiverNetworkOptions opt,
                                           bool[] waterCells, StopToken stop)
        { return Extract(wd, opt, waterCells, null, stop); } // UC-T03-07: preserve the existing API/method group.

        /// <summary>UC-T03-07: extracts the same network while retaining selected valid lake outflow
        /// paths below ordinary initiation thresholds. Protected water remains excluded.</summary>
        /// <param name="wd">Existing finite drainage lattice with flow and accumulation.</param>
        /// <param name="opt">Authored initiation, discharge and reach controls.</param>
        /// <param name="waterCells">Optional protected water flags on the solve lattice.</param>
        /// <param name="lakeSources">Validated sources from ResolveLakeOutlets on this same solve; null preserves threshold-only extraction.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Connected network, or null for missing solve/cancellation.</returns>
        /// <exception cref="System.ArgumentException">A selected source is outside the solve or protected by water.</exception>
        /// <exception cref="System.InvalidOperationException">A selected lake path climbs or fails to reach receiving water.</exception>
        public static RiverNetwork Extract(WorldDrainage wd, RiverNetworkOptions opt,
                                           bool[] waterCells, IReadOnlyList<LakeOutletSource> lakeSources, StopToken stop)
        {
            if (wd == null || wd.dir == null || wd.accum == null) return null;
            if (wd.height == null || wd.height.arr == null || wd.filled == null) return null;

            int resX = wd.resX, resZ = wd.resZ;
            int count = resX * resZ;
            if (count <= 0 || wd.dir.Length < count || wd.accum.Length < count) return null;
            if (wd.filled.Length < count) return null;

            bool haveWater = waterCells != null && waterCells.Length >= count;
            float cellArea = wd.cellArea > 1e-6f ? wd.cellArea : 1f;

            // Metres per cell, for the along-flow slope. PixelSize is the coarse grid's
            // world step, so a diagonal D8 step is sqrt(2) longer than an axial one.
            Vector2 pix = wd.height.PixelSize;
            float mppX = Mathf.Max(1e-3f, pix.x);
            float mppZ = Mathf.Max(1e-3f, pix.y);

            // THE HEIGHT FIELD IS NORMALISED; THE FLOW LAW IS NOT.
            // ------------------------------------------------
            // `filled` holds 0..1 samples, but a slope is metres per metre and the
            // Montgomery-Dietrich threshold C is an AREA in square metres. Dividing a
            // normalised drop by a run in metres yields a slope that is wrong by the whole
            // world height - on a 600 m world, ~600x too small - so `A * S` never reaches
            // any sane C and NO cell is ever a channel. The network then comes back empty,
            // the node passes the height through untouched, and the author sees "I
            // connected the nodes and nothing spawns". One factor, the whole feature.
            //
            // The carve in RiverNetworkNodes already multiplies by worldHeightM for exactly
            // this reason; the extractor is the one place that was left without it. The
            // self-test could not see it because the confluence gate sets theta = 0, which
            // deletes the slope term from the law it was checking.
            float worldHeightM = wd.height.worldHeight > 1e-3f ? wd.height.worldHeight : 1f;

            // ---------------------------------------------------------------------
            // 1) CHANNEL MASK - Montgomery & Dietrich (1992): A * S^theta >= C, with a
            //    hard minimum drainage area under it. Slope is taken ALONG THE FLOW
            //    PATH (cell to its own D8 downstream neighbour), which is the slope the
            //    law is written for, and is free given `dir`.
            //
            //    Two passes. The first measures every draining cell's slope in real
            //    metres per metre and accumulates their mean; the second applies the
            //    law. The split exists so <see cref="RiverNetworkOptions.scaleThresholdToWorld"/>
            //    can derive C from the world's own slope scale instead of an absolute
            //    Earth figure - and it costs one extra linear scan, no more.
            // ---------------------------------------------------------------------
            bool[] channel = new bool[count];
            float[] slope  = new float[count];
            float minArea  = Mathf.Max(0f, opt.minDrainageAreaM2);
            float theta    = Mathf.Max(0f, opt.slopeExponent);
            float cThresh  = Mathf.Max(0f, opt.slopeAreaThreshold);

            double slopeSum = 0;
            int slopeCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                // WATER IS NEVER CHANNEL. This one exclusion is what makes the network
                // behave the way a real drainage net does, and it settles four things at
                // once (author requirements, 2026-10-06):
                //   - A trench cannot CUT a lake: there is no channel inside one, so the
                //     carve has nothing to dig there.
                //   - A trench cannot BISECT the ocean for the same reason; a reach that
                //     arrives stops at the waterline as a mouth.
                //   - A lake gets an OUTFLOW river. The cell just downstream of a lake now
                //     has no channel upstream of it, so it reads as a channel HEAD and
                //     starts its own reach - which is exactly "the lake is the source of
                //     the river draining out". Without this exclusion that cell inherited
                //     an upstream channel, was not a head, and no outflow was ever traced.
                //   - The river therefore KNOWS where the lakes are, because the same water
                //     mask the terrain passes use is what defines the channel mask here.
                if (haveWater && waterCells[i]) continue;

                // DIRECTION CODES, NOT CELL INDICES. See Hydrology.DownstreamCell: `dir`
                // stores the neighbour 0..7, and reading it as a cell index silently
                // samples row 0 instead - the slope then reads 0 everywhere and no cell
                // is ever a channel.
                int d = Hydrology.DownstreamCell(wd.dir, i, resX, resZ);
                if (d < 0) continue;                     // pit or flat: no channel here

                // Along-flow slope from the depression-FILLED surface, so a filled sink
                // reads as the flat it hydrologically is rather than as a cliff. Scaled to
                // real metres: this is the unit fix described above.
                float dz = (wd.filled[i] - wd.filled[d]) * worldHeightM;
                if (dz < 0f) dz = 0f;

                int ddx = (d % resX) - (i % resX);
                int ddz = (d / resX) - (i / resX);
                float runX = ddx * mppX;
                float runZ = ddz * mppZ;
                float run = Mathf.Sqrt(runX * runX + runZ * runZ);
                float s = run > 1e-4f ? dz / run : 0f;
                slope[i] = s;

                if (s > 0f) { slopeSum += s; slopeCount++; }
            }

            // C relative to this world: a catchment of `minArea` at the world's mean slope
            // just qualifies. With no measurable slope anywhere (a dead-flat world) the
            // authored value stands - there is no scale to derive from.
            if (opt.scaleThresholdToWorld && slopeCount > 0)
            {
                float meanSlope = (float)(slopeSum / slopeCount);
                cThresh = minArea * Mathf.Pow(meanSlope, theta);
            }

            for (int i = 0; i < count; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;
                if (haveWater && waterCells[i]) continue;

                int d = Hydrology.DownstreamCell(wd.dir, i, resX, resZ);
                if (d < 0) continue;

                float areaM2 = wd.accum[i] * cellArea;
                if (areaM2 < minArea) continue;

                // theta = 0 collapses S^theta to 1, leaving the pure support-area rule.
                float term = theta <= 1e-6f ? areaM2 : areaM2 * Mathf.Pow(slope[i], theta);
                if (term >= cThresh) channel[i] = true;
                else if (opt.trunkAreaMultiple > 0f && areaM2 >= minArea * opt.trunkAreaMultiple) channel[i] = true;
            }
            if (stop != null && stop.StopRequested) return null;

            // UC-T03-07: one union of selected dry routes. Shared tails are traversed once;
            // normal head/confluence extraction then prevents duplicate reaches on regeneration.
            bool[] lakeChannel = lakeSources != null ? new bool[count] : null;
            int[] lakeRouteOwner = lakeSources != null ? new int[count] : null;
            int sourceTag = 0;
            if (lakeSources != null)
                foreach (LakeOutletSource source in lakeSources)
                {
                    if (source == null || !source.Selected) continue;
                    sourceTag++;
                    int cell = source.SourceCell, visited = 0;
                    if (!source.HasValidSpill || cell < 0 || cell >= count || (haveWater && waterCells[cell]))
                        throw new System.ArgumentException("Invalid selected lake source cell.");
                    while (cell >= 0 && (!haveWater || !waterCells[cell]) && !lakeChannel[cell])
                    {
                        if (stop != null && (visited & 1023) == 0 && stop.StopRequested) return null;
                        if (++visited > count) throw new System.InvalidOperationException("Cyclic lake source route.");
                        lakeChannel[cell] = true; channel[cell] = true; lakeRouteOwner[cell] = sourceTag;
                        int next = Hydrology.DownstreamCell(wd.dir, cell, resX, resZ);
                        if (next < 0 || wd.filled[next] > wd.filled[cell] ||
                            ((!haveWater || !waterCells[next]) && wd.height.arr[next] > wd.height.arr[cell] + 4e-7f))
                            throw new System.InvalidOperationException("Lake source route cannot climb or end before receiving water.");
                        cell = next;
                    }
                    if (cell >= 0 && lakeRouteOwner[cell] == sourceTag)
                        throw new System.InvalidOperationException("Cyclic selected lake source route.");
                }

            // ---------------------------------------------------------------------
            // 2) How many channel cells flow INTO each channel cell. A cell fed by two
            //    or more is a confluence, which is where one reach ends and the next
            //    begins - that is what makes trenches JOIN rather than cross.
            // ---------------------------------------------------------------------
            int[] inDegree = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (!channel[i]) continue;
                int d = Hydrology.DownstreamCell(wd.dir, i, resX, resZ);
                if (d >= 0 && channel[d]) inDegree[d]++;
            }

            // ---------------------------------------------------------------------
            // 3) Trace reaches. A reach starts at a channel HEAD (no channel feeding
            //    it) or AT a confluence, and runs downstream until it hits the next
            //    confluence, water, a pit or the world edge.
            // ---------------------------------------------------------------------
            RiverNetwork net = new RiverNetwork();
            net.resX = resX;
            net.resZ = resZ;

            // Which reach BEGINS at a given cell. Lets a reach that ends on a confluence
            // find the reach it feeds without searching.
            Dictionary<int, int> reachStartingAt = new Dictionary<int, int>();
            bool[] consumed = new bool[count];

            List<int> starts = new List<int>();
            for (int i = 0; i < count; i++)
                if (channel[i] && inDegree[i] == 0) starts.Add(i);

            // Confluence cells are reach starts too: the junction begins a new, higher
            // order reach. Added after the heads so heads are traced first and the
            // junction is still free to open the trunk below it.
            for (int i = 0; i < count; i++)
                if (channel[i] && inDegree[i] >= 2) starts.Add(i);

            for (int sIdx = 0; sIdx < starts.Count; sIdx++)
            {
                if (stop != null && stop.StopRequested) return null;

                int c = starts[sIdx];
                if (c < 0 || c >= count || consumed[c]) continue;

                RiverReach reach = new RiverReach();
                int guard = 0;
                bool containsLakeChannel = false; // UC-T03-07: a selected spill may lie below an ordinary channel head.

                while (c >= 0 && c < count && channel[c] && !consumed[c] && guard++ <= count)
                {
                    consumed[c] = true;
                    reach.cells.Add(c);
                    containsLakeChannel |= lakeChannel != null && lakeChannel[c];

                    // Already in water: this cell IS the mouth, stop here.
                    if (haveWater && waterCells[c])
                    {
                        reach.end = RiverReachEnd.Water;
                        break;
                    }

                    int d = Hydrology.DownstreamCell(wd.dir, c, resX, resZ);

                    if (d < 0)
                    {
                        // No downstream cell at all: an endorheic sink, i.e. a lake site.
                        reach.end = RiverReachEnd.Sink;
                        break;
                    }

                    if (haveWater && waterCells[d])
                    {
                        // Next cell is the lake/sea: include it so the trench reaches the
                        // waterline with no dry gap, and end as a mouth.
                        reach.cells.Add(d);
                        reach.end = RiverReachEnd.Water;
                        break;
                    }

                    if (!channel[d])
                    {
                        // Flow leaves the channel network without reaching water: the
                        // network ran to the edge of the solved grid.
                        reach.cells.Add(d);
                        reach.end = RiverReachEnd.WorldEdge;
                        break;
                    }

                    if (inDegree[d] >= 2)
                    {
                        // A confluence: include the junction cell so this reach and the
                        // trunk share a vertex (no gap in the trench), then stop.
                        reach.cells.Add(d);
                        reach.end = RiverReachEnd.Confluence;
                        break;
                    }

                    c = d;
                }

                // UC-T03-07: a real short spill connector is not threshold noise.
                if (reach.cells.Count < Mathf.Max(1, opt.minReachCells) && !containsLakeChannel) continue;

                int reachIndex = net.reaches.Count;
                net.reaches.Add(reach);
                if (!reachStartingAt.ContainsKey(reach.cells[0]))
                    reachStartingAt[reach.cells[0]] = reachIndex;
            }
            if (stop != null && stop.StopRequested) return null;

            // ---------------------------------------------------------------------
            // 4) Link reaches: a reach ending at a confluence flows into whichever reach
            //    STARTS at that junction cell.
            // ---------------------------------------------------------------------
            for (int r = 0; r < net.reaches.Count; r++)
            {
                RiverReach reach = net.reaches[r];
                if (reach.end != RiverReachEnd.Confluence || reach.cells.Count == 0) continue;

                int junction = reach.cells[reach.cells.Count - 1];
                int target;
                if (reachStartingAt.TryGetValue(junction, out target) && target != r)
                    reach.downstream = target;
            }

            // ---------------------------------------------------------------------
            // 5) STRAHLER ORDER (1952). Resolved by repeated relaxation rather than
            //    recursion: a reach's order follows from its tributaries', and the
            //    network is a DAG, so a bounded number of sweeps settles it. The sweep
            //    count is bounded by reach count so a malformed link cannot spin forever.
            // ---------------------------------------------------------------------
            int n = net.reaches.Count;
            int[] maxTrib = new int[n];
            int[] tribCount = new int[n];

            for (int sweep = 0; sweep <= n; sweep++)
            {
                if (stop != null && stop.StopRequested) return null;

                for (int i = 0; i < n; i++) { maxTrib[i] = 0; tribCount[i] = 0; }

                for (int i = 0; i < n; i++)
                {
                    int d = net.reaches[i].downstream;
                    if (d < 0 || d >= n) continue;
                    int o = net.reaches[i].order;
                    if (o > maxTrib[d]) { maxTrib[d] = o; tribCount[d] = 1; }
                    else if (o == maxTrib[d]) tribCount[d]++;
                }

                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    // No tributary: a headwater, order 1. Otherwise the highest incoming
                    // order, +1 only when at least two tributaries share that order.
                    int want = maxTrib[i] == 0 ? 1
                             : (tribCount[i] >= 2 ? maxTrib[i] + 1 : maxTrib[i]);
                    if (net.reaches[i].order != want) { net.reaches[i].order = want; changed = true; }
                }
                if (!changed) break;
            }

            // ---------------------------------------------------------------------
            // 6) Hydrology per reach outlet: area -> discharge (rational method) ->
            //    channel size (Leopold + Shields, via the existing solver) -> floodplain.
            // ---------------------------------------------------------------------
            float runoff = Mathf.Max(0f, opt.runoffMetresPerSecond);
            float beltMul = Mathf.Max(1f, opt.floodplainWidthMultiple);

            for (int r = 0; r < n; r++)
            {
                RiverReach reach = net.reaches[r];
                if (reach.cells.Count == 0) continue;

                int outlet = reach.cells[reach.cells.Count - 1];
                if (outlet < 0 || outlet >= count) continue;

                reach.outletAreaM2 = wd.accum[outlet] * cellArea;

                // Rational method in physical form: depth of runoff per second over the
                // catchment area. No fitted regional exponent is involved.
                reach.outletDischargeM3PerS = runoff * reach.outletAreaM2;

                float halfW, depth;
                RiverCourseSolver.HydraulicGeometry(reach.outletDischargeM3PerS,
                                                    slope[outlet], out halfW, out depth);
                reach.outletHalfWidthM = halfW;
                reach.outletDepthM = depth;

                // Meander-belt scaling off BANKFULL width (2 * half-width), kept as a
                // multiple so it stays correct at every river size.
                reach.outletFloodplainHalfWidthM = halfW * beltMul;
            }

            return net;
        }
    }
}
