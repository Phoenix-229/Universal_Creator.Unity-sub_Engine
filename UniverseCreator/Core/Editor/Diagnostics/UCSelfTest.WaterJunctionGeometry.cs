// UC-T04-06: real receiver ownership, surface union and sloping shared-boundary acceptance.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWaterJunctionGeometry()
        {
            var incoming = JunctionQuad(-1, 1, 0, 10, false);
            var receiver = JunctionQuad(-3, 3, 5, 15, true);
            var original = (Vector3[])incoming.vertices.Clone();
            bool success = WaterJunctionMesh.TryJoin(incoming, Matrix4x4.identity, receiver, Matrix4x4.identity, 1,
                out var joined, out var diagnostic);
            double area = 0; int boundary = 0; bool continuous = success;
            if (success)
            {
                foreach (var p in joined.vertices)
                {
                    continuous &= p.z <= 5.00001f;
                    if (Mathf.Abs(p.z - 5) < 0.00001f)
                    { boundary++; continuous &= Mathf.Abs(p.y - (0.02f * p.x + 0.03f * p.z)) < 0.00001f; }
                }
                for (int i = 0; i < joined.triangles.Length; i += 3)
                {
                    var a = joined.vertices[joined.triangles[i]]; var b = joined.vertices[joined.triangles[i + 1]];
                    var c = joined.vertices[joined.triangles[i + 2]];
                    double triangle = Vector3.Cross(b - a, c - a).y * 0.5;
                    continuous &= triangle > 0; area += triangle;
                }
            }
            Check(continuous && boundary > 0 && Math.Abs(area - 10) < 0.0001,
                "UC-T04-06 receiver owns all overlap and the complete clipped boundary matches its sloping surface.");
            Check(Array.TrueForAll(original, p => Array.IndexOf(incoming.vertices, p) >= 0) && incoming.vertices.Length == 4,
                "UC-T04-06 junction preparation leaves borrowed source geometry unchanged.");
            Check(!WaterJunctionMesh.TryJoin(incoming, Matrix4x4.identity, receiver, Matrix4x4.identity, 0.001f,
                out _, out diagnostic) && diagnostic.Contains("clearance"),
                "UC-T04-06 insufficient shared-level clearance fails without publishing hanging geometry.");
            Check(!WaterJunctionMesh.TryJoin(incoming, Matrix4x4.identity, receiver, Matrix4x4.Translate(new Vector3(0, 0, 30)), 1,
                out _, out diagnostic) && diagnostic.Contains("boundary"),
                "UC-T04-06 a nearby receiver without intersecting triangles cannot accept a junction.");
            var offset = Matrix4x4.Translate(new Vector3(120, 30, -80));
            Check(WaterJunctionMesh.TryJoin(incoming, offset, receiver, offset, 1, out joined, out _) &&
                Array.TrueForAll(joined.vertices, p => p.z <= 5.00001f),
                "UC-T04-06 copied local meshes connect correctly after world-space transforms.");
            TestGroupedWaterJunctionGeometry();
        }

        private static WaterBodyData JunctionQuad(float minX, float maxX, float minZ, float maxZ, bool slope)
        {
            var points = new[] { new Vector3(maxX, 0, minZ), new Vector3(minX, 0, minZ),
                new Vector3(maxX, 0, maxZ), new Vector3(minX, 0, maxZ) };
            var data = new WaterBodyData { vertices = points, triangles = new[] { 0, 1, 2, 1, 3, 2 },
                normals = new Vector3[4], tangents = new Vector4[4], uv = new Vector2[4], colors = new Color[4] };
            for (int i = 0; i < points.Length; i++)
            {
                if (slope) points[i].y = 0.02f * points[i].x + 0.03f * points[i].z;
                data.normals[i] = Vector3.up; data.tangents[i] = new Vector4(1, 0, 0, -1);
                data.uv[i] = new Vector2(0, 1); data.colors[i] = Color.white;
            }
            return data;
        }

        // UC-T04-06: independent separating-axis oracle tests positive-area overlap of actual accepted triangles; touching edges are allowed within 0.01 mm.
        private static bool JunctionMeshesOverlap(UCWaterBody first, UCWaterBody second)
        {
            var left = first.LastBuild; var right = second.LastBuild;
            for (int i = 0; i < left.triangles.Length; i += 3)
            {
                Vector3 a = first.transform.TransformPoint(left.vertices[left.triangles[i]]);
                Vector3 b = first.transform.TransformPoint(left.vertices[left.triangles[i + 1]]);
                Vector3 c = first.transform.TransformPoint(left.vertices[left.triangles[i + 2]]);
                for (int j = 0; j < right.triangles.Length; j += 3)
                {
                    Vector3 d = second.transform.TransformPoint(right.vertices[right.triangles[j]]);
                    Vector3 e = second.transform.TransformPoint(right.vertices[right.triangles[j + 1]]);
                    Vector3 f = second.transform.TransformPoint(right.vertices[right.triangles[j + 2]]);
                    bool separated = false;
                    for (int edge = 0; edge < 6; edge++)
                    {
                        Vector3 start = edge == 0 ? a : edge == 1 ? b : edge == 2 ? c : edge == 3 ? d : edge == 4 ? e : f;
                        Vector3 end = edge == 0 ? b : edge == 1 ? c : edge == 2 ? a : edge == 3 ? e : edge == 4 ? f : d;
                        double nx = (double)end.z - start.z, nz = (double)start.x - end.x;
                        double pa = a.x * nx + a.z * nz, pb = b.x * nx + b.z * nz, pc = c.x * nx + c.z * nz;
                        double pd = d.x * nx + d.z * nz, pe = e.x * nx + e.z * nz, pf = f.x * nx + f.z * nz;
                        double tolerance = 0.00001 * Math.Sqrt(nx * nx + nz * nz);
                        if (Math.Max(pa, Math.Max(pb, pc)) <= Math.Min(pd, Math.Min(pe, pf)) + tolerance ||
                            Math.Max(pd, Math.Max(pe, pf)) <= Math.Min(pa, Math.Min(pb, pc)) + tolerance)
                        { separated = true; break; }
                    }
                    if (!separated) return true;
                }
            }
            return false;
        }

        private static void TestGroupedWaterJunctionGeometry()
        {
            var root = new GameObject("UC differently grouped junctions"); root.SetActive(false);
            var native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
            GameObject terrainRoot = null; UCWaterBody river = null, lake = null;
            try
            {
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.4f + Mathf.Min(0.2f, Mathf.Abs(x - 16) * 0.05f);
                native.SetHeights(0, 0, heights); heights = native.GetHeights(0, 0, 33, 33); // UC-T04-06: preserve the exact native baseline, including quantization.
                terrainRoot = Terrain.CreateTerrainGameObject(native);
                var riverGroup = new GameObject("River group"); riverGroup.transform.SetParent(root.transform, false);
                var lakeGroup = new GameObject("Lake group"); lakeGroup.transform.SetParent(root.transform, false);
                lake = JunctionLevelBody(lakeGroup.transform, "Accepted receiving lake", new Vector3(16, 49, 24), WaterBodyKind.Lake);
                lake.digTerrain = false; lake.generateVolume = false; lake.bedSource = UCWaterBody.BedSource.FlatFloor; lake.meshResolution = 12;
                lake.RebuildSurface();
                river = JunctionRiverBody(riverGroup.transform, "Incoming river", new Vector3(16, 49, 6), new Vector3(16, 49, 24));
                river.digTerrain = false; river.generateVolume = false;
                var nodes = river.OwnedRiverNodes(); var source = nodes[0].transform.position; var mouth = nodes[1].transform.position;
                river.BuildDig(false); river.RebuildSurface();
                var records = river.GetWaterJunctionRecords(); bool contained = river.LastBuild != null && !JunctionMeshesOverlap(river, lake); // UC-T04-06: compare actual conformed receiver coverage rather than a nominal rectangle edge.
                Check(contained && records.Length == 1 && records[0].GeometryAccepted && records[0].ToBodyId == lake.StableWaterBodyId,
                    "UC-T04-06 production joins separately grouped bodies and accepts only receiver-subtracted native geometry. " + river.RiverSurfaceDiagnostic + " " + river.WaterJunctionDiagnostic +
                    $" built={river.LastBuild != null}, contained={contained}, records={records.Length}, accepted={(records.Length > 0 && records[0].GeometryAccepted)}");
                Check(nodes[0].transform.position == source && nodes[1].transform.position == mouth &&
                    RiverReadHeightsEqual(heights, native.GetHeights(0, 0, 33, 33)),
                    "UC-T04-06 automatic surface joining preserves authored nodes and non-excavating native terrain.");
                lake.enabled = false;
                Check(UCWaterBody.GetWaterRebuildOrder(new[] { river }).Length == 0 && river.LastBuild == null &&
                    river.GetWaterJunctionRecords().Length == 1 && !river.GetWaterJunctionRecords()[0].GeometryAccepted &&
                    river.GetWaterJunctionRecords()[0].ToBodyId == lake.StableWaterBodyId,
                    "UC-T04-07 a disabled receiving lake withdraws its incoming surface while retaining the exact saved endpoint identity.");
                lake.enabled = true; lake.RebuildSurface(); river.Rebuild();
                records = river.GetWaterJunctionRecords();
                Check(river.LastBuild != null && records.Length == 1 && records[0].GeometryAccepted && records[0].ToBodyId == lake.StableWaterBodyId,
                    "UC-T04-07 re-enabling the saved receiver restores the real native junction without moving its authored endpoints.");
                lake.Clear(); river.BuildDig(false); river.RebuildSurface();
                Check(river.WaterJunctionDiagnostic.Contains("pending") && Array.TrueForAll(river.GetWaterJunctionRecords(), record => !record.GeometryAccepted),
                    "UC-T04-06 missing receiver geometry remains pending instead of accepting a stacked or hanging replacement.");
                var spring = JunctionRiverBody(riverGroup.transform, "Unfed manual spring", new Vector3(16, 40, 6), new Vector3(16, 40, 14)); // UC-T04-06: end outside the unrelated lake's shoreline slack.
                try
                {
                    spring.generateVolume = false; spring.BuildDig(false);
                    var layer = spring.CarveLayer;
                    Check(layer != null && layer.riverDepthM != null && layer.riverDepthM.Length > 1 &&
                        layer.riverDepthM[0] <= 0.02001f && layer.riverDepthM[layer.riverDepthM.Length - 1] > layer.riverDepthM[0] &&
                        layer.riverHalfWidth[0] <= 0.15001f && spring.OwnedRiverNodes()[0].transform.position == new Vector3(16, 40, 6),
                        "UC-T04-06 manual headwaters introduce shallow narrow geometry while keeping the clicked source position. " + spring.RiverSurfaceDiagnostic);
                    spring.OwnedRiverNodes()[1].transform.position = new Vector3(16, 40, 16);
                    spring.InvalidatePendingWaterAuthoring(); spring.BuildDig(false);
                    Check(spring.CarveLayer == null && spring.RiverSurfaceDiagnostic.Contains("uphill") &&
                        spring.OwnedRiverNodes()[0].transform.position == new Vector3(16, 40, 6),
                        "UC-T04-06 an actually uphill receiving lake rejects excavation without relocating the authored source.");
                }
                finally { RiverCourseInputNode.UnpublishBodyCourse(spring.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(spring); spring.Clear(); }
            }
            finally
            {
                foreach (var body in new[] { river, lake }) if (body != null)
                { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); body.Clear(); }
                if (terrainRoot != null) UnityEngine.Object.DestroyImmediate(terrainRoot);
                UnityEngine.Object.DestroyImmediate(native); UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
