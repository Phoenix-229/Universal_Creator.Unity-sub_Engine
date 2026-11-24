// UC-T04-04: bounded, read-only wetted-bank fitting; no terrain request or Unity object ownership.
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    internal static partial class RiverCrossSectionFit
    {
        internal delegate bool GroundSample(float x, float z, out float elevationM);
        internal const int MaxStations = 2048;
        private const int CrossSamples = 32;
        private const float MinimumSupportedHalfWidthM = 0.01f;

        // Width is an upper bound, never a request to extend past the bank. Sample between
        // stations too, so smoothing cannot bridge a narrow bank or a missing terrain strip.
        internal static bool TryFit(IList<Vector3> course, float[] bed, float[] depth, float[] requested,
            GroundSample ground, out float[] fitted, out string diagnostic)
        {
            fitted = null; diagnostic = string.Empty;
            int count = course?.Count ?? 0;
            if (count < 2 || count > MaxStations || bed?.Length != count || depth?.Length != count || requested?.Length != count || ground == null)
            { diagnostic = "Incomplete river profile or station budget exceeded (maximum 2048)."; return false; }
            var limits = new float[count];
            var arc = new float[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 p = course[i];
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(bed[i]) || !Finite(depth[i]) || depth[i] <= 0 ||
                    !Finite(requested[i]) || requested[i] < MinimumSupportedHalfWidthM)
                { diagnostic = $"Invalid or unsupported river section at station {i}."; return false; }
                if (i > 0)
                {
                    float length = XZ(course[i] - course[i - 1]).magnitude;
                    if (length < 0.001f) { diagnostic = $"Coincident river stations at {i}."; return false; }
                    arc[i] = arc[i - 1] + length;
                }
                if (!TryExtent(p, Normal(course, i), bed[i] + depth[i], requested[i], ground, out limits[i]))
                { diagnostic = $"Missing ground, dry centre or a bank narrower than 2 cm at station {i}."; return false; }
                if (i > 0 && i + 1 < count)
                {
                    Vector2 before = XZ(p - course[i - 1]).normalized, after = XZ(course[i + 1] - p).normalized;
                    float dot = Vector2.Dot(before, after);
                    if (dot < -0.9f) { diagnostic = $"Unsupported reversing bend at station {i}."; return false; }
                    float sine = Mathf.Abs(before.x * after.y - before.y * after.x);
                    if (sine > 1e-4f)
                        limits[i] = Mathf.Min(limits[i], 0.4f * Mathf.Min(XZ(p - course[i - 1]).magnitude,
                            XZ(course[i + 1] - p).magnitude) / sine);
                }
            }
            for (int i = 0; i + 1 < count; i++)
            {
                for (int k = 1; k < 4; k++)
                {
                    float t = k * 0.25f;
                    Vector3 p = Vector3.Lerp(course[i], course[i + 1], t);
                    Vector2 normal = Vector2.Lerp(Normal(course, i), Normal(course, i + 1), t).normalized;
                    float waterline = Mathf.Lerp(bed[i] + depth[i], bed[i + 1] + depth[i + 1], t);
                    if (!TryExtent(p, normal, waterline, Mathf.Lerp(requested[i], requested[i + 1], t), ground, out float extent))
                    { diagnostic = $"Discontinuous ground or dry river span after station {i}."; return false; }
                    // Conservative endpoint clamp makes the interpolated strip no wider than the sample.
                    limits[i] = Mathf.Min(limits[i], extent); limits[i + 1] = Mathf.Min(limits[i + 1], extent);
                }
            }
            var fit = (float[])limits.Clone();
            for (int pass = 0; pass < 2; pass++)
            {
                var previous = (float[])fit.Clone();
                for (int i = 1; i + 1 < count; i++) fit[i] = 0.25f * previous[i - 1] + 0.5f * previous[i] + 0.25f * previous[i + 1];
            }
            for (int i = 0; i < count; i++)
            {
                fit[i] = Mathf.Min(fit[i], limits[i], requested[i]); // Final clamp, after every smoothing pass.
                if (fit[i] < MinimumSupportedHalfWidthM)
                { diagnostic = $"River section is too narrow to represent at station {i}."; return false; }
            }
            // At most ~2 million nonlocal pairs. Nearby stations share the same ribbon;
            // distant branches must not overlap, even when their centrelines do not cross.
            for (int i = 0; i + 1 < count; i++) for (int j = i + 2; j + 1 < count; j++)
            {
                float width = Mathf.Max(fit[i], fit[i + 1]) + Mathf.Max(fit[j], fit[j + 1]);
                if (arc[j] - arc[i + 1] <= width * 2) continue;
                if (SegmentDistance(XZ(course[i]), XZ(course[i + 1]), XZ(course[j]), XZ(course[j + 1])) < width)
                { diagnostic = $"River branches overlap between spans {i} and {j}; edit the authored route."; return false; }
            }
            fitted = fit; return true;
        }

        internal static Vector2 Normal(IList<Vector3> course, int index)
        {
            // UC-T04-06: a unit-tangent bisector does not lean toward the longer adjacent span.
            Vector2 before = index > 0 ? XZ(course[index] - course[index - 1]).normalized : Vector2.zero;
            Vector2 after = index + 1 < course.Count ? XZ(course[index + 1] - course[index]).normalized : Vector2.zero;
            Vector2 tangent = (before + after).normalized;
            return new Vector2(-tangent.y, tangent.x);
        }

        private static bool TryExtent(Vector3 centre, Vector2 normal, float level, float maxWidth, GroundSample ground, out float extent)
        {
            extent = maxWidth;
            if (!ground(centre.x, centre.z, out float centreY) || !Finite(centreY) || centreY >= level - 0.0001f) return false;
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                float side = sideIndex == 0 ? -1f : 1f;
                float wet = 0;
                for (int k = 1; k <= CrossSamples; k++)
                {
                    float distance = maxWidth * k / CrossSamples;
                    if (!ground(centre.x + normal.x * side * distance, centre.z + normal.y * side * distance, out float height) || !Finite(height)) return false;
                    if (height >= level)
                    {
                        float dry = distance;
                        for (int refine = 0; refine < 8; refine++)
                        {
                            float middle = (wet + dry) * 0.5f;
                            if (!ground(centre.x + normal.x * side * middle, centre.z + normal.y * side * middle, out height) || !Finite(height)) return false;
                            if (height < level) wet = middle; else dry = middle;
                        }
                        break;
                    }
                    wet = distance;
                }
                extent = Mathf.Min(extent, wet);
            }
            return extent >= MinimumSupportedHalfWidthM;
        }

        private static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static float PointDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            return Vector2.Distance(p, a + d * Mathf.Clamp01(Vector2.Dot(p - a, d) / Mathf.Max(1e-12f, d.sqrMagnitude)));
        }
        private static float SegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 ab = b - a, cd = d - c; float denominator = Cross(ab, cd);
            if (Mathf.Abs(denominator) > 1e-8f)
            {
                float t = Cross(c - a, cd) / denominator, u = Cross(c - a, ab) / denominator;
                if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return 0;
            }
            return Mathf.Min(PointDistance(a, c, d), PointDistance(b, c, d), PointDistance(c, a, b), PointDistance(d, a, b));
        }
    }
}
