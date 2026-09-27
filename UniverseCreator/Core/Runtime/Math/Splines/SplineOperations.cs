// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    SplineOperations.cs
//  Purpose: Stateless polyline operations - resample, smooth, offset, subdivide,
//           simplify, tangents, projection. These are what turn a hand-placed or
//           traced set of points into a usable river centreline or shoreline.
//
//  Every operation returns a NEW Polyline and never mutates its input. A traced
//  river feeds the carve node, the bank mask, and the scatter exclusion filter
//  at once; if smoothing wrote in place, whichever consumer ran second would
//  silently get different geometry from the first.
//
//  Managed C# on List<Vector3>. No Burst, no NativeArray, no Unity.Mathematics.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>How a smoothing pass moves points.</summary>
    public enum SmoothMethod
    {
        /// <summary>Moving average over a window. Cheap, shrinks sharp corners.</summary>
        MovingAverage = 0,
        /// <summary>Laplacian relaxation. Gentler, converges toward a straight line.</summary>
        Laplacian = 1,
        /// <summary>Chaikin corner cutting. Preserves overall shape while removing kinks.</summary>
        Chaikin = 2
    }

    /// <summary>
    /// Stateless polyline math shared by the river, shoreline, and road paths.
    /// </summary>
    public static class SplineOperations
    {
        // =====================================================================
        //  RESAMPLE
        // =====================================================================

        /// <summary>
        /// Rebuilds the line with points at exactly equal arc-length spacing.
        /// Downstream carving and scatter both assume uniform density - an
        /// unresampled traced line has points bunched at the corners, which is
        /// where a carve would then cut deepest for no physical reason.
        /// </summary>
        /// <param name="src">Source line. Not modified.</param>
        /// <param name="spacing">Target distance between output points, world metres.</param>
        /// <param name="keepEnds">
        /// When true the final point is placed exactly on the source endpoint even
        /// if that makes the last segment shorter than spacing. Rivers need this:
        /// the mouth must land on the coastline, not near it.
        /// </param>
        public static Polyline Resample(Polyline src, float spacing, bool keepEnds = true)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (spacing <= 1e-4f) return new Polyline(src);

            Polyline dst = new Polyline(Mathf.Max(4, (int)(src.Length() / spacing) + 2));
            dst.closed = src.closed;

            bool carryWeights = src.HasWeights;

            dst.points.Add(src.PointAt(0));
            if (carryWeights) dst.weights.Add(src.WeightAt(0));

            int segs = src.SegmentCount;
            float carried = 0f;   // distance already walked into the current segment

            for (int s = 0; s < segs; s++)
            {
                Vector3 a = src.PointAt(s);
                Vector3 b = src.PointAt(s + 1);
                float segLen = Vector3.Distance(a, b);
                if (segLen < 1e-6f) continue;

                float wa = carryWeights ? src.WeightAt(s) : 0f;
                float wb = carryWeights ? src.WeightAt(s + 1) : 0f;

                // Walk this segment, emitting a point every time the running
                // distance crosses another multiple of spacing.
                float travelled = spacing - carried;
                while (travelled <= segLen)
                {
                    float t = travelled / segLen;
                    dst.points.Add(Vector3.Lerp(a, b, t));
                    if (carryWeights) dst.weights.Add(Mathf.Lerp(wa, wb, t));
                    travelled += spacing;
                }

                carried = segLen - (travelled - spacing);
            }

            // The walk above lands the last emitted point up to one spacing short
            // of the true end. For an open line that visibly truncates the river.
            if (!src.closed && keepEnds)
            {
                Vector3 last = src.PointAt(src.Count - 1);
                if (dst.Count == 0 || Vector3.Distance(dst.points[dst.Count - 1], last) > 1e-4f)
                {
                    dst.points.Add(last);
                    if (carryWeights) dst.weights.Add(src.WeightAt(src.Count - 1));
                }
            }

            if (!carryWeights) dst.DiscardWeights();
            return dst;
        }

        /// <summary>
        /// Resamples with a spacing that TIGHTENS on bends and RELAXES on straights, so a
        /// meander keeps the points it needs to read as a smooth curve while a straight
        /// reach stays cheap. Walk the (already-smooth) source and keep a point whenever
        /// EITHER <paramref name="maxSpacing"/> of arc length OR <paramref name="maxTurnRadians"/>
        /// of accumulated heading change has built up since the last kept point, floored so
        /// no two kept points are closer than <paramref name="minSpacing"/>.
        /// </summary>
        /// <remarks>
        /// The uniform <see cref="Resample"/> decimates a smooth Catmull-Rom curve back to
        /// evenly spaced chords, which re-corners every tight bend - a river ribbon built on
        /// that reads as "snapped/cut" at the meander. This keeps the curve smooth THROUGH
        /// the bend without paying for points on the straights. Feed it the dense curve from
        /// <see cref="TessellateCatmullRom"/>, not the raw nodes, so the kept points lie on
        /// the true curve rather than on the control polygon.
        /// </remarks>
        public static Polyline ResampleAdaptive(Polyline src, float maxSpacing,
                                                float minSpacing, float maxTurnRadians)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (src.Count < 3 || maxSpacing <= 1e-4f) return Resample(src, maxSpacing, true);

            minSpacing = Mathf.Clamp(minSpacing, 0.05f, maxSpacing);
            maxTurnRadians = Mathf.Max(1e-3f, maxTurnRadians);

            bool carryWeights = src.HasWeights;
            Polyline dst = new Polyline(src.Count);
            dst.closed = src.closed;

            dst.points.Add(src.PointAt(0));
            if (carryWeights) dst.weights.Add(src.WeightAt(0));

            float accDist = 0f;   // arc length since the last kept point
            float accTurn = 0f;   // total heading change since the last kept point

            int last = src.closed ? src.Count : src.Count - 1;
            for (int i = 1; i < last; i++)
            {
                Vector3 a = src.PointAt(i - 1);
                Vector3 b = src.PointAt(i);
                accDist += Vector3.Distance(a, b);

                // Heading change AT this point: the angle between the segment arriving and
                // the one leaving. Summed, it is how far the course has turned since the
                // last kept point - the quantity a facet budget should be measured in.
                Vector3 into = b - a; into.y = 0f;
                Vector3 outOf = src.PointAt(i + 1) - b; outOf.y = 0f;
                if (into.sqrMagnitude > 1e-8f && outOf.sqrMagnitude > 1e-8f)
                    accTurn += Mathf.Acos(Mathf.Clamp(
                        Vector3.Dot(into.normalized, outOf.normalized), -1f, 1f));

                bool spacingHit = accDist >= maxSpacing;
                bool turnHit = accTurn >= maxTurnRadians && accDist >= minSpacing;
                if (spacingHit || turnHit)
                {
                    dst.points.Add(b);
                    if (carryWeights) dst.weights.Add(src.WeightAt(i));
                    accDist = 0f;
                    accTurn = 0f;
                }
            }

            // The mouth must land exactly where it was drawn, not one spacing short of it.
            if (!src.closed)
            {
                Vector3 end = src.PointAt(src.Count - 1);
                if (Vector3.Distance(dst.points[dst.Count - 1], end) > 1e-4f)
                {
                    dst.points.Add(end);
                    if (carryWeights) dst.weights.Add(src.WeightAt(src.Count - 1));
                }
            }

            if (dst.Count < 2) return Resample(src, maxSpacing, true);
            if (!carryWeights) dst.DiscardWeights();
            return dst;
        }

        /// <summary>
        /// Resamples to a fixed point count rather than a fixed spacing. Used
        /// where two lines must correspond point-for-point, such as the two banks
        /// of one river.
        /// </summary>
        public static Polyline ResampleToCount(Polyline src, int count)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (count < 2) count = 2;

            float total = src.Length();
            if (total < 1e-6f) return new Polyline(src);

            int divisions = src.closed ? count : count - 1;
            return Resample(src, total / divisions, !src.closed);
        }

        // =====================================================================
        //  SMOOTH
        // =====================================================================

        /// <summary>
        /// Irons out zig-zags. A traced flow path steps pixel to pixel and so
        /// arrives as a staircase; carving that directly leaves visible terracing
        /// along the bank.
        /// </summary>
        /// <param name="iterations">Passes. Each one widens the effective kernel.</param>
        /// <param name="strength">0 leaves the line alone, 1 applies the full move.</param>
        /// <param name="window">Half-width of the average, in points. Ignored by Chaikin.</param>
        public static Polyline Smooth(Polyline src, SmoothMethod method = SmoothMethod.MovingAverage,
                                      int iterations = 1, float strength = 1f, int window = 1)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (iterations < 1) return new Polyline(src);

            if (method == SmoothMethod.Chaikin)
                return Chaikin(src, iterations);

            Polyline current = new Polyline(src);
            float t = Mathf.Clamp01(strength);
            int w = Mathf.Max(1, window);

            for (int pass = 0; pass < iterations; pass++)
            {
                Polyline next = new Polyline(current);

                // Endpoints of an open line are anchors: a river mouth that
                // drifts inland each smoothing pass stops meeting the sea.
                int first = current.closed ? 0 : 1;
                int last = current.closed ? current.Count - 1 : current.Count - 2;

                for (int i = first; i <= last; i++)
                {
                    Vector3 target;

                    if (method == SmoothMethod.Laplacian)
                    {
                        target = (current.PointAt(i - 1) + current.PointAt(i + 1)) * 0.5f;
                    }
                    else
                    {
                        Vector3 sum = Vector3.zero;
                        int n = 0;
                        for (int k = -w; k <= w; k++)
                        {
                            // An open line has no neighbour past its end; skipping
                            // rather than clamping stops the endpoint value being
                            // counted twice and dragging the average toward it.
                            int idx = i + k;
                            if (!current.closed && (idx < 0 || idx >= current.Count)) continue;
                            sum += current.PointAt(idx);
                            n++;
                        }
                        target = n > 0 ? sum / n : current.PointAt(i);
                    }

                    next.points[i] = Vector3.Lerp(current.PointAt(i), target, t);
                }

                current = next;
            }

            return current;
        }

        /// <summary>
        /// Chaikin corner cutting. Replaces each point with two at 1/4 and 3/4 of
        /// its neighbouring segments, doubling the point count per pass and
        /// converging on a quadratic B-spline.
        /// </summary>
        public static Polyline Chaikin(Polyline src, int iterations = 1)
        {
            if (src == null || !src.IsValid) return new Polyline(src);

            Polyline current = new Polyline(src);

            for (int pass = 0; pass < Mathf.Max(1, iterations); pass++)
            {
                // Doubling four times over already multiplies the count by 16;
                // past that the extra points are below any sane sampling
                // resolution and only cost memory.
                if (current.Count > 65536) break;

                Polyline next = new Polyline(current.Count * 2);
                next.closed = current.closed;

                if (!current.closed) next.points.Add(current.PointAt(0));

                int segs = current.SegmentCount;
                for (int i = 0; i < segs; i++)
                {
                    Vector3 a = current.PointAt(i);
                    Vector3 b = current.PointAt(i + 1);
                    next.points.Add(Vector3.Lerp(a, b, 0.25f));
                    next.points.Add(Vector3.Lerp(a, b, 0.75f));
                }

                if (!current.closed) next.points.Add(current.PointAt(current.Count - 1));

                // Point count changed and the new points sit between old ones, so
                // the weight channel no longer corresponds. Dropped, not guessed.
                next.DiscardWeights();
                current = next;
            }

            return current;
        }

        // =====================================================================
        //  OFFSET
        // =====================================================================

        /// <summary>
        /// Offsets the line sideways in the XZ plane by a constant distance.
        /// Two calls with +w and -w give the banks of a river of width 2w.
        /// </summary>
        /// <param name="distance">
        /// Positive offsets to the left of travel direction, negative to the right.
        /// </param>
        public static Polyline Offset(Polyline src, float distance)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (Mathf.Abs(distance) < 1e-5f) return new Polyline(src);

            Polyline dst = new Polyline(src);

            for (int i = 0; i < src.Count; i++)
            {
                Vector3 normal = NormalAt(src, i);
                dst.points[i] = src.PointAt(i) + normal * distance;
            }

            return dst;
        }

        /// <summary>
        /// Offsets by a per-point distance taken from the weight channel scaled
        /// by <paramref name="scale"/>. A river widens toward its mouth, so a
        /// constant offset gives banks that are wrong at both ends.
        /// </summary>
        public static Polyline OffsetWeighted(Polyline src, float scale)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (!src.HasWeights) return Offset(src, scale);

            Polyline dst = new Polyline(src);

            for (int i = 0; i < src.Count; i++)
            {
                Vector3 normal = NormalAt(src, i);
                dst.points[i] = src.PointAt(i) + normal * (src.WeightAt(i) * scale);
            }

            return dst;
        }

        /// <summary>
        /// Left-hand normal in the XZ plane at a point, from the averaged
        /// direction of the segments either side of it. Averaging is what stops
        /// the offset line pinching at corners.
        /// </summary>
        public static Vector3 NormalAt(Polyline line, int index)
        {
            Vector3 tangent = TangentAt(line, index);
            // Rotate 90 degrees about Y. Y stays 0 so the offset is horizontal
            // and the bank does not climb out of the valley it belongs to.
            return new Vector3(-tangent.z, 0f, tangent.x);
        }

        /// <summary>Normalised direction of travel at a point.</summary>
        public static Vector3 TangentAt(Polyline line, int index)
        {
            if (line == null || line.Count < 2) return Vector3.forward;

            Vector3 prev = line.PointAt(index - 1);
            Vector3 next = line.PointAt(index + 1);
            Vector3 here = line.PointAt(index);

            Vector3 dir;

            if (!line.closed && index == 0)
                dir = next - here;
            else if (!line.closed && index >= line.Count - 1)
                dir = here - prev;
            else
                dir = next - prev;

            dir.y = 0f;

            float len = dir.magnitude;
            if (len < 1e-6f) return Vector3.forward;
            return dir / len;
        }

        /// <summary>
        /// Per-point tangents for the whole line in one pass. Cheaper than
        /// calling <see cref="TangentAt"/> in a loop when a consumer needs all
        /// of them, which mesh extrusion always does.
        /// </summary>
        public static Vector3[] ComputeTangents(Polyline line)
        {
            if (line == null || line.Count == 0) return new Vector3[0];

            Vector3[] tangents = new Vector3[line.Count];
            for (int i = 0; i < line.Count; i++) tangents[i] = TangentAt(line, i);
            return tangents;
        }

        // =====================================================================
        //  SUBDIVIDE AND SIMPLIFY
        // =====================================================================

        /// <summary>
        /// Inserts points along each segment. Unlike Resample this keeps every
        /// original point exactly where it was, which matters when the original
        /// points are meaningful (a traced flow path's cell centres) rather than
        /// arbitrary samples.
        /// </summary>
        /// <param name="cuts">New points per segment. 1 doubles the segment count.</param>
        public static Polyline Subdivide(Polyline src, int cuts)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (cuts < 1) return new Polyline(src);

            Polyline dst = new Polyline(src.Count * (cuts + 1) + 1);
            dst.closed = src.closed;
            bool carryWeights = src.HasWeights;

            int segs = src.SegmentCount;
            for (int i = 0; i < segs; i++)
            {
                Vector3 a = src.PointAt(i);
                Vector3 b = src.PointAt(i + 1);
                float wa = carryWeights ? src.WeightAt(i) : 0f;
                float wb = carryWeights ? src.WeightAt(i + 1) : 0f;

                dst.points.Add(a);
                if (carryWeights) dst.weights.Add(wa);

                for (int c = 1; c <= cuts; c++)
                {
                    float t = (float)c / (cuts + 1);
                    dst.points.Add(Vector3.Lerp(a, b, t));
                    if (carryWeights) dst.weights.Add(Mathf.Lerp(wa, wb, t));
                }
            }

            if (!src.closed)
            {
                dst.points.Add(src.PointAt(src.Count - 1));
                if (carryWeights) dst.weights.Add(src.WeightAt(src.Count - 1));
            }

            if (!carryWeights) dst.DiscardWeights();
            return dst;
        }

        /// <summary>
        /// Subdivides with Catmull-Rom interpolation rather than linearly, so the
        /// inserted points bulge into a curve through the originals instead of
        /// sitting on the chords. This is the one to use before extruding a river
        /// mesh; the linear form leaves visible facets on bends.
        /// </summary>
        public static Polyline SubdivideSmooth(Polyline src, int cuts)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (cuts < 1) return new Polyline(src);

            Polyline dst = new Polyline(src.Count * (cuts + 1) + 1);
            dst.closed = src.closed;

            int segs = src.SegmentCount;
            for (int i = 0; i < segs; i++)
            {
                Vector3 p0 = src.PointAt(i - 1);
                Vector3 p1 = src.PointAt(i);
                Vector3 p2 = src.PointAt(i + 1);
                Vector3 p3 = src.PointAt(i + 2);

                dst.points.Add(p1);

                for (int c = 1; c <= cuts; c++)
                {
                    float t = (float)c / (cuts + 1);
                    dst.points.Add(new Vector3(
                        InterpolationMath.CatmullRom(p0.x, p1.x, p2.x, p3.x, t),
                        InterpolationMath.CatmullRom(p0.y, p1.y, p2.y, p3.y, t),
                        InterpolationMath.CatmullRom(p0.z, p1.z, p2.z, p3.z, t)));
                }
            }

            if (!src.closed) dst.points.Add(src.PointAt(src.Count - 1));

            dst.DiscardWeights();
            return dst;
        }

        // =====================================================================
        //  CENTRIPETAL CATMULL-ROM
        //
        //  SubdivideSmooth above is the UNIFORM form: every span gets the same
        //  parameter length whatever its actual length. That is fine for a traced
        //  line whose points are already evenly spaced, and wrong for a line the
        //  user placed by hand, where one span is 8 metres and the next is 200.
        //  Uniform parameterisation on spans like that overshoots the short one -
        //  the curve loops out past the node and back - and on a tight corner it
        //  can form a cusp or cross itself. A river that crosses itself digs a
        //  trench through its own bank.
        //
        //  Centripetal parameterisation (knot spacing = chord length to the power
        //  alpha, alpha = 0.5) is the published fix: Yuksel, Schaefer and Keyser,
        //  "Parameterization and Applications of Catmull-Rom Curves", proves it
        //  cannot cusp and cannot self-intersect for any control polygon. It still
        //  passes exactly through every point, which is the whole reason to use a
        //  Catmull-Rom here rather than corner-cutting: the user placed those
        //  nodes and the river has to go through them, not near them.
        // =====================================================================

        /// <summary>
        /// Tessellates the line as a centripetal Catmull-Rom curve. The result
        /// passes through every source point and bends through it, with each span
        /// sampled according to how long it is - so a long straight reach costs
        /// almost nothing and a tight bend gets the points it needs to read as a
        /// curve rather than a corner.
        /// </summary>
        /// <param name="src">Source line. Not modified.</param>
        /// <param name="maxSegmentMetres">
        /// Longest chord to leave between output points. Smaller is smoother and
        /// slower; there is no point going below the spacing of whatever resample
        /// follows.
        /// </param>
        /// <param name="alpha">
        /// Knot exponent. 0.5 is centripetal and is the safe default; 0 is uniform
        /// (what SubdivideSmooth does) and 1 is chordal, which is smoother on
        /// even spacing but overshoots on uneven.
        /// </param>
        public static Polyline TessellateCatmullRom(Polyline src, float maxSegmentMetres,
                                                    float alpha = 0.5f)
        {
            if (src == null || !src.IsValid) return new Polyline(src);

            // Two points have no interior node to bend through, so the curve
            // through them IS the chord. Returning early also keeps the phantom
            // endpoint construction below from being the only thing shaping it.
            if (src.Count < 3) return new Polyline(src);

            float step = Mathf.Max(0.05f, maxSegmentMetres);
            bool carryWeights = src.HasWeights;

            Polyline dst = new Polyline(src.Count * 8);
            dst.closed = src.closed;

            int segs = src.SegmentCount;

            for (int i = 0; i < segs; i++)
            {
                Vector3 p1 = src.PointAt(i);
                Vector3 p2 = src.PointAt(i + 1);

                // The two spans either side of this one set its tangents. At an
                // open end there is no neighbour, so one is mirrored through the
                // endpoint - NOT clamped to the endpoint itself, which would make
                // a zero-length knot span and divide by zero below.
                Vector3 p0 = (src.closed || i > 0) ? src.PointAt(i - 1) : p1 + (p1 - p2);
                Vector3 p3 = (src.closed || i + 2 <= src.Count - 1)
                    ? src.PointAt(i + 2)
                    : p2 + (p2 - p1);

                float t0 = 0f;
                float t1 = t0 + KnotSpan(p0, p1, alpha);
                float t2 = t1 + KnotSpan(p1, p2, alpha);
                float t3 = t2 + KnotSpan(p2, p3, alpha);

                float w1 = carryWeights ? src.WeightAt(i) : 0f;
                float w2 = carryWeights ? src.WeightAt(i + 1) : 0f;

                dst.points.Add(p1);
                if (carryWeights) dst.weights.Add(w1);

                float chord = Vector3.Distance(p1, p2);
                int samples = Mathf.Clamp(Mathf.CeilToInt(chord / step), 1, 64);

                for (int c = 1; c < samples; c++)
                {
                    float u = (float)c / samples;
                    float t = Mathf.Lerp(t1, t2, u);

                    dst.points.Add(BarryGoldman(p0, p1, p2, p3, t0, t1, t2, t3, t));

                    // The weight rides on the straight parameter, not the curve:
                    // width is a per-node value being blended between two nodes,
                    // and there is nothing to curve about it.
                    if (carryWeights) dst.weights.Add(Mathf.Lerp(w1, w2, u));
                }
            }

            if (!src.closed)
            {
                dst.points.Add(src.PointAt(src.Count - 1));
                if (carryWeights) dst.weights.Add(src.WeightAt(src.Count - 1));
            }

            if (!carryWeights) dst.DiscardWeights();
            return dst;
        }

        // Knot spacing for one span. Floored rather than allowed to reach zero so
        // that two coincident control points - which a user placing nodes by
        // clicking will produce sooner or later - cannot divide by zero.
        private static float KnotSpan(Vector3 a, Vector3 b, float alpha)
        {
            float d = Vector3.Distance(a, b);
            if (d < 1e-5f) return 1e-4f;
            if (alpha <= 0f) return 1f;
            if (alpha >= 1f) return d;
            return Mathf.Pow(d, alpha);
        }

        // Barry-Goldman pyramidal evaluation. The plain Hermite form assumes a
        // uniform knot vector; this one takes the knots as arguments, which is
        // what makes the centripetal spacing above actually change the shape
        // rather than just relabel it.
        private static Vector3 BarryGoldman(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                            float t0, float t1, float t2, float t3, float t)
        {
            Vector3 a1 = Blend(p0, p1, t0, t1, t);
            Vector3 a2 = Blend(p1, p2, t1, t2, t);
            Vector3 a3 = Blend(p2, p3, t2, t3, t);

            Vector3 b1 = Blend(a1, a2, t0, t2, t);
            Vector3 b2 = Blend(a2, a3, t1, t3, t);

            return Blend(b1, b2, t1, t2, t);
        }

        private static Vector3 Blend(Vector3 a, Vector3 b, float ta, float tb, float t)
        {
            float span = tb - ta;
            if (span < 1e-6f) return b;
            float u = (t - ta) / span;
            return a * (1f - u) + b * u;
        }

        /// <summary>
        /// Ramer-Douglas-Peucker simplification. Drops points that lie within
        /// <paramref name="tolerance"/> of the chord they sit on. A flow trace
        /// arrives with one point per heightmap pixel, which is far more than the
        /// geometry needs and makes every downstream op proportionally slower.
        /// </summary>
        public static Polyline Simplify(Polyline src, float tolerance)
        {
            if (src == null || src.Count < 3) return new Polyline(src);
            if (tolerance <= 0f) return new Polyline(src);

            bool[] keep = new bool[src.Count];
            keep[0] = true;
            keep[src.Count - 1] = true;

            // Explicit stack rather than recursion: a pixel-dense trace can be
            // tens of thousands of points, and the recursive form overflows on
            // the near-collinear worst case.
            Stack<int> stack = new Stack<int>();
            stack.Push(0);
            stack.Push(src.Count - 1);

            while (stack.Count >= 2)
            {
                int last = stack.Pop();
                int first = stack.Pop();
                if (last <= first + 1) continue;

                float maxDist = 0f;
                int maxIndex = first;

                Vector3 a = src.points[first];
                Vector3 b = src.points[last];

                for (int i = first + 1; i < last; i++)
                {
                    float d = PerpendicularDistance(src.points[i], a, b);
                    if (d > maxDist)
                    {
                        maxDist = d;
                        maxIndex = i;
                    }
                }

                if (maxDist > tolerance)
                {
                    keep[maxIndex] = true;
                    stack.Push(first);
                    stack.Push(maxIndex);
                    stack.Push(maxIndex);
                    stack.Push(last);
                }
            }

            Polyline dst = new Polyline();
            dst.closed = src.closed;
            bool carryWeights = src.HasWeights;

            for (int i = 0; i < src.Count; i++)
            {
                if (!keep[i]) continue;
                dst.points.Add(src.points[i]);
                if (carryWeights) dst.weights.Add(src.WeightAt(i));
            }

            if (!carryWeights) dst.DiscardWeights();
            return dst;
        }

        /// <summary>
        /// Merges points closer together than <paramref name="threshold"/>.
        /// Traced lines routinely emit duplicate points where the flow direction
        /// reverses, and a zero-length segment makes every tangent at that point
        /// undefined.
        /// </summary>
        public static Polyline FuseClose(Polyline src, float threshold)
        {
            if (src == null || src.Count < 2) return new Polyline(src);
            if (threshold <= 0f) return new Polyline(src);

            float sqr = threshold * threshold;
            Polyline dst = new Polyline(src.Count);
            dst.closed = src.closed;
            bool carryWeights = src.HasWeights;

            dst.points.Add(src.points[0]);
            if (carryWeights) dst.weights.Add(src.WeightAt(0));

            for (int i = 1; i < src.Count; i++)
            {
                Vector3 prev = dst.points[dst.Count - 1];
                if ((src.points[i] - prev).sqrMagnitude < sqr) continue;

                dst.points.Add(src.points[i]);
                if (carryWeights) dst.weights.Add(src.WeightAt(i));
            }

            if (!carryWeights) dst.DiscardWeights();
            return dst;
        }

        // =====================================================================
        //  QUERY
        // =====================================================================

        /// <summary>
        /// Squared distance from a world XZ position to the nearest point on the
        /// line, ignoring Y. Squared because the carve and mask consumers compare
        /// against a radius and never need the root.
        /// </summary>
        public static float SqrDistanceToXZ(Polyline line, float x, float z)
        {
            if (line == null || line.Count == 0) return float.MaxValue;

            if (line.Count == 1)
            {
                float ddx = line.points[0].x - x;
                float ddz = line.points[0].z - z;
                return ddx * ddx + ddz * ddz;
            }

            float best = float.MaxValue;
            int segs = line.SegmentCount;

            for (int i = 0; i < segs; i++)
            {
                Vector3 a = line.PointAt(i);
                Vector3 b = line.PointAt(i + 1);

                float abx = b.x - a.x;
                float abz = b.z - a.z;
                float apx = x - a.x;
                float apz = z - a.z;

                float lenSqr = abx * abx + abz * abz;
                float t = lenSqr > 1e-12f ? (apx * abx + apz * abz) / lenSqr : 0f;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;

                float cx = a.x + abx * t - x;
                float cz = a.z + abz * t - z;
                float d = cx * cx + cz * cz;

                if (d < best) best = d;
            }

            return best;
        }

        /// <summary>
        /// Position at a normalised parameter along the line, arc-length
        /// accurate. Parameterising by point index instead would run fast
        /// through dense stretches and slow through sparse ones.
        /// </summary>
        public static Vector3 Evaluate(Polyline line, float t)
        {
            if (line == null || line.Count == 0) return Vector3.zero;
            if (line.Count == 1) return line.points[0];

            float[] arc = line.BuildArcTable();
            float total = arc[arc.Length - 1];
            if (total < 1e-6f) return line.points[0];

            float target = Mathf.Clamp01(t) * total;

            for (int i = 1; i < arc.Length; i++)
            {
                if (arc[i] < target) continue;

                float segLen = arc[i] - arc[i - 1];
                float local = segLen > 1e-6f ? (target - arc[i - 1]) / segLen : 0f;
                return Vector3.Lerp(line.PointAt(i - 1), line.PointAt(i), local);
            }

            return line.PointAt(line.Count - 1);
        }

        /// <summary>
        /// True when a world XZ position is inside a closed line. Even-odd ray
        /// cast along +X. Used by the lake and island masks; an open line always
        /// answers false rather than guessing at a closure.
        /// </summary>
        public static bool ContainsXZ(Polyline line, float x, float z)
        {
            if (line == null || !line.closed || line.Count < 3) return false;

            bool inside = false;
            int n = line.Count;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float zi = line.points[i].z;
                float zj = line.points[j].z;

                if ((zi > z) == (zj > z)) continue;

                float xi = line.points[i].x;
                float xj = line.points[j].x;
                float crossX = (xj - xi) * (z - zi) / (zj - zi) + xi;

                if (x < crossX) inside = !inside;
            }

            return inside;
        }

        /// <summary>
        /// Lifts every point to the surface described by a heightfield, plus an
        /// optional offset. A river centreline traced in 2-D has no Y until this
        /// runs.
        /// </summary>
        /// <param name="heights">Field to sample. Its worldPos/worldSize give the mapping.</param>
        /// <param name="worldHeight">World metres represented by a value of 1.</param>
        /// <param name="yOffset">Added after sampling, world metres.</param>
        public static Polyline ProjectToHeightfield(Polyline src, Matrix heights,
                                                    float worldHeight, float yOffset = 0f)
        {
            if (src == null || !src.IsValid) return new Polyline(src);
            if (heights == null || heights.arr == null || heights.arr.Length == 0)
                return new Polyline(src);

            Polyline dst = new Polyline(src);

            float mppX = heights.PixelSize.x;
            float mppZ = heights.PixelSize.y;
            if (mppX < 1e-9f || mppZ < 1e-9f) return dst;

            for (int i = 0; i < dst.Count; i++)
            {
                Vector3 p = dst.points[i];

                // Absolute world -> pixel coordinate in this field's frame. Same
                // convention the noise creators sample by, so a point on a chunk
                // seam resolves identically from either side.
                float px = (p.x - heights.worldPos.x) / mppX;
                float pz = (p.z - heights.worldPos.z) / mppZ;

                float h = heights.GetInterpolated(px, pz);
                p.y = h * worldHeight + yOffset;
                dst.points[i] = p;
            }

            return dst;
        }

        // =====================================================================
        //  INTERNAL
        // =====================================================================

        /// <summary>Perpendicular XZ distance from p to the infinite line ab.</summary>
        private static float PerpendicularDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            float abx = b.x - a.x;
            float abz = b.z - a.z;
            float lenSqr = abx * abx + abz * abz;

            if (lenSqr < 1e-12f)
            {
                float dx0 = p.x - a.x;
                float dz0 = p.z - a.z;
                return Mathf.Sqrt(dx0 * dx0 + dz0 * dz0);
            }

            float cross = abx * (a.z - p.z) - abz * (a.x - p.x);
            return Mathf.Abs(cross) / Mathf.Sqrt(lenSqr);
        }
    }
}
