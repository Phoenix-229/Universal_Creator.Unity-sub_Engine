// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    PrimMST.cs   (GEO-2)
//  Purpose: Minimum spanning tree/forest over the GEO-1 Delaunay neighbour graph,
//           by Prim's algorithm with a binary min-heap.
//
//  WHY THIS EXISTS
//  ---------------
//  Given a set of sites and "who is next to whom" (GEO-1), the MST is the cheapest
//  connected linework joining them all: the natural skeleton for trails between
//  groves, tributary spurs off a trunk, hedgerows along field boundaries, a fence
//  or path network. It links by least TOTAL length, so the result reads organic
//  rather than radial or gridded.
//
//  The edge weight is pluggable (a delegate), defaulting to Euclidean distance.
//  Passing a slope-aware cost (e.g. horizontal distance biased by the climb between
//  the two sites) makes the tree prefer gentle connections - the same steepness
//  idea the hydrology router uses - without this class knowing anything about terrain.
//
//  Robust to a disconnected input: it restarts from any unvisited vertex, so the
//  result is a spanning FOREST (one tree per connected component) rather than
//  throwing on a degenerate/collinear point set GEO-1 could not fully triangulate.
//
//  Deterministic. Managed C# over arrays/List. Safe on a worker thread.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Geo
{
    /// <summary>The MST (or forest): the chosen edges as endpoint-index pairs, and the total cost.</summary>
    public sealed class MstResult
    {
        public readonly List<int> edgeA = new List<int>();
        public readonly List<int> edgeB = new List<int>();
        public float totalWeight;

        public int EdgeCount { get { return edgeA.Count; } }
    }

    /// <summary>Prim's minimum spanning tree over a GEO-1 neighbour graph.</summary>
    public static class PrimMST
    {
        // PLACEHOLDER-GEO2
        /// <summary>
        /// Builds the MST/forest over <paramref name="tri"/>'s neighbour graph. Edge cost is
        /// <paramref name="weightFn"/>(a, b), or Euclidean distance between the points when null.
        /// Returns empty for fewer than two points.
        /// </summary>
        public static MstResult Build(IList<Vector2> points, DelaunayResult tri,
                                      System.Func<int, int, float> weightFn = null)
        {
            MstResult r = new MstResult();
            int n = points != null ? points.Count : 0;
            if (n < 2 || tri == null || tri.neighbours == null) return r;

            if (weightFn == null)
                weightFn = (a, b) => Vector2.Distance(points[a], points[b]);

            bool[] inTree = new bool[n];
            Heap heap = new Heap(Mathf.Max(16, tri.EdgeCount * 2));

            // Spanning FOREST: seed Prim from every component's first unvisited vertex.
            for (int seed = 0; seed < n; seed++)
            {
                if (inTree[seed]) continue;

                inTree[seed] = true;
                PushEdges(heap, tri, inTree, weightFn, seed);

                while (heap.Count > 0)
                {
                    Edge e = heap.Pop();
                    if (inTree[e.b]) continue;        // lazy deletion: stale edge into the tree

                    inTree[e.b] = true;
                    r.edgeA.Add(e.a);
                    r.edgeB.Add(e.b);
                    r.totalWeight += e.w;

                    PushEdges(heap, tri, inTree, weightFn, e.b);
                }
            }

            return r;
        }

        private static void PushEdges(Heap heap, DelaunayResult tri, bool[] inTree,
                                      System.Func<int, int, float> weightFn, int from)
        {
            List<int> nbrs = tri.neighbours[from];
            for (int i = 0; i < nbrs.Count; i++)
            {
                int to = nbrs[i];
                if (inTree[to]) continue;             // already in the tree: no edge needed
                heap.Push(new Edge { a = from, b = to, w = weightFn(from, to) });
            }
        }

        // PLACEHOLDER-GEO2HEAP
        private struct Edge { public int a, b; public float w; }

        // A tiny binary min-heap keyed on edge weight. Self-contained rather than reaching into
        // the hydrology solver's private heap - no coupling, and the Prim loop is its only user.
        private sealed class Heap
        {
            private Edge[] items;
            private int count;

            public Heap(int capacity) { items = new Edge[Mathf.Max(4, capacity)]; }
            public int Count { get { return count; } }

            public void Push(Edge e)
            {
                if (count == items.Length) System.Array.Resize(ref items, items.Length * 2);
                int i = count++;
                items[i] = e;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (items[parent].w <= items[i].w) break;
                    Swap(parent, i);
                    i = parent;
                }
            }

            public Edge Pop()
            {
                Edge top = items[0];
                items[0] = items[--count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, rr = 2 * i + 2, small = i;
                    if (l < count && items[l].w < items[small].w) small = l;
                    if (rr < count && items[rr].w < items[small].w) small = rr;
                    if (small == i) break;
                    Swap(small, i);
                    i = small;
                }
                return top;
            }

            private void Swap(int x, int y) { Edge t = items[x]; items[x] = items[y]; items[y] = t; }
        }
    }
}
