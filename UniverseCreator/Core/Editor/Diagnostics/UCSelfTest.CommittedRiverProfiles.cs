// UC-T03-09: native terrain, retained products and consumers must agree on accepted channel context.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static ChannelGraphPublication CommittedProfilePublication(float fixedLevel = float.NaN)
        {
            var widths = new float[9]; var depths = new float[9]; var ground = new float[9];
            var downstream = new int[9]; var reaches = new string[9]; var levels = new float[9];
            for (int c = 0; c < 9; c++) { ground[c] = 0.5f; downstream[c] = -1; levels[c] = float.NaN; }
            widths[4] = widths[7] = 4; depths[4] = depths[7] = 4; downstream[4] = 7;
            reaches[4] = reaches[7] = "accepted-fixture:reach:4"; levels[4] = fixedLevel;
            return new ChannelGraphPublication("accepted-fixture:channels", widths, depths, 3, 3, 0, 0, 16, 16,
                null, null, downstream, reaches, ground, 0, 100, levels);
        }

        private static TileData CommittedProfileTile(UniverseCreatorObject world, float depthNorm = 0.1f, bool channels = true,
            float fixedLevel = float.NaN, float originX = 0, float extentX = 32, int cellX = 0)
        {
            var tile = new TileData(new Coord(cellX, 0), new CoordRect(0, 0, 33, 33), new Vector3(originX, 0, 0), new Vector3(extentX, 100, 32), 100);
            tile.AssignRun(world.GenerationScheduler.CurrentRun); tile.AttachMemory(world.ProductMemory);
            tile.outputs.heightmap = new float[33, 33];
            for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++)
                tile.outputs.heightmap[z, x] = 0.5f - depthNorm * Mathf.Clamp01(1 - Mathf.Abs(originX + extentX * x / 32 - 16) / 4f);
            if (channels) tile.AddPublication(CommittedProfilePublication(fixedLevel));
            tile.FinishPublications(); return tile;
        }

        private static void TestCommittedRiverProfiles()
        {
            var root = new GameObject("UC accepted river owner"); root.SetActive(false);
            var otherRoot = new GameObject("UC accepted river foreign owner"); otherRoot.SetActive(false);
            var tiles = new List<TileData>(); TerrainData native = null, otherNative = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.maxHeightMeters = 100; world.generateCollider = false;
                var other = otherRoot.AddComponent<UniverseCreatorObject>(); other.maxHeightMeters = 100; other.generateCollider = false;
                var cold = CommittedProfileTile(world); tiles.Add(cold);
                Check(!RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out _, out var validity) && validity == RiverChannelValidity.NoCommittedWorld,
                    "UC-T03-09 generation candidates cannot be read as accepted native profiles.");
                native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
                var bounds = new Bounds(new Vector3(16, 50, 16), new Vector3(32, 100, 32));
                TerrainBuilder.ApplyOutputs(native, cold.outputs, world, null, bounds, 1, null);
                Check(RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var accepted, out validity) &&
                    validity == RiverChannelValidity.Valid && accepted.Bed.y == 40 && accepted.LeftBank.y == 50 && accepted.RightBank.y == 50 &&
                    accepted.WaterSurfaceY == 43 && accepted.ReachId == "accepted-fixture:reach:4" &&
                    Mathf.Abs(native.GetInterpolatedHeight(0.5f, 0.625f) - accepted.Bed.y) < 0.01f,
                    "UC-T03-09 committed cross-sections sample the actual native bed and both banks after all shaping.");
                var height = new Matrix(new CoordRect(0, 0, 3, 3)); height.Fill(0.5f); height.worldSize = new Vector3(32, 100, 32); height.worldHeight = 100;
                var solve = WorldDrainage.CreateForTests(height, (float[])height.arr.Clone());
                var hydro = HydroField.For(solve, 0, cold.RunId);
                Check(hydro.HasAnyWater && hydro.TryRiverContext(16, 20, out var context) && context.Run.Equals(accepted.Run) &&
                    context.Bed == accepted.Bed && hydro.WaterSurfaceY(16, 20) == accepted.WaterSurfaceY &&
                    hydro.DistanceToWaterM(16, 20) == 0 && hydro.NearestKind(16, 20) == WaterBodyKind.River,
                    "UC-T03-09 HydroField and scene-water lookups share exact accepted river kind, bed, surface and reach identity.");
                cold.outputs.heightmap[20, 16] = 0.9f;
                Check(RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var frozen, out _) && frozen.Bed.y == 40,
                    "UC-T03-09 accepted profiles own a frozen ground copy independent of later output-array mutation.");
                Check(!RiverChannelRegistry.TrySampleCommitted(other, 16, 20, 20, out _, out _) &&
                    !RiverChannelRegistry.TrySampleCommitted(world, 33, 20, 100, out _, out validity) && validity == RiverChannelValidity.OutsideCoverage,
                    "UC-T03-09 foreign worlds and points outside committed bounds cannot borrow a nearby channel.");
                Check(!RiverChannelRegistry.TrySampleCommitted(world, 21, 20, 0, out _, out _) &&
                    RiverChannelRegistry.TrySampleCommitted(world, 21, 20, 2, out var near, out _) && near.SignedBankDistanceMetres > 0,
                    "UC-T03-09 proximity is explicitly separate from exact bank containment.");
                var overlap = CommittedProfileTile(other, 0.2f); tiles.Add(overlap);
                otherNative = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
                TerrainBuilder.ApplyOutputs(otherNative, overlap.outputs, other, null, bounds, 1, null);
                Check(RiverChannelRegistry.TrySampleCommitted(other, 16, 20, 0, out var foreign, out _) && Mathf.Abs(foreign.Bed.y - 30) < 1e-4f &&
                    RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out frozen, out _) && frozen.Bed.y == 40,
                    "UC-T03-09 two overlapping accepted worlds retain independent native channel profiles.");
                world.GenerationScheduler.BeginRun();
                Check(!CommitPublicationTestTile(world, cold) && RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out frozen, out _) && frozen.Bed.y == 40,
                    "UC-T03-09 a superseded commit preserves the previous accepted terrain profile.");
                var level = CommittedProfileTile(world, 0.1f, true, 47); tiles.Add(level);
                TerrainBuilder.ApplyOutputs(native, level.outputs, world, null, bounds, 1, null);
                Check(RiverChannelRegistry.TrySampleCommitted(world, 16, 16, 0, out var source, out _) && source.WaterSurfaceY == 47,
                    "UC-T03-09 a captured lake-source water level survives final native terrain reconciliation exactly.");
                var invalidLevel = CommittedProfileTile(world, 0.1f, true, 60); tiles.Add(invalidLevel);
                TerrainBuilder.ApplyOutputs(native, invalidLevel.outputs, world, null, bounds, 1, null);
                Check(!RiverChannelRegistry.TrySampleCommitted(world, 16, 16, 0, out _, out validity) && validity == RiverChannelValidity.OutletLevelMismatch,
                    "UC-T03-09 final banks below a captured lake level reject the outlet instead of forcing uphill water.");
                var empty = CommittedProfileTile(world, 0, false); tiles.Add(empty);
                TerrainBuilder.ApplyOutputs(native, empty.outputs, world, null, bounds, 1, null);
                Check(!RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out _, out validity) && validity == RiverChannelValidity.NoChannel,
                    "UC-T03-09 an accepted empty replacement withdraws the old channel with a coverage tombstone.");
                RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                Check(!hydro.TryRiverContext(16, 20, out _) && !RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out _, out _),
                    "UC-T03-09 world unload withdraws exact lookups and attached environment context.");
                TestCommittedProfileCache(world, native, bounds, tiles);
                TestCommittedProfileBorders(world, tiles);
            }
            finally
            {
                foreach (var tile in tiles) tile.ReleaseProductMemory();
                var owner = root.GetComponent<UniverseCreatorObject>(); var foreignOwner = otherRoot.GetComponent<UniverseCreatorObject>();
                if (owner != null) RiverChannelRegistry.Withdraw(owner.GenerationScheduler.CurrentRun.WorldId);
                if (foreignOwner != null) RiverChannelRegistry.Withdraw(foreignOwner.GenerationScheduler.CurrentRun.WorldId);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(otherRoot);
                if (native != null) UnityEngine.Object.DestroyImmediate(native);
                if (otherNative != null) UnityEngine.Object.DestroyImmediate(otherNative);
            }
        }

        [Serializable]
        private sealed class CommittedProfileFixture : CreatorNode
        {
            public override string NodeName => "Committed river profile fixture";
            public override NodeCategory Category => NodeCategory.Creator;
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                for (int z = 0; z < matrix.rect.size.z; z++) for (int x = 0; x < matrix.rect.size.x; x++)
                {
                    float wx = matrix.worldPos.x + x * matrix.PixelSize.x;
                    matrix.arr[z * matrix.rect.size.x + x] = 0.5f - 0.1f * Mathf.Clamp01(1 - Mathf.Abs(wx - 16) / 4f);
                }
                tile.AddPublication(CommittedProfilePublication());
            }
        }

        private static void TestCommittedProfileBorders(UniverseCreatorObject world, List<TileData> tiles)
        {
            world.GenerationScheduler.BeginRun();
            var left = CommittedProfileTile(world, 0.1f, true, float.NaN, 0, 16, 0); tiles.Add(left);
            var right = CommittedProfileTile(world, 0.1f, true, float.NaN, 16, 16, 1); tiles.Add(right);
            TerrainData native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(16, 100, 32));
            try
            {
                var leftBounds = new Bounds(new Vector3(8, 50, 16), new Vector3(16, 100, 32));
                var rightBounds = new Bounds(new Vector3(24, 50, 16), new Vector3(16, 100, 32));
                TerrainBuilder.ApplyOutputs(native, left.outputs, world, null, leftBounds, 0.5f, null);
                Check(!RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out _, out var validity) && validity == RiverChannelValidity.MissingGround,
                    "UC-T03-09 a bank across an uncommitted neighbour reports missing final ground.");
                TerrainBuilder.ApplyOutputs(native, right.outputs, world, null, rightBounds, 0.5f, null);
                Check(RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var forward, out _) && forward.Bed.y == 40,
                    "UC-T03-09 matching accepted neighbours jointly supply a seam cross-section.");
                RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                Check(CommitPublicationTestTile(world, right) && CommitPublicationTestTile(world, left) &&
                    RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var reverse, out _) && reverse.Bed == forward.Bed && reverse.LeftBank == forward.LeftBank && reverse.RightBank == forward.RightBank,
                    "UC-T03-09 reversed neighbour commits preserve the accepted seam bed and banks.");
            }
            finally { UnityEngine.Object.DestroyImmediate(native); }
        }

        private static void TestCommittedProfileCache(UniverseCreatorObject world, TerrainData native, Bounds bounds, List<TileData> tiles)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var ground = new CommittedProfileFixture { id = "accepted-cache-ground", contentHash = 8391 };
                var output = new HeightOutputNode { id = "accepted-cache-output", heightScale = 100,
                    enforceSeaLevelFloor = false, enforceCoastalShelf = false, enforceContinentalRise = false };
                graph.nodes.Add(ground); graph.nodes.Add(output); graph.AddConnection(new Connection(ground.id, 0, output.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); var evaluator = new GraphEvaluator(graph);
                var cold = CommittedProfileTile(world, 0, false); tiles.Add(cold); cold.retained = new Dictionary<string, RetainedProduct>();
                evaluator.Evaluate(cold, new StopToken());
                TerrainBuilder.ApplyOutputs(native, cold.outputs, world, null, bounds, 1, null);
                RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                var warm = CommittedProfileTile(world, 0, false); tiles.Add(warm); warm.retained = cold.retained;
                evaluator.Evaluate(warm, new StopToken());
                Check(warm.outputs.channelPublications.Length == 1 && ReferenceEquals(cold.outputs.channelPublications[0], warm.outputs.channelPublications[0]),
                    "UC-T03-09 retained height products replay the same immutable station metadata without another solve.");
                TerrainBuilder.ApplyOutputs(native, warm.outputs, world, null, bounds, 1, null);
                Check(RiverChannelRegistry.TrySampleCommitted(world, 16, 20, 0, out var accepted, out _) && accepted.Bed.y == 40,
                    "UC-T03-09 a cached production Height Output commits fresh final ground and restores channel lookup.");
                var bodyRoot = new GameObject("UC accepted profile read consumer"); bodyRoot.SetActive(false);
                bodyRoot.transform.SetParent(world.transform, false);
                try
                {
                    var body = bodyRoot.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River);
                    body.shape = UCWaterBody.ShapeMode.RiverSpline; body.followProceduralTrench = true; body.digTerrain = true; body.meander = 0;
                    // UC-T03-09: BuildDig gathers authored child handles; a bare cached outline is deliberately cleared.
                    AddRiverNode(bodyRoot.transform, new Vector3(16, 0, 18), 0);
                    AddRiverNode(bodyRoot.transform, new Vector3(16, 0, 26), 0);
                    Check(ReferenceEquals(body.CarveWorld, world), "UC-T03-09 an inactive authored river hierarchy retains its nearest world owner.");
                    body.BuildDig(false);
                    bool hasFit = body.TryGetFittedRiverProfile(out _, out var readBed, out var readDepth, out _); // UC-T04-02: inspect a surface copy; read mode has no terrain request.
                    bool equal = body.CarveLayer == null && body.LastChannelValidity == RiverChannelValidity.Valid && hasFit;
                    if (equal) for (int i = 0; i < readBed.Length; i++)
                        equal &= Mathf.Abs(readBed[i] - accepted.Bed.y) < 1e-4f &&
                            Mathf.Abs(readBed[i] + readDepth[i] - accepted.WaterSurfaceY) < 1e-4f;
                    Check(equal, "UC-T03-09 procedural read mode consumes the exact accepted bed and surface while keeping terrain excavation disabled.");
                    RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                    body.BuildDig(false);
                    Check(body.CarveLayer == null && body.LastChannelValidity == RiverChannelValidity.NoCommittedWorld,
                        "UC-T03-09 withdrawn read-mode data produces an explicit diagnostic and no fallback excavation.");
                }
                finally { UnityEngine.Object.DestroyImmediate(bodyRoot); }
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
