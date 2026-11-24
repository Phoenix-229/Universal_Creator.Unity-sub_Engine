// UC-T09-04: exercise production demand admission and accepted native readiness, without a second streaming pump.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestChunkRegionDemand()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC-T09-04 region demand world"); root.SetActive(false);
            var foreignRoot = new GameObject("UC-T09-04 foreign demand world"); foreignRoot.SetActive(false);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.worldSizeKm = 0.512f; world.chunkCountPerSide = 16; world.maxHeightMeters = 100;
                world.generateCollider = true; world.graph = null;
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                var manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var foreignWorld = foreignRoot.AddComponent<UniverseCreatorObject>(); foreignWorld.enabled = false; foreignWorld.autoRegenerate = false;
                var other = foreignRoot.AddComponent<ChunkManager>(); other.enabled = false; other.Initialize(foreignWorld);
                Type type = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager);
                var cells = (IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager);
                Vector3 origin = (Vector3)typeof(ChunkManager).GetMethod("WorldMinCorner", flags).Invoke(manager, new object[] { world });
                float size = world.GetChunkSize();
                // Prepared cells own native holders; only readiness coverage allocates accepted TerrainData.
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                {
                    object chunk = Activator.CreateInstance(type, true);
                    SetCommitField(type, chunk, "cell", new Coord(x, z)); SetCommitField(type, chunk, "lod", LODLevel.High);
                    var ground = new GameObject("Terrain_" + x + "_" + z); ground.transform.SetParent(root.transform, false);
                    ground.transform.position = origin + new Vector3(x * size, 0, z * size);
                    var terrain = ground.AddComponent<UnityEngine.Terrain>();
                    SetCommitField(type, chunk, "go", ground); SetCommitField(type, chunk, "terrain", terrain);
                    if (x == 0 && z == 0)
                    {
                        terrain.terrainData = data;
                        ground.AddComponent<TerrainCollider>().terrainData = data;
                        SetCommitField(type, chunk, "go", ground); SetCommitField(type, chunk, "terrain", terrain);
                        SetCommitField(type, chunk, "data", data); SetCommitField(type, chunk, "appliedHeights", CommitHeights(33, 0));
                        SetCommitField(type, chunk, "resolution", 33);
                    }
                    pool.Add(chunk); cells.Add(new Coord(x, z), chunk);
                }
                typeof(ChunkManager).GetField("built", flags).SetValue(manager, true);
                var union = (IDictionary)typeof(ChunkManager).GetField("regionDemand", flags).GetValue(manager);
                var requests = (IList)typeof(ChunkManager).GetField("regionRequests", flags).GetValue(manager);
                var window = typeof(ChunkManager).GetMethod("ApplyStreamWindow", flags);
                Bounds one = RegionTestBounds(origin, size, 0, 0, 1, 1);
                int terrainRequest = manager.RequestRegionDemand(one, ChunkDemandContent.Terrain, 3);
                int visibleRequest = manager.RequestRegionDemand(one, ChunkDemandContent.Visible, 1);
                int collisionRequest = manager.RequestRegionDemand(one, ChunkDemandContent.Collision, 0);
                Check(terrainRequest > 0 && visibleRequest > 0 && collisionRequest > 0 && union.Count == 1,
                    "UC-T09-04 exact upper faces select one cell and overlapping terrain/visible/collision demands coalesce.");
                Check(manager.GetRegionDemandStatus(terrainRequest) == ChunkRegionDemandStatus.Pending &&
                    (int)typeof(ChunkManager).GetField("activeWorkers", flags).GetValue(manager) == 0,
                    "UC-T09-04 request admission never activates native ground or performs a synchronous bake.");
                window.Invoke(manager, new object[] { world }); root.SetActive(true); Physics.SyncTransforms();
                Check(manager.GetRegionDemandStatus(terrainRequest) == ChunkRegionDemandStatus.Ready &&
                    manager.GetRegionDemandStatus(visibleRequest) == ChunkRegionDemandStatus.Ready &&
                    manager.GetRegionDemandStatus(collisionRequest) == ChunkRegionDemandStatus.Ready,
                    "UC-T09-04 terrain/visible/collision status observes actual accepted graphless native output.");
                var collider = ((GameObject)type.GetField("go").GetValue(cells[new Coord(0, 0)])).GetComponent<TerrainCollider>();
                collider.enabled = false;
                Check(manager.GetRegionDemandStatus(collisionRequest) == ChunkRegionDemandStatus.Pending &&
                    manager.GetRegionDemandStatus(terrainRequest) == ChunkRegionDemandStatus.Ready,
                    "UC-T09-04 missing native collision holds collision demand independently of accepted terrain residency.");
                collider.enabled = true;
                Check(other.GetRegionDemandStatus(collisionRequest) == ChunkRegionDemandStatus.Unavailable,
                    "UC-T09-04 a different world's manager cannot observe this world's request or accepted resources.");
                other.CancelRegionDemand(collisionRequest);
                manager.CancelRegionDemand(visibleRequest); manager.CancelRegionDemand(terrainRequest);
                Check(union.Count == 1 && manager.GetRegionDemandStatus(collisionRequest) == ChunkRegionDemandStatus.Ready &&
                    manager.GetRegionDemandStatus(visibleRequest) == ChunkRegionDemandStatus.Cancelled,
                    "UC-T09-04 cancelling overlapping demand removes only that request; foreign cancellation has no effect.");
                manager.CancelRegionDemand(collisionRequest);

                object policyChunk = cells[new Coord(0, 0)];
                world.visibilityRadiusCells = 0; world.collisionRadiusCells = 2;
                typeof(ChunkManager).GetField("centreCell", flags).SetValue(manager, new Coord(2, 2));
                var visiblePolicy = typeof(ChunkManager).GetMethod("WantsChunkVisible", flags);
                var collisionPolicy = typeof(ChunkManager).GetMethod("WantsChunkCollision", flags);
                Check(!(bool)visiblePolicy.Invoke(manager, new object[] { world, policyChunk, false }) &&
                    (bool)collisionPolicy.Invoke(manager, new object[] { world, policyChunk, false }),
                    "UC-T09-04 runtime policy admits collision independently of the authored visual radius.");
                int policyRequest = manager.RequestRegionDemand(one, ChunkDemandContent.Visible);
                Check((bool)visiblePolicy.Invoke(manager, new object[] { world, policyChunk, false }) &&
                    (bool)visiblePolicy.Invoke(manager, new object[] { world, policyChunk, true }),
                    "UC-T09-04 explicit visibility extends runtime policy while Editor whole-world presentation is retained.");
                manager.CancelRegionDemand(policyRequest);
                world.visibilityRadiusCells = -1; world.collisionRadiusCells = -1;
                object unprepared = cells[new Coord(1, 0)];
                GameObject holder = (GameObject)type.GetField("go").GetValue(unprepared);
                SetCommitField(type, unprepared, "go", null);
                Check(manager.RequestRegionDemand(RegionTestBounds(origin, size, 0, 0, 2, 1)) == 0 && union.Count == 0,
                    "UC-T09-04 an unprepared native holder rejects the entire request before admission.");
                SetCommitField(type, unprepared, "go", holder);

                int first = manager.RequestRegionDemand(RegionTestBounds(origin, size, 0, 0, 8, 8));
                int second = manager.RequestRegionDemand(RegionTestBounds(origin, size, 0, 8, 8, 8));
                int excess = manager.RequestRegionDemand(RegionTestBounds(origin, size, 8, 0, 1, 1));
                Check(first > 0 && second > 0 && excess == 0 && union.Count == 128 && manager.RegionDemandDiagnostic.Contains("128"),
                    "UC-T09-04 distinct-cell cap rejects the complete excess request without partially mutating existing demand.");
                manager.CancelRegionDemand(first); manager.CancelRegionDemand(second);
                Check(manager.RequestRegionDemand(RegionTestBounds(origin, size, 0, 0, 9, 8)) == 0 && union.Count == 0 &&
                    manager.RegionDemandDiagnostic.Contains("64"), "UC-T09-04 per-request footprint cap preserves existing demand atomically.");
                int[] ids = new int[16];
                for (int i = 0; i < ids.Length; i++) ids[i] = manager.RequestRegionDemand(one);
                bool admitted = true; foreach (int id in ids) admitted &= id > 0;
                Check(admitted && manager.RequestRegionDemand(one) == 0 && union.Count == 1 && manager.RegionDemandDiagnostic.Contains("16"),
                    "UC-T09-04 overlapping sources still respect the bounded request-slot cap.");
                foreach (int id in ids) manager.CancelRegionDemand(id);
                int expiring = manager.RequestRegionDemand(one);
                foreach (object request in requests)
                    if ((int)request.GetType().GetField("Id", flags).GetValue(request) == expiring)
                        request.GetType().GetField("Expires", flags).SetValue(request, 0d);
                Check(manager.GetRegionDemandStatus(expiring) == ChunkRegionDemandStatus.Expired && union.Count == 0 &&
                    (bool)typeof(ChunkManager).GetField("regionDemandChanged", flags).GetValue(manager),
                    "UC-T09-04 unscaled expiry withdraws stationary demand and schedules the ordinary residency sweep.");
                int rebound = manager.RequestRegionDemand(one);
                manager.CancelRegionDemand(expiring);
                Check(rebound > expiring && union.Count == 1,
                    "UC-T09-04 slot reuse keeps monotonically increasing IDs; stale cancellation cannot withdraw a new request.");
                world.transform.position = new Vector3(10, 0, 0);
                Check(manager.GetRegionDemandStatus(rebound) == ChunkRegionDemandStatus.Unavailable && union.Count == 0,
                    "UC-T09-04 changed world lattice invalidates old spatial demand instead of silently shifting it.");
                world.transform.position = Vector3.zero;
                int disabledCollision = manager.RequestRegionDemand(one, ChunkDemandContent.Collision);
                world.generateCollider = false;
                Check(manager.GetRegionDemandStatus(disabledCollision) == ChunkRegionDemandStatus.Unavailable &&
                    manager.RequestRegionDemand(one, ChunkDemandContent.Collision) == 0,
                    "UC-T09-04 removing authored collision cannot falsely certify a previously admitted collision request.");
                world.generateCollider = true;
                Check(manager.RequestRegionDemand(new Bounds(new Vector3(float.NaN, 0, 0), Vector3.one)) == 0 &&
                    manager.RequestRegionDemand(one, (ChunkDemandContent)4) == 0 && manager.RequestRegionDemand(one, timeoutSeconds: float.PositiveInfinity) == 0 &&
                    manager.RequestRegionDemand(new Bounds(Vector3.one * 100000, Vector3.one)) == 0,
                    "UC-T09-04 non-finite, unsupported and non-intersecting region requests are rejected.");
                int oldWorld = manager.RequestRegionDemand(one);
                manager.Initialize(foreignWorld);
                Check(manager.GetRegionDemandStatus(oldWorld) == ChunkRegionDemandStatus.Cancelled && union.Count == 0,
                    "UC-T09-04 rebinding withdraws manager-local demands rather than attaching them to another world's content.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(foreignRoot);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        private static Bounds RegionTestBounds(Vector3 origin, float size, int x, int z, int width, int depth) =>
            new Bounds(origin + new Vector3((x + width * 0.5f) * size, 50, (z + depth * 0.5f) * size),
                new Vector3(width * size, 100, depth * size));
    }
}
