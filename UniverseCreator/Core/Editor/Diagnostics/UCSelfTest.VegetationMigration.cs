// UC-T06-02: representative legacy/migrated placement, accepted-ground boundaries and native rollback.
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Ecology;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable]
        private sealed class MigrationBiomeFieldNode : Generator
        {
            public override string NodeName => "Migration biome fixture";
            public override NodeCategory Category => NodeCategory.Biome;
            public override Port[] GetInputs() => SharedPorts.None;
            public override Port[] GetOutputs() => new[] { new Port("Biome", PortType.Matrix, PortDirection.Output) };
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                var matrix = tile.CreateMatrix();
                for (int z = 0; z < matrix.rect.size.z; z++)
                    for (int x = 0; x < matrix.rect.size.x; x++)
                        matrix.arr[z * matrix.rect.size.x + x] = WhittakerBiome.Encode(x < matrix.rect.size.x / 2 ? WhittakerBiome.TemperateForest : WhittakerBiome.Desert);
                return matrix;
            }
        }

        private static void TestVegetationMigration()
        {
            foreach (bool mixed in new[] { false, true }) TestVegetationMigrationCase(mixed);
        }

        private static void TestVegetationMigrationCase(bool mixed)
        {
            string folder = "Assets/UC_T06_Migration_" + Guid.NewGuid().ToString("N");
            var root = new GameObject("UC vegetation migration acceptance"); root.SetActive(false);
            var source = ScriptableObject.CreateInstance<Graph>(); Graph legacyGraph = null;
            TileData legacy = null, migrated = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
                var height = new ConstantNode { id = "vegetation-final-height", value = 0.2f };
                var output = new HeightOutputNode { id = "vegetation-terrain-output" };
                var biome = new MigrationBiomeFieldNode { id = "vegetation-local-biomes" };
                source.nodes.Add(height); source.nodes.Add(output);
                source.connections.Add(new Connection(height.id, 0, output.id, 0));
                if (mixed) source.nodes.Add(biome);
                var scatter = new ScatterNodeBase[] {
                    new TreeScatterNode { id = "migration-tree", seed = 302, density = 20000, minSpacing = 4, maxSlope = 90, seaLevel = 0.1f }, // UC-T06-02: live land fixture stands above its declared water plane.
                    new ShrubScatterNode { id = "migration-shrub", seed = 303, density = 20000, minSpacing = 4, maxSlope = 90, clumping = 0, seaLevel = 0.1f },
                    new GrassDetailScatterNode { id = "migration-grass", seed = 304, patchiness = 0, maxSlope = 90 },
                    new UnderwaterScatterNode { id = "migration-aquatic", seed = 305, density = 20000, minSpacing = 4, maxSlope = 90, waterLevel = 0.3f }
                };
                foreach (var node in scatter)
                {
                    node.onlyInBiome = mixed ? WhittakerBiome.TemperateForest : -1;
                    source.nodes.Add(node); source.connections.Add(new Connection(height.id, 0, node.id, 0));
                    if (mixed) source.connections.Add(new Connection(biome.id, 0, node.id, 2));
                }
                AssetDatabase.CreateAsset(source, folder + "/Legacy.asset");
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false; world.graph = source;
                world.scatterLibrary = new ScatterPrototypeLibrary(); world.scatterLibrary.trees.Add(new TreePrototypeDef { prefab = root });
                var manager = root.AddComponent<UCVegetationSystemManager>(); manager.enabled = false; manager.RemovedInstances.Remove(1729);
                var run = world.GenerationScheduler.CurrentRun;
                string original = GraphSerializer.ToJson(source), prototypes = EditorJsonUtility.ToJson(world.scatterLibrary);
                legacyGraph = GraphSerializer.FromJson(original);
                legacy = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(32, 100, 32), 100);
                legacy.AssignRun(run); new GraphEvaluator(legacyGraph).Evaluate(legacy, new StopToken());
                var plan = GraphMigrationPlan.Analyze(source, world);
                Check(plan.CanApply && plan.AcceptedGroundConnections.Count == 4 && plan.Destinations[height.id] == UCGraphDomain.TerrainWater,
                    "UC-T06-02 exact published height crossings migrate without duplicating terrain; mixed=" + mixed);
                bool rolledBack = false;
                try { GraphMigrationExecutor.Apply(plan, folder, count => { if (count == 3) throw new InvalidOperationException("UC vegetation injected failure"); }); }
                catch (InvalidOperationException error) { rolledBack = error.Message == "UC vegetation injected failure"; }
                Check(rolledBack && world.graph == source && world.GetComponent<UCWorldGraphBindings>() == null && GraphSerializer.ToJson(source) == original &&
                    manager.RemovedInstances.IsRemoved(1729) && AssetDatabase.FindAssets("t:Graph", new[] { folder }).Length == 1,
                    "UC-T06-02 rollback retains legacy authoring, native prototypes and persistent removals; mixed=" + mixed);
                var result = GraphMigrationExecutor.Apply(GraphMigrationPlan.Analyze(source, world), folder);
                var vegetation = result.Assets[UCGraphDomain.Vegetation];
                var input = vegetation.nodes.OfType<WorldGraphInputNode>().Single();
                Check(input.mainGraph == world.graph && vegetation.nodes.Where(n => n != input).All(n =>
                    EditorJsonUtility.ToJson(n) == EditorJsonUtility.ToJson(source.GetNodeById(n.id))) &&
                    GraphSerializer.ToJson(source) == original && EditorJsonUtility.ToJson(world.scatterLibrary) == prototypes && manager.RemovedInstances.IsRemoved(1729),
                    "UC-T06-02 migration preserves original node IDs/seeds/settings, native prototype references and removal storage; mixed=" + mixed);
                migrated = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(32, 100, 32), 100);
                migrated.AssignRun(run);
                var ground = new RiverChannelSnapshot(run, "migration-ground", new Rect(0, 0, 32, 32), Array.Empty<ChannelGraphPublication>(), legacy.outputs.heightmap, 0, 100);
                migrated.worldGraphGround = new WorldGraphGround(world.graph, ground);
                new GraphEvaluator(vegetation).Evaluate(migrated, new StopToken());
                foreach (string channel in new[] { "trees", "shrubs", "underwater" })
                    Check(legacy.outputs.scatter.TryGetValue(channel, out var before) && before.Count > 0 &&
                        migrated.outputs.scatter.TryGetValue(channel, out var after) && before.SequenceEqual(after),
                        "UC-T06-02 legacy/migrated " + channel + " candidates, transforms, prototypes and IDs are equal; mixed=" + mixed);
                Check(legacy.outputs.detailLayers.Count > 0 && legacy.outputs.detailLayers.Count == migrated.outputs.detailLayers.Count &&
                    legacy.outputs.detailLayers.All(p => migrated.outputs.detailLayers.TryGetValue(p.Key, out var grid) &&
                        p.Value.Cast<int>().SequenceEqual(grid.Cast<int>())), "UC-T06-02 legacy/migrated grass detail coverage is equal; mixed=" + mixed);
                Check(world.Chunks == null && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T06-02 reference/domain migration creates no terrain manager or new generation revision; explicit terrain acceptance remains required.");
            }
            catch (Exception error) { Check(false, "UC-T06-02 vegetation migration exception, mixed=" + mixed + ": " + error); }
            finally
            {
                legacy?.ReleaseProductMemory(); migrated?.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(root);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (legacyGraph != null) UnityEngine.Object.DestroyImmediate(legacyGraph);
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
            }
        }
    }
}
