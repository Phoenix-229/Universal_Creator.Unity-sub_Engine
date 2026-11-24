// UC-T06-03: patch merging preserves accepted placement outside half-open world bounds and owns every merged collection.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    internal static class GraphVegetationPatch
    {
        // UC-T06-03: subtract the union of resident rectangles, so overlaps cannot hide an unloaded gap.
        // Worker only, bounded to 4096 resident regions and uncovered pieces; exceeding the limit keeps the request pending.
        internal static bool HasCoverage(IReadOnlyList<GraphVegetationRegion> regions, Rect patch, StopToken stop)
        {
            ValidateBounds(patch);
            const int limit = 4096;
            if (regions == null || regions.Count > limit) return false;
            var remaining = new List<Rect> { patch };
            var next = new List<Rect>();
            foreach (var region in regions)
            {
                if (stop != null && stop.StopRequested) throw new OperationCanceledException();
                if (region == null) continue;
                var bounds = new Rect(region.Position.x, region.Position.z, region.Size.x, region.Size.z);
                ValidateBounds(bounds);
                next.Clear();
                foreach (var area in remaining)
                {
                    float x0 = Mathf.Max(area.xMin, bounds.xMin), z0 = Mathf.Max(area.yMin, bounds.yMin);
                    float x1 = Mathf.Min(area.xMax, bounds.xMax), z1 = Mathf.Min(area.yMax, bounds.yMax);
                    if (x0 >= x1 || z0 >= z1) next.Add(area);
                    else
                    {
                        AddRemainder(next, area.xMin, area.yMin, x0, area.yMax);
                        AddRemainder(next, x1, area.yMin, area.xMax, area.yMax);
                        AddRemainder(next, x0, area.yMin, x1, z0);
                        AddRemainder(next, x0, z1, x1, area.yMax);
                    }
                    if (next.Count > limit) return false;
                }
                if (next.Count == 0) return true;
                var swap = remaining; remaining = next; next = swap;
            }
            return false;
        }

        private static void AddRemainder(List<Rect> pieces, float x0, float z0, float x1, float z1)
        {
            if (x1 > x0 && z1 > z0) pieces.Add(new Rect(x0, z0, x1 - x0, z1 - z0));
        }

        internal static void ValidateBounds(Rect bounds)
        {
            if (!Finite(bounds.xMin) || !Finite(bounds.yMin) || !Finite(bounds.xMax) || !Finite(bounds.yMax) ||
                bounds.width <= 0 || bounds.height <= 0 || bounds.xMax <= bounds.xMin || bounds.yMax <= bounds.yMin)
                throw new ArgumentException("Vegetation patch requires finite positive world-metre bounds.", nameof(bounds));
        }

        // Managed worker only. Both bags are owned accepted/candidate products; prior is never edited or shared into the merged result.
        // Detail cell centres define patch membership. Preserve the prior grid lattice; nearest candidate sampling handles changed detail resolution.
        internal static void Merge(OutputBag prior, OutputBag candidate, Vector3 position, Vector3 size, Rect patch, StopToken stop)
        {
            ValidateBounds(patch);
            if (prior == null || candidate == null) throw new ArgumentNullException(prior == null ? nameof(prior) : nameof(candidate));
            var channels = new HashSet<string>(prior.scatter.Keys); channels.UnionWith(candidate.scatter.Keys);
            foreach (string channel in channels)
            {
                var merged = new List<ScatterInstance>();
                if (prior.scatter.TryGetValue(channel, out var old))
                    for (int i = 0; i < old.Count; i++)
                    {
                        if ((i & 255) == 0 && stop != null && stop.StopRequested) throw new OperationCanceledException();
                        if (!Contains(patch, old[i].position.x, old[i].position.z)) merged.Add(old[i]);
                    }
                if (candidate.scatter.TryGetValue(channel, out var next))
                    for (int i = 0; i < next.Count; i++)
                    {
                        if ((i & 255) == 0 && stop != null && stop.StopRequested) throw new OperationCanceledException();
                        if (Contains(patch, next[i].position.x, next[i].position.z)) merged.Add(next[i]);
                    }
                candidate.scatter[channel] = merged;
            }
            var layers = new HashSet<int>(prior.detailLayers.Keys); layers.UnionWith(candidate.detailLayers.Keys);
            foreach (int layer in layers)
            {
                prior.detailLayers.TryGetValue(layer, out var old); candidate.detailLayers.TryGetValue(layer, out var next);
                var lattice = old ?? next;
                if (lattice == null) throw new InvalidOperationException("A patch cannot preserve a null detail-grid lattice.");
                int rows = lattice.GetLength(0), columns = lattice.GetLength(1);
                if (rows == 0 || columns == 0 || next != null && (next.GetLength(0) == 0 || next.GetLength(1) == 0))
                    throw new InvalidOperationException("A patch cannot preserve an empty detail-grid lattice.");
                var merged = new int[rows, columns];
                for (int z = 0; z < rows; z++)
                {
                    if (stop != null && stop.StopRequested) throw new OperationCanceledException();
                    for (int x = 0; x < columns; x++)
                    {
                        float worldX = position.x + (x + 0.5f) / columns * size.x, worldZ = position.z + (z + 0.5f) / rows * size.z;
                        merged[z, x] = Contains(patch, worldX, worldZ) ? next == null ? 0 :
                            next[Math.Min(next.GetLength(0) - 1, (int)((z + 0.5f) / rows * next.GetLength(0))),
                                 Math.Min(next.GetLength(1) - 1, (int)((x + 0.5f) / columns * next.GetLength(1)))] : old == null ? 0 : old[z, x];
                    }
                }
                candidate.detailLayers[layer] = merged;
            }
        }

        private static bool Contains(Rect bounds, float x, float z) => x >= bounds.xMin && x < bounds.xMax && z >= bounds.yMin && z < bounds.yMax;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
