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
        [SerializeField] private int bakeResolution = 257;

        private UCGraphView view;
        private VisualElement emptyState;
        private Label statusLabel;
        private Label nameLabel;
        private ObjectField terrainField;

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
            graph = g;
            if (view != null) view.Populate(graph);
            RefreshChrome();
        }

        private void CreateGUI()
        {
            rootVisualElement.Add(BuildToolbar());

            view = new UCGraphView();
            view.style.flexGrow = 1f;

            // The one line that makes this a texture graph rather than a second
            // height graph. Everything else in this window is chrome.
            view.CategoryFilter = NodeCategoryColors.IsTextureGraph;

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
                                "Higher is sharper and slower; it does not affect the material itself.";
            bakeField.style.width = 90f;
            bakeField.RegisterValueChangedCallback(evt => bakeResolution = evt.newValue);
            bar.Add(bakeField);

            bar.Add(new ToolbarButton(ApplyToTerrain)
            {
                text = "Apply to Terrain",
                tooltip = "Evaluate the graph and write the Material, TerrainLayers and baked maps."
            });

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
        //  Apply
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the graph once and commits the result.
        ///
        /// Synchronous and on the main thread, both deliberately. Texture nodes
        /// hold Texture2D references and the terminal step writes assets, so this
        /// is the one graph in the project that cannot go to a worker. The bake
        /// resolution cap in the toolbar is what keeps that honest.
        /// </summary>
        private void ApplyToTerrain()
        {
            if (graph == null)
            {
                ShowNotification(new GUIContent("No graph loaded."));
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

            TerrainData data = terrain.terrainData;

            // Tile geometry comes from the terrain the user pointed at. Texture
            // nodes are position-independent - none of them read tile.worldPos -
            // but the bakers read the Matrix a Terrain Height Source produced,
            // which carries the terrain's own metres-per-pixel. Passing the same
            // terrain's size here keeps the two consistent for anything added
            // later that does sample by world position.
            int res = Mathf.Clamp(bakeResolution, 33, 1025);
            TileData tile = new TileData(
                Coord.Zero,
                new CoordRect(0, 0, res, res),
                terrain.transform.position,
                data.size,
                data.size.y);
            tile.SetFullWorld(terrain.transform.position, data.size);

            StopToken stop = new StopToken();

            try
            {
                new GraphEvaluator(graph).Evaluate(tile, stop);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Universe Creator: texture graph evaluation failed - " + ex);
                ShowNotification(new GUIContent("Evaluation failed. See the console."));
                return;
            }

            if (tile.outputs.surface == null)
            {
                // The single most common authoring mistake, and the one with the
                // least obvious symptom: everything is wired but nothing happens.
                ShowNotification(new GUIContent("Nothing to apply - the graph has no Shader Output."));
                return;
            }

            if (!UCTextureGraphApplier.Apply(tile.outputs, terrain))
            {
                ShowNotification(new GUIContent("Apply failed. See the console."));
                return;
            }

            int siblings = ShareWithSiblingChunks(terrain);

            AssetDatabase.SaveAssets();

            ShowNotification(new GUIContent(siblings > 0
                ? "Applied to " + (siblings + 1) + " terrains"
                : "Applied to " + terrain.name));
        }

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
                Debug.Log("Universe Creator: texture graph applied to '" + found.name +
                          "'. Assign a different terrain in the Texture Graph toolbar to override.");
            }

            return found;
        }

        /// <summary>
        /// Copies the finished material and layer set onto the other chunks of the
        /// same world.
        ///
        /// WHY COPY RATHER THAN RE-APPLY
        /// -----------------------------
        /// Calling the applier once per chunk would work - every asset it touches
        /// is found by path, so twenty-five passes produce one material, not
        /// twenty-five. But it would also re-bake every map twenty-five times and
        /// log the same batch of notices twenty-five times, which is how a console
        /// stops being read. The chunks of one world are one surface; they need
        /// the same two references, not the same computation.
        /// </summary>
        private int ShareWithSiblingChunks(UnityEngine.Terrain source)
        {
            UniverseCreatorObject world = source.GetComponentInParent<UniverseCreatorObject>(true);
            if (world == null) return 0;

            UnityEngine.Terrain[] all = world.GetComponentsInChildren<UnityEngine.Terrain>(true);
            int shared = 0;

            for (int i = 0; i < all.Length; i++)
            {
                UnityEngine.Terrain t = all[i];
                if (t == null || t == source || t.terrainData == null) continue;

                if (t.materialTemplate != source.materialTemplate)
                {
                    t.materialTemplate = source.materialTemplate;
                    EditorUtility.SetDirty(t);
                }

                // Reference comparison, not element-by-element: the applier reuses
                // one TerrainLayer asset per channel, so two chunks already in sync
                // hold the same array contents and assigning again would dirty a
                // TerrainData and force a basemap rebuild for no change.
                if (!SameLayers(t.terrainData.terrainLayers, source.terrainData.terrainLayers))
                {
                    t.terrainData.terrainLayers = source.terrainData.terrainLayers;
                    EditorUtility.SetDirty(t.terrainData);
                }

                shared++;
            }

            return shared;
        }

        private static bool SameLayers(TerrainLayer[] a, TerrainLayer[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
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
