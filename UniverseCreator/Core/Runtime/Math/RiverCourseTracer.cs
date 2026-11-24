// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    RiverCourseTracer.cs   (RIV-2)
//  Purpose: Trace a procedural river course from a seeded high source DOWNHILL to
//           the sea, by a descent-biased best-first (Dijkstra/A*) search with a
//           breadcrumb reconstruction - the route-search half of RIV-2 that a grep
//           of the codebase found entirely unbuilt (no A*/Dijkstra, no decrease-key
//           heap, no breadcrumb-to-outlet in the whole asset).
//
//  WHY A SEARCH AND NOT JUST "FOLLOW THE STEEPEST NEIGHBOUR". A pure steepest-descent
//  walk stalls the instant it reaches a local pit - a closed cell lower than all its
//  neighbours - and a real noise heightfield is full of them. A cost search that lets
//  the course climb a little when it must (a large but finite uphill penalty) threads
//  out of a pit and carries on to the sea, while still preferring to fall wherever it
//  can. The goal is the first sub-sea cell reached, so the course always ENDS in the
//  ocean, which is the structural guarantee a river needs.
//
//  Pure, deterministic, managed C#. The caller supplies the coarse height grid (the
//  same whole-world field the drainage solve builds); this returns the ordered list
//  of cell indices from source to outlet, ready to reduce to spline knots (RDP, via
//  SplineOperations) and feed the river nodes.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>Finds bounded descent-biased least-cost courses on a captured, read-only height lattice.</summary>
    public static class RiverCourseTracer
    {
        /// <summary>
        /// Traces a descent-biased least-cost course from <paramref name="sourceIndex"/> to the
        /// least-cost cell below <paramref name="sea"/>. Returns the ordered cell indices (source first,
        /// outlet last) or null if the source is already sub-sea or no outlet is reachable.
        /// <paramref name="uphillPenalty"/> is the extra cost per unit of climb: large so the course
        /// falls wherever it can, finite so it can still cross a pit lip to reach the sea.
        /// </summary>
        /// <param name="height">Read-only normalized heights, at least resX * resZ finite samples.</param>
        /// <param name="resX">Positive lattice width.</param>
        /// <param name="resZ">Positive lattice height; product must fit an int.</param>
        /// <param name="sourceIndex">Source cell in row-major order.</param>
        /// <param name="sea">Finite normalized sea level; outlet tests are strictly below this value.</param>
        /// <param name="uphillPenalty">Finite cost per unit of height climb, clamped to at least one.</param>
        /// <returns>A caller-owned course, or null for invalid input, submerged source or no outlet.</returns>
        /// <remarks>UC-T03-04: retains the original method-group signature and unit-grid cost convention.
        /// Ties use cell index for deterministic ordering. Use the workspace overload for physical spacing and cancellation.</remarks>
        public static List<int> TraceDownhill(float[] height, int resX, int resZ,
                                              int sourceIndex, float sea, float uphillPenalty = 1000f)
        {
            // UC-T03-04: compatibility entry point delegates to the same bounded tracer.
            if (!ValidGrid(height, resX, resZ, sourceIndex) || !RiverSearchWorkspace.Finite(sea) ||
                !RiverSearchWorkspace.Finite(uphillPenalty)) return null;
            int count = resX * resZ;
            var workspace = new RiverSearchWorkspace(count);
            return TraceDownhill(height, resX, resZ, sourceIndex, sea, Mathf.Max(1f, uphillPenalty),
                Vector2.one, 1f, count, workspace).Course;
        }

        /// <summary>UC-T03-04: traces with physical distances, bounded scratch/frontier storage and explicit outcomes.</summary>
        /// <param name="height">Borrowed row-major normalized heights, at least resX * resZ samples; immutable for the call.
        /// Encountered nonfinite samples reject the search; unvisited cells are not inspected.</param>
        /// <param name="resX">Positive grid width.</param>
        /// <param name="resZ">Positive grid height; product must fit an int and workspace capacity.</param>
        /// <param name="sourceIndex">Source cell in the grid.</param>
        /// <param name="sea">Finite normalized sea level; a goal is strictly below it.</param>
        /// <param name="uphillPenalty">Finite nonnegative extra metres of search cost per metre of climb.</param>
        /// <param name="cellSizeMetres">Positive finite X/Z cell spacing in metres.</param>
        /// <param name="worldHeightMetres">Positive finite metres per normalized height unit.</param>
        /// <param name="maxVisits">Positive settled-cell budget, including the goal; at most the grid cell count is used.</param>
        /// <param name="workspace">Non-null owned workspace; one call at a time, reusable after any result or exception.</param>
        /// <param name="cancelled">Optional cooperative callback checked before each settlement and breadcrumb step.
        /// Runs on the caller's thread; exceptions propagate after releasing workspace ownership.</param>
        /// <returns>Complete path only on Found; all other statuses carry no partial course.</returns>
        /// <exception cref="InvalidOperationException">The workspace is already in use.</exception>
        public static RiverSearchResult TraceDownhill(float[] height, int resX, int resZ, int sourceIndex,
            float sea, float uphillPenalty, Vector2 cellSizeMetres, float worldHeightMetres, int maxVisits,
            RiverSearchWorkspace workspace, Func<bool> cancelled = null)
        {
            if (!ValidGrid(height, resX, resZ, sourceIndex) || workspace == null || workspace.Capacity < (long)resX * resZ ||
                maxVisits <= 0 || !RiverSearchWorkspace.Finite(sea) || !RiverSearchWorkspace.Finite(uphillPenalty) || uphillPenalty < 0f ||
                !RiverSearchWorkspace.Finite(cellSizeMetres.x) || cellSizeMetres.x <= 0f ||
                !RiverSearchWorkspace.Finite(cellSizeMetres.y) || cellSizeMetres.y <= 0f ||
                !RiverSearchWorkspace.Finite(worldHeightMetres) || worldHeightMetres <= 0f)
                return new RiverSearchResult(RiverSearchStatus.InvalidInput, null, 0, 0, 0, double.NaN, System.Diagnostics.Stopwatch.GetTimestamp());
            return workspace.Trace(height, resX, resZ, sourceIndex, sea, uphillPenalty, cellSizeMetres.x,
                cellSizeMetres.y, worldHeightMetres, Math.Min(maxVisits, resX * resZ), cancelled);
        }

        private static bool ValidGrid(float[] height, int resX, int resZ, int source)
        {
            long count = (long)resX * resZ;
            return resX > 0 && resZ > 0 && count <= int.MaxValue && height != null &&
                height.Length >= count && source >= 0 && source < count;
        }
    }
}
