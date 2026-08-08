// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    NodeGraphWindow.cs
//  Purpose: Hosts the node graph canvas: a toolbar, the UCGraphView, and the
//           no-graph-loaded panel.
//
//  Correction C-06. This window used to draw the canvas itself in IMGUI, which
//  is what made wires undraggable (port hit tests ran in screen space while the
//  nodes were drawn through a scaled GUI.matrix) and made the grid snap while
//  zooming (the grid was screen space, the nodes were content space). Both are
//  now GraphView's problem, which solves them structurally rather than by
//  correcting the arithmetic. The runtime graph engine is unchanged.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Windows
{
    public class NodeGraphWindow : EditorWindow
    {
        // Serialized so the open graph survives a domain reload. Possible only
        // because Graph is a ScriptableObject asset with a GUID.
        [SerializeField] private Graph graph;

        private UCGraphView view;
        private VisualElement emptyState;
        private Label statusLabel;
        private Label nameLabel;

        // No [MenuItem] here - UniverseCreatorMenus owns every menu path so the
        // ordering stays in one file. A duplicate path would shadow that entry.
        public static void OpenWindow()
        {
            NodeGraphWindow window = GetWindow<NodeGraphWindow>("Universe Creator - Node Graph");
            window.minSize = new Vector2(800, 600);
            window.Show();
        }

        /// <summary>Loads a graph into the canvas. Safe before the UI is built:
        /// CreateGUI populates from the serialized field.</summary>
        public void SetGraph(Graph g)
        {
            graph = g;
            if (view != null) view.Populate(graph);
            RefreshChrome();
        }

        private void CreateGUI()
        {
            rootVisualElement.Add(BuildToolbar());

            view = new UCGraphView();
            view.style.flexGrow = 1f;
            view.StructureChanged += RefreshChrome;
            rootVisualElement.Add(view);

            emptyState = BuildEmptyState();
            rootVisualElement.Add(emptyState);

            view.Populate(graph);
            RefreshChrome();
        }

        // ------------------------------------------------------------------
        //  Toolbar
        // ------------------------------------------------------------------

        private VisualElement BuildToolbar()
        {
            Toolbar bar = new Toolbar();

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

            // .ucgraph export/import: portable JSON for moving a graph between
            // projects. The in-project format stays .asset - see GraphSerializer.
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

                // Switch to the import rather than merging into the open graph:
                // merging would need a conflict policy for colliding node ids.
                SetGraph(imported);
                ShowNotification(new GUIContent("Imported " + imported.name));
            })
            { text = "Import" });

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

            return bar;
        }

        private void RefreshChrome()
        {
            bool loaded = graph != null;

            if (emptyState != null)
                emptyState.style.display = loaded ? DisplayStyle.None : DisplayStyle.Flex;

            if (view != null)
                view.style.display = loaded ? DisplayStyle.Flex : DisplayStyle.None;

            if (nameLabel != null)
                nameLabel.text = loaded ? graph.name : "No graph";

            if (statusLabel != null)
            {
                statusLabel.text = loaded
                    ? graph.nodes.Count + " nodes  |  " + graph.connections.Count + " links"
                    : string.Empty;
            }
        }

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
            box.style.width = 340f;
            panel.Add(box);

            Label heading = new Label("No Graph Loaded");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.fontSize = 15f;
            heading.style.marginBottom = 6f;
            box.Add(heading);

            Label blurb = new Label(
                "Select a Universe Creator world in the hierarchy, pick a graph " +
                "asset below, or start a new one.");
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

            box.Add(new Button(() => CreateAndLoad(GraphTemplates.Template.Empty))
            { text = "Create Empty Graph" });

            box.Add(new Button(() => CreateAndLoad(GraphTemplates.Template.SimpleTerrain))
            { text = "Create Simple Terrain Graph" });

            return panel;
        }

        private void CreateAndLoad(GraphTemplates.Template template)
        {
            Graph created = GraphAssetUtility.CreateAsset(template);
            if (created != null) SetGraph(created);
        }
    }
}
