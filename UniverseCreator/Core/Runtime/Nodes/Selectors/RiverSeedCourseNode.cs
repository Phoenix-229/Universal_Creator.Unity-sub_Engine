// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverSeedCourseNode.cs   (RIV-2, the node half)
//  Purpose: The CONSUMER that was missing. `RiverSourceSeedNode` has always emitted
//           a sparse mask of headwater points and NOTHING read it - it was an orphan
//           producer, and `RiverCourseTracer.TraceDownhill` (RIV-2's route search) was
//           an orphan algorithm with no node able to reach it. This node is the seam
//           between them: it takes the seeded points, walks each one downhill to the
//           sea, and carves the resulting course.
//
//  WHY A THIRD NODE AND NOT A NEW INPUT ON River Network
//  -----------------------------------------------------
//  River Network finds its own channel heads from the slope-area law (Montgomery and
//  Dietrich) and sizes them from catchment discharge. That is a complete, shipped,
//  well-tested answer to "where do rivers begin" and adding a competing seed input to
//  it would put two authorities inside one node. Instead this node is a SIBLING: it
//  emits exactly the same two outputs in the same order (Height, River Mask) as both
//  River Network and River Course Input, so the whole existing tail - River Fill,
//  River Banks, Water Mask, scatter exclusion - consumes it with no change whatsoever.
//  Three ways to get a river, one downstream contract.
//
//  THE DIVISION OF AUTHORITY (so this is not read as a half-built River Network)
//  ----------------------------------------------------------------------------
//    River Network      - the world decides where rivers are (slope-area law) and how
//                         big (Leopold hydraulic geometry from discharge).
//    This node          - the AUTHOR decides where rivers start (the seed mask) and
//                         how big (width/depth fields below). No discharge model, on
//                         purpose: catchment-sized geometry is River Network's job and
//                         is not re-derived here.
//    River Course Input - a hand-drawn spline, carved scene-side.
//
//  WHY IT IS SEAM-FREE (the mistake this pattern exists to prevent)
//  ---------------------------------------------------------------
//  A river's catchment is not inside one chunk, so a course solved per chunk would
//  restart at every border and step at the seams. Everything here is solved ONCE at
//  world scale through `WorldDrainage.Solve` and read onto each chunk with
//  `ReadAtChunk` - the established pattern River Network and Lake Fill both use.
//  `Solve`'s per-bake cache is keyed on the input index, so pulling the seed mask at
//  world resolution costs one evaluation for the whole bake, not one per chunk.
//
//  WHY THE BED CANNOT CLIMB
//  ------------------------
//  The walk runs on `filled`, the depression-filled surface, which priority-flood
//  guarantees never decreases against the flow direction. So a course traced on it
//  descends BY CONSTRUCTION and needs no "enforce downhill" correction pass - the
//  same guarantee River Network and River Fill rely on. (Tracing on the RAW height
//  would stall in every noise pit, which is why the tracer is a cost search and not a
//  steepest-descent walk.)
//
//  DEEPENING ONLY. Like Lake Basin and River Network, the cut is subtractive: a
//  channel lowers ground and never raises it, because raising ground to meet a
//  shallow bed would fill the valley the river runs through.
//
//  Inert, never wrong: with the Sources port unwired - or a seed mask that is empty,
//  or all below sea level - Height passes through untouched and the River Mask is
//  empty, so adding this node to a graph without seeds changes nothing.
//
//  Deterministic: a pure function of the two input fields. Seed selection is sorted
//  by source strength with the cell index as tiebreak, so the chosen rivers do not
//  depend on iteration order. No RNG, no clock.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Traces a river course downhill from each seeded headwater to the sea and carves it,
    /// emitting the same (Height, River Mask) pair as River Network so the identical
    /// Fill / Banks / scatter tail consumes it. Feed it River Source Seed's mask.
    /// </summary>
    [System.Serializable]
    public class RiverSeedCourseNode : Generator
    {
        public override string NodeName { get { return "River Seed Course"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }

        public override string Tooltip
        {
            get
            {
                return "Walks each seeded headwater downhill to the sea and carves the course. " +
                       "Feed it River Source Seed's mask; its outputs match River Network, so " +
                       "River Fill, Banks and scatter exclusion all read it unchanged.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Sources", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        // -- Where the courses are solved -----------------------------------------
        /// <summary>
        /// Whole-world solve cells per side. A course is a world-scale question, so it is
        /// solved once at this resolution and read onto each chunk - which is also what
        /// makes it seam-free. Matches River Network's default. Minimum 16, maximum 2048;
        /// larger values are rejected during generation and storage requires budget admission.
        /// </summary>
        public int solveResolution = 256;

        /// <summary>Normalised sea height. A course ends at the first cell below it.</summary>
        public float seaLevel = 0.35f;

        // -- Which seeds become rivers --------------------------------------------
        /// <summary>Seed-mask value at or above which a cell counts as a headwater, 0..1.</summary>
        public float sourceThreshold = 0.5f;

        /// <summary>
        /// Most courses traced. The strongest seeds win (ties broken by cell index, so the
        /// choice is deterministic). A cap matters because each course is its own search.
        /// </summary>
        public int maxRivers = 12;

        /// <summary>
        /// Closest two headwaters may be, in world metres. Without a spacing rule a blob of
        /// adjacent high-mask cells would all seed, tracing many near-identical courses.
        /// </summary>
        public float minSourceSpacingMetres = 1500f;

        // -- The channel it cuts ---------------------------------------------------
        /// <summary>Half-width of the full-depth channel, in world metres.</summary>
        public float channelHalfWidthMetres = 6f;

        /// <summary>
        /// How far the cut feathers out past the channel, in world metres - the floodplain
        /// shoulder. Also the extent of the River Mask, so banks and scatter read a verge
        /// rather than a cliff edge.
        /// </summary>
        public float floodplainMetres = 40f;

        /// <summary>
        /// Channel depth as a fraction of world height (normalised, like Lake Basin's
        /// basinDepth). Full depth in the channel, tapering to nothing across the floodplain.
        /// </summary>
        public float channelDepth = 0.01f;

        /// <summary>
        /// UC-T03-04: extra metres of search cost per metre of climb, clamped to at least one.
        /// Finite values let a route cross a pit lip; nonfinite values reject the build.
        /// </summary>
        public float uphillPenalty = 1000f;

        /// <summary>UC-T03-04: maximum settled cells per source; zero uses the whole solve lattice.
        /// A positive exhausted budget rejects the build instead of publishing incomplete channels.</summary>
        [Min(0), Tooltip("Settled cells per river search. 0 allows the full solve grid; an exhausted positive limit stops this build.")]
        public int maxSearchVisits = 0;

        // -- RIV-3: along-course variation ----------------------------------------
        /// <summary>
        /// Opt-in. When false every station is cut at the same width and depth - today's
        /// behaviour exactly, so the default is the off position. When true the channel
        /// grows from headwater to mouth, which is what a real river does: a trunk is wider
        /// and deeper than its own headwaters.
        ///
        /// Deliberately scalar multipliers rather than an `AnimationCurve`: Generate runs on
        /// a worker Task and `AnimationCurve.Evaluate` is not thread-safe. The authored-curve
        /// form lives in `RiverProfileCurves`, which bakes to a lookup table on the main
        /// thread for exactly this reason.
        /// </summary>
        public bool taperAlongCourse = false;

        /// <summary>Channel width at the mouth as a multiple of the headwater width.</summary>
        public float mouthWidthMultiplier = 2.5f;

        /// <summary>Channel depth at the mouth as a multiple of the headwater depth.</summary>
        public float mouthDepthMultiplier = 1.6f;

        /// <summary>
        /// RIV-3: the AUTHORED along-course profile (width/depth curves). Takes precedence over the
        /// scalar taper above when it is both enabled and baked; otherwise the taper applies, and
        /// with both off every station is identical - today's flat channel. Disabled by default, so
        /// this field changes nothing until an author turns it on.
        ///
        /// Its curves must be baked to a lookup table on the MAIN thread before the graph
        /// evaluates (`AnimationCurve.Evaluate` is not thread-safe and Generate runs on a worker).
        /// `UCWaterSystemManager.BakeRiverProfilesInGraph` does that from `ApplyToGraph`, the same
        /// sync-to-graph moment that pushes sea level and manual courses in. Until it is baked
        /// `TryEvaluate` refuses and the node falls back to the taper, so an unbaked profile is
        /// inert rather than wrong.
        /// </summary>
        public UniverseCreator.TerrainSystem.RiverProfileCurves profile =
            new UniverseCreator.TerrainSystem.RiverProfileCurves();

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            // Sources unwired: the node is inert. Height through, mask empty - checked
            // before any world solve so an unwired node costs nothing at all.
            Matrix localSources = GetInputMatrix(tile, 1, stop);
            if (localSources == null) return Passthrough(outputIndex, src);
            if (stop != null && stop.StopRequested) return null;

            int res = TileWindows.DrainageResolution(solveResolution); // UC repair 30: seeded and automatic courses share the same supported grid.

            // needFlow: false - this node routes with the cost search, not with flow
            // directions, and `filled` (which is what it traces on) is solved either way.
            // Skipping the flow pass skips most of the solve's cost.
            WorldDrainage wd = WorldDrainage.Solve(this, 0, tile, res, 0f, false, stop);
            if (wd == null || wd.filled == null) return Passthrough(outputIndex, src);
            if (stop != null && stop.StopRequested) return null;

            // The seed mask at world scale. Same resolution and world rect as the height
            // solve, so the two arrays are index-comparable.
            WorldDrainage ws = WorldDrainage.Solve(this, 1, tile, res, 0f, false, stop);
            if (ws == null || ws.height == null || ws.resX != wd.resX || ws.resZ != wd.resZ)
                return Passthrough(outputIndex, src);
            if (stop != null && stop.StopRequested) return null;

            // UC-T03-04: retain scratch admission through the last read of both world fields.
            // This includes the fixed workspace, candidates and returned path while courses build.
            using (tile.ReserveProductMemory(2048 + (long)wd.resX * wd.resZ * 80)) // UC-T03-09: station metadata shares the admitted search lifetime.
            {
                float[] cutNorm, maskNorm;
                BuildCourses(wd, ws.height.arr, Mathf.Max(1f, tile.worldHeight), stop,
                             out cutNorm, out maskNorm, out var widths, out var depths, out var downstream, out var reachIds);
                if (stop != null && stop.StopRequested) return null;
                Vector2 stationCell = wd.height.PixelSize; // UC-T03-09: candidate profiles publish with the resulting field, never directly to the scene.
                tile.AddPublication(new ChannelGraphPublication(id + ":channels", widths, depths, wd.resX, wd.resZ,
                    wd.height.worldPos.x, wd.height.worldPos.z, stationCell.x, stationCell.y, null, null,
                    downstream, reachIds, wd.height.arr, wd.height.worldPos.y, wd.height.worldHeight));

                if (outputIndex == 1)
                {
                    float[] chunkMask = wd.ReadAtChunk(maskNorm, src, WorldRead.Bilinear, stop); // UC-T03-04: cancel while resampling.
                    if (stop != null && stop.StopRequested) return null;

                    Matrix mask = src.Blank();
                    int mn = mask.arr.Length;
                    for (int i = 0; i < mn; i++) mask.arr[i] = Mathf.Clamp01(chunkMask[i]);
                    return mask;
                }

                float[] chunkCut = wd.ReadAtChunk(cutNorm, src, WorldRead.Bilinear, stop);
                if (stop != null && stop.StopRequested) return null;

                Matrix dst = new Matrix(src);
                // UC-T03-02: preserve the source ground only on the actual carve path, never for an unwired Sources inlet.
                dst.trenchReference = src.trenchReference ?? src;
                int n = dst.arr.Length;
                for (int i = 0; i < n; i++)
                {
                    float cut = chunkCut[i];
                    if (cut > 0f) dst.arr[i] = Mathf.Max(0f, dst.arr[i] - cut);   // deepening only
                }
                return dst;
            }
        }

        /// <summary>Height passes through, mask is empty - the "no seeds" answer.</summary>
        private static object Passthrough(int outputIndex, Matrix src)
        {
            if (outputIndex == 0) return new Matrix(src);
            return src.Blank();
        }

        /// <summary>
        /// Picks the headwaters, traces each one to the sea, and stamps the courses into a
        /// world-scale cut field and mask. Exposed-by-structure rather than inlined so the
        /// gate can reason about it; both outputs are built together because one trace
        /// serves them both.
        /// </summary>
        private void BuildCourses(WorldDrainage wd, float[] sources, float worldHeightM,
                                  StopToken stop, out float[] cutNorm, out float[] maskNorm, out float[] widths,
                                  out float[] depths, out int[] downstream, out string[] reachIds)
        {
            int resX = wd.resX, resZ = wd.resZ;
            int n = resX * resZ;
            cutNorm = new float[n];
            maskNorm = new float[n];
            widths = new float[n]; depths = new float[n]; downstream = new int[n]; reachIds = new string[n]; // UC-T03-09.
            for (int c = 0; c < n; c++) downstream[c] = -1;

            float[] filled = wd.filled;
            if (filled == null || filled.Length < n || sources == null || sources.Length < n) return;

            float sea = Mathf.Clamp01(seaLevel);
            // UC-T03-04: use both physical axes; an area-equivalent square distorts rectangular worlds.
            Vector2 cellSize = wd.height.PixelSize;
            float cellM = Mathf.Min(cellSize.x, cellSize.y);
            float spacingMetres = Mathf.Max(cellM, minSourceSpacingMetres);
            float halfMetres = Mathf.Max(0.5f * cellM, channelHalfWidthMetres);
            float plainMetres = Mathf.Max(halfMetres + 0.5f * cellM, floodplainMetres);
            float depth = Mathf.Max(0f, channelDepth);
            int cap = Mathf.Clamp(maxRivers, 0, 256);
            if (cap == 0) return;

            // 1. Candidates: strong enough to be a headwater, and above the sea (a sub-sea
            //    "source" has nowhere to flow and the tracer would refuse it anyway).
            float thr = Mathf.Clamp01(sourceThreshold);
            List<int> cand = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0 && stop != null && stop.StopRequested) return;
                if (sources[i] >= thr && filled[i] >= sea) cand.Add(i);
            }
            if (cand.Count == 0) return;

            // Strongest first; cell index breaks ties so the selection cannot depend on
            // iteration order. This is what keeps the node deterministic.
            float[] srcRef = sources;
            cand.Sort(delegate (int a, int b)
            {
                int c = srcRef[b].CompareTo(srcRef[a]);
                return c != 0 ? c : a.CompareTo(b);
            });

            // 2. Thin them by spacing, greedily in that order.
            List<int> seeds = new List<int>();
            double sp2 = (double)spacingMetres * spacingMetres;
            for (int k = 0; k < cand.Count && seeds.Count < cap; k++)
            {
                if ((k & 255) == 0 && stop != null && stop.StopRequested) return;
                int i = cand[k];
                int ix = i % resX, iz = i / resX;
                bool ok = true;
                for (int s = 0; s < seeds.Count; s++)
                {
                    int j = seeds[s];
                    double dx = ((j % resX) - ix) * (double)cellSize.x;
                    double dz = ((j / resX) - iz) * (double)cellSize.y;
                    if (dx * dx + dz * dz < sp2) { ok = false; break; }
                }
                if (ok) seeds.Add(i);
            }

            // 3. Trace each, stamp the channel. A seed with no reachable outlet is skipped,
            //    not an error: an endorheic basin genuinely has no river to the sea.
            float wMulMouth = taperAlongCourse ? Mathf.Max(0.01f, mouthWidthMultiplier) : 1f;
            float dMulMouth = taperAlongCourse ? Mathf.Max(0.01f, mouthDepthMultiplier) : 1f;
            var workspace = new RiverSearchWorkspace(n); // UC-T03-04: one indexed heap for every source in this pass.
            int visits = maxSearchVisits <= 0 ? n : Mathf.Min(n, maxSearchVisits);
            System.Func<bool> cancelled = stop == null ? (System.Func<bool>)null : () => stop.StopRequested;
            float penalty = RiverSearchWorkspace.Finite(uphillPenalty) ? Mathf.Max(1f, uphillPenalty) : uphillPenalty;

            for (int s = 0; s < seeds.Count; s++)
            {
                if (stop != null && stop.StopRequested) return;

                RiverSearchResult search = RiverCourseTracer.TraceDownhill(filled, resX, resZ, seeds[s], sea,
                    penalty, cellSize, worldHeightM, visits, workspace, cancelled);
                if (search.Status == RiverSearchStatus.Cancelled) return;
                if (search.Status == RiverSearchStatus.VisitLimitExceeded || search.Status == RiverSearchStatus.InvalidInput)
                    throw new System.InvalidOperationException("River Seed Course search failed: " + search.Status +
                        "; settled " + search.VisitedCount + " of " + visits + " allowed cells.");
                List<int> course = search.Course;
                if (course == null || course.Count < 2) continue;

                float lastStation = course.Count - 1;
                float sourceArcM = 0;
                for (int p = 0; p < course.Count; p++)
                {
                    if (stop != null && stop.StopRequested) return;
                    // t runs 0 at the headwater to 1 at the mouth (RIV-3).
                    float t = p / lastStation;

                    float stationHalf, stationDepth;

                    // PRECEDENCE: an authored, baked profile wins; else the scalar taper; else a
                    // flat channel. With both dials off this is bit-identical to a flat carve.
                    UniverseCreator.TerrainSystem.RiverProfileSample ps;
                    if (profile != null && profile.TryEvaluate(t, out ps))
                    {
                        // Authored width is a full width in METRES; the stamp wants a half-width in
                        // metres. Authored depth is metres too, so convert to the normalised height
                        // the cut field carries.
                        stationHalf = Mathf.Max(0.5f * cellM, ps.widthM * 0.5f);
                        stationDepth = Mathf.Max(0f, ps.depthM / worldHeightM);
                    }
                    else if (taperAlongCourse)
                    {
                        stationHalf = halfMetres * Mathf.Lerp(1f, wMulMouth, t);
                        stationDepth = depth * Mathf.Lerp(1f, dMulMouth, t);
                    }
                    else
                    {
                        stationHalf = halfMetres;
                        stationDepth = depth;
                    }

                    // UC-T04-06: a spring begins with a shallow rounded stamp and
                    // grows over physical metres; authored profiles still govern the later reach.
                    if (p > 0)
                    {
                        float dx = (course[p] % resX - course[p - 1] % resX) * cellSize.x;
                        float dz = (course[p] / resX - course[p - 1] / resX) * cellSize.y;
                        sourceArcM += Mathf.Sqrt(dx * dx + dz * dz);
                    }
                    float onset = Mathf.SmoothStep(0, 1, sourceArcM / Mathf.Max(2 * cellM, halfMetres * 4));
                    stationHalf = Mathf.Lerp(Mathf.Min(stationHalf, cellM * 0.5f), stationHalf, onset);
                    stationDepth = Mathf.Lerp(Mathf.Min(stationDepth, 0.02f / worldHeightM), stationDepth, onset);

                    // The floodplain shoulder scales with the channel so a wider reach keeps a
                    // proportionate verge rather than a sudden cliff edge.
                    float widen = stationHalf / Mathf.Max(1e-4f, halfMetres);
                    float stationPlain = Mathf.Max(stationHalf + 0.5f * cellM, plainMetres * widen);

                    Stamp(course[p], resX, resZ, cellSize, stationHalf, stationPlain,
                          stationDepth, cutNorm, maskNorm, stop);
                    int cell = course[p]; // UC-T03-09: wider shared stations keep their own complete profile.
                    if (stationHalf >= widths[cell])
                    {
                        widths[cell] = stationHalf; depths[cell] = stationDepth * worldHeightM;
                        downstream[cell] = p + 1 < course.Count ? course[p + 1] : -1;
                        reachIds[cell] = id + ":reach:" + seeds[s];
                    }
                }
            }
        }

        /// <summary>
        /// Paints one course station: full depth and mask inside the channel half-width,
        /// smoothly feathering to nothing at the floodplain edge. Max-combined so
        /// overlapping stations and confluences do not double-cut.
        /// </summary>
        internal static void Stamp(int cell, int resX, int resZ, Vector2 cellSize,
                                  float halfMetres, float plainMetres, float depth,
                                  float[] cutNorm, float[] maskNorm, StopToken stop)
        {
            int cx = cell % resX, cz = cell / resX;
            float band = Mathf.Max(1e-4f, plainMetres - halfMetres);
            // UC-T03-04: finite physical radii and grid-clamped bounds keep rectangular stamps bounded.
            if (!RiverSearchWorkspace.Finite(halfMetres) || !RiverSearchWorkspace.Finite(plainMetres) ||
                !RiverSearchWorkspace.Finite(depth)) throw new System.InvalidOperationException("River profile widths/depth must be finite.");
            int rx = (int)System.Math.Min(resX, System.Math.Ceiling((double)plainMetres / cellSize.x));
            int rz = (int)System.Math.Min(resZ, System.Math.Ceiling((double)plainMetres / cellSize.y));

            for (int z = Mathf.Max(0, cz - rz); z <= Mathf.Min(resZ - 1, cz + rz); z++)
            {
                if (stop != null && stop.StopRequested) return;
                int row = z * resX;

                for (int x = Mathf.Max(0, cx - rx); x <= Mathf.Min(resX - 1, cx + rx); x++)
                {
                    double dx = (x - cx) * (double)cellSize.x, dz = (z - cz) * (double)cellSize.y;
                    double d = System.Math.Sqrt(dx * dx + dz * dz);
                    if (d > plainMetres) continue;

                    float w;
                    if (d <= halfMetres) w = 1f;
                    else
                    {
                        float t = (float)((d - halfMetres) / band);
                        w = 1f - (t * t * (3f - 2f * t));   // smooth shoulder, flat at both ends
                    }

                    int j = row + x;
                    if (w > maskNorm[j]) maskNorm[j] = w;
                    float c = depth * w;
                    if (c > cutNorm[j]) cutNorm[j] = c;
                }
            }
        }
    }
}
