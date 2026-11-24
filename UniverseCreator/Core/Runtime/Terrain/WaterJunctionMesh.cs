// UC-T04-06: subtract accepted receiver coverage and match its surface at the shared boundary.
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    internal static class WaterJunctionMesh
    {
        private const int MaxTriangles = 131072;
        private const int MaxOutputVertices = 196608;
        private const int MaxPairChecks = 2000000;
        private const int MaxFragmentsPerTriangle = 128;
        private const double SideTolerance = 1e-8;

        private struct Vertex
        {
            internal Vector3 Position, Normal;
            internal Vector4 Tangent;
            internal Vector2 UV;
            internal Color Color;
        }

        private struct Receiver
        {
            internal Vector3 A, B, C;
            internal float MinX, MaxX, MinZ, MaxZ;
        }

        // Plain copied mesh data only. Neither input is changed, including when a work/geometry limit fails.
        internal static bool TryJoin(WaterBodyData incoming, Matrix4x4 incomingToWorld, WaterBodyData target,
            Matrix4x4 targetToWorld, float maxLevelCorrectionM, out WaterBodyData joined, out string diagnostic)
        {
            joined = null; diagnostic = string.Empty;
            if (!Valid(incoming) || !Valid(target) || !Finite(maxLevelCorrectionM) || maxLevelCorrectionM < 0)
            { diagnostic = "Junction requires complete finite incoming and receiving mesh data."; return false; }
            var source = new Vertex[incoming.vertices.Length];
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = minX, maxZ = maxX;
            for (int i = 0; i < source.Length; i++)
            {
                Vector3 p = incomingToWorld.MultiplyPoint3x4(incoming.vertices[i]);
                if (!Finite(p)) { diagnostic = "Incoming junction transform is not finite."; return false; }
                source[i] = new Vertex { Position = p, Normal = incoming.normals[i], Tangent = incoming.tangents[i], UV = incoming.uv[i], Color = incoming.colors[i] };
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            var receivers = new List<Receiver>();
            for (int i = 0; i < target.triangles.Length; i += 3)
            {
                Vector3 a = targetToWorld.MultiplyPoint3x4(target.vertices[target.triangles[i]]);
                Vector3 b = targetToWorld.MultiplyPoint3x4(target.vertices[target.triangles[i + 1]]);
                Vector3 c = targetToWorld.MultiplyPoint3x4(target.vertices[target.triangles[i + 2]]);
                if (!Finite(a) || !Finite(b) || !Finite(c)) { diagnostic = "Receiving junction transform is not finite."; return false; }
                double area = Side(a, b, c);
                if (System.Math.Abs(area) < SideTolerance) continue;
                if (area < 0) { Vector3 swap = b; b = c; c = swap; }
                var receiver = new Receiver { A = a, B = b, C = c, MinX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)),
                    MaxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)), MinZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), MaxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z)) };
                if (receiver.MaxX >= minX && receiver.MinX <= maxX && receiver.MaxZ >= minZ && receiver.MinZ <= maxZ) receivers.Add(receiver);
            }
            // UC-T04-06: index receiver triangles once; local ribbon triangles must
            // not scan every lake/ocean triangle. Bound index storage independently.
            int grid = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(receivers.Count)), 1, 64);
            float spanX = Mathf.Max(0.001f, maxX - minX), spanZ = Mathf.Max(0.001f, maxZ - minZ);
            var bins = new List<int>[grid * grid]; int binEntries = 0;
            int GridX(float x) => Mathf.Clamp((int)((x - minX) / spanX * grid), 0, grid - 1);
            int GridZ(float z) => Mathf.Clamp((int)((z - minZ) / spanZ * grid), 0, grid - 1);
            for (int i = 0; i < receivers.Count; i++)
            {
                Receiver receiver = receivers[i];
                for (int z = GridZ(receiver.MinZ); z <= GridZ(receiver.MaxZ); z++)
                    for (int x = GridX(receiver.MinX); x <= GridX(receiver.MaxX); x++)
                    {
                        if (++binEntries > MaxPairChecks) { diagnostic = "Junction spatial index budget exceeded."; return false; }
                        int bin = z * grid + x;
                        if (bins[bin] == null) bins[bin] = new List<int>();
                        bins[bin].Add(i);
                    }
            }
            var seen = new int[receivers.Count]; var candidates = new List<int>();
            var result = new List<Vertex>(); int checks = 0; double removedArea = 0; bool matchedBoundary = false;
            for (int triangle = 0; triangle < incoming.triangles.Length; triangle += 3)
            {
                Vertex a = source[incoming.triangles[triangle]], b = source[incoming.triangles[triangle + 1]], c = source[incoming.triangles[triangle + 2]];
                double initialArea = Side(a.Position, b.Position, c.Position) * 0.5;
                if (initialArea <= SideTolerance) { diagnostic = "Incoming river has a folded or degenerate triangle."; return false; }
                var fragments = new List<List<Vertex>> { new List<Vertex> { a, b, c } };
                candidates.Clear(); int stamp = triangle + 1;
                for (int z = GridZ(Mathf.Min(a.Position.z, Mathf.Min(b.Position.z, c.Position.z))); z <= GridZ(Mathf.Max(a.Position.z, Mathf.Max(b.Position.z, c.Position.z))); z++)
                    for (int x = GridX(Mathf.Min(a.Position.x, Mathf.Min(b.Position.x, c.Position.x))); x <= GridX(Mathf.Max(a.Position.x, Mathf.Max(b.Position.x, c.Position.x))); x++)
                    {
                        var bin = bins[z * grid + x]; if (bin == null) continue;
                        foreach (int receiverIndex in bin)
                        {
                            if (++checks > MaxPairChecks) { diagnostic = "Junction spatial query budget exceeded."; return false; }
                            if (seen[receiverIndex] == stamp) continue;
                            seen[receiverIndex] = stamp; candidates.Add(receiverIndex);
                        }
                    }
                candidates.Sort(); // Fixed receiver order keeps clipping reproducible across index cells.
                foreach (int receiverIndex in candidates)
                {
                    Receiver receiver = receivers[receiverIndex];
                    if (++checks > MaxPairChecks) { diagnostic = "Junction clipping work budget exceeded."; return false; }
                    if (!Touches(a.Position, b.Position, c.Position, receiver)) continue;
                    var next = new List<List<Vertex>>();
                    foreach (List<Vertex> fragment in fragments)
                    {
                        if (++checks > MaxPairChecks) { diagnostic = "Junction clipping work budget exceeded."; return false; }
                        List<Vertex> inside = fragment;
                        for (int edge = 0; edge < 3 && inside.Count >= 3; edge++)
                        {
                            Vector3 start = edge == 0 ? receiver.A : edge == 1 ? receiver.B : receiver.C;
                            Vector3 end = edge == 0 ? receiver.B : edge == 1 ? receiver.C : receiver.A;
                            if (!Split(inside, start, end, receiver, maxLevelCorrectionM, ref matchedBoundary,
                                out var retainedInside, out var outside))
                            { diagnostic = "Junction level correction exceeds available transition clearance."; return false; }
                            if (outside.Count >= 3) next.Add(outside);
                            inside = retainedInside;
                        }
                        if (next.Count > MaxFragmentsPerTriangle) { diagnostic = "Junction fragment budget exceeded."; return false; }
                    }
                    fragments = next;
                    if (fragments.Count == 0) break;
                }
                double retainedArea = 0;
                foreach (List<Vertex> polygon in fragments)
                {
                    for (int i = 1; i + 1 < polygon.Count; i++)
                    {
                        double area = Side(polygon[0].Position, polygon[i].Position, polygon[i + 1].Position) * 0.5;
                        if (area <= SideTolerance) continue;
                        retainedArea += area;
                        if (result.Count + 3 > MaxOutputVertices) { diagnostic = "Junction output vertex budget exceeded."; return false; }
                        result.Add(polygon[0]); result.Add(polygon[i]); result.Add(polygon[i + 1]);
                    }
                }
                removedArea += System.Math.Max(0, initialArea - retainedArea);
            }
            if (removedArea <= SideTolerance || !matchedBoundary || result.Count == 0)
            { diagnostic = "River and receiver need a shared boundary with a nonempty incoming reach."; return false; }
            Matrix4x4 toLocal = incomingToWorld.inverse;
            var data = new WaterBodyData { name = incoming.name, kind = incoming.kind, product = incoming.product, generateVolume = incoming.generateVolume,
                meanFlow = incoming.meanFlow, maxFlowSpeed = incoming.maxFlowSpeed, vertices = new Vector3[result.Count], normals = new Vector3[result.Count],
                tangents = new Vector4[result.Count], uv = new Vector2[result.Count], colors = new Color[result.Count], triangles = new int[result.Count],
                shorelineSegmentsLocal = incoming.shorelineSegmentsLocal }; // UC-T07-11: incoming bank contours retain the same owner-local coordinate space through junction fitting.
            double sum = 0;
            for (int i = 0; i < result.Count; i++)
            {
                Vertex vertex = result[i]; data.vertices[i] = toLocal.MultiplyPoint3x4(vertex.Position); data.normals[i] = vertex.Normal;
                data.tangents[i] = vertex.Tangent; data.uv[i] = vertex.UV; data.colors[i] = vertex.Color; data.triangles[i] = i;
                if (!Finite(data.vertices[i])) { diagnostic = "Junction owner transform is singular or not finite."; return false; }
                if (i == 0) data.worldBounds = new Bounds(vertex.Position, Vector3.zero); else data.worldBounds.Encapsulate(vertex.Position);
                data.maxDepth = Mathf.Max(data.maxDepth, vertex.UV.y); sum += vertex.Position.y;
            }
            data.surfaceLevel = (float)(sum / result.Count); joined = data; return true;
        }

        private static bool Split(List<Vertex> polygon, Vector3 start, Vector3 end, Receiver receiver, float maxCorrection,
            ref bool matched, out List<Vertex> inside, out List<Vertex> outside)
        {
            inside = new List<Vertex>(polygon.Count + 1); outside = new List<Vertex>(polygon.Count + 1);
            Vertex previous = polygon[polygon.Count - 1]; double previousSide = Side(start, end, previous.Position);
            for (int i = 0; i < polygon.Count; i++)
            {
                Vertex current = polygon[i]; double currentSide = Side(start, end, current.Position);
                bool wasInside = previousSide >= 0, isInside = currentSide >= 0;
                if (wasInside != isInside)
                {
                    float t = (float)(previousSide / (previousSide - currentSide));
                    Vertex intersection = Lerp(previous, current, t);
                    if (!Match(ref intersection, receiver, maxCorrection, ref matched)) return false;
                    inside.Add(intersection); outside.Add(intersection);
                }
                if (System.Math.Abs(currentSide) <= SideTolerance && !Match(ref current, receiver, maxCorrection, ref matched)) return false;
                if (isInside) inside.Add(current); else outside.Add(current);
                previous = polygon[i]; previousSide = currentSide;
            }
            return true;
        }

        private static bool Match(ref Vertex vertex, Receiver receiver, float maxCorrection, ref bool matched)
        {
            Vector3 p = vertex.Position;
            double total = Side(receiver.A, receiver.B, receiver.C);
            double a = Side(receiver.B, receiver.C, p) / total, b = Side(receiver.C, receiver.A, p) / total, c = 1 - a - b;
            if (a < -1e-6 || b < -1e-6 || c < -1e-6) return true; // Infinite edge extensions are not receiver coverage.
            float y = (float)(a * receiver.A.y + b * receiver.B.y + c * receiver.C.y);
            float delta = y - p.y;
            if (!Finite(y) || Mathf.Abs(delta) > maxCorrection || vertex.UV.y + delta < -0.0001f) return false;
            vertex.Position.y = y; vertex.UV.y = Mathf.Max(0, vertex.UV.y + delta);
            vertex.UV.x = -1; // UC-T04-09: the complete shared boundary stays still without making the water transparent.
            matched = true; return true;
        }

        private static Vertex Lerp(Vertex a, Vertex b, float t)
        {
            return new Vertex { Position = Vector3.Lerp(a.Position, b.Position, t), Normal = Vector3.Lerp(a.Normal, b.Normal, t).normalized,
                Tangent = Vector4.Lerp(a.Tangent, b.Tangent, t), UV = Vector2.Lerp(a.UV, b.UV, t), Color = Color.Lerp(a.Color, b.Color, t) };
        }

        private static bool Touches(Vector3 a, Vector3 b, Vector3 c, Receiver r)
        {
            return Mathf.Max(a.x, Mathf.Max(b.x, c.x)) >= r.MinX && Mathf.Min(a.x, Mathf.Min(b.x, c.x)) <= r.MaxX &&
                Mathf.Max(a.z, Mathf.Max(b.z, c.z)) >= r.MinZ && Mathf.Min(a.z, Mathf.Min(b.z, c.z)) <= r.MaxZ;
        }

        private static bool Valid(WaterBodyData data)
        {
            int count = data?.vertices?.Length ?? 0;
            if (count < 3 || count > MaxOutputVertices || data.normals?.Length != count || data.tangents?.Length != count ||
                data.uv?.Length != count || data.colors?.Length != count || data.triangles == null || data.triangles.Length < 3 ||
                data.triangles.Length % 3 != 0 || data.triangles.Length / 3 > MaxTriangles) return false;
            foreach (int index in data.triangles) if (index < 0 || index >= count) return false;
            for (int i = 0; i < count; i++)
                if (!Finite(data.vertices[i]) || !Finite(data.normals[i]) || !Finite(data.uv[i].x) || !Finite(data.uv[i].y) ||
                    !Finite(data.tangents[i].x) || !Finite(data.tangents[i].y) || !Finite(data.tangents[i].z) || !Finite(data.tangents[i].w) ||
                    !Finite(data.colors[i].r) || !Finite(data.colors[i].g) || !Finite(data.colors[i].b) || !Finite(data.colors[i].a)) return false;
            return true;
        }

        private static double Side(Vector3 a, Vector3 b, Vector3 p) =>
            ((double)b.z - a.z) * ((double)p.x - a.x) - ((double)b.x - a.x) * ((double)p.z - a.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
