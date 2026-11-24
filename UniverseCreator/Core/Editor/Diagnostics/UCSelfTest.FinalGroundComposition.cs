// UC-T03-10: final height, normals, native holes, collision and environment sampling share one accepted surface.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestFinalGroundComposition()
        {
            var root = new GameObject("UC final ground owner"); root.SetActive(false); root.transform.position = new Vector3(0, 20, 0);
            var terrainRoot = new GameObject("UC final ground native terrain"); terrainRoot.transform.position = root.transform.position;
            TerrainData data = null; TileData tile = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.maxHeightMeters = 100; world.generateCollider = true;
                tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), root.transform.position, new Vector3(32, 100, 32), 100);
                tile.AssignRun(world.GenerationScheduler.CurrentRun); tile.AttachMemory(world.ProductMemory);
                tile.outputs.heightmap = new float[33, 33]; tile.outputs.normals = new Vector3[33 * 33];
                tile.outputs.holes = new bool[32, 32];
                var graphHeight = tile.CreateMatrix();
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++)
                {
                    float height = 0.2f + x * 0.002f + z * 0.003f;
                    tile.outputs.heightmap[z, x] = height; graphHeight.arr[z * 33 + x] = height;
                    tile.outputs.normals[z * 33 + x] = Vector3.up;
                    if (x < 32 && z < 32) tile.outputs.holes[z, x] = true;
                }
                tile.outputs.holes[16, 16] = false; tile.FinishPublications();
                var sculpt = new TerrainOverrideLayer(); sculpt.RecordDelta(8, 8, -0.05f, 1);
                var bounds = new Bounds(new Vector3(16, 70, 16), new Vector3(32, 100, 32));
                data = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
                var terrain = terrainRoot.AddComponent<Terrain>(); terrain.terrainData = data;
                var collider = terrainRoot.AddComponent<TerrainCollider>(); collider.terrainData = data;
                Check(TerrainBuilder.ApplyOutputs(data, tile.outputs, world, sculpt, bounds, 1, null),
                    "UC-T03-10 final height upload requests native collider publication.");
                Check(RiverChannelRegistry.TrySampleGround(world, 8, 8, out float acceptedY, out var run) && run.Equals(tile.RunId) &&
                    Mathf.Abs(acceptedY - (20 + data.GetInterpolatedHeight(0.25f, 0.25f))) < 0.02f &&
                    Mathf.Abs(acceptedY - (20 + (graphHeight.arr[8 * 33 + 8] - 0.05f) * 100)) < 0.02f,
                    "UC-T03-10 accepted ground includes authored sculpt after generated terrain at a translated world origin.");
                Vector3 expectedNormal = new Vector3(-0.2f, 1, -0.3f).normalized;
                Check(Vector3.Dot(tile.outputs.normals[10 * 33 + 12], expectedNormal) > 0.9999f &&
                    Vector3.Dot(tile.outputs.normals[10 * 33 + 12], data.GetInterpolatedNormal(12f / 32, 10f / 32)) > 0.999f,
                    "UC-T03-10 optional normal output is rebuilt from final physical ground and agrees with the native planar normal.");
                Check(!data.GetHoles(16, 16, 1, 1)[0, 0] &&
                    !RiverChannelRegistry.TrySampleGround(world, 16.5f, 16.5f, out _, out _),
                    "UC-T03-10 a native hole is absent from accepted ground sampling instead of returning a ghost surface.");
                Physics.SyncTransforms();
                Check(collider.Raycast(new Ray(new Vector3(8, 200, 8), Vector3.down), out var hit, 300) && Mathf.Abs(hit.point.y - acceptedY) < 0.05f &&
                    !collider.Raycast(new Ray(new Vector3(16.5f, 200, 16.5f), Vector3.down), out _, 300),
                    "UC-T03-10 native collider sampling matches the final sculpt and respects the actual hole.");
                var solve = WorldDrainage.CreateForTests(graphHeight, (float[])graphHeight.arr.Clone());
                var hydro = HydroField.For(solve, 20, tile.RunId);
                var settings = new EcoSampleSettings { seaLevelY = 20, waterSearchRadiusM = 10 };
                var sampler = new EnvironmentSampler(); sampler.Bind(settings, graphHeight, null, null, null, 0, null); sampler.AttachHydrology(hydro);
                Check(Mathf.Abs(sampler.Sample(8, 8).worldPos.y - acceptedY) < 0.02f,
                    "UC-T03-10 environment sampling reads matching accepted sculpted ground rather than its pending graph height.");
                tile.outputs.holes = null; tile.outputs.normalStrength = 0;
                TerrainBuilder.ApplyOutputs(data, tile.outputs, world, null, bounds, 1, null);
                Check(!RiverChannelRegistry.TrySampleGround(world, 16.5f, 16.5f, out _, out _) && tile.outputs.normals[10 * 33 + 12] == Vector3.up,
                    "UC-T03-10 a later height-only commit preserves native holes and authored zero normal strength.");
                tile.outputs.normalStrength = 1;
                TerrainBuilder.ApplyOutputs(data, tile.outputs, world);
                Check(Vector3.Dot(tile.outputs.normals[10 * 33 + 12], expectedNormal) > 0.9999f,
                    "UC-T03-10 synchronous commits without spatial shaping still derive normals from native sample spacing.");
                var first = (float[])graphHeight.arr.Clone(); var second = (float[])graphHeight.arr.Clone();
                var droplets = DropletSettings.Default; droplets.droplets = 128; droplets.coarseFactor = 1; droplets.seed = 83010;
                DropletErosion.Run(first, 33, 33, droplets, null); DropletErosion.Run(second, 33, 33, droplets, null);
                bool finite = true, equal = true;
                for (int i = 0; i < first.Length; i++)
                { finite &= !float.IsNaN(first[i]) && !float.IsInfinity(first[i]); equal &= first[i] == second[i]; }
                Check(finite && equal, "UC-T03-10 managed CPU droplet fallback remains finite and deterministic without a graphics device.");
                TestFinalGroundOperators(graphHeight);
                TestAcceptedGroundRefresh(world);
            }
            finally
            {
                var world = root.GetComponent<UniverseCreatorObject>();
                if (world != null) RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                tile?.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(terrainRoot); UnityEngine.Object.DestroyImmediate(root);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
            }
        }

        // UC-T03-10: exercise existing operators before considering any additional regional retarget solver.
        private static void TestFinalGroundOperators(Matrix plane)
        {
            Matrix constant = plane.Copy(); constant.Fill(0.4f);
            Matrix constantBlur = MatrixOperations.GaussianBlur(constant, 1f);
            Matrix planeBlur = MatrixOperations.GaussianBlur(plane, 1f);
            bool preserved = true;
            foreach (float value in constantBlur.arr) preserved &= Mathf.Abs(value - 0.4f) < 1e-6f;
            for (int z = 8; z < 25; z++) for (int x = 8; x < 25; x++)
                preserved &= Mathf.Abs(planeBlur.arr[z * 33 + x] - plane.arr[z * 33 + x]) < 1e-6f;
            Check(preserved, "UC-T03-10 existing Gaussian smoothing preserves constants and planar interiors without a regional replacement.");
            Matrix thermal = plane.Copy(); var settings = Erosion.ThermalSettings.Default;
            settings.iterations = 2; settings.relax = 0; settings.talusAngle = 45;
            Erosion.Thermal(thermal, settings);
            bool equal = true;
            for (int i = 0; i < plane.arr.Length; i++) equal &= Mathf.Abs(thermal.arr[i] - plane.arr[i]) < 1e-6f;
            Check(equal, "UC-T03-10 existing thermal relaxation preserves a physical slope below the authored talus threshold, including borders.");
        }

        // UC-T03-10: edits after the initial commit must replace the version and invalidate missing native banks.
        private static void TestAcceptedGroundRefresh(UniverseCreatorObject world)
        {
            world.GenerationScheduler.BeginRun();
            var tile = CommittedProfileTile(world);
            TerrainData data = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
            try
            {
                var bounds = new Bounds(new Vector3(16, 50, 16), new Vector3(32, 100, 32));
                TerrainBuilder.ApplyOutputs(data, tile.outputs, world, null, bounds, 1, null);
                Check(RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var before, out _),
                    "UC-T03-10 refresh fixture starts with an accepted native trench.");
                float[,] edited = data.GetHeights(0, 0, 33, 33);
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) edited[z, x] += 0.02f;
                data.SetHeightsDelayLOD(0, 0, edited); data.SyncHeightmap();
                Check(RiverChannelRegistry.RefreshGround(world, tile.RunId, tile.outputs.publicationOwner, edited, data) &&
                    RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var after, out _) &&
                    after.GroundVersion != before.GroundVersion && Mathf.Abs(after.Bed.y - before.Bed.y - 2) < 0.02f,
                    "UC-T03-10 a synchronized neighbouring edit changes the accepted ground version and exact bed while preserving its banks.");
                bool[,] holes = data.GetHoles(0, 0, 32, 32); holes[20, 12] = false; data.SetHoles(0, 0, holes);
                RiverChannelRegistry.RefreshGround(world, tile.RunId, tile.outputs.publicationOwner, edited, data);
                Check(!RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out _, out var validity) && validity == RiverChannelValidity.MissingGround,
                    "UC-T03-10 removing a native bank quad invalidates its accepted river section.");
                var overlap = CommittedProfileTile(world);
                try
                {
                    overlap.outputs.publicationOwner = tile.outputs.publicationOwner + "~overlap";
                    Check(CommitPublicationTestTile(world, overlap) &&
                        !RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out _, out validity) && validity == RiverChannelValidity.MissingGround,
                        "UC-T03-10 an overlapping accepted cell cannot replace a hole in the authoritative bank surface.");
                }
                finally { overlap.ReleaseProductMemory(); }
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(data); }
        }
    }
}
