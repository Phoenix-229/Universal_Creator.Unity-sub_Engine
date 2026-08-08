// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ChunkManagerInspector.cs
//  Purpose: Authoring surface for the chunk manager: chunk dimensions, the slice
//           map, focus/materials, live stats, and the manager's actions.
//
//  WHY DIMENSIONS ARE EDITED HERE
//  ------------------------------
//  The fields themselves live on UniverseCreatorObject because generation reads
//  them off the world root, but chunking is what this component does, so this is
//  where they are reachable. Edits are proxied to the owner and undo is recorded
//  against the owner, not the manager.
//
//  INPUT RULE
//  ----------
//  Delayed fields: nothing commits per keystroke. Every commit can restart a
//  generation, so a live field would queue one run per character typed.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(ChunkManager))]
    public class ChunkManagerInspector : UnityEditor.Editor
    {
        private static bool dimensionsFoldout = true;
        private static bool sliceMapFoldout = true;
        private static bool focusFoldout;
        private static bool statsFoldout;

        private ChunkManager manager;
        private UniverseCreatorObject owner;
        private bool ownerChanged;
        private bool layoutChanged;
        private bool mapChanged;

        public override void OnInspectorGUI()
        {
            manager = (ChunkManager)target;
            owner = manager.GetComponentInParent<UniverseCreatorObject>();
            ownerChanged = false;
            layoutChanged = false;
            mapChanged = false;

            UCInspectorUI.HeaderBar("Chunk Manager", "d_Terrain Icon",
                                    UCInspectorUI.HeaderTeal, StatusText());

            if (owner == null)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "No Universe Creator world above this component. The chunk manager " +
                    "is a child of a world root and cannot configure anything on its own.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(6);
            DrawDimensions();
            EditorGUILayout.Space(4);
            DrawSliceMap();
            EditorGUILayout.Space(4);
            DrawFocus();
            EditorGUILayout.Space(4);
            DrawStats();
            EditorGUILayout.Space(10);
            DrawActions();

            if (ownerChanged)
            {
                owner.ValidateSettings();
                EditorUtility.SetDirty(owner);
            }

            // A dimension change moves every cell, so the pool has to be replaced and
            // the map redrawn. Layout only - generation stays on the world's own commit
            // path so one edit cannot kick off two runs.
            if (layoutChanged && !Application.isPlaying)
            {
                manager.RebuildLayout();
                EditorUtility.SetDirty(manager);
            }
            // A map-only change (colour, offset, density) does not move a cell, so it
            // redraws the map without touching the pool.
            else if (mapChanged)
            {
                manager.RefreshSliceMap();
                EditorUtility.SetDirty(manager);
            }
        }

        private string StatusText()
        {
            int cells = owner != null ? owner.chunkCountPerSide * owner.chunkCountPerSide : 0;
            if (owner != null && owner.IsGenerating) return cells + " cells  |  generating";
            return cells + " cells";
        }

        // =====================================================================
        //  CHUNK DIMENSIONS
        // =====================================================================

        private void DrawDimensions()
        {
            dimensionsFoldout = UCInspectorUI.Section(dimensionsFoldout, "Chunk Dimensions",
                                                      UCInspectorUI.HeaderTeal);
            if (!dimensionsFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                float span = owner.worldSizeKm * 1000f;

                float km = EditorGUILayout.DelayedFloatField(
                    new GUIContent("World Size (km)", "World side length in kilometres."),
                    owner.worldSizeKm);
                if (!Mathf.Approximately(km, owner.worldSizeKm))
                {
                    RecordOwner("Change World Size");
                    owner.worldSizeKm = km;
                    layoutChanged = true;
                }

                // Odd counts only - an even grid has no single centre cell for the
                // player to sit in, which the recentring logic depends on.
                int side = EditorGUILayout.IntSlider(
                    new GUIContent("Chunks Per Side", "Cells per axis in the virtual grid. Odd values only."),
                    owner.chunkCountPerSide, 1, 15);
                if (side != owner.chunkCountPerSide)
                {
                    RecordOwner("Change Chunk Count");
                    owner.chunkCountPerSide = side;
                    layoutChanged = true;
                }

                // Chunk size is derived from span/count, so setting it solves for the
                // count instead of storing a second source of truth that could drift.
                float currentSize = owner.GetChunkSize();
                float wantSize = EditorGUILayout.DelayedFloatField(
                    new GUIContent("Chunk Size (m)",
                                   "Metres per cell. Solves for the chunk count; the result snaps to an odd value."),
                    currentSize);
                if (wantSize > 0.01f && !Mathf.Approximately(wantSize, currentSize))
                {
                    int solved = Mathf.Clamp(Mathf.RoundToInt(span / wantSize), 1, 15);
                    if (solved % 2 == 0) solved++;
                    if (solved != owner.chunkCountPerSide)
                    {
                        RecordOwner("Change Chunk Size");
                        owner.chunkCountPerSide = solved;
                        layoutChanged = true;
                    }
                }

                EditorGUILayout.LabelField(" ",
                    "Grid " + owner.chunkCountPerSide + " x " + owner.chunkCountPerSide +
                    "  |  " + owner.GetChunkSize().ToString("F0") + " m per cell",
                    EditorStyles.miniLabel);

                int res = EditorGUILayout.DelayedIntField(
                    new GUIContent("Resolution", "Heightmap samples per cell. Snaps to 2^n+1."),
                    owner.resolution);
                if (res != owner.resolution)
                {
                    RecordOwner("Change Resolution");
                    owner.resolution = res;
                    layoutChanged = true;
                }

                int ring = EditorGUILayout.IntSlider(
                    new GUIContent("High-LOD Ring", "Centre square held at full fidelity."),
                    owner.highLODRingSize, 1, Mathf.Max(1, owner.chunkCountPerSide));
                if (ring != owner.highLODRingSize)
                {
                    RecordOwner("Change High-LOD Ring");
                    owner.highLODRingSize = ring;
                }

                int buffer = EditorGUILayout.IntSlider(
                    new GUIContent("Buffer Ring", "Extra precomputed rings beyond the visible grid."),
                    owner.bufferRingWidth, 0, 3);
                if (buffer != owner.bufferRingWidth)
                {
                    RecordOwner("Change Buffer Ring");
                    owner.bufferRingWidth = buffer;
                }

                int workers = EditorGUILayout.IntSlider(
                    new GUIContent("Max Concurrent Cells", "Cells generating at once."),
                    owner.maxConcurrentChunks, 1, 16);
                if (workers != owner.maxConcurrentChunks)
                {
                    RecordOwner("Change Concurrency");
                    owner.maxConcurrentChunks = workers;
                }

                bool collide = EditorGUILayout.Toggle(
                    new GUIContent("Generate Colliders", "Colliders on high-LOD cells."),
                    owner.generateCollider);
                if (collide != owner.generateCollider)
                {
                    RecordOwner("Change Colliders");
                    owner.generateCollider = collide;
                }
            }
        }
        // =====================================================================
        //  SLICE MAP
        // =====================================================================

        private void DrawSliceMap()
        {
            sliceMapFoldout = UCInspectorUI.Section(sliceMapFoldout, "Slice Map",
                                                   UCInspectorUI.HeaderGreen);
            if (!sliceMapFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(" ",
                    "One permanent child draws the virtual cell boundaries over the whole terrain.",
                    EditorStyles.miniLabel);

                bool show = EditorGUILayout.Toggle(
                    new GUIContent("Show Slice Map", "Draw the chunk boundary lines."),
                    manager.showSliceMap);
                if (show != manager.showSliceMap)
                {
                    RecordManager("Toggle Slice Map");
                    manager.showSliceMap = show;
                    mapChanged = true;
                }

                using (new EditorGUI.DisabledScope(!manager.showSliceMap))
                {
                    Color colour = EditorGUILayout.ColorField(
                        new GUIContent("Line Colour"), manager.sliceMapColour);
                    if (colour != manager.sliceMapColour)
                    {
                        RecordManager("Change Slice Map Colour");
                        manager.sliceMapColour = colour;
                        mapChanged = true;
                    }

                    float offset = EditorGUILayout.Slider(
                        new GUIContent("Height Offset (m)",
                                       "Metres above the surface, so the lines do not z-fight the terrain."),
                        manager.sliceMapHeightOffset, 0f, 50f);
                    if (!Mathf.Approximately(offset, manager.sliceMapHeightOffset))
                    {
                        RecordManager("Change Slice Map Offset");
                        manager.sliceMapHeightOffset = offset;
                        mapChanged = true;
                    }

                    int samples = EditorGUILayout.IntSlider(
                        new GUIContent("Samples Per Cell",
                                       "Points along each cell edge. Higher hugs the surface more closely."),
                        manager.sliceMapSamplesPerCell, 1, 32);
                    if (samples != manager.sliceMapSamplesPerCell)
                    {
                        RecordManager("Change Slice Map Samples");
                        manager.sliceMapSamplesPerCell = samples;
                        mapChanged = true;
                    }

                    bool follow = EditorGUILayout.Toggle(
                        new GUIContent("Follow Surface",
                                       "Off draws the map flat at the world base."),
                        manager.sliceMapFollowsSurface);
                    if (follow != manager.sliceMapFollowsSurface)
                    {
                        RecordManager("Change Slice Map Follow");
                        manager.sliceMapFollowsSurface = follow;
                        mapChanged = true;
                    }
                }

                if (UCInspectorUI.TintedButton("Rebuild Slice Map", UCInspectorUI.ButtonGreen))
                    manager.RefreshSliceMap();
            }
        }

        // =====================================================================
        //  FOCUS AND MATERIALS
        // =====================================================================

        private void DrawFocus()
        {
            focusFoldout = UCInspectorUI.Section(focusFoldout, "Focus and Materials",
                                                 UCInspectorUI.HeaderBlue);
            if (!focusFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                Transform focus = (Transform)EditorGUILayout.ObjectField(
                    new GUIContent("LOD Focus", "What the LOD grid centres on. Defaults to the main camera."),
                    manager.focus, typeof(Transform), true);
                if (focus != manager.focus)
                {
                    RecordManager("Change Focus");
                    manager.focus = focus;
                }

                Material shell = (Material)EditorGUILayout.ObjectField(
                    new GUIContent("Tunnel Shell Material", "Walls and floors of carved tunnels."),
                    manager.tunnelShellMaterial, typeof(Material), false);
                if (shell != manager.tunnelShellMaterial)
                {
                    RecordManager("Change Shell Material");
                    manager.tunnelShellMaterial = shell;
                }

                Color wire = EditorGUILayout.ColorField(
                    new GUIContent("Preview Colour", "Wireframe stand-in shown while a cell generates."),
                    manager.wireframePreviewColour);
                if (wire != manager.wireframePreviewColour)
                {
                    RecordManager("Change Preview Colour");
                    manager.wireframePreviewColour = wire;
                }
            }
        }

        // =====================================================================
        //  STATS
        // =====================================================================

        private void DrawStats()
        {
            statsFoldout = UCInspectorUI.Section(statsFoldout, "Stats",
                                                 UCInspectorUI.HeaderGrey);
            if (!statsFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                int side = owner.chunkCountPerSide;
                int cells = side * side;
                int high = owner.GetHighLODChunkCount();
                int low = owner.GetLowLODChunkCount();

                // Every count is shown with the grid that produces it. Bare numbers
                // read as grid dimensions - "High LOD 9" was taken for a 9x9 grid,
                // which is a plausible misreading of a number with no units on it.
                int highSide = Mathf.Clamp(owner.highLODRingSize, 1, side);

                Row("Grid", side + " x " + side + "  =  " + cells + " cells");
                Row("Fidelity Mode", low > 0
                        ? "Mixed  -  " + high + " high + " + low + " low"
                        : "Uniform high  -  all " + cells + " cells");

                EditorGUILayout.Space(2);

                // Resolution is uniform across every cell - it has to be, or
                // Terrain.SetNeighbors refuses to stitch the grid. Fidelity is
                // separated by screen-space error instead, which is Unity's own
                // continuous terrain LOD and costs nothing at the seams.
                int res = owner.GetChunkResolution();
                float hiErr = DualLODSystem.PixelErrorFor(LODLevel.High, owner.pixelError);
                float loErr = DualLODSystem.PixelErrorFor(LODLevel.Low, owner.pixelError);

                Row("Heightmap Res", res + " on every cell  (uniform, required for seams)");
                Row("  High fidelity", highSide + " x " + highSide + "  =  " + high +
                                       " cells  @  " + hiErr.ToString("F0") + " px error");
                Row("  Low fidelity", low + " cells  @  " + loErr.ToString("F0") + " px error");
                Row("  Buffer ring", owner.GetBufferChunkCount() +
                                     " cells pre-generated outside the grid");

                EditorGUILayout.Space(2);

                Row("Cell Size", owner.GetChunkSize().ToString("F0") + " m");
                Row("World Span", (owner.GetChunkSize() * side / 1000f).ToString("F2") + " km");
                Row("Metres Per Sample", owner.GetMetersPerPixel().ToString("F2") + " m");

                float mb = owner.GetEstimatedMemoryMB();
                Row("Estimated Memory", mb.ToString("F1") + " MB");

                DrawBudgetVerdict(mb, cells, low > 0);
            }
        }

        private void Row(string label, string value)
        {
            EditorGUILayout.LabelField(label, value, EditorStyles.miniLabel);
        }

        /// <summary>
        /// Selects the middle native Terrain cell and frames it. The world root and the
        /// Terrain container are both plain GameObjects, so selecting either shows a bare
        /// Transform - Unity's sculpt and paint toolbar only appears on an object that
        /// actually carries a Terrain component, which is one level further down.
        /// </summary>
        private void SelectTerrainCell()
        {
            // By component, not by name: this stays correct whatever the cells are called.
            Terrain[] cells = owner.GetComponentsInChildren<Terrain>(true);

            if (cells == null || cells.Length == 0)
            {
                Debug.LogWarning("Universe Creator: no native Terrain cells exist yet. " +
                                 "Use Rebuild Layout, or assign a graph and regenerate.");
                return;
            }

            // Middle of the list is the middle of the grid: cells are appended in row
            // order, so this lands on or beside the centre cell.
            GameObject pick = cells[cells.Length / 2].gameObject;

            Selection.activeGameObject = pick;
            EditorGUIUtility.PingObject(pick);

            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();
        }

        // Heightmaps are the cheap part; colliders are what actually hurt on a mid-range
        // machine, because TerrainCollider cooks a heightfield per cell. The thresholds
        // are deliberately about collider count, not megabytes.
        private void DrawBudgetVerdict(float mb, int cells, bool mixed)
        {
            int colliders = owner.generateCollider ? owner.GetHighLODChunkCount() : 0;

            if (!mixed)
            {
                EditorGUILayout.HelpBox(
                    "Every cell is at full fidelity. " + cells + " cells, " + colliders +
                    " colliders, " + mb.ToString("F1") + " MB of heightmap.\n" +
                    "Raise Chunks Per Side above the high-LOD ring size to get a low-" +
                    "fidelity outer ring back.",
                    cells > 25 ? MessageType.Warning : MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "Mixed fidelity is active - the outer ring runs at half resolution, so " +
                "it costs about a quarter of a high cell each.\n" +
                colliders + " terrain colliders (high-fidelity cells only), " +
                mb.ToString("F1") + " MB of heightmap. Comfortable for a mid-range " +
                "machine at this size.",
                MessageType.Info);
        }

        // =====================================================================
        //  ACTIONS
        // =====================================================================

        private void DrawActions()
        {
            using (new EditorGUI.DisabledScope(owner.graph == null))
            {
                EditorGUILayout.BeginHorizontal();

                if (UCInspectorUI.TintedButton("Rebuild All Cells", UCInspectorUI.ButtonBlue))
                {
                    Debug.Log("[Universe Creator] Rebuilding all cells...");
                    manager.RebuildAll();
                }

                if (UCInspectorUI.TintedButton("Cancel", UCInspectorUI.ButtonRed))
                {
                    Debug.Log("[Universe Creator] Generation cancelled.");
                    manager.CancelAll();
                }

                EditorGUILayout.EndHorizontal();
            }

            if (owner.graph == null)
                EditorGUILayout.HelpBox("Assign a graph on the world root to generate terrain.",
                                        MessageType.Info);

            EditorGUILayout.BeginHorizontal();

            if (UCInspectorUI.TintedButton("Select Terrain Cell", UCInspectorUI.ButtonBlue))
                SelectTerrainCell();

            if (UCInspectorUI.TintedButton("Capture Manual Edits", UCInspectorUI.ButtonAmber))
            {
                int captured = manager.CaptureManualEdits();
                Debug.Log("[Universe Creator] Captured " + captured + " sculpted samples.");
            }

            if (UCInspectorUI.TintedButton("Clear Manual Edits", UCInspectorUI.ButtonRed))
            {
                Debug.Log("[Universe Creator] Manual edits cleared.");
                manager.ClearManualEdits();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            // Cells are left alone unless the world values change, so per-cell edits made
            // in the native Terrain inspector stick. This is the way back to the world
            // values when a cell has been tuned by hand and should rejoin the group.
            if (UCInspectorUI.TintedButton("Push Terrain Settings To Cells", UCInspectorUI.ButtonBlue))
            {
                int pushed = manager.SyncTerrainSettings(true);
                Debug.Log("[Universe Creator] Pushed world Terrain Settings to " + pushed + " cells.");
            }

            if (UCInspectorUI.TintedButton("Clear All Tunnels", UCInspectorUI.ButtonRed))
            {
                Debug.Log("[Universe Creator] All tunnels cleared.");
                manager.ClearAllCarves();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Terrain Settings on each cell are Unity's own. Universe Creator pushes the " +
                "world values only when you change them here, so anything you tune per cell in " +
                "the native Terrain inspector survives a regeneration. Use the push button to " +
                "bring hand-tuned cells back in line.", MessageType.None);
        }

        // =====================================================================
        //  UNDO
        // =====================================================================

        private void RecordOwner(string label)
        {
            Undo.RecordObject(owner, label);
            ownerChanged = true;
        }

        private void RecordManager(string label)
        {
            Undo.RecordObject(manager, label);
            EditorUtility.SetDirty(manager);
        }
    }
}
