// UC-T04-09: immutable accepted mesh lookup; bounded build memory and allocation-free local queries.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    internal sealed class WaterQuerySurface
    {
        private const int Grid = 64, MaxTriangles = 524288, MaxReferences = 2097152;
        private readonly Vector3[] vertices;
        private readonly Vector2[] depthWeight;
        private readonly Vector3[] directionSpeed;
        private readonly int[] triangles;
        private readonly List<int>[] cells = new List<int>[Grid * Grid];
        private readonly Bounds bounds;
        internal Bounds WorldBounds => bounds; // UC-T06-11: immutable footprint bounds support conservative regional water invalidation/proximity estimates.
        private readonly Vector2 scale;
        private readonly WaterShorelineIndex shoreline; // UC-T07-11: optional immutable actual-shore metadata; no outer-mesh/bounds substitution.
        internal long EstimatedManagedBytes { get; private set; } // UC-T05-06: a borrowed query index is admitted once per shared payload identity.

        internal WaterQuerySurface(WaterBodyData data, Matrix4x4 localToWorld, bool vertexFlow, Vector2 uniformFlow)
        {
            if (data?.vertices == null || data.vertices.Length == 0 || data.vertices.Length > 1048576 || data.triangles == null ||
                data.triangles.Length % 3 != 0 || data.triangles.Length / 3 > MaxTriangles ||
                data.uv?.Length != data.vertices.Length || data.colors?.Length != data.vertices.Length)
                throw new ArgumentException("Accepted water mesh has invalid streams or exceeds the query triangle budget.", nameof(data));
            vertices = new Vector3[data.vertices.Length];
            depthWeight = new Vector2[vertices.Length]; directionSpeed = new Vector3[vertices.Length];
            triangles = (int[])data.triangles.Clone();
            var extent = new Bounds(localToWorld.MultiplyPoint3x4(data.vertices[0]), Vector3.zero);
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = localToWorld.MultiplyPoint3x4(data.vertices[i]);
                var color = data.colors[i]; float depth = data.uv[i].y;
                if (!Finite(p) || !RiverChannelSnapshot.Finite(depth) || !RiverChannelSnapshot.Finite(color.a))
                    throw new ArgumentException("Accepted water mesh contains nonfinite depth or position.", nameof(data));
                vertices[i] = p; extent.Encapsulate(p);
                depthWeight[i] = new Vector2(Mathf.Max(0, depth), data.uv[i].x < 0 ? 0 : Mathf.Clamp01(color.a)); // UC-T04-09: quiet junction geometry preserves opacity.
                Vector2 direction = vertexFlow ? new Vector2(color.r * 2 - 1, color.g * 2 - 1) : uniformFlow;
                float speed = vertexFlow ? color.b * data.maxFlowSpeed : uniformFlow.magnitude;
                if (!RiverChannelSnapshot.Finite(direction.x) || !RiverChannelSnapshot.Finite(direction.y) || !RiverChannelSnapshot.Finite(speed))
                    throw new ArgumentException("Accepted water mesh contains nonfinite flow.", nameof(data));
                direction.Normalize(); directionSpeed[i] = new Vector3(direction.x, direction.y, Mathf.Max(0, speed));
            }
            bounds = extent;
            scale = new Vector2(Grid / Mathf.Max(0.001f, bounds.size.x), Grid / Mathf.Max(0.001f, bounds.size.z));
            int references = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    throw new ArgumentException("Accepted water mesh contains an invalid index.", nameof(data));
                Vector3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
                // Skirts/walls cannot supply a water column in plan.
                if (Cross(a, b, c) >= -1e-10) continue;
                int x0 = Cell(Mathf.Min(a.x, Mathf.Min(b.x, c.x)), bounds.min.x, scale.x);
                int x1 = Cell(Mathf.Max(a.x, Mathf.Max(b.x, c.x)), bounds.min.x, scale.x);
                int z0 = Cell(Mathf.Min(a.z, Mathf.Min(b.z, c.z)), bounds.min.z, scale.y);
                int z1 = Cell(Mathf.Max(a.z, Mathf.Max(b.z, c.z)), bounds.min.z, scale.y);
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
                {
                    if (++references > MaxReferences) throw new ArgumentException("Accepted water mesh exceeds the query index budget.", nameof(data));
                    int cell = z * Grid + x;
                    if (cells[cell] == null) cells[cell] = new List<int>();
                    if (cells[cell].Count >= 4096) throw new ArgumentException("Accepted water mesh exceeds the local query work budget.", nameof(data));
                    cells[cell].Add(t);
                }
            }
            shoreline = data.shorelineSegmentsLocal != null ? new WaterShorelineIndex(data.shorelineSegmentsLocal, localToWorld) : null;
            EstimatedManagedBytes = 64L + vertices.LongLength * 32L + triangles.LongLength * 4L + cells.LongLength * 32L + references * 8L + (shoreline?.EstimatedBytes ?? 0);
        }

        internal float ShoreDistance(float x, float z, float radius, out bool wetSide, out bool observed)
        {
            if (shoreline == null) throw new InvalidOperationException("Pending: accepted water has no pre-skirt shore metadata. Rebuild the bound water body; mesh skirts and rectangular bounds are not physical shore observations.");
            return shoreline.Distance(x, z, radius, out wetSide, out observed);
        }

        internal bool TrySample(Vector2 p, float time, in WaterSurfaceParams parameters,
            out float level, out float surfaceY, out float depth, out Vector2 flow, out Vector3 normal)
        {
            level = surfaceY = depth = 0; flow = Vector2.zero; normal = Vector3.up;
            float pad = parameters.horizontalDisplacement ? Mathf.Max(0, parameters.waveHeight) * Mathf.Max(1, parameters.stormAmp) * 0.535f : 0;
            if (!RiverChannelSnapshot.Finite(p.x) || !RiverChannelSnapshot.Finite(p.y) ||
                p.x < bounds.min.x - pad || p.x > bounds.max.x + pad || p.y < bounds.min.z - pad || p.y > bounds.max.z + pad) return false;
            int x0 = Cell(p.x - pad, bounds.min.x, scale.x), x1 = Cell(p.x + pad, bounds.min.x, scale.x);
            int z0 = Cell(p.y - pad, bounds.min.z, scale.y), z1 = Cell(p.y + pad, bounds.min.z, scale.y);
            int checks = 0;
            var point = new Vector3(p.x, 0, p.y);
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                var candidates = cells[z * Grid + x];
                if (candidates == null) continue;
                foreach (int t in candidates)
                {
                if (++checks > 4096) return false; // Refuse pathological work rather than stalling a physics step.
                int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                Vector3 a = Displaced(ia, time, parameters), b = Displaced(ib, time, parameters), c = Displaced(ic, time, parameters);
                double area = Cross(a, b, c);
                if (area >= -1e-10) continue; // A dynamically folded triangle cannot supply water.
                double u = Cross(b, c, point) / area, v = Cross(c, a, point) / area, w = 1 - u - v;
                if (u < -1e-6 || v < -1e-6 || w < -1e-6) continue;
                float y = (float)(u * a.y + v * b.y + w * c.y);
                float d = (float)(u * depthWeight[ia].x + v * depthWeight[ib].x + w * depthWeight[ic].x);
                if (d <= 0.0001f) continue;
                level = (float)(u * vertices[ia].y + v * vertices[ib].y + w * vertices[ic].y); surfaceY = y; depth = d;
                Vector3 f = directionSpeed[ia] * (float)u + directionSpeed[ib] * (float)v + directionSpeed[ic] * (float)w;
                flow = new Vector2(f.x, f.y) * f.z; normal = Vector3.Cross(b - a, c - a).normalized;
                return true;
                }
            }
            return false;
        }

        private Vector3 Displaced(int index, float time, in WaterSurfaceParams parameters)
        {
            var p = parameters; var f = directionSpeed[index]; p.flow = new Vector2(f.x, f.y) * f.z;
            Vector3 vertex = vertices[index]; Vector2 xz = new Vector2(vertex.x, vertex.z);
            float depth = depthWeight[index].x, weight = depthWeight[index].y;
            Vector2 horizontal = UCWaterSurface.HorizontalOffset(xz, time, p, weight);
            float limit = depth * 0.25f, magnitude = horizontal.magnitude;
            if (magnitude > limit && magnitude > 0) horizontal *= limit / magnitude;
            vertex.y += Mathf.Clamp(UCWaterSurface.WaveOffset(xz, time, p, weight), -limit, limit);
            vertex.x += horizontal.x; vertex.z += horizontal.y;
            return vertex;
        }

        private static int Cell(float p, float origin, float scale) => Mathf.Clamp(Mathf.FloorToInt((p - origin) * scale), 0, Grid - 1);
        private static double Cross(Vector3 a, Vector3 b, Vector3 c) =>
            ((double)b.x - a.x) * ((double)c.z - a.z) - ((double)b.z - a.z) * ((double)c.x - a.x);
        private static bool Finite(Vector3 p) => RiverChannelSnapshot.Finite(p.x) && RiverChannelSnapshot.Finite(p.y) && RiverChannelSnapshot.Finite(p.z);
    }
}
