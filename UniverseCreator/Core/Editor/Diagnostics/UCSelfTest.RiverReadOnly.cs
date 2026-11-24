// UC-T04-02: accepted read profiles cannot enter native or graph excavation paths.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private sealed class ReadProfileCarver : IUCTerrainCarver
        {
            public UniverseCreatorObject CarveWorld { get; set; }
            public WaterCarveLayer CarveLayer { get; set; }
        }

        private static void TestRiverReadOnlyOwnership()
        {
            var root = new GameObject("UC read-mode native owner"); root.SetActive(false);
            var foreignRoot = new GameObject("UC read-mode foreign owner"); foreignRoot.SetActive(false);
            TerrainData native = null; Texture2D texture = null; var tiles = new List<TileData>();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.maxHeightMeters = 100;
                var foreign = foreignRoot.AddComponent<UniverseCreatorObject>(); foreign.maxHeightMeters = 100;
                native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
                var bounds = new Bounds(new Vector3(16, 50, 16), new Vector3(32, 100, 32));
                var tile = CommittedProfileTile(world); tiles.Add(tile);
                TerrainBuilder.ApplyOutputs(native, tile.outputs, world, null, bounds, 1, null);
                texture = new Texture2D(2, 2); texture.Apply();
                native.SetDetailResolution(32, 8);
                native.detailPrototypes = new[] { new DetailPrototype { prototypeTexture = texture,
                    minWidth = 1, maxWidth = 1, minHeight = 1, maxHeight = 1, renderMode = DetailRenderMode.GrassBillboard } };
                var details = new int[32, 32]; details[3, 5] = 2; native.SetDetailLayer(0, 0, 0, details);
                bool[,] holes = native.GetHoles(0, 0, 32, 32); holes[2, 2] = false; native.SetHoles(0, 0, holes);
                float[,] baseline = native.GetHeights(0, 0, 33, 33);
                foreach (int dimensions in new[] { 0, 1, 2, 3, 4 })
                {
                    var bodyRoot = new GameObject("UC read dimension fixture"); bodyRoot.SetActive(false); bodyRoot.transform.SetParent(root.transform, false);
                    UCWaterBody body = null;
                    try
                    {
                        body = bodyRoot.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River);
                        body.shape = UCWaterBody.ShapeMode.RiverSpline; body.meander = 0; body.ruggedness = 0;
                        var a = RiverModePoint(bodyRoot.transform, new Vector3(16, 0, 18), false);
                        var b = RiverModePoint(bodyRoot.transform, new Vector3(16, 0, 26), false);
                        foreach (var point in new[] { a, b })
                        {
                            point.halfWidthM = dimensions == 1 || dimensions == 4 ? 40 : 0;
                            point.depthM = dimensions == 2 || dimensions == 4 ? 25 : 0;
                            point.dischargeM3PerS = dimensions == 3 || dimensions == 4 ? 1000 : 0;
                            point.velocityMps = 0;
                        }
                        body.SetRiverTerrainMode(RiverTerrainMode.ReadProceduralTrench); WaterCarveRegistry.Register(body);
                        body.BuildDig(false);
                        bool fitted = body.TryGetFittedRiverProfile(out var course, out var bed, out var depth, out var width);
                        bool accepted = fitted && body.LastChannelValidity == RiverChannelValidity.Valid;
                        if (fitted)
                            for (int i = 0; i < course.Length; i++)
                                accepted &= RiverChannelRegistry.TrySampleCommitted(world, course[i].x, course[i].z, 0, out var sample, out _) &&
                                    Mathf.Abs(bed[i] - sample.Bed.y) < 1e-4f && Mathf.Abs(bed[i] + depth[i] - sample.WaterSurfaceY) < 1e-4f;
                        Check(accepted && body.CarveLayer == null && !body.TryGetSolvedCourse(1, out _, out _, out _, out _),
                            $"UC-T04-02 read dimensions={dimensions} use accepted bed/level and expose no native or graph excavation request.");
                        var staged = (float[,])baseline.Clone();
                        Check(WaterCarveRegistry.CountAffecting(world, bounds) == 0 && WaterCarveRegistry.ApplyAll(world, staged, bounds, 1) == 0,
                            "UC-T04-02 a registered read body is never counted or applied as a terrain excavator.");
                        body.BuildDig(); body.ReapplyToTerrain();
                        Check(RiverReadHeightsEqual(staged, baseline) && RiverReadHeightsEqual(native.GetHeights(0, 0, 33, 33), baseline) &&
                            !native.GetHoles(2, 2, 1, 1)[0, 0] && native.GetDetailLayer(5, 3, 1, 1, 0)[0, 0] == 2,
                            "UC-T04-02 read Build/refit preserve nonempty native heights, holes and detail counts exactly.");
                        if (fitted)
                        {
                            bed[0] += 100;
                            body.TryGetFittedRiverProfile(out _, out var unchanged, out _, out _);
                            Check(unchanged[0] != bed[0], "UC-T04-02 surface profile copies cannot mutate the body's accepted fit.");
                        }
                        b.transform.localPosition = new Vector3(16, 0, 60); body.BuildDig(false);
                        Check(body.CarveLayer == null && !body.TryGetFittedRiverProfile(out _, out _, out _, out _) &&
                            body.LastChannelValidity != RiverChannelValidity.Valid,
                            "UC-T04-02 partial committed coverage clears the fit and cannot fall back to excavation.");
                        bodyRoot.transform.SetParent(foreignRoot.transform, false); b.transform.localPosition = new Vector3(16, 0, 26);
                        body.BuildDig(false);
                        Check(body.LastChannelValidity == RiverChannelValidity.NoCommittedWorld && body.CarveLayer == null &&
                            !body.TryGetFittedRiverProfile(out _, out _, out _, out _), "UC-T04-02 a foreign owner cannot read or carve another world's accepted trench.");
                    }
                    finally
                    {
                        if (body != null) { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); }
                        UnityEngine.Object.DestroyImmediate(bodyRoot);
                    }
                }
                TestReadLayerDirectApplication(world, bounds, baseline);
                TestReadModeRejectsPendingCarve(world);
            }
            finally
            {
                foreach (var tile in tiles) tile.ReleaseProductMemory();
                RiverChannelRegistry.Withdraw(root.GetComponent<UniverseCreatorObject>().GenerationScheduler.CurrentRun.WorldId);
                RiverChannelRegistry.Withdraw(foreignRoot.GetComponent<UniverseCreatorObject>().GenerationScheduler.CurrentRun.WorldId);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(foreignRoot);
                if (native != null) UnityEngine.Object.DestroyImmediate(native);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static bool RiverReadHeightsEqual(float[,] a, float[,] b)
        {
            for (int z = 0; z < a.GetLength(0); z++) for (int x = 0; x < a.GetLength(1); x++) if (a[z, x] != b[z, x]) return false;
            return true;
        }

        private static void TestReadLayerDirectApplication(UniverseCreatorObject world, Bounds bounds, float[,] baseline)
        {
            var read = new WaterCarveLayer { followOnly = true, enabled = true, isRiver = false,
                ringPoints = new List<Vector3> { Vector3.zero, new Vector3(32, 0, 0), new Vector3(32, 0, 32), new Vector3(0, 0, 32) } };
            read.RecomputeBounds();
            var carver = new ReadProfileCarver { CarveWorld = world, CarveLayer = read }; WaterCarveRegistry.Register(carver);
            try
            {
                var copied = (float[,])baseline.Clone(); read.ApplyTo(copied, bounds, 1, 100);
                Check(read.HeightAt(16, 16, 40) == 40 && RiverReadHeightsEqual(copied, baseline) &&
                    WaterCarveRegistry.ApplyAll(world, copied, bounds, 1) == 0 && WaterCarveRegistry.CountAffecting(world, bounds) == 0,
                    "UC-T04-02 direct and external registry application cannot bypass read authority by supplying a ring/corridor request.");
            }
            finally { WaterCarveRegistry.Unregister(carver); }
        }

        private static void TestReadModeRejectsPendingCarve(UniverseCreatorObject world)
        {
            var root = new GameObject("UC mode-switch pending request"); root.SetActive(false); root.transform.SetParent(world.transform, false);
            try
            {
                var body = root.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                RiverModePoint(root.transform, new Vector3(16, 0, 18), false); RiverModePoint(root.transform, new Vector3(16, 0, 26), false);
                body.EnsureRiverTerrainMode(); GenerationRunId frozen = world.GenerationScheduler.CurrentRun;
                body.BuildDig(false);
                Check(body.CarveLayer != null, "UC-T04-02 the mode-switch fixture begins with its own manual excavation request.");
                body.SetRiverTerrainMode(RiverTerrainMode.ReadProceduralTrench);
                bool published = false;
                Check(body.CarveLayer == null && !body.TryGetSolvedCourse(1, out _, out _, out _, out _) &&
                    !world.GenerationScheduler.TryPublish(frozen, () => published = true) && !published,
                    "UC-T04-02 mode switching withdraws the old cut and rejects frozen workers carrying its previous graph excavation.");
                RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId); body.BuildDig(false);
                Check(body.LastChannelValidity == RiverChannelValidity.NoCommittedWorld && body.CarveLayer == null &&
                    !body.TryGetFittedRiverProfile(out _, out _, out _, out _), "UC-T04-02 missing/withdrawn committed data cannot enable a read-mode fallback cut.");
            }
            finally
            {
                var body = root.GetComponent<UCWaterBody>();
                if (body != null) RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode());
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
