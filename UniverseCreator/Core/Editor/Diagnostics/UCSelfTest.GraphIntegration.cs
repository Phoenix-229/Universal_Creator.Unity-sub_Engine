// UC-T05-11: shared sample authoring, all four native asset references and Unity-backed array/nested widget contracts.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Editor.Windows;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable] private sealed class WidgetProfile { public float weight = 2; public Vector3 offset = Vector3.one; }
        [Serializable] private sealed class WidgetFieldNode : Generator
        {
            public float[] samples = { 1, 2 };
            public List<WidgetProfile> profiles = new List<WidgetProfile> { new WidgetProfile() };
            public override string NodeName => "Serialized widget fixture";
            public override NodeCategory Category => NodeCategory.Utility;
            public override Port[] GetInputs() => SharedPorts.None;
            public override Port[] GetOutputs() => new[] { new Port("Field", PortType.Matrix, PortDirection.Output) };
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                var matrix = tile.CreateMatrix();
                for (int i = 0; i < matrix.arr.Length; i++) matrix.arr[i] = samples[0] + profiles[0].weight;
                return matrix;
            }
        }

        private static void TestGraphSerializedWidgets()
        {
            TestBiomeRuleControls(); // UC biome repair: exercise the real buttons and struct fields, not a guide contract.
            var graph = ScriptableObject.CreateInstance<Graph>(); var view = new UCGraphView(); Graph roundtrip = null;
            try
            {
                graph.SetDomain(UCGraphDomain.TerrainWater); var field = new WidgetFieldNode { id = "widget-native-field" }; graph.nodes.Add(field);
                string before = GraphSerializer.ToJson(graph); view.Controller.AutomaticGeneration = false; view.Populate(graph);
                Check(view.Q<PropertyField>("uc-serialized-samples") != null && view.Q<PropertyField>("uc-serialized-profiles") != null && GraphSerializer.ToJson(graph) == before,
                    "UC-T05-11 array and nested-prototype controls bind to the actual authored graph without changing saved values on opening.");
                using (var serialized = new SerializedObject(graph))
                {
                    var node = serialized.FindProperty("nodes").GetArrayElementAtIndex(0);
                    node.FindPropertyRelative("samples").GetArrayElementAtIndex(0).floatValue = 3;
                    node.FindPropertyRelative("profiles").GetArrayElementAtIndex(0).FindPropertyRelative("weight").floatValue = 4;
                    serialized.ApplyModifiedProperties(); Undo.FlushUndoRecordObjects();
                }
                var current = (WidgetFieldNode)graph.nodes[0];
                var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 2, 2), Vector3.zero, new Vector3(1, 100, 1), 100);
                Check(((Matrix)current.Generate(0, tile, new StopToken())).arr[0] == 7,
                    "UC-T05-11 edits to native array/nested fields reach the actual generator execution."); tile.ReleaseProductMemory();
                Undo.PerformUndo();
                Check(((WidgetFieldNode)graph.nodes[0]).samples[0] == 1 && ((WidgetFieldNode)graph.nodes[0]).profiles[0].weight == 2,
                    "UC-T05-11 native Undo restores both array elements and nested profile settings.");
                Undo.PerformRedo(); roundtrip = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));
                Check(roundtrip.nodes[0] is WidgetFieldNode copied && copied.samples[0] == 3 && copied.profiles[0].weight == 4 && copied.profiles[0].offset == Vector3.one,
                    "UC-T05-11 native Redo and portable reload preserve edited arrays, nested values and untouched vector settings.");
            }
            finally { view.Dispose(); Undo.ClearUndo(graph); UnityEngine.Object.DestroyImmediate(graph); if (roundtrip != null) UnityEngine.Object.DestroyImmediate(roundtrip); }
        }

        // UC biome repair: replay the owner's add/edit/clear/reload sequence against the production UI.
        private sealed class BiomeRulesTestWindow : EditorWindow { }

        private static void TestBiomeRuleControls()
        {
            var graph = ScriptableObject.CreateInstance<Graph>(); var view = new UCGraphView(); Graph copy = null;
            var window = ScriptableObject.CreateInstance<BiomeRulesTestWindow>();
            try
            {
                // UC biome repair: BaseField value changes require a live panel to exercise the normal editor event path.
                window.rootVisualElement.Add(view); window.Show();
                Check(view.panel != null, "UC biome Rules authoring fixture uses a native Editor panel.");
                graph.SetDomain(UCGraphDomain.TerrainWater);
                var output = new UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode { id = "rule-controls", defaultLayer = 2 };
                graph.nodes.Add(output); view.Controller.AutomaticGeneration = false; view.Populate(graph);
                Action<Button> click = button =>
                {
                    if (button == null) throw new InvalidOperationException("Missing biome rule button.");
                    typeof(Clickable).GetMethod("Invoke", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic).Invoke(button.clickable, new object[] { null });
                };
                click(view.Q<Button>("uc-biome-rule-add")); click(view.Q<Button>("uc-biome-rule-add"));
                Check(output.rules.Count == 2 && output.rules[0].biome != output.rules[1].biome && output.rules.All(r => r.weight == 1 && r.groundLayer == 2),
                    "UC biome Rules adds a second independent enabled rule with useful defaults.");
                var row = view.Q<Foldout>("uc-biome-rule-0");
                row.Q<IntegerField>("groundLayer").value = 3; row.Q<FloatField>("weight").value = 0.75f;
                row.Q<FloatField>("slopeStart").value = 25;
                Check(output.rules[0].groundLayer == 3 && output.rules[0].weight == 0.75f && output.rules[0].slopeStart == 25 && output.rules[0].slopeEnd == 46,
                    "UC biome Rules edits preserve other fields of the current serialized struct.");
                var staleRemove = row.Q<Button>("remove"); click(row.Q<Button>("down"));
                Check(output.rules[1].groundLayer == 3, "UC biome Rules reorder preserves the complete authored rule.");
                click(staleRemove);
                Check(output.rules.Count == 2, "UC biome Rules ignores a detached stale row callback after reorder.");
                Undo.IncrementCurrentGroup(); // UC biome repair: this removal must be independent of earlier same-frame fixture edits.
                click(view.Q<Foldout>("uc-biome-rule-1").Q<Button>("remove")); Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); view.Populate(graph);
                output = (UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode)graph.nodes[0];
                Check(output.rules.Count == 2 && output.rules[1].groundLayer == 3, "UC biome Rules Undo restores a removed rule and its edited fields.");
                Undo.PerformRedo(); view.Populate(graph); output = (UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode)graph.nodes[0];
                Check(output.rules.Count == 1, "UC biome Rules Redo reapplies removal without corrupting the graph.");
                for (int iteration = 0; iteration < 20; iteration++)
                { click(view.Q<Button>("uc-biome-rule-add")); click(view.Q<Foldout>("uc-biome-rule-1").Q<Button>("remove")); }
                click(view.Q<Foldout>("uc-biome-rule-0").Q<Button>("remove")); click(view.Q<Button>("uc-biome-rule-add"));
                Check(output.rules.Count == 1 && output.rules[0].weight == 1 && view.Q<Foldout>("uc-biome-rule-0").Q<FloatField>("highEnd") != null,
                    "UC biome Rules repeated add/remove, clearing and fresh creation retain every visible rule field.");
                copy = GraphSerializer.FromJson(GraphSerializer.ToJson(graph)); view.Populate(copy);
                Check(copy.nodes[0] is UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode loaded && loaded.rules.Count == 1 &&
                    loaded.rules[0].weight == 1 && view.Q<Button>("uc-biome-rule-add") != null,
                    "UC biome Rules portable save/reload restores the list and working authoring controls.");
                click(view.Q<Button>("uc-biome-rule-add"));
                Check(((UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode)copy.nodes[0]).rules.Count == 2,
                    "UC biome Rules can add the second rule after graph reload.");
            }
            finally
            {
                view.Dispose(); window.Close(); Undo.ClearUndo(graph); UnityEngine.Object.DestroyImmediate(graph);
                if (copy != null) { Undo.ClearUndo(copy); UnityEngine.Object.DestroyImmediate(copy); }
            }
        }

        private static void TestFourGraphSample()
        {
            string folder = "Assets/UCFourGraphAcceptance_" + Guid.NewGuid().ToString("N");
            // UC-T05-11: an empty batch startup has no active scene to restore through the native API.
            var setup = EditorSceneManager.GetSceneManagerSetup(); bool single = setup.Length == 0 || setup.Any(s => string.IsNullOrEmpty(s.path));
            Scene scene = default; GameObject root = null;
            var windows = new List<EditorWindow>();
            try
            {
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
                var assets = FourGraphSample.CreateAssets(folder);
                Check(assets.Count == 4 && assets.All(a => a.Key == a.Value.Domain && GraphValidationReport.Analyze(a.Value,
                    NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu).IsValid),
                    "UC-T05-11 shared starter saves four valid domain assets using the existing templates and typed catalog.");
                var main = assets[UCGraphDomain.TerrainWater];
                Check(assets.Where(a => a.Key != UCGraphDomain.TerrainWater).All(a => a.Value.nodes.OfType<WorldGraphInputNode>().All(n => n.mainGraph == main)) &&
                    main.nodes.Any(n => n is UniverseCreator.Nodes.Selectors.RiverNetworkNode),
                    "UC-T05-11 every companion selects the same validated main graph; procedural river trenches remain in the main producer.");
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
                foreach (var domain in GraphMigrationPlan.Domains) assets[domain] = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/" + domain + ".asset");
                root = FourGraphSample.CreateWorld(assets); SceneManager.MoveGameObjectToScene(root, scene);
                var world = root.GetComponent<UniverseCreatorObject>(); var run = world.GenerationScheduler.CurrentRun;
                var node = ScriptableObject.CreateInstance<NodeGraphWindow>(); node.SetGraph(world.graph); windows.Add(node);
                var vegetation = ScriptableObject.CreateInstance<VegetationGraphWindow>(); vegetation.SetWorld(world); windows.Add(vegetation);
                var lighting = ScriptableObject.CreateInstance<LightingGraphWindow>(); lighting.SetWorld(world); windows.Add(lighting);
                var texture = ScriptableObject.CreateInstance<TextureGraphWindow>(); texture.SetGraph(assets[UCGraphDomain.TextureMaterialShader]); windows.Add(texture);
                Check(world.GenerationScheduler.CurrentRun.Equals(run) && root.GetComponent<UCWorldGraphBindings>().VegetationRevision == 0 &&
                    root.GetComponentInChildren<UCWaterBody>(true).FollowTrench && !root.GetComponentInChildren<UCWaterBody>(true).digTerrain,
                    "UC-T05-11 opening all four sample windows does not Run/Apply; the manual river reader owns no terrain excavation.");
                foreach (var window in windows) UnityEngine.Object.DestroyImmediate(window); windows.Clear();
                Check(EditorSceneManager.SaveScene(scene, folder + "/SharedWorld.unity"),
                    "UC-T05-11 sample world and river mode save through the supported native scene API.");
                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); else EditorSceneManager.CloseScene(scene, true);
                scene = EditorSceneManager.OpenScene(folder + "/SharedWorld.unity", single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                root = scene.GetRootGameObjects().Single(g => g.name == "UC shared four-graph sample"); world = root.GetComponent<UniverseCreatorObject>();
                var binding = root.GetComponent<UCWorldGraphBindings>();
                Check(world.graph.Domain == UCGraphDomain.TerrainWater && binding.VegetationGraph.Domain == UCGraphDomain.Vegetation &&
                    binding.LightingGraph.Domain == UCGraphDomain.LightingClimateWeather && binding.TextureGraph.Domain == UCGraphDomain.TextureMaterialShader &&
                    binding.VegetationGraph.nodes.OfType<WorldGraphInputNode>().Single().mainGraph == world.graph && root.GetComponentInChildren<UCWaterBody>(true).FollowTrench,
                    "UC-T05-11 all four asset identities, main dependency and read-only river mode survive a real native scene reload.");
            }
            finally
            {
                foreach (var window in windows) if (window != null) UnityEngine.Object.DestroyImmediate(window);
                if (scene.IsValid() && scene.isLoaded) { if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); else EditorSceneManager.CloseScene(scene, true); }
                if (!single) EditorSceneManager.RestoreSceneManagerSetup(setup);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
