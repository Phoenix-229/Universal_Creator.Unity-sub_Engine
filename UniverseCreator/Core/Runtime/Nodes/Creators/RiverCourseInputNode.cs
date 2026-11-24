// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverCourseInputNode.cs
//  Purpose: Bridges a HAND-DRAWN river into the node graph. A manual river is a
//           UCWaterBody (WaterBodyKind.River) whose child UCRiverNodes lay the course;
//           it carves the terrain and builds its own water surface scene-side, but the
//           GRAPH never sees it - so graph-side scatter, texture and the River Banks /
//           Water Mask nodes cannot tell there is a river there, and trees happily grow
//           down the middle of a hand-drawn channel. This node closes that: it emits the
//           SAME River Mask the procedural River Network does, so a manual river feeds the
//           identical Banks / Water Mask / scatter-exclusion tail.
//
//  FIX 2026-10-19: THIS NODE NOW CUTS THE TRENCH AND BANKS TOO
//  -----------------------------------------------------------
//  The original design only published a mask. Authors reported "I connected River Course
//  Input and nothing spawns": no trench, no banks. Two causes, both fixed:
//    (a) the course was only pushed into the node when the SEA LEVEL changed, so on a normal
//        bake the node held no course and emitted an empty mask (see UCWaterSystemManager).
//    (b) even with a course the Height output was a pure pass-through, so the graph showed no
//        channel. The node now cuts a descending-bed channel plus a slope-limited bank
//        (deepening only, Min against the ground) when Carve Trench is on. Because it is a Min
//        against the ground and the scene-side dig is also a Min, the two never stack - the
//        deeper of the two wins - so turning this on cannot double the cut.
//
//  (original note, kept for history) WHY IT DID NOT CARVE (the no-duplication rule)
//  -----------------------------------------------
//  Height passes through UNTOUCHED. The manual river already cuts its trench into the
//  terrain scene-side (UCWaterBody.BuildRiverDig via WaterCarveLayer) and fills it with
//  its own surface (UCWaterBody.BuildRiverBody). Re-carving here would double the cut and
//  fight the settled carve path. This node's only product is the mask; the ground and the
//  water are already handled where they belong.
//
//  WHERE THE COURSE COMES FROM
//  ---------------------------
//  Nodes are pure over the tile and must not reach into the scene. So the water system
//  (UCWaterSystemManager) PUSHES each manual river's baked world course into this node
//  before the graph evaluates - the same way it pushes the settled sea level into the
//  nodes that read it. With nothing pushed (no manual rivers, or a graph opened cold) the
//  mask is simply empty and the Height still passes through, so the node is inert, never
//  wrong. Deterministic: a pure function of the pushed course and the tile's world rect.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    /// <summary>
    /// Reads hand-drawn river courses (pushed by the water system) and emits a River Mask at
    /// chunk resolution, optionally cutting Height when carveTrench is enabled. Outputs mirror River Network
    /// (Height, River Mask) so a manual river plugs into the same River Banks / Water Mask /
    /// scatter tail the procedural network uses. Pure; the course is pushed, never fetched.
    /// </summary>
    [System.Serializable]
    public class RiverCourseInputNode : Generator, IPreparedGraphInputs, IPreparedGraphInputHash, IGenerationSnapshotInputs
    {
        public override string NodeName { get { return "River Course Input"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }

        public override string Tooltip
        {
            get
            {
                // UC-T03-03: describe the actual authored toggle, not the historical mask-only implementation.
                return carveTrench
                    ? "Carves the hand-drawn river course into Height and outputs its River Mask."
                    : "Outputs the hand-drawn River Mask and passes Height through unchanged.";
            }
        }

        /// <summary>Soft wetted edge in metres outside the channel half-width (feathers the mask).</summary>
        [Tooltip("Mask edge feather in world metres. Minimum 0.01 m; nonfinite values use that minimum.")] // UC-T03-03: expose the executed units and limit.
        public float edgeFeatherMetres = 1.5f;

        /// <summary>UC-T03-03: channel half-width in metres for a missing/nonfinite course width; shared by height, mask and bounds.</summary>
        [Tooltip("Fallback channel half-width in metres for missing or invalid point widths. Both the trench and its mask use it; the minimum is 0.1 m.")]
        public float fallbackHalfWidthMetres = 3f;

        [Tooltip("Cut the authored channel and banks into Height. Turn off to publish its mask and pass Height through unchanged.")] // UC-T03-03: terrain and scene-water ownership are separate.
        public bool carveTrench = true;

        [Tooltip("Steepest the bank may rise away from the water, as rise over run. 0.5 = about 27 degrees. Lower = a wider, gentler valley.")]
        public float bankSlope = 0.5f;

        [Tooltip("Furthest the graded bank reaches beyond the channel edge, in metres. Past it the terrain is untouched; the grade fades out smoothly before it, so there is no step.")]
        public float bankReachMetres = 40f;

        // Scene-derived, pushed by UCWaterSystemManager before evaluation. NOT serialized into
        // the graph asset (a graph is a project asset; a scene river is not part of it).
        [System.NonSerialized] private List<ManualRiverCourse> _courses;
        [System.NonSerialized] private bool manualCoursesAssigned;

        /// <summary>One hand-drawn river's baked world course: XZ points and the half-width at each.</summary>
        [System.Serializable] // UC-T01-07: every consumed course field participates in its dependency.
        public sealed class ManualRiverCourse
        {
            /// <summary>Source body identity within the loaded world; zero for an anonymous pushed course.</summary>
            public int bodyId;
            public Vector2[] pointsXZ;    // world-space course points
            public float[] halfWidthM;    // channel half-width at each point (>=0)
            public float[] bedY;          // solved thalweg elevation, WORLD metres (null = mask only)
            public float[] depthM;        // channel depth in metres (null = mask only)

            // Plan bounds, padded by the widest reach, so a chunk the course cannot touch is skipped.
            public float minX, maxX, minZ, maxZ;
            /// <summary>UC-T03-03: bounds the course, resolved channel width and requested outer band.</summary>
            /// <param name="pad">Nonnegative additional bank/feather extent in world metres; nonfinite values use zero.</param>
            /// <remarks>Empty points leave empty bounds. The node supplies its authored fallback during preparation.</remarks>
            public void ComputeBounds(float pad) { ComputeBounds(pad, 0f); }

            // Preserve the public one-argument API, including existing method-group callers.
            internal void ComputeBounds(float pad, float fallbackHalfWidth)
            {
                minX = minZ = float.MaxValue; maxX = maxZ = float.MinValue;
                if (pointsXZ == null) return;
                float hw = 0f;
                for (int i = 0; i < pointsXZ.Length; i++)
                {
                    hw = Mathf.Max(hw, ResolveHalfWidth(this, i, fallbackHalfWidth));
                    if (pointsXZ[i].x < minX) minX = pointsXZ[i].x;
                    if (pointsXZ[i].x > maxX) maxX = pointsXZ[i].x;
                    if (pointsXZ[i].y < minZ) minZ = pointsXZ[i].y;
                    if (pointsXZ[i].y > maxZ) maxZ = pointsXZ[i].y;
                }
                // UC-T03-03: finite authored extents must not overflow prepared bounds.
                double p = (Finite(pad) ? System.Math.Max(0d, pad) : 0d) + hw;
                minX = FiniteFloat(minX - p); maxX = FiniteFloat(maxX + p);
                minZ = FiniteFloat(minZ - p); maxZ = FiniteFloat(maxZ + p);
            }
        }

        /// <summary>Water system hands the manual river courses in before the graph bakes. Null clears them.</summary>
        // ------------------------------------------------------------------------------------
        //  SCENE SNAPSHOT - why a regenerate can no longer lose the hand-placed rivers
        // ------------------------------------------------------------------------------------
        //  The course used to reach this node ONLY by being pushed into the instance. The instance
        //  field is not serialised, so a domain reload, a graph reload or a regenerate that ran before
        //  the next push left the node empty and the trench vanished from the graph's terrain.
        //  Each hand-placed river now PUBLISHES its solved course here whenever it builds, and
        //  withdraws it when it is disabled. The node reads this snapshot at Generate time, so the
        //  trench is present on every bake whatever order things ran in. UC-T01-06/07: this legacy
        //  registry is read only during main-thread preparation. World bakes explicitly push their
        //  own bodies before preparation; workers read only this node's detached course arrays.
        // ------------------------------------------------------------------------------------
        private static readonly object SnapLock = new object();
        private static readonly Dictionary<int, ManualRiverCourse> SnapBodies = new Dictionary<int, ManualRiverCourse>();
        private static List<ManualRiverCourse> _snapshot = new List<ManualRiverCourse>();

        public static void PublishBodyCourse(int bodyId, ManualRiverCourse course)
        {
            lock (SnapLock)
            {
                if (course == null || course.pointsXZ == null || course.pointsXZ.Length < 2) SnapBodies.Remove(bodyId);
                else { course.ComputeBounds(40f); SnapBodies[bodyId] = course; }
                _snapshot = new List<ManualRiverCourse>(SnapBodies.Values);
            }
        }

        public static void UnpublishBodyCourse(int bodyId) { PublishBodyCourse(bodyId, null); }

        private static List<ManualRiverCourse> Snapshot() { lock (SnapLock) return _snapshot; }

        /// <summary>UC-T01-06/07: captures legacy published inputs when no explicit world courses were assigned.</summary>
        /// <remarks>Main thread only; explicitly assigning null clears inputs and suppresses the global fallback.</remarks>
        public override void PrepareMainThread()
        {
            if (!manualCoursesAssigned) _courses = CopyCourses(Snapshot());
        }

        /// <summary>UC-T01-06/07: detaches the supplied world courses before hashing or generation.</summary>
        /// <param name="courses">Ordered world-metre points, half-widths, beds and depths; null clears all courses.</param>
        /// <remarks>Main thread. Later edits to supplied lists/arrays require a new push and cannot change an active bake.</remarks>
        /// <exception cref="System.ArgumentException">A supplied position, bed elevation or depth is nonfinite; previous captured courses remain intact.</exception>
        public void SetManualCourses(List<ManualRiverCourse> courses)
        {
            List<ManualRiverCourse> prepared = CopyCourses(courses); // UC-T03-03: validate before publishing any new preparation state.
            manualCoursesAssigned = true;
            _courses = prepared;
        }

        private List<ManualRiverCourse> CopyCourses(List<ManualRiverCourse> courses)
        {
            if (courses == null || courses.Count == 0) return null;
            List<ManualRiverCourse> copy = new List<ManualRiverCourse>(courses.Count);
            for (int i = 0; i < courses.Count; i++)
            {
                ManualRiverCourse source = courses[i];
                if (source == null) { copy.Add(null); continue; }
                ValidateCourse(source); // UC-T03-03: reject invalid geometry at the preparation boundary, not inside a worker's sample loop.
                ManualRiverCourse course = new ManualRiverCourse
                {
                    bodyId = source.bodyId,
                    pointsXZ = source.pointsXZ != null ? (Vector2[])source.pointsXZ.Clone() : null,
                    halfWidthM = source.halfWidthM != null ? (float[])source.halfWidthM.Clone() : null,
                    bedY = source.bedY != null && source.bedY.Length > 0 ? (float[])source.bedY.Clone() : null,
                    depthM = source.depthM != null && source.depthM.Length > 0 ? (float[])source.depthM.Clone() : null
                };
                float feather = Finite(edgeFeatherMetres) ? Mathf.Max(0.01f, edgeFeatherMetres) : 0.01f;
                float reach = Finite(bankReachMetres) ? Mathf.Max(0f, bankReachMetres) : 0f;
                course.ComputeBounds(Mathf.Max(feather, reach), fallbackHalfWidthMetres);
                copy.Add(course);
            }
            return copy;
        }

        // UC-T03-03: retain missing-width provenance through snapshots; resolving it here
        // and in bounds lets a later fallback edit affect the next bake without a new course push.
        private static float ResolveHalfWidth(ManualRiverCourse course, int index, float fallback)
        {
            float width = course.halfWidthM != null && index < course.halfWidthM.Length
                ? course.halfWidthM[index] : fallback;
            if (!Finite(width)) width = Finite(fallback) ? fallback : 3f;
            return Mathf.Max(0.1f, width);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // UC-T03-03: saturate only the representable range, without imposing an authored world-size limit.
        private static float FiniteFloat(double value) => (float)System.Math.Max(-float.MaxValue, System.Math.Min(float.MaxValue, value));

        private static void ValidateCourse(ManualRiverCourse course)
        {
            if (course.pointsXZ != null)
                foreach (Vector2 point in course.pointsXZ)
                    if (!Finite(point.x) || !Finite(point.y))
                        throw new System.ArgumentException("River course positions must be finite world metres.", "courses");
            if (course.bedY != null)
                foreach (float bed in course.bedY)
                    if (!Finite(bed)) throw new System.ArgumentException("River bed elevations must be finite world metres.", "courses");
            if (course.depthM != null)
                foreach (float depth in course.depthM)
                    if (!Finite(depth)) throw new System.ArgumentException("River depths must be finite metres.", "courses");
        }

        /// <summary>UC-T01-07: fingerprints complete captured course data, including absence and sub-decimetre edits.</summary>
        /// <returns>A 32-bit fold of the prepared dependency hash for the existing main-thread push comparison.</returns>
        public int CourseSignature()
        {
            ulong hash = GetPreparedInputHash();
            return unchecked((int)(hash ^ (hash >> 32)));
        }

        [System.Serializable]
        private sealed class PreparedCourses
        {
            public List<ManualRiverCourse> courses;
        }

        /// <summary>UC-T01-07: exposes the detached data used by this node's next bake.</summary>
        /// <returns>Plain course data, or null for no courses; main thread after preparation.</returns>
        public object GetPreparedInputsForHash()
        {
            return _courses != null ? new PreparedCourses { courses = _courses } : null;
        }

        /// <summary>UC-T01-07: hashes every captured point, width, bed, depth, bound and source identity in order.</summary>
        /// <returns>Deterministic 64-bit fingerprint; no allocation or scene/Unity access.</returns>
        public ulong GetPreparedInputHash()
        {
            ulong hash = PreparedRasterInputs.Mix(14695981039346656037UL,
                _courses == null ? uint.MaxValue : (uint)_courses.Count);
            if (_courses == null) return hash;
            for (int c = 0; c < _courses.Count; c++)
            {
                ManualRiverCourse course = _courses[c];
                hash = PreparedRasterInputs.Mix(hash, course == null ? 0U : 1U);
                if (course == null) continue;
                hash = PreparedRasterInputs.Mix(hash, unchecked((uint)course.bodyId));
                hash = PreparedRasterInputs.Mix(hash, course.pointsXZ == null ? uint.MaxValue : (uint)course.pointsXZ.Length);
                if (course.pointsXZ != null)
                    for (int i = 0; i < course.pointsXZ.Length; i++)
                    {
                        hash = PreparedRasterInputs.MixFloat(hash, course.pointsXZ[i].x);
                        hash = PreparedRasterInputs.MixFloat(hash, course.pointsXZ[i].y);
                    }
                hash = MixArray(hash, course.halfWidthM);
                hash = MixArray(hash, course.bedY);
                hash = MixArray(hash, course.depthM);
                hash = PreparedRasterInputs.MixFloat(hash, course.minX);
                hash = PreparedRasterInputs.MixFloat(hash, course.maxX);
                hash = PreparedRasterInputs.MixFloat(hash, course.minZ);
                hash = PreparedRasterInputs.MixFloat(hash, course.maxZ);
            }
            return hash;
        }

        private static ulong MixArray(ulong hash, float[] array)
        {
            ulong value = PreparedRasterInputs.Hash(0, 0, array);
            return PreparedRasterInputs.Mix(PreparedRasterInputs.Mix(hash, (uint)value), (uint)(value >> 32));
        }

        /// <summary>UC-T01-06: copies explicit course state to a same-type generation node on the main thread.</summary>
        /// <param name="snapshot">Detached RiverCourseInputNode; another type is rejected.</param>
        public void CopyInputsTo(Generator snapshot)
        {
            RiverCourseInputNode target = snapshot as RiverCourseInputNode;
            if (target == null) throw new System.ArgumentException("Expected a River Course Input snapshot.", "snapshot");
            if (manualCoursesAssigned) target.SetManualCourses(_courses);
        }

        /// <summary>True when at least one manual course has been captured for this node.</summary>
        public bool HasCourses { get { return _courses != null && _courses.Count > 0; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            // Height: pass-through, plus the trench and graded banks when Carve Trench is on.
            if (outputIndex == 0)
            {
                Matrix h = new Matrix(src);
                List<ManualRiverCourse> eff = _courses;
                if (carveTrench && eff != null && eff.Count > 0)
                {
                    // UC-T03-02: graph-owned manual trenches follow the same terminal composition as procedural ones.
                    h.trenchReference = src.trenchReference ?? src;
                    CarveCourses(h, eff, stop);
                }
                return h;
            }

            // River Mask: rasterize the pushed course(s) onto this chunk's world rect.
            Matrix dst = src.Blank();
            List<ManualRiverCourse> courses = _courses;
            if (courses == null || courses.Count == 0) return dst;   // no manual river -> empty mask

            float feather = Finite(edgeFeatherMetres) ? Mathf.Max(0.01f, edgeFeatherMetres) : 0.01f;
            Vector2 px = dst.PixelSize;
            float ox = dst.worldPos.x;
            float oz = dst.worldPos.z;
            int sx = dst.rect.size.x;
            int sz = dst.rect.size.z;

            for (int z = 0; z < sz; z++)
            {
                if (stop != null && (z & 15) == 0 && stop.StopRequested) return null;
                float wz = oz + z * px.y;
                int row = z * sx;
                for (int x = 0; x < sx; x++)
                {
                    float wx = ox + x * px.x;
                    dst.arr[row + x] = MaskAt(wx, wz, courses, feather, fallbackHalfWidthMetres);
                }
            }
            return dst;
        }

        /// <summary>
        /// Cuts every pushed course into <paramref name="h"/>: a parabolic channel whose thalweg is
        /// the solved descending bed, then a bank that rises away from the water at no more than
        /// <see cref="bankSlope"/>, fading to nothing at <see cref="bankReachMetres"/>.
        /// Deepening only (Min against the ground), so it can never build a levee or raise a floor.
        /// </summary>
        private void CarveCourses(Matrix h, List<ManualRiverCourse> courses, StopToken stop)
        {
            Vector2 px = h.PixelSize;
            float ox = h.worldPos.x, oy = h.worldPos.y, oz = h.worldPos.z;
            float worldH = h.worldHeight > 1e-3f ? h.worldHeight : 600f;
            int sx = h.rect.size.x, sz = h.rect.size.z;
            float slope = Finite(bankSlope) ? Mathf.Max(0.05f, bankSlope) : 0.5f;
            float reach = Finite(bankReachMetres) ? Mathf.Max(0f, bankReachMetres) : 0f;

            for (int z = 0; z < sz; z++)
            {
                if (stop != null && (z & 15) == 0 && stop.StopRequested) return;
                float wz = oz + z * px.y;
                int row = z * sx;
                for (int x = 0; x < sx; x++)
                {
                    float wx = ox + x * px.x;
                    float ground = h.arr[row + x];
                    float best = ground;

                    for (int ci = 0; ci < courses.Count; ci++)
                    {
                        ManualRiverCourse c = courses[ci];
                        if (c == null || c.pointsXZ == null || c.pointsXZ.Length < 2) continue;
                        if (c.bedY == null || c.bedY.Length == 0 || c.depthM == null || c.depthM.Length == 0) continue;
                        if (wx < c.minX || wx > c.maxX || wz < c.minZ || wz > c.maxZ) continue;

                        // nearest segment
                        Vector2[] p = c.pointsXZ;
                        float bd = float.MaxValue, bt = 0f; int bi = -1;
                        for (int i = 0; i + 1 < p.Length; i++)
                        {
                            float t;
                            float d = DistanceToSegment(wx, wz, p[i].x, p[i].y, p[i + 1].x, p[i + 1].y, out t);
                            if (d < bd) { bd = d; bt = t; bi = i; }
                        }
                        if (bi < 0) continue;

                        int j = Mathf.Min(bi + 1, c.bedY.Length - 1);
                        int i0 = Mathf.Min(bi, c.bedY.Length - 1);
                        // UC-T03-03: interpolation and grading may exceed float range for otherwise finite authored values.
                        double bedWorld = c.bedY[i0] + ((double)c.bedY[j] - c.bedY[i0]) * bt;
                        double depthStart = c.depthM[Mathf.Min(bi, c.depthM.Length - 1)];
                        double depth = System.Math.Max(0.1d, depthStart +
                            (c.depthM[Mathf.Min(bi + 1, c.depthM.Length - 1)] - depthStart) * bt);
                        float half = Mathf.Lerp(ResolveHalfWidth(c, bi, fallbackHalfWidthMetres),
                            ResolveHalfWidth(c, bi + 1, fallbackHalfWidthMetres), bt);

                        double bedN = (bedWorld - oy) / worldH;
                        double depthN = depth / worldH;
                        double profile;

                        if (bd <= half)
                        {
                            float u = bd / half;
                            profile = bedN + depthN * (u * u);            // thalweg -> waterline at the bank
                        }
                        else
                        {
                            float outM = bd - half;
                            if (outM >= reach) continue;
                            double waterN = bedN + depthN;
                            double capN = waterN + ((double)outM * slope) / worldH;
                            float fade = 1f - SmoothStep01((outM / reach - 0.5f) * 2f);   // full strength inner half
                            double lowered = ground > capN ? ground - fade * (ground - capN) : ground;
                            profile = lowered;
                        }
                        if (profile < best) best = FiniteFloat(profile);
                    }
                    h.arr[row + x] = best;
                }
            }
        }

        private static float SmoothStep01(float t)
        {
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// The River-Mask value at one world point: 1 inside any course's channel half-width,
        /// feathering to 0 across <paramref name="feather"/> metres beyond it, 0 elsewhere. The
        /// max over every course and segment, so overlapping reaches union rather than cancel.
        ///
        /// Pure and pulled out of the loop on purpose - the same reason RiverFillNode.IsWet is:
        /// a compile cannot catch an inside/outside sign error, only a pinned test can. If the
        /// half-width comparison were inverted the mask would mark the dry banks and leave the
        /// channel clear - a river the scatter gate would plant trees straight down.
        /// </summary>
        internal static float MaskAt(float wx, float wz, List<ManualRiverCourse> courses, float feather, float fallbackHalfWidth = 3f)
        {
            // UC-T03-03: no prepared course or invalid query position means no water.
            if (courses == null || !Finite(wx) || !Finite(wz)) return 0f;
            float best = 0f;
            float f = Finite(feather) ? Mathf.Max(0.01f, feather) : 0.01f;

            for (int ci = 0; ci < courses.Count; ci++)
            {
                ManualRiverCourse c = courses[ci];
                if (c == null || c.pointsXZ == null || c.pointsXZ.Length < 2) continue;
                if (c.maxX > c.minX && (wx < c.minX || wx > c.maxX || wz < c.minZ || wz > c.maxZ)) continue;
                Vector2[] p = c.pointsXZ;

                for (int i = 0; i + 1 < p.Length; i++)
                {
                    float t;
                    float d = DistanceToSegment(wx, wz, p[i].x, p[i].y, p[i + 1].x, p[i + 1].y, out t);

                    // Channel half-width at the closest point along this segment.
                    float ha = ResolveHalfWidth(c, i, fallbackHalfWidth);
                    float hb = ResolveHalfWidth(c, i + 1, fallbackHalfWidth);
                    float half = ha + (hb - ha) * t;
                    if (half < 0f) half = 0f;

                    float val;
                    if (d <= half) val = 1f;                               // in the wetted channel
                    // Subtract before dividing: adding two finite large widths can overflow.
                    else if ((double)d - half <= f) val = InterpolationMath.SmoothStep((float)(1d - ((double)d - half) / f));
                    else val = 0f;                                          // beyond the feathered edge

                    if (val > best) best = val;
                    if (best >= 1f) return 1f;                             // cannot get wetter
                }
            }
            return best;
        }

        /// <summary>UC-T03-03: world-metre distance to a segment; double intermediates preserve finite extreme coordinates.</summary>
        /// <param name="px">Query X in finite world metres.</param>
        /// <param name="pz">Query Z in finite world metres.</param>
        /// <param name="ax">Start X in finite world metres.</param>
        /// <param name="az">Start Z in finite world metres.</param>
        /// <param name="bx">End X in finite world metres.</param>
        /// <param name="bz">End Z in finite world metres.</param>
        /// <param name="t">Closest-point parameter, clamped to 0..1; zero for a degenerate or invalid segment.</param>
        /// <returns>Distance capped at float.MaxValue; invalid coordinates return that same out-of-range distance.</returns>
        internal static float DistanceToSegment(float px, float pz, float ax, float az,
                                                float bx, float bz, out float t)
        {
            t = 0f;
            if (!Finite(px) || !Finite(pz) || !Finite(ax) || !Finite(az) || !Finite(bx) || !Finite(bz))
                return float.MaxValue;
            double dx = (double)bx - ax, dz = (double)bz - az;
            double len2 = dx * dx + dz * dz;
            double along = len2 < 1e-9d ? 0d : System.Math.Max(0d, System.Math.Min(1d,
                (((double)px - ax) * dx + ((double)pz - az) * dz) / len2));
            t = (float)along;
            double rx = px - (ax + along * dx), rz = pz - (az + along * dz);
            return (float)System.Math.Min(float.MaxValue, System.Math.Sqrt(rx * rx + rz * rz));
        }
    }
}
