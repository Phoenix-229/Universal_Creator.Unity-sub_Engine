// UC-T06-05: scheduling-independent world candidates, cross-region spacing, stable budget subsets and canopy dependency invalidation.
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem; // UC-T06-05: the world snapshot owner lives in the terrain runtime assembly namespace.

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationCandidates()
        {
            PoissonSettings settings = PoissonSettings.Default;
            settings.stableWorldGrid = true; settings.minSpacing = 2; settings.seed = 142057; settings.maxPoints = 100000;
            var right = PoissonDisc.Solve(settings, 0, -8, 16, 16, null);
            var left = PoissonDisc.Solve(settings, -16, -8, 16, 16, null);
            var whole = PoissonDisc.Solve(settings, -16, -8, 32, 16, null);
            var merged = left.Concat(right).OrderBy(c => c.worldZ).ThenBy(c => c.worldX).ToArray();
            Check(whole.Count > 10 && whole.SequenceEqual(merged),
                "UC-T06-05 adjacent world regions in reverse order equal a whole demand rectangle, including negative coordinates.");
            bool spaced = true;
            for (int i = 0; i < merged.Length; i++) for (int j = 0; j < i; j++)
            {
                double dx = (double)merged[i].worldX - merged[j].worldX, dz = (double)merged[i].worldZ - merged[j].worldZ;
                spaced &= dx * dx + dz * dz >= 4;
            }
            Check(spaced, "UC-T06-05 every pair, including pairs across a chunk edge, respects the authored minimum spacing.");
            var overlap = PoissonDisc.Solve(settings, -8, -4, 16, 8, null);
            Check(overlap.SequenceEqual(whole.Where(c => c.worldX >= -8 && c.worldX < 8 && c.worldZ >= -4 && c.worldZ < 4)),
                "UC-T06-05 overlapping demand windows retain the same candidates and variation draws.");
            settings.maxPoints = 25; var medium = PoissonDisc.Solve(settings, -16, -8, 32, 16, null);
            PoissonSamplingReport report = default; bool reported = false;
            settings.maxPoints = 5; var low = PoissonDisc.Solve(settings, -16, -8, 32, 16, null, null, value => { report = value; reported = true; });
            Check(low.Count == 5 && medium.Count == 25 && low.All(medium.Contains) && medium.All(whole.Contains),
                "UC-T06-05 reducing point caps selects a stable ranked subset without reseeding or coarsening the lattice.");
            Check(reported && report.StableWorldGrid && report.CellsVisited == 153 && report.CapRejected == whole.Count - low.Count &&
                report.Candidates == report.SpacingRejected + report.DensityRejected + report.InvalidDensityRejected + report.CapRejected + report.Emitted,
                "UC-T06-05 sampler counters account for known grid work and every candidate rejection/selection without counting rendered plants.");
            settings.maxPoints = 100000;
            var thinned = PoissonDisc.Solve(settings, -16, -8, 32, 16, (x, z) => 0.4f);
            Check(thinned.Count > 0 && thinned.Count < whole.Count && thinned.All(c => whole.Any(w => w.worldX == c.worldX && w.worldZ == c.worldZ)),
                "UC-T06-05 fractional density thins a fixed candidate field without shifting survivors.");
            Check(PoissonDisc.Solve(settings, -16, -8, 32, 16, (x, z) => float.NaN).Count == 0,
                "UC-T06-05 invalid candidate density excludes points rather than admitting a dense field.");
            var clustered = PoissonDisc.ApplyClusterBias(whole, 8, 1, null, settings.seed, true);
            var clusteredPart = PoissonDisc.ApplyClusterBias(overlap, 8, 1, null, settings.seed, true);
            Check(clustered.Count > 0 && clustered.Count < whole.Count && clusteredPart.SequenceEqual(clustered.Where(c =>
                c.worldX >= -8 && c.worldX < 8 && c.worldZ >= -4 && c.worldZ < 4)),
                "UC-T06-05 clustering uses one world field and agrees across overlapping regions.");
            int cancellationChecks = 0;
            Check(PoissonDisc.Solve(settings, -16, -8, 32, 16, null, () => ++cancellationChecks > 3).Count == 0,
                "UC-T06-05 cancelled stable placement discards its partial candidate set.");
            settings.minSpacing = 0.01f; int visits = 0; bool bounded = false;
            try { PoissonDisc.Solve(settings, 0, 0, 1024, 1024, (x, z) => { visits++; return 1; }); }
            catch (InvalidOperationException) { bounded = true; }
            Check(bounded && visits == 0, "UC-T06-05 pathological density fails its fixed work bound before invoking any candidate predicate.");
            settings.minSpacing = float.NaN; bounded = false;
            try { PoissonDisc.Solve(settings, 0, 0, 16, 16, null); } catch (ArgumentException) { bounded = true; }
            Check(bounded, "UC-T06-05 invalid stable spacing reports an error before grid arithmetic.");
            settings.minSpacing = 0.01f; bounded = false;
            try { PoissonDisc.Solve(settings, 1000000, 0, 0.1f, 0.1f, null); }
            catch (InvalidOperationException) { bounded = true; }
            Check(bounded, "UC-T06-05 unresolved float world coordinates fail before distant cells can collapse into duplicate positions.");
            TestVegetationCanopyDependencies();
        }

        private static void TestVegetationCanopyDependencies()
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                graph.SetDomain(UCGraphDomain.Vegetation);
                var grass = new GrassDetailScatterNode { id = "candidate-grass" };
                var shrub = new ShrubScatterNode { id = "candidate-shrub" };
                var tree = new TreeScatterNode { id = "candidate-tree" };
                var habitat = new VegetationHabitatNode { id = "candidate-habitat", family = VegetationHabitatFamily.Details };
                graph.nodes.Add(grass); graph.nodes.Add(habitat); graph.nodes.Add(shrub); graph.nodes.Add(tree);
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                Check(graph.compiledPlan.Outputs.Select(r => r.Node).SequenceEqual(new Generator[] { tree, shrub, grass }) &&
                    graph.compiledPlan.GetDependents(tree.id).Contains(grass) && graph.compiledPlan.GetDependents(tree.id).Contains(habitat),
                    "UC-T06-05 compiled vegetation roots apply canopy before understory and index their semantic dependents.");
                GraphContentHasher.ComputeHashes(graph);
                ulong grassBefore = grass.contentHash, habitatBefore = habitat.contentHash, shrubBefore = shrub.contentHash;
                tree.seed++;
                GraphContentHasher.ComputeHashes(graph);
                Check(grassBefore != grass.contentHash && habitatBefore != habitat.contentHash && shrubBefore != shrub.contentHash,
                    "UC-T06-05 a tree edit invalidates retained grass, shrub and habitat products without a visible canopy wire.");
                graph.connections.Add(new Connection(habitat.id, 0, tree.id, 1));
                Check(GraphValidationReport.Analyze(graph, NodeExecutionDomain.Worker, false).Diagnostics.Any(d => d.Code == "CYCLE"),
                    "UC-T06-05 canopy feedback through a lower-layer habitat mask is rejected before execution.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
            TestVegetationCanopyCache();
        }

        [Serializable]
        private sealed class CandidateCanopyProducer : TreeScatterNode
        {
            [NonSerialized] internal int computes;
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                computes++;
                var settings = PoissonSettings.Default; settings.stableWorldGrid = true;
                PoissonDisc.Solve(settings, tile.worldPos.x, tile.worldPos.z, tile.worldSize.x, tile.worldSize.z, null, null,
                    report => tile.AddPublication(new ScatterSamplingGraphPublication(id + ":sampling", report)));
                var height = tile.CreateMatrix(); height.Fill(0.25f);
                // Fixture seed is the leaf-area input so changing the producer has a directly observable shade effect.
                tile.AddPublication(new EcologyGraphPublication(id + ":canopy", height,
                    new[] { new CanopyGraphStamp(8, 8, 4, seed, CanopyLayer.Canopy) }, new int[LandformAssemblage.ZoneCount]));
                return height;
            }
        }

        [Serializable]
        private sealed class CandidateUnderstoryConsumer : ShrubScatterNode
        {
            [NonSerialized] internal int computes;
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                computes++;
                return tile.ecology.sampler.Sample(8, 8).floorLightFraction;
            }
        }

        private static void TestVegetationCanopyCache()
        {
            var root = new GameObject("UC canopy cache acceptance"); root.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
            var graph = ScriptableObject.CreateInstance<Graph>();
            var tiles = new List<TileData>();
            try
            {
                graph.SetDomain(UCGraphDomain.Vegetation);
                var producer = new CandidateCanopyProducer { id = "canopy-producer", seed = 3 };
                var consumer = new CandidateUnderstoryConsumer { id = "canopy-consumer" };
                graph.nodes.Add(consumer); graph.nodes.Add(producer);
                graph.compiledPlan = CompiledGraphPlan.Build(graph); GraphContentHasher.ComputeHashes(graph);
                var cold = PublicationTestTile(world); tiles.Add(cold);
                var evaluator = new GraphEvaluator(graph); evaluator.Evaluate(cold, new StopToken());
                float coldLight = (float)evaluator.PullNode(consumer, 0, cold, new StopToken());
                var warm = PublicationTestTile(world); tiles.Add(warm); warm.retained = cold.retained;
                evaluator.Evaluate(warm, new StopToken());
                float warmLight = (float)evaluator.PullNode(consumer, 0, warm, new StopToken());
                Check(coldLight < 1 && coldLight == warmLight && producer.computes == 1 && consumer.computes == 1 &&
                    warm.ecology.canopy.TotalLai(8, 8) == cold.ecology.canopy.TotalLai(8, 8),
                    "UC-T06-05 cold and retained canopy runs give identical shade without recomputation or duplicate stamps.");
                Check(cold.outputs.scatterSamplingReports.ContainsKey(producer.id + ":sampling") &&
                    warm.outputs.scatterSamplingReports[producer.id + ":sampling"].Equals(cold.outputs.scatterSamplingReports[producer.id + ":sampling"]),
                    "UC-T06-05 sampler evidence replays through the same retained publication owner as canopy products.");
                producer.seed = 0; GraphContentHasher.ComputeHashes(graph);
                var edited = PublicationTestTile(world); tiles.Add(edited); edited.retained = cold.retained;
                evaluator.Evaluate(edited, new StopToken());
                Check((float)evaluator.PullNode(consumer, 0, edited, new StopToken()) == 1 && producer.computes == 2 && consumer.computes == 2,
                    "UC-T06-05 replacing a canopy producer recomputes its lower-layer consumer and withdraws obsolete shade.");
            }
            catch (Exception error) { Check(false, "UC-T06-05 canopy retained-path exception: " + error); }
            finally
            {
                foreach (var tile in tiles) tile.ReleaseProductMemory();
                world.ProductMemory.ClearRetained();
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
