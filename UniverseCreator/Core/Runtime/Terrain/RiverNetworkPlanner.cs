// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverNetworkPlanner.cs
//  Purpose: Works out where a world's rivers actually run, by reading the
//           landscape's own drainage instead of guessing.
//
//  WHAT THIS FILE IS NOT
//  ---------------------
//  It is not a river builder and it does not shape anything. UCWaterBody.WorldCourse
//  and RiverCourseSolver already do that, and they do it well: centripetal
//  Catmull-Rom through the nodes, resample, valley seek, a descent walk that swings
//  round high ground, a taper where the water sinks. None of that is duplicated here.
//
//  What that machinery cannot do is invent a course. It is handed one and it fits it.
//  So this file answers the only two questions it cannot answer for itself: WHERE
//  DOES A RIVER START and WHERE DOES IT END.
//
//  WHY IT WAS NEEDED
//  -----------------
//  The seed it used to be handed was the highest of twenty random samples, followed
//  by eight-direction steepest-descent steps that stopped the moment a step failed.
//  On any real heightfield that is the first shallow pit, a hundred metres in - so
//  the "river" was two or three points long, ended nowhere, and had no mouth. Fitting
//  a good spline to a stub still leaves a stub.
//
//  HOW DRAINAGE ANSWERS BOTH QUESTIONS
//  -----------------------------------
//  This is the standard digital-terrain drainage chain, the same one the graph's
//  hydrology nodes run, applied to the whole world at once instead of per tile:
//
//    1. SAMPLE     the settled terrain onto a grid, in metres
//    2. FILL       priority-flood every closed depression to its spill elevation,
//                  so there is no pit left for a walk to die in - this alone is why
//                  the old trace stopped and this one does not
//    3. D8         each cell gets its steepest-descent neighbour
//    4. ACCUMULATE how many cells drain through each one. That number IS discharge,
//                  and multiplied by the cell area it is the catchment in m2
//
//  A river MOUTH is then a land cell that drains straight into standing water - the
//  sea, or a lake. Rank those by accumulation and the largest are, by definition, the
//  trunk rivers of the world: the biggest catchments end at the biggest mouths.
//
//  A lake's DRAIN-OUT river is the same reasoning run from the other end. A lake with
//  an inflow and no outflow is a sink, not a lake - it would rise forever. Real lakes
//  above sea level overflow at their lowest rim point, and the fill step has already
//  found that point for every basin, because the elevation it raised a basin to IS its
//  spill elevation. So the outlet's source needs no search either.
//
//  A river SOURCE is found by walking back up from the mouth, at each step taking the
//  upstream neighbour that contributes the most water, until the contributing area
//  falls below what a permanent channel needs. That walk traces the main stem exactly
//  the way a hydrologist names a river's source - follow the largest tributary at
//  every confluence. It cannot wander onto a hillside, because every cell on it
//  genuinely drains to the mouth.
//
//  So the course is continuous by construction (it is a flow path), it never climbs
//  (D8 on a filled surface cannot), and it ends in real water (the mouth was chosen
//  that way). Those are three of the reported faults, fixed at the source rather than
//  patched downstream.
//
//  DETERMINISTIC
//  -------------
//  Not one random number is drawn in this file. The same terrain plans the same
//  rivers every time, which is what makes a saved edit survive a Regenerate.
//
//  COST
//  ----
//  One solve per Generate, on the main thread, after the terrain has settled. A
//  200x200 grid is 40,000 cells; the fill is a priority flood and the accumulation is
//  a topological sweep, so both are effectively linear. Managed float[] and int[]
//  throughout - no jobs, no native containers.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Plans river courses from a world's own drainage. Stateless: everything it
    /// works out is returned, nothing is cached between calls.
    /// </summary>
    public static class RiverNetworkPlanner
    {
        // =====================================================================
        //  REAL-WORLD CONSTANTS
        // =====================================================================

        // A permanent channel needs a catchment behind it. Below roughly a quarter of
        // a square kilometre, temperate hillslope runoff is sheet flow and gullies
        // that only run after rain - not a river. This is the value that decides where
        // a river's source is, so it is stated once, here, in real units.
        //
        // Public because it is no longer private arithmetic: the node defaults quote it,
        // ResolveChannelHeadArea measures it against the world, and the self-test asserts
        // both - and the self-test lives in the editor assembly, which cannot see
        // internals. A figure three separate places depend on is part of the API.
        public const float ChannelHeadAreaSquareMetres = 250000f;

        // A trunk river drains several channel-heads' worth of land. Used to reject
        // coastal trickles on the first pass, before the fallback relaxes it.
        public const float TrunkCatchmentMultiple = 4f;

        // Hack's law exponent: channel length scales with catchment area as L proportional
        // to A^h. The classic figure is 0.6; 0.57 is the modern best fit and the value used
        // here. It is scale-invariant, so a tributary's length RELATIVE to the trunk it
        // joins is (A_trib / A_trunk)^h from the two catchments alone - which is how a
        // branch is bounded to a realistic fraction of its trunk rather than being allowed
        // to run nearly the trunk's whole length and gouge the terrain.
        public const float HackLengthExponent = 0.57f;

        // A little headroom over the Hack estimate before a branch is trimmed, so ordinary
        // scatter in the length-area relation is not clipped - only a branch that is
        // genuinely trunk-sized is brought back to scale.
        private const float TributaryLengthTolerance = 1.35f;

        // Two millimetres of residual fall per filled cell. Enough for D8 to have a
        // direction to pick across a flat the flood levelled off; far too small to
        // bend a slope that was really there.
        private const float FillEpsilonMetres = 0.002f;

        // Under this a "river" is a puddle's outflow, not a course worth an object.
        private const float MinCourseMetres = 250f;

        /// <summary>The minimum course length, exposed read-only so the self test can
        /// assert the tributary length bound never clips a branch below it.</summary>
        public static float MinCourseMetresForTest { get { return MinCourseMetres; } }

        // A lake's drain-out can legitimately be a short connecting reach between two
        // neighbouring lakes, so it is held to a lower floor than a trunk river - but
        // still long enough that a one-cell spill is never spawned as a river object.
        private const float MinOutletMetres = 80f;

        // Fewer nodes than this cannot describe a curve, and a two-point course is
        // the exact bug this file exists to remove.
        private const int MinCourseNodes = 3;

        // =====================================================================
        //  THE THRESHOLD, MEASURED AGAINST THE WORLD IT IS APPLIED TO
        // =====================================================================

        /// <summary>
        /// The channel-head area to actually use on a world whose drainage is described
        /// by <paramref name="accumCells"/>: the figure asked for when the world can
        /// meet it, and otherwise the largest catchment the world really has, divided by
        /// <see cref="TrunkCatchmentMultiple"/> so a trunk still drains several channel
        /// heads. Only ever lowers the figure, never raises it.
        /// </summary>
        /// <remarks>
        /// WHY A REAL-WORLD CONSTANT HAS TO BE MEASURED AGAINST THE WORLD
        /// A quarter of a square kilometre is the right figure for real temperate
        /// hillslope, and it is why <see cref="ChannelHeadAreaSquareMetres"/> is stated
        /// in real units. But a test world of one 800 m chunk is 0.64 km^2 in total, and
        /// no single catchment inside it comes near 0.25 km^2. Every channel test then
        /// fails, TraceChannels returns an empty list, no mouth clears the floor, and
        /// the whole water system reports success having produced nothing: no river
        /// mask, no trench, no river object. The user sees a Hydrology Spawner with
        /// every field filled in and a world with no water in it, which is
        /// indistinguishable from a node that does nothing at all.
        ///
        /// Refusing to draw a river there is the literally correct answer about the real
        /// world and a useless one about a scale model. What makes the rescale legitimate
        /// rather than a fudge is that drainage networks have no characteristic scale:
        /// Hack's law (channel length against basin area) and Horton's bifurcation and
        /// length ratios are power laws, so a basin's branching structure is
        /// statistically the same whatever its size. Only the calibration - how much land
        /// a PERMANENT channel needs before it stops being a wet-weather gully - carries
        /// real climate and soil in it, and that calibration is the one thing a
        /// sub-kilometre world cannot host. Setting the threshold from the world's own
        /// largest catchment keeps the whole hierarchy: the biggest drainage becomes the
        /// trunk, a quarter of it is a channel head, and the network below that is the
        /// same self-similar shape the real figure would have produced on real ground.
        ///
        /// It cannot invent water. The threshold is read off the accumulation field, so
        /// a world that drains nowhere still yields nothing, and a world large enough to
        /// meet the real figure is left on the real figure untouched.
        /// </remarks>
        /// <param name="accumCells">Flow accumulation per cell, in CELLS of upstream
        /// area - the field D8 accumulation produces.</param>
        /// <param name="cellAreaSquareMetres">Ground one cell covers.</param>
        /// <param name="requestedSquareMetres">The figure the caller authored. Zero or
        /// less means "use the real-world constant".</param>
        public static float ResolveChannelHeadArea(float[] accumCells,
                                                   float cellAreaSquareMetres,
                                                   float requestedSquareMetres)
        {
            float requested = requestedSquareMetres > 0f
                ? requestedSquareMetres
                : ChannelHeadAreaSquareMetres;

            if (accumCells == null || accumCells.Length == 0) return requested;
            if (cellAreaSquareMetres <= 0f) return requested;

            float largestCells = 0f;
            for (int i = 0; i < accumCells.Length; i++)
                if (accumCells[i] > largestCells) largestCells = accumCells[i];

            float largestArea = largestCells * cellAreaSquareMetres;
            if (largestArea <= 0f) return requested;

            // The world's biggest drainage is its trunk, so a channel head is that
            // divided by the trunk multiple. Above the requested figure this is no
            // constraint at all and the authored value stands.
            float affordable = largestArea / Mathf.Max(1f, TrunkCatchmentMultiple);
            if (affordable >= requested) return requested;

            // Never below one cell: a threshold finer than the lattice would mark every
            // cell as a channel and paint the whole world wet.
            return Mathf.Max(cellAreaSquareMetres, affordable);
        }

        // =====================================================================
        //  RESULTS
        // =====================================================================

        /// <summary>What a planned river runs into at its downstream end.</summary>
        public enum MouthKind
        {
            /// <summary>It reaches the sea.</summary>
            Ocean = 0,

            /// <summary>It reaches a lake.</summary>
            Lake = 1,

            /// <summary>It leaves the world at the edge, or runs out inland.</summary>
            Outflow = 2,

            /// <summary>It joins another planned river at a confluence.</summary>
            River = 3
        }

        /// <summary>
        /// One planned course, head first, in world space, sitting on the ground.
        /// Feed <see cref="course"/> straight to a river body as its authored course.
        /// </summary>
        public class PlannedRiver
        {
            /// <summary>Head to mouth. World space, y on the sampled ground.</summary>
            public List<Vector3> course;

            /// <summary>Land draining through the mouth, in square metres. Real
            /// discharge is proportional to this, so hydraulic geometry - width and
            /// depth - can be derived from it rather than guessed.</summary>
            public float catchmentSquareMetres;

            /// <summary>Course length along the ground, in metres.</summary>
            public float lengthMetres;

            /// <summary>What it drains into.</summary>
            public MouthKind mouth;

            /// <summary>
            /// Index in the returned list of the course this one joins, or -1 when it
            /// ends in water or leaves the world. The last point of this course is the
            /// confluence itself, so a join is a shared point rather than two ends that
            /// happen to be near each other.
            /// </summary>
            public int joinsPlanIndex = -1;

            /// <summary>
            /// Index into the lakeCentres list this course spills OUT of, or -1 when it
            /// is not a lake outlet. Recorded where the spill is chosen rather than
            /// recovered later by a nearest-lake search, which on two lakes sharing a
            /// valley would pick the wrong one.
            /// </summary>
            public int fromLakeIndex = -1;

            /// <summary>
            /// Strahler stream order: 1 for a headwater, rising by one only where two
            /// branches of equal order meet. It measures the network behind a course
            /// rather than its length, which is what makes it the standard way to say
            /// how major a river is.
            /// </summary>
            public int streamOrder = 1;
        }

        /// <summary>
        /// A world sampled onto a grid and solved for drainage. Kept as a result
        /// object rather than thrown away inside the planner, because the stream
        /// hierarchy work needs exactly this network and solving it twice would be
        /// two answers to one question.
        /// </summary>
        public class DrainageGrid
        {
            public int resX;
            public int resZ;

            /// <summary>Metres between cell centres.</summary>
            public float cellSizeX;
            public float cellSizeZ;

            /// <summary>World position of cell (0,0). Only x and z are meaningful.</summary>
            public Vector3 origin;

            /// <summary>Terrain height in metres, as sampled.</summary>
            public float[] ground;

            /// <summary>Ground with every closed depression raised to its spill level.</summary>
            public float[] filled;

            /// <summary>D8 neighbour index 0..7 of steepest descent, -1 at an outlet.</summary>
            public int[] dir;

            /// <summary>Cells draining through each cell, itself included.</summary>
            public float[] accum;

            public int Count { get { return resX * resZ; } }

            public float CellAreaSquareMetres { get { return cellSizeX * cellSizeZ; } }

            /// <summary>Mean cell pitch, for turning metres into cells and back.</summary>
            public float CellSizeMetres { get { return (cellSizeX + cellSizeZ) * 0.5f; } }

            /// <summary>World point at a cell, on the sampled ground.</summary>
            public Vector3 WorldOf(int index)
            {
                int x = index % resX;
                int z = index / resX;

                return new Vector3(origin.x + x * cellSizeX,
                                   ground[index],
                                   origin.z + z * cellSizeZ);
            }

            /// <summary>Nearest cell to a world XZ, clamped inside the grid.</summary>
            public int CellAt(float worldX, float worldZ)
            {
                int x = Mathf.RoundToInt((worldX - origin.x) / Mathf.Max(1e-3f, cellSizeX));
                int z = Mathf.RoundToInt((worldZ - origin.z) / Mathf.Max(1e-3f, cellSizeZ));

                x = Mathf.Clamp(x, 0, resX - 1);
                z = Mathf.Clamp(z, 0, resZ - 1);

                return z * resX + x;
            }

            /// <summary>The cell this one drains into, or -1 where nothing is lower.</summary>
            public int DownstreamOf(int index)
            {
                int d = dir[index];
                if (d < 0) return -1;

                int dx, dz;
                Hydrology.NeighbourOffset(d, out dx, out dz);

                int nx = index % resX + dx;
                int nz = index / resX + dz;
                if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) return -1;

                return nz * resX + nx;
            }
        }

        // =====================================================================
        //  SOLVE
        // =====================================================================

        /// <summary>
        /// Samples the world and solves its drainage.
        ///
        /// The ground sampler is passed in rather than a Terrain array so this file
        /// never has to know what a Terrain is - the same arrangement RiverCourseSolver
        /// uses, and the reason its delegate is reused here instead of a second one
        /// being declared with the same signature.
        /// </summary>
        /// <param name="ground">World ground height at a world XZ.</param>
        /// <param name="bounds">The world to cover. Only XZ is used.</param>
        /// <param name="gridPerSide">Cells per side. Clamped to a sane range.</param>
        /// <returns>The solved grid, or null if the world is too small to sample.</returns>
        public static DrainageGrid Solve(RiverCourseSolver.GroundSampler ground,
                                         Bounds bounds, int gridPerSide)
        {
            if (ground == null) return null;
            if (bounds.size.x < 1f || bounds.size.z < 1f) return null;

            int res = Mathf.Clamp(gridPerSide, 16, 512);

            DrainageGrid g = new DrainageGrid();
            g.resX = res;
            g.resZ = res;
            g.cellSizeX = bounds.size.x / (res - 1);
            g.cellSizeZ = bounds.size.z / (res - 1);
            g.origin = new Vector3(bounds.min.x, 0f, bounds.min.z);

            int count = res * res;
            g.ground = new float[count];

            for (int z = 0; z < res; z++)
            {
                float wz = g.origin.z + z * g.cellSizeZ;
                int row = z * res;

                for (int x = 0; x < res; x++)
                    g.ground[row + x] = ground(g.origin.x + x * g.cellSizeX, wz);
            }

            // Filling first is the whole difference between this and the old trace.
            // A walk on the raw surface stops at the first pit; a walk on the filled
            // surface has a non-increasing path to the world edge from every cell.
            g.filled = Hydrology.FillDepressions(g.ground, res, res, FillEpsilonMetres, null);
            g.dir = Hydrology.FlowDirection(g.filled, res, res, null);
            g.accum = Hydrology.FlowAccumulation(g.dir, res, res, null, null);

            return g;
        }

        // =====================================================================
        //  PLAN THE TRUNK RIVERS
        // =====================================================================

        /// <summary>
        /// Picks the world's largest river courses: the biggest catchments, each
        /// traced from its mouth back up its main stem to its source, plus the
        /// tributaries that join each one.
        ///
        /// Ranking mouths by accumulated flow is not a heuristic - it is the
        /// definition. The mouth with the most land behind it carries the most water,
        /// and that is what a trunk river is. So the count asked for is met by taking
        /// the top N, and no two of them can be the same river because a course already
        /// claimed by one is not walked again by the next.
        ///
        /// A main stem alone is not a river network, though. Following the largest
        /// tributary at every confluence traces ONE line from mouth to source and leaves
        /// every branch it passed unrecorded, which is why no junction was ever visible
        /// in the result. The tributaries are therefore planned here too, from the same
        /// flow graph, each ending exactly on the cell of the course it joins.
        /// </summary>
        /// <param name="g">A solved grid from <see cref="Solve"/>.</param>
        /// <param name="trunkCount">How many main stems to return, at most.</param>
        /// <param name="tributariesPerTrunk">How many branches each main stem may keep.
        /// Zero returns bare stems.</param>
        /// <param name="seaLevelMetres">The sea surface. Pass float.NegativeInfinity
        /// for a world with no sea, and no course will be given an ocean mouth.</param>
        /// <param name="lakeCentres">Where lakes will be, so rivers can end in one.
        /// May be null.</param>
        /// <param name="lakeRadiiMetres">How far each lake in <paramref name="lakeCentres"/>
        /// reaches from its own centre, index-aligned with it. One shared figure would
        /// stamp every pond with the biggest lake's footprint.</param>
        /// <param name="seedSpacingMetres">Node spacing of the returned course. The
        /// spline resamples far finer than this; these are the editable nodes.</param>
        /// <param name="channelHeadAreaSquareMetres">Land a channel must drain before it
        /// counts as a river. Pass the figure the caller's River Mask was stamped at, so
        /// the courses planned here are the courses that mask drew. Zero or less falls
        /// back to <see cref="ChannelHeadAreaSquareMetres"/>.</param>
        public static List<PlannedRiver> PlanTrunkRivers(DrainageGrid g, int trunkCount,
                                                         int tributariesPerTrunk,
                                                         float seaLevelMetres,
                                                         IList<Vector3> lakeCentres,
                                                         IList<float> lakeRadiiMetres,
                                                         float seedSpacingMetres,
                                                         float channelHeadAreaSquareMetres)
        {
            List<PlannedRiver> planned = new List<PlannedRiver>();
            if (g == null || trunkCount <= 0) return planned;

            float cellArea = g.CellAreaSquareMetres;
            if (cellArea <= 0f) return planned;

            int count = g.Count;

            // Catchment thresholds converted from real areas into cells, once.
            //
            // THE AREA COMES FROM THE CALLER, NOT FROM THE CONSTANT
            // The same number decides where the Hydrology Spawner's River Mask stamps a
            // channel. Read from the constant here, a user who lowered the spawner's
            // channel-head area to suit a world smaller than a full catchment got the
            // mask - and the trenches the height chain carved from it - at the new
            // figure, while this still demanded a quarter of a square kilometre. No
            // mouth qualified, no course was planned, and no river OBJECT was ever put
            // in the trench that had just been dug for it. The constant stays as the
            // real-world default for a caller with no figure of its own.
            float headArea = channelHeadAreaSquareMetres > 0f
                ? channelHeadAreaSquareMetres
                : ChannelHeadAreaSquareMetres;

            // And then measured against the world, because a figure no catchment here can
            // reach rejects every mouth and plans nothing. See ResolveChannelHeadArea.
            headArea = ResolveChannelHeadArea(g.accum, cellArea, headArea);

            float headCells = Mathf.Max(4f, headArea / cellArea);
            float trunkCells = headCells * TrunkCatchmentMultiple;

            MouthKind[] waterKind = BuildWaterMask(g, seaLevelMetres, lakeCentres,
                                                   lakeRadiiMetres);

            // For every cell, the upstream neighbour that brings it the most water.
            // Built in one pass over the D8 graph: walking this backwards from a mouth
            // is the main stem, which is how a river's source is defined.
            int[] bestUp = BuildUpstreamMaxGraph(g);

            List<int> mouthLand = new List<int>();
            List<int> mouthWater = new List<int>();
            CollectMouths(g, waterKind, mouthLand, mouthWater);

            // No standing water anywhere - no sea, no lakes. The world still drains:
            // its rivers leave at the edge. Those outlets are the mouths instead, so a
            // land-locked world is not left riverless.
            if (mouthLand.Count == 0) CollectEdgeOutlets(g, mouthLand, mouthWater);
            if (mouthLand.Count == 0) return planned;

            // Biggest catchment first. Insertion sort on the index lists would be
            // O(n^2) on a coastline, so sort an index array by accumulation instead.
            int[] order = new int[mouthLand.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            float[] score = new float[mouthLand.Count];
            for (int i = 0; i < score.Length; i++) score[i] = g.accum[mouthLand[i]];

            System.Array.Sort(score, order);
            System.Array.Reverse(order);

            bool[] used = new bool[count];

            // Counted apart from planned.Count, which now also holds tributaries: the
            // caller asked for N main stems, not for N courses.
            int trunks = 0;

            // Two passes over the same ranking. The first insists on a real trunk
            // catchment so a coastal trickle cannot take a slot a river deserves; the
            // second drops that floor, so a small or flat world still gets the count
            // it asked for rather than being told its rivers are too small.
            for (int pass = 0; pass < 2 && trunks < trunkCount; pass++)
            {
                float floorCells = pass == 0 ? trunkCells : headCells;

                for (int o = 0; o < order.Length && trunks < trunkCount; o++)
                {
                    int land = mouthLand[order[o]];
                    if (used[land]) continue;
                    if (g.accum[land] < floorCells) continue;

                    int water = mouthWater[order[o]];

                    // The mouth's kind is the kind of the water it runs INTO. The land
                    // cell's own kind is always Outflow, because a mouth is by
                    // definition the last dry cell.
                    MouthKind kind = water >= 0 ? waterKind[water] : MouthKind.Outflow;

                    List<int> stem;
                    PlannedRiver river = TraceStem(g, bestUp, used, land, water, kind,
                                                   headCells, seedSpacingMetres, out stem);

                    if (river == null) continue;

                    int trunkIndex = planned.Count;
                    planned.Add(river);
                    trunks++;

                    AddTributaries(g, bestUp, used, planned, stem, trunkIndex,
                                   tributariesPerTrunk, headCells, seedSpacingMetres);
                }
            }

            AssignStreamOrder(planned);
            SnapConfluences(planned);

            return planned;
        }

        /// <summary>
        /// Records the branches that join a main stem, largest catchment first.
        ///
        /// A cell is upstream of the stem when its own flow direction points at the
        /// stem, so the branches are read off the flow graph rather than searched for,
        /// and one that carries less land than a permanent channel needs is not a
        /// tributary. Each branch is traced by the same stem walk the trunk used, given
        /// the junction cell as its mouth - so its last point IS the confluence and the
        /// two courses share it exactly.
        /// </summary>
        /// <remarks>
        /// Only one level is taken: branches of branches would multiply without bound on
        /// a large world and each is a smaller feature than the one before.
        ///
        /// WHICH BRANCHES, AND WHERE THEY MAY JOIN
        /// Candidates are gathered before any is taken, then ranked by the land behind
        /// them. Accepting them in D8 scan order instead ranked them by COMPASS BEARING:
        /// whichever of a junction's branches happened to lie north-east won the slot, so
        /// a headwater trickle could take the place of the confluence that actually
        /// changes the river. Three rules then filter what is left, each of them a thing
        /// real drainage does:
        ///   smaller than its parent - a branch carrying more land than the stem it joins
        ///     is the stem, and the trunk took the wrong fork;
        ///   arriving downstream - a tributary points the way the parent is going. The
        ///     opposite is a barbed junction, which follows a drainage reversal and not a
        ///     hillside;
        ///   spaced apart - two confluences closer than one sample cannot be drawn as
        ///     two, and read as a single forked blob.
        /// A branch rejected after tracing keeps its cells marked used. That is wanted:
        /// they were shown to carry a channel, and another river re-tracing the same
        /// water would draw it twice.
        /// </remarks>
        private static void AddTributaries(DrainageGrid g, int[] bestUp, bool[] used,
                                           List<PlannedRiver> planned, List<int> stem,
                                           int parentIndex, int maxTributaries,
                                           float headCells, float seedSpacingMetres)
        {
            if (maxTributaries <= 0 || stem == null || stem.Count < 2) return;

            List<int> candUp = new List<int>();
            List<int> candJunction = new List<int>();

            // stem[0] is the mouth, so s rises upstream and stem[s - 1] is the way out.
            for (int s = 1; s < stem.Count; s++)
            {
                int junction = stem[s];
                Vector3 here = g.WorldOf(junction);
                Vector3 downstream = g.WorldOf(stem[s - 1]) - here;
                int stemNext = s + 1 < stem.Count ? stem[s + 1] : -1;

                for (int d = 0; d < 8; d++)
                {
                    int up = NeighbourOf(g, junction, d);
                    if (up < 0 || up == stemNext || used[up]) continue;
                    if (g.DownstreamOf(up) != junction) continue;    // not upstream of here
                    if (g.accum[up] < headCells) continue;           // not a channel
                    if (g.accum[up] >= g.accum[junction]) continue;  // it is the stem, not a branch
                    if (!ArrivesDownstream(here - g.WorldOf(up), downstream)) continue;

                    candUp.Add(up);
                    candJunction.Add(junction);
                }
            }

            if (candUp.Count == 0) return;

            List<int> order = new List<int>(candUp.Count);
            for (int i = 0; i < candUp.Count; i++) order.Add(i);
            order.Sort((a, b) => g.accum[candUp[b]].CompareTo(g.accum[candUp[a]]));

            float minSpacing = Mathf.Max(seedSpacingMetres, 3f * g.CellSizeMetres);
            float parentLength = planned[parentIndex].lengthMetres;
            float parentCatchment = Mathf.Max(1f, planned[parentIndex].catchmentSquareMetres);

            List<Vector3> taken = new List<Vector3>();
            int spawned = 0;

            for (int r = 0; r < order.Count && spawned < maxTributaries; r++)
            {
                int c = order[r];
                int up = candUp[c];
                if (used[up]) continue;                  // claimed by a branch taken already

                Vector3 at = g.WorldOf(candJunction[c]);
                if (TooCloseToAny(taken, at, minSpacing)) continue;

                List<int> branch;
                PlannedRiver trib = TraceStem(g, bestUp, used, up, candJunction[c],
                                              MouthKind.River, headCells,
                                              seedSpacingMetres, out branch);
                if (trib == null) continue;

                // A tributary is SHORTER than its trunk in proportion to the land it
                // drains - Hack's law, L proportional to A^h, scale-invariant - so the
                // length it should have is the parent's length times (its catchment over
                // the parent's)^h. A branch that traced far longer than that (the reported
                // "tributary as long as a trunk" that gouges the terrain) is trimmed back
                // to that estimate, plus a little tolerance for natural scatter. It is
                // trimmed from its SOURCE end because the mouth is the confluence and must
                // stay put: the excess comes off the head, leaving a real, shorter
                // tributary meeting the parent at the same point - more realistic, not
                // fewer joins. Never trimmed longer than the parent regardless, and never
                // below MinCourseMetres so a valid branch is not clipped out of existence.
                float maxLength = TributaryLengthBound(parentLength, parentCatchment,
                                                       trib.catchmentSquareMetres);
                if (trib.lengthMetres > maxLength)
                    TrimToLengthFromSource(trib, maxLength);
                if (trib.course == null || trib.course.Count < 2) continue;

                trib.joinsPlanIndex = parentIndex;
                planned.Add(trib);
                taken.Add(at);
                spawned++;
            }
        }

        /// <summary>
        /// The longest a tributary of the given catchment should be, for a trunk of the
        /// given length and catchment, under Hack's law (L proportional to A^h).
        /// </summary>
        /// <remarks>
        /// Hack's law is scale-invariant, so a tributary's length RELATIVE to its trunk is
        /// (A_trib / A_trunk)^h - a branch draining a tenth of the trunk's land is about
        /// 0.1^0.57 ~ 0.27 of its length, not nearly all of it. The bound is that estimate
        /// times a small tolerance for natural scatter, never longer than the trunk itself
        /// (a tributary longer than its trunk is the wrong hierarchy), and never shorter
        /// than MinCourseMetres so a legitimate branch is not clipped out of existence.
        /// Pure and public so the relation can be asserted directly in the self test.
        /// </remarks>
        public static float TributaryLengthBound(float parentLengthMetres,
                                                 float parentCatchmentSquareMetres,
                                                 float tributaryCatchmentSquareMetres)
        {
            float parentCatchment = Mathf.Max(1f, parentCatchmentSquareMetres);
            float catchRatio = Mathf.Clamp01(tributaryCatchmentSquareMetres / parentCatchment);
            float hackLength = parentLengthMetres * Mathf.Pow(catchRatio, HackLengthExponent);
            return Mathf.Min(parentLengthMetres,
                             Mathf.Max(MinCourseMetres, hackLength * TributaryLengthTolerance));
        }

        /// <summary>
        /// Trims a course from its SOURCE end (index 0) so its length does not exceed
        /// <paramref name="maxLength"/> metres, leaving the mouth (last point) untouched.
        /// The course is head-to-mouth, so it walks back from the mouth accumulating
        /// length and keeps only the run within budget; the kept span's first point is
        /// nudged onto the budget line so the trim is exact rather than to the nearest
        /// node. Recomputes lengthMetres. Leaves at least two points.
        /// </summary>
        private static void TrimToLengthFromSource(PlannedRiver river, float maxLength)
        {
            List<Vector3> c = river.course;
            if (c == null || c.Count < 2 || maxLength <= 0f) return;

            // Walk from the mouth (last) upstream, summing segment lengths, until adding
            // the next segment would exceed the budget. keepFrom is the first index kept.
            float acc = 0f;
            int keepFrom = 0;
            Vector3 cut = c[0];
            for (int i = c.Count - 1; i > 0; i--)
            {
                float seg = Vector3.Distance(c[i], c[i - 1]);
                if (acc + seg >= maxLength)
                {
                    // Interpolate the exact point at the budget along this segment.
                    float remain = maxLength - acc;
                    float t = seg > 1e-4f ? Mathf.Clamp01(remain / seg) : 0f;
                    cut = Vector3.Lerp(c[i], c[i - 1], t);
                    keepFrom = i - 1;
                    break;
                }
                acc += seg;
            }

            if (keepFrom <= 0) return;   // whole course already within budget

            List<Vector3> trimmed = new List<Vector3>(c.Count - keepFrom + 1);
            trimmed.Add(cut);                                   // new source, exactly on the budget line
            for (int i = keepFrom + 1; i < c.Count; i++) trimmed.Add(c[i]);

            if (trimmed.Count < 2) return;   // refuse to trim below a real course

            float len = 0f;
            for (int i = 1; i < trimmed.Count; i++) len += Vector3.Distance(trimmed[i], trimmed[i - 1]);

            river.course = trimmed;
            river.lengthMetres = len;
        }

        /// <summary>Cosine of the widest angle a tributary may arrive at - 105 degrees
        /// from the parent's downstream direction. Wider than that is a barbed junction.
        /// </summary>
        private const float MaxConfluenceCos = -0.2588f;

        /// <summary>True when a branch arriving along <paramref name="arrival"/> points
        /// broadly the way the parent flows.</summary>
        private static bool ArrivesDownstream(Vector3 arrival, Vector3 downstream)
        {
            float ax = arrival.x, az = arrival.z;
            float bx = downstream.x, bz = downstream.z;

            float la = Mathf.Sqrt(ax * ax + az * az);
            float lb = Mathf.Sqrt(bx * bx + bz * bz);
            if (la < 1e-4f || lb < 1e-4f) return true;   // no direction to judge it by

            return (ax * bx + az * bz) / (la * lb) > MaxConfluenceCos;
        }

        /// <summary>True when a point is within <paramref name="minMetres"/> of any taken
        /// confluence, measured in plan (XZ).</summary>
        private static bool TooCloseToAny(List<Vector3> taken, Vector3 at, float minMetres)
        {
            float limitSq = minMetres * minMetres;

            for (int i = 0; i < taken.Count; i++)
            {
                float dx = taken[i].x - at.x;
                float dz = taken[i].z - at.z;
                if (dx * dx + dz * dz < limitSq) return true;
            }

            return false;
        }

        /// <summary>Neighbour of a cell in D8 direction d, or -1 outside the grid.</summary>
        private static int NeighbourOf(DrainageGrid g, int index, int d)
        {
            int dx, dz;
            Hydrology.NeighbourOffset(d, out dx, out dz);

            int nx = index % g.resX + dx;
            int nz = index / g.resX + dz;
            if (nx < 0 || nz < 0 || nx >= g.resX || nz >= g.resZ) return -1;

            return nz * g.resX + nx;
        }

        /// <summary>Moves each tributary's last point exactly onto the course it joins.</summary>
        /// <remarks>
        /// The junction cell is exact on the flow grid, but both courses are relaxed and
        /// thinned after that, so the parent's polyline no longer passes precisely through
        /// it. Left alone the two ends sit about a cell apart and the confluence reads as
        /// a gap - which is what a user sees as rivers that do not join. Snapping to the
        /// nearest point on the parent's own line closes it by construction, and only the
        /// tributary moves: the trunk keeps the shape its drainage gave it.
        /// </remarks>
        private static void SnapConfluences(List<PlannedRiver> plans)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                PlannedRiver p = plans[i];

                int into = p.joinsPlanIndex;
                if (into < 0 || into >= plans.Count) continue;
                if (p.course == null || p.course.Count < 2) continue;

                List<Vector3> parent = plans[into].course;
                if (parent == null || parent.Count < 2) continue;

                int last = p.course.Count - 1;
                p.course[last] = NearestOnPolyline(parent, p.course[last]);
            }
        }

        /// <summary>Closest point on a polyline to a position, measured in plan (XZ).</summary>
        private static Vector3 NearestOnPolyline(List<Vector3> line, Vector3 to)
        {
            Vector3 best = line[0];
            float bestSq = float.MaxValue;

            for (int i = 0; i < line.Count - 1; i++)
            {
                Vector3 a = line[i];
                Vector3 b = line[i + 1];

                float ex = b.x - a.x;
                float ez = b.z - a.z;
                float lenSq = ex * ex + ez * ez;

                float t = lenSq > 1e-6f
                    ? Mathf.Clamp01(((to.x - a.x) * ex + (to.z - a.z) * ez) / lenSq)
                    : 0f;

                Vector3 on = new Vector3(a.x + ex * t, Mathf.Lerp(a.y, b.y, t), a.z + ez * t);

                float dx = on.x - to.x;
                float dz = on.z - to.z;
                float sq = dx * dx + dz * dz;

                if (sq < bestSq) { bestSq = sq; best = on; }
            }

            return best;
        }

        /// <summary>
        /// Fills in every course's Strahler order.
        /// </summary>
        /// <remarks>
        /// A tributary is always appended after the course it joins, so one backward
        /// pass settles every order before the course that needs it is reached - no
        /// recursion and no repeated sweeps. The rule itself is Strahler's: a course
        /// with no tributary is order 1, otherwise it takes its largest tributary's
        /// order and rises by one only when two of that same order meet.
        /// </remarks>
        private static void AssignStreamOrder(List<PlannedRiver> plans)
        {
            int n = plans.Count;
            int[] best = new int[n];    // highest order joining each course
            int[] ties = new int[n];    // how many tributaries hold that order

            for (int i = n - 1; i >= 0; i--)
            {
                PlannedRiver p = plans[i];

                p.streamOrder = best[i] == 0
                    ? 1
                    : (ties[i] > 1 ? best[i] + 1 : best[i]);

                int into = p.joinsPlanIndex;
                if (into < 0 || into >= n) continue;

                if (p.streamOrder > best[into]) { best[into] = p.streamOrder; ties[into] = 1; }
                else if (p.streamOrder == best[into]) ties[into]++;
            }
        }

        // =====================================================================
        //  PIECES
        // =====================================================================

        /// <summary>
        /// One lake's reach, floored at a cell so a lake smaller than the lattice still
        /// marks the cell it sits in. A missing or short list falls back to the cell size
        /// rather than to another lake's figure.
        /// </summary>
        private static float LakeRadius(DrainageGrid g, IList<float> radii, int index)
        {
            float r = radii != null && index >= 0 && index < radii.Count ? radii[index] : 0f;
            return Mathf.Max(g.CellSizeMetres, r);
        }

        /// <summary>
        /// Marks every cell that is standing water, and what kind. The ocean is simply
        /// ground at or below the sea surface - the same test the ocean body's own mesh
        /// uses to decide where it shows - so the coastline the rivers aim at is the
        /// coastline that will be there.
        /// </summary>
        private static MouthKind[] BuildWaterMask(DrainageGrid g, float seaLevelMetres,
                                                  IList<Vector3> lakeCentres,
                                                  IList<float> lakeRadiiMetres)
        {
            int count = g.Count;

            // Outflow doubles as "not water" - no cell is ever marked Outflow here,
            // and it saves a parallel bool[] that could disagree with this one.
            MouthKind[] kind = new MouthKind[count];
            for (int i = 0; i < count; i++) kind[i] = MouthKind.Outflow;

            bool hasSea = !float.IsNegativeInfinity(seaLevelMetres) &&
                          !float.IsNaN(seaLevelMetres);

            if (hasSea)
                for (int i = 0; i < count; i++)
                    if (g.ground[i] <= seaLevelMetres) kind[i] = MouthKind.Ocean;

            if (lakeCentres == null || lakeCentres.Count == 0) return kind;

            // Stamped as discs around the chosen basins rather than tested per cell
            // against every lake, so the cost is the lakes' own footprint. Each disc is
            // that lake's own reach: one shared radius gave a hillside pond the span of
            // the largest lake in the world, so rivers were told they had arrived at
            // water hundreds of metres from any, and stopped in dry ground.
            for (int l = 0; l < lakeCentres.Count; l++)
            {
                Vector3 c = lakeCentres[l];

                float radius = LakeRadius(g, lakeRadiiMetres, l);
                float radiusSq = radius * radius;

                int spanX = Mathf.CeilToInt(radius / Mathf.Max(1e-3f, g.cellSizeX));
                int spanZ = Mathf.CeilToInt(radius / Mathf.Max(1e-3f, g.cellSizeZ));

                int cx = Mathf.RoundToInt((c.x - g.origin.x) / Mathf.Max(1e-3f, g.cellSizeX));
                int cz = Mathf.RoundToInt((c.z - g.origin.z) / Mathf.Max(1e-3f, g.cellSizeZ));

                for (int z = cz - spanZ; z <= cz + spanZ; z++)
                {
                    if (z < 0 || z >= g.resZ) continue;
                    float wz = g.origin.z + z * g.cellSizeZ;
                    int row = z * g.resX;

                    for (int x = cx - spanX; x <= cx + spanX; x++)
                    {
                        if (x < 0 || x >= g.resX) continue;

                        float wx = g.origin.x + x * g.cellSizeX;
                        float dx = wx - c.x;
                        float dz = wz - c.z;
                        if (dx * dx + dz * dz > radiusSq) continue;

                        // The sea outranks a lake: a lake basin the sea has already
                        // flooded is a bay, and a river arriving there reaches the sea.
                        if (kind[row + x] == MouthKind.Outflow) kind[row + x] = MouthKind.Lake;
                    }
                }
            }

            return kind;
        }

        /// <summary>
        /// For each cell, which upstream neighbour contributes the most flow. One pass
        /// over the D8 graph; following it backwards from any cell is that cell's main
        /// stem, because at every confluence it takes the larger tributary.
        /// </summary>
        private static int[] BuildUpstreamMaxGraph(DrainageGrid g)
        {
            int count = g.Count;
            int[] bestUp = new int[count];
            for (int i = 0; i < count; i++) bestUp[i] = -1;

            for (int i = 0; i < count; i++)
            {
                int down = g.DownstreamOf(i);
                if (down < 0) continue;

                int held = bestUp[down];
                if (held < 0 || g.accum[i] > g.accum[held]) bestUp[down] = i;
            }

            return bestUp;
        }

        /// <summary>
        /// Every land cell that drains straight into standing water. That is a river
        /// mouth: the last cell of dry land a course passes through.
        /// </summary>
        private static void CollectMouths(DrainageGrid g, MouthKind[] kind,
                                          List<int> land, List<int> water)
        {
            int count = g.Count;

            for (int i = 0; i < count; i++)
            {
                if (kind[i] != MouthKind.Outflow) continue;   // already water

                int down = g.DownstreamOf(i);
                if (down < 0) continue;
                if (kind[down] == MouthKind.Outflow) continue; // drains onto more land

                land.Add(i);
                water.Add(down);
            }
        }

        /// <summary>
        /// The cells a world drains out of when it holds no standing water. Priority
        /// flood guarantees a non-increasing path to the border, so these exist on any
        /// terrain; ranking them by flow gives the same answer for a land-locked world
        /// that the coastline gives for one with a sea.
        /// </summary>
        private static void CollectEdgeOutlets(DrainageGrid g, List<int> land, List<int> water)
        {
            int count = g.Count;

            for (int i = 0; i < count; i++)
            {
                if (g.DownstreamOf(i) >= 0) continue;

                land.Add(i);
                water.Add(-1);          // nothing to reach into
            }
        }

        /// <summary>
        /// Walks up the main stem from a mouth and turns it into an editable course.
        ///
        /// The walk stops when the contributing area falls below what a permanent
        /// channel needs - that point IS the river's source, not an arbitrary step
        /// count - or when it reaches a cell an accepted river already occupies, in
        /// which case what is above belongs to that river and this one starts here.
        ///
        /// Returns null when what is left is too short to be a river, which is how a
        /// stub is rejected rather than spawned.
        /// </summary>
        /// <param name="stemCells">The cells walked, mouth first. The tributary pass
        /// hangs its junctions off these, so they are reported rather than discarded.</param>
        private static PlannedRiver TraceStem(DrainageGrid g, int[] bestUp, bool[] used,
                                              int mouthLand, int mouthWater,
                                              MouthKind mouthKind,
                                              float headCells, float seedSpacingMetres,
                                              out List<int> stemCells)
        {
            List<int> cells = new List<int>(256);
            stemCells = cells;

            int at = mouthLand;
            int budget = g.Count;   // a flow path cannot revisit a cell; this is a backstop

            while (at >= 0 && budget-- > 0)
            {
                if (used[at]) break;

                cells.Add(at);

                int up = bestUp[at];
                if (up < 0) break;
                if (g.accum[up] < headCells) break;     // above here it is not a channel

                at = up;
            }

            if (cells.Count < 2) return null;

            for (int i = 0; i < cells.Count; i++) used[cells[i]] = true;

            // Head first: the list was gathered walking upstream.
            List<Vector3> path = new List<Vector3>(cells.Count + 1);
            for (int i = cells.Count - 1; i >= 0; i--) path.Add(g.WorldOf(cells[i]));

            // One point into the water, so the mouth is in the body rather than a step
            // short of it. This is what lets the river's own auto-connect find the sea.
            if (mouthWater >= 0) path.Add(g.WorldOf(mouthWater));

            float length;
            List<Vector3> seed = ToCourse(path, MinCourseMetres, seedSpacingMetres,
                                          MinCourseNodes, out length);
            if (seed == null) return null;

            PlannedRiver river = new PlannedRiver();
            river.course = seed;
            river.lengthMetres = length;
            river.catchmentSquareMetres = g.accum[mouthLand] * g.CellAreaSquareMetres;
            river.mouth = mouthKind;

            return river;
        }

        // =====================================================================
        //  PLAN THE LAKE DRAIN-OUT RIVERS
        // =====================================================================

        /// <summary>
        /// Plans the rivers that carry lakes' overflow away.
        ///
        /// A lake with an inflow and no outflow is not a lake, it is a sink: it would
        /// rise forever. Every real lake above sea level overflows at its lowest rim
        /// point - the sill - and the water leaving there is a river. So these courses
        /// are not decoration added to the lakes; they are the other half of what makes
        /// the lakes physically possible.
        ///
        /// Nothing is guessed. The sill is where the filled surface's own drainage
        /// leaves the lake, the course is the flow path down from it, and the ranking is
        /// by the flow that actually crosses each sill - so when there are more lakes
        /// than outlets asked for, the ones that overflow hardest are the ones drawn.
        /// </summary>
        /// <param name="g">A solved grid from <see cref="Solve"/>.</param>
        /// <param name="outletCount">How many drain-out rivers to return, at most.</param>
        /// <param name="seaLevelMetres">The sea surface, so an outlet can end in it.
        /// float.NegativeInfinity for a world with no sea.</param>
        /// <param name="lakeCentres">Where the lakes are. Both the sources of these
        /// rivers and, for the others, something for one to end in.</param>
        /// <param name="lakeRadiiMetres">How far each lake reaches from its own centre,
        /// index-aligned with <paramref name="lakeCentres"/>.</param>
        /// <param name="seedSpacingMetres">Node spacing of the returned course.</param>
        public static List<PlannedRiver> PlanLakeOutlets(DrainageGrid g, int outletCount,
                                                        float seaLevelMetres,
                                                        IList<Vector3> lakeCentres,
                                                        IList<float> lakeRadiiMetres,
                                                        float seedSpacingMetres)
        {
            List<PlannedRiver> planned = new List<PlannedRiver>();
            if (g == null || outletCount <= 0) return planned;
            if (lakeCentres == null || lakeCentres.Count == 0) return planned;

            float cellArea = g.CellAreaSquareMetres;
            if (cellArea <= 0f) return planned;

            // The same water mask the trunk rivers aim at, so an outlet ends in exactly
            // the sea or lake a trunk river would have ended in - one definition of
            // "standing water" for the whole planner.
            MouthKind[] waterKind = BuildWaterMask(g, seaLevelMetres, lakeCentres,
                                                   lakeRadiiMetres);

            int[] spill = new int[lakeCentres.Count];
            float[] score = new float[lakeCentres.Count];
            int[] order = new int[lakeCentres.Count];

            for (int l = 0; l < lakeCentres.Count; l++)
            {
                spill[l] = FindLakeSpill(g, lakeCentres[l], LakeRadius(g, lakeRadiiMetres, l));
                score[l] = spill[l] >= 0 ? g.accum[spill[l]] : -1f;
                order[l] = l;
            }

            // Most overflow first. A lake fed by a large catchment certainly spills; a
            // pond on a hillside may not, so it only gets a river if the count allows.
            System.Array.Sort(score, order);
            System.Array.Reverse(order);

            // A reach already taken by an accepted outlet is not walked again: where two
            // lakes drain the same valley the second joins the first as a tributary
            // instead of being drawn on top of it.
            bool[] used = new bool[g.Count];

            for (int o = 0; o < order.Length && planned.Count < outletCount; o++)
            {
                int start = spill[order[o]];
                if (start < 0) continue;

                // The sill sits in other water already: the lake is a bay of the sea, or
                // it merges with its neighbour. Either way there is no channel between
                // them to draw.
                if (waterKind[start] != MouthKind.Outflow) continue;

                int water;
                List<int> cells = WalkToWater(g, start, waterKind, used, out water);
                if (cells.Count < 2) continue;

                for (int i = 0; i < cells.Count; i++) used[cells[i]] = true;

                List<Vector3> path = new List<Vector3>(cells.Count + 1);
                for (int i = 0; i < cells.Count; i++) path.Add(g.WorldOf(cells[i]));

                // One point inside whatever it runs into, for the same reason a trunk
                // river gets one: the mouth has to be in the body, not a step short of it.
                if (water >= 0) path.Add(g.WorldOf(water));

                float length;
                List<Vector3> seed = ToCourse(path, MinOutletMetres, seedSpacingMetres,
                                              2, out length);
                if (seed == null) continue;

                PlannedRiver river = new PlannedRiver();
                river.course = seed;
                river.lengthMetres = length;
                // The land draining through the sill is the lake's whole catchment, which
                // is what sizes the outflow - so an outlet is scaled by the same
                // hydraulic geometry as every other river rather than by a fixed width.
                river.catchmentSquareMetres = g.accum[start] * cellArea;
                river.mouth = water >= 0 ? waterKind[water] : MouthKind.Outflow;
                // Which lake this drains, so the spawner can join the head to it. The
                // sill was found from THIS lake's basin, so the link is exact.
                river.fromLakeIndex = order[o];

                planned.Add(river);
            }

            return planned;
        }

        /// <summary>
        /// Joins any course that ended inland to the course it actually flows into, so a
        /// lake outlet (or a trunk that ran out on land) meets the river below it at a
        /// real confluence instead of stopping short and reading as two channels that
        /// climb over each other.
        /// </summary>
        /// <remarks>
        /// A confluence is not guessed by nearest-endpoint: it is the point where one
        /// flow path enters another, and on a solved grid that is a SHARED CELL on the D8
        /// graph. The trunk tributaries already declare exactly this - AddTributaries sets
        /// mouth=River, joinsPlanIndex=host and ends the branch on the host's own cell.
        /// The only reason an outlet does not is that PlanLakeOutlets is a separate call
        /// with its own <c>used</c> array, so it never learns which cells the trunks hold.
        ///
        /// This runs once the whole set is known. It marks every cell each course occupies
        /// with that course's index, then for any course whose mouth is still Outflow
        /// (ended inland, not at sea/lake/river) it walks the flow graph on from the mouth:
        /// the first cell owned by ANOTHER course is where this water joins that one. The
        /// walked cells are appended so the channel follows the real flow path to the
        /// junction rather than a straight cut, and the join is declared the same way a
        /// tributary's is, so the existing junction machinery builds it.
        ///
        /// Two real-drainage rules gate it, matching AddTributaries:
        ///   - it may only join a course with an equal or larger catchment (the bigger
        ///     water is the trunk; a smaller one joining would be the wrong hierarchy);
        ///   - it may only join a course EARLIER in the list, which is also what lets the
        ///     spawner - which builds parent before child - have the host already built
        ///     when the joiner asks it for the edge point. Trunks and their tributaries
        ///     are planned before outlets, so an outlet can always reach a trunk.
        /// Nothing is invented: a course that reaches no other course on the graph is left
        /// exactly as it was.
        /// </remarks>
        /// <param name="g">The solved grid the courses were planned on.</param>
        /// <param name="plans">The full planned set, trunks/tributaries then outlets.</param>
        public static void ResolveInlandConfluences(DrainageGrid g, List<PlannedRiver> plans)
        {
            if (g == null || plans == null || plans.Count < 2) return;

            int count = g.Count;

            // Cell -> owning plan index. The LOWEST index wins a shared cell, so a course
            // is never recorded as joining itself and a branch cannot claim its trunk's
            // cell out from under it. -1 is unowned ground.
            int[] owner = new int[count];
            for (int i = 0; i < count; i++) owner[i] = -1;

            for (int p = 0; p < plans.Count; p++)
            {
                PlannedRiver plan = plans[p];
                if (plan == null || plan.course == null) continue;

                for (int k = 0; k < plan.course.Count; k++)
                {
                    Vector3 w = plan.course[k];
                    int cell = g.CellAt(w.x, w.z);
                    if (cell >= 0 && cell < count && owner[cell] < 0) owner[cell] = p;
                }
            }

            for (int p = 0; p < plans.Count; p++)
            {
                PlannedRiver plan = plans[p];
                if (plan == null || plan.course == null || plan.course.Count < 2) continue;

                // Only a course that ended inland is unresolved. One that already reaches
                // the sea, a lake, or a declared river is left alone.
                if (plan.mouth != MouthKind.Outflow || plan.joinsPlanIndex >= 0) continue;

                Vector3 mouth = plan.course[plan.course.Count - 1];
                int at = g.CellAt(mouth.x, mouth.z);
                if (at < 0) continue;

                // Walk the flow graph on from the mouth. A flow path cannot revisit a
                // cell, so Count is a hard backstop, never the real limit.
                List<Vector3> extra = new List<Vector3>();
                int host = -1;
                int budget = count;

                int step = g.DownstreamOf(at);
                while (step >= 0 && budget-- > 0)
                {
                    int o = owner[step];
                    if (o >= 0 && o != p) { host = o; extra.Add(g.WorldOf(step)); break; }

                    // Still on this course's own cells (the mouth region) - keep walking
                    // without drawing, so a course does not append its own tail.
                    if (o != p) extra.Add(g.WorldOf(step));
                    step = g.DownstreamOf(step);
                }

                if (host < 0) continue;                          // reached no other course

                PlannedRiver hostPlan = plans[host];
                if (hostPlan == null) continue;

                // The bigger water is the trunk. A course carrying more land than the one
                // it would join is the trunk itself, and joining it would inseminate the
                // wrong hierarchy - so it is left to be resolved the other way (the host
                // will already point at it, or it stays an inland outflow).
                if (plan.catchmentSquareMetres > hostPlan.catchmentSquareMetres) continue;

                // Append the walked cells so the channel follows the real flow path into
                // the junction; the last appended point is the shared confluence cell,
                // exactly as a tributary ends on its parent's cell.
                for (int e = 0; e < extra.Count; e++) plan.course.Add(extra[e]);

                plan.mouth = MouthKind.River;
                plan.joinsPlanIndex = host;
            }
        }

        /// <summary>
        /// Where a lake overflows: the first cell its own drainage reaches beyond the
        /// lake's footprint.
        ///
        /// Walking the filled surface finds the sill exactly, with no rim search and no
        /// guessing, because the depression fill already raised the basin to its spill
        /// elevation - so D8 on that surface leaves the basin through the one point real
        /// water would leave it by. Returns -1 when the walk never gets clear of the
        /// lake, which is a lake with nowhere to drain to on this grid.
        /// </summary>
        private static int FindLakeSpill(DrainageGrid g, Vector3 centre, float radiusMetres)
        {
            float radiusSq = radiusMetres * radiusMetres;

            int at = g.CellAt(centre.x, centre.z);
            int budget = g.Count;   // a flow path cannot revisit a cell; backstop only

            while (at >= 0 && budget-- > 0)
            {
                Vector3 w = g.WorldOf(at);
                float dx = w.x - centre.x;
                float dz = w.z - centre.z;

                if (dx * dx + dz * dz > radiusSq) return at;

                at = g.DownstreamOf(at);
            }

            return -1;
        }

        // =====================================================================
        //  DELTA ARMS
        // =====================================================================

        /// <summary>
        /// Follows the drainage downstream from a point until it reaches standing water
        /// or leaves the world, and returns it as an editable course.
        ///
        /// This is what a delta distributary is: at a river's mouth the flow splits and
        /// each arm takes its own way to the sea, and the way it takes is the low ground
        /// beside the trunk. Walking the existing flow graph finds that ground, so an
        /// arm cannot run uphill or strike out across a ridge - which is what a separate
        /// stepping tracer used to do.
        ///
        /// Returns null when the walk is too short to be an arm, so a stub is never
        /// handed back to be spawned.
        /// </summary>
        /// <param name="g">A solved grid from <see cref="Solve"/>.</param>
        /// <param name="fromWorld">Where the arm leaves the trunk.</param>
        /// <param name="seaLevelMetres">The sea surface, so the walk stops on reaching
        /// it instead of continuing across the seabed. float.NegativeInfinity for none.</param>
        /// <param name="minLengthMetres">Shorter than this and it is not an arm.</param>
        /// <param name="seedSpacingMetres">Node spacing of the returned course.</param>
        public static List<Vector3> PlanDownstream(DrainageGrid g, Vector3 fromWorld,
                                                  float seaLevelMetres,
                                                  float minLengthMetres,
                                                  float seedSpacingMetres)
        {
            if (g == null) return null;

            // Sea only. A delta arm splits off at the coast and its whole business is
            // reaching the sea, so no lakes are passed and none can stop it inland.
            MouthKind[] waterKind = BuildWaterMask(g, seaLevelMetres, null, null);

            int water;
            List<int> cells = WalkToWater(g, g.CellAt(fromWorld.x, fromWorld.z),
                                          waterKind, null, out water);

            List<Vector3> path = new List<Vector3>(cells.Count + 1);
            for (int i = 0; i < cells.Count; i++) path.Add(g.WorldOf(cells[i]));

            // One cell into the water and stop: the arm has arrived.
            if (water >= 0) path.Add(g.WorldOf(water));

            float length;
            return ToCourse(path, Mathf.Max(1f, minLengthMetres), seedSpacingMetres,
                            2, out length);
        }

        // =====================================================================
        //  WALKING AND SHAPING - shared by every planner above
        // =====================================================================

        /// <summary>
        /// Follows the flow graph from a cell until it reaches standing water, meets a
        /// reach an earlier course already claimed, or leaves the world. Returns the dry
        /// cells walked, in order, and reports the water cell it arrived in separately so
        /// the caller can decide whether to step into it.
        /// </summary>
        /// <remarks>
        /// Every downstream walk in this file goes through here. Walking the D8 graph
        /// rather than stepping by steepest descent is what makes a course continuous and
        /// unable to climb; having ONE implementation of it is what stops a trunk river,
        /// a delta arm and a lake outlet from disagreeing about where water goes.
        /// </remarks>
        private static List<int> WalkToWater(DrainageGrid g, int startCell,
                                             MouthKind[] waterKind, bool[] used,
                                             out int arrivedWater)
        {
            List<int> cells = new List<int>(128);
            arrivedWater = -1;

            int at = startCell;
            int budget = g.Count;   // a flow path cannot revisit a cell; backstop only

            while (at >= 0 && budget-- > 0)
            {
                if (used != null && used[at]) break;

                if (waterKind != null && waterKind[at] != MouthKind.Outflow)
                {
                    arrivedWater = at;
                    break;
                }

                cells.Add(at);
                at = g.DownstreamOf(at);
            }

            return cells;
        }

        /// <summary>
        /// Turns a walked flow path into an editable course: takes the D8 staircase off
        /// it, rejects it if it is a stub, and thins it to nodes. Head first, in place.
        /// Returns null when the path is too short to be a river.
        /// </summary>
        /// <remarks>
        /// D8 can only step on eight bearings, so a flow path always arrives as a
        /// staircase. Relaxing it removes the stair corners BEFORE the nodes are chosen,
        /// which is the difference between a spline through a curve and a spline through
        /// a zigzag. The ends are held, so the source and the mouth stay exactly where
        /// the drainage put them.
        /// </remarks>
        private static List<Vector3> ToCourse(List<Vector3> path, float minLengthMetres,
                                             float seedSpacingMetres, int minNodes,
                                             out float lengthMetres)
        {
            lengthMetres = 0f;
            if (path == null || path.Count < 2) return null;

            RiverCourseSolver.Relax(path, 2);

            lengthMetres = PathLength(path);
            if (lengthMetres < minLengthMetres) return null;

            List<Vector3> seed = Downsample(path, seedSpacingMetres);
            if (seed.Count < minNodes) return null;

            return seed;
        }

        /// <summary>Ground-plan length of a course, in metres.</summary>
        private static float PathLength(List<Vector3> path)
        {
            float total = 0f;

            for (int i = 0; i < path.Count - 1; i++)
            {
                float dx = path[i + 1].x - path[i].x;
                float dz = path[i + 1].z - path[i].z;
                total += Mathf.Sqrt(dx * dx + dz * dz);
            }

            return total;
        }

        /// <summary>
        /// Thins a dense flow path down to editable nodes at a fixed spacing, keeping
        /// the source and the mouth exactly.
        ///
        /// Spacing is by distance travelled along the path, not by index, so a course
        /// that runs straight for a kilometre and then meanders gets nodes evenly
        /// along its length instead of piling them up in the meander.
        /// </summary>
        private static List<Vector3> Downsample(List<Vector3> path, float spacingMetres)
        {
            List<Vector3> result = new List<Vector3>();
            if (path == null || path.Count == 0) return result;

            float spacing = Mathf.Max(1f, spacingMetres);

            result.Add(path[0]);
            float since = 0f;

            for (int i = 1; i < path.Count - 1; i++)
            {
                float dx = path[i].x - path[i - 1].x;
                float dz = path[i].z - path[i - 1].z;
                since += Mathf.Sqrt(dx * dx + dz * dz);

                if (since < spacing) continue;

                result.Add(path[i]);
                since = 0f;
            }

            if (path.Count > 1) result.Add(path[path.Count - 1]);

            return result;
        }
    }
}
