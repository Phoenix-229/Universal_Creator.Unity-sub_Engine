// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    WaterCarveLayer.cs
//  Purpose: The dig. A water body's description of the hole it needs in the ground,
//           in absolute world coordinates, applied as each chunk's heights are
//           written.
//
//  WHY A LAYER AND NOT A ONE-OFF EDIT
//  ----------------------------------
//  A river that stamped the terrain once would be erased the next time the graph
//  ran, because the graph owns the heightfield and rewrites it wholesale. So the
//  dig is not an edit - it is a rule, re-derived from these fields every time a
//  chunk is applied, at the same point where manual sculpting folds in. Press
//  Regenerate and the channel is cut again from scratch; sculpt the bank by hand
//  and the sculpt rides on top. Neither can lose to the other.
//
//  Everything here is a pure function of absolute world position, so two chunks
//  that share a seam compute the same bed on both sides and there is no step in
//  the middle of the river.
//
//  THE SHORELINE IS NOT A CUT EDGE
//  -------------------------------
//  A polygon dropped into terrain leaves a sharp rim: the ground stops at one
//  height and the water plane starts at another, so you get a visible lip and a
//  gap you can see under. Two rules fix that, and they are the reason this class
//  exists rather than a simple depth stamp:
//
//    1. AT the waterline the ground is pulled to exactly the water surface
//       elevation. Not near it - equal to it. That is what closes the gap: the
//       mesh edge and the terrain meet at the same Y, so there is nothing to see
//       through and nothing to fall between.
//
//    2. OUTSIDE the waterline the ground rolls from that elevation back up to
//       whatever the terrain was doing, over the bevel distance, on a smoothstep.
//       That is the rounded bank - a fillet, not a chamfer, because the blend is
//       C1 continuous at both ends and so has no crease at either.
//
//  Past the bevel, nothing is touched. That is the whole promise of the marked
//  area: a lake alters the ground inside its shoreline and inside its bank, and
//  nowhere else in the world.
//
//  NO DOTS, NO BURST, NO NATIVEARRAY. Flat float[,] and managed lists.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// A world-space description of the bed one water body needs. Serialized on the
    /// body, rebuilt by it, and read by TerrainBuilder as chunks are applied.
    /// </summary>
    [System.Serializable]
    public class WaterCarveLayer
    {
        // =====================================================================
        //  WHAT IS BEING DUG
        // =====================================================================

        public bool enabled = true;

        /// <summary>Set by the body. Only used to pick sensible fallbacks.</summary>
        public Nodes.WaterBodyKind kind = Nodes.WaterBodyKind.Lake;

        public BedProfile profile = BedProfile.LakeBasin;

        /// <summary>True when the shape is a course with a width, false when it is a
        /// closed ring. The two need different distance math, and nothing else.</summary>
        public bool isCourse;

        // ---- Course bodies (rivers) -----------------------------------------
        // Parallel lists rather than a struct list: Unity serializes them without a
        // wrapper type, and the carve reads them by index in a tight loop.

        public List<Vector3> coursePoints = new List<Vector3>();
        public List<float> courseWidths = new List<float>();
        public List<float> courseDepths = new List<float>();
        public List<float> courseSurfaceY = new List<float>();

        // ---- Ring bodies (lakes, oceans, pools) -----------------------------

        public List<Vector3> ringPoints = new List<Vector3>();

        /// <summary>
        /// Round the shoreline but dig nothing.
        ///
        /// WHY THE OCEAN USES THIS
        /// -----------------------
        /// An ocean's depth already comes from the terrain: the seabed is wherever
        /// the ground happens to fall below sea level, across the whole world, and
        /// that is both correct and what the graph was designed around. Carving a
        /// shelf into it would replace a working world-scale seabed with a shelf
        /// measured from a shoreline the ocean does not really have - the sea's edge
        /// is a contour line, not a drawn ring.
        ///
        /// The one thing the sea did need was the rounded edge, because a beach that
        /// meets the water at a hard rim looks wrong at any scale. So an ocean gets
        /// the bevel and nothing else: its depth, its bed and its shoreline behave
        /// exactly as they did before any of this existed.
        /// </summary>
        public bool bevelOnly;

        /// <summary>Water surface elevation in metres. The bed is measured down from
        /// this, and the shoreline is pulled up or down to meet it.</summary>
        public float surfaceY;

        public float maxDepthMetres = 5f;

        /// <summary>How far in from the shore the bed reaches full depth. Beyond it
        /// the floor is flat at maxDepth. This is what makes a lake read as a lake
        /// rather than a bathtub - the deep part is a floor, not a point.</summary>
        public float shoreRunMetres = 40f;

        public float shelfFraction = 0.25f;
        public float bankExponent = 2f;

        // =====================================================================
        //  BASIN IRREGULARITY
        //
        //  WHY A LAKE IS NOT A FUNNEL
        //  --------------------------
        //  Depth measured purely as distance from the shore puts the deepest point
        //  at the middle of the body and makes the bed a smooth cone. No real lake
        //  is shaped like that. A real basin has its deep water off to one side, a
        //  shallow bay at one end, a bar running out from a headland - some parts
        //  drop away almost immediately and others stay wadeable a long way out.
        //
        //  So the distance from shore is warped by noise before it is turned into a
        //  depth. Where the warp pushes the distance up, the bed dives early; where
        //  it pulls it down, the shallows run far out. The shore itself is untouched
        //  because the warp is faded to nothing there - the waterline stays exactly
        //  where the outline says it is, and only what happens underneath changes.
        // =====================================================================

        /// <summary>How much the deep zone wanders, 0..1. 0 is the plain cone with
        /// its single deepest point in the middle; 0.5 is a natural basin.</summary>
        public float basinIrregularity = 0.45f;

        /// <summary>Size of the deep and shallow zones, in metres. Roughly the width
        /// of one bay or one bar.</summary>
        public float basinFeatureMetres = 60f;

        // =====================================================================
        //  EDGE QUALITY
        // =====================================================================

        /// <summary>Width of the rounded bank outside the waterline, in metres. 0 is
        /// a sharp cut, which is almost never what is wanted.</summary>
        public float bevelMetres = 6f;

        /// <summary>Shapes the fillet. 1 is a plain smoothstep, higher hugs the
        /// waterline and then climbs, which reads as a more scooped bank.</summary>
        public float bevelRoundness = 1f;

        /// <summary>The bed is never allowed closer than this to the surface inside
        /// the body, so a body is never zero-depth in its own middle.</summary>
        public float minDepthMetres = 0.05f;

        /// <summary>Band outside the bevel where generated ground is capped to a valley
        /// gradient instead of being handed back raw, in metres. 0 is off, and off is
        /// the old behaviour exactly.</summary>
        public float corridorMetres;

        /// <summary>How much of the way to the cap the ground is pulled, 0..1.</summary>
        public float corridorStrength = 0.6f;

        // =====================================================================
        //  THE RIVER VALLEY  (the floodplain sag)
        //
        //  corridorMetres above is a CEILING: it shaves ground that stands too
        //  high too close to the water, which stops a channel crossing a hillside
        //  reading as a slot sawn through a cliff. On ground that is already
        //  gentle it does nothing at all, by design - and that is why a river laid
        //  across a plain still had no valley. It sat in a channel cut straight
        //  into flat ground, banks and all, which is the one place a real river
        //  never is.
        //
        //  A real river of any size occupies a valley it cut itself: a floodplain
        //  a little above the water, wider than the channel, with the untouched
        //  land beyond it. It is visible in the dry season as a shallow trough and
        //  it is what the water spreads into when the river is high. So the dig
        //  lowers a band either side of the bank toward that floodplain level -
        //  the SAG - and the ceiling above keeps doing its separate job.
        //
        //  Only big rivers get one, because only big rivers make one. A headwater
        //  stream runs in a V-notch it has not had the discharge to widen; the
        //  floodplain appears downstream as the channel grows. The gate is the
        //  LOCAL channel width, so one river develops its valley along its own
        //  length exactly the way a real one does, and no per-body flag is needed
        //  to say which rivers are big.
        //
        //  Bounds, in channel widths, so the numbers hold at every scale:
        //    sag band   1 x local width beyond the bank, capped by the corridor.
        //               With the channel itself that is a valley floor about
        //               three widths across, which is the low end of the
        //               measured range for a meandering river and the right end
        //               to be conservative at.
        //    sag depth  1 x local channel depth below the surrounding ground,
        //               and never below the water: the trough is of the river's
        //               own scale, not a second excavation.
        //    freeboard  half the channel depth held between the floodplain and
        //               the water, so the valley floor is dry land at normal
        //               flow rather than a sheet of marsh at waterline.
        // =====================================================================

        /// <summary>Local channel width, in metres, at which a river starts to cut a
        /// floodplain. Below it the reach keeps the plain bank it has - a stream in a
        /// notch - and the sag is off entirely. Lowered 2026-09-19 from 8 m: generated
        /// rivers floor at 4 m wide, and at the old onset every one of them read as a slot
        /// with no floodplain. From 3 m up a river now eases in a gentle plain.</summary>
        public const float ValleyOnsetWidthMetres = 3f;

        /// <summary>Local width at which the floodplain is fully developed. Between the
        /// onset and here it eases in, so a river that widens downstream grows its
        /// valley along its length instead of gaining one at a step.</summary>
        public const float ValleyFullWidthMetres = 25f;

        /// <summary>Sag band beyond the bank, in local channel widths.</summary>
        public const float ValleyBandWidths = 1f;

        /// <summary>Fraction of the beyond-bank band that is the flat floodplain SOLE, the
        /// rest being the valley WALL that rises to meet the untouched hillside. A real
        /// valley section is a broad near-level floor then a distinct wall, not one curved
        /// scoop - so the sole is held flat across this fraction and only the wall fades
        /// out. 0.6 puts most of the floor in the flat plain, which is the low-confinement
        /// shape a meandering river builds; the wall's own steepness then comes from how
        /// high the surrounding ground stands, so a confined reach gets a steep wall and an
        /// open one a gentle wall with no per-reach flag.</summary>
        public const float ValleySoleFraction = 0.6f;

        /// <summary>Dry height held between the floodplain and the water, in local
        /// channel depths.</summary>
        public const float ValleyFreeboardDepths = 0.5f;

        /// <summary>Set false to build a river with no valley at all - a canal, a
        /// millrace, anything cut by hand rather than by water.</summary>
        public bool valley = true;

        // =====================================================================
        //  RUGGEDNESS
        // =====================================================================

        public float ruggednessMetres;
        public float ruggednessFeatureMetres = 18f;
        public int ruggednessOctaves = 3;
        public float ruggednessPersistence = 0.5f;
        public int seed = 20250811;

        /// <summary>Ruggedness is faded out over this distance in from the shore.
        /// Without it, noise on the bed breaks the waterline and the shoreline
        /// becomes a dashed line of islands.</summary>
        public float ruggednessCalmMetres = 8f;

        // =====================================================================
        //  KEPT ISLANDS
        //
        //  A projection large enough to read as land is left as land: the dig steps
        //  around it instead of flattening it, and the surface builder - reading this
        //  same class through HeightAt - leaves it dry, so the water routes around it.
        //  Small projections carry no mask and are dug to the bed like any other high
        //  spot, so a lake is not stippled with pin-prick islets.
        //
        //  A coarse row-major occupancy grid over the ring's plan bounds. Nearest-cell
        //  lookup is enough: an island edge is soft and the mesh keeps a fade ring at
        //  every waterline. Rebuilt from the terrain on every BuildDig, so its exact
        //  serialisation does not matter.
        // =====================================================================

        public bool[] islandMask;
        public float islandMinX;
        public float islandMinZ;
        public float islandCellSize;
        public int islandCols;
        public int islandRows;

        // =====================================================================
        //  BOUNDS
        // =====================================================================

        /// <summary>World bounds of the dig including bevel. Chunks outside it are
        /// skipped without touching a single sample.</summary>
        public Bounds worldBounds;

        public bool HasShape
        {
            get
            {
                return isCourse
                    ? (coursePoints != null && coursePoints.Count >= 2)
                    : (ringPoints != null && ringPoints.Count >= 3);
            }
        }

        /// <summary>How far outside the shape this dig can still change a height. One
        /// answer for the bounds, the chunk test and the sample window, because three
        /// separate ones drift apart and the symptom is a channel clipped flat at a
        /// chunk edge.</summary>
        private float OutsideReach()
        {
            return Mathf.Max(0f, bevelMetres) + Mathf.Max(0f, corridorMetres);
        }

        /// <summary>Cheap rejection for a chunk that this dig cannot reach.</summary>
        public bool AffectsRegion(Bounds chunkWorldBounds)
        {
            if (!enabled || !HasShape) return false;

            float pad = OutsideReach() + 2f;

            Bounds b = worldBounds;
            b.Expand(new Vector3(pad * 2f, 1e6f, pad * 2f));

            // XZ only: the dig is a plan-view shape and the chunk's Y extent has
            // nothing to say about whether the river crosses it.
            return b.min.x <= chunkWorldBounds.max.x && b.max.x >= chunkWorldBounds.min.x
                && b.min.z <= chunkWorldBounds.max.z && b.max.z >= chunkWorldBounds.min.z;
        }

        /// <summary>Recomputes worldBounds from the current shape. Called by the body
        /// after it fills the lists, so AffectsRegion never lies.</summary>
        public void RecomputeBounds()
        {
            List<Vector3> pts = isCourse ? coursePoints : ringPoints;
            if (pts == null || pts.Count == 0)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.zero);
                return;
            }

            Vector3 min = pts[0];
            Vector3 max = pts[0];

            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 p = pts[i];
                if (p.x < min.x) min.x = p.x;
                if (p.y < min.y) min.y = p.y;
                if (p.z < min.z) min.z = p.z;
                if (p.x > max.x) max.x = p.x;
                if (p.y > max.y) max.y = p.y;
                if (p.z > max.z) max.z = p.z;
            }

            Bounds b = new Bounds();
            b.SetMinMax(min, max);

            // A course is a line, so its own bounds are zero-width. Widen by the
            // widest half-width it carries or the ring gets clipped at its ends.
            float pad = HalfWidthMax() + OutsideReach() + 2f;
            b.Expand(new Vector3(pad * 2f, 4f, pad * 2f));

            worldBounds = b;
        }

        private float HalfWidthMax()
        {
            if (!isCourse || courseWidths == null) return 0f;

            float w = 0f;
            for (int i = 0; i < courseWidths.Count; i++)
                if (courseWidths[i] > w) w = courseWidths[i];

            return w * 0.5f;
        }

        // =====================================================================
        //  THE DIG
        // =====================================================================

        /// <summary>
        /// Folds this dig into a chunk's normalised heightfield. Mirrors
        /// TerrainOverrideLayer.ApplyTo so both can be called from the same place in
        /// TerrainBuilder, in a fixed order, with no interaction between them.
        ///
        /// heights is [z,x] normalised 0..1, which is what Unity's TerrainData wants.
        /// maxHeightMetres converts between the two, and is the world's, not ours.
        /// </summary>
        public void ApplyTo(float[,] heights, Bounds chunkWorldBounds,
                            float metresPerSample, float maxHeightMetres)
        {
            if (heights == null || maxHeightMetres <= 0f) return;
            if (!AffectsRegion(chunkWorldBounds)) return;

            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            if (resZ < 2 || resX < 2) return;

            float mps = Mathf.Max(1e-4f, metresPerSample);
            float invMax = 1f / maxHeightMetres;

            // Only the samples the dig can possibly reach. On a big world a small
            // pond touches a few hundred samples out of a million, and walking the
            // whole chunk to find that out would make every rebuild slower for
            // nothing.
            float pad = OutsideReach() + mps;

            int x0 = Mathf.Max(0, Mathf.FloorToInt((worldBounds.min.x - pad - chunkWorldBounds.min.x) / mps));
            int x1 = Mathf.Min(resX - 1, Mathf.CeilToInt((worldBounds.max.x + pad - chunkWorldBounds.min.x) / mps));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((worldBounds.min.z - pad - chunkWorldBounds.min.z) / mps));
            int z1 = Mathf.Min(resZ - 1, Mathf.CeilToInt((worldBounds.max.z + pad - chunkWorldBounds.min.z) / mps));

            if (x1 < x0 || z1 < z0) return;

            // ANTI-ALIASING FOR NARROW CHANNELS.
            //
            // A point sample per heightmap post cannot draw a channel narrower than the
            // post spacing: the dig band falls between posts on some rows and squarely
            // on a post on others, so the trench comes out as a row of disconnected pits
            // - the reported serrated/zig-zag trench. A 4 m stream (half-width 2 m) on a
            // 1 km / 256 = 3.9 m lattice is exactly this case.
            //
            // The honest fix is to sample each post's own footprint rather than its
            // centre alone, and take the DEEPEST result. Deepest is correct because the
            // dig is deepening-only (HeightAt ends in Min(existing, bed)): if the channel
            // passes anywhere through a post's cell, that post is carved, so the trench
            // is continuous and at least one post wide however narrow the channel. It
            // does not widen the water mesh - that is built separately from the spline -
            // only guarantees the terrain trench under it has no gaps.
            //
            // Paid only where it is needed. A body whose narrowest feature is already a
            // couple of posts across cannot alias, so it keeps the single-sample path and
            // its result is unchanged - lakes, oceans and wide rivers included.
            float narrowest = NarrowestFeatureMetres();
            int ss = Mathf.Clamp(Mathf.CeilToInt(2f * mps / Mathf.Max(0.5f, narrowest)), 1, 3);

            // The dig is computed into a scratch window first, then a bounded low-pass
            // rounds the DRY bank/valley cells before the result is written back. Why:
            // HeightAt is a smooth continuous surface, but sampled one point per
            // heightmap post (~4 m) with distinct bank/sole/wall regions, the discrete
            // result reads faceted - the reported "valley like joined stones". A low-pass
            // over the sampled field is the honest correction: it removes the sampling
            // facets and the region-break creases without changing the underlying shape
            // the maths already got right. The WETTED BED is excluded (see the eligible
            // test) so a channel keeps its solved depth, and the pass is deepening-only
            // so it can never raise ground into a wall the terrain did not have.
            int ww = x1 - x0 + 1;
            int wh = z1 - z0 + 1;
            float[] dugBuf = new float[ww * wh];
            float[] origBuf = new float[ww * wh];
            bool[] eligible = new bool[ww * wh];

            for (int lz = z0; lz <= z1; lz++)
            {
                float wz = chunkWorldBounds.min.z + lz * mps;

                for (int lx = x0; lx <= x1; lx++)
                {
                    float wx = chunkWorldBounds.min.x + lx * mps;

                    float existing = heights[lz, lx] * maxHeightMetres;

                    float dug;
                    if (ss <= 1)
                    {
                        dug = HeightAt(wx, wz, existing);
                    }
                    else
                    {
                        // Deepest sub-sample across the post's cell (span +/- half a
                        // post), so a channel threading between post centres still cuts
                        // the post whose cell it crosses.
                        dug = existing;
                        for (int sz = 0; sz < ss; sz++)
                        {
                            float oz = ((sz + 0.5f) / ss - 0.5f) * mps;
                            for (int sx = 0; sx < ss; sx++)
                            {
                                float ox = ((sx + 0.5f) / ss - 0.5f) * mps;
                                float d = HeightAt(wx + ox, wz + oz, existing);
                                if (d < dug) dug = d;
                            }
                        }
                    }

                    int w = (lz - z0) * ww + (lx - x0);
                    dugBuf[w] = dug;
                    origBuf[w] = existing;
                    // Eligible for the de-facet pass only where the dig actually lowered
                    // the ground AND the sample is on the DRY side of the waterline (the
                    // bank, floodplain and valley wall). The wetted bed is left exactly as
                    // solved so the river keeps its depth.
                    eligible[w] = dug < existing - 1e-3f && IsDryBankSample(wx, wz);
                }
            }

            SmoothValleyWindow(dugBuf, origBuf, eligible, ww, wh);

            for (int lz = z0; lz <= z1; lz++)
                for (int lx = x0; lx <= x1; lx++)
                {
                    int w = (lz - z0) * ww + (lx - x0);
                    // Only ever assigned, never accumulated. Two applications of the
                    // same dig to the same terrain give the same answer, which is
                    // what makes the order of chunk builds irrelevant.
                    heights[lz, lx] = Mathf.Clamp01(dugBuf[w] * invMax);
                }
        }

        // Smoothing strength and passes for the de-facet low-pass. Two gentle passes take
        // the sampling facets and region-break creases out of the valley while leaving the
        // low-frequency valley shape (which the maths already made smooth) essentially
        // untouched. Kept small on purpose: a strong blur would eat the bank the dig cut.
        private const int ValleySmoothPasses = 2;
        private const float ValleySmoothStrength = 0.5f;

        /// <summary>
        /// Bounded low-pass over the dry bank/valley cells of a dug window, in place.
        /// Each eligible cell is eased toward the mean of its four neighbours (which may
        /// be untouched ground, so the crease where the valley meets the hillside is what
        /// gets rounded), then clamped so it never rises above the original terrain -
        /// deepening only, exactly like the dig itself. Ineligible cells (the wetted bed,
        /// and untouched ground) are read as neighbours but never written, so the channel
        /// keeps its solved depth and no ground outside the dig is disturbed.
        /// </summary>
        private static void SmoothValleyWindow(float[] dug, float[] orig, bool[] eligible,
                                               int ww, int wh)
        {
            if (dug == null || ww < 3 || wh < 3) return;

            bool any = false;
            for (int i = 0; i < eligible.Length; i++) if (eligible[i]) { any = true; break; }
            if (!any) return;

            float[] src = new float[dug.Length];

            for (int pass = 0; pass < ValleySmoothPasses; pass++)
            {
                System.Array.Copy(dug, src, dug.Length);

                for (int z = 0; z < wh; z++)
                {
                    for (int x = 0; x < ww; x++)
                    {
                        int i = z * ww + x;
                        if (!eligible[i]) continue;

                        // Four-neighbour mean, edges falling back to the cell itself so a
                        // window-border cell is not dragged by a neighbour that is not there.
                        float left  = x > 0      ? src[i - 1]  : src[i];
                        float right = x < ww - 1 ? src[i + 1]  : src[i];
                        float down  = z > 0      ? src[i - ww] : src[i];
                        float up    = z < wh - 1 ? src[i + ww] : src[i];

                        float mean = (left + right + down + up) * 0.25f;
                        float eased = Mathf.Lerp(src[i], mean, ValleySmoothStrength);

                        // Deepening only: never lift a bank above the ground the terrain
                        // actually had there.
                        dug[i] = Mathf.Min(orig[i], eased);
                    }
                }
            }
        }

        /// <summary>
        /// True when a world sample is on the DRY side of this body's waterline - the
        /// bank, floodplain or valley wall - and within the dig's reach. False in the
        /// wetted channel/basin (so the de-facet pass leaves the bed's depth alone) and
        /// false for an ocean, whose bevel-only shape is already smooth and has no valley.
        /// Mirrors the region split at the top of <see cref="HeightAt"/>.
        /// </summary>
        private bool IsDryBankSample(float worldX, float worldZ)
        {
            if (!enabled || !HasShape || bevelOnly) return false;

            if (isCourse)
            {
                float signed, s, d, r;
                if (!CourseFrame(worldX, worldZ, out signed, out s, out d, out r)) return false;
                return signed > 0f;
            }

            if (!IsIsland(worldX, worldZ))
                return RingSignedDistance(worldX, worldZ) > 0f;

            return false;
        }

        /// <summary>
        /// The narrowest thing this dig has to draw, in metres, used to decide whether a
        /// point sample per post can resolve it. For a course it is the slimmest channel
        /// width along the run; a ring body (lake, pool, ocean) has no feature finer than
        /// its own footprint, so it never needs sub-sampling and reports a large value.
        /// </summary>
        private float NarrowestFeatureMetres()
        {
            if (!isCourse || courseWidths == null || courseWidths.Count == 0)
                return float.MaxValue;

            float min = float.MaxValue;
            for (int i = 0; i < courseWidths.Count; i++)
                if (courseWidths[i] > 0.05f && courseWidths[i] < min) min = courseWidths[i];

            return min == float.MaxValue ? maxDepthMetres * 2f : min;
        }

        /// <summary>
        /// The bed elevation in metres at one world position, given what the terrain
        /// was doing there. Returns existingY unchanged outside the bevel.
        ///
        /// This is the single source of truth for the shape. The inspector's preview,
        /// the gizmo and the chunk apply all call it, so what is drawn is what is dug.
        /// </summary>
        public float HeightAt(float worldX, float worldZ, float existingY)
        {
            if (!enabled || !HasShape) return existingY;

            // An ocean rounds its beach and touches nothing else. Handled before the
            // distance fields because it does not use them: the sea's edge is the
            // contour where the ground crosses sea level, which every sample already
            // knows from its own height, so no shape needs consulting to find it.
            if (bevelOnly) return BevelAgainstLevel(worldX, worldZ, existingY);

            // signed = metres from the waterline. Negative inside the body, positive
            // outside it on the bank. localRun is how far in from the waterline the
            // bed reaches full depth, which a river reads from its own half-width and
            // a lake from its shore run.
            float signed;
            float localSurfaceY;
            float localDepth;
            float localRun;

            if (isCourse)
            {
                if (!CourseFrame(worldX, worldZ, out signed, out localSurfaceY,
                                 out localDepth, out localRun))
                    return existingY;
            }
            else
            {
                signed = RingSignedDistance(worldX, worldZ);
                localSurfaceY = surfaceY;
                localDepth = maxDepthMetres;
                localRun = shoreRunMetres;
            }

            // ---- OUTSIDE: the rounded bank, then the corridor ----------------
            if (signed > 0f)
            {
                float bevel = Mathf.Max(0f, bevelMetres);
                float corridor = Mathf.Max(0f, corridorMetres);

                if (signed >= bevel + corridor) return existingY;

                float outside = existingY;

                if (signed < bevel && bevel > 1e-4f)
                {
                    // At the waterline the bank starts at exactly the surface elevation,
                    // so ground and water meet with no lip. It then eases to the original
                    // terrain by the far side of the bevel.
                    float t = signed / bevel;
                    float r = Mathf.Max(0.25f, bevelRoundness);
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Pow(t, r));

                    float blended = Mathf.Lerp(localSurfaceY, existingY, k);

                    // A bank may be pulled down to the waterline but never pushed up
                    // into a wall the terrain did not have. Raising it would build a dam
                    // around every pond dropped on a slope.
                    //
                    // The two cases this single Min covers, spelled out because the
                    // second one is where the hanging-water fault used to live:
                    //
                    //   ground ABOVE the water - blended sits between the two, so the
                    //   bank is cut down to meet the waterline. This is the common case
                    //   and it is what closes the gap.
                    //
                    //   ground BELOW the water - blended is above the ground, the Min
                    //   discards it, and the terrain is left exactly as it was. Nothing
                    //   here can fix that case without raising ground, so nothing here
                    //   tries: the water is lowered to its lowest containing bank
                    //   before the carve ever sees it. See UCWaterBody.ContainToBanks.
                    //
                    // This used to read Min(existing, Max(blended, Min(existing,
                    // surface))). Both branches of that collapse to the same two values
                    // as the line below, so the extra clamps only made the rule harder
                    // to read than it is.
                    outside = Mathf.Min(existingY, blended);

                }

                // THE VALLEY. Lowers a band either side of the bank toward the
                // floodplain, before the ceiling below shaves what stands too high.
                // Two separate questions: this one is how low the valley floor is,
                // that one is how fast the valley's sides may rise.
                outside = ValleySag(signed, bevel, corridor, localSurfaceY,
                                    localDepth, localRun, outside);

                return CorridorCap(signed, bevel, corridor, localSurfaceY, outside);
            }

            // ---- INSIDE: the bed --------------------------------------------
            // A kept island is land the dig steps around: hand back the ground
            // untouched so the hill survives and the surface builder (reading this same
            // function) stays dry over it, and the water routes around it. Only rings
            // carry islands; a course has none.
            if (!isCourse && IsIsland(worldX, worldZ)) return existingY;

            float inward = -signed;

            float run = Mathf.Max(0.5f, localRun);
            float t2 = Mathf.Clamp01(inward / run);

            // A basin is not a cone. Warping the shore-to-deep fraction moves the deep
            // water off-centre and gives some stretches of shore a fast drop and others
            // a long wade, which is what every real lake does and what a plain
            // distance-from-shore depth cannot. Rivers skip it: a channel's profile
            // across its width really is close to symmetric.
            if (!isCourse) t2 = WarpBasinFraction(t2, worldX, worldZ);

            float frac = WaterBedMath.DepthFraction(profile, t2, shelfFraction, bankExponent);
            float depth = Mathf.Max(minDepthMetres, localDepth * frac);

            float bed = localSurfaceY - depth;

            // ---- Ruggedness --------------------------------------------------
            if (ruggednessMetres > 1e-4f)
            {
                float calm = Mathf.Max(0.01f, ruggednessCalmMetres);
                float fade = Mathf.Clamp01(inward / calm);

                // Squared so the fade is gentle right at the edge and only reaches
                // full strength well inside. A linear fade still leaves visible
                // bumps in the first metre of water.
                fade *= fade;

                float n = WaterBedMath.Ruggedness(worldX, worldZ, ruggednessFeatureMetres,
                                                  ruggednessOctaves, ruggednessPersistence, seed);

                bed += n * ruggednessMetres * fade;
            }

            // The bed must stay under its own water surface however rough it is, or
            // ruggedness pokes islands through the middle of the river.
            bed = Mathf.Min(bed, localSurfaceY - minDepthMetres);

            // Deepening only. A bed is a hole, and lifting the ground to meet a
            // shallow bed would fill a valley the river happens to cross.
            return Mathf.Min(existingY, bed);
        }

        /// <summary>
        /// Caps how fast the ground may climb away from the waterline, over a band
        /// outside the bevel.
        ///
        /// This is the difference between terrain that was cut by the water and terrain
        /// that was built around it. The bevel alone hands back to raw generated ground
        /// the moment it ends, so a regenerated ridge can land against the bank and the
        /// river reads as a slot sawn through a cliff. The cap says: within this many
        /// metres of the water, the ground rises at a valley's rate, not a cliff's.
        ///
        /// The rate is half a metre up per metre out - an ordinary valley side. At the
        /// waterline the cap equals the water surface, which is exactly where the bevel
        /// already put the ground, so the two meet with no step however they are set.
        ///
        /// It only ever lowers. Ground that is already below the cap is left alone,
        /// which is why a river crossing a plain sees no change from any of this: flat
        /// ground never reaches the cap in the first place.
        /// </summary>
        private float CorridorCap(float signed, float bevel, float corridor,
                                  float localSurfaceY, float current)
        {
            if (corridor <= 1e-4f) return current;

            float strength = Mathf.Clamp01(corridorStrength);
            if (strength <= 1e-4f) return current;

            float reach = bevel + corridor;
            float u = Mathf.Clamp01(signed / Mathf.Max(0.5f, reach));

            // Smoothstepped rather than linear so the cap releases the terrain gently at
            // the outer edge. A linear release leaves a visible crease in the hillside
            // along the corridor boundary, which is the exact artefact this is here to
            // prevent, just moved further out.
            float rise = reach * 0.5f * Mathf.SmoothStep(0f, 1f, u);
            float cap = localSurfaceY + rise;

            if (current <= cap) return current;

            return Mathf.Lerp(current, cap, strength);
        }

        /// <summary>
        /// Lowers the ground either side of the bank toward the river's floodplain, and
        /// hands it back untouched everywhere the valley does not reach.
        /// </summary>
        /// <remarks>
        /// Every bound is in channel widths or channel depths - see the constants above -
        /// so the same rule gives a brook a scrape and a trunk river a broad trough
        /// without a size table anywhere.
        ///
        /// Three properties are load-bearing and each one is a fault that was visible
        /// before it held:
        ///
        ///   Zero at BOTH ends of the band. At the waterline the sag is nothing, so the
        ///   ground still meets the water at exactly surface level and there is no lip
        ///   to stand on. At the far edge it is nothing again, so the valley meets the
        ///   untouched hillside with no crease along a line.
        ///
        ///   Lower only, never raise. Ground already below the floodplain is a backwater
        ///   or a side channel; filling it would be this function inventing land the
        ///   generator never made.
        ///
        ///   Never below the water. The floodplain is dry land at normal flow, held a
        ///   fraction of the channel's depth clear of the surface. Without that the
        ///   trough bottoms out at waterline and the valley reads as marsh.
        /// </remarks>
        private float ValleySag(float signed, float bevel, float corridor,
                                float localSurfaceY, float localDepth, float localRun,
                                float current)
        {
            // Rings never get one: a lake's valley is the basin it sits in, and its
            // shore is the bevel. This is the river path only.
            if (!valley || !isCourse) return current;

            // 0 corridor is off, and off has to mean exactly the old behaviour.
            if (corridor <= 1e-4f) return current;

            // No bank, no valley. A river built with the bevel at 0 is a cut channel by
            // request - a canal - and the sag would have nowhere to ease in from, so it
            // would put a step at the waterline instead of a bank.
            if (bevel <= 1e-4f) return current;

            float strength = Mathf.Clamp01(corridorStrength);
            if (strength <= 1e-4f) return current;

            // Big rivers only, gated on the LOCAL width, which is what lets one river
            // run in a notch at its head and a floodplain at its mouth.
            float width = Mathf.Max(0f, localRun) * 2f;
            float size = Mathf.InverseLerp(ValleyOnsetWidthMetres, ValleyFullWidthMetres, width);
            if (size <= 1e-4f) return current;

            size = Mathf.SmoothStep(0f, 1f, size);

            float band = bevel + Mathf.Min(corridor, width * ValleyBandWidths);
            if (signed >= band) return current;

            // A real valley section beyond the bank is not one curved scoop - it is a
            // broad, near-level SOLE (the floodplain the river built by migrating across
            // it) and then a distinct WALL that rises to meet the untouched hillside. The
            // old shape faded linearly from the bank straight out to the band edge, which
            // dug deepest right at the channel and barely touched the ground 30 m out: a
            // bowl, not a plain. Split the beyond-bank band instead:
            //
            //   bank      : signed <= bevel        - ease up from the waterline (unchanged)
            //   sole      : bevel..soleEnd         - held FULL, so the floor stays flat
            //   wall      : soleEnd..band          - fade to nothing into the hillside
            //
            // The wall's own steepness is not set here and needs no per-reach flag: the
            // floor sits at one elevation (the floodplain), so where the surrounding
            // ground stands high the wall is tall over the same run - a confined, steep
            // valley - and where it stands low the wall is short and gentle. The terrain
            // decides, which is the real-world behaviour.
            float soleEnd = bevel + ValleySoleFraction * Mathf.Max(1e-4f, band - bevel);

            float shape;
            if (signed <= bevel)
                shape = signed / bevel;                                  // bank ramp
            else if (signed <= soleEnd)
                shape = 1f;                                              // flat sole
            else
                shape = 1f - (signed - soleEnd) / Mathf.Max(1e-4f, band - soleEnd); // wall

            shape = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(shape));

            float depth = Mathf.Max(0f, localDepth);

            // The valley floor: the floodplain, held a fraction of the channel's depth
            // above the water so it is dry land at normal flow. rampIn keeps it at
            // exactly the waterline where the bank meets the water - no lip - and lifts
            // it to the floodplain over the width of the bevel.
            float rampIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(signed / bevel));
            float floor = localSurfaceY + depth * ValleyFreeboardDepths * size * rampIn;

            // Pull the ground TOWARD that floor - do not subtract a fixed trough from it.
            // The old code lowered the band by one channel depth, which for an alluvial
            // river is only a metre or two: where the surrounding ground stood higher
            // than that, the scrape could not reach the floodplain and the rise was left
            // reading as two hills with the river slotted between them (the reported
            // fault). Targeting the floor elevation and easing toward it by strength x
            // shape excavates a real valley however high the ground stands, and still
            // fades to nothing at both ends of the band (shape) and at the waterline
            // (the bank blend above), so there is no crease and no lip. Lower only:
            // ground already at or below the floodplain is a backwater the generator
            // made, not ours to fill.
            float pull = Mathf.Clamp01(size * strength * shape);
            float target = Mathf.Lerp(current, floor, pull);

            return Mathf.Min(current, target);
        }

        /// <summary>
        /// Warps the shore-to-deep fraction so the basin is not a cone.
        ///
        /// The warp is scaled by t, which is what keeps the waterline honest: at the
        /// shore t is 0, so the warp contributes nothing however strong it is set, and
        /// the water's edge stays exactly on the outline the body was given. It grows
        /// with depth, so the deep water is free to sit off to one side while the
        /// shallows still begin where the shore says they do.
        /// </summary>
        private float WarpBasinFraction(float t, float worldX, float worldZ)
        {
            float amount = Mathf.Clamp01(basinIrregularity);
            if (amount <= 1e-4f) return t;

            float n = WaterBedMath.Ruggedness(worldX, worldZ,
                                              Mathf.Max(2f, basinFeatureMetres),
                                              3, 0.5f, seed + 7717);

            float warped = t + n * amount * 0.55f * t * (1f - t * 0.5f);
            return Mathf.Clamp01(warped);
        }

        // =====================================================================
        //  BEVEL ONLY  (the ocean path)
        // =====================================================================

        /// <summary>
        /// Softens the ground where it crosses the water level, and changes nothing
        /// anywhere else. No shelf, no dig, no depth of its own.
        ///
        /// A drawn shoreline is no use here, so the beach is found the way the sea
        /// actually finds it: by how far the ground is, vertically, from sea level.
        /// Ground well above the water and ground well below it are both left exactly
        /// as the terrain built them - only the thin band that straddles the waterline
        /// is eased, and even there the ground is only ever pulled down, never raised.
        ///
        /// That last rule is what makes this safe to leave switched on across an
        /// entire ocean: the seabed is untouched, the cliffs are untouched, and the
        /// only thing that changes is that the surf line no longer meets the sand at
        /// a hard rim.
        /// </summary>
        private float BevelAgainstLevel(float worldX, float worldZ, float existingY)
        {
            float bevel = Mathf.Max(0f, bevelMetres);
            if (bevel <= 1e-4f) return existingY;

            // Vertical distance from sea level, in metres. Positive is beach.
            float above = existingY - surfaceY;

            // Below the waterline the seabed is the terrain's business, exactly as it
            // was before. Far above it, so is the hillside.
            if (above <= 0f) return existingY;

            // The band is measured as a height rather than a distance because that is
            // the only measure available without a shape - and it behaves correctly
            // for the case that matters: a shallow beach has a wide soft band and a
            // cliff has almost none, which is what the real difference between them is.
            float band = bevel * BeachSlopeRatio;
            if (above >= band) return existingY;

            float t = above / band;
            float r = Mathf.Max(0.25f, bevelRoundness);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Pow(t, r));

            // Eased from the waterline up to the terrain across the band. Pulling
            // down only, so no sample is ever lifted into a berm the sea would have
            // to climb.
            float blended = Mathf.Lerp(surfaceY, existingY, k);
            return Mathf.Min(existingY, blended);
        }

        /// <summary>
        /// Converts the bevel's horizontal width into the vertical band the ocean
        /// bevel works in. A beach that a bevel of N metres should soften rises about
        /// a fifth of N over that distance - roughly an 11 degree foreshore, which is
        /// a normal sand beach.
        /// </summary>
        private const float BeachSlopeRatio = 0.2f;

        // =====================================================================
        //  DISTANCE FIELDS
        //
        //  Both return metres, signed so that negative is wet. They are the only
        //  part of the carve that knows what shape the body is, which is what keeps
        //  HeightAt free of per-kind branching.
        // =====================================================================

        /// <summary>
        /// Nearest point on the course, and what the river is doing there. Returns
        /// false when the sample is out of reach entirely, so the caller can leave
        /// the terrain alone without computing a profile.
        ///
        /// signedDistance is metres from the water's edge: negative in the channel,
        /// positive on the bank. Width, depth and surface elevation are interpolated
        /// along the segment, so a river that widens and deepens down its length does
        /// so smoothly rather than in steps at each spline point.
        /// </summary>
        private bool CourseFrame(float worldX, float worldZ, out float signedDistance,
                                 out float localSurfaceY, out float localDepth, out float localRun)
        {
            signedDistance = 0f;
            localSurfaceY = surfaceY;
            localDepth = maxDepthMetres;
            localRun = 1f;

            int count = coursePoints.Count;
            if (count < 2) return false;

            float bestSqr = float.MaxValue;
            int bestSeg = -1;
            float bestT = 0f;

            for (int i = 0; i < count - 1; i++)
            {
                Vector3 a = coursePoints[i];
                Vector3 b = coursePoints[i + 1];

                float dx = b.x - a.x;
                float dz = b.z - a.z;

                float lenSqr = dx * dx + dz * dz;
                if (lenSqr < 1e-8f) continue;

                float t = ((worldX - a.x) * dx + (worldZ - a.z) * dz) / lenSqr;
                t = Mathf.Clamp01(t);

                float px = a.x + dx * t;
                float pz = a.z + dz * t;

                float ox = worldX - px;
                float oz = worldZ - pz;
                float sqr = ox * ox + oz * oz;

                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestSeg = i;
                    bestT = t;
                }
            }

            if (bestSeg < 0) return false;

            float distance = Mathf.Sqrt(bestSqr);

            float wA = WidthAt(bestSeg);
            float wB = WidthAt(bestSeg + 1);
            float halfWidth = Mathf.Max(0.05f, Mathf.Lerp(wA, wB, bestT) * 0.5f);

            // Nothing beyond the bank and its corridor is this river's business.
            if (distance > halfWidth + OutsideReach()) return false;

            localDepth = Mathf.Lerp(DepthAt(bestSeg), DepthAt(bestSeg + 1), bestT);
            localSurfaceY = Mathf.Lerp(SurfaceAt(bestSeg), SurfaceAt(bestSeg + 1), bestT);

            // A channel is deepest at its middle, so the run from bank to deepest
            // point is half the width.
            localRun = halfWidth;

            signedDistance = distance - halfWidth;
            return true;
        }

        private float WidthAt(int i)
        {
            if (courseWidths == null || courseWidths.Count == 0) return 12f;
            return courseWidths[Mathf.Clamp(i, 0, courseWidths.Count - 1)];
        }

        private float DepthAt(int i)
        {
            if (courseDepths == null || courseDepths.Count == 0) return maxDepthMetres;
            return courseDepths[Mathf.Clamp(i, 0, courseDepths.Count - 1)];
        }

        private float SurfaceAt(int i)
        {
            if (courseSurfaceY == null || courseSurfaceY.Count == 0) return surfaceY;
            return courseSurfaceY[Mathf.Clamp(i, 0, courseSurfaceY.Count - 1)];
        }

        /// <summary>
        /// Signed distance to a closed ring: negative inside, positive outside, in
        /// metres. Inside/outside comes from an even-odd crossing count and the
        /// magnitude from the nearest edge, because the two questions are cheap
        /// separately and expensive together.
        /// </summary>
        private float RingSignedDistance(float worldX, float worldZ)
        {
            int count = ringPoints.Count;
            if (count < 3) return float.MaxValue;

            bool inside = false;
            float bestSqr = float.MaxValue;

            int j = count - 1;
            for (int i = 0; i < count; i++)
            {
                Vector3 a = ringPoints[i];
                Vector3 b = ringPoints[j];

                // Even-odd: count how many edges a ray cast in +X crosses.
                if ((a.z > worldZ) != (b.z > worldZ))
                {
                    float cross = (b.x - a.x) * (worldZ - a.z) / (b.z - a.z) + a.x;
                    if (worldX < cross) inside = !inside;
                }

                float dx = b.x - a.x;
                float dz = b.z - a.z;
                float lenSqr = dx * dx + dz * dz;

                float t = lenSqr < 1e-8f
                    ? 0f
                    : Mathf.Clamp01(((worldX - a.x) * dx + (worldZ - a.z) * dz) / lenSqr);

                float ox = worldX - (a.x + dx * t);
                float oz = worldZ - (a.z + dz * t);
                float sqr = ox * ox + oz * oz;

                if (sqr < bestSqr) bestSqr = sqr;

                j = i;
            }

            float distance = Mathf.Sqrt(bestSqr);
            return inside ? -distance : distance;
        }

        /// <summary>
        /// True where a kept island sits, by nearest cell of the keep-out mask. False
        /// when there is no mask, which is the common case and the old behaviour
        /// exactly - so a body with no large projections is dug just as before.
        /// </summary>
        private bool IsIsland(float worldX, float worldZ)
        {
            if (islandMask == null || islandMask.Length == 0 || islandCellSize <= 0f) return false;

            int cx = Mathf.FloorToInt((worldX - islandMinX) / islandCellSize);
            int cz = Mathf.FloorToInt((worldZ - islandMinZ) / islandCellSize);
            if (cx < 0 || cz < 0 || cx >= islandCols || cz >= islandRows) return false;

            return islandMask[cz * islandCols + cx];
        }

        // =====================================================================
        //  SURFACE CONTACT
        // =====================================================================

        /// <summary>
        /// Where the water surface should sit at a world position for the mesh to
        /// meet the ground with no gap. The mesh builder calls this instead of
        /// guessing, which is what guarantees the two agree: both read one function.
        /// </summary>
        public float SurfaceYAt(float worldX, float worldZ)
        {
            if (!isCourse) return surfaceY;

            float signed, s, d, run;
            if (!CourseFrame(worldX, worldZ, out signed, out s, out d, out run))
                return surfaceY;

            return s;
        }

        /// <summary>
        /// Depth of water at a world position after the dig, in metres. Negative
        /// where the carved bed is above the surface, which is dry land. Used by the
        /// surface builder to trim its mesh exactly to the waterline rather than to
        /// an approximation of it.
        /// </summary>
        public float WaterDepthAt(float worldX, float worldZ, float existingY)
        {
            float bed = HeightAt(worldX, worldZ, existingY);
            return SurfaceYAt(worldX, worldZ) - bed;
        }

        // =====================================================================
        //  MAINTENANCE
        // =====================================================================

        public void Clear()
        {
            if (coursePoints != null) coursePoints.Clear();
            if (courseWidths != null) courseWidths.Clear();
            if (courseDepths != null) courseDepths.Clear();
            if (courseSurfaceY != null) courseSurfaceY.Clear();
            if (ringPoints != null) ringPoints.Clear();

            islandMask = null;
            islandCols = 0;
            islandRows = 0;

            worldBounds = new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>Total shape points, for the inspector readout.</summary>
        public int PointCount
        {
            get
            {
                if (isCourse) return coursePoints != null ? coursePoints.Count : 0;
                return ringPoints != null ? ringPoints.Count : 0;
            }
        }
    }
}
