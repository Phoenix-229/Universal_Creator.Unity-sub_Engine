// UC-T05-09/10: read-only migration analysis and recoverable domain partitions use actual saved assets and native Undo.
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestGraphMigrationAnalysis()
        {
            var root = new GameObject("UC legacy migration analysis"); root.SetActive(false);
            var source = ScriptableObject.CreateInstance<Graph>();
            var foreign = ScriptableObject.CreateInstance<Graph>();
            Graph missing = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.graph = source;
                var height = new ConstantNode { id = "migration-height", value = 0.6f };
                var temperature = new ConstantNode { id = "migration-temperature", value = 4 };
                var heightOutput = new HeightOutputNode { id = "migration-height-output" };
                var weatherOutput = new WeatherOutputNode { id = "migration-weather-output" };
                source.nodes.AddRange(new Generator[] { height, temperature, heightOutput, weatherOutput });
                source.connections.Add(new Connection(height.id, 0, heightOutput.id, 0));
                source.connections.Add(new Connection(temperature.id, 0, weatherOutput.id, 1));
                string original = GraphSerializer.ToJson(source); var run = world.GenerationScheduler.CurrentRun;
                var plan = GraphMigrationPlan.Analyze(source, world);
                Check(plan.CanApply && plan.IsCurrent() && plan.Destinations[height.id] == UCGraphDomain.TerrainWater &&
                    plan.Destinations[temperature.id] == UCGraphDomain.LightingClimateWeather && GraphSerializer.ToJson(source) == original &&
                    world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T05-09 migration analysis inventories real terminal chains/IDs and changes neither serialized content nor the world run.");
                source.connections[1] = new Connection(height.id, 0, weatherOutput.id, 1);
                var shared = GraphMigrationPlan.Analyze(source, world);
                Check(!shared.CanApply && shared.Errors.Any(e => e.Contains("Shared node migration-height")) && !plan.IsCurrent(),
                    "UC-T05-09 shared intermediates require an explicit accepted-input boundary and invalidate the old analysis.");
                source.connections[1] = new Connection(temperature.id, 0, weatherOutput.id, 1);
                world.graph = foreign;
                Check(!GraphMigrationPlan.Analyze(source, world).CanApply,
                    "UC-T05-09 migration rejects a foreign world reference without silently selecting another main graph.");
                missing = GraphSerializer.FromJson(original.Replace(typeof(ConstantNode).FullName, "Missing.Plugin.MigrationField")); world.graph = missing;
                var unresolved = GraphMigrationPlan.Analyze(missing, world);
                Check(!unresolved.CanApply && unresolved.Details.Any(e => e.Contains("Preserved unresolved slot")) && missing.unresolvedNodes.Count > 0,
                    "UC-T05-09 missing-plugin types remain visible and preserved while unsafe migration is blocked.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(foreign); if (missing != null) UnityEngine.Object.DestroyImmediate(missing); }
        }

        private static void TestGraphMigrationExecution()
        {
            string folder = "Assets/UCGraphMigrationAcceptance_" + Guid.NewGuid().ToString("N");
            var root = new GameObject("UC graph migration transaction"); root.SetActive(false);
            var source = ScriptableObject.CreateInstance<Graph>();
            UniverseCreatorObject world = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
                var field = new ConstantNode { id = "migration-execution-field", value = 0.65f };
                var temperature = new ConstantNode { id = "migration-execution-temperature", value = 5 };
                var height = new HeightOutputNode { id = "migration-execution-height" };
                var weather = new WeatherOutputNode { id = "migration-execution-weather" };
                source.nodes.AddRange(new Generator[] { field, temperature, height, weather });
                source.connections.Add(new Connection(field.id, 0, height.id, 0));
                source.connections.Add(new Connection(temperature.id, 0, weather.id, 1));
                AssetDatabase.CreateAsset(source, folder + "/Legacy.asset");
                world = root.AddComponent<UniverseCreatorObject>(); world.graph = source;
                string original = GraphSerializer.ToJson(source);
                bool injected = false;
                try { GraphMigrationExecutor.Apply(GraphMigrationPlan.Analyze(source, world), folder,
                    count => { if (count == 1) throw new InvalidOperationException("Injected migration write failure"); }); }
                catch (InvalidOperationException error) { injected = error.Message == "Injected migration write failure"; }
                Check(injected && world.graph == source && world.GetComponent<UCWorldGraphBindings>() == null &&
                    AssetDatabase.FindAssets("t:Graph", new[] { folder }).Length == 1 && GraphSerializer.ToJson(source) == original,
                    "UC-T05-10 failure after native asset creation removes only new replacements and preserves source/references.");
                var result = GraphMigrationExecutor.Apply(GraphMigrationPlan.Analyze(source, world), folder);
                var binding = world.GetComponent<UCWorldGraphBindings>();
                Check(result.Assets.Count == 4 && result.Assets.Values.Sum(g => g.nodes.Count) == source.nodes.Count &&
                    result.Assets.Values.SelectMany(g => g.nodes).Select(n => n.id).Distinct().Count() == source.nodes.Count &&
                    world.graph == result.Assets[UCGraphDomain.TerrainWater] && binding.LightingGraph == result.Assets[UCGraphDomain.LightingClimateWeather] &&
                    binding.VegetationGraph.Domain == UCGraphDomain.Vegetation && binding.TextureGraph.Domain == UCGraphDomain.TextureMaterialShader,
                    "UC-T05-10 supported Editor asset creation partitions every stable node ID once and binds all four domains atomically.");
                var legacy = new TileData(Coord.Zero, new CoordRect(0, 0, 3, 3), Vector3.zero, new Vector3(2, 100, 2), 100);
                var terrain = new TileData(Coord.Zero, new CoordRect(0, 0, 3, 3), Vector3.zero, new Vector3(2, 100, 2), 100);
                var lighting = new TileData(Coord.Zero, new CoordRect(0, 0, 3, 3), Vector3.zero, new Vector3(2, 100, 2), 100);
                try
                {
                    new GraphEvaluator(source).Evaluate(legacy, new StopToken());
                    new GraphEvaluator(world.graph).Evaluate(terrain, new StopToken());
                    new GraphEvaluator(binding.LightingGraph).Evaluate(lighting, new StopToken());
                    Check(legacy.outputs.heightmap.Cast<float>().SequenceEqual(terrain.outputs.heightmap.Cast<float>()) &&
                        legacy.outputs.weatherTemperatureBias.SequenceEqual(lighting.outputs.weatherTemperatureBias),
                        "UC-T05-10 legacy/migrated deterministic height and weather products are exactly equal on the same physical sample lattice.");
                }
                finally { legacy.ReleaseProductMemory(); terrain.ReleaseProductMemory(); lighting.ReleaseProductMemory(); }
                Undo.PerformUndo();
                Check(world.graph == source && world.GetComponent<UCWorldGraphBindings>() == null && AssetDatabase.Contains(source) &&
                    result.Assets.Values.All(AssetDatabase.Contains),
                    "UC-T05-10 native Undo restores original world references while retaining recoverable replacement assets.");
                Undo.PerformRedo(); binding = world.GetComponent<UCWorldGraphBindings>();
                Check(world.graph == result.Assets[UCGraphDomain.TerrainWater] && binding != null &&
                    binding.LightingGraph == result.Assets[UCGraphDomain.LightingClimateWeather] && GraphSerializer.ToJson(source) == original,
                    "UC-T05-10 native Redo restores the complete reference switch without modifying the legacy asset.");
                foreach (var asset in result.Assets)
                {
                    string path = AssetDatabase.GetAssetPath(asset.Value); AssetDatabase.ImportAsset(path);
                    var loaded = AssetDatabase.LoadAssetAtPath<Graph>(path);
                    Check(loaded != null && loaded.Domain == asset.Key && loaded.nodes.Select(n => n.id).SequenceEqual(asset.Value.nodes.Select(n => n.id)),
                        "UC-T05-10 " + asset.Key + " replacement retains domain and stable node identities after native save/reimport.");
                }
            }
            finally
            {
                if (world != null) { Undo.ClearUndo(world); var binding = world.GetComponent<UCWorldGraphBindings>(); if (binding != null) Undo.ClearUndo(binding); }
                UnityEngine.Object.DestroyImmediate(root);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                else if (source != null && !AssetDatabase.Contains(source)) UnityEngine.Object.DestroyImmediate(source);
            }
        }
    }
}
