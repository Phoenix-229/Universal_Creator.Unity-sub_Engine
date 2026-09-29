// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    UCWaterBodyInspector.cs
//  Purpose: Authoring surface for a hand-placed water body: every field grouped
//           and tooltipped, seed and build actions, a live readout, and scene
//           view handles for the outline and course splines.
//
//  SPLINE HANDLES
//  --------------
//  River nodes are drawn in the scene view, Road Architect style:
//   - Hold CTRL + Click on the terrain to append a new node at the end.
//     A preview dotted line follows the cursor while Ctrl is held.
//   - SHIFT + Click on an existing segment to insert a node between two others.
//   - Drag a node handle to move it.  Press Delete to remove the nearest node.
//  Everything goes through Undo. A drag does not rebuild per mouse-move - it
//  rebuilds once on mouse-up. Every node append or insert triggers an immediate
//  rebuild so the river mesh and material appear as you draw.
//
//  NO DIALOGS. Anything that would need one is a Debug.Log or a HelpBox, so the
//  editor never stops on a modal popup.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(UCWaterBody))]
    [CanEditMultipleObjects]
    public class UCWaterBodyInspector : UnityEditor.Editor
    {
        private static bool levelFoldout = true;
        private static bool digFoldout = true;
        private static bool bedFoldout = true;
        private static bool meshFoldout;
        private static bool hydraulicsFoldout;
        private static bool physicsFoldout;
        private static bool underwaterFoldout;
        private static bool materialFoldout;

        // Screen-pixel radius a click has to land within to grab a point or a
        // segment. Handle-size relative would shrink to nothing when zoomed out,
        // which is exactly when a long river is hardest to edit.
        private const float PickPixels = 12f;

        // True between mouse-down on a handle and mouse-up. Set by the handle
        // drag, consumed by OnSceneGUI, so the rebuild happens once.
        private bool dragging;

        // Set when a field changed but the edit is not yet committed (still typing, or
        // mid-drag). Flushed to a single Rebuild once the text field is left / Enter is
        // pressed / the drag ends. Keeps regeneration off the keystroke and off a bare
        // selection - see the commit-gate in OnInspectorGUI.
        private bool pendingRebuild;

        private UCWaterBody Body { get { return (UCWaterBody)target; } }

        // =====================================================================
        //  INSPECTOR
        // =====================================================================

        public override void OnInspectorGUI()
        {
            UCWaterBody body = Body;
            if (body == null) return;

            UCInspectorUI.HeaderBar("Water Body", "d_Particle Effect",
                                    UCInspectorUI.HeaderBlue, StatusText(body));

            if (targets != null && targets.Length > 1)
            {
                DrawMultiSelect();
                return;
            }

            EditorGUILayout.Space(4);

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            DrawIdentity(body);

            // Applied immediately so the sections below see the shape the user just
            // picked. Without this the inspector shows the previous shape's fields
            // for one repaint, which reads as the dropdown not having worked.
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();

            levelFoldout = Group(levelFoldout, "Level Body", UCInspectorUI.HeaderBlue);
            if (levelFoldout) DrawLevel(body);

            digFoldout = Group(digFoldout, "Dig  (carves the terrain)", UCInspectorUI.HeaderGreen);
            if (digFoldout) DrawDig(body);

            bedFoldout = Group(bedFoldout, "Bed and Shoreline", UCInspectorUI.HeaderGrey);
            if (bedFoldout) DrawBed();

            meshFoldout = Group(meshFoldout, "Mesh", UCInspectorUI.HeaderGrey);
            if (meshFoldout) DrawMesh(body);

            hydraulicsFoldout = Group(hydraulicsFoldout, "Hydraulics", UCInspectorUI.HeaderPurple);
            if (hydraulicsFoldout) DrawHydraulics(body);

            physicsFoldout = Group(physicsFoldout, "Physics", UCInspectorUI.HeaderPurple);
            if (physicsFoldout) DrawPhysics(body);

            underwaterFoldout = Group(underwaterFoldout, "Underwater  (seen from inside)",
                                      UCInspectorUI.HeaderTeal);
            if (underwaterFoldout) DrawUnderwater(body);

            materialFoldout = Group(materialFoldout, "Material", UCInspectorUI.HeaderTeal);
            if (materialFoldout) DrawMaterial(body);

            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();

            // COMMIT-GATED REBUILD. EndChangeCheck flips true on every keystroke inside a
            // text field and every pixel of a slider drag, so rebuilding here directly - as
            // this used to - regenerated the whole river mid-typing and per drag-frame. That
            // is the "it works on key stroke, not on focus-out" the user reported, and the
            // spurious cost that made clicking a procedural body feel like it kicked off a
            // generation. Instead we remember that something changed and flush the rebuild
            // ONLY once the edit is committed: no text field is being edited
            // (EditorGUIUtility.editingTextField is false, i.e. Enter or focus-out), and no
            // drag is in progress (hotControl == 0). A bare selection changes nothing, so
            // pendingRebuild is never set and selecting a body rebuilds nothing.
            if (changed && body.buildTrigger == UCWaterBody.BuildTrigger.Automatic)
                pendingRebuild = true;

            if (pendingRebuild && !EditorGUIUtility.editingTextField && GUIUtility.hotControl == 0)
            {
                pendingRebuild = false;
                Rebuild(body);
            }
            else if (pendingRebuild)
            {
                // Still typing / dragging. Ask for another repaint so the flush above is
                // re-evaluated the moment the edit commits, rather than waiting for some
                // unrelated event to redraw the inspector.
                Repaint();
            }

            EditorGUILayout.Space(8);
            DrawActions(body);
            EditorGUILayout.Space(4);
            DrawReadout(body);
        }

        private void DrawMultiSelect()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "Several water bodies selected. Kind is fixed and never editable. " +
                "Shape, spline points, per-node depths and material are per body and " +
                "are hidden here; everything else applies to all of them. " +
                "Press Build All below when you are done.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            serializedObject.Update();
            // bodyKind is excluded for the same reason it is text in the single
            // view: it is not editable. Excluding it here also stops a multi-select
            // from offering one dropdown that would re-kind every selected body.
            // placementSaved is excluded because SavePlacement() also pins
            // onRegenerate; a raw toggle here would set the flag and leave the
            // body still refitting, which is the opposite of what it promises.
            DrawPropertiesExcluding(serializedObject,
                "m_Script", "bodyKind", "shape", "outline",
                "nodeDepthMetres", "placementSaved",
                "useOwnMaterial", "water", "materialOverride");
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            if (UCInspectorUI.TintedButton("Build All Selected", UCInspectorUI.ButtonGreen, 26))
                for (int i = 0; i < targets.Length; i++) Rebuild(targets[i] as UCWaterBody);
        }

        /// <summary>What this is and when it rebuilds - the two choices that
        /// decide everything below, so they sit above the foldouts.</summary>
        private void DrawIdentity(UCWaterBody body)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Kind is locked text, not a dropdown. A body is its kind - the fields
            // below were authored against it - so changing it means creating the
            // right body, not editing this one. Spawned by the presets via
            // SetKindOnce, which is why there is no setter to bind to here.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Kind");
            EditorGUILayout.LabelField(body.kind.ToString());
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("shape"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildTrigger"));

            EditorGUILayout.EndVertical();
        }

        private void DrawLevel(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            if (body.shape == UCWaterBody.ShapeMode.Rectangle)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sizeMetres"));
            }
            else
            {
                PolylineSummary(body.outline, "Outline",
                    "Closed shoreline. Click a node in the scene view to select it, drag " +
                    "the handle to move it; SHIFT-click a segment to insert a point; hover " +
                    "a point and press Delete to remove it.");

                EditorGUILayout.BeginHorizontal();
                if (ActionButton("Seed 12-Sided Outline", UCInspectorUI.ButtonBlue))
                    RunAuthoringAction(body, "Seed water outline", () => body.SeedOutline(12));

                if (ActionButton("Snap to Ground", UCInspectorUI.ButtonAmber))
                    RunAuthoringAction(body, "Snap water outline", body.SnapCourseToGround);
                EditorGUILayout.EndHorizontal();

                if (body.outline == null || !body.outline.IsValid)
                    EditorGUILayout.HelpBox(
                        "A polygon needs at least three points. Seed one, or add points " +
                        "in the scene view.", MessageType.Warning);
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("surfaceOffsetMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("currentMetresPerSecond"));

            EditorGUI.indentLevel--;
        }

        private int selectedNodeIndex = -1;

        // =====================================================================
        //  THE DIG
        // =====================================================================

        /// <summary>
        /// The hole in the ground. Ocean is deliberately a short section: it takes the
        /// bevel and nothing else, so showing it a bed profile and a depth would be
        /// offering settings that do nothing.
        /// </summary>
        private void DrawDig(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("digTerrain"));

            using (new EditorGUI.DisabledScope(!body.digTerrain))
            {
                bool ocean = body.kind == WaterBodyKind.Ocean;

                if (ocean)
                {
                    EditorGUILayout.HelpBox(
                        "An ocean rounds its beach and changes nothing else. Its depth, " +
                        "its seabed and its shoreline stay exactly what the terrain and " +
                        "the graph make them. Only the bevel below applies.",
                        MessageType.None);
                }
                else
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("bedProfile"));

                    EditorGUILayout.PropertyField(serializedObject.FindProperty("digDepthMetres"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("shoreRunMetres"));

                    EditorGUILayout.PropertyField(serializedObject.FindProperty("shelfFraction"));
                }

                EditorGUILayout.Space(2);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("shoreBevelMetres"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("bevelRoundness"));

                if (!ocean)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Bed ruggedness", EditorStyles.miniBoldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("bedRuggednessMetres"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ruggednessFeatureMetres"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ruggednessOctaves"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ruggednessPersistence"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ruggednessCalmMetres"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ruggednessSeed"));

                    // Basin shape is a lake and pool idea.
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Basin shape", EditorStyles.miniBoldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("basinIrregularity"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("basinFeatureMetres"));
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Procedural", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onRegenerate"));

                if (!ocean)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("corridorMetres"));

                    // Greyed on the corridor the dig uses, not on the typed field: with
                    // metres left at 0 a river still gets a derived valley, and strength
                    // is the dial that says how much of it lands.
                    using (new EditorGUI.DisabledScope(body.ValleyCorridorMetres <= 0f))
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("corridorStrength"));
                }

                DrawProceduralStatus(body);

                // Save Placement - only for a body the generator owns (its name matches
                // the generated scheme). Pins it so the next Regenerate keeps it: the
                // reconcile and the disconnect teardown skip it, and its footprint is
                // held and re-cut rather than re-fitted. Hand-placed bodies are already
                // safe by name, so the control is not shown for them. The mutation is
                // flushed and the serialized view re-synced so the apply at the end of
                // OnInspectorGUI does not revert it (same pattern as the shape dropdown).
                // The lock is offered to any body a regeneration could move, not only
                // one whose name still matches the generated scheme: a generated body
                // the author renamed, or wired to a node, is exactly the one that most
                // needs the protection, and gating on the name alone loses it the moment
                // it is renamed. The real signals are - it is already saved (so it can be
                // released), it re-fits on regenerate, it is linked to a graph node, or
                // its name still matches. Any one of them means this is a procedural body
                // the generator owns and the bake/lock applies.
                bool canManagePlacement = !ocean &&
                    (body.placementSaved ||
                     body.onRegenerate == UCWaterBody.ProceduralResponse.RefitAutomatically ||
                     body.IsGraphLinked ||
                     HydrologyGenerator.IsGeneratedBodyName(body.name));
                if (canManagePlacement)
                {
                    EditorGUILayout.Space(2);
                    if (body.placementSaved)
                    {
                        EditorGUILayout.HelpBox(
                            "Placement saved. This procedural body is protected from the " +
                            "next regeneration - not culled or removed, and its footprint " +
                            "is held and re-cut into the new terrain instead of re-fitted.",
                            MessageType.Info);
                        if (GUILayout.Button(new GUIContent("Release to Generator",
                            "Hand this body back to the generator so a regeneration can " +
                            "re-fit, re-count and remove it again.")))
                        {
                            serializedObject.ApplyModifiedProperties();
                            Undo.RecordObject(body, "Release water placement");
                            body.ReleasePlacement();
                            EditorUtility.SetDirty(body);
                            serializedObject.Update();
                        }
                    }
                    else if (GUILayout.Button(new GUIContent("Save Placement (protect from Regenerate)",
                        "Keep this body's current placement and edits when the world is " +
                        "regenerated. The generator will no longer move, re-count or remove it.")))
                    {
                        serializedObject.ApplyModifiedProperties();
                        Undo.RecordObject(body, "Save water placement");
                        body.SavePlacement();
                        EditorUtility.SetDirty(body);
                        serializedObject.Update();
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Says in one line what a regeneration will do to this body, because the
        /// answer depends on a node connection the user cannot see from here.
        /// </summary>
        private void DrawProceduralStatus(UCWaterBody body)
        {
            bool linked = body.IsGraphLinked;

            string message;
            MessageType type;

            if (body.onRegenerate == UCWaterBody.ProceduralResponse.WaitForBuild)
            {
                message = "Regeneration re-cuts this dig but never moves it. Press Build " +
                          "when you want it re-placed.";
                type = MessageType.None;
            }
            else if (linked)
            {
                message = "Linked to a node in the graph. A regeneration or a cell rebuild " +
                          "re-places this body automatically - no Build press needed.";
                type = MessageType.Info;
            }
            else
            {
                message = "Not linked to a graph node. The channel is always re-cut, so a " +
                          "regeneration cannot erase it - but to re-fit it to ground that " +
                          "has moved, press Build again.";
                type = MessageType.Warning;
            }

            EditorGUILayout.HelpBox(message, type);

            // The connection tick the author asked to SEE: a read-only confirmation that
            // this body is node-joined to another (ocean outlet -> river, river -> lake,
            // two courses merged). It reads the real join reference, so a tick here means
            // the plumbing is genuinely wired, not that we hope it is.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ToggleLeft(
                    body.IsConnected
                        ? "Connected to another body:  " + body.ConnectionSummary
                        : "Not connected to another body",
                    body.IsConnected);
        }


        private void DrawBed()
        {
            EditorGUI.indentLevel++;

            SerializedProperty source = serializedObject.FindProperty("bedSource");
            EditorGUILayout.PropertyField(source);

            if (source.enumValueIndex == (int)UCWaterBody.BedSource.FlatFloor)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("flatFloorDepthMetres"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("minDepthMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("edgeFeatherMetres"));

            EditorGUI.indentLevel--;
        }

        private void DrawMesh(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            if (body.shape != UCWaterBody.ShapeMode.RiverSpline)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("meshResolution"));

            EditorGUI.indentLevel--;
        }

        private void DrawHydraulics(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("manningRoughness"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("hydraulicRadiusCap"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxFlowSpeed"));

            if (body.shape != UCWaterBody.ShapeMode.RiverSpline)
                EditorGUILayout.HelpBox(
                    "A level body has no surface slope, so Manning's equation gives it " +
                    "zero velocity and these three do nothing. Its motion comes from " +
                    "Current above. Switch the shape to River Spline to use them.",
                    MessageType.None);

            EditorGUI.indentLevel--;
        }

        private void DrawPhysics(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateVolume"));

            using (new EditorGUI.DisabledScope(!body.generateVolume))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("flowForce"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("viscosityScale"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("emitRipples"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("densityOverride"));

                float density = body.densityOverride > 0.01f
                    ? body.densityOverride
                    : (body.kind == WaterBodyKind.Ocean ? 1025f : 998f);
                EditorGUILayout.LabelField(" ", "Effective density " + density.ToString("0") + " kg/m3",
                                           EditorStyles.miniLabel);
            }

            EditorGUI.indentLevel--;
        }

        // What the body looks like from inside it, which is a different question from
        // what it looks like from above: from above you mostly see a reflection, from
        // inside every ray reaching the eye has been filtered by metres of water.
        // Seeded per kind at spawn, so this group is here to tune a specific body -
        // a storm-silted river, a night dive - not to make water look like water.
        private void DrawUnderwater(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("underwaterView"));

            using (new EditorGUI.DisabledScope(!body.underwaterView))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("underwaterColor"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("visibilityMetres"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("underwaterBrightness"));
            }

            // The volume is what the camera queries, so no volume means no dive
            // however these fields are set. Worth saying here rather than letting
            // the user conclude the underwater view is broken.
            if (!body.generateVolume)
                EditorGUILayout.HelpBox(
                    "Generate Volume is off under Physics, so this body has nothing for a " +
                    "camera to query and the view will not change when you swim into it.",
                    MessageType.Warning);
            else if (body.underwaterView)
                EditorGUILayout.HelpBox(
                    "Needs a UC Underwater View component on the camera - that is the piece " +
                    "that notices the camera has gone under and tells the shaders. Add it " +
                    "once to your main camera and every body in the scene works.",
                    MessageType.None);

            EditorGUI.indentLevel--;
        }

        private void DrawMaterial(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("useOwnMaterial"));

            if (body.useOwnMaterial)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("water"), true);
            else
                EditorGUILayout.HelpBox(
                    "Sharing the world's default water material. Every body without its " +
                    "own description draws with the same material, which is what keeps " +
                    "them batchable.", MessageType.None);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("materialOverride"));

            if (body.materialOverride != null)
                EditorGUILayout.HelpBox(
                    "A material override is set, so the description above is ignored. " +
                    "The geometry, the flow stream and the physics still come from here.",
                    MessageType.None);

            EditorGUI.indentLevel--;
        }

        /// <summary>Point count and its editing tooltip. The list itself is not
        /// drawn: a 40-point river as forty Vector3 rows buries every other
        /// control, and the points are meant to be dragged, not typed.</summary>
        private static void PolylineSummary(Polyline line, string label, string tooltip)
        {
            int count = line != null ? line.Count : 0;

            Rect row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            UCInspectorUI.AccentStrip(row, UCInspectorUI.HeaderGrey);

            GUILayout.Space(4);
            EditorGUILayout.LabelField(new GUIContent(label + ": " + count + " points", tooltip),
                                       EditorStyles.boldLabel);

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Section foldout that does not count as an edit. The whole inspector sits
        /// inside one change check, and IMGUI sets GUI.changed when a foldout is
        /// toggled, so without this every collapse rebuilt the body.
        /// </summary>
        private static bool Group(bool state, string title, Color accent)
        {
            bool before = GUI.changed;
            bool next = UCInspectorUI.Section(state, title, accent);
            GUI.changed = before;
            return next;
        }

        /// <summary>
        /// Action button that does not count as an edit, for the same reason. These
        /// buttons rebuild the body themselves; letting the change check see the
        /// click would rebuild it a second time in the same repaint.
        /// </summary>
        private static bool ActionButton(string label, Color tint, float height = 20f)
        {
            bool before = GUI.changed;
            bool clicked = UCInspectorUI.TintedButton(label, tint, height);
            if (clicked) GUI.changed = before;
            return clicked;
        }

        // =====================================================================
        //  ACTIONS AND READOUT
        // =====================================================================

        private void DrawActions(UCWaterBody body)
        {
            EditorGUILayout.BeginHorizontal();

            if (UCInspectorUI.TintedButton("Build Now", UCInspectorUI.ButtonGreen, 26))
                Rebuild(body);

            if (UCInspectorUI.TintedButton("Clear", UCInspectorUI.ButtonAmber, 26))
                body.Clear();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawReadout(UCWaterBody body)
        {
            WaterBodyData built = body.LastBuild;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Last Build", EditorStyles.boldLabel);

            if (built == null)
            {
                EditorGUILayout.LabelField(
                    body.buildTrigger == UCWaterBody.BuildTrigger.Manual
                        ? "Nothing built. Press Build Now."
                        : "Nothing built. Check the shape has enough points.");
            }
            else
            {
                Readout("Vertices", built.VertexCount.ToString());
                Readout("Triangles", built.TriangleCount.ToString());
                Readout("Surface level", built.surfaceLevel.ToString("0.00") + " m");
                Readout("Max depth", built.maxDepth.ToString("0.00") + " m");
                Readout("Mean flow", (built.meanFlow.magnitude).ToString("0.00") + " m/s");
                Readout("Flow scale", built.maxFlowSpeed.ToString("0.00") + " m/s");
            }

            EditorGUILayout.EndVertical();
        }

        private static void Readout(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(120));
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Runs an authoring helper under Undo and rebuilds after it.
        /// The helpers mutate the Polyline lists in place, so the record has to
        /// happen before the call rather than after.</summary>
        private void RunAuthoringAction(UCWaterBody body, string undoLabel, System.Action action)
        {
            Undo.RecordObject(body, undoLabel);
            action();
            EditorUtility.SetDirty(body);
            serializedObject.Update();
            Rebuild(body);
        }

        private static void Rebuild(UCWaterBody body)
        {
            if (body == null) return;
            try
            {
                body.Rebuild();
            }
            catch (System.Exception e)
            {
                // The build samples terrain and walks splines. A malformed spline
                // must not throw on every repaint and take the inspector with it.
                Debug.LogError("Universe Creator: water body build failed on '" +
                               body.name + "'. " + e.Message, body);
            }
        }

        private static string StatusText(UCWaterBody body)
        {
            if (body == null) return "";

            if (body.shape == UCWaterBody.ShapeMode.Polygon &&
                (body.outline == null || !body.outline.IsValid)) return "no outline";

            if (body.LastBuild == null) return "not built";

            return body.BuiltTriangleCount + " tris";
        }

        // =====================================================================
        //  SCENE VIEW
        // =====================================================================

        private void OnSceneGUI()
        {
            UCWaterBody body = target as UCWaterBody;
            if (body == null) return;

            Polyline line = null;
            Color tint = Color.white;

            if (body.shape == UCWaterBody.ShapeMode.Polygon)
            {
                line = body.outline;
                tint = new Color(0.35f, 0.75f, 1f, 1f);
            }

            if (line == null) return;

            // ---- Draw existing outline and handles ----
            if (line.Count > 0)
            {
                DrawSegments(body, line, tint);
                DrawPointHandles(body, line);
                HandleShiftInsert(body, line);
                HandleDelete(body, line);
            }

            // One rebuild per drag, on release.
            if (dragging && Event.current.type == EventType.MouseUp)
            {
                dragging = false;
                EditorUtility.SetDirty(body);
                Rebuild(body);
            }
        }

        /// <summary>
        /// Raycasts against physics colliders first, then falls back to sampling
        /// terrain height via a ground plane. Returns true if a valid ground
        /// point was found.
        /// </summary>
        private static bool RaycastTerrain(Ray ray, UCWaterBody body, out Vector3 point)
        {
            // Try physics first (terrain colliders)
            if (Physics.Raycast(ray, out RaycastHit hit, 10000f))
            {
                point = hit.point;
                return true;
            }

            // Fallback: intersect a horizontal plane at Y=0 and sample terrain height
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float enter))
            {
                point = ray.GetPoint(enter);
                point.y = UCWaterBody.SampleGround(Terrain.activeTerrains, point, point.y);
                return true;
            }

            point = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Draws the line the body is actually built from.
        ///
        /// A polygon outline stays straight, because a polygon edge IS straight -
        /// bending it would be inventing geometry the fill does not use.
        /// </summary>
        private static void DrawSegments(UCWaterBody body, Polyline line, Color tint)
        {
            if (Event.current.type != EventType.Repaint || line.Count < 2) return;

            Transform t = body.transform;
            Handles.color = tint;

            int segments = line.closed ? line.Count : line.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3 a = t.TransformPoint(line.PointAt(i));
                Vector3 b = t.TransformPoint(line.PointAt(i + 1));
                Handles.DrawLine(a, b, 3f);
            }
        }

        // =====================================================================
        //  NODE MARKERS
        //
        //  Every outline point is a post you can click, and only the selected one
        //  gets a three-axis handle to drag. Drawing a full PositionHandle for every
        //  point turned a many-sided outline into overlapping gizmos with no way to
        //  click a node without dragging it - the first click of a select was already
        //  a move.
        // =====================================================================

        private static readonly Color NodePlain = new Color(0.42f, 0.72f, 1f);      // outline blue
        private static readonly Color NodeSelected = new Color(1f, 0.92f, 0.25f);

        private void DrawPointHandles(UCWaterBody body, Polyline line)
        {
            Transform t = body.transform;

            Color entry = Handles.color;

            for (int i = 0; i < line.Count; i++)
            {
                Vector3 p = t.TransformPoint(line.points[i]);

                bool selected = i == selectedNodeIndex;

                float hs = HandleUtility.GetHandleSize(p);
                float post = hs * 0.6f;
                float cap = hs * (selected ? 0.16f : 0.09f);

                Vector3 top = p + Vector3.up * post;

                Handles.color = selected ? NodeSelected : NodePlain;

                Handles.DrawLine(p, top);

                if (selected)
                {
                    Handles.CubeHandleCap(0, top, Quaternion.identity, cap, EventType.Repaint);
                    DrawNodeLabel(i, top, cap);
                    continue;   // its position handle is drawn after the loop
                }

                // One click, one selection, no drag. Handles.Button is what makes the
                // marker itself the hit target, so the manual WorldToGUIPoint distance
                // test this used to do - which fired on any mouse-down anywhere near the
                // point, handle or not - is gone.
                if (Handles.Button(top, Quaternion.identity, cap, cap * 1.6f,
                                   Handles.CubeHandleCap))
                {
                    selectedNodeIndex = i;
                    Repaint();
                }
            }

            Handles.color = entry;

            DrawSelectedNodeHandle(body, line);
        }

        /// <summary>The drag handle, for the selected node only.</summary>
        private void DrawSelectedNodeHandle(UCWaterBody body, Polyline line)
        {
            int i = selectedNodeIndex;
            if (i < 0 || i >= line.Count) return;

            Transform t = body.transform;
            Vector3 at = t.TransformPoint(line.points[i]);

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(at, t.rotation);
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(body, "Move water point");

            line.points[i] = t.InverseTransformPoint(moved);
            dragging = true;
        }

        /// <summary>The node's index, floating above its post.</summary>
        private static void DrawNodeLabel(int i, Vector3 top, float cap)
        {
            Handles.Label(top + Vector3.up * (cap * 2.2f), "[" + i + "]");
        }

        /// <summary>Shift-click on a segment inserts a point at the click position.
        /// Like Road Architect's Shift+Click insert mode.</summary>
        private void HandleShiftInsert(UCWaterBody body, Polyline line)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !e.shift) return;
            if (line.Count < 2) return;

            Transform t = body.transform;

            int bestSegment = -1;
            float bestDistance = PickPixels;
            Vector3 bestPoint = Vector3.zero;

            int segments = line.closed ? line.Count : line.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3 a = t.TransformPoint(line.PointAt(i));
                Vector3 b = t.TransformPoint(line.PointAt(i + 1));

                float distance = HandleUtility.DistanceToLine(a, b);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                bestSegment = i;

                Ray segmentRay = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                bestPoint = ClosestPointOnSegment(a, b, segmentRay);
            }

            if (bestSegment < 0) return;

            Undo.RecordObject(body, "Insert water point");

            int insertAt = bestSegment + 1;
            line.points.Insert(insertAt, t.InverseTransformPoint(bestPoint));
            if (line.weights != null && line.weights.Count >= insertAt)
                line.weights.Insert(insertAt, line.WeightAt(bestSegment));

            selectedNodeIndex = insertAt;

            EditorUtility.SetDirty(body);
            Rebuild(body);
            e.Use();
        }

        /// <summary>Delete removes the point nearest the cursor. Nearest and not
        /// "selected", because a position handle has no selected state to read.</summary>
        private void HandleDelete(UCWaterBody body, Polyline line)
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode != KeyCode.Delete && e.keyCode != KeyCode.Backspace) return;

            int floor = line.closed ? 3 : 2;
            if (line.Count <= floor) return;

            Transform t = body.transform;

            // Hit-test against the SAME world point the node post is drawn at, or Delete
            // targets a different screen position than the marker the user is aiming at.
            int best = -1;
            float bestDistance = PickPixels * 2f;

            for (int i = 0; i < line.Count; i++)
            {
                Vector3 wp = t.TransformPoint(line.points[i]);
                Vector2 gui = HandleUtility.WorldToGUIPoint(wp);
                float distance = Vector2.Distance(gui, e.mousePosition);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = i;
            }

            if (best < 0) return;

            RemoveNodeAt(body, line, best);
            e.Use();
        }

        /// <summary>Removes control point <paramref name="index"/> together with its weight,
        /// fixes the selection, marks the body dirty and rebuilds. Shared by the scene-view
        /// Delete key and the inspector's "Delete this node" button so the two paths can never
        /// drift apart. Refuses to drop a shape below the minimum it needs to exist (2 points
        /// for an open line, 3 for a closed outline) - the same floor the key path enforces.</summary>
        private void RemoveNodeAt(UCWaterBody body, Polyline line, int index)
        {
            if (line == null || index < 0 || index >= line.Count) return;
            int floor = line.closed ? 3 : 2;
            if (line.Count <= floor) return;

            Undo.RecordObject(body, "Remove water point");

            line.points.RemoveAt(index);
            if (line.weights != null && line.weights.Count == line.points.Count + 1)
                line.weights.RemoveAt(index);

            if (selectedNodeIndex == index) selectedNodeIndex = -1;
            else if (selectedNodeIndex > index) selectedNodeIndex--;

            EditorUtility.SetDirty(body);
            Rebuild(body);
        }

        /// <summary>Point on segment ab nearest the camera ray. This is the
        /// closest-approach of two lines, clamped to the segment.</summary>
        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Ray ray)
        {
            Vector3 ab = b - a;
            float abLengthSqr = ab.sqrMagnitude;
            if (abLengthSqr < 1e-6f) return a;

            Vector3 w = a - ray.origin;
            float d = Vector3.Dot(ab, ray.direction);
            float e = Vector3.Dot(ray.direction, ray.direction);
            float denominator = abLengthSqr * e - d * d;

            // Parallel: the ray gives no useful position along the segment, so
            // fall back to its midpoint rather than dividing by ~0.
            if (Mathf.Abs(denominator) < 1e-6f) return (a + b) * 0.5f;

            float s = (d * Vector3.Dot(ray.direction, w) - e * Vector3.Dot(ab, w)) / denominator;
            return a + ab * Mathf.Clamp01(s);
        }
    }
}
