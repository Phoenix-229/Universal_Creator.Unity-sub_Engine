// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TextureGraphWindow.cs
//  Purpose: The Texture Graph canvas. Same GraphView, same asset type and same
//           evaluator as the height graph; a filtered node menu and a terminal
//           Apply step are the whole of the difference.
//
//  WHY IT SHARES THE HEIGHT GRAPH'S CANVAS
//  ---------------------------------------
//  A texture node and a height node are both Generators. They serialise through
//  the same Graph asset, cache through the same TileData dictionary and are
//  pulled by the same GraphEvaluator, because that machinery was written against
//  `object` rather than against Matrix. Giving this window its own canvas class
//  would have duplicated every wire-dragging, trash-bin and undo bug in the
//  project, and the two copies would have drifted the first time one was fixed.
//
//  So the differences are three, and they are all small:
//    1. CategoryFilter narrows the add-node search to Texture/Surface/Water.
//    2. The presets come from TextureGraphTemplates, not GraphTemplates.
//    3. There is an Apply step, because a texture graph's result is a Material
//       rather than a heightfield the chunk system was already going to write.
//
//  WHY APPLY IS A BUTTON AND NOT AUTOMATIC
//  ---------------------------------------
//  The height graph regenerates on commit because a heightfield is cheap to throw
//  away - the next run overwrites it. A texture graph writes project assets: a
//  Material, up to four TerrainLayers, baked AO. Doing that on every slider
//  release would mean AssetDatabase writes at the rate of a drag, and a texture
//  import queue that never empties. The user says when.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Windows
{
    public class TextureGraphWindow : EditorWindow
    {
        // Serialized so the open graph and the chosen terrain survive a domain
        // reload. Both are assets or scene objects with stable ids, so this works
        // for the same reason NodeGraphWindow's graph field does.
        [SerializeField] private Graph graph;
        [SerializeField] private UnityEngine.Terrain targetTerrain;
        [SerializeField] private UniverseCreatorObject targetWorld; // UC-T05-06: inspector-opened texture work stays on the explicitly bound world.
        [SerializeField] private int bakeResolution = 257;

        private UCGraphView view;
        private VisualElement emptyState;
        private Label statusLabel;
        private Label nameLabel;
        private ObjectField terrainField;
        private TextureGraphBake bake; // UC-T07-09: one explicit captured world run, yielding between graph outputs.
        private readonly TextureBakeCache bakeCache = new TextureBakeCache();
        internal bool IsBakeRunning => bake != null && !bake.IsComplete;

        /// <summary>
        /// Bake sizes offered in the toolbar. These are the heightmap-style
        /// (2^n)+1 values, because the bakers march a Matrix whose edge samples
        /// must line up with a terrain's, and 256 would be one sample short.
        /// </summary>
        private static readonly int[] BakeSizes = { 129, 257, 513 };

        // No [MenuItem] here - UniverseCreatorMenus owns every menu path so the
        // ordering stays in one file. A duplicate path would shadow that entry.
        public static void OpenWindow()
        {
            TextureGraphWindow window = GetWindow<TextureGraphWindow>("Universe Creator - Texture Graph");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        /// <summary>Loads a graph into the canvas. Safe before the UI is built:
        /// CreateGUI populates from the serialized field.</summary>
        public void SetGraph(Graph g)
        {
            CancelBake(); bakeCache.Dispose(); // UC-T07-09: replacements retire the former graph's bake/cache owner.
            graph = g;
            if (view != null) view.Populate(graph);
            RefreshChrome();
        }

        /// <summary>Selects the owning world for texture application without running a graph or changing terrain.</summary>
        /// <param name="world">Target UC world, or null for explicit standalone terrain authoring.</param>
        /// <remarks>A changed world clears a foreign terrain selection; Apply still validates the actual terrain owner.</remarks>
        public void SetWorld(UniverseCreatorObject world)
        {
            CancelBake(); bakeCache.Dispose(); // UC-T07-09: a world switch cannot publish the former owner's staged material.
            targetWorld = world;
            if (targetTerrain != null && targetTerrain.GetComponentInParent<UniverseCreatorObject>(true) != world)
            { targetTerrain = null; terrainField?.SetValueWithoutNotify(null); }
        }

        private void CreateGUI()
        {
            view?.Dispose(); rootVisualElement.Clear(); // UC-T05-02: retire a previous UI's resources before replacing it.
            view = new UCGraphView();
            rootVisualElement.Add(BuildToolbar());
            view.Controller.AutomaticGeneration = false; // UC-T05-02: authoring is pending until the explicit Apply button.
            view.style.flexGrow = 1f;
            view.HostWindow = this;   // so the add-node picker opens over this window
            // UC-T07-09: captured Apply yields between graph outputs; native dependencies still require their declared owner thread.
            view.AllowedExecutionDomains = NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu;

            // The one line that makes this a texture graph rather than a second
            // height graph. Everything else in this window is chrome.
            view.EntryFilter = AcceptNode; // UC-T05-05: shared utilities/field combiners are available when supported by the explicit texture domain.

            view.StructureChanged += RefreshChrome;
            rootVisualElement.Add(view);

            emptyState = BuildEmptyState();
            rootVisualElement.Add(emptyState);

            view.Populate(graph);
            RefreshChrome();
        }

        private void OnDisable()
        {
            CancelBake(); bakeCache.Dispose(); // UC-T07-09: no captured bake or retained texture products survive closing the window.
            view?.Dispose(); view = null; // UC-T05-02: no retained provider or editor callbacks after window close.
        }

        // ------------------------------------------------------------------
        //  Toolbar
        // ------------------------------------------------------------------

        private VisualElement BuildToolbar()
        {
            Toolbar bar = new Toolbar();
            bar.Add(view.CreateNavigationToolbar()); // UC-T08-15: shared hand/pointer navigation.

            bar.Add(new ToolbarButton(() => { if (graph != null) view.ShowAddNodeMenu(); })
            { text = "Add Node" });

            bar.Add(new ToolbarButton(() => { if (view != null) view.FrameAll(); })
            { text = "Frame All" });

            bar.Add(new ToolbarButton(() =>
            {
                if (graph == null) return;
                GraphAssetUtility.MarkDirty(graph);
                GraphAssetUtility.SavePending();
            })
            { text = "Save" });

            bar.Add(new ToolbarButton(() =>
            {
                if (graph == null) return;
                string written = GraphSerializer.ExportWithDialog(graph);
                if (!string.IsNullOrEmpty(written))
                    ShowNotification(new GUIContent("Exported " + System.IO.Path.GetFileName(written)));
            })
            { text = "Export" });

            bar.Add(new ToolbarButton(() =>
            {
                Graph imported = GraphSerializer.ImportWithDialog();
                if (imported == null) return;
                SetGraph(imported);
                ShowNotification(new GUIContent("Imported " + imported.name));
            })
            { text = "Import" });

            // ---- apply group -------------------------------------------------

            VisualElement gap = new VisualElement();
            gap.style.width = 12f;
            bar.Add(gap);

            terrainField = new ObjectField
            {
                objectType = typeof(UnityEngine.Terrain),
                allowSceneObjects = true,
                value = targetTerrain,
                tooltip = "Terrain the material is written to. Leave empty and Apply picks " +
                          "the first Universe Creator chunk in the scene and fills this in."
            };
            terrainField.style.width = 180f;
            terrainField.RegisterValueChangedCallback(evt =>
            {
                CancelBake(); bakeCache.Dispose(); // UC-T07-09: changing the target withdraws the former request and cache ownership.
                targetTerrain = evt.newValue as UnityEngine.Terrain;
            });
            bar.Add(terrainField);

            // A popup rather than a free int: the bakers are O(pixels x rays x
            // steps), so the difference between 257 and 1025 is a two second wait
            // and a forty second one. Three named steps make that cost visible.
            PopupField<int> bakeField = new PopupField<int>(
                new System.Collections.Generic.List<int>(BakeSizes),
                IndexOfBakeSize(bakeResolution),
                v => "Bake " + v,
                v => v + " x " + v);
            bakeField.tooltip = "Resolution used for baked maps (occlusion, snow traction). " +
                                "Image nodes declare their own dimensions and colour/data channels; Image to Texture uploads them for material use."; // UC-T07-02: distinguish CPU images from material parameters.
            bakeField.style.width = 90f;
            bakeField.RegisterValueChangedCallback(evt => { CancelBake(); bakeResolution = evt.newValue; }); // UC-T07-09: changing size invalidates the pending request.
            bar.Add(bakeField);

            bar.Add(new ToolbarButton(ApplyToTerrain)
            {
                text = "Run / Apply",
                tooltip = "Capture current graph/world inputs, bake incrementally, then apply each loaded terrain cell using its own accepted fields."
            });
            bar.Add(new ToolbarButton(CancelBake) { text = "Cancel Bake" }); // UC-T07-09: cancellation is available while graph/pixel work is staged.

            nameLabel = new Label();
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            nameLabel.style.marginLeft = 8f;
            bar.Add(nameLabel);

            VisualElement spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            bar.Add(spacer);

            statusLabel = new Label();
            statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            statusLabel.style.marginRight = 8f;
            bar.Add(statusLabel);

            // A narrow window used to crush this bar: each ToolbarButton shrank
            // below its own label until the names collided and clipped into one
            // another ("Add Node" reading as "dd No", overlapping "Frame All").
            // Stop every control from shrinking below its content, and let the bar
            // wrap onto a second row instead of overlapping when the width runs out.
            // A wide window still lays out as one row, so its look is unchanged.
            foreach (VisualElement child in bar.Children())
                child.style.flexShrink = 0f;
            bar.style.flexWrap = Wrap.Wrap;
            bar.style.height = StyleKeyword.Auto;

            return bar;
        }

        private static int IndexOfBakeSize(int value)
        {
            for (int i = 0; i < BakeSizes.Length; i++)
                if (BakeSizes[i] == value) return i;
            return 1;   // 257
        }

        private void RefreshChrome()
        {
            bool loaded = graph != null;
            if (view != null) view.CategoryFilter = loaded && graph.Domain == UCGraphDomain.LegacyMixed ?
                (System.Func<NodeCategory, bool>)NodeCategoryColors.IsTextureGraph : null; // UC-T05-05: preserve the legacy palette until migration.

            if (emptyState != null)
                emptyState.style.display = loaded ? DisplayStyle.None : DisplayStyle.Flex;

            if (view != null)
                view.style.display = loaded ? DisplayStyle.Flex : DisplayStyle.None;

            if (nameLabel != null)
                nameLabel.text = loaded ? graph.name : "No graph";

            if (statusLabel != null)
            {
                statusLabel.text = loaded
                    ? graph.nodes.Count + " nodes  |  " + graph.connections.Count + " links  |  " +
                        (graph.Domain == UCGraphDomain.LegacyMixed ? "Legacy mixed graph — preserved for migration" :
                         graph.Domain == UCGraphDomain.TextureMaterialShader ? "Textures / Materials / Shaders" : "Incompatible domain — preserved for inspection")
                    : string.Empty;
            }
        }

        // UC-T05-05: metadata controls new authoring; loading another domain remains a non-destructive inspection.
        private bool AcceptNode(NodeCatalogEntry entry) => graph != null && graph.HasSupportedDomainMetadata &&
            (graph.Domain == UCGraphDomain.LegacyMixed ||
             (graph.Domain == UCGraphDomain.TextureMaterialShader && GraphDomainRules.Supports(entry.graphDomains, UCGraphDomain.TextureMaterialShader)));

        // ------------------------------------------------------------------
        //  Apply
        // ------------------------------------------------------------------

        /// <summary>
        /// UC-T07-09: captures one explicit world texture run, yielding between bounded graph outputs before applying its regions.
        /// </summary>
        private void ApplyToTerrain()
        {
            CancelBake();
            if (graph == null)
            {
                ShowNotification(new GUIContent("No graph loaded."));
                return;
            }

            if (!graph.HasSupportedDomainMetadata ||
                (graph.Domain != UCGraphDomain.LegacyMixed && graph.Domain != UCGraphDomain.TextureMaterialShader))
            {
                ShowNotification(new GUIContent("Select a Texture Graph; this asset is preserved for inspection and migration."));
                return; // UC-T05-05: inspecting a main/companion asset cannot accidentally run its outputs through texture Apply.
            }

            // UC-T01-09: analyze before evaluating any node or allocating/applying generated resources.
            GraphValidationReport report = view != null ? view.RefreshValidation() : GraphValidationReport.Analyze(graph,
                NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu);
            if (!report.IsValid)
            {
                Debug.LogError("Universe Creator: texture graph is invalid. " + string.Join("\n", graph.Validate()));
                ShowNotification(new GUIContent("Fix the graph errors shown on its nodes before applying."));
                return;
            }

            UnityEngine.Terrain terrain = ResolveTerrain();
            if (terrain == null)
            {
                ShowNotification(new GUIContent("No terrain found. Assign one in the toolbar."));
                return;
            }

            if (terrain.terrainData == null)
            {
                ShowNotification(new GUIContent("That terrain has no TerrainData."));
                return;
            }

            try
            {
                bake = new TextureGraphBake(graph, terrain, targetWorld, BakeSizes[IndexOfBakeSize(bakeResolution)], bakeCache);
                EditorApplication.update += PumpBake;
                if (statusLabel != null) statusLabel.text = bake.Status;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Universe Creator: texture graph capture failed - " + ex.Message);
                ShowNotification(new GUIContent(ex.Message));
            }
        }

        internal void PumpBake()
        {
            if (bake == null) { EditorApplication.update -= PumpBake; return; }
            bake.Pump(); if (statusLabel != null) statusLabel.text = bake.Status;
            if (!bake.IsComplete) return;
            string message = bake.Status; bool succeeded = bake.Succeeded;
            bake.Dispose(); bake = null; EditorApplication.update -= PumpBake;
            if (!succeeded) Debug.LogWarning("Universe Creator: " + message);
            ShowNotification(new GUIContent(message));
        }

        private void CancelBake()
        {
            if (bake != null) { bake.Cancel(); bake.Dispose(); bake = null; }
            EditorApplication.update -= PumpBake;
            if (statusLabel != null) statusLabel.text = "Bake cancelled; Run / Apply publishes current settings.";
        }

        // Remembers the terrain we last announced an auto-resolve for, so the
        // "using terrain X" notice fires once when the guess first lands (or when
        // it lands on a DIFFERENT terrain), not on every Apply. Without this the
        // same line prints on each evaluation and the console stops being read.
        // A REFERENCE, not GetInstanceID(): that getter is CS0619-obsolete in
        // Unity 6 (warnings are errors here), and reference identity is exactly
        // what we want - a destroyed-and-recreated terrain becomes a different
        // reference (the old one compares as fake-null), so it re-announces.
        [System.NonSerialized] private static UnityEngine.Terrain s_lastAutoResolved;

        /// <summary>
        /// The terrain to write to: the field if set, otherwise the first chunk of
        /// the first world in the scene, written back into the field.
        ///
        /// Guessing and then SAYING SO is the point. A silent guess would make the
        /// result depend on hierarchy order with nothing on screen to explain it;
        /// filling the field in means the next Apply is explicit even though the
        /// first one was not.
        /// </summary>
        private UnityEngine.Terrain ResolveTerrain()
        {
            if (targetTerrain != null) return targetTerrain;

            if (targetWorld != null) // UC-T05-06: an explicitly bound world never falls through to another world's terrain.
            {
                targetTerrain = targetWorld.GetComponentInChildren<UnityEngine.Terrain>(true);
                terrainField?.SetValueWithoutNotify(targetTerrain);
                return targetTerrain;
            }

            UnityEngine.Terrain found = null;

            // No sort mode: Unity 6 deprecated the sorted overloads because the
            // order came from instance ids that are being replaced. Nothing here
            // depends on the order anyway - the first world with a terrain wins,
            // and the answer is written back into the field either way.
            UniverseCreatorObject[] worlds =
                Object.FindObjectsByType<UniverseCreatorObject>(FindObjectsInactive.Exclude);

            for (int i = 0; i < worlds.Length && found == null; i++)
            {
                if (worlds[i] == null) continue;
                found = worlds[i].GetComponentInChildren<UnityEngine.Terrain>(true);
            }

            // No Universe Creator world in the scene is a legitimate case - the
            // texture graph works on any Unity terrain, ours or not.
            if (found == null)
                found = Object.FindAnyObjectByType<UnityEngine.Terrain>(FindObjectsInactive.Exclude);

            if (found != null)
            {
                targetTerrain = found;
                if (terrainField != null) terrainField.SetValueWithoutNotify(found);

                if (!ReferenceEquals(found, s_lastAutoResolved))
                {
                    s_lastAutoResolved = found;
                    Debug.Log("Universe Creator: no terrain was set in the Texture Graph " +
                              "toolbar, so it resolved to '" + found.name +
                              "'. Assign a different terrain in the toolbar to override.");
                }
            }

            return found;
        }

        // UC-T07-09: TextureGraphBake replaces the former single-cell sibling-copy path;
        // spatially baked resources now belong to each accepted region instead of the final copied cell.

        // ------------------------------------------------------------------
        //  No-graph-loaded panel
        // ------------------------------------------------------------------

        private VisualElement BuildEmptyState()
        {
            VisualElement panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = 0f;
            panel.style.right = 0f;
            panel.style.top = 0f;
            panel.style.bottom = 0f;
            panel.style.alignItems = Align.Center;
            panel.style.justifyContent = Justify.Center;
            panel.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);

            VisualElement box = new VisualElement();
            box.style.width = 360f;
            panel.Add(box);

            Label heading = new Label("No Texture Graph Loaded");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.fontSize = 15f;
            heading.style.marginBottom = 6f;
            box.Add(heading);

            Label blurb = new Label(
                "A texture graph describes the terrain material: layers, cliffs, " +
                "snow, wetness and water. Pick one below, or start from a preset.");
            blurb.style.whiteSpace = WhiteSpace.Normal;
            blurb.style.marginBottom = 12f;
            box.Add(blurb);

            ObjectField picker = new ObjectField("Graph")
            { objectType = typeof(Graph), allowSceneObjects = false };
            picker.RegisterValueChangedCallback(evt =>
            {
                Graph picked = evt.newValue as Graph;
                if (picked != null) SetGraph(picked);
            });
            box.Add(picker);

            box.Add(new Button(() => CreateAndLoad(TextureGraphTemplates.Template.Empty))
            { text = "Create Empty Texture Graph" });

            box.Add(new Button(() => CreateAndLoad(TextureGraphTemplates.Template.BasicSurface))
            {
                text = "Create Basic Surface",
                tooltip = "Four albedo layers, splatted, budgeted and written."
            });

            box.Add(new Button(() => CreateAndLoad(TextureGraphTemplates.Template.FullPbrSurface))
            {
                text = "Create Full PBR Surface",
                tooltip = "Basic Surface plus cliff, macro variation, snow, wetness, distance fade and grade."
            });

            return panel;
        }

        private void CreateAndLoad(TextureGraphTemplates.Template template)
        {
            Graph created = TextureGraphTemplates.CreateAsset(template);
            if (created != null) SetGraph(created);
        }
    }
}
