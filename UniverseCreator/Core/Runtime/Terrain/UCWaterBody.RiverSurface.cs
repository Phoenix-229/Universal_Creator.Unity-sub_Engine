// UC-T04-04: river meshes follow validated cross-sections instead of overflowing a coarse wet grid.
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWaterBody
    {
        private WaterBodyData BuildFittedRiverSurface(Terrain[] terrains)
        {
            WaterCarveLayer fit = riverSurfaceFit;
            if (fit == null || riverShoreline == null || riverShoreline.Course.Count != fit.courseWorld.Count) return null;
            const int Across = 8;
            int count = fit.courseWorld.Count, vertices = count * (Across + 1);
            var body = new WaterBodyData
            {
                vertices = new Vector3[vertices], normals = new Vector3[vertices], tangents = new Vector4[vertices],
                uv = new Vector2[vertices], colors = new Color[vertices], triangles = new int[(count - 1) * Across * 6]
            };
            var positions = new Vector3[vertices];
            var world = CarveWorld; bool read = FollowTrench;
            bool Ground(float x, float z, out float y)
            {
                y = 0;
                return read ? world != null && RiverChannelRegistry.TrySampleGround(riverFitRun.WorldId, x, z, riverFitRun.Revision, out y, out var run) && run.Equals(riverFitRun)
                    : TrySampleSurfaceGround(terrains, world, x, z, out y);
            }
            float sum = 0, deepest = 0;
            Vector2 flowSum = Vector2.zero;
            // UC-T04-08: sample the same inherited authored payload along the fitted
            // course, once per build; bends must not turn every reach into a rapid.
            var authored = riverProfile?.Bake(outline);
            float totalArc = 0, stationArc = 0;
            for (int i = 1; i < count; i++) totalArc += Vector3.Distance(fit.courseWorld[i - 1], fit.courseWorld[i]);
            for (int i = 0; i < count; i++)
            {
                Vector2 normal = RiverCrossSectionFit.Normal(fit.courseWorld, i), direction = new Vector2(normal.y, -normal.x);
                float level = fit.riverBedY[i] + fit.riverDepthM[i];
                // UC-T04-06: the two banks need not be symmetric about the authored centreline.
                float left = riverShoreline.Left[i], right = riverShoreline.Right[i], width = left + right;
                if (i > 0) stationArc += Vector3.Distance(fit.courseWorld[i - 1], fit.courseWorld[i]);
                var hydraulic = authored != null ? authored.SampleAtArc(totalArc > 0 ? stationArc / totalArc * authored.TotalLengthM : 0) : default;
                float speed = authored != null && authored.HasVelocity ? hydraulic.velocityMps
                    : authored != null && authored.HasDischarge ? hydraulic.dischargeM3PerS / Mathf.Max(0.01f, width * fit.riverDepthM[i])
                    : water.flowMetresPerSecond.magnitude;
                speed = RiverChannelSnapshot.Finite(speed) ? Mathf.Clamp(speed, 0, Mathf.Max(0.01f, maxFlowSpeed)) : 0;
                sum += level; flowSum += direction * speed;
                for (int k = 0; k <= Across; k++)
                {
                    int v = i * (Across + 1) + k;
                    float lateral = Mathf.Lerp(-left, right, (float)k / Across);
                    Vector3 p = fit.courseWorld[i] + new Vector3(normal.x * lateral, 0, normal.y * lateral); p.y = level;
                    if (!Ground(p.x, p.z, out float ground) || ground > level + 0.0001f)
                    { RiverSurfaceDiagnostic = $"River vertex leaves accepted wet ground at station {i}."; riverSurfaceFit = null; return null; }
                    float depth = Mathf.Max(0, level - ground);
                    positions[v] = p; body.vertices[v] = transform.InverseTransformPoint(p);
                    body.normals[v] = transform.InverseTransformDirection(Vector3.up).normalized;
                    Vector3 tangent = transform.InverseTransformDirection(Vector3.right).normalized;
                    body.tangents[v] = new Vector4(tangent.x, tangent.y, tangent.z, -1);
                    body.uv[v] = new Vector2(0, depth);
                    float edge = Mathf.Min(k, Across - k) * width / Across;
                    body.colors[v] = new Color(direction.x * 0.5f + 0.5f, direction.y * 0.5f + 0.5f, speed / Mathf.Max(0.01f, maxFlowSpeed),
                        Mathf.SmoothStep(0, 1, Mathf.Clamp01(Mathf.Min(edge, depth) / Mathf.Max(0.01f, edgeFeatherMetres))));
                    deepest = Mathf.Max(deepest, depth);
                    if (v == 0) body.worldBounds = new Bounds(p, Vector3.zero); else body.worldBounds.Encapsulate(p);
                }
            }
            int index = 0;
            for (int i = 0; i + 1 < count; i++) for (int k = 0; k < Across; k++)
            {
                int a = i * (Across + 1) + k, b = a + 1, c = a + Across + 1, d = c + 1;
                if (!Triangle(a, b, c) || !Triangle(b, d, c))
                { RiverSurfaceDiagnostic = $"River surface folds or bridges dry/missing ground after station {i}."; riverSurfaceFit = null; return null; }
            }
            bool Triangle(int a, int b, int c)
            {
                Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
                if (Vector3.Cross(pb - pa, pc - pa).y <= 1e-8f) return false;
                // Vertex containment alone misses a dry wedge between sections. Check the
                // triangle centre and every edge midpoint before replacing a native mesh.
                for (int sample = 0; sample < 4; sample++)
                {
                    Vector3 p = sample == 0 ? (pa + pb + pc) / 3 : sample == 1 ? (pa + pb) / 2 : sample == 2 ? (pb + pc) / 2 : (pc + pa) / 2;
                    if (!Ground(p.x, p.z, out float ground) || ground > p.y + 0.0001f) return false;
                }
                body.triangles[index++] = a; body.triangles[index++] = b; body.triangles[index++] = c; return true;
            }
            body.surfaceLevel = sum / count; body.maxDepth = deepest; body.meanFlow = flowSum / count;
            // UC-T07-11: real fitted banks, with water on the left in world XZ. Inlet/outlet cross-sections are not shores.
            body.shorelineSegmentsLocal = new Vector3[(count - 1) * 4];
            for (int i = 0; i < count - 1; i++)
            {
                body.shorelineSegmentsLocal[i * 4] = body.vertices[i * (Across + 1)];
                body.shorelineSegmentsLocal[i * 4 + 1] = body.vertices[(i + 1) * (Across + 1)];
                body.shorelineSegmentsLocal[i * 4 + 2] = body.vertices[(i + 1) * (Across + 1) + Across];
                body.shorelineSegmentsLocal[i * 4 + 3] = body.vertices[i * (Across + 1) + Across];
            }
            return body;
        }
    }
}
