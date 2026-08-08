// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    UniverseCreatorInspector.cs
//  Purpose: Inspector for the world root object. Two states:
//
//           NO GRAPH  - graph slot, its picker circle, Create Empty / Simple
//                       Terrain, and nothing else.
//           GRAPH SET - every world setting reveals itself automatically.
//
//  INPUT RULE
//  ----------
//  Nothing commits per keystroke. Text boxes commit on Enter or focus-out;
//  sliders commit on drag-release (hotControl back to 0), showing the pending
//  number greyed until then. Every commit can start a full regeneration, so a
//  per-keystroke control would queue one generation per character typed.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.Windows;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(UniverseCreatorObject))]
    public class UniverseCreatorInspector : UnityEditor.Editor
    {
        // Static so the sections stay where the user left them when reselecting.
        private static bool graphInfoFoldout = true;
        private static bool worldFoldout = true;
        private static bool chunkFoldout = true;
        private static bool terrainFoldout;
        private static bool layersFoldout;
        private static bool foliageFoldout;
        private static bool performanceFoldout;
        private static bool generationFoldout;
        private static bool statsFoldout;

        // Focus-out text box state. Only one control holds focus, so one buffer.
        private string focusedField = "";
        private string pendingText = "";

        // Slider drag state. Only one slider can be dragged, so one buffer.
        private string liveSliderField;
        private float liveSliderValue;

        // Set once per OnInspectorGUI so the control helpers can record undo.
        private UniverseCreatorObject ucTarget;
        private bool valueChanged;

        private int chunkProgressId = -1;
        private bool hasAutoCleared = false;

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (chunkProgressId != -1)
            {
                if (Progress.Exists(chunkProgressId)) Progress.Finish(chunkProgressId, Progress.Status.Canceled);
                chunkProgressId = -1;
            }
        }

        private void OnEditorUpdate()
        {
            UniverseCreatorObject uc = target as UniverseCreatorObject;
            if (uc == null) return;

            // Auto-clear once to fix corrupted layers from previous bugs
            if (!hasAutoCleared && uc.overrideLayer != null)
            {
                uc.ClearManualEdits();
                Debug.Log("[Universe Creator] Auto-cleared manual edits.");
                hasAutoCleared = true;
            }

            ChunkManager cm = uc.GetComponentInChildren<ChunkManager>();
            if (cm != null)
            {
                bool isGenerating = cm.IsGenerating;
                
                if (isGenerating && chunkProgressId == -1)
                {
                    chunkProgressId = Progress.Start("Universe Creator", "Generating chunks...", Progress.Options.None);
                }
                else if (!isGenerating && chunkProgressId != -1)
                {
                    if (Progress.Exists(chunkProgressId)) Progress.Finish(chunkProgressId, Progress.Status.Succeeded);
                    chunkProgressId = -1;
                }
                
                if (isGenerating && chunkProgressId != -1)
                {
                    Progress.Report(chunkProgressId, cm.GetProgress(), "Generating chunks...");
                }

                if (cm.PumpCompleted()) Repaint();
            }

            if (uc.IsGenerating) Repaint();
        }

        public override void OnInspectorGUI()
        {
            UniverseCreatorObject uc = (UniverseCreatorObject)target;
            ucTarget = uc;
            valueChanged = false;

            DrawHeaderBar(uc);
            EditorGUILayout.Space(6);
            DrawFarFromOriginWarning(uc);
            DrawGraphSlot(uc);

            // ================================================================
            //  COLLAPSED - nothing to configure until a graph exists
            // ================================================================
            if (uc.graph == null)
            {
                DrawNoGraphState(uc);
                return;
            }

            // ================================================================
            //  REVEALED - a graph is assigned, show the whole system
            // ================================================================
            EditorGUILayout.Space(8);
            DrawGraphInfo(uc);
            EditorGUILayout.Space(4);
            DrawWorldSettings(uc);
            EditorGUILayout.Space(4);
            DrawChunkSettings(uc);
            EditorGUILayout.Space(4);
            DrawTerrainSettings(uc);
            EditorGUILayout.Space(4);
            DrawLayerSettings(uc);
            EditorGUILayout.Space(4);
            DrawFoliageSettings(uc);
            EditorGUILayout.Space(4);
            DrawPerformanceSettings(uc);
            EditorGUILayout.Space(4);
            DrawGenerationSettings(uc);
            EditorGUILayout.Space(4);
            DrawStats(uc);
            EditorGUILayout.Space(10);
            DrawActionBar(uc);

            if (valueChanged)
            {
                uc.ValidateSettings();
                EditorUtility.SetDirty(uc);
                if (uc.autoRegenerate) Regenerate(uc, "Setting changed - regenerating");
            }

            // A running generation has to redraw the header spinner and the
            // enabled state of Regenerate/Cancel.
            if (uc.IsGenerating) Repaint();
        }

        // =====================================================================
        //  HEADER
        // =====================================================================

        // Not UCInspectorUI.HeaderBar: this bar carries a live cancel button, which
        // that helper has no room for. It borrows the accent strip only, so the two
        // header styles still match.
        // Only appears when the world is genuinely far out, so a correctly placed world
        // never sees it. Far-out worlds lose float precision across the heightfield,
        // which shows up as jitter, z-fighting and cracks between cells.
        private void DrawFarFromOriginWarning(UniverseCreatorObject uc)
        {
            Vector3 p = uc.transform.position;
            float distance = new Vector2(p.x, p.z).magnitude;

            float worldSpan = Mathf.Max(1f, uc.worldSizeKm * 1000f);
            float limit = Mathf.Max(10000f, worldSpan * 4f);
            if (distance <= limit) return;

            EditorGUILayout.HelpBox(
                "This world sits " + (distance / 1000f).ToString("0.#") + " km from the origin, " +
                "far enough that float precision degrades across the terrain and the surface " +
                "can jitter or crack between cells. Nothing about the world changes by moving " +
                "it - the graph is evaluated from the world seed and origin offset, not the " +
                "transform.", MessageType.Warning);

            if (UCInspectorUI.TintedButton("Move World To Origin", UCInspectorUI.ButtonBlue))
            {
                Undo.RecordObject(uc.transform, "Move World To Origin");
                uc.transform.position = new Vector3(0f, p.y, 0f);

                ChunkManager cm = uc.GetComponentInChildren<ChunkManager>();
                if (cm != null) cm.RebuildLayout();

                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(6);
        }

        private void DrawHeaderBar(UniverseCreatorObject uc)
        {
            Rect row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            UCInspectorUI.AccentStrip(row, UCInspectorUI.HeaderTeal);

            GUILayout.Space(4);

            Texture2D logo = UCIcons.Builtin("d_Terrain Icon");
            if (logo != null) GUILayout.Label(logo, GUILayout.Width(22), GUILayout.Height(22));

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel);
            titleStyle.fontSize = 12;
            GUILayout.Label("Universe Creator World", titleStyle);

            GUILayout.FlexibleSpace();

            if (uc.IsGenerating)
            {
                GUILayout.Label("Generating...", EditorStyles.miniLabel);
                if (GUILayout.Button(UCIcons.Content("Action_Stop", "Cancel the running generation", "Stop"),
                                     EditorStyles.miniButton, GUILayout.Width(28), GUILayout.Height(18)))
                {
                    uc.CancelGeneration();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // =====================================================================
        //  GRAPH SLOT
        // =====================================================================

        private void DrawGraphSlot(UniverseCreatorObject uc)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            Texture2D graphIcon = UCIcons.Get("Action_Graph");
            if (graphIcon != null) GUILayout.Label(graphIcon, GUILayout.Width(16), GUILayout.Height(16));
            GUILayout.Label("Node Graph", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            // The circle at the right of this field IS the graph picker. Clicking it
            // opens Unity's object browser filtered to Graph assets, i.e. the
            // "select graph" dialog. Graph being a ScriptableObject is what makes
            // this possible at all.
            EditorGUI.BeginChangeCheck();
            Graph picked = (Graph)EditorGUILayout.ObjectField(
                new GUIContent("Graph", "Drag a graph asset here, or click the circle to browse."),
                uc.graph, typeof(Graph), false);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(uc, "Assign Graph");
                uc.graph = picked;
                EditorUtility.SetDirty(uc);

                if (picked != null)
                {
                    GraphAssetUtility.Open(picked);
                    if (uc.autoRegenerate) Regenerate(uc, "Graph assigned - generating terrain");
                }
            }

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(uc.graph == null))
            {
                if (GUILayout.Button(IconLabel("Action_Graph", "Open Node Graph",
                                               "Open this graph in the node editor"), GUILayout.Height(24)))
                {
                    GraphAssetUtility.Open(uc.graph);
                }
            }

            if (GUILayout.Button(IconLabel("Action_Add", "Create Empty",
                                           "Create a graph containing a Height Output node, assign it, and open it"),
                                 GUILayout.Height(24)))
            {
                CreateAndAssign(uc, GraphTemplates.Template.Empty);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawNoGraphState(UniverseCreatorObject uc)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "No graph assigned. World settings stay hidden until this world has a " +
                "graph to drive.\n\n" +
                "Click the circle on the Graph field to pick an existing graph, or use " +
                "Create Empty for a fresh one. An empty graph already contains a Height " +
                "Output node - wire anything into it and terrain appears.",
                MessageType.Info);

            EditorGUILayout.Space(6);
            GUILayout.Label("Start From a Template", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (UCInspectorUI.TintedButton(IconLabel("Action_Add", "Empty Graph",
                                                    "Height Output only - you build the rest"),
                                           UCInspectorUI.ButtonBlue, 28f))
            {
                CreateAndAssign(uc, GraphTemplates.Template.Empty);
            }

            if (UCInspectorUI.TintedButton(
                    IconLabel("Action_Generate", "Simple Terrain",
                              "Perlin Noise -> Hydraulic Erosion -> Height Output, pre-wired"),
                    UCInspectorUI.ButtonGreen, 28f))
            {
                CreateAndAssign(uc, GraphTemplates.Template.SimpleTerrain);
            }

            EditorGUILayout.EndHorizontal();
        }

        // Creates the asset, assigns it, opens the node window, and generates.
        private void CreateAndAssign(UniverseCreatorObject uc, GraphTemplates.Template template)
        {
            Graph created = GraphAssetUtility.CreateAsset(template);
            if (created == null)
            {
                Debug.LogError("Universe Creator: could not create the graph asset.");
                return;
            }

            Undo.RecordObject(uc, "Create Graph");
            uc.graph = created;
            EditorUtility.SetDirty(uc);

            GraphAssetUtility.Open(created);
            EditorGUIUtility.PingObject(created);

            if (uc.autoRegenerate) Regenerate(uc, "Graph created - generating terrain");
        }

        // =====================================================================
        //  GRAPH INFO
        // =====================================================================

        private void DrawGraphInfo(UniverseCreatorObject uc)
        {
            graphInfoFoldout = Section(graphInfoFoldout, "Graph", "Action_NodeList",
                                       UCInspectorUI.HeaderPurple);
            if (!graphInfoFoldout) return;

            EditorGUI.indentLevel++;

            int nodeCount = uc.graph.nodes != null ? uc.graph.nodes.Count : 0;
            int connCount = uc.graph.connections != null ? uc.graph.connections.Count : 0;

            ReadOnlyRow("Asset", uc.graph.name);
            ReadOnlyRow("Nodes", nodeCount.ToString());
            ReadOnlyRow("Connections", connCount.ToString());

            List<string> errors = uc.graph.Validate();
            if (errors != null && errors.Count > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", errors.ToArray()), MessageType.Warning);
            }
            else if (nodeCount == 0)
            {
                EditorGUILayout.HelpBox("Graph is empty. Open the node editor and add a Height Output node.",
                                        MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(IconLabel("Action_NodePalette", "Texture Graph",
                                           "Open the texture graph editor"), GUILayout.Height(22)))
            {
                TextureGraphWindow.OpenWindow();
            }
            if (GUILayout.Button(IconLabel("Action_Settings", "World Settings",
                                           "Open the world settings window"), GUILayout.Height(22)))
            {
                WorldSettingsWindow.OpenWindow();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  WORLD SETTINGS
        // =====================================================================

        private void DrawWorldSettings(UniverseCreatorObject uc)
        {
            worldFoldout = Section(worldFoldout, "World Settings", "Action_Tools",
                                   UCInspectorUI.HeaderBlue);
            if (!worldFoldout) return;

            EditorGUI.indentLevel++;

            // Seed drives every noise node's sub-seed, so it gets a roll button.
            EditorGUILayout.BeginHorizontal();
            IntBox("Seed", "uc.seed", ref uc.seed, int.MinValue, int.MaxValue,
                   "Master seed. Reshuffles the world while keeping its character.");
            if (GUILayout.Button(UCIcons.Content("Action_Seed", "Roll a new random seed", "Roll"),
                                 GUILayout.Width(28), GUILayout.Height(18)))
            {
                Undo.RecordObject(uc, "Randomize Seed");
                uc.seed = Random.Range(1, int.MaxValue);
                valueChanged = true;
            }
            EditorGUILayout.EndHorizontal();

            FloatSlider("World Size (km)", "uc.worldSizeKm", ref uc.worldSizeKm, 0.1f, 32f, "F2",
                        "Side length of the world in kilometres.");
            FloatSlider("Max Height (m)", "uc.maxHeightMeters", ref uc.maxHeightMeters, 10f, 5000f, "F0",
                        "A heightmap value of 1.0 maps to this many metres.");

            // Snaps to 2^n+1 in ValidateSettings: Unity's TerrainData takes nothing else.
            IntBox("Resolution", "uc.resolution", ref uc.resolution, 33, 4097,
                   "Heightmap samples per chunk side. Snaps to 2^n+1.");

            Vector2Row("World Offset", ref uc.worldOriginOffset,
                       "Slides the sampling window through the noise field without moving the object.");

            EditorGUILayout.Space(2);
            InfoRow(string.Format("{0:F0} x {0:F0} m   |   {1:F2} m per heightmap sample",
                                  uc.worldSizeKm * 1000f, uc.GetMetersPerPixel()));

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  CHUNK SETTINGS
        // =====================================================================

        private void DrawChunkSettings(UniverseCreatorObject uc)
        {
            chunkFoldout = Section(chunkFoldout, "Chunk Settings (25+1 Virtual Grid)", "View_Frame",
                                   UCInspectorUI.HeaderTeal);
            if (!chunkFoldout) return;

            EditorGUI.indentLevel++;

            IntSlider("Chunks Per Side", "uc.chunkCountPerSide", ref uc.chunkCountPerSide, 1, 15,
                      "Odd values only - the grid needs one centre chunk to recentre around.");
            IntSlider("High-LOD Ring", "uc.highLODRingSize", ref uc.highLODRingSize, 1,
                      Mathf.Max(1, uc.chunkCountPerSide),
                      "Side length of the full-fidelity centre square.");
            IntSlider("Buffer Ring Width", "uc.bufferRingWidth", ref uc.bufferRingWidth, 0, 3,
                      "Extra rings generated ahead of the camera so streaming never shows a seam.");
            FloatSlider("Recentre Threshold", "uc.recenterThreshold", ref uc.recenterThreshold, 0f, 1f, "F2",
                        "Fraction of a chunk the player may cross before the grid shifts.");

            EditorGUILayout.Space(2);
            InfoRow(string.Format("Chunk size: {0:F1} m   |   {1} high-LOD + {2} low-LOD + {3} buffer",
                                  uc.GetChunkSize(), uc.GetHighLODChunkCount(),
                                  uc.GetLowLODChunkCount(), uc.GetBufferChunkCount()));

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  TERRAIN RENDERING
        // =====================================================================

        private void DrawTerrainSettings(UniverseCreatorObject uc)
        {
            terrainFoldout = Section(terrainFoldout, "Terrain Rendering", "Action_PreviewMode",
                                     UCInspectorUI.HeaderGreen);
            if (!terrainFoldout) return;

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();
            Material mat = (Material)EditorGUILayout.ObjectField(
                new GUIContent("Material", "Applied to every generated chunk. Null uses Unity's default."),
                uc.terrainMaterial, typeof(Material), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(uc, "Change Terrain Material");
                uc.terrainMaterial = mat;
                valueChanged = true;
            }

            FloatSlider("Pixel Error", "uc.pixelError", ref uc.pixelError, 1f, 200f, "F1",
                        "Allowed screen-space error. Higher means fewer triangles and more popping.");
            FloatSlider("Basemap Distance", "uc.basemapDistance", ref uc.basemapDistance, 0f, 5000f, "F0",
                        "Beyond this, terrain uses the baked basemap instead of live splat layers.");

            Toggle("Cast Shadows", ref uc.castShadows,
                   "Disable on mobile for a large fill-rate saving.");
            Toggle("Draw Instanced", ref uc.drawInstanced,
                   "Requires a material with instancing enabled.");
            Toggle("Generate Collider", ref uc.generateCollider,
                   "Off gives a visual-only preview with no physics cost.");

            ObjectSlot("Physics Material", ref uc.terrainPhysicsMaterial,
                       "Friction and bounce for the terrain surface. Empty uses Unity's default, " +
                       "which is slippery enough that vehicles slide on mild slopes.");

            EditorGUILayout.Space(2);
            EnumRow("Reflection Probes", ref uc.reflectionProbeUsage,
                    "How generated chunks sample reflection probes.");

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  TERRAIN LAYERS
        // =====================================================================

        // Splatmap channels index this array positionally, so channel 0 of every
        // Texture Output node paints whatever sits in element 0 here. Reordering the
        // list repaints the world without touching the graph.
        private void DrawLayerSettings(UniverseCreatorObject uc)
        {
            layersFoldout = Section(layersFoldout, "Terrain Layers", "Action_PreviewMode",
                                    UCInspectorUI.HeaderGreen);
            if (!layersFoldout) return;

            EditorGUI.indentLevel++;

            int count = uc.terrainLayers != null ? uc.terrainLayers.Length : 0;

            EditorGUI.BeginChangeCheck();
            int nextCount = EditorGUILayout.DelayedIntField(
                new GUIContent("Layer Count", "Number of paintable texture layers. " +
                               "Splatmap channels map to these in order."), count);
            if (EditorGUI.EndChangeCheck())
            {
                nextCount = Mathf.Clamp(nextCount, 0, 16);
                Undo.RecordObject(uc, "Change Layer Count");
                TerrainLayer[] resized = new TerrainLayer[nextCount];
                for (int i = 0; i < nextCount && i < count; i++) resized[i] = uc.terrainLayers[i];
                uc.terrainLayers = resized;
                valueChanged = true;
                count = nextCount;
            }

            for (int i = 0; i < count; i++)
            {
                EditorGUI.BeginChangeCheck();
                TerrainLayer next = (TerrainLayer)EditorGUILayout.ObjectField(
                    new GUIContent("Channel " + i, "Painted by splatmap channel " + i + "."),
                    uc.terrainLayers[i], typeof(TerrainLayer), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(uc, "Change Terrain Layer");
                    uc.terrainLayers[i] = next;
                    valueChanged = true;
                }
            }

            if (count == 0)
                InfoRow("No layers assigned — chunks render untextured whatever the graph paints.");

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  TREES, GRASS AND DETAIL
        // =====================================================================

        private void DrawFoliageSettings(UniverseCreatorObject uc)
        {
            foliageFoldout = Section(foliageFoldout, "Trees, Grass & Detail", "View_LOD_High",
                                     UCInspectorUI.HeaderAmber);
            if (!foliageFoldout) return;

            EditorGUI.indentLevel++;

            Toggle("Draw Trees & Foliage", ref uc.drawTreesAndFoliage,
                   "Off strips all trees, grass and detail meshes from every chunk.");

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Detail Meshes & Grass", EditorStyles.miniBoldLabel);

            FloatSlider("Detail Distance", "uc.detailObjectDistance",
                        ref uc.detailObjectDistance, 0f, 250f, "F0",
                        "Metres beyond which grass and detail meshes stop drawing.");
            FloatSlider("Detail Density", "uc.detailObjectDensity",
                        ref uc.detailObjectDensity, 0f, 1f, "F2",
                        "Fraction of authored density actually drawn. The cheapest grass optimisation.");

            FloatSlider("Wind Strength", "uc.wavingGrassStrength",
                        ref uc.wavingGrassStrength, 0f, 1f, "F2", "Grass sway strength.");
            FloatSlider("Wind Amount", "uc.wavingGrassAmount",
                        ref uc.wavingGrassAmount, 0f, 1f, "F2", "How much grass bends per gust.");
            FloatSlider("Wind Speed", "uc.wavingGrassSpeed",
                        ref uc.wavingGrassSpeed, 0f, 1f, "F2", "Gust speed.");
            ColorRow("Wind Tint", ref uc.wavingGrassTint,
                     "Colour multiplied into grass as it waves.");

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Trees", EditorStyles.miniBoldLabel);

            FloatSlider("Tree Distance", "uc.treeDistance",
                        ref uc.treeDistance, 0f, 5000f, "F0",
                        "Metres beyond which trees stop drawing entirely.");
            FloatSlider("Billboard Start", "uc.treeBillboardDistance",
                        ref uc.treeBillboardDistance, 0f, 2000f, "F0",
                        "Metres beyond which trees switch from mesh to billboard.");
            FloatSlider("Fade Length", "uc.treeCrossFadeLength",
                        ref uc.treeCrossFadeLength, 0f, 200f, "F0",
                        "Metres over which the mesh-to-billboard swap blends. 0 pops visibly.");
            IntSlider("Max Full-Mesh Trees", "uc.treeMaximumFullLODCount",
                      ref uc.treeMaximumFullLODCount, 0, 500,
                      "How many trees stay full-mesh near the camera before billboarding.");

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  PERFORMANCE
        // =====================================================================

        private void DrawPerformanceSettings(UniverseCreatorObject uc)
        {
            performanceFoldout = Section(performanceFoldout, "Performance", "View_LOD_High",
                                         UCInspectorUI.HeaderAmber);
            if (!performanceFoldout) return;

            EditorGUI.indentLevel++;

            Toggle("GPU Instancing", ref uc.enableGPUInstancing,
                   "Batch scattered vegetation and props through GPU instancing.");
            FloatSlider("Draw Distance (m)", "uc.drawDistance", ref uc.drawDistance, 50f, 5000f, "F0",
                        "Chunks beyond this are not rendered.");
            FloatSlider("LOD Bias", "uc.lodBias", ref uc.lodBias, 0.5f, 2f, "F2",
                        "Below 1 drops to low-poly sooner (mobile); above 1 holds detail longer.");
            IntSlider("Max Concurrent Chunks", "uc.maxConcurrentChunks", ref uc.maxConcurrentChunks, 1, 16,
                      "Worker threads allowed to generate at once. Keep below your core count.");

            EditorGUI.BeginChangeCheck();
            PreviewQuality pq = (PreviewQuality)EditorGUILayout.EnumPopup(
                new GUIContent("Preview Quality", "Resolution used for interactive regeneration."),
                uc.previewQuality);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(uc, "Change Preview Quality");
                uc.previewQuality = pq;
                valueChanged = true;
            }

            InfoRow(string.Format("Effective resolution: {0} x {0}", uc.GetChunkResolution()));

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  GENERATION BEHAVIOUR
        // =====================================================================

        private void DrawGenerationSettings(UniverseCreatorObject uc)
        {
            generationFoldout = Section(generationFoldout, "Generation", "Action_Generate",
                                        UCInspectorUI.HeaderBlue);
            if (!generationFoldout) return;

            EditorGUI.indentLevel++;

            Toggle("Auto Regenerate", ref uc.autoRegenerate,
                   "Regenerate whenever a setting is committed. Off means only the button generates.");
            Toggle("Wireframe Preview", ref uc.showWireframePreview,
                   "Show a low-poly stand-in while the real chunk is still generating.");

            if (!uc.autoRegenerate)
            {
                EditorGUILayout.HelpBox("Auto Regenerate is off. Changes apply on the next manual Regenerate.",
                                        MessageType.None);
            }

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  ESTIMATES
        // =====================================================================

        private void DrawStats(UniverseCreatorObject uc)
        {
            statsFoldout = Section(statsFoldout, "Estimates", "Status_Info",
                                   UCInspectorUI.HeaderGrey);
            if (!statsFoldout) return;

            EditorGUI.indentLevel++;

            ReadOnlyRow("Heightmap samples", uc.GetEstimatedSampleCount().ToString("N0"));
            ReadOnlyRow("Heightmap memory", uc.GetEstimatedMemoryMB().ToString("F1") + " MB");
            ReadOnlyRow("Total chunks", (uc.chunkCountPerSide * uc.chunkCountPerSide +
                                         uc.GetBufferChunkCount()).ToString());
            ReadOnlyRow("Metres per sample", uc.GetMetersPerPixel().ToString("F3"));

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  ACTION BAR
        // =====================================================================

        private void DrawActionBar(UniverseCreatorObject uc)
        {
            EditorGUILayout.BeginHorizontal();

            // C-12. Tint follows consequence: green builds, amber interrupts, red
            // discards. Width and height stay as they were so the bar does not reflow.
            using (new EditorGUI.DisabledScope(uc.IsGenerating))
            {
                if (UCInspectorUI.TintedButton(IconLabel("Action_Generate", "Regenerate",
                                                         "Rebuild the terrain from the graph"),
                                               UCInspectorUI.ButtonGreen, 28f))
                {
                    Regenerate(uc, "Manual regeneration");
                }
            }

            using (new EditorGUI.DisabledScope(!uc.IsGenerating))
            {
                Color previousCancel = GUI.backgroundColor;
                GUI.backgroundColor = UCInspectorUI.ButtonAmber;
                bool cancel = GUILayout.Button(
                    IconLabel("Action_Stop", "Cancel", "Stop the running generation"),
                    GUILayout.Width(90), GUILayout.Height(28));
                GUI.backgroundColor = previousCancel;

                if (cancel) uc.CancelGeneration();
            }

            Color previousReset = GUI.backgroundColor;
            GUI.backgroundColor = UCInspectorUI.ButtonRed;
            bool reset = GUILayout.Button(
                UCIcons.Content("Action_Reset", "Reset every setting to its default", "Reset"),
                GUILayout.Width(40), GUILayout.Height(28));
            GUI.backgroundColor = previousReset;

            if (reset)
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                        "Reset all world settings to their defaults? The graph reference is kept.",
                        "Reset", "Cancel"))
                {
                    ResetSettings(uc);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void ResetSettings(UniverseCreatorObject uc)
        {
            Undo.RecordObject(uc, "Reset Universe Creator Settings");

            uc.seed = 12345;
            uc.worldSizeKm = 4f;
            uc.maxHeightMeters = 600f;
            uc.resolution = 257;
            uc.worldOriginOffset = Vector2.zero;
            uc.chunkCountPerSide = 5;
            uc.highLODRingSize = 3;
            uc.bufferRingWidth = 1;
            uc.recenterThreshold = 0.5f;
            uc.pixelError = 5f;
            uc.basemapDistance = 1000f;
            uc.castShadows = true;
            uc.drawInstanced = true;
            uc.generateCollider = true;
            uc.enableGPUInstancing = true;
            uc.drawDistance = 1500f;
            uc.lodBias = 1f;
            uc.maxConcurrentChunks = 4;
            uc.previewQuality = PreviewQuality.Full;
            uc.autoRegenerate = true;
            uc.showWireframePreview = true;

            // Native Terrain settings. Reset leaves terrainLayers and the physics
            // material alone: those are asset references the user wired up by hand,
            // and clearing them on a settings reset would look like data loss.
            uc.drawTreesAndFoliage = true;
            uc.detailObjectDistance = 80f;
            uc.detailObjectDensity = 1f;
            uc.treeDistance = 900f;
            uc.treeBillboardDistance = 50f;
            uc.treeCrossFadeLength = 5f;
            uc.treeMaximumFullLODCount = 50;
            uc.wavingGrassStrength = 0.5f;
            uc.wavingGrassAmount = 0.5f;
            uc.wavingGrassSpeed = 0.5f;
            uc.wavingGrassTint = new Color(0.7f, 0.6f, 0.5f, 1f);
            uc.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;

            uc.ValidateSettings();
            EditorUtility.SetDirty(uc);
        }

        // Every regeneration path routes through here so the background progress
        // item can never be skipped.
        //
        // Prefers the chunked path: RebuildChunks generates every cell of the virtual
        // grid into its pooled TerrainData, which is what actually puts terrain in the
        // scene. It falls back to the single-tile RegenerateAsync internally when the
        // hierarchy has no ChunkManager, so a bare root still previews.
        private void Regenerate(UniverseCreatorObject uc, string label)
        {
            if (!uc.RebuildChunks()) return;

            // CurrentTask is only set by the single-tile fallback. The chunked path
            // runs many tasks and reports through its own pump, so there is nothing
            // to hand the progress bar in that case.
            if (uc.CurrentTask != null)
                AsyncProgressUI.Track(uc, uc.CurrentTask, label);
        }

        // =====================================================================
        //  CONTROLS
        // =====================================================================

        // Foldout header with an icon beside the arrow. A plain Foldout inside a box
        // rather than BeginFoldoutHeaderGroup, which has no room for the icon.
        //
        // C-12. The accent strip is drawn by the shared chrome so this inspector and
        // the chunk manager's cannot drift apart on section colour. Accent is
        // optional: a section that does not pass one keeps the plain box it had.
        private bool Section(bool state, string title, string iconName)
        {
            return Section(state, title, iconName, UCInspectorUI.HeaderGrey);
        }

        private bool Section(bool state, string title, string iconName, Color accent)
        {
            Rect row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            UCInspectorUI.AccentStrip(row, accent);

            GUILayout.Space(4);

            Texture2D icon = UCIcons.Get(iconName);
            if (icon != null) GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));

            bool next = EditorGUILayout.Foldout(state, title, true, EditorStyles.foldoutHeader);

            EditorGUILayout.EndHorizontal();
            return next;
        }

        private GUIContent IconLabel(string iconName, string text, string tooltip)
        {
            Texture2D icon = UCIcons.Get(iconName);
            return icon != null ? new GUIContent(" " + text, icon, tooltip)
                                : new GUIContent(text, tooltip);
        }

        private void InfoRow(string message)
        {
            EditorGUILayout.LabelField(" ", message, EditorStyles.miniLabel);
        }

        private void ReadOnlyRow(string label, string value)
        {
            EditorGUILayout.LabelField(label, value, EditorStyles.miniLabel);
        }

        private void Toggle(string label, ref bool value, string tooltip)
        {
            EditorGUI.BeginChangeCheck();
            bool next = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ucTarget, "Change " + label);
                value = next;
                valueChanged = true;
            }
        }

        // Vector2Field draws its own sub-fields and gives no focus-out hook, so the
        // commit is deferred to whatever the user does next rather than firing a
        // regeneration per digit.
        private void Vector2Row(string label, ref Vector2 value, string tooltip)
        {
            EditorGUI.BeginChangeCheck();
            Vector2 next = EditorGUILayout.Vector2Field(new GUIContent(label, tooltip), value);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ucTarget, "Change " + label);
                value = next;
                EditorUtility.SetDirty(ucTarget);
            }
        }

        // Enum, colour and object slots are one discrete decision each, so there is
        // no half-typed state to defer - they commit immediately.
        private void EnumRow<T>(string label, ref T value, string tooltip) where T : System.Enum
        {
            EditorGUI.BeginChangeCheck();
            System.Enum next = EditorGUILayout.EnumPopup(new GUIContent(label, tooltip), value);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ucTarget, "Change " + label);
                value = (T)next;
                valueChanged = true;
            }
        }

        private void ColorRow(string label, ref Color value, string tooltip)
        {
            EditorGUI.BeginChangeCheck();
            Color next = EditorGUILayout.ColorField(new GUIContent(label, tooltip), value);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ucTarget, "Change " + label);
                value = next;
                valueChanged = true;
            }
        }

        private void ObjectSlot<T>(string label, ref T value, string tooltip) where T : UnityEngine.Object
        {
            EditorGUI.BeginChangeCheck();
            T next = (T)EditorGUILayout.ObjectField(
                new GUIContent(label, tooltip), value, typeof(T), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ucTarget, "Change " + label);
                value = next;
                valueChanged = true;
            }
        }

        // ------------------------------------------------------------------
        //  Focus-out text boxes
        // ------------------------------------------------------------------

        /// <summary>
        /// Int text box. Commits on Enter or focus-out, never while typing.
        /// </summary>
        private void IntBox(string label, string fieldName, ref int value,
                            int min, int max, string tooltip)
        {
            string commit = TextBoxFocusOut(label, fieldName, value.ToString(), tooltip);
            if (commit == null) return;

            long parsed;
            if (!long.TryParse(commit, out parsed)) return;

            // Clamped in long space so min/max of int.MinValue/MaxValue (the seed)
            // cannot overflow the comparison.
            parsed = System.Math.Max(min, System.Math.Min(max, parsed));
            if ((int)parsed == value) return;

            Undo.RecordObject(ucTarget, "Change " + label);
            value = (int)parsed;
            valueChanged = true;
        }

        /// <summary>
        /// Draws a text box holding <paramref name="committedText"/> and returns the
        /// new string only on the frame the user commits (Enter or focus-out).
        /// Returns null on every other frame. This is the whole focus-out contract.
        /// </summary>
        private string TextBoxFocusOut(string label, string fieldName, string committedText, string tooltip)
        {
            string commit = null;
            bool hasFocus = GUI.GetNameOfFocusedControl() == fieldName;

            // Focus-out: focused last frame, not now.
            if (!hasFocus && focusedField == fieldName)
            {
                commit = pendingText;
                focusedField = "";
                pendingText = "";
            }

            // Focus-in: seed the buffer from the committed value.
            if (hasFocus && focusedField != fieldName)
            {
                focusedField = fieldName;
                pendingText = committedText;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            GUI.SetNextControlName(fieldName);

            if (hasFocus)
            {
                // Edits live in pendingText; the real value is untouched until commit.
                pendingText = EditorGUILayout.TextField(pendingText);

                Event e = Event.current;
                if (e.type == EventType.KeyDown &&
                    (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    commit = pendingText;
                    focusedField = "";
                    pendingText = "";
                    GUI.FocusControl(null);
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    // Abandon the edit; the committed value stands.
                    focusedField = "";
                    pendingText = "";
                    GUI.FocusControl(null);
                    e.Use();
                }
            }
            else
            {
                EditorGUILayout.TextField(committedText);
            }

            EditorGUILayout.EndHorizontal();
            return commit;
        }

        // ------------------------------------------------------------------
        //  Drag-release sliders
        // ------------------------------------------------------------------

        /// <summary>
        /// Slider plus a focus-out number box. The slider commits when the drag ends
        /// (hotControl back to 0); while dragging, the pending value is shown as a
        /// plain label so it is visibly not applied yet.
        /// </summary>
        private void FloatSlider(string label, string fieldName, ref float value,
                                 float min, float max, string format, string tooltip)
        {
            if (max < min) max = min;

            bool dragging = liveSliderField == fieldName;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            EditorGUI.BeginChangeCheck();
            float next = GUILayout.HorizontalSlider(dragging ? liveSliderValue : value, min, max,
                                                    GUILayout.MinWidth(40));
            if (EditorGUI.EndChangeCheck())
            {
                liveSliderField = fieldName;
                liveSliderValue = next;
                dragging = true;
            }

            if (dragging)
            {
                GUILayout.Label(liveSliderValue.ToString(format), EditorStyles.miniLabel, GUILayout.Width(58));
                EditorGUILayout.EndHorizontal();

                // hotControl drops to 0 the moment the mouse button is released.
                if (GUIUtility.hotControl == 0)
                {
                    float committed = Mathf.Clamp(liveSliderValue, min, max);
                    liveSliderField = null;

                    if (!Mathf.Approximately(committed, value))
                    {
                        Undo.RecordObject(ucTarget, "Change " + label);
                        value = committed;
                        valueChanged = true;
                    }
                }
                return;
            }

            string commit = NumberBoxFocusOut(fieldName + "#num", value.ToString(format));
            EditorGUILayout.EndHorizontal();

            if (commit == null) return;

            float parsed;
            if (!float.TryParse(commit, out parsed)) return;

            parsed = Mathf.Clamp(parsed, min, max);
            if (Mathf.Approximately(parsed, value)) return;

            Undo.RecordObject(ucTarget, "Change " + label);
            value = parsed;
            valueChanged = true;
        }

        /// <summary>Integer drag-release slider. Same contract as FloatSlider.</summary>
        private void IntSlider(string label, string fieldName, ref int value,
                               int min, int max, string tooltip)
        {
            if (max < min) max = min;

            bool dragging = liveSliderField == fieldName;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            EditorGUI.BeginChangeCheck();
            float next = GUILayout.HorizontalSlider(dragging ? liveSliderValue : value, min, max,
                                                    GUILayout.MinWidth(40));
            if (EditorGUI.EndChangeCheck())
            {
                liveSliderField = fieldName;
                liveSliderValue = Mathf.Round(next);   // snap so the label never shows a fraction
                dragging = true;
            }

            if (dragging)
            {
                GUILayout.Label(Mathf.RoundToInt(liveSliderValue).ToString(),
                                EditorStyles.miniLabel, GUILayout.Width(58));
                EditorGUILayout.EndHorizontal();

                if (GUIUtility.hotControl == 0)
                {
                    int committed = Mathf.Clamp(Mathf.RoundToInt(liveSliderValue), min, max);
                    liveSliderField = null;

                    if (committed != value)
                    {
                        Undo.RecordObject(ucTarget, "Change " + label);
                        value = committed;
                        valueChanged = true;
                    }
                }
                return;
            }

            string commit = NumberBoxFocusOut(fieldName + "#num", value.ToString());
            EditorGUILayout.EndHorizontal();

            if (commit == null) return;

            int parsed;
            if (!int.TryParse(commit, out parsed)) return;

            parsed = Mathf.Clamp(parsed, min, max);
            if (parsed == value) return;

            Undo.RecordObject(ucTarget, "Change " + label);
            value = parsed;
            valueChanged = true;
        }

        // The narrow number box that sits at the right of a slider. Same focus-out
        // contract as TextBoxFocusOut but without its own prefix label, because the
        // caller has already drawn one and owns the surrounding horizontal group.
        private string NumberBoxFocusOut(string key, string committedText)
        {
            string commit = null;
            bool hasFocus = GUI.GetNameOfFocusedControl() == key;

            if (!hasFocus && focusedField == key)
            {
                commit = pendingText;
                focusedField = "";
                pendingText = "";
            }
            if (hasFocus && focusedField != key)
            {
                focusedField = key;
                pendingText = committedText;
            }

            GUI.SetNextControlName(key);

            if (hasFocus)
            {
                pendingText = EditorGUILayout.TextField(pendingText, GUILayout.Width(58));

                Event e = Event.current;
                if (e.type == EventType.KeyDown &&
                    (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    commit = pendingText;
                    focusedField = "";
                    pendingText = "";
                    GUI.FocusControl(null);
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    focusedField = "";
                    pendingText = "";
                    GUI.FocusControl(null);
                    e.Use();
                }
            }
            else
            {
                EditorGUILayout.TextField(committedText, GUILayout.Width(58));
            }

            return commit;
        }
    }
}
