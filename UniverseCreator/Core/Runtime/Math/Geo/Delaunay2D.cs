// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    Delaunay2D.cs   (GEO-1)
//  Purpose: A 2D Delaunay triangulation of a point cloud, exposing the triangle
//           list AND the point-adjacency (neighbour) graph + unique edge set.
//
//  WHY THIS EXISTS
//  ---------------
//  Our math layer has Voronoi only as a RASTER field (TectonicPlatesNode,
//  Noise3D) - a value per pixel, with no notion of which cells are neighbours.
//  A whole family of organic structure needs the DUAL of that: the adjacency
//  graph over a set of SITES (grove centres, region seeds, settlement points).
//  Delaunay gives exactly that - triangles whose edges are the "who is next to
//  whom" relation - and it is the input GEO-2 (Prim MST) walks to lay trails,
//  tributaries and hedgerows along the cheapest connected linework.
//
//  ALGORITHM (and why this one)
//  ----------------------------
//  Bowyer-Watson incremental insertion, with an orientation-independent
//  circumcircle test (circumcentre + radius, no winding assumption). PCGEx wraps
//  the header-only `delaunator` (S-hull) for raw O(n log n) speed; we deliberately
//  take Bowyer-Watson instead because it is short, auditable, and its correctness
//  is PROVABLE by the empty-circumcircle property the self-test checks directly.
//  It is O(n^2) worst case, which is the right trade here: this runs ONCE per bake
//  on a MODEST set of sites (groves/regions = hundreds, not pixels), off the main
//  thread. It is never a per-pixel operation. For very large N a delaunator port
//  can replace the core later behind this same result type.
//
//  Deterministic (pure function of the input points - no RNG, no clock).
//  Managed C# over List<>/arrays. No Unity API beyond Vector2; safe on a worker.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Geo
{
    /// <summary>
    /// Result of a 2D Delaunay triangulation: the triangles (3 point indices each),
    /// the unique undirected edge set, and a per-point neighbour-index list. All
    /// indices refer back to the input point list.
    /// </summary>
    public sealed class DelaunayResult
    {
        /// <summary>Triangle corners, 3 consecutive input-point indices per triangle.</summary>
        public readonly List<int> triangles = new List<int>();

        /// <summary>Unique undirected edges, as parallel endpoint arrays with edgeA[i] &lt; edgeB[i].</summary>
        public readonly List<int> edgeA = new List<int>();
        public readonly List<int> edgeB = new List<int>();

        /// <summary>For each input point, the indices of the points it shares an edge with.</summary>
        public List<int>[] neighbours;

        /// <summary>Number of input points this result was built from.</summary>
        public int pointCount;

        /// <summary>Triangle count (triangles.Count / 3).</summary>
        public int TriangleCount { get { return triangles.Count / 3; } }

        /// <summary>Unique undirected edge count.</summary>
        public int EdgeCount { get { return edgeA.Count; } }
    }

    /// <summary>
    /// Builds a 2D Delaunay triangulation and its neighbour graph. See the file
    /// header for the algorithm choice and the complexity note.
    /// </summary>
    public static class Delaunay2D
    {
        /// <summary>
        /// Triangulates <paramref name="points"/> and returns the triangles, the unique edge
        /// set and the per-point neighbour graph. Fewer than 3 points yields no triangles
        /// (two points yield the single connecting edge, so GEO-2 still has something to walk).
        /// </summary>
        public static DelaunayResult Triangulate(IList<Vector2> points)
        {
            DelaunayResult r = new DelaunayResult();
            int n = points != null ? points.Count : 0;
            r.pointCount = n;
            r.neighbours = new List<int>[n < 0 ? 0 : n];
            for (int i = 0; i < n; i++) r.neighbours[i] = new List<int>();

            if (n < 2) return r;
            if (n == 2) { AddEdge(r, 0, 1); return r; }

            // Working vertices = the input points plus three super-triangle corners (n..n+2).
            Vector2[] v = new Vector2[n + 3];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = points[i]; v[i] = p;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            float dmax = Mathf.Max(maxX - minX, maxY - minY);
            if (dmax <= 1e-9f) dmax = 1f;
            float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
            v[n]     = new Vector2(cx - 20f * dmax, cy - dmax);
            v[n + 1] = new Vector2(cx,              cy + 20f * dmax);
            v[n + 2] = new Vector2(cx + 20f * dmax, cy - dmax);

            // Triangles as a flat list of vertex indices into v. Start with the super-triangle.
            List<int> tri = new List<int> { n, n + 1, n + 2 };
            List<int> badEdges = new List<int>(64);
            // PLACEHOLDER-GEO1B
            for (int i = 0; i < n; i++)
            {
                Vector2 p = v[i];
                badEdges.Clear();

                // Remove every triangle whose circumcircle contains p, banking its three edges.
                for (int t = tri.Count - 3; t >= 0; t -= 3)
                {
                    int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                    if (InCircumcircle(v[a], v[b], v[c], p))
                    {
                        badEdges.Add(a); badEdges.Add(b);
                        badEdges.Add(b); badEdges.Add(c);
                        badEdges.Add(c); badEdges.Add(a);
                        tri.RemoveRange(t, 3);
                    }
                }

                // Hole boundary = edges that appear exactly once among the removed triangles;
                // an edge shared by two removed triangles is interior and is dropped (both copies).
                for (int e = 0; e < badEdges.Count; e += 2)
                {
                    int a = badEdges[e]; if (a < 0) continue;
                    int b = badEdges[e + 1];
                    bool shared = false;
                    for (int f = e + 2; f < badEdges.Count; f += 2)
                    {
                        int c = badEdges[f]; if (c < 0) continue;
                        int d = badEdges[f + 1];
                        if ((a == c && b == d) || (a == d && b == c))
                        {
                            badEdges[f] = -1; badEdges[f + 1] = -1;
                            shared = true;
                        }
                    }
                    if (shared) { badEdges[e] = -1; badEdges[e + 1] = -1; }
                }

                // Retriangulate the hole: each surviving boundary edge joined to p (vertex i).
                for (int e = 0; e < badEdges.Count; e += 2)
                {
                    int a = badEdges[e]; if (a < 0) continue;
                    tri.Add(a); tri.Add(badEdges[e + 1]); tri.Add(i);
                }
            }

            // Emit the triangles that do NOT touch a super vertex, plus their edges/neighbours.
            HashSet<long> seenEdge = new HashSet<long>();
            for (int t = 0; t < tri.Count; t += 3)
            {
                int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                if (a >= n || b >= n || c >= n) continue;
                r.triangles.Add(a); r.triangles.Add(b); r.triangles.Add(c);
                TryAddEdge(r, seenEdge, a, b);
                TryAddEdge(r, seenEdge, b, c);
                TryAddEdge(r, seenEdge, c, a);
            }
            return r;
        }

        // PLACEHOLDER-GEO1C
        private static void TryAddEdge(DelaunayResult r, HashSet<long> seen, int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;
            if (!seen.Add(key)) return;
            AddEdge(r, lo, hi);
        }

        private static void AddEdge(DelaunayResult r, int a, int b)
        {
            r.edgeA.Add(a); r.edgeB.Add(b);
            if (r.neighbours != null)
            {
                if (a < r.neighbours.Length) r.neighbours[a].Add(b);
                if (b < r.neighbours.Length) r.neighbours[b].Add(a);
            }
        }

        /// <summary>
        /// True when <paramref name="p"/> lies strictly inside the circumcircle of triangle
        /// (a,b,c). Orientation-independent: it builds the circumcentre and compares squared
        /// distances, so it does not care whether the triangle is wound CW or CCW. A collinear
        /// triangle has no circumcircle and returns false. Computed in double for stability.
        /// </summary>
        private static bool InCircumcircle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            double ax = a.x, ay = a.y, bx = b.x, by = b.y, cx = c.x, cy = c.y;
            double d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (d > -1e-12 && d < 1e-12) return false;        // collinear: no circumcircle

            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;

            double rr = (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy);
            double dp = (p.x - ux) * (p.x - ux) + (p.y - uy) * (p.y - uy);
            return dp < rr * (1.0 - 1e-9);                    // strictly inside, with a hair of slack
        }
    }
}
