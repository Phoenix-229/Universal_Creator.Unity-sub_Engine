// UC-T04-06: independent bank contact and bounded subdivision of the actual water strip.
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    internal static partial class RiverCrossSectionFit
    {
        internal sealed class Shoreline
        {
            internal readonly List<Vector3> Course = new List<Vector3>();
            internal float[] Bed, Depth, Arc, Left, Right;
        }

        private struct Section
        {
            internal Vector3 Point;
            internal float Bed, Depth, Requested, Left, Right;
        }

        // Metres. A limit failure retains the previous accepted mesh; it never admits a floating edge.
        private const float BankHeightToleranceM = 0.01f;
        private const float BankPlanToleranceM = 0.025f;
        private const int MaxRefinements = 8;
        private const int BankSearchSamples = 64;

        internal static bool TryFitShoreline(IList<Vector3> course, float[] bed, float[] depth, float[] requested,
            GroundSample ground, out Shoreline shoreline, out string diagnostic)
        {
            shoreline = null; diagnostic = string.Empty;
            int count = course?.Count ?? 0;
            if (count < 2 || count > MaxStations || bed?.Length != count || depth?.Length != count || requested?.Length != count || ground == null)
            { diagnostic = "Incomplete river profile or shoreline station budget exceeded (maximum 2048)."; return false; }
            var sections = new List<Section>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = course[i];
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(bed[i]) || !Finite(depth[i]) || depth[i] <= 0 ||
                    !Finite(requested[i]) || requested[i] < MinimumSupportedHalfWidthM || !Finite(bed[i] + depth[i]))
                { diagnostic = $"Invalid shoreline profile at station {i}."; return false; }
                if (i > 0 && XZ(p - course[i - 1]).sqrMagnitude < 1e-6f)
                { diagnostic = $"Coincident river stations at {i}."; return false; }
                sections.Add(new Section { Point = p, Bed = bed[i], Depth = depth[i], Requested = requested[i] });
            }
            for (int pass = 0; pass <= MaxRefinements; pass++)
            {
                var points = new List<Vector3>(sections.Count);
                foreach (Section section in sections) points.Add(section.Point);
                for (int i = 0; i < sections.Count; i++)
                {
                    Section section = sections[i];
                    if (!FitSection(ref section, Normal(points, i), ground))
                    { diagnostic = $"No supported pair of wet banks at river station {i}; check ground coverage, level and route."; return false; }
                    sections[i] = section;
                }
                var refined = new List<Section>(sections.Count);
                bool changed = false; int added = 0;
                for (int i = 0; i + 1 < sections.Count; i++)
                {
                    Section a = sections[i], b = sections[i + 1];
                    refined.Add(a);
                    Vector2 normalA = Normal(points, i), normalB = Normal(points, i + 1);
                    Vector3 leftA = Edge(a, normalA, true), rightA = Edge(a, normalA, false);
                    Vector3 leftB = Edge(b, normalB, true), rightB = Edge(b, normalB, false);
                    bool split = false;
                    for (int k = 1; k < 4; k++)
                    {
                        float t = k * 0.25f;
                        Section sample = Interpolate(a, b, t);
                        Vector2 normal = Vector2.Lerp(normalA, normalB, t).normalized;
                        if (!FitSection(ref sample, normal, ground))
                        { diagnostic = $"Missing banks or dry water span after station {i}."; return false; }
                        Vector3 left = Vector3.Lerp(leftA, leftB, t), right = Vector3.Lerp(rightA, rightB, t);
                        if (XZ(left - Edge(sample, normal, true)).magnitude > BankPlanToleranceM ||
                            XZ(right - Edge(sample, normal, false)).magnitude > BankPlanToleranceM ||
                            !BankContact(left, ground) || !BankContact(right, ground)) split = true;
                    }
                    if (split)
                    {
                        if (pass == MaxRefinements || sections.Count + added >= MaxStations || XZ(b.Point - a.Point).magnitude < 0.01f)
                        { diagnostic = "River shoreline needs more detail than the bounded fit supports; refine the authored course or reduce the bend."; return false; }
                        refined.Add(Interpolate(a, b, 0.5f)); added++; changed = true;
                    }
                }
                refined.Add(sections[sections.Count - 1]);
                if (changed) { sections = refined; continue; }
                var leftEdges = new Vector3[sections.Count]; var rightEdges = new Vector3[sections.Count];
                for (int i = 0; i < sections.Count; i++)
                { Vector2 normal = Normal(points, i); leftEdges[i] = Edge(sections[i], normal, true); rightEdges[i] = Edge(sections[i], normal, false); }
                // Check actual independent edges, rather than narrowing both sides to the smaller bank.
                for (int i = 0; i + 1 < sections.Count; i++)
                {
                    Vector3 left = leftEdges[i], right = rightEdges[i], nextLeft = leftEdges[i + 1], nextRight = rightEdges[i + 1];
                    if (Vector3.Cross(right - left, nextLeft - left).y <= 1e-8f || Vector3.Cross(nextRight - right, nextLeft - right).y <= 1e-8f ||
                        Vector3.Cross(nextLeft - nextRight, left - nextRight).y <= 1e-8f || Vector3.Cross(left - nextLeft, right - nextLeft).y <= 1e-8f)
                    { diagnostic = $"River bank sections fold at bend {i}; no overlapping water surface was accepted."; return false; }
                    for (int j = i + 2; j + 1 < sections.Count; j++)
                    {
                        Vector3 otherLeft = leftEdges[j], otherRight = rightEdges[j], otherNextLeft = leftEdges[j + 1], otherNextRight = rightEdges[j + 1];
                        if (QuadsOverlap(left, right, nextRight, nextLeft, otherLeft, otherRight, otherNextRight, otherNextLeft))
                        { diagnostic = $"River surface overlaps between spans {i} and {j}; edit the authored route."; return false; }
                    }
                }
                var result = new Shoreline { Bed = new float[sections.Count], Depth = new float[sections.Count], Arc = new float[sections.Count],
                    Left = new float[sections.Count], Right = new float[sections.Count] };
                for (int i = 0; i < sections.Count; i++)
                {
                    Section s = sections[i]; result.Course.Add(s.Point); result.Bed[i] = s.Bed; result.Depth[i] = s.Depth;
                    result.Left[i] = s.Left; result.Right[i] = s.Right;
                    if (i > 0) result.Arc[i] = result.Arc[i - 1] + XZ(s.Point - sections[i - 1].Point).magnitude;
                }
                shoreline = result; return true;
            }
            diagnostic = "River shoreline refinement budget exceeded."; return false;
        }

        private static Section Interpolate(Section a, Section b, float t)
        {
            return new Section { Point = Vector3.Lerp(a.Point, b.Point, t), Bed = Mathf.Lerp(a.Bed, b.Bed, t),
                Depth = Mathf.Lerp(a.Depth, b.Depth, t), Requested = Mathf.Lerp(a.Requested, b.Requested, t) };
        }

        private static Vector3 Edge(Section s, Vector2 normal, bool left)
        {
            float distance = left ? -s.Left : s.Right;
            return new Vector3(s.Point.x + normal.x * distance, s.Bed + s.Depth, s.Point.z + normal.y * distance);
        }

        private static bool FitSection(ref Section section, Vector2 normal, GroundSample ground)
        {
            float level = section.Bed + section.Depth;
            if (normal.sqrMagnitude < 0.5f || !ground(section.Point.x, section.Point.z, out float y) || !Finite(y) || y >= level - 0.0001f) return false;
            // Authored excavation widths are unchanged. Surface search may reach the actual wider bank,
            // but cannot run indefinitely across an ocean/lake or missing terrain coverage.
            float search = Mathf.Max(section.Requested * 4, section.Requested + 4 * (level - y));
            if (!Finite(search) || search > 10000f) return false;
            return FindBank(section.Point, -normal, level, search, ground, out section.Left) &&
                FindBank(section.Point, normal, level, search, ground, out section.Right);
        }

        private static bool FindBank(Vector3 centre, Vector2 direction, float level, float search, GroundSample ground, out float width)
        {
            width = 0; float wet = 0;
            for (int i = 1; i <= BankSearchSamples; i++)
            {
                float t = (float)i / BankSearchSamples, distance = search * t * t;
                if (!ground(centre.x + direction.x * distance, centre.z + direction.y * distance, out float y) || !Finite(y)) return false;
                if (y >= level)
                {
                    float dry = distance;
                    for (int j = 0; j < 16; j++)
                    {
                        float middle = (wet + dry) * 0.5f;
                        if (!ground(centre.x + direction.x * middle, centre.z + direction.y * middle, out y) || !Finite(y)) return false;
                        if (y < level) wet = middle; else dry = middle;
                    }
                    width = wet;
                    return width >= MinimumSupportedHalfWidthM && BankContact(new Vector3(centre.x + direction.x * width, level, centre.z + direction.y * width), ground);
                }
                wet = distance;
            }
            return false;
        }

        private static bool BankContact(Vector3 point, GroundSample ground)
        {
            return ground(point.x, point.z, out float y) && Finite(y) && y <= point.y + 0.0001f && point.y - y <= BankHeightToleranceM;
        }

        // Separating-axis test on two convex strips; no per-pair allocations. Positive winding was checked above.
        private static bool QuadsOverlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e, Vector3 f, Vector3 g, Vector3 h)
        {
            if (Maximum(a.x, b.x, c.x, d.x) <= Minimum(e.x, f.x, g.x, h.x) || Maximum(e.x, f.x, g.x, h.x) <= Minimum(a.x, b.x, c.x, d.x) ||
                Maximum(a.z, b.z, c.z, d.z) <= Minimum(e.z, f.z, g.z, h.z) || Maximum(e.z, f.z, g.z, h.z) <= Minimum(a.z, b.z, c.z, d.z)) return false;
            return !Separated(a, b) && !Separated(b, c) && !Separated(c, d) && !Separated(d, a) &&
                !Separated(e, f) && !Separated(f, g) && !Separated(g, h) && !Separated(h, e);
            bool Separated(Vector3 p, Vector3 q)
            {
                Vector2 normal = new Vector2(q.z - p.z, p.x - q.x).normalized;
                float pa = Vector2.Dot(normal, XZ(a)), pb = Vector2.Dot(normal, XZ(b)), pc = Vector2.Dot(normal, XZ(c)), pd = Vector2.Dot(normal, XZ(d));
                float pe = Vector2.Dot(normal, XZ(e)), pf = Vector2.Dot(normal, XZ(f)), pg = Vector2.Dot(normal, XZ(g)), ph = Vector2.Dot(normal, XZ(h));
                return Maximum(pa, pb, pc, pd) <= Minimum(pe, pf, pg, ph) + 0.0001f || Maximum(pe, pf, pg, ph) <= Minimum(pa, pb, pc, pd) + 0.0001f;
            }
        }

        private static float Minimum(float a, float b, float c, float d) => Mathf.Min(Mathf.Min(a, b), Mathf.Min(c, d));
        private static float Maximum(float a, float b, float c, float d) => Mathf.Max(Mathf.Max(a, b), Mathf.Max(c, d));
    }
}
