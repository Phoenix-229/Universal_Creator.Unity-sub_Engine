// UC-T05-03: shared companion chrome, canvas, persistence and explicit world Run/Cancel controls.
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>Shared editor host for explicitly applied companion domains, using the existing canvas and runtime binding services.</summary>
    public abstract class CompanionGraphWindow : EditorWindow
    {
        [SerializeField] private Graph graph;
        [SerializeField] private UniverseCreatorObject world;
        [SerializeField] private Vector2Int vegetationRegionCell; // UC-T06-03: window-owned region scope survives reload; it is not another placement authority.
        [SerializeField] private Vector2 vegetationPatchMinimum, vegetationPatchSize = new Vector2(100, 100);
        private UCGraphView view;
        private Label status;
        private Task<bool> run;
        private double nextStatus;
        private ObjectField graphPicker, worldPicker, mainPicker;
        private Graph appliedGraph;
        private int appliedDirtyCount;
        protected abstract UCGraphDomain Domain { get; }

        /// <summary>Opens an asset without rewriting or pruning its saved nodes; incompatible assets remain visible with diagnostics.</summary>
        /// <param name="asset">Graph asset, or null to clear the canvas.</param>
        public void SetGraph(Graph asset)
        {
            graph = asset; graphPicker?.SetValueWithoutNotify(graph); view?.Populate(graph); RefreshStatus();
        }

        /// <summary>Selects the owning UC world without applying or changing its graph bindings.</summary>
        /// <param name="owner">Existing world, or null to clear the binding selection.</param>
        public void SetWorld(UniverseCreatorObject owner)
        {
            world = owner; worldPicker?.SetValueWithoutNotify(world);
            if (graph == null && world != null)
            {
                var bindings = world.GetComponent<UCWorldGraphBindings>();
                SetGraph(Domain == UCGraphDomain.Vegetation ? bindings?.VegetationGraph : bindings?.LightingGraph);
            }
            RefreshStatus();
        }

        private void CreateGUI()
        {
            view?.Dispose(); rootVisualElement.Clear();
            var bar = new Toolbar(); bar.style.flexWrap = Wrap.Wrap; bar.style.height = StyleKeyword.Auto;
            graphPicker = new ObjectField("Graph") { objectType = typeof(Graph), allowSceneObjects = false, value = graph };
            graphPicker.RegisterValueChangedCallback(evt => SetGraph(evt.newValue as Graph)); bar.Add(graphPicker);
            worldPicker = new ObjectField("World") { objectType = typeof(UniverseCreatorObject), allowSceneObjects = true, value = world };
            worldPicker.RegisterValueChangedCallback(evt => SetWorld(evt.newValue as UniverseCreatorObject)); bar.Add(worldPicker);
            mainPicker = new ObjectField("Main Node Graph") { objectType = typeof(Graph), allowSceneObjects = false };
            mainPicker.SetEnabled(false); bar.Add(mainPicker); // UC-T05-03: show the authoritative world dependency without creating a second main binding.
            bar.Add(new ToolbarButton(() => SetGraph(CreateGraphAsset(false))) { text = "New Empty" });
            bar.Add(new ToolbarButton(() => SetGraph(CreateGraphAsset(true))) { text = "New Starter" });
            bar.Add(new ToolbarButton(() => view?.ShowAddNodeMenu()) { text = "Add Node" });
            bar.Add(new ToolbarButton(() => view?.FrameAll()) { text = "Frame All" });
            bar.Add(new ToolbarButton(() => { if (graph != null) { GraphAssetUtility.MarkDirty(graph); GraphAssetUtility.SavePending(); } }) { text = "Save" });
            bar.Add(new ToolbarButton(() => { if (graph != null) GraphSerializer.ExportWithDialog(graph); }) { text = "Export" });
            bar.Add(new ToolbarButton(() => { Graph imported = GraphSerializer.ImportWithDialog(); if (imported != null) SetGraph(imported); }) { text = "Import" });
            bar.Add(new ToolbarButton(RunGraph) { text = "Run / Apply", tooltip = "Capture and validate this graph, then apply it to the bound world." });
            if (Domain == UCGraphDomain.Vegetation)
            {
                var region = new Vector2IntField("Region cell (X/Z)") { name = "uc-vegetation-region-cell", value = vegetationRegionCell,
                    tooltip = "Integer terrain-region X/Z in this world. Run Region applies only this resident accepted cell; regional density shapes are authored with Vegetation Region Mask." };
                region.RegisterValueChangedCallback(evt => vegetationRegionCell = evt.newValue); bar.Add(region);
                bar.Add(new ToolbarButton(() => RunGraph(false, true)) { text = "Run Region", tooltip = "Apply the current graph to the selected accepted terrain region without rebuilding terrain." });
                var patchMinimum = new Vector2Field("Patch min XZ (m)") { name = "uc-vegetation-patch-minimum", value = vegetationPatchMinimum };
                patchMinimum.RegisterValueChangedCallback(evt => vegetationPatchMinimum = evt.newValue); bar.Add(patchMinimum);
                var patchSize = new Vector2Field("Patch size XZ (m)") { name = "uc-vegetation-patch-size", value = vegetationPatchSize,
                    tooltip = "Positive world-metre width/depth. Patch Run preserves accepted placement outside this half-open rectangle; apply intersecting regions first." };
                patchSize.RegisterValueChangedCallback(evt => vegetationPatchSize = evt.newValue); bar.Add(patchSize);
                bar.Add(new ToolbarButton(() => RunGraph(false, false, true)) { text = "Run Patch", tooltip = "Replace only placement inside the world rectangle, preserving accepted placement outside it." });
            }
            if (Domain == UCGraphDomain.LightingClimateWeather)
                bar.Add(new ToolbarButton(() => RunGraph(true)) { text = "Run Baseline Climate", tooltip = "Apply terrain-independent weather/climate fields before terrain generation; the clock keeps running." });
            bar.Add(new ToolbarButton(() =>
            {
                var bindings = world != null ? world.GetComponent<UCWorldGraphBindings>() : null;
                if (Domain == UCGraphDomain.Vegetation) bindings?.CancelVegetation(); else bindings?.CancelLighting();
            }) { text = "Cancel" });
            foreach (var child in bar.Children()) child.style.flexShrink = 0;
            rootVisualElement.Add(bar);
            status = new Label(); status.style.whiteSpace = WhiteSpace.Normal; rootVisualElement.Add(status);
            view = new UCGraphView { HostWindow = this };
            bar.Insert(0, view.CreateNavigationToolbar()); // UC-T08-15: vegetation/lighting retain the same navigation owner.
            view.Controller.AutomaticGeneration = false;
            view.EntryFilter = entry => GraphDomainRules.Supports(entry.graphDomains, Domain);
            view.StructureChanged += RefreshStatus; view.style.flexGrow = 1;
            rootVisualElement.Add(view); view.Populate(graph);
            EditorApplication.update -= Tick; EditorApplication.update += Tick;
            RefreshStatus();
        }

        protected virtual Graph CreateGraphAsset(bool starter)
        {
            var created = ScriptableObject.CreateInstance<Graph>(); created.SetDomain(Domain);
            if (starter && Domain == UCGraphDomain.Vegetation)
            {
                var input = new WorldGraphInputNode { editorPosition = new Vector2(30, 100) };
                created.AddNode(input);
                var tree = new UniverseCreator.Nodes.Scatter.TreeScatterNode { editorPosition = new Vector2(430, 50) };
                var shrub = new UniverseCreator.Nodes.Scatter.ShrubScatterNode { editorPosition = new Vector2(430, 300) };
                var grass = new UniverseCreator.Nodes.Scatter.GrassDetailScatterNode { editorPosition = new Vector2(430, 550) };
                foreach (var output in new Generator[] { tree, shrub, grass })
                {
                    output.OnCreatedFresh(); created.AddNode(output);
                    created.connections.Add(new Connection(input.id, 0, output.id, 0));
                    created.connections.Add(new Connection(input.id, 1, output.id, 1));
                }
            }
            else if (starter) // UC-T05-04: neutral climate/weather and lighting starter; baseline has no final-ground dependency.
            {
                var zero = new UniverseCreator.Nodes.Creators.ConstantNode { value = 0, editorPosition = new Vector2(30, 100) };
                var weather = new UniverseCreator.Nodes.Outputs.WeatherOutputNode { editorPosition = new Vector2(430, 50) };
                var light = new UniverseCreator.Nodes.Outputs.LightingOutputNode { editorPosition = new Vector2(430, 350) };
                created.AddNode(zero); created.AddNode(weather); created.AddNode(light);
                created.AddNode(new WorldGraphInputNode { editorPosition = new Vector2(30, 450) });
                created.connections.Add(new Connection(zero.id, 0, weather.id, 1));
                created.connections.Add(new Connection(zero.id, 0, light.id, 4));
            }
            return GraphAssetUtility.SaveAsset(created, GraphAssetUtility.DefaultFolder,
                Domain == UCGraphDomain.Vegetation ? "Vegetation Graph" : Domain + " Graph");
        }

        private void RunGraph() => RunGraph(false);

        private void RunGraph(bool baselineClimate, bool regionOnly = false, bool patchOnly = false)
        {
            if (graph == null || world == null) { ShowNotification(new GUIContent("Select a graph and its UC world.")); return; }
            if (!graph.HasSupportedDomainMetadata || graph.Domain != Domain || !view.RefreshValidation().IsValid)
            { ShowNotification(new GUIContent("Fix domain/graph errors before applying; preserve legacy assets for migration.")); return; }
            var patch = new Rect(vegetationPatchMinimum, vegetationPatchSize);
            if (patchOnly)
                try { GraphVegetationPatch.ValidateBounds(patch); }
                catch (ArgumentException error) { ShowNotification(new GUIContent(error.Message)); return; }
            var bindings = world.GetComponent<UCWorldGraphBindings>();
            if (bindings == null) bindings = Undo.AddComponent<UCWorldGraphBindings>(world.gameObject);
            Undo.RecordObject(bindings, "Bind companion graph");
            if (Domain == UCGraphDomain.Vegetation) VegetationGraphAuthoring.Bind(bindings, graph); else bindings.LightingGraph = graph; // UC-T06-15: share manager Undo/binding behavior.
            EditorUtility.SetDirty(bindings);
            appliedGraph = regionOnly || patchOnly ? null : graph; appliedDirtyCount = EditorUtility.GetDirtyCount(graph); // UC-T06-03: partial application must not claim every resident region is current.
            run = Domain == UCGraphDomain.Vegetation ? VegetationGraphAuthoring.Run(bindings,
                regionOnly ? new Coord(vegetationRegionCell.x, vegetationRegionCell.y) : (Coord?)null, patchOnly ? patch : (Rect?)null) : bindings.RunLightingAsync(baselineClimate);
            RefreshStatus();
        }

        private void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextStatus) return;
            nextStatus = EditorApplication.timeSinceStartup + 0.25;
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (status == null) return;
            mainPicker?.SetValueWithoutNotify(world != null ? world.graph : null);
            if (graph == null) { status.text = "Select or create a " + Domain + " graph; assign your licensed prototypes on its UC world."; return; }
            string prefix = graph.name + " — " + graph.nodes.Count + " nodes, " + graph.connections.Count + " links. ";
            if (graph.Domain != Domain) { status.text = prefix + "Incompatible/legacy domain: retained for inspection and migration."; return; }
            if (world == null) { status.text = prefix + "Pending: select the UC world whose accepted main ground this graph uses."; return; }
            var bindings = world.GetComponent<UCWorldGraphBindings>();
            bindings?.RefreshVegetationDependencies();
            status.text = prefix + (bindings != null ? (Domain == UCGraphDomain.Vegetation ? bindings.VegetationStatus : bindings.LightingStatus) :
                "Pending: Run / Apply binds this graph to the selected world.");
            if (run != null && run.Status == TaskStatus.RanToCompletion && run.Result &&
                (!ReferenceEquals(graph, appliedGraph) || EditorUtility.GetDirtyCount(graph) != appliedDirtyCount))
                status.text += " Unapplied authored changes: Run / Apply captures them; streaming keeps the accepted plan.";
            if (run != null && run.IsFaulted) status.text += " Run failed: " + run.Exception.GetBaseException().Message;
            if (Domain == UCGraphDomain.Vegetation && VegetationGraphAuthoring.HasPendingChanges(bindings))
                status.text += " Current authoring needs Run / Apply; accepted streaming retains its captured inputs."; // UC-T06-15: manager and graph edits share the pending readout.
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            view?.Dispose(); view = null; // Runtime Run belongs to the world; closing authoring UI does not cancel accepted streaming.
        }
    }
}
