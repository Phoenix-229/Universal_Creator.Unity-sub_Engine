// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    HydrologyNodes.cs
//  Purpose: River tracing and carving, lakes, ocean level, bank seating, watershed
//           basins, wetlands, and submarine terrain.
//
//  They all sit on the shared Hydrology solver, so they agree with each other:
//  the lake surface a river drains into is the same spill elevation Lake Fill
//  floods to, and the basin a wetland sits in is the one Watershed segments.
//  Deriving each independently is how you get rivers that miss their lakes.
//
//  ONE NAME PER SIGNAL
//  -------------------
//  A port pair that does not read as one connection is a graph nobody can wire.
//  So the traced course is called "River Mask" from the node that emits it all the
//  way to the nodes that consume it, and a flooded footprint - which is a different
//  thing, the water's own outline rather than the channel's - is called "Water Mask"
//  by every node that emits one, whether the water is a river, a lake or the sea.
//  Water Bank Seat therefore takes a "Water Mask" from any of them without the user
//  having to work out which name means which.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Traces the world's river network from its own drainage and emits it as a
    /// width-graded mask, 1 on the centreline falling to 0 at the channel edge.
    /// </summary>
    /// <remarks>
    /// SOLVED ONCE FOR THE WORLD, STAMPED PER CHUNK
    /// Flow accumulation is the land draining THROUGH a cell, and that land can
    /// begin tens of chunks upstream. Solved chunk by chunk it restarts from zero at
    /// every seam, so each chunk traces a different network: the courses break into
    /// disconnected veins along the tile grid, one set per chunk, and the bed
    /// see-saws wherever two chunks disagree about which way the water went. No
    /// ghost margin fixes that, because the neighbourhood is unbounded rather than
    /// merely wide - a larger margin only moves the seam outwards.
    ///
    /// So the drainage is solved once over the whole world on a coarse grid, which
    /// is where the TOPOLOGY lives: which way water leaves a cell, and how much land
    /// stands behind it, are decided by the shape of the landscape and not by its
    /// last metre. What comes out is a set of world-space courses, and each chunk
    /// stamps the reaches that cross it at its own full resolution. The network is
    /// therefore identical no matter which chunk asks, while the channel edge stays
    /// as crisp as the heightmap allows.
    ///
    /// WIDTHS AND THRESHOLDS IN REAL UNITS
    /// Both are areas of drained land in square metres, not counts of grid cells, so
    /// the same settings describe the same river whatever resolution the solve runs
    /// at. A cell count silently means a different river on every world size.
    ///
    /// See WorldDrainage for why the solve is coarse, why the stamp is not, and why
    /// none of it is cached.
    /// </remarks>
    [System.Serializable]
    public class RiverNetworkTraceNode : Generator
    {
        public override string NodeName { get { return "River Network Trace"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Traces connected river courses from world-wide flow accumulation."; } }

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>
        /// The traced course. This is the SOURCE FEED of the whole river chain: River
        /// Carve digs it, River Fill fills what it dug, Water Bank Seat seats the banks
        /// against the water that ended up in it. It is named for what it carries, not
        /// as a generic mask, because every node downstream declares a "River Mask"
        /// input and the pair has to read as one connection.
        /// </summary>
        private static readonly Port[] Outs =
        {
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true)
        };

        /// <summary>
        /// Cells per side of the world drainage solve. Coarse on purpose: it decides
        /// where the rivers run, not how sharp their banks are.
        /// </summary>
        public int drainageResolution = 256;

        /// <summary>
        /// Land a channel must drain before it counts as a river, in square metres.
        /// Below roughly a quarter of a square kilometre of temperate hillside the
        /// runoff is sheet flow and gullies that only run after rain, not a permanent
        /// watercourse. Lower it on a small test world, which may not contain a
        /// catchment that large anywhere.
        /// </summary>
        public float channelHeadArea = 250000f;

        /// <summary>Channel width where a channel first forms, in world metres.</summary>
        public float baseWidth = 6f;

        /// <summary>
        /// How width grows with catchment. Downstream hydraulic geometry - Leopold
        /// and Maddock, 1953 - puts width proportional to discharge^0.5.
        /// </summary>
        public float widthExponent = 0.5f;

        /// <summary>Cap on channel width, in world metres.</summary>
        public float maxWidth = 60f;

        /// <summary>Rivers stop at this elevation. Below it they have reached the sea.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Most courses traced. Guards pathological heightfields.</summary>
        public int maxChannels = 512;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix mask = tile.CreateMatrix();

            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      1e-6f, true, stop);
            if (solve == null) return mask;
            if (stop != null && stop.StopRequested) return null;

            return StampNetwork(mask, solve, channelHeadArea, baseWidth, widthExponent,
                                maxWidth, seaLevel, maxChannels, stop) ? mask : null;
        }

        /// <summary>
        /// Draws the whole traced network into a chunk mask. Returns false only when the
        /// solve was cancelled.
        /// </summary>
        /// <remarks>
        /// Shared with the Hydrology Spawner, whose River Mask port has to be the SAME
        /// network this node emits. Traced twice from two copies of this loop, the
        /// spawned river OBJECTS would sit somewhere other than the channel the graph
        /// carved - water beside its own trench, which is the one failure the whole
        /// water chain exists to avoid. One tracing, two callers.
        /// </remarks>
        internal static bool StampNetwork(Matrix mask, WorldDrainage solve, float channelHeadArea,
                                          float baseWidth, float widthExponent, float maxWidth,
                                          float seaLevel, int maxChannels, StopToken stop,
                                          System.Action<List<Vector2>, List<float>> refineCourse = null)
        {
            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            Matrix world = solve.height;
            int resX = solve.resX;

            // The authored figure, measured against the world it is about to be applied
            // to. A quarter of a square kilometre is the right real-world channel head
            // and the wrong threshold for a world smaller than one catchment: nothing
            // clears it, TraceChannels returns nothing, and the River Mask comes back
            // blank while every field on the node looks correctly filled in. The rescale
            // rule lives with the constants it rescales, in RiverNetworkPlanner, and the
            // river OBJECT planner calls the same one - so the courses that get water are
            // the courses this mask drew. Fully qualified rather than imported: this is
            // the one reach across into the terrain layer from here, and naming it says
            // so at the call site.
            float resolvedHeadArea =
                UniverseCreator.TerrainSystem.RiverNetworkPlanner.ResolveChannelHeadArea(
                    solve.accum, solve.cellArea, channelHeadArea);

            float headArea = Mathf.Max(solve.cellArea, resolvedHeadArea);

            List<List<int>> channels = Hydrology.TraceChannels(
                solve.dir, solve.accum, resX, solve.resZ,
                solve.CellsForArea(resolvedHeadArea), Mathf.Max(1, maxChannels), cancel);

            if (stop != null && stop.StopRequested) return false;

            Vector2 solvePixel = world.PixelSize;
            float sea = Mathf.Clamp01(seaLevel);
            float cap = Mathf.Max(baseWidth, maxWidth);
            float originX = world.worldPos.x;
            float originZ = world.worldPos.z;

            // Reused across channels so a world-wide network does not allocate two lists
            // per course.
            List<Vector2> points = new List<Vector2>();
            List<float> widths = new List<float>();

            for (int c = 0; c < channels.Count; c++)
            {
                if (stop != null && stop.StopRequested) return false;

                List<int> channel = channels[c];

                points.Clear();
                widths.Clear();

                float carried = 0f;

                for (int p = 0; p < channel.Count; p++)
                {
                    int i = channel[p];
                    if (world.arr[i] < sea) break;   // reached the sea; the course ends

                    // Hydraulic geometry: discharge grows with catchment and width
                    // with the root of discharge, so a trunk is wide without absurdity.
                    float catchment = solve.AreaOfCells(solve.accum[i]);
                    float grown = Mathf.Pow(Mathf.Max(1f, catchment / headArea), widthExponent);
                    float width = Mathf.Min(baseWidth * grown, cap);

                    // Running maximum, for the reason HydrologyGenerator grades its river
                    // nodes the same way: D8 turns on whole cells, so a course point can
                    // round to a cell just off the thread of the channel and report less
                    // catchment than the reach above it. A river does not lose its
                    // watershed halfway down, and honouring the dip would pinch the mask
                    // to a thread and reopen it - a width that stutters along the course.
                    if (width > carried) carried = width;

                    // XZ only: a course is a plan-view line. Its elevation is whatever the
                    // ground under it turns out to be, which River Carve then cuts.
                    // Inverse of Matrix.PixelToWorld for a local array index.
                    points.Add(new Vector2(originX + (i % resX) * solvePixel.x,
                                           originZ + (i / resX) * solvePixel.y));
                    widths.Add(carried);
                }

                SmoothCourse(points);

                // Optional course refinement (River Course Solver node). The trace above is
                // a D8 thread smoothed once; the refiner runs the real-world course passes
                // (valley-seek onto the thalweg, then meander loops sized by Leopold-Wolman)
                // over the SAME polyline before it is stamped, so the mask the carve reads
                // is a fitted course, not a raw drainage thread. Null for the plain Trace
                // node and the spawner, so their behaviour is unchanged. Point count is
                // preserved by the passes used, so widths stay index-aligned.
                if (refineCourse != null) refineCourse(points, widths);

                for (int p = 1; p < points.Count; p++)
                    StampReach(mask, points[p - 1], widths[p - 1], points[p], widths[p]);
            }

            return true;
        }

        /// <summary>
        /// Takes the corners off a traced course: one pass of a three-point moving
        /// average over the interior points, endpoints held.
        /// </summary>
        /// <remarks>
        /// D8 flow directions can only turn in 45 degree steps, so a course crossing
        /// ground that falls at any other angle is forced to approximate it by
        /// alternating between two of the eight - a staircase. Stamped literally that
        /// reads as a river running in zigzags, which is the second half of the reported
        /// "zigzag" fault and is an artefact of the direction set, not of the terrain.
        ///
        /// Real channels do not have corners: flow has momentum, and a bend tighter than
        /// a few channel widths is cut off rather than followed. Averaging three
        /// consecutive points is the mildest correction that removes a staircase, and it
        /// moves a point by at most two thirds of a solve cell - well inside the corridor
        /// River Carve re-seats the bed within, so the course cannot be nudged out of the
        /// low ground it was traced along. The filter reads the ORIGINAL neighbours, not
        /// its own output, so it stays a single pass and cannot drift.
        /// </remarks>
        private static void SmoothCourse(List<Vector2> points)
        {
            if (points.Count < 3) return;

            Vector2 previous = points[0];

            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector2 here = points[i];
                points[i] = (previous + here + points[i + 1]) / 3f;
                previous = here;
            }
        }

        /// <summary>
        /// Draws one reach of a course into the chunk: a capsule measured in world
        /// metres, tapering from one node's width to the next.
        /// </summary>
        /// <remarks>
        /// Only the pixels the reach can possibly touch are visited - its own bounding
        /// box grown by half a width, clipped to the chunk - so a world-wide network
        /// costs the length of the courses crossing this chunk, not the chunk's area
        /// once per reach. Points are XZ world positions packed into a Vector2.
        /// </remarks>
        private static void StampReach(Matrix mask, Vector2 a, float widthA,
                                       Vector2 b, float widthB)
        {
            float halfMax = Mathf.Max(widthA, widthB) * 0.5f;
            if (halfMax <= 1e-4f) return;

            Vector2 pixel = mask.PixelSize;
            float mppX = Mathf.Max(1e-4f, pixel.x);
            float mppZ = Mathf.Max(1e-4f, pixel.y);

            // A channel cannot be drawn narrower than the grid it is drawn on. Where the
            // real width falls under one pixel the capsule passes BETWEEN pixel centres
            // and paints nothing, so consecutive reaches each leave a single isolated
            // pixel and the trench arrives as a dotted line - the reported "not
            // continuous but zigzag hole". Flooring the coverage radius at half a pixel
            // makes the thinnest channel one continuous pixel wide, which is also the
            // narrowest thing a heightfield of this resolution can physically hold. The
            // real width still decides everything above that floor, so no river is
            // widened; only unrepresentable ones are made continuous.
            float minHalf = 0.5f * Mathf.Max(mppX, mppZ);
            if (halfMax < minHalf) halfMax = minHalf;

            float originX = mask.worldPos.x;
            float originZ = mask.worldPos.z;

            int resX = mask.rect.size.x;
            int resZ = mask.rect.size.z;

            int x0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - halfMax - originX) / mppX);
            int x1 = Mathf.CeilToInt((Mathf.Max(a.x, b.x) + halfMax - originX) / mppX);
            int z0 = Mathf.FloorToInt((Mathf.Min(a.y, b.y) - halfMax - originZ) / mppZ);
            int z1 = Mathf.CeilToInt((Mathf.Max(a.y, b.y) + halfMax - originZ) / mppZ);

            if (x1 < 0 || z1 < 0 || x0 > resX - 1 || z0 > resZ - 1) return;

            if (x0 < 0) x0 = 0;
            if (z0 < 0) z0 = 0;
            if (x1 > resX - 1) x1 = resX - 1;
            if (z1 > resZ - 1) z1 = resZ - 1;

            Vector2 along = b - a;
            float lengthSq = along.sqrMagnitude;

            for (int z = z0; z <= z1; z++)
            {
                int row = z * resX;
                float worldZ = originZ + z * mppZ;

                for (int x = x0; x <= x1; x++)
                {
                    float worldX = originX + x * mppX;

                    // How far along the reach the nearest point lies, 0-1.
                    float t = 0f;
                    if (lengthSq > 1e-6f)
                    {
                        float dot = (worldX - a.x) * along.x + (worldZ - a.y) * along.y;
                        t = Mathf.Clamp01(dot / lengthSq);
                    }

                    float half = Mathf.Lerp(widthA, widthB, t) * 0.5f;
                    if (half < minHalf) half = minHalf;

                    float offX = worldX - (a.x + along.x * t);
                    float offZ = worldZ - (a.y + along.y * t);
                    float d = Mathf.Sqrt(offX * offX + offZ * offZ);
                    if (d >= half) continue;

                    // 1 on the thalweg falling to 0 at the bank, so the mask doubles as
                    // distance from the centreline: River Carve reads it to shape the
                    // cross-section and River Fill to find the bank.
                    float v = 1f - InterpolationMath.SmoothStep(d / half);
                    if (v > mask.arr[row + x]) mask.arr[row + x] = v;
                }
            }
        }
    }

    /// <summary>
    /// Traces the world network exactly as River Network Trace does, then runs the
    /// real-world course passes over each course before it is stamped: valley-seek
    /// onto the thalweg (endpoints held), a light relax, and meander loops sized by
    /// Leopold and Wolman. The output is the SAME "River Mask" port the plain trace
    /// emits, so River Carve reads it identically - the difference is the mask now
    /// carries fitted, meandering courses rather than raw D8 threads.
    /// </summary>
    /// <remarks>
    /// WHY A NODE AND NOT A DUPLICATE
    /// The graph has no polyline port, so a course solver cannot be a spline in and a
    /// spline out. It shares River Network Trace's one tracing (StampNetwork) and
    /// injects the solver's passes through the refineCourse hook - the passes that are
    /// NOT in SmoothCourse. So there is exactly one place that traces the network and
    /// one static course solver; this node wires them together, it does not re-derive
    /// either.
    ///
    /// ENDPOINT HOLD
    /// Every pass used here holds i=0 (source) and i=n-1 (mouth), so the fitted course
    /// still starts at its spring and ends where the raw thread reached the sea. The
    /// passes preserve point count, so widths stay index-aligned with points.
    /// </remarks>
    [System.Serializable]
    public class RiverCourseSolverNode : Generator
    {
        public override string NodeName { get { return "River Course Solver"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Traces river courses, then fits them to the valley and meanders them by real-world course law."; } }

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>
        /// The fitted course, emitted under the SAME name River Network Trace uses so
        /// River Carve / River Fill / Water Bank Seat consume it with no rewiring.
        /// </summary>
        private static readonly Port[] Outs =
        {
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true)
        };

        /// <summary>Cells per side of the world drainage solve. See River Network Trace.</summary>
        public int drainageResolution = 256;

        /// <summary>Land a channel must drain before it counts as a river, in square metres.</summary>
        public float channelHeadArea = 250000f;

        /// <summary>Channel width where a channel first forms, in world metres.</summary>
        public float baseWidth = 6f;

        /// <summary>How width grows with catchment (Leopold and Maddock: w ∝ Q^0.5).</summary>
        public float widthExponent = 0.5f;

        /// <summary>Cap on channel width, in world metres.</summary>
        public float maxWidth = 60f;

        /// <summary>Rivers stop at this elevation. Below it they have reached the sea.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Most courses traced. Guards pathological heightfields.</summary>
        public int maxChannels = 512;

        /// <summary>How far to either side the valley-seek looks for lower ground, in metres.</summary>
        public float valleySearchMetres = 40f;

        /// <summary>How hard a point is pulled toward the valley floor, 0-1.</summary>
        public float valleyPull = 0.6f;

        /// <summary>Meander amplitude, 0-1. 0 leaves the course straight-ish.</summary>
        public float meanderStrength = 0.5f;

        /// <summary>Steepest grade a meander loop may climb into a bank before it is refused.</summary>
        public float meanderMaxGrade = 0.06f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix mask = tile.CreateMatrix();

            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      1e-6f, true, stop);
            if (solve == null) return mask;
            if (stop != null && stop.StopRequested) return null;

            // The course solver samples the SAME world height the drainage was solved on,
            // so the valley it seeks is the valley the network was traced through. XZ is a
            // plan-view line; Y is whatever the ground under it turns out to be.
            UniverseCreator.TerrainSystem.RiverCourseSolver.GroundSampler ground =
                delegate (float wx, float wz) { return solve.height.GetWorldHeight(wx, wz); };

            float search = Mathf.Max(0f, valleySearchMetres);
            float pull = Mathf.Clamp01(valleyPull);
            float meander = Mathf.Clamp01(meanderStrength);
            float grade = Mathf.Max(0.002f, meanderMaxGrade);

            // Scratch lists reused across every course; the delegate is called once per
            // course by StampNetwork, in-between the trace and the stamp.
            List<Vector3> course = new List<Vector3>();

            System.Action<List<Vector2>, List<float>> refine =
                delegate (List<Vector2> pts, List<float> widths)
                {
                    if (pts == null || pts.Count < 2) return;

                    course.Clear();
                    for (int i = 0; i < pts.Count; i++)
                        course.Add(new Vector3(pts[i].x, ground(pts[i].x, pts[i].y), pts[i].y));

                    // Real-world course passes, endpoints held so the source and mouth do
                    // not move. Count is preserved by all three, so widths stay aligned.
                    UniverseCreator.TerrainSystem.RiverCourseSolver.SnapToValley(
                        course, ground, search, pull, 12, 0f, true);
                    UniverseCreator.TerrainSystem.RiverCourseSolver.Relax(course, 2);
                    UniverseCreator.TerrainSystem.RiverCourseSolver.Meander(
                        course, ground, widths, meander, grade, Mathf.Max(0.5f, baseWidth));

                    // Write the fitted planform back; the stamp reads XZ only.
                    int n = Mathf.Min(pts.Count, course.Count);
                    for (int i = 0; i < n; i++)
                        pts[i] = new Vector2(course[i].x, course[i].z);
                };

            return RiverNetworkTraceNode.StampNetwork(
                mask, solve, channelHeadArea, baseWidth, widthExponent, maxWidth,
                seaLevel, maxChannels, stop, refine) ? mask : null;
        }
    }

    /// <summary>
    /// Cuts the river channel into the heightfield. Separate from the trace so a
    /// user can carve one network and texture another, and so the carve can be
    /// masked without recomputing drainage.
    /// </summary>
    [System.Serializable]
    public class RiverCarveNode : Generator
    {
        public override string NodeName { get { return "River Carve"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts a channel profile into the terrain along a river mask."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("River Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>Channel depth at full mask, as a fraction of world height.</summary>
        public float depth = 0.012f;

        /// <summary>Width of the banks either side of the channel, in metres.</summary>
        public float bankWidth = 25f;

        /// <summary>Cross-section shape. 1 is V-shaped, 2+ approaches a flat bed.</summary>
        public float profilePower = 1.6f;

        /// <summary>Flattens the channel floor so the river bed does not undulate.</summary>
        public float bedFlattening = 0.6f;

        /// <summary>Raises a levee alongside the channel, as real rivers deposit.</summary>
        public float leveeHeight;

        /// <summary>
        /// Floodplain half-width as a multiple of the local channel width (W_fp ≈ k·w).
        /// Real unconfined reaches carry k ≈ 3–8; 0 turns the floodplain off and only the
        /// channel is cut. The belt is the gently graded, near-level ground a hand-placed
        /// river drops into and meanders across - NOT a constant width, because it is
        /// measured from the channel width at each point and then pulled in wherever the
        /// valley is confined (both sides rising within the search), so a gorge reach gets
        /// a narrow asymmetric plain and an open reach a wide gentle one.
        /// </summary>
        public float floodplainWidthK = 5f;

        /// <summary>
        /// How far below the surrounding ground the floodplain belt is graded, as a
        /// fraction of the channel depth. The belt is not cut to the channel bed - it is a
        /// shallow, near-level bench a little above the water - so a river laid along the
        /// carved trench sits IN its floodplain instead of in a slot. 0 leaves the belt at
        /// ground level (a graded shelf with no lowering).
        /// </summary>
        public float floodplainDepthFraction = 0.25f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix river = GetInputMatrix(tile, 1, stop);
            if (river == null) return new Matrix(src);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = new Matrix(src);

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            Vector2 ps = src.PixelSize;
            float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);

            // Bank width in pixels sets the blur sigma used to find the local river
            // grade: the bed follows the smoothed valley floor, not every bump.
            float sigma = Mathf.Clamp(bankWidth / mpp, 0.5f, 32f);
            Matrix smoothed = MatrixOperations.GaussianBlur(src, sigma);

            if (stop != null && stop.StopRequested) return null;

            // WHERE THE VALLEY FLOOR ACTUALLY IS.
            //
            // The mask is stamped from a drainage solve a few hundred cells across, so
            // a traced centreline is only accurate to one solve cell - twenty metres or
            // more on a normal world. On open ground that error is invisible. On a
            // hillside it puts the line up the slope from the low ground it is meant to
            // follow, and cutting a fixed depth below THAT grooves the hillside: a
            // channel perched on a slope with its downhill shoulder standing over it
            // like a ridge. That is the reported "rivers on the sides of hills, banks
            // like a hill instead of a valley".
            //
            // Water does not do that. A channel occupies the lowest ground in its own
            // CROSS-SECTION - the line across the flow - so the bed is seated on that
            // line and the solve's positional error stops mattering.
            //
            // The cross-section is a line, not a box. This used to take the minimum over
            // a square window, which also reached UP- and DOWN-stream: a pool a bank
            // width downstream then set the bed here, dragging the profile into flat
            // steps, and the window's own corners printed the axis-aligned rectangular
            // footprints around every channel. A line across the flow has neither fault,
            // and costs less because it is only read where the mask is wet.
            int corridor = Mathf.Clamp(Mathf.RoundToInt(bankWidth / mpp), 1, 64);

            // The mask at the corridor's own scale. The raw mask is a hard stamp whose
            // gradient is meaningless between cells; blurred, it is a smooth ridge whose
            // structure gives the direction across the channel at every wet cell.
            Matrix maskSmooth = MatrixOperations.GaussianBlur(river, sigma);
            if (stop != null && stop.StopRequested) return null;

            float power = Mathf.Max(0.2f, profilePower);
            float flatten = Mathf.Clamp01(bedFlattening);

            // Floodplain belt (§4). Half-width scales with the channel (W_fp ≈ k·w), so it
            // is NOT a constant: a wider channel carries a wider plain. Bounded in pixels so
            // a huge k on a fine chunk cannot run the march away. The belt is graded to a
            // BENCH a little below the local valley floor and well above the channel bed, so
            // a hand-placed river laid along the trench drops into its floodplain instead of
            // a slot. The march stops where the ground rises out of the belt band - that is
            // the valley wall - which is what pulls the belt in on a confined reach and lets
            // it spread on an open one, the confinement trade the spec asks for.
            int fpHalfPixels = floodplainWidthK > 1e-3f
                ? Mathf.Clamp(Mathf.RoundToInt(floodplainWidthK * bankWidth / mpp), 0, 64)
                : 0;
            float fpDepthFrac = Mathf.Clamp01(floodplainDepthFraction);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float w = Mathf.Clamp01(river.arr[i]);
                    if (w <= 0.001f) continue;

                    // Mask value doubles as distance from the centreline: 1 at the
                    // thalweg, 0 at the bank. Raising it to a power sets the profile.
                    float profile = Mathf.Pow(w, power);

                    // Seated on the valley floor, then flattened along it. Taking the
                    // lowest ground across the channel first is what stops a hillside
                    // course from being cut as a perched groove: bed is the low ground,
                    // wherever on the cross-section that turns out to be.
                    float floorHere = LowestAcross(src, maskSmooth, x, z, resX, resZ, corridor);
                    float bed = Mathf.Lerp(floorHere, smoothed.arr[i], flatten * w);
                    float cut = bed - depth * profile;

                    float value = Mathf.Min(dst.arr[i], cut);

                    if (leveeHeight > 1e-5f)
                    {
                        // Levees sit on the shoulder: strongest where the mask is
                        // partial, absent in the channel itself.
                        //
                        // Capped at the ground that was here, because this is the one
                        // place the carve could RAISE terrain. A levee that climbs above
                        // untouched ground is the ridge-beside-the-channel fault: real
                        // levees are deposited from the channel's own spoil and stand
                        // above the FLOODPLAIN, never above the hillside behind them.
                        // Off by default; this keeps it honest when turned on.
                        float shoulder = w * (1f - w) * 4f;
                        value = Mathf.Min(src.arr[i], value + leveeHeight * shoulder);
                    }

                    dst.arr[i] = Mathf.Clamp01(value);

                    // Grade the floodplain from the channel CORE only (w near the thalweg),
                    // so the march runs once per centreline cell rather than from every
                    // partial-bank cell. The bench is a shallow bench below the valley floor
                    // and above the channel bed - the near-level ground the river meanders
                    // across.
                    if (fpHalfPixels > 0 && w > 0.5f)
                    {
                        float benchLevel = bed - depth * fpDepthFrac;
                        GradeFloodplain(src, dst, maskSmooth, x, z, resX, resZ,
                                        fpHalfPixels, benchLevel, depth);
                    }
                }
            }

            return dst;
        }

        /// <summary>
        /// Grades the floodplain belt out from a channel-core cell, across the flow, to
        /// <paramref name="halfPixels"/> either side. Each cell in the belt is lowered
        /// toward <paramref name="benchLevel"/> (never raised) with a smooth falloff by
        /// distance, so the plain eases from the bench near the channel back to natural
        /// ground at the belt edge. The march STOPS on each side the moment the ground
        /// rises more than the channel depth above the bench: that is the valley wall, so a
        /// confined reach gets a narrow belt and cannot be cut into a hillside, while an
        /// open reach spreads to the full width - the confinement trade (§4). Reads ground
        /// from <paramref name="src"/> and writes the minimum into <paramref name="dst"/>,
        /// so the result does not depend on the order cells are visited.
        /// </summary>
        private static void GradeFloodplain(Matrix src, Matrix dst, Matrix mask,
                                            int x, int z, int resX, int resZ,
                                            int halfPixels, float benchLevel, float depth)
        {
            float dx, dz;
            if (!CrossDirection(mask, x, z, resX, resZ, out dx, out dz)) return;

            float wallBand = Mathf.Max(1e-4f, depth);   // rise that ends the belt = one channel depth

            for (int sign = -1; sign <= 1; sign += 2)
            {
                for (int k = 1; k <= halfPixels; k++)
                {
                    int ax = Mathf.Clamp(x + Mathf.RoundToInt(dx * k * sign), 0, resX - 1);
                    int az = Mathf.Clamp(z + Mathf.RoundToInt(dz * k * sign), 0, resZ - 1);
                    int ai = az * resX + ax;

                    float ground = src.arr[ai];

                    // Valley wall: the ground has climbed a channel depth above the bench.
                    // Stop here - beyond is hillside, not floodplain, and grading it would
                    // cut a shelf into the slope (the exact fault the belt must avoid).
                    if (ground - benchLevel > wallBand) break;

                    // Already at or below the bench (in the trench, or a lower plain): the
                    // channel carve owns it, nothing to grade.
                    if (ground <= benchLevel) continue;

                    // Smooth falloff: full pull to the bench near the channel, easing to no
                    // change at the belt edge, so the plain grades gently rather than
                    // stepping down.
                    float t = 1f - (k / (float)halfPixels);
                    float ease = t * t * (3f - 2f * t);   // smoothstep
                    float target = Mathf.Lerp(ground, benchLevel, ease);

                    if (target < dst.arr[ai]) dst.arr[ai] = Mathf.Clamp01(target);
                }
            }
        }

        /// <summary>
        /// Lowest ground on the line ACROSS the channel through (x, z), out to
        /// <paramref name="radius"/> pixels either side. Returns the cell's own height
        /// when no cross direction can be read.
        /// </summary>
        /// <remarks>
        /// Clamped at the edges rather than wrapped: a chunk's neighbour is a different
        /// matrix, so the line is measured against the ground this chunk has.
        /// </remarks>
        private static float LowestAcross(Matrix src, Matrix mask, int x, int z,
                                          int resX, int resZ, int radius)
        {
            float low = src.arr[z * resX + x];

            float dx, dz;
            if (!CrossDirection(mask, x, z, resX, resZ, out dx, out dz)) return low;

            for (int k = 1; k <= radius; k++)
            {
                float ox = dx * k;
                float oz = dz * k;

                int ax = Mathf.Clamp(x + Mathf.RoundToInt(ox), 0, resX - 1);
                int az = Mathf.Clamp(z + Mathf.RoundToInt(oz), 0, resZ - 1);
                float a = src.arr[az * resX + ax];
                if (a < low) low = a;

                int bx = Mathf.Clamp(x - Mathf.RoundToInt(ox), 0, resX - 1);
                int bz = Mathf.Clamp(z - Mathf.RoundToInt(oz), 0, resZ - 1);
                float b = src.arr[bz * resX + bx];
                if (b < low) low = b;
            }

            return low;
        }

        /// <summary>
        /// Unit vector across the channel at (x, z), from the mask's structure tensor.
        /// False when the mask is featureless there and no cross-section exists.
        /// </summary>
        /// <remarks>
        /// A plain gradient will not do. The mask ridges along the thalweg, so its
        /// gradient vanishes exactly on the centreline - the one place the seat matters
        /// most - and picking an axis to fall back on is what puts axis-aligned edges
        /// back into the result. The structure tensor sums g outer g over a window, and
        /// the two banks' gradients are antiparallel, so they reinforce there instead of
        /// cancelling. Its principal axis is the cross-section; flow runs perpendicular
        /// to it. Undirected by construction, which is why both signs are sampled above.
        /// </remarks>
        private static bool CrossDirection(Matrix mask, int x, int z, int resX, int resZ,
                                           out float dx, out float dz)
        {
            dx = 0f;
            dz = 0f;
            if (mask == null || resX < 3 || resZ < 3) return false;

            float jxx = 0f, jxz = 0f, jzz = 0f;

            for (int oz = -1; oz <= 1; oz++)
            {
                int cz = Mathf.Clamp(z + oz, 1, resZ - 2);

                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = Mathf.Clamp(x + ox, 1, resX - 2);
                    int c = cz * resX + cx;

                    float gx = mask.arr[c + 1] - mask.arr[c - 1];
                    float gz = mask.arr[c + resX] - mask.arr[c - resX];

                    jxx += gx * gx;
                    jxz += gx * gz;
                    jzz += gz * gz;
                }
            }

            if (jxx + jzz < 1e-10f) return false;

            float angle = 0.5f * Mathf.Atan2(2f * jxz, jxx - jzz);
            dx = Mathf.Cos(angle);
            dz = Mathf.Sin(angle);
            return true;
        }
    }

    /// <summary>
    /// Cuts a lake BASIN into the heightfield: a rugged, non-constant-depth bed with a
    /// wave-cut shelf near the shore, a dish falling to the deep, and low-amplitude bed
    /// ruggedness. The lake counterpart of River Carve - it authors the terrain the
    /// water sits in, so a lake placed on this bed is flush to its shore and never
    /// floats. Reads the Lake Mask (where water stands) and the Water Level (the flat
    /// surface it fills to); the depth profile runs from 0 at the waterline to full in
    /// the deep, shaped by the shared WaterBedMath.LakeBasin profile.
    /// </summary>
    /// <remarks>
    /// WHY THE PROFILE IS A DISTANCE FIELD, NOT THE MASK
    /// The Lake Mask is 1 across the whole interior and only softens at the very edge,
    /// so it cannot say how far from shore a cell is. A shore-to-deep fraction has to
    /// come from an actual distance to the waterline. SpreadLinear on the INVERTED mask
    /// gives distance inward from the dry bank, from which t (0 at the shore, 1 in the
    /// deep) falls straight out - the same t the runtime WaterCarveLayer feeds
    /// WaterBedMath.DepthFraction, so the graph guide and the placed lake read the same
    /// bed shape.
    ///
    /// DEEPENING ONLY. Like River Carve, the bed is a Min against the ground: a basin
    /// is a hole, and lifting ground to meet a shallow bed would fill a valley the lake
    /// happens to sit in.
    /// </remarks>
    [System.Serializable]
    public class LakeBasinNode : Generator
    {
        public override string NodeName { get { return "Lake Basin"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts a rugged, shelved lake basin into the terrain under a lake mask."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Lake Mask", PortType.Matrix, PortDirection.Input),
            new Port("Water Level", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>Deepest the basin digs below its surface, as a fraction of world height.</summary>
        public float maxDepth = 0.03f;

        /// <summary>How far from shore the dish reaches full depth, in metres.</summary>
        public float shoreRunMetres = 120f;

        /// <summary>
        /// How much of the shore-to-deep run is the wave-cut shelf (shallow bench).
        /// Read by the LakeBasin profile - see WaterBedMath.DepthFraction.
        /// </summary>
        public float shelfFraction = 0.25f;

        /// <summary>Bed roughness amplitude, as a fraction of world height. 0 is a smooth dish.</summary>
        public float ruggedness = 0.004f;

        /// <summary>Feature size of the bed roughness, in metres.</summary>
        public float ruggednessFeatureMetres = 40f;

        /// <summary>Octaves of the bed roughness.</summary>
        public int ruggednessOctaves = 3;

        /// <summary>Roughness persistence, 0..1.</summary>
        public float ruggednessPersistence = 0.5f;

        /// <summary>Seed for the bed roughness, so a basin is identical on every rebuild.</summary>
        public int seed = 12345;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix lake = GetInputMatrix(tile, 1, stop);
            if (lake == null) return new Matrix(src);
            if (stop != null && stop.StopRequested) return null;

            Matrix waterLevel = GetInputMatrix(tile, 2, stop);

            Matrix dst = new Matrix(src);

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            Vector2 ps = src.PixelSize;
            float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);

            // Distance INWARD from the shore, in pixels, capped at the shore run. On the
            // inverted mask the dry bank is the seed, so the field is 1 at the waterline
            // falling to 0 in the deep; t is its complement, matching WaterBedMath.
            float runPixels = Mathf.Max(1f, shoreRunMetres / mpp);
            Matrix inv = src.Blank();
            for (int i = 0; i < inv.arr.Length; i++)
                inv.arr[i] = 1f - Mathf.Clamp01(lake.arr[i]);

            Matrix shoreField = MatrixOperations.SpreadLinear(inv, 0.5f, runPixels);
            if (stop != null && stop.StopRequested) return null;

            float depth = Mathf.Max(0f, maxDepth);
            float shelf = Mathf.Clamp(shelfFraction, 0.02f, 0.9f);
            float rug = Mathf.Max(0f, ruggedness);
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float wet = Mathf.Clamp01(lake.arr[i]);
                    if (wet <= 0.001f) continue;

                    // t = 0 at the waterline, 1 in the deep. shoreField is 1 at the bank.
                    float t = Mathf.Clamp01(1f - shoreField.arr[i]);

                    // Shared lake profile: shallow shelf, then a dish. bankExponent is
                    // unused by the LakeBasin case, so any value passes. Fully qualified:
                    // WaterBedMath lives in the terrain layer, reached the same way the
                    // trace node reaches RiverNetworkPlanner.
                    float frac = UniverseCreator.TerrainSystem.WaterBedMath.DepthFraction(
                        UniverseCreator.TerrainSystem.BedProfile.LakeBasin, t, shelf, 2f);

                    // Surface the basin is measured DOWN from: the flat fill level where
                    // wired, else the local ground (a self-consistent hollow).
                    float surface = waterLevel != null
                        ? Mathf.Clamp01(waterLevel.arr[i])
                        : src.arr[i];

                    float bed = surface - depth * frac;

                    // Bed ruggedness, faded out toward the shore so it never breaks the
                    // waterline into a dashed line of islands (t^2 fade, as the runtime
                    // carve does).
                    if (rug > 1e-6f)
                    {
                        float fade = t * t;
                        float worldX = originX + x * ps.x;
                        float worldZ = originZ + z * ps.y;
                        float n = UniverseCreator.TerrainSystem.WaterBedMath.Ruggedness(
                            worldX, worldZ, ruggednessFeatureMetres,
                            ruggednessOctaves, ruggednessPersistence, seed);
                        bed += n * rug * fade;
                    }

                    // Never above its own surface, and deepening only.
                    if (bed > surface) bed = surface;
                    dst.arr[i] = Mathf.Clamp01(Mathf.Min(dst.arr[i], bed));
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Floods every closed basin to its spill elevation. Port 0 is the water mask,
    /// port 1 the depth, port 2 the surface elevation, so the texture graph can fade
    /// a lake from shallow shore to deep centre without a second solve.
    /// </summary>
    /// <remarks>
    /// SOLVED FOR THE WORLD, SHORELINE CUT PER CHUNK
    /// A lake's level is a property of its whole rim: the water rises until it finds
    /// the lowest point it can escape through, and that point may be in another
    /// chunk. Filled chunk by chunk, a lake spanning a seam gets a different level on
    /// each side - a step straight across open water. So the fill runs once over the
    /// world (see WorldDrainage) and each chunk reads the surface back.
    ///
    /// The surface is read as the LOWEST of the four surrounding solve cells. Inside
    /// the lake all four hold the same spill elevation, so the surface is exactly
    /// flat; at the rim the land cells stand higher, so the lowest is still the spill
    /// elevation and the water never ramps up the bank. Where two lakes at different
    /// levels touch, the rim of the higher one reads the lower level for one solve
    /// cell - shallower, never wider.
    ///
    /// The GROUND it is subtracted from stays at chunk resolution, so the shoreline
    /// is as sharp as the heightmap: a coarse solve does not give a coarse coast.
    ///
    /// A MINIMUM AREA, BECAUSE PUDDLES ARE NOT LAKES
    /// Every pit in a noise field is a closed basin, and flooding all of them
    /// speckles the terrain with pond-sized discs that read as artefacts. Each body
    /// is measured as a whole - half a lake is not a lake - and anything under the
    /// minimum is left as dry ground.
    /// </remarks>
    [System.Serializable]
    public class LakeFillNode : Generator
    {
        public override string NodeName { get { return "Lake Fill"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        // Role: LAKE SURFACE LEVEL. It computes the flat elevation each basin fills to
        // (plus the wet mask and depth that fall out of it). It does NOT place or shape a
        // basin - Lake Basin cuts the rugged bed, and the water body reads this level to
        // know how high to fill. Kept separate so the surface and the bed have one author
        // each and cannot disagree about the waterline.
        public override string Tooltip { get { return "Lake surface level: floods closed basins to their spill elevation. Outputs mask, depth and level."; } }

        // Same split as River Fill: the input mask RESTRICTS where lakes may form, the
        // first output is the water that ended up there. One name for both made the
        // feed and the result indistinguishable in the graph.
        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Water Level", PortType.Matrix, PortDirection.Output, true)
        };

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Lake Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Cells per side of the world fill. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>
        /// Water a body must cover before it counts as a lake, in square metres.
        /// Sixty square metres - a patch about eight metres across - is the floor:
        /// anything smaller is a rain puddle in a heightmap dimple and is left dry.
        /// Above that the SIZE IS NOT SET HERE. Each basin fills to its own spill
        /// elevation, so one world gives a tarn and a broad lake from the same
        /// number, which is what a real landscape does. Raise it to keep only the
        /// big water; lowering it below 60 starts reading noise as lakes.
        /// </summary>
        public float minAreaSquareMetres = 60f;

        /// <summary>Basins shallower than this are ignored, 0..1 of world height.</summary>
        public float minDepth = 0.002f;

        /// <summary>Deepest a lake reads as full, used to normalise the depth port.</summary>
        public float depthNormalise = 0.05f;

        /// <summary>Sea level, 0..1 of world height. Lakes do not form at or below it.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Softness of the shoreline in the mask, 0..1.</summary>
        public float shoreSoftness = 0.15f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            Matrix inputMask = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int count = src.arr.Length;
            if (count == 0) return dst;

            float floor = Mathf.Max(0f, minDepth);
            float sea = Mathf.Clamp01(seaLevel);

            // Zero epsilon: a lake surface is genuinely flat, and here that is wanted.
            // No flow needed - only the filled surface decides where water stands.
            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                     0f, false, stop);
            if (stop != null && stop.StopRequested) return null;

            // No solve means no standing water anywhere. Water Level still answers with
            // the ground so a wired port is never a hole in the world.
            if (solve == null)
                return outputIndex == 2 ? new Matrix(src) : dst;

            float[] worldLevel = LakeSurface(solve, floor, sea,
                                             Mathf.Max(0f, minAreaSquareMetres), stop);
            if (stop != null && stop.StopRequested) return null;

            float[] level = solve.ReadAtChunk(worldLevel, dst, WorldRead.Minimum);
            if (stop != null && stop.StopRequested) return null;

            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);
            float soft = Mathf.Clamp01(shoreSoftness);

            for (int i = 0; i < count; i++)
            {
                float ground = src.arr[i];
                float surface = level[i];
                float d = surface - ground;

                if (surface <= sea + 1e-5f || d < floor)
                {
                    dst.arr[i] = outputIndex == 2 ? ground : 0f;
                    continue;
                }

                if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(d * norm); continue; }
                if (outputIndex == 2) { dst.arr[i] = Mathf.Clamp01(surface); continue; }

                // Mask: fade in over the first slice of depth so the shoreline is not
                // a hard one-pixel edge where it meets a shallow gradient.
                float edge = soft > 1e-4f
                    ? Mathf.Clamp01((d - floor) / (soft * Mathf.Max(1e-4f, depthNormalise)))
                    : 1f;

                float finalMask = InterpolationMath.SmoothStep(edge);
                if (inputMask != null) finalMask *= Mathf.Clamp01(inputMask.arr[i]);

                dst.arr[i] = finalMask;
            }

            return dst;
        }

        /// <summary>
        /// World-scale water surface: the spill elevation of every basin large enough
        /// to be a lake, and bare ground everywhere else.
        /// </summary>
        /// <remarks>
        /// The flood fill is iterative over an explicit stack rather than recursive: a
        /// single basin can cover most of the grid, and recursion would overflow the
        /// worker thread's stack long before it finished.
        ///
        /// Shared with the Hydrology Spawner for the same reason River Network Trace's
        /// stamp is: the basins this finds are the basins the spawner places Lake
        /// objects in. A second copy of the fill would drift from this one and put a
        /// lake where no basin was flooded.
        /// </remarks>
        internal static float[] LakeSurface(WorldDrainage solve, float floor, float sea,
                                            float minArea, StopToken stop)
        {
            int resX = solve.resX;
            int resZ = solve.resZ;
            int count = resX * resZ;

            float[] ground = solve.height.arr;
            float[] filled = solve.filled;

            float[] level = new float[count];
            bool[] wet = new bool[count];

            for (int i = 0; i < count; i++)
            {
                level[i] = ground[i];
                wet[i] = filled[i] > sea + 1e-5f && filled[i] - ground[i] > floor;
            }

            bool[] seen = new bool[count];
            int[] stack = new int[count];
            int[] body = new int[count];

            for (int start = 0; start < count; start++)
            {
                if (!wet[start] || seen[start]) continue;
                if (stop != null && stop.StopRequested) return level;

                // One standing body: every wet cell reachable from this one.
                int top = 0;
                int size = 0;
                stack[top++] = start;
                seen[start] = true;

                while (top > 0)
                {
                    int i = stack[--top];
                    body[size++] = i;

                    int x = i % resX;
                    int z = i / resX;

                    if (x > 0) { int n = i - 1; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (x < resX - 1) { int n = i + 1; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (z > 0) { int n = i - resX; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (z < resZ - 1) { int n = i + resX; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                }

                if (solve.AreaOfCells(size) < minArea) continue;   // a pond, not a lake

                for (int k = 0; k < size; k++)
                    level[body[k]] = filled[body[k]];
            }

            return level;
        }
    }

    /// <summary>
    /// Turns a carved river channel into actual water: the surface the flow stands
    /// at, how deep it is, and where its edge lies.
    /// </summary>
    /// <remarks>
    /// The five ports are one calculation, not three. Height and River Mask come in,
    /// the channel is measured against its own banks, and Water Level, Depth and the
    /// cleaned River Mask all fall out of that single surface - so they cannot
    /// disagree about where the waterline is.
    ///
    /// WHY THE SURFACE IS MEASURED RATHER THAN OFFSET
    /// A water surface is flat across the channel and never climbs along it. Adding a
    /// constant to the ground fails both tests: it reproduces every bump of the bed,
    /// so the "surface" undulates and the depth comes out the same everywhere. Here
    /// the bed and the bank crest are each read as a sliding extreme over the
    /// channel's own width - a min for the thalweg, a max for the crest. Both are
    /// flat across the channel by construction and change only as the window slides
    /// along the valley, so the surface is level bank to bank and follows the valley
    /// downhill. The stage is then a fraction of the way from bed to crest: the
    /// channel filled to a height, which is what bankfull geometry describes.
    ///
    /// Depth is measured from that surface down to the real ground, so it reaches
    /// zero exactly where the water meets the bank and the mask edge IS the
    /// waterline - no gap and no overlap for the shader to expose.
    ///
    /// The bed is read from a depression-filled copy of the height so that a channel
    /// which see-saws by a pixel cannot make the water climb: filling guarantees a
    /// downhill path from every source to the outlet.
    /// </remarks>
    [System.Serializable]
    public class RiverFillNode : Generator
    {
        public override string NodeName { get { return "River Fill"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Computes river water surfaces and depths. Outputs mask and depth."; } }

        // The input is the CHANNEL - the course River Network Trace drew. The first
        // output is the WATER - the part of that channel the stage actually covers,
        // which is narrower and stops short of the head where the channel is too
        // shallow to hold anything. Two different things, so two different names; they
        // used to share one and the graph could not tell the feed from the result.
        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Water Level", PortType.Matrix, PortDirection.Output, true)
        };

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("River Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>
        /// Distance out to the bank crest, in world metres. Set it to the River
        /// Carve node's bank width: it is the same bank being described.
        /// </summary>
        public float bankWidth = 25f;

        /// <summary>How full the channel runs, 0-1. 1 is brim-full at the crest.</summary>
        public float bankfullFraction = 0.7f;

        /// <summary>Water thinner than this does not count as river, 0-1 of world height.</summary>
        public float minDepth = 0.0005f;

        /// <summary>
        /// Depth at which the depth port saturates. Smaller than the lake node's
        /// because a river of the same standing is far shallower than a lake.
        /// </summary>
        public float depthNormalise = 0.02f;

        /// <summary>Sea level, 0-1 of world height. At and below it the river surface is the sea's.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Softness of the waterline in the mask, 0-1.</summary>
        public float shoreSoftness = 0.15f;

        // Out-of-channel sentinels for the two sliding windows: high enough that a
        // pixel outside the course can never win the minimum, low enough that it can
        // never win the maximum. Heights are 0-1 of world height, so these sit far
        // outside any real value while staying ordinary finite floats.
        private const float OutsideForMin = 1e6f;
        private const float OutsideForMax = -1e6f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            // The bed and crest are sliding extremes over the channel's own width, so
            // a pixel within one window of the chunk edge would otherwise measure only
            // the part of the channel that happens to be inside this chunk - a
            // different water level on each side of the seam. A ghost ring the width
            // of the window makes every returned pixel see its whole channel; the ring
            // is cropped off at the end. Bounded neighbourhood, so a margin is the
            // complete fix here - see TileWindows.
            int margin = WindowRadius(MetresPerPixel(tile));
            TileData view = TileWindows.Expand(tile, margin);
            if (view == null) { view = tile; margin = 0; }

            Matrix src = view.PullInput(this, 0, stop) as Matrix;
            if (src == null) return tile.CreateMatrix();
            Matrix inputMask = view.PullInput(this, 1, stop) as Matrix;
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            int count = resX * resZ;
            if (count == 0) return tile.CreateMatrix();

            // No traced course means no channel to fill, and the geometry alone cannot
            // stand in for one: on any slope the sliding min sits downhill of the
            // sliding max, so a bare hillside would read as filled. Water Level still
            // answers with the ground, so a wired port is never a hole.
            if (inputMask == null)
            {
                Matrix bare = outputIndex == 2 ? new Matrix(src) : dst;
                return margin > 0 ? TileWindows.Crop(bare, tile, margin) : bare;
            }

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            // Local conditioning, not a drainage solve: filling removes the pixel-scale
            // see-saw that would otherwise let the sliding min pick a pit and hold the
            // water above its own reach. It only ever raises ground, and only inside
            // pits, so the window min it feeds is still governed by the true thalweg.
            float[] conditioned = Hydrology.FillDepressions(src.arr, resX, resZ, 1e-6f, cancel);
            if (stop != null && stop.StopRequested) return null;

            int radius = WindowRadius(src.PixelSize.x);

            // MEASURED AGAINST ITS OWN BANKS, NOT AGAINST THE COUNTRYSIDE
            // Both sliding extremes read a copy of the height in which everything
            // outside the traced course is pushed out of range. Without that the crest
            // is the highest ground anywhere within a bank width - the hillside above
            // the river, a ridge, the shoulder of the next valley - and a stage set
            // from it stands metres above the bank it is supposed to be held by. That
            // is the reported fault exactly: a ribbon of water hanging over dry ground,
            // and no depth where the river really is.
            //
            // Restricted to the footprint, the crest is the untouched rim of the
            // channel (River Carve's dig scales with the same mask, so the mask edge is
            // where the ground was left alone) and the bed is the thalweg it cut. The
            // stage is then a fraction of the channel's OWN depth, which is what
            // bankfull means, and it cannot exceed the rim because the rim IS the top.
            //
            // The sentinels are large finite numbers rather than infinities so the
            // window pass stays in ordinary float arithmetic, and neither can leak into
            // a value that is read: a pixel is only read where the mask is positive,
            // and such a pixel lies inside its own window, so every window that matters
            // holds at least one real sample.
            float[] channelBed = new float[count];
            float[] channelTop = new float[count];

            for (int i = 0; i < count; i++)
            {
                if (inputMask.arr[i] > 0f)
                {
                    channelBed[i] = conditioned[i];
                    channelTop[i] = src.arr[i];
                }
                else
                {
                    channelBed[i] = OutsideForMin;
                    channelTop[i] = OutsideForMax;
                }
            }

            float[] bed = MatrixOperations.WindowMin(channelBed, resX, resZ, radius);
            float[] crest = MatrixOperations.WindowMax(channelTop, resX, resZ, radius);
            if (stop != null && stop.StopRequested) return null;

            float fill = Mathf.Clamp01(bankfullFraction);
            float floor = Mathf.Max(0f, minDepth);
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);
            float soft = Mathf.Clamp01(shoreSoftness);
            float sea = Mathf.Clamp01(seaLevel);

            for (int i = 0; i < count; i++)
            {
                float m = Mathf.Clamp01(inputMask.arr[i]);
                float ground = src.arr[i];

                if (m <= 0.001f)
                {
                    if (outputIndex == 2) dst.arr[i] = ground;
                    continue;
                }

                // Both extremes come from the channel's own footprint, so the crest is
                // its rim and the bed is its thalweg. Inside a basin the fill can still
                // raise the thalweg above that rim; taking the higher of the two is what
                // keeps the stage from inverting there.
                float top = crest[i] > bed[i] ? crest[i] : bed[i];
                float level = bed[i] + (top - bed[i]) * fill;

                // At the mouth the river IS the sea. Returning the level the ocean
                // uses is what makes that join seamless instead of a step.
                if (ground <= sea && level < sea) level = sea;

                float d = level - ground;

                if (d < floor)
                {
                    if (outputIndex == 2) dst.arr[i] = ground;
                    continue;
                }

                if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(d * norm); continue; }
                if (outputIndex == 2) { dst.arr[i] = Mathf.Clamp01(level); continue; }

                // Mask: the waterline itself, intersected with the traced course so
                // the water can never be wider than the river it belongs to. Fading
                // over the first slice of depth keeps a shallow bank from ending in a
                // one-pixel cliff.
                float edge = soft > 1e-4f
                    ? Mathf.Clamp01((d - floor) / (soft * Mathf.Max(1e-4f, depthNormalise)))
                    : 1f;

                dst.arr[i] = InterpolationMath.SmoothStep(edge) * m;
            }

            return margin > 0 ? TileWindows.Crop(dst, tile, margin) : dst;
        }

        /// <summary>Metres one heightmap pixel of this chunk covers.</summary>
        private static float MetresPerPixel(TileData tile)
        {
            if (tile == null) return 1f;
            return tile.rect.size.x > 1
                ? tile.worldSize.x / (tile.rect.size.x - 1)
                : tile.worldSize.x;
        }

        /// <summary>
        /// Half-width of the bed and crest windows, in pixels. Capped because the
        /// ghost ring it doubles as makes the whole upstream graph re-evaluate over a
        /// larger rect, and past a point that costs more than the seam is worth.
        /// </summary>
        private int WindowRadius(float metresPerPixel)
        {
            float mpp = Mathf.Max(1e-4f, metresPerPixel);
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1f, bankWidth) / mpp), 1, 64);
        }
    }

    /// <summary>
    /// Reshapes the bank so a water surface actually meets the ground it sits in.
    /// Takes the terrain plus the Water Level and mask of any water body - lake,
    /// river or ocean - and returns the terrain with a shore cut into it.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NOT OPTIONAL DRESSING
    /// The fill nodes decide where the water is and how high it stands. Nothing in
    /// that decides where the ground is, and a water surface at one elevation
    /// crossing a bank of arbitrary steepness does not meet it: the water's boundary
    /// ends at whatever height the mask faded out at, which is not the ground's
    /// height there. That is the floating edge and the rectangular notch. Seating
    /// has to happen on the GROUND side, which is why it is a height node and not
    /// another output port on Lake Fill.
    ///
    /// WHY A SEPARATE NODE RATHER THAN BUILT INTO EACH FILL
    /// The seat needs a ghost ring as wide as its bench plus skirt - wider than
    /// either fill node needs for itself - and the arithmetic is identical for a
    /// lake, a river and a coast. One node reached by all three costs one padded
    /// pull and keeps one implementation; a port on each fill node would mean three
    /// copies and three different margins. Chain one per body, or combine the masks
    /// first and seat them together.
    ///
    /// Feed it the SAME Water Level and mask that drive the water surface. Seating
    /// against a different level is worse than not seating at all: the ground would
    /// be cut to meet water that is not there.
    /// </remarks>
    [System.Serializable]
    public class WaterBankSeatNode : Generator
    {
        public override string NodeName { get { return "Water Bank Seat"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts a shore so the water surface meets the ground exactly."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Water Level", PortType.Matrix, PortDirection.Input),
            new Port("Water Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>
        /// Width of the shelf held level with the water, in metres. The waterline
        /// lands inside this band, which is what makes the meeting exact rather than
        /// approximate. Zero still works - the skirt alone puts ground and water
        /// together at the mask edge - but a few metres absorbs the mask's own fade.
        /// </summary>
        public float benchWidth = 3f;

        /// <summary>
        /// Distance over which the bench eases back to the natural terrain, in
        /// metres. Too small reads as a moat; roughly a bank's own width reads as a
        /// bank.
        /// </summary>
        public float skirtWidth = 30f;

        /// <summary>
        /// Clearance kept between the bed and the surface, 0-1 of world height. Stops
        /// a bump in the bed from poking back through its own water.
        /// </summary>
        public float minDepth = 0.001f;

        /// <summary>0 leaves the terrain untouched, 1 applies the full seat.</summary>
        public float strength = 1f;

        // Past this the padded pull re-evaluates the whole upstream graph over a much
        // larger rect for a blend the eye cannot follow anyway.
        private const int MaxMargin = 96;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            // Water just outside the chunk still owns bank inside it, and the sweep
            // that measures distance to water is a bounded neighbourhood - so a ghost
            // ring the width of the bench plus skirt is the complete fix. See
            // TileWindows for why drainage needs something else entirely.
            float mpp = MetresPerPixel(tile);
            int margin = WaterBankSeat.MarginPixels(benchWidth, skirtWidth, mpp, MaxMargin);

            TileData view = TileWindows.Expand(tile, margin);
            if (view == null) { view = tile; margin = 0; }

            Matrix src = view.PullInput(this, 0, stop) as Matrix;
            if (src == null) return tile.CreateMatrix();

            Matrix level = view.PullInput(this, 1, stop) as Matrix;
            Matrix mask = view.PullInput(this, 2, stop) as Matrix;
            if (stop != null && stop.StopRequested) return null;

            // No water described means no bank to cut. Passing the terrain through is
            // the honest answer - a half-wired seat must not deform anything.
            if (level == null || mask == null)
                return margin > 0 ? TileWindows.Crop(src, tile, margin) : new Matrix(src);

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            float[] seated = WaterBankSeat.Apply(src.arr, level.arr, mask.arr,
                                                 resX, resZ, src.PixelSize.x,
                                                 benchWidth, skirtWidth,
                                                 minDepth, strength, cancel);

            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();
            System.Array.Copy(seated, dst.arr, dst.arr.Length);

            return margin > 0 ? TileWindows.Crop(dst, tile, margin) : dst;
        }

        /// <summary>Metres one heightmap pixel of this chunk covers.</summary>
        private static float MetresPerPixel(TileData tile)
        {
            if (tile == null) return 1f;
            return tile.rect.size.x > 1
                ? tile.worldSize.x / (tile.rect.size.x - 1)
                : tile.worldSize.x;
        }
    }

    /// <summary>
    /// Sea level as a mask, with depth and a shoreline band. Trivially a threshold,
    /// but every downstream node wants the same sea level and the same softened
    /// edge, so it belongs in one node rather than six copies of a Height Select.
    /// </summary>
    [System.Serializable]
    public class OceanLevelNode : Generator
    {
        public override string NodeName { get { return "Ocean Level"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Sea level to ocean mask, depth, and a shoreline band."; } }

        // "Water Mask" rather than "Ocean Mask" so the sea's footprint plugs into Water
        // Bank Seat's Water Mask input by name, exactly as a lake's or a river's does.
        // The Shoreline output is a different signal - the beach band ABOVE the water -
        // so it keeps its own name.
        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Shoreline", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Sea level, 0..1 of world height.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Vertical width of the shoreline band, 0..1.</summary>
        public float shorelineBand = 0.015f;

        /// <summary>Softness of the ocean mask edge, 0..1.</summary>
        public float edgeSoftness = 0.004f;

        /// <summary>Depth at which the depth port saturates.</summary>
        public float depthNormalise = 0.2f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            float sea = Mathf.Clamp01(seaLevel);
            float band = Mathf.Max(1e-5f, shorelineBand);
            float soft = Mathf.Max(1e-5f, edgeSoftness);
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);

            for (int i = 0; i < src.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float h = src.arr[i];

                if (outputIndex == 1)
                {
                    dst.arr[i] = Mathf.Clamp01((sea - h) * norm);
                    continue;
                }

                if (outputIndex == 2)
                {
                    // Peaks at the waterline and falls off both ways: this is the
                    // beach, not "everything below sea level".
                    float dist = Mathf.Abs(h - sea) / band;
                    dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(dist));
                    continue;
                }

                dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01((h - sea) / soft + 0.5f));
            }

            return dst;
        }
    }

    /// <summary>
    /// Segments the world into drainage basins. Port 0 is a basin id for texturing or
    /// biome variation, port 1 marks the divides - the ridge lines that separate one
    /// catchment from the next - and port 2 how large each basin is.
    /// </summary>
    /// <remarks>
    /// A basin id is only meaningful if every chunk agrees on the numbering, and a
    /// basin's extent is decided by where its ridges run, which is not a chunk-local
    /// question. Labelled per chunk, basin 3 in one chunk is a different catchment
    /// from basin 3 next door, so any shading driven from this port changes at every
    /// tile border. So the labelling runs once over the world (see WorldDrainage) and
    /// each chunk reads its own labels back.
    ///
    /// The divides are then found at CHUNK resolution from those labels, which gives a
    /// one-pixel line rather than a band a whole solve cell wide. The line's PATH is
    /// still only as precise as the solve grid; a ridge is a broad feature, so that is
    /// the right trade.
    /// </remarks>
    [System.Serializable]
    public class WatershedBasinSegmentNode : Generator
    {
        public override string NodeName { get { return "Watershed Basin Segment"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Segments the world into drainage basins; also emits the divides."; } }

        private static readonly Port[] Outs =
        {
            new Port("Basin Id", PortType.Matrix, PortDirection.Output, true),
            new Port("Divides", PortType.Matrix, PortDirection.Output, true),
            new Port("Basin Size", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Cells per side of the world solve. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>Basins smaller than this fraction of the WORLD are left unlabelled.</summary>
        public float minBasinFraction = 0.002f;

        /// <summary>Randomises the emitted ids so adjacent basins contrast.</summary>
        public bool scrambleIds = true;

        public int seed = 616;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int outX = dst.rect.size.x;
            int outZ = dst.rect.size.z;
            int outCount = outX * outZ;
            if (outCount == 0) return dst;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      1e-6f, true, stop);
            if (solve == null) return dst;
            if (stop != null && stop.StopRequested) return null;

            int worldCount = solve.resX * solve.resZ;

            int basinCount;
            int[] label = Hydrology.Basins(solve.dir, solve.resX, solve.resZ,
                                           out basinCount, cancel);
            if (stop != null && stop.StopRequested) return null;
            if (basinCount <= 0) return dst;

            int[] sizes = new int[basinCount];
            for (int i = 0; i < worldCount; i++)
                if (label[i] >= 0) sizes[label[i]]++;

            int[] chunkLabel = solve.ReadLabelsAtChunk(label, dst);
            if (stop != null && stop.StopRequested) return null;

            if (outputIndex == 2)
            {
                // Scaled against the world's largest basin, so the port means the same
                // thing in every chunk.
                int largest = 0;
                for (int b = 0; b < basinCount; b++)
                    if (sizes[b] > largest) largest = sizes[b];

                float invLargest = largest > 0 ? 1f / largest : 0f;
                for (int i = 0; i < outCount; i++)
                {
                    int b = chunkLabel[i];
                    dst.arr[i] = b >= 0 ? Mathf.Clamp01(sizes[b] * invLargest) : 0f;
                }

                return dst;
            }

            if (outputIndex == 1)
            {
                // A divide is where two different basins touch.
                for (int z = 0; z < outZ; z++)
                {
                    if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                    int row = z * outX;
                    for (int x = 0; x < outX; x++)
                    {
                        int i = row + x;
                        int mine = chunkLabel[i];
                        bool divide = false;

                        for (int n = 0; n < 4 && !divide; n++)
                        {
                            int nx = x + (n == 0 ? -1 : n == 1 ? 1 : 0);
                            int nz = z + (n == 2 ? -1 : n == 3 ? 1 : 0);
                            if (nx < 0 || nz < 0 || nx >= outX || nz >= outZ) continue;
                            if (chunkLabel[nz * outX + nx] != mine) divide = true;
                        }

                        dst.arr[i] = divide ? 1f : 0f;
                    }
                }

                return dst;
            }

            // Basin id, optionally scrambled so neighbouring ids are not adjacent
            // greys - two basins that share a divide should look different.
            int minSize = Mathf.RoundToInt(Mathf.Clamp01(minBasinFraction) * worldCount);
            Noise noise = new Noise(seed);
            float invBasins = basinCount > 1 ? 1f / (basinCount - 1) : 0f;

            for (int i = 0; i < outCount; i++)
            {
                int b = chunkLabel[i];
                if (b < 0 || sizes[b] < minSize) { dst.arr[i] = 0f; continue; }

                dst.arr[i] = scrambleIds ? noise.Random(b) : b * invBasins;
            }

            return dst;
        }
    }

    /// <summary>
    /// Wetlands: flat ground, high throughflow, close to standing water. All three
    /// have to hold at once - a flat hilltop is not a marsh and a steep stream bank
    /// does not hold water.
    /// </summary>
    /// <remarks>
    /// Throughflow and ponding both come from the world drainage solve rather than a
    /// chunk-local one, because the land draining through a marsh usually starts well
    /// outside the chunk it sits in. Solved per chunk the marshes appear as a stripe
    /// down each tile instead of in the valley floors. Slope and the noise that breaks
    /// up the edges stay at chunk resolution, where the detail is.
    /// </remarks>
    [System.Serializable]
    public class GroundwaterWetlandMaskNode : Generator
    {
        public override string NodeName { get { return "Groundwater Wetland Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Marks marsh ground: low slope, high flow, near standing water."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Water Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        /// <summary>Cells per side of the world drainage solve. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>Slope above which ground drains too fast to stay wet, in degrees.</summary>
        public float maxSlope = 4f;

        /// <summary>
        /// Land that must drain through before ground stays saturated, in square
        /// metres. Five hectares of hillside keeps a valley floor wet in a temperate
        /// climate. An area rather than a cell count, so the answer does not change
        /// when the solve resolution does.
        /// </summary>
        public float saturationArea = 50000f;

        /// <summary>How far the influence of standing water reaches, in metres.</summary>
        public float waterProximity = 90f;

        /// <summary>Elevation above local water that still counts as wet, 0..1.</summary>
        public float tableDepth = 0.006f;

        /// <summary>Breaks the edges up so the marsh is not an obvious contour.</summary>
        public float edgeNoise = 0.3f;

        public int seed = 4242;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix water = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      1e-6f, true, stop);
            if (solve == null) return dst;
            if (stop != null && stop.StopRequested) return null;

            // Throughflow is a smooth quantity, so it blends. The ponded surface is a
            // water level, so it takes the lowest of its neighbours - see WorldDrainage.
            float[] accum = solve.ReadAtChunk(solve.accum, dst, WorldRead.Bilinear);
            float[] filled = solve.ReadAtChunk(solve.filled, dst, WorldRead.Minimum);
            if (stop != null && stop.StopRequested) return null;

            Matrix slopes = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return null;

            // Proximity to standing water: blurring the water mask is a cheap and
            // stable distance proxy, and it stays smooth across tile edges.
            Matrix nearWater = null;
            if (water != null)
            {
                Vector2 wps = src.PixelSize;
                float wmpp = Mathf.Max(1e-4f, (wps.x + wps.y) * 0.5f);
                float sigma = Mathf.Clamp(waterProximity / wmpp, 0.5f, 48f);
                nearWater = MatrixOperations.GaussianBlur(water, sigma);
            }

            Noise noise = new Noise(seed);
            float slopeLimit = Mathf.Max(0.5f, maxSlope);
            float flowLimit = solve.CellsForArea(saturationArea);
            float table = Mathf.Max(1e-5f, tableDepth);
            float noiseAmt = Mathf.Clamp01(edgeNoise);

            float mppX = src.PixelSize.x;
            float mppZ = src.PixelSize.y;
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    // SlopeAngles already reports degrees, 0 flat to 90 vertical.
                    float angle = slopes.arr[i];
                    float flat = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(angle / slopeLimit));
                    if (flat <= 0.001f) continue;

                    float wet = Mathf.Clamp01(accum[i] / flowLimit);

                    // Ponded depth from the fill: ground the water table is at or
                    // just below is saturated even without visible surface water.
                    float ponded = Mathf.Clamp01((filled[i] - src.arr[i]) / table);

                    float near = nearWater != null ? Mathf.Clamp01(nearWater.arr[i] * 2f) : 0f;

                    float v = flat * Mathf.Clamp01(Mathf.Max(Mathf.Max(wet, ponded), near));

                    if (noiseAmt > 0.001f)
                    {
                        float wx = originX + x * mppX;
                        float n = noise.Fractal(wx, wz, 140f, 3);
                        v *= 1f - noiseAmt + n * noiseAmt * 2f;
                    }

                    dst.arr[i] = Mathf.Clamp01(v);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Shapes the sea bed below sea level: submarine canyons cut along the drainage
    /// the land already established, plus seamounts. Coastal Shelf Shaper sets the
    /// broad gradient; this puts detail on it.
    /// </summary>
    [System.Serializable]
    public class UnderwaterTerrainExtendNode : ModifierNode
    {
        public override string NodeName { get { return "Underwater Terrain Extend"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Adds submarine canyons and seamounts below sea level."; } }

        public int seed = 1919;

        /// <summary>Cells per side of the world drainage solve. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>Sea level, 0..1. Nothing above it is touched.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Depth of submarine canyons, 0..1 of world height.</summary>
        public float canyonDepth = 0.05f;

        /// <summary>
        /// Land a canyon needs draining into it before it cuts at full depth, in
        /// square metres. Matching River Network Trace's channel head area is what
        /// lines a submarine canyon up with the river mouth that fed it.
        /// </summary>
        public float canyonCatchmentArea = 250000f;

        /// <summary>How far offshore canyons persist, 0..1 of the submarine range.</summary>
        public float canyonReach = 0.7f;

        /// <summary>Height of seamounts, 0..1.</summary>
        public float seamountHeight = 0.06f;

        /// <summary>Seamount spacing in world metres.</summary>
        public float seamountSpacing = 2200f;

        /// <summary>Fine sediment ripple amplitude, 0..1.</summary>
        public float sedimentDetail = 0.004f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float sea = Mathf.Clamp01(seaLevel);
            if (sea <= 1e-5f) return;

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;

            // Canyons follow the drainage the LAND already carved, which is why a
            // submarine canyon lines up with the river mouth that fed it - and why the
            // drainage has to be the world's, not this chunk's. Solved only when
            // canyons are actually wanted; it is the expensive part of this node.
            float[] accum = null;
            float canyonCells = 1f;

            if (canyonDepth > 1e-5f)
            {
                WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                         1e-6f, true, stop);
                if (stop != null && stop.StopRequested) return;

                if (solve != null)
                {
                    accum = solve.ReadAtChunk(solve.accum, m, WorldRead.Bilinear);
                    canyonCells = solve.CellsForArea(canyonCatchmentArea);
                    if (stop != null && stop.StopRequested) return;
                }
            }

            Noise noise = new Noise(seed);
            Noise mountNoise = noise.WithSubSeed(77);

            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSpacing = Mathf.Max(1f, seamountSpacing);
            float reach = Mathf.Clamp01(canyonReach);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float h = m.arr[i];
                    if (h >= sea) continue;

                    float wx = originX + x * mppX;

                    // 0 at the shoreline, 1 at the deepest point.
                    float submarine = 1f - Mathf.Clamp01(h / sea);
                    float value = h;

                    if (accum != null)
                    {
                        float channel = Mathf.Clamp01(accum[i] / canyonCells);
                        float fade = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(submarine / Mathf.Max(1e-4f, reach)));
                        value -= canyonDepth * channel * fade;
                    }

                    if (seamountHeight > 1e-5f)
                    {
                        // Cellular distance inverted: a cone rising from each cell
                        // centre, only offshore where the abyssal plain is.
                        float d = mountNoise.Voronoi(wx / safeSpacing, wz / safeSpacing, 0f, VoronoiBlend.Cellular);
                        float cone = Mathf.Clamp01(1f - d * 3.5f);
                        value += seamountHeight * cone * cone * submarine;
                    }

                    if (sedimentDetail > 1e-6f)
                    {
                        float ripple = noise.Fractal(wx, wz, 60f, 3) - 0.5f;
                        value += ripple * 2f * sedimentDetail * submarine;
                    }

                    // Never push the sea bed above the waterline.
                    m.arr[i] = Mathf.Clamp(value, 0f, sea - 1e-5f);
                }
            }
        }
    }
}
