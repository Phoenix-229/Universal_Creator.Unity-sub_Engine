// UC-T05-11: a shared-world starter uses the existing graph templates, accepted-input node and river authoring owner.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Combiners;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    internal static class FourGraphSample
    {
        [MenuItem("Tools/Universe Creator/Graphs/Create Shared Four-Graph Sample")]
        private static void CreateSample()
        {
            string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/UniverseCreator/SharedWorldSample");
            AssetDatabase.CreateFolder("Assets/UniverseCreator", System.IO.Path.GetFileName(folder));
            var assets = CreateAssets(folder);
            var root = CreateWorld(assets);
            root.SetActive(true);
            Undo.RegisterCreatedObjectUndo(root, "Create UC shared four-graph sample");
            Selection.activeGameObject = root;
        }

        internal static Dictionary<UCGraphDomain, Graph> CreateAssets(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) throw new ArgumentException("Select an existing project asset folder.", nameof(folder));
            var assets = new Dictionary<UCGraphDomain, Graph>();
            try
            {
                var main = GraphTemplates.CreateIslands(); assets.Add(UCGraphDomain.TerrainWater, main);
                var height = main.nodes.OfType<HeightOutputNode>().Single();
                Connection source = main.GetInputConnection(height.id, 0);
                var network = new RiverNetworkNode { solveResolution = 64, seaLevel = height.seaLevel, editorPosition = new Vector2(740, 200) };
                main.AddNode(network); main.connections.RemoveAll(c => c.targetNodeId == height.id && c.targetPortIndex == 0);
                main.AddConnection(new Connection(source.sourceNodeId, source.sourcePortIndex, network.id, 0));
                main.AddConnection(new Connection(network.id, 0, height.id, 0)); height.editorPosition = new Vector2(980, 200);

                var vegetation = ScriptableObject.CreateInstance<Graph>(); vegetation.SetDomain(UCGraphDomain.Vegetation); assets.Add(UCGraphDomain.Vegetation, vegetation);
                var ground = new WorldGraphInputNode { mainGraph = main, editorPosition = new Vector2(30, 100) };
                var dry = new InvertNode { editorPosition = new Vector2(280, 350) };
                var validDryGround = new MinimumNode { editorPosition = new Vector2(540, 350) };
                vegetation.nodes.AddRange(new Generator[] { ground, dry, validDryGround });
                vegetation.connections.Add(new Connection(ground.id, 5, dry.id, 0));
                vegetation.connections.Add(new Connection(ground.id, 1, validDryGround.id, 0));
                vegetation.connections.Add(new Connection(dry.id, 0, validDryGround.id, 1));
                int y = 40;
                foreach (var scatter in new ScatterNodeBase[] { new TreeScatterNode(), new ShrubScatterNode(), new GrassDetailScatterNode() })
                {
                    scatter.OnCreatedFresh(); scatter.editorPosition = new Vector2(820, y); y += 280;
                    vegetation.AddNode(scatter); vegetation.connections.Add(new Connection(ground.id, 0, scatter.id, 0));
                    vegetation.connections.Add(new Connection(validDryGround.id, 0, scatter.id, 1));
                }
                var lighting = ScriptableObject.CreateInstance<Graph>(); lighting.SetDomain(UCGraphDomain.LightingClimateWeather); assets.Add(UCGraphDomain.LightingClimateWeather, lighting);
                var input = new WorldGraphInputNode { mainGraph = main, editorPosition = new Vector2(30, 300) };
                var zero = new ConstantNode { value = 0, editorPosition = new Vector2(30, 50) };
                var weather = new WeatherOutputNode { editorPosition = new Vector2(430, 50) };
                var light = new LightingOutputNode { editorPosition = new Vector2(430, 350) };
                lighting.nodes.AddRange(new Generator[] { input, zero, weather, light });
                lighting.connections.Add(new Connection(zero.id, 0, weather.id, 1));
                lighting.connections.Add(new Connection(input.id, 0, light.id, 4));

                var texture = TextureGraphTemplates.CreateBasicSurface(); assets.Add(UCGraphDomain.TextureMaterialShader, texture);
                var accepted = new WorldGraphInputNode { mainGraph = main, editorPosition = new Vector2(800, 500) };
                var snow = new SnowCoverNode { editorPosition = new Vector2(1040, 200) };
                var output = texture.nodes.OfType<ShaderOutputNode>().Single();
                Connection surface = texture.GetInputConnection(output.id, 0);
                texture.nodes.Add(accepted); texture.nodes.Add(snow);
                texture.connections.RemoveAll(c => c.targetNodeId == output.id && c.targetPortIndex == 0);
                texture.connections.Add(new Connection(surface.sourceNodeId, surface.sourcePortIndex, snow.id, 0));
                texture.connections.Add(new Connection(accepted.id, 0, snow.id, 1));
                texture.connections.Add(new Connection(snow.id, 0, output.id, 0)); output.editorPosition = new Vector2(1300, 200);
                output.outputFolder = folder + "/Materials"; output.materialName = "SharedWorldSurface";
                foreach (var asset in assets)
                {
                    GraphValidationReport.Analyze(asset.Value, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu).ThrowIfInvalid();
                    asset.Value.name = asset.Key.ToString(); AssetDatabase.CreateAsset(asset.Value,
                        AssetDatabase.GenerateUniqueAssetPath(folder + "/" + asset.Key + ".asset")); AssetDatabase.SaveAssetIfDirty(asset.Value);
                }
                return assets;
            }
            catch
            {
                foreach (var graph in assets.Values)
                    if (AssetDatabase.Contains(graph)) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(graph));
                    else UnityEngine.Object.DestroyImmediate(graph);
                throw;
            }
        }

        internal static GameObject CreateWorld(Dictionary<UCGraphDomain, Graph> assets)
        {
            var root = new GameObject("UC shared four-graph sample"); root.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>(); world.autoRegenerate = false; world.graph = assets[UCGraphDomain.TerrainWater];
            var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.VegetationGraph = assets[UCGraphDomain.Vegetation];
            bindings.LightingGraph = assets[UCGraphDomain.LightingClimateWeather]; bindings.TextureGraph = assets[UCGraphDomain.TextureMaterialShader];
            var riverObject = new GameObject("Read trench river - place nodes on an accepted channel after Node Graph Run"); riverObject.transform.SetParent(root.transform, false);
            var river = riverObject.AddComponent<UCWaterBody>(); river.SetKindOnce(WaterBodyKind.River); river.shape = UCWaterBody.ShapeMode.RiverSpline;
            river.digTerrain = false; river.SetRiverTerrainMode(RiverTerrainMode.ReadProceduralTrench);
            // No guessed course or buyer content is installed. Existing node placement fits the accepted trench;
            // vegetation prototypes, texture maps and licensed audio clips are deliberately empty authoring slots.
            return root;
        }
    }
}
