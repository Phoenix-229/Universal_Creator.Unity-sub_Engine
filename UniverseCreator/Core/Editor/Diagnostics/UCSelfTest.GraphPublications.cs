using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Ecology;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable]
        private sealed class PublicationFieldNode : CreatorNode
        {
            internal int computes;
            public override string NodeName => "Publication field";
            // UC-T02-07: publications do not replace required node metadata.
            public override NodeCategory Category => NodeCategory.Creator;
            /// <inheritdoc />
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                computes++; matrix.Fill(0.25f);
                tile.AddPublication(new ChannelGraphPublication(id + ":channels", new[] { 4f, 4f, 4f, 4f },
                    new[] { 2f, 2f, 2f, 2f }, 2, 2, tile.worldPos.x, tile.worldPos.z, 16, 16));
                var histogram = new int[LandformAssemblage.ZoneCount]; histogram[0] = 3;
                tile.AddPublication(new EcologyGraphPublication(id + ":ecology", matrix,
                    new[] { new CanopyGraphStamp(8, 8, 4, 3, CanopyLayer.Canopy) }, histogram));
            }
        }

        [Serializable]
        private sealed class PublicationRelayNode : Generator
        {
            public override string NodeName => "Publication relay";
            // UC-T02-07: the relay uses the ordinary utility-node contract.
            public override NodeCategory Category => NodeCategory.Utility;
            public override Port[] GetInputs() => new[] { new Port("Input", PortType.Matrix, PortDirection.Input) };
            public override Port[] GetOutputs() => new[] { new Port("Output", PortType.Matrix, PortDirection.Output) };
            public override object Generate(int outputIndex, TileData tile, StopToken stop) => tile.PullInput(this, 0, stop);
        }

        // UC-T02-07: retain an ancestor, so a warm run skips the producer and must replay its transitive obligations.
        private static void TestGraphPublications()
        {
            var root = new GameObject("UC publication owner"); root.SetActive(false);
            var otherRoot = new GameObject("UC foreign publication owner"); otherRoot.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>();
            var other = otherRoot.AddComponent<UniverseCreatorObject>();
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new PublicationFieldNode { id = "publication-source", contentHash = 11 };
            var relay = new PublicationRelayNode { id = "publication-relay", contentHash = 12 };
            var output = new HeightOutputNode { id = "height", enforceSeaLevelFloor = false,
                enforceContinentalRise = false, enforceCoastalShelf = false };
            graph.nodes.Add(source); graph.nodes.Add(relay); graph.nodes.Add(output);
            graph.connections.Add(new Connection(source.id, 0, relay.id, 0));
            graph.connections.Add(new Connection(relay.id, 0, output.id, 0));
            TileData cold = null, warm = null, cancelled = null, empty = null;
            try
            {
                var evaluator = new GraphEvaluator(graph);
                cold = PublicationTestTile(world);
                evaluator.Evaluate(cold, new StopToken());
                Check(!RiverChannelRegistry.TrySample(world, 8, 8, 25, out _, out _),
                    "UC-T02-07 computing a field does not publish scene channel metadata.");
                Check(CommitPublicationTestTile(world, cold) && RiverChannelRegistry.TrySample(world, 8, 8, 25, out float width, out float depth) && width == 4 && depth == 2,
                    "UC-T02-07 current accepted commit publishes its channel dimensions.");
                Check(!RiverChannelRegistry.TrySample(other, 8, 8, 25, out _, out _) && !RiverChannelRegistry.TrySample(world, 100, 100, 200, out _, out _),
                    "UC-T02-07 foreign worlds and uncommitted cells cannot borrow a channel grid.");

                var retained = new Dictionary<string, RetainedProduct>(cold.retained);
                cold.AcceptRetainedProducts();
                float canopy = cold.ecology.canopy.TotalLai(8, 8);
                cold.ReleaseProductMemory();
                RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                world.GenerationScheduler.BeginRun();
                warm = PublicationTestTile(world); warm.retained = retained;
                evaluator.Evaluate(warm, new StopToken());
                Check(source.computes == 1 && warm.outputs.zoneHistogram[0] == 3 && canopy > 0 &&
                      Mathf.Abs(warm.ecology.canopy.TotalLai(8, 8) - canopy) < 1e-6f,
                    "UC-T02-07 a retained ancestor restores canopy and ecology tallies without recomputing the source.");
                evaluator.PullNode(source, 0, warm, new StopToken());
                Check(warm.outputs.zoneHistogram[0] == 3 && Mathf.Abs(warm.ecology.canopy.TotalLai(8, 8) - canopy) < 1e-6f,
                    "UC-T02-07 shared/aliased publications replay once per tile.");
                Check(CommitPublicationTestTile(world, warm) && RiverChannelRegistry.TrySample(world, 8, 8, 25, out width, out depth) && width == 4,
                    "UC-T02-07 retained arrays restore withdrawn scene metadata at the replacement commit.");

                cancelled = PublicationTestTile(world); cancelled.retained = retained;
                evaluator.Evaluate(cancelled, new StopToken());
                world.GenerationScheduler.BeginRun();
                Check(!CommitPublicationTestTile(world, cancelled) && RiverChannelRegistry.TrySample(world, 8, 8, 25, out width, out _) && width == 4,
                    "UC-T02-07 cancelled commit preserves the previous accepted channel publication.");
                empty = PublicationTestTile(world); empty.FinishPublications();
                Check(CommitPublicationTestTile(world, empty) && !RiverChannelRegistry.TrySample(world, 8, 8, 25, out _, out _),
                    "UC-T02-07 an accepted empty result withdraws the previous owner's channel.");
            }
            finally
            {
                RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                RiverChannelRegistry.Withdraw(other.GenerationScheduler.CurrentRun.WorldId);
                cold?.ReleaseProductMemory(); warm?.ReleaseProductMemory(); cancelled?.ReleaseProductMemory(); empty?.ReleaseProductMemory();
                world.ProductMemory.ClearRetained();
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(otherRoot); UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static TileData PublicationTestTile(UniverseCreatorObject world)
        {
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 17, 17), Vector3.zero, new Vector3(16, 100, 16), 100);
            tile.AssignRun(world.GenerationScheduler.CurrentRun); tile.AttachMemory(world.ProductMemory);
            tile.retained = new Dictionary<string, RetainedProduct>();
            tile.ecology = new EcoScatterContext();
            tile.ecology.settings.canopyCompetition = true; // UC-T08-15: this fixture explicitly requests crown publication/replay.
            return tile;
        }

        private static bool CommitPublicationTestTile(UniverseCreatorObject world, TileData tile)
        {
            ProductMemoryBudget.Lease memory = GraphPublicationCommit.Prepare(world, tile.outputs);
            try { return GraphPublicationCommit.Commit(world, tile.outputs, memory); }
            catch { memory?.Dispose(); throw; }
        }
    }
}
