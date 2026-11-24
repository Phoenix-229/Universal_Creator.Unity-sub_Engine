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
using UniverseCreator.TerrainSystem.Geometry;

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
        // True once a MouseDrag happens during a slider grab. A bare click on a built-in
        // IntSlider/Slider track jumps its value but emits no MouseDrag, so this stays false
        // and the jumped value is discarded rather than committed - the fix for a stray click
        // on Resident Window / Height Offset triggering RebuildAll/RefreshSliceMap (R2-10/§19).
        private bool sliderDragged;

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
            if (!string.IsNullOrEmpty(manager.ReadinessDiagnostic))
                EditorGUILayout.HelpBox(manager.ReadinessDiagnostic, MessageType.Error); // UC-T09-03: a held landing/reveal names the failed dependency instead of silently hanging.
            if (!string.IsNullOrEmpty(manager.TerrainResidencyDiagnostic))
                EditorGUILayout.HelpBox(manager.TerrainResidencyDiagnostic, MessageType.Warning); // UC-T09-04: preserve the payload and expose a bounded retry.
            if (!string.IsNullOrEmpty(manager.FarTerrainDiagnostic))
                EditorGUILayout.HelpBox(manager.FarTerrainDiagnostic, MessageType.Warning);
            if (owner.retireDormantTerrain)
                EditorGUILayout.LabelField("Terrain Residency", $"Dormant {manager.DormantTerrainCount} / {manager.DormantTerrainBytes / 1048576.0:F2} MiB; distant {manager.FarTerrainCount} / {manager.FarTerrainBytes / 1048576.0:F2} MiB");
            if (!string.IsNullOrEmpty(manager.TerrainResidencyDiagnostic) || !string.IsNullOrEmpty(manager.FarTerrainDiagnostic))
            {
                if (GUILayout.Button(new GUIContent("Retry Terrain Residency", "Retry through the existing commit pump after freeing memory or correcting the payload.")))
                    manager.RetryTerrainResidency();
            }
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
                if (owner.graph != null)
                {
                    // A layout rebuild replaces the pool and re-runs the graph over
                    // the live terrains, so pull any hand sculpting out first on the
                    // exact same terms as the world's own commit path
                    // (UniverseCreatorObject.RebuildChunks): RebuildAll does not
                    // capture on its own, and a tool that drives it directly must, or
                    // the sculpt is lost under the regenerated surface. C-04.
                    if (owner.captureManualEditsOnRegenerate) manager.CaptureManualEdits();
                    manager.RebuildAll();
                }
                else
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
                float km = EditorGUILayout.DelayedFloatField(
                    new GUIContent("World Size (km)", "World side length in kilometres."),
                    owner.worldSizeKm);
                if (!Mathf.Approximately(km, owner.worldSizeKm))
                {
                    RecordOwner("Change World Size");
                    owner.worldSizeKm = km;
                    layoutChanged = true;
                }

                // CHUNK SIZE IS THE AUTHORED VALUE. The count is derived from it and the
                // world size, so a tile stays the size the developer asked for whatever
                // the world size - type 1000 and get 1 km tiles, not 800. This is the
                // inversion the old inspector had backwards, which is why setting the size
                // used to snap you back to a 25-chunk grid.
                float wantSize = EditorGUILayout.DelayedFloatField(
                    new GUIContent("Chunk Size (m)",
                                   "Metres per tile - the authored value. The chunk count follows from " +
                                   "this and the world size. 1000 = 1 km tiles."),
                    owner.chunkSizeMeters);
                if (wantSize > 0.01f && !Mathf.Approximately(wantSize, owner.chunkSizeMeters))
                {
                    RecordOwner("Change Chunk Size");
                    owner.chunkSizeMeters = wantSize;
                    layoutChanged = true;
                }

                // Resident window - 'the 25'. The tiles kept loaded around the player at
                // runtime. Fixed, not tied to world size, so a big world does not hold
                // itself resident and crash a phone. Odd only.
                if (Event.current.type == EventType.MouseDown) sliderDragged = false;
                if (Event.current.type == EventType.MouseDrag) sliderDragged = true;
                int window = EditorGUILayout.IntSlider(
                    new GUIContent("Resident Window",
                                   "Tiles per side kept loaded around the player at runtime ('the 25'). " +
                                   "Odd only. Not tied to world size."),
                    owner.residentWindowPerSide, 1, UniverseCreatorObject.MaxResidentWindowPerSide);
                // Accept a change only from a real drag OR a keyboard edit of the number box;
                // a bare click on the slider track (no drag, not editing text) is discarded so
                // focusing the row cannot trigger RebuildAll (R2-10 / §19). Typing still works
                // because editingTextField is true while the field has focus.
                if (window != owner.residentWindowPerSide &&
                    (sliderDragged || EditorGUIUtility.editingTextField))
                {
                    RecordOwner("Change Resident Window");
                    owner.residentWindowPerSide = window;
                    layoutChanged = true;
                }

                // Hybrid streaming toggle. On by default; only engages when the world is
                // bigger than the resident window, so a small world stays whole regardless.
                bool stream = EditorGUILayout.Toggle(
                    new GUIContent("Stream At Runtime",
                                   "On: a large world streams a window around the player in play mode. " +
                                   "Engages only when the world is bigger than the resident window - a " +
                                   "small world stays whole either way. Untick to force whole-world."),
                    owner.streamingEnabled);
                if (stream != owner.streamingEnabled)
                {
                    RecordOwner("Change Stream At Runtime");
                    owner.streamingEnabled = stream;
                    layoutChanged = true;
                }

                owner.ValidateSettings();

                int total = owner.chunkCountPerSide * owner.chunkCountPerSide;
                int resident = owner.residentWindowPerSide * owner.residentWindowPerSide;
                bool fits = (owner.chunkCountPerSide - 1) / 2 <= owner.GetStreamRadius();
                EditorGUILayout.LabelField(" ",
                    "Grid " + owner.chunkCountPerSide + " x " + owner.chunkCountPerSide +
                    " = " + total + " tiles  |  " + owner.GetChunkSize().ToString("F0") + " m each",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(" ",
                    (!owner.streamingEnabled || fits
                        ? "Runtime: whole world (fits the window, no streaming)"
                        : "Runtime: streaming " + resident + " (+ buffer ring)") + "  |  whole world in editor",
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
                    new GUIContent("High-LOD Ring", "Centre square of the resident window held at full fidelity."),
                    owner.highLODRingSize, 1, Mathf.Max(1, owner.residentWindowPerSide));
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

                    if (Event.current.type == EventType.MouseDown) sliderDragged = false;
                    if (Event.current.type == EventType.MouseDrag) sliderDragged = true;
                    float offset = EditorGUILayout.Slider(
                        new GUIContent("Height Offset (m)",
                                       "Metres above the surface, so the lines do not z-fight the terrain."),
                        manager.sliceMapHeightOffset, 0f, 50f);
                    // Drag-or-type only; a stray track-click is ignored (R2-10 / §19).
                    if (!Mathf.Approximately(offset, manager.sliceMapHeightOffset) &&
                        (sliderDragged || EditorGUIUtility.editingTextField))
                    {
                        RecordManager("Change Slice Map Offset");
                        manager.sliceMapHeightOffset = offset;
                        mapChanged = true;
                    }

                    if (Event.current.type == EventType.MouseDown) sliderDragged = false;
                    if (Event.current.type == EventType.MouseDrag) sliderDragged = true;
                    int samples = EditorGUILayout.IntSlider(
                        new GUIContent("Samples Per Cell",
                                       "Points along each cell edge. Higher hugs the surface more closely."),
                        manager.sliceMapSamplesPerCell, 1, 32);
                    // Drag-or-type only; a stray track-click is ignored (R2-10 / §19).
                    if (samples != manager.sliceMapSamplesPerCell &&
                        (sliderDragged || EditorGUIUtility.editingTextField))
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
                    new GUIContent("LOD Focus", "What the LOD grid centres on. Defaults to Main Camera. For a camera outside this world, also assign its Transform to LightingSystem > Lighting Graph > Environment Sample Target. Parenting is optional."),
                    manager.focus, typeof(Transform), true);
                if (focus != manager.focus)
                {
                    RecordManager("Change Focus");
                    manager.focus = focus;
                }

                DrawCameraDiagnostics(); // UC Scene/Play: reveal the actual lens/binding without silently moving a host camera.

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

        /// <summary>Displays the active focus camera and authored clip distances without modifying host transforms, layers or lenses.</summary>
        /// <remarks>Editor only; environment ownership is explicit for cameras outside the world hierarchy.</remarks>
        private void DrawCameraDiagnostics()
        {
            Camera camera = manager.focus != null ? manager.focus.GetComponent<Camera>() : null;
            if (camera == null) camera = Camera.main;
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("View Camera", camera, typeof(Camera), true);
            if (camera == null)
            {
                EditorGUILayout.HelpBox("Game view needs an enabled Camera. Assign its Transform to LOD Focus, or tag the intended camera MainCamera.", MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField("Camera Position (world m)", camera.transform.position.ToString("F2"));
            EditorGUILayout.LabelField("Camera Near / Far (m)", $"{camera.nearClipPlane:F2} / {camera.farClipPlane:F0}");
            if (!camera.isActiveAndEnabled)
                EditorGUILayout.HelpBox("This camera is disabled. Game view cannot use its image until the camera and its GameObject are enabled.", MessageType.Warning);
            var bindings = owner != null ? owner.GetComponent<UCWorldGraphBindings>() : null;
            if (owner != null && !camera.transform.IsChildOf(owner.transform) &&
                (bindings == null || bindings.EnvironmentSampleTarget != camera.transform))
                EditorGUILayout.HelpBox("For this external camera, assign its Transform to LightingSystem > Lighting Graph > Environment Sample Target so the world publishes its sky and weather for this view.", MessageType.Info);
            EditorGUILayout.HelpBox("Place this camera above the actual ground and aim it at the landscape. Frame the intended Scene view, select the camera and use GameObject > Align With View. Check its Culling Mask and near/far clipping in the Camera Inspector; UC preserves those settings. A character and camera parenting are optional.", MessageType.Info);
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

                Row("Heightmap Res", res + " on every cell  (uniform, required for seams)");
                Row("  High fidelity", highSide + " x " + highSide + "  =  " + high + " cells");
                Row("  Low fidelity", low + " cells");
                Row("  Buffer ring", owner.GetBufferChunkCount() +
                                     " cells pre-generated outside the grid");

                EditorGUILayout.Space(2);

                // Pixel error is no longer a per-tier authored number; it is the acuity
                // budget (L-49) floored at the source field's Nyquist limit (L-50) and
                // derived per cell from the live view distance, field of view and screen
                // height, so it is the on-screen error that is held constant across
                // displays rather than the world-space error a fixed number would freeze.
                float texelM = AngularError.TexelMetres(owner.GetChunkSize(), res);
                Row("Acuity budget", owner.acuityPixels.ToString("F2") +
                                     " px target screen error  (view-driven, per cell)");
                Row("Source limit", "detail floored at " + texelM.ToString("F2") +
                                    " m texel spacing  (Nyquist)");

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
                    // Same contract as the world's commit path: RebuildAll overwrites
                    // the live terrains, so capture hand sculpts first when capture is
                    // on, or they are lost under the regenerated surface. C-04.
                    if (owner.captureManualEditsOnRegenerate) manager.CaptureManualEdits();
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
