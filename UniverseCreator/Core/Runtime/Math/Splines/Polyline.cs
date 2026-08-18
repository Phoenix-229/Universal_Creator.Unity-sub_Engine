// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    Polyline.cs
//  Purpose: An ordered run of points in absolute world space, open or closed.
//           Rivers, shorelines, ridge crests, and road centrelines are all this
//           shape, and every spline operation in the toolkit consumes or
//           produces one.
//
//  WORLD SPACE, NOT TILE SPACE
//  ---------------------------
//  Points are absolute world metres, matching the sampling convention the noise
//  creators use. A river does not stop at a chunk boundary, so a polyline in
//  tile-local coordinates would have to be re-based every time a neighbouring
//  chunk pulled it, and the two chunks would disagree about where the bank is.
//
//  Plain managed C#: a List<Vector3> and floats. No NativeArray, no job system,
//  so this is safe to build and traverse from a background Task.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// An ordered sequence of world-space points, optionally closed into a loop.
    /// </summary>
    [Serializable]
    public class Polyline
    {
        /// <summary>Points in order, absolute world metres.</summary>
        public List<Vector3> points = new List<Vector3>();

        /// <summary>
        /// True if the last point connects back to the first. A closed line has
        /// Count segments; an open one has Count - 1.
        /// </summary>
        public bool closed;

        /// <summary>
        /// Optional per-point scalar, carried alongside the geometry. Rivers use
        /// it for width or discharge, shorelines for beach depth. Either empty or
        /// exactly as long as <see cref="points"/>; operations that resample the
        /// geometry interpolate it in step, and ones that cannot keep it aligned
        /// drop it rather than leaving it stale.
        /// </summary>
        public List<float> weights = new List<float>();

        public Polyline() { }

        public Polyline(int capacity)
        {
            points = new List<Vector3>(capacity);
            weights = new List<float>(capacity);
        }

        public Polyline(IList<Vector3> source, bool closed = false)
        {
            if (source != null)
            {
                points = new List<Vector3>(source.Count);
                for (int i = 0; i < source.Count; i++) points.Add(source[i]);
            }
            this.closed = closed;
        }

        /// <summary>Deep copy. Spline ops never mutate their input.</summary>
        public Polyline(Polyline source)
        {
            if (source == null) return;
            points = new List<Vector3>(source.points);
            weights = new List<float>(source.weights);
            closed = source.closed;
        }

        public int Count { get { return points.Count; } }

        /// <summary>True once there are enough points to describe a segment.</summary>
        public bool IsValid { get { return points.Count >= 2; } }

        /// <summary>Number of segments, accounting for the closing one.</summary>
        public int SegmentCount
        {
            get
            {
                if (points.Count < 2) return 0;
                return closed ? points.Count : points.Count - 1;
            }
        }

        /// <summary>True when the weight channel is present and correctly sized.</summary>
        public bool HasWeights
        {
            get { return weights != null && weights.Count == points.Count && points.Count > 0; }
        }

        public void Add(Vector3 p)
        {
            points.Add(p);
        }

        public void Add(Vector3 p, float weight)
        {
            points.Add(p);
            weights.Add(weight);
        }

        public void Clear()
        {
            points.Clear();
            weights.Clear();
        }

        /// <summary>
        /// Wraps an index into range for closed lines and clamps it for open
        /// ones. Every traversal goes through this so the closed case never
        /// needs a special path at the call site.
        /// </summary>
        public int WrapIndex(int i)
        {
            int n = points.Count;
            if (n == 0) return 0;

            if (closed)
            {
                i %= n;
                if (i < 0) i += n;
                return i;
            }

            if (i < 0) return 0;
            if (i >= n) return n - 1;
            return i;
        }

        public Vector3 PointAt(int i)
        {
            if (points.Count == 0) return Vector3.zero;
            return points[WrapIndex(i)];
        }

        public float WeightAt(int i)
        {
            if (!HasWeights) return 1f;
            return weights[WrapIndex(i)];
        }

        /// <summary>Total length in world metres, including the closing segment.</summary>
        public float Length()
        {
            float total = 0f;
            int segs = SegmentCount;
            for (int i = 0; i < segs; i++)
                total += Vector3.Distance(PointAt(i), PointAt(i + 1));
            return total;
        }

        /// <summary>
        /// Cumulative distance at each point. Entry 0 is always 0, and for a
        /// closed line the array has one extra trailing entry holding the full
        /// perimeter, so a binary search over it never has to special-case the
        /// wrap.
        /// </summary>
        public float[] BuildArcTable()
        {
            int n = points.Count;
            if (n == 0) return new float[0];

            int entries = closed ? n + 1 : n;
            float[] arc = new float[entries];
            arc[0] = 0f;

            for (int i = 1; i < entries; i++)
                arc[i] = arc[i - 1] + Vector3.Distance(PointAt(i - 1), PointAt(i));

            return arc;
        }

        /// <summary>
        /// Drops the weight channel. Called by operations that change the point
        /// count without a defensible way to carry the scalar across, so a stale
        /// half-aligned channel can never reach a consumer.
        /// </summary>
        public void DiscardWeights()
        {
            if (weights != null) weights.Clear();
        }

        /// <summary>Axis-aligned bounds of the points. Empty line gives a zero bounds.</summary>
        public Bounds GetBounds()
        {
            if (points.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

            Bounds b = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) b.Encapsulate(points[i]);
            return b;
        }
    }
}
