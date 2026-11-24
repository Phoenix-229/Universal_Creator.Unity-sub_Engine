// UC-T07-11: immutable physical shore segments with bounded, allocation-free horizontal distance queries.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    internal sealed class WaterShorelineIndex
    {
        private const int Grid = 64, MaxReferences = 1048576;
        private readonly Vector2[] points;
        private readonly List<int>[] cells = new List<int>[Grid * Grid];
        private readonly Vector2 origin, scale;
        internal long EstimatedBytes { get; }

        internal WaterShorelineIndex(Vector3[] localPairs, Matrix4x4 localToWorld)
        {
            if (localPairs == null || localPairs.Length % 2 != 0 || localPairs.Length > 65536)
                throw new ArgumentException("Accepted shore metadata requires endpoint pairs and at most 32,768 segments.");
            points = new Vector2[localPairs.Length]; Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity), max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < localPairs.Length; i++)
            {
                Vector3 p = localToWorld.MultiplyPoint3x4(localPairs[i]);
                if (!RiverChannelSnapshot.Finite(p.x) || !RiverChannelSnapshot.Finite(p.y) || !RiverChannelSnapshot.Finite(p.z)) throw new ArgumentException("Accepted shore transform contains nonfinite coordinates.");
                points[i] = new Vector2(p.x, p.z); min = Vector2.Min(min, points[i]); max = Vector2.Max(max, points[i]);
            }
            origin = points.Length == 0 ? Vector2.zero : min;
            Vector2 extent = points.Length == 0 ? Vector2.one : max - min;
            scale = new Vector2(Grid / Mathf.Max(0.001f, extent.x), Grid / Mathf.Max(0.001f, extent.y));
            int references = 0;
            for (int i = 0; i < points.Length; i += 2)
            {
                Vector2 a = points[i], b = points[i + 1];
                int x0 = Cell(Mathf.Min(a.x, b.x), origin.x, scale.x), x1 = Cell(Mathf.Max(a.x, b.x), origin.x, scale.x);
                int z0 = Cell(Mathf.Min(a.y, b.y), origin.y, scale.y), z1 = Cell(Mathf.Max(a.y, b.y), origin.y, scale.y);
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
                {
                    if (++references > MaxReferences) throw new ArgumentException("Accepted shore exceeds its spatial-reference budget.");
                    int cell = z * Grid + x;
                    if (cells[cell] == null) cells[cell] = new List<int>();
                    if (cells[cell].Count == 4096) throw new ArgumentException("Accepted shore exceeds the local query envelope.");
                    cells[cell].Add(i);
                }
            }
            EstimatedBytes = 128L + points.LongLength * 8 + cells.LongLength * 32 + references * 8L;
        }

        internal float Distance(float x, float z, float radius, out bool wetSide, out bool observed)
        {
            if (!RiverChannelSnapshot.Finite(x) || !RiverChannelSnapshot.Finite(z) || !RiverChannelSnapshot.Finite(radius) || radius <= 0 || radius > 256)
                throw new ArgumentException("Accepted shore distance requires finite world metres and a search radius 0…256 metres.");
            double nearestSquared = (double)radius * radius; int checks = 0; wetSide = observed = false;
            int x0 = Cell(x - radius, origin.x, scale.x), x1 = Cell(x + radius, origin.x, scale.x);
            int z0 = Cell(z - radius, origin.y, scale.y), z1 = Cell(z + radius, origin.y, scale.y);
            for (int iz = z0; iz <= z1; iz++) for (int ix = x0; ix <= x1; ix++)
            {
                var candidates = cells[iz * Grid + ix]; if (candidates == null) continue;
                foreach (int i in candidates)
                {
                    Vector2 a = points[i], b = points[i + 1];
                    // UC-T07-11: each segment is present in every intersected cell. Test it only in its first queried cell,
                    // keeping the immutable worker lookup allocation-free and the limit about distinct geometric work.
                    if (ix != Math.Max(x0, Cell(Mathf.Min(a.x, b.x), origin.x, scale.x)) ||
                        iz != Math.Max(z0, Cell(Mathf.Min(a.y, b.y), origin.y, scale.y))) continue;
                    if (++checks > 4096) throw new InvalidOperationException("Accepted shore query exceeds 4,096 distinct segment checks; reduce the search band or publish smaller water regions.");
                    double dx = (double)b.x - a.x, dz = (double)b.y - a.y;
                    double lengthSquared = dx * dx + dz * dz;
                    double t = lengthSquared > 0 ? Math.Max(0, Math.Min(1, (((double)x - a.x) * dx + ((double)z - a.y) * dz) / lengthSquared)) : 0;
                    double px = a.x + t * dx - x, pz = a.y + t * dz - z;
                    double distanceSquared = px * px + pz * pz;
                    if (distanceSquared < nearestSquared)
                    {
                        nearestSquared = distanceSquared; observed = true;
                        wetSide = dx * ((double)z - a.y) - dz * ((double)x - a.x) >= 0;
                    }
                }
            }
            return (float)Math.Sqrt(nearestSquared);
        }

        private static int Cell(float p, float min, float scale) => Mathf.Clamp(Mathf.FloorToInt((p - min) * scale), 0, Grid - 1);
    }
}
