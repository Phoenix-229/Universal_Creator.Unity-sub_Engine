// UC-T04-03: native reapplication follows manual points, preserves unrelated trenches and restores pristine ground.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestManualRiverCourseOwnership()
        {
            foreach (int resolution in new[] { 129, 65 }) TestManualRiverCourseAtResolution(resolution);
            TestManualRiverNaturalisation();
        }

        private static void TestManualRiverCourseAtResolution(int resolution)
        {
            var root = new GameObject("UC manual river native owner"); root.SetActive(false);
            TerrainData data = null; ChunkManager manager = null; UCWaterBody body = null; TileData channels = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false;
                world.autoRegenerate = false; world.chunkCountPerSide = 1; world.worldSizeKm = 0.032f;
                world.resolution = 129; world.maxHeightMeters = 100; world.generateCollider = false;
                var host = new GameObject("Manual river chunks"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                data = TerrainBuilder.GetOrCreateTerrainData(null, resolution, new Vector3(32, 100, 32));
                var pristine = CommitHeights(resolution, 0.5f);
                for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++)
                    pristine[z, x] -= 0.1f * Mathf.Clamp01(1 - Mathf.Abs(x * 32f / (resolution - 1) - 16) / 4);
                data.SetHeights(0, 0, pristine);
                var terrainRoot = new GameObject("Manual river native terrain"); terrainRoot.transform.SetParent(root.transform, false);
                var terrain = terrainRoot.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = data;
                object chunk = Activator.CreateInstance(chunkType, true);
                SetCommitField(chunkType, chunk, "go", terrainRoot); SetCommitField(chunkType, chunk, "terrain", terrain);
                SetCommitField(chunkType, chunk, "data", data); SetCommitField(chunkType, chunk, "resolution", resolution);
                SetCommitField(chunkType, chunk, "cell", Coord.Zero); SetCommitField(chunkType, chunk, "lod", LODLevel.High);
                SetCommitField(chunkType, chunk, "applied", true); SetCommitField(chunkType, chunk, "bakedThisSession", true);
                SetCommitField(chunkType, chunk, "graphHeights", (float[,])pristine.Clone());
                SetCommitField(chunkType, chunk, "appliedHeights", (float[,])pristine.Clone());
                ((IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager)).Add(chunk);
                ((IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager)).Add(Coord.Zero, chunk);
                channels = CommittedProfileTile(world); CommitPublicationTestTile(world, channels);
                var bodyRoot = new GameObject("Manual route beside a procedural trench"); bodyRoot.SetActive(false); bodyRoot.transform.SetParent(root.transform, false);
                body = bodyRoot.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                body.meander = 0; body.ruggedness = 0; body.corridorStrength = 0;
                var first = RiverModePoint(bodyRoot.transform, new Vector3(26, 50, 8), false);
                var last = RiverModePoint(bodyRoot.transform, new Vector3(26, 50, 24), false);
                first.halfWidthM = last.halfWidthM = 1; first.depthM = last.depthM = 2;
                first.dischargeM3PerS = last.dischargeM3PerS = 0;
                body.SetRiverTerrainMode(RiverTerrainMode.CarveManualCourse); body.BuildDig(false); WaterCarveRegistry.Register(body);
                Check(body.CarveLayer != null && !body.CarveLayer.followOnly && manager.ReapplyCarves() == 1,
                    $"UC-T04-03 native resolution={resolution} accepts only the manual river's own excavation request.");
                var cut = data.GetHeights(0, 0, resolution, resolution); int routeX = Mathf.RoundToInt(26f / 32 * (resolution - 1));
                int middleZ = (resolution - 1) / 2; int otherX = (resolution - 1) / 2;
                Check(cut[middleZ, routeX] < pristine[middleZ, routeX] - 0.005f &&
                    Mathf.Abs(cut[middleZ, otherX] - pristine[middleZ, otherX]) < 2e-5f &&
                    first.transform.localPosition == new Vector3(26, 50, 8) && last.transform.localPosition == new Vector3(26, 50, 24),
                    "UC-T04-03 native excavation follows the authored route beside an unrelated accepted trench and does not move either endpoint.");
                manager.ReapplyCarves();
                Check(RiverReadHeightsNear(data.GetHeights(0, 0, resolution, resolution), cut),
                    "UC-T04-03 repeated native reapplication starts from pristine graph heights rather than deepening the previous manual cut.");
                // UC-T08-15: the inspector's real capture path must keep an unconnected brush trench, not capture registered body excavation.
                Check(manager.CaptureManualEdits() == 0 && world.overrideLayer.IsEmpty,
                    "UC-T08-15 capturing an unchanged native river surface does not save a procedural or registered cut twice.");
                int sculptZ = Mathf.RoundToInt(2f / 32 * (resolution - 1));
                var sculpted = (float[,])cut.Clone();
                for (int x = 0; x < resolution; x++)
                    sculpted[sculptZ, x] -= 0.012f * Mathf.Clamp01(1 - Mathf.Abs(x * 32f / (resolution - 1) - 16) / 3);
                data.SetHeights(0, 0, sculpted);
                Check(manager.CaptureManualEdits() > 0 && manager.CaptureManualEdits() == 0,
                    "UC-T08-15 native inspector capture saves a hand-sculpted trench once without any River Course Input connected.");
                world.overrideLayer = JsonUtility.FromJson<TerrainOverrideLayer>(JsonUtility.ToJson(world.overrideLayer));
                manager.ReapplyCarves();
                Check(Mathf.Abs(data.GetHeights(0, 0, resolution, resolution)[sculptZ, otherX] - sculpted[sculptZ, otherX]) < 2e-5f,
                    "UC-T08-15 serialized manual trench survives native reapplication alongside a registered manual river.");
                first.transform.localPosition = new Vector3(6, 50, 8); last.transform.localPosition = new Vector3(6, 50, 24); // UC-T04-03: keep both complete bank footprints apart when checking restoration.
                body.ResolveBedFromTerrain(); manager.ReapplyCarves();
                var moved = data.GetHeights(0, 0, resolution, resolution); int movedX = Mathf.RoundToInt(6f / 32 * (resolution - 1));
                Check(Mathf.Abs(moved[middleZ, routeX] - pristine[middleZ, routeX]) < 2e-5f &&
                    moved[middleZ, movedX] < pristine[middleZ, movedX] - 0.005f,
                    $"UC-T04-03 moving the manual course restores its former cut and excavates only its new owned route. resolution={resolution}, old={moved[middleZ, routeX]}/{pristine[middleZ, routeX]}, new={moved[middleZ, movedX]}/{pristine[middleZ, movedX]}, fit={body.RiverSurfaceDiagnostic}");
                RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); body = null;
                manager.ReapplyCarves();
                var savedPristine = (float[,])pristine.Clone();
                world.overrideLayer.ApplyTo(savedPristine, new Bounds(new Vector3(16, 50, 16), new Vector3(32, 100, 32)), 32f / (resolution - 1)); // UC-T08-15: the fixture's native lattice can differ from the world's authored resolution.
                Check(RiverReadHeightsNear(data.GetHeights(0, 0, resolution, resolution), savedPristine),
                    "UC-T04-03 withdrawing the final manual owner restores the pristine graph terrain including the separate procedural trench.");
                var regenerated = (float[,])pristine.Clone();
                for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++) regenerated[z, x] += 0.025f;
                SetCommitField(chunkType, chunk, "graphHeights", regenerated);
                manager.ReapplyCarves();
                Check(Mathf.Abs(data.GetHeights(0, 0, resolution, resolution)[sculptZ, otherX] - (savedPristine[sculptZ, otherX] + 0.025f)) < 2e-5f,
                    "UC-T08-15 unconnected saved trench rides rebuilt procedural ground rather than freezing its absolute height.");
            }
            finally
            {
                if (body != null) { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); }
                channels?.ReleaseProductMemory();
                RiverChannelRegistry.Withdraw(root.GetComponent<UniverseCreatorObject>().GenerationScheduler.CurrentRun.WorldId);
                UnityEngine.Object.DestroyImmediate(root);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
            }
        }

        private static bool RiverReadHeightsNear(float[,] a, float[,] b)
        {
            for (int z = 0; z < a.GetLength(0); z++) for (int x = 0; x < a.GetLength(1); x++)
                if (Mathf.Abs(a[z, x] - b[z, x]) > 2e-5f) return false;
            return true;
        }

        private static void TestManualRiverNaturalisation()
        {
            var root = new GameObject("UC explicit manual river meander"); root.SetActive(false);
            try
            {
                var body = root.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                var first = RiverModePoint(root.transform, new Vector3(0, 5, 0), false);
                RiverModePoint(root.transform, new Vector3(0, 4, 20), false);
                var last = RiverModePoint(root.transform, new Vector3(0, 3, 40), false);
                body.EnsureRiverTerrainMode();
                foreach (float amount in new[] { 0f, 1.5f, 37f, float.NaN, float.PositiveInfinity })
                {
                    body.meander = amount;
                    bool baked = body.TryBakeWorldCourseXZ(1, out var course, out _); bool finite = baked, bounded = baked;
                    if (baked) foreach (var point in course)
                    {
                        finite &= !float.IsNaN(point.x) && !float.IsInfinity(point.x) && !float.IsNaN(point.y) && !float.IsInfinity(point.y);
                        bounded &= Mathf.Abs(point.x) <= 25.2f + 1e-4f;
                    }
                    Check(finite && bounded && course[0] == new Vector2(0, 0) && course[course.Length - 1] == new Vector2(0, 40) &&
                        first.transform.localPosition == new Vector3(0, 5, 0) && last.transform.localPosition == new Vector3(0, 3, 40),
                        "UC-T04-03 explicit naturalisation is finite and bounded, with fixed endpoints and unchanged authored point transforms.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
