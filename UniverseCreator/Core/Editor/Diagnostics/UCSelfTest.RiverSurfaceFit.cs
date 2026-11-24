// UC-T04-04: actual wet-bank limits, thin sections, missing spans and accepted native surface preservation.
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestRiverSurfaceFitting()
        {
            var course = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(0, 0, 5), new Vector3(0, 0, 10) };
            var bed = new float[3]; var depth = new[] { 1f, 1f, 1f }; var requested = new[] { 2f, 2f, 2f };
            bool Banks(float x, float z, out float y) { float bankHalfWidth = 0.08f + Mathf.Abs(z - 5) * 0.2f; y = Mathf.Abs(x) / bankHalfWidth; return true; }
            bool fitted = RiverCrossSectionFit.TryFit(course, bed, depth, requested, Banks, out var width, out _);
            Check(fitted && width[1] <= 0.08f && width[1] > 0.01f && requested[1] == 2,
                "UC-T04-04 smoothing cannot widen a thin wet bank or modify the excavation width; no invented 15 cm floor.");
            bool Slope(float x, float z, out float y) { y = Mathf.Abs(x) * (x < 0 ? 4 : 1); return true; }
            Check(RiverCrossSectionFit.TryFit(course, bed, depth, requested, Slope, out width, out _) && width[1] <= 0.25f,
                "UC-T04-04 steep asymmetric cross-slopes use the narrower of both wetted banks.");
            bool Missing(float x, float z, out float y) { y = 0; return z < 1 || z > 4; }
            Check(!RiverCrossSectionFit.TryFit(course, bed, depth, requested, Missing, out _, out var diagnostic) && diagnostic.Length > 0,
                "UC-T04-04 missing coverage between valid endpoints rejects the whole replacement with a diagnostic.");
            bool Dry(float x, float z, out float y) { y = 2; return true; }
            Check(!RiverCrossSectionFit.TryFit(course, bed, depth, requested, Dry, out _, out _),
                "UC-T04-04 a dry centre cannot be replaced by a minimum-width floating sheet.");
            bool Flat(float x, float z, out float y) { y = 0; return true; }
            var crossing = new List<Vector3> { new Vector3(-10, 0, -10), new Vector3(10, 0, 10), new Vector3(-10, 0, 10), new Vector3(10, 0, -10) };
            Check(!RiverCrossSectionFit.TryFit(crossing, new float[4], new[] { 1f, 1f, 1f, 1f }, new[] { 1f, 1f, 1f, 1f }, Flat, out _, out diagnostic) && diagnostic.Contains("overlap"),
                "UC-T04-04 nonlocal self-overlap fails rather than layering two transparent river sheets.");
            var reversal = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 5), new Vector3(0.1f, 0, 0) };
            Check(!RiverCrossSectionFit.TryFit(reversal, bed, depth, requested, Flat, out _, out diagnostic) && diagnostic.Contains("bend"),
                "UC-T04-04 an unsupported reversing bend reports a route error before producing folded geometry.");
            TestRiverSurfaceWithoutExcavation();
            TestManualSurfaceWithoutExcavation();
        }

        private static void TestManualSurfaceWithoutExcavation()
        {
            var root = new GameObject("UC manual non-excavating surface"); root.SetActive(false);
            TerrainData native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
            GameObject terrainRoot = null; UCWaterBody body = null;
            try
            {
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.4f + Mathf.Min(0.1f, Mathf.Abs(x - 16) * 0.025f);
                native.SetHeights(0, 0, heights);
                heights = native.GetHeights(0, 0, 33, 33); // UC-T04-04: unchanged native heights are compared after Unity quantization.
                terrainRoot = Terrain.CreateTerrainGameObject(native);
                body = root.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                body.meander = 0; body.ruggedness = 0; body.digTerrain = false;
                RiverModePoint(root.transform, new Vector3(16, 49, 6), false);
                RiverModePoint(root.transform, new Vector3(16, 49, 26), false);
                body.EnsureRiverTerrainMode(); body.BuildDig(false);
                Check(body.BuildData() != null && body.CarveLayer == null && body.TryGetFittedRiverProfile(out _, out _, out _, out _) &&
                    RiverReadHeightsEqual(heights, native.GetHeights(0, 0, 33, 33)),
                    "UC-T04-04 manual surface fitting uses its authored water level with digging disabled and leaves native heights unchanged. " + body.RiverSurfaceDiagnostic + " NativeHole=" + native.IsHole(16, 16));
            }
            finally
            {
                if (body != null) { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); body.Clear(); }
                if (terrainRoot != null) Object.DestroyImmediate(terrainRoot);
                Object.DestroyImmediate(root); Object.DestroyImmediate(native);
            }
        }

        private static void TestRiverSurfaceWithoutExcavation()
        {
            var root = new GameObject("UC fitted river surface owner"); root.SetActive(false);
            TileData tile = null; UCWaterBody body = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.maxHeightMeters = 100;
                tile = CommittedProfileTile(world); CommitPublicationTestTile(world, tile);
                var child = new GameObject("Read surface with Dig Terrain disabled"); child.transform.SetParent(root.transform, false);
                body = child.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                body.meander = 0; body.ruggedness = 0; body.digTerrain = false;
                RiverModePoint(child.transform, new Vector3(16, 0, 18), false);
                var end = RiverModePoint(child.transform, new Vector3(16, 0, 26), false);
                body.SetRiverTerrainMode(RiverTerrainMode.ReadProceduralTrench); body.BuildDig(false);
                var data = body.BuildData();
                bool contained = data != null && body.CarveLayer == null && body.TryGetFittedRiverProfile(out _, out _, out _, out _);
                if (data != null) foreach (var vertex in data.vertices)
                {
                    Vector3 p = body.transform.TransformPoint(vertex);
                    contained &= RiverChannelRegistry.TrySampleGround(world, p.x, p.z, out float ground, out _) && ground <= p.y + 1e-4f;
                }
                Check(contained, "UC-T04-04 excavation-disabled read mode fits a real surface and every vertex stays on accepted wet ground.");
                body.RebuildSurface(); var accepted = body.LastBuild;
                var mesh = child.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
                end.transform.localPosition = new Vector3(16, 0, 60); body.BuildDig(false); body.RebuildSurface();
                Check(accepted != null && ReferenceEquals(accepted, body.LastBuild) && mesh != null &&
                    child.GetComponentInChildren<MeshFilter>(true)?.sharedMesh == mesh && body.RiverSurfaceDiagnostic.Length > 0,
                    "UC-T04-04 invalid replacement preserves the prior accepted native mesh and exposes the missing-ground diagnostic.");
            }
            finally
            {
                if (body != null) { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); body.Clear(); WaterCarveRegistry.Unregister(body); }
                tile?.ReleaseProductMemory(); RiverChannelRegistry.Withdraw(root.GetComponent<UniverseCreatorObject>().GenerationScheduler.CurrentRun.WorldId);
                Object.DestroyImmediate(root);
            }
        }
    }
}
