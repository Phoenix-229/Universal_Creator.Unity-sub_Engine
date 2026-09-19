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
        private static bool riverFoldout = true;
        private static bool digFoldout = true;
        private static bool routingFoldout = true;
        private static bool nodesFoldout;
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

            if (body.shape != UCWaterBody.ShapeMode.RiverSpline)
            {
                levelFoldout = Group(levelFoldout, "Level Body", UCInspectorUI.HeaderBlue);
                if (levelFoldout) DrawLevel(body);
            }
            else
            {
                riverFoldout = Group(riverFoldout, "River Channel", UCInspectorUI.HeaderBlue);
                if (riverFoldout) DrawRiver(body);

                routingFoldout = Group(routingFoldout, "Routing and Junctions", UCInspectorUI.HeaderAmber);
                if (routingFoldout) DrawRouting(body);
            }

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
                "m_Script", "bodyKind", "shape", "outline", "course",
                "nodeDepthMetres", "nodeWidthMetres", "placementSaved",
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
        private bool isConnectingJunction = false;

        private void DrawRiver(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            PolylineSummary(body.course, "Course",
                "Centre line, upstream point first. Hold CTRL + Click on the terrain in the scene view to draw the river point by point. SHIFT + Click on a segment to insert a node.");

            EditorGUILayout.BeginHorizontal();
            if (ActionButton("Snap to Ground", UCInspectorUI.ButtonAmber))
                RunAuthoringAction(body, "Snap river course", body.SnapCourseToGround);
            if (ActionButton("Clear Course", UCInspectorUI.ButtonRed))
                RunAuthoringAction(body, "Clear river course", () => { 
                    body.course.Clear(); 
                    if (body.nodeWidthMetres != null) body.nodeWidthMetres.Clear();
                    if (body.nodeDepthMetres != null) body.nodeDepthMetres.Clear();
                    selectedNodeIndex = -1; 
                });
            EditorGUILayout.EndHorizontal();

            if (body.course == null || !body.course.IsValid)
                EditorGUILayout.HelpBox(
                    "Hold CTRL + Click in the Scene view to start drawing your river.", MessageType.Warning);
            else if (body.CourseDropMetres() < 0f)
                EditorGUILayout.HelpBox(
                    "The course was drawn uphill: the last point is higher than the " +
                    "first. Reverse the points, or leave Enforce Downhill on and the " +
                    "river will pond instead of climbing.", MessageType.Warning);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("widthMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("widthSegments"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pointSpacingMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("channelDepthMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("bankProfile"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enforceDownhill"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("profileSmoothing"));

            // Selected Node Menu
            if (body.course != null && selectedNodeIndex >= 0 && selectedNodeIndex < body.course.Count)
                DrawSelectedNode(body);

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// The one node the designer clicked, and nothing about the other thirty-nine.
        /// </summary>
        /// <remarks>
        /// Elevation is the reading that has to be handled carefully, because the honest
        /// answer differs by mode and the box used to pretend it did not. It edited the
        /// course point's local Y, which the solve discards in every mode except
        /// flat-floor: a river's elevation is set by the ground it crosses, so typing a
        /// number here moved nothing and matched nothing on screen.
        ///
        /// So the box now shows UCWaterBody.NodeSurfaceY - where the water actually is at
        /// that node - and is editable only in the mode where an authored Y is what the
        /// river is built from. Elsewhere it is a live readout with the reason next to it.
        /// A control that does nothing is worse than one that is greyed out, because the
        /// greyed one tells you why.
        ///
        /// Grade is the figure a road node carries and the one a course is actually
        /// steered with. "This reach is at 9%" is a rapid; "0.2%" is a pond. It is what
        /// tells you which single node is wrong on a river that looks wrong.
        /// </remarks>
        private void DrawSelectedNode(UCWaterBody body)
        {
            int i = selectedNodeIndex;
            int last = body.course.Count - 1;

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string title = "Selected Node [" + i + "]";
            if (i == 0) title += "   Head";
            else if (i == last) title += "   Mouth";

            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            bool authoredY = body.bedSource == UCWaterBody.BedSource.FlatFloor;

            // Kept in step with the course here as well as in OnValidate: a node can be
            // appended in the scene view and clicked in the same frame, before anything
            // has validated, and an index past the end of these lists throws.
            while (body.nodeDepthMetres.Count < body.course.Count) body.nodeDepthMetres.Add(0f);
            while (body.nodeWidthMetres.Count < body.course.Count) body.nodeWidthMetres.Add(0f);

            EditorGUI.BeginChangeCheck();

            float shownY = body.NodeSurfaceY(i);
            float newElevation;

            using (new EditorGUI.DisabledScope(!authoredY))
                newElevation = EditorGUILayout.FloatField(
                    new GUIContent(authoredY ? "Elevation (m)" : "Elevation (m, from ground)",
                                   authoredY
                                       ? "The water surface at this node. Flat Floor builds "
                                       + "the river from the line as drawn, so this is yours "
                                       + "to set."
                                       : "Where the water is at this node, read off the "
                                       + "ground. Switch Bed Source to Flat Floor to set "
                                       + "elevations by hand."),
                    shownY);

            float newDepth = EditorGUILayout.FloatField(
                new GUIContent("Depth (0 to inherit)",
                               "Channel depth at this node in metres. 0 carries the depth "
                               + "of the last node that set one, easing between them."),
                body.nodeDepthMetres[i]);

            float newWidth = EditorGUILayout.FloatField(
                new GUIContent("Width (0 to inherit)",
                               "Channel width at this node in metres. 0 carries the width "
                               + "of the last node that set one."),
                body.nodeWidthMetres[i]);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(body, "Edit River Node");

                if (authoredY && !Mathf.Approximately(newElevation, shownY))
                {
                    // The box holds the SURFACE, the point holds the line the surface is
                    // measured down from, so the offsets come back off before it is stored.
                    Vector3 w = body.NodeWorldPoint(i);
                    w.y = newElevation - body.surfaceOffsetMetres + body.flatFloorDepthMetres;
                    body.SetNodeWorldPoint(i, w);
                }

                body.nodeDepthMetres[i] = newDepth;
                body.nodeWidthMetres[i] = newWidth;

                EditorUtility.SetDirty(body);
                // Per-node typing fires EndChangeCheck on EVERY keystroke. Do NOT rebuild here;
                // arm the commit gate so the rebuild runs once the edit is committed
                // (focus-out / Enter / slider release), never per character.
                if (body.buildTrigger == UCWaterBody.BuildTrigger.Automatic) pendingRebuild = true;
            }

            // Grade either side, the way a road node reports it.
            if (body.course.Count >= 2)
            {
                string grades = "";
                if (i > 0) grades += "from " + body.NodeGradePercentFromPrev(i).ToString("0.##") + "%";
                if (i > 0 && i < last) grades += "    ";
                if (i < last) grades += "to " + body.NodeGradePercentToNext(i).ToString("0.##") + "%";

                EditorGUILayout.LabelField("Grade", grades);

                bool climbs = (i > 0 && body.NodeGradePercentFromPrev(i) > 0.01f)
                           || (i < last && body.NodeGradePercentToNext(i) < -0.01f);

                if (climbs)
                    EditorGUILayout.HelpBox(
                        "This node is uphill of its neighbour. The solve will flatten the "
                        + "reach rather than let water climb, so the river will pond here. "
                        + "Move the node downhill to fix it.", MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            if (body.IsEndNode(i))
            {
                UCWaterBody joined = body.JunctionAt(i);

                if (joined != null)
                {
                    EditorGUILayout.LabelField("Joined to", joined.name + "  (" + joined.kind + ")");

                    if (GUILayout.Button(new GUIContent("Clear Connection",
                            "Drops the junction. An end left unjoined that runs into water "
                            + "on its way downhill will be reconnected by the build.")))
                    {
                        Undo.RecordObject(body, "Clear River Junction");

                        if (i == 0) body.joinUpstreamFrom = null;
                        else body.joinDownstreamTo = null;

                        EditorUtility.SetDirty(body);
                        if (body.buildTrigger == UCWaterBody.BuildTrigger.Automatic) Rebuild(body);
                    }
                }
                else if (isConnectingJunction)
                {
                    EditorGUILayout.HelpBox(
                        "Click another water body in the Scene view to connect this node to "
                        + "it. Press ESC to cancel.", MessageType.Info);

                    if (GUILayout.Button("Cancel Connection"))
                    {
                        isConnectingJunction = false;
                        SceneView.RepaintAll();
                    }
                }
                else if (GUILayout.Button(new GUIContent("Connect to Water Body",
                             "Click to select a target water body in the scene view to join "
                             + "this end to.")))
                {
                    isConnectingJunction = true;
                    SceneView.RepaintAll();
                }
            }

            EditorGUILayout.EndVertical();
        }

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
                bool river = body.shape == UCWaterBody.ShapeMode.RiverSpline;

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

                    if (river)
                    {
                        EditorGUILayout.LabelField(" ", "Depth comes from Channel Depth above",
                                                   EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("digDepthMetres"));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("shoreRunMetres"));
                    }

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

                    // Basin shape is a lake and pool idea. A river's cross-section
                    // really is close to symmetric, and the irregularity is skipped for
                    // it in the dig, so offering the dials here would be a lie.
                    if (!river)
                    {
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField("Basin shape", EditorStyles.miniBoldLabel);
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("basinIrregularity"));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("basinFeatureMetres"));
                    }
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Procedural", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onRegenerate"));

                if (!ocean)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("corridorMetres"));

                    // The valley. Only a river has one - a lake's shore is its bevel -
                    // and it was reachable from nothing until this row existed, so every
                    // river silently took the derived default with no way to tune it.
                    if (river)
                    {
                        EditorGUILayout.PropertyField(
                            serializedObject.FindProperty("valleyWidthMultiple"));

                        // The metres the dig will actually use, read from the body rather
                        // than recomputed here, so this row cannot disagree with the cut.
                        float valley = body.ValleyCorridorMetres;
                        EditorGUILayout.LabelField(" ",
                            valley > 0f
                                ? "Valley " + valley.ToString("0.#") + " m beyond the bank"
                                  + (body.corridorMetres > 0f ? "  (typed)" : "  (from width)")
                                : "No valley - the dig cuts what it finds",
                            EditorStyles.miniLabel);
                    }

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
                if (!ocean && HydrologyGenerator.IsGeneratedBodyName(body.name))
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
        }

        // =====================================================================
        //  ROUTING
        // =====================================================================

        private void DrawRouting(UCWaterBody body)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("fitToValley"));

            using (new EditorGUI.DisabledScope(!body.fitToValley))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("valleySearchMetres"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("valleyPull"));
            }
            
            if (GUILayout.Button(new GUIContent("Reroute to Pass Through Valley",
                                           "Permanently moves your spline nodes into the lowest nearby valley based on the valley settings above."),
                                 GUILayout.Height(24)))
            {
                Undo.RecordObject(body, "Reroute through valley");
                body.ApplyValleyReroute();
                EditorUtility.SetDirty(body);
                if (body.buildTrigger == UCWaterBody.BuildTrigger.Automatic) Rebuild(body);
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("routeAroundHighGround"));

            using (new EditorGUI.DisabledScope(!body.routeAroundHighGround))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("routeFanDegrees"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("climbToleranceMetres"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sinkRunMetres"));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Junctions", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoConnectMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("joinDownstreamTo"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("joinUpstreamFrom"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("joinBlendMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("joinMatchesDepth"));

            EditorGUILayout.HelpBox(
                "A river can join an ocean, a lake or another river - never a pool. " +
                "Leave the two fields empty and the mouth connects by itself to " +
                "whatever ocean or lake it reaches within Auto Connect metres.",
                MessageType.None);

            EditorGUI.indentLevel--;
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

            if (body.shape == UCWaterBody.ShapeMode.RiverSpline)
            {
                EditorGUILayout.Space(2);
                Readout("Course length", body.CourseLengthMetres().ToString("0.0") + " m");

                float drop = body.CourseDropMetres();
                Readout("Course drop", drop.ToString("0.0") + " m" + (drop < 0f ? "  (uphill)" : ""));

                float length = body.CourseLengthMetres();
                if (length > 1f)
                    Readout("Mean gradient", (100f * Mathf.Max(0f, drop) / length).ToString("0.00") + " %");
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

            if (body.shape == UCWaterBody.ShapeMode.RiverSpline &&
                (body.course == null || !body.course.IsValid)) return "no course";

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

            Event e = Event.current;
            
            // ---- Junction Connection Mode ----
            if (isConnectingJunction && body.course != null
                && selectedNodeIndex >= 0 && selectedNodeIndex < body.course.Count)
            {
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    isConnectingJunction = false;
                    e.Use();
                    return;
                }

                // Prevent default scene interactions (orbit, selection) while connecting
                if (e.type == EventType.Layout)
                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

                Vector3 startPoint = body.transform.TransformPoint(body.course.points[selectedNodeIndex]);
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Vector3 endPoint = ray.GetPoint(100f);
                if (Physics.Raycast(ray, out RaycastHit hit, 10000f)) endPoint = hit.point;
                else
                {
                    Plane plane = new Plane(Vector3.up, Vector3.zero);
                    if (plane.Raycast(ray, out float enter)) endPoint = ray.GetPoint(enter);
                }

                Handles.color = Color.green;
                Handles.DrawDottedLine(startPoint, endPoint, 4f);
                SceneView.currentDrawingSceneView.Repaint();

                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    GameObject picked = HandleUtility.PickGameObject(e.mousePosition, false);
                    if (picked != null)
                    {
                        UCWaterBody targetBody = picked.GetComponentInParent<UCWaterBody>();
                        if (targetBody != null && targetBody != body)
                        {
                            Undo.RecordObject(body, "Connect Water Body");
                            
                            // Snap position to closest node on target
                            if (targetBody.course != null && targetBody.course.Count > 0)
                            {
                                float closestDist = float.MaxValue;
                                Vector3 closestWorld = Vector3.zero;
                                foreach (Vector3 tp in targetBody.course.points)
                                {
                                    Vector3 tw = targetBody.transform.TransformPoint(tp);
                                    float dist = Vector3.Distance(tw, endPoint);
                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestWorld = tw;
                                    }
                                }
                                body.course.points[selectedNodeIndex] = body.transform.InverseTransformPoint(closestWorld);
                            }

                            if (selectedNodeIndex == body.course.Count - 1)
                                body.joinDownstreamTo = targetBody;
                            else if (selectedNodeIndex == 0)
                                body.joinUpstreamFrom = targetBody;

                            EditorUtility.SetDirty(body);
                            if (body.buildTrigger == UCWaterBody.BuildTrigger.Automatic) Rebuild(body);
                            isConnectingJunction = false;
                        }
                    }
                    e.Use();
                    return;
                }
            }

            Polyline line = null;
            Color tint = Color.white;

            if (body.shape == UCWaterBody.ShapeMode.RiverSpline)
            {
                line = body.course;
                tint = new Color(0.30f, 0.85f, 0.95f, 1f);
            }
            else if (body.shape == UCWaterBody.ShapeMode.Polygon)
            {
                line = body.outline;
                tint = new Color(0.35f, 0.75f, 1f, 1f);
            }

            if (line == null) return;



            // ---- Ctrl held: preview where the next appended node will go ----
            // Like Road Architect, holding Ctrl shows a dotted line from the last
            // node to the mouse cursor on the terrain.
            if (body.shape == UCWaterBody.ShapeMode.RiverSpline && e.control)
            {
                // Raycast the terrain under the mouse for the preview
                if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.Repaint)
                {
                    Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    Vector3 previewPoint;
                    bool previewValid = RaycastTerrain(ray, body, out previewPoint);

                    if (previewValid && Event.current.type == EventType.Repaint)
                    {
                        Handles.color = new Color(tint.r, tint.g, tint.b, 0.6f);

                        if (line.Count > 0)
                        {
                            Vector3 lastPoint = body.transform.TransformPoint(line.points[line.Count - 1]);
                            Handles.DrawDottedLine(lastPoint, previewPoint, 4f);
                        }

                        // Draw a disc at the preview point
                        float handleSize = HandleUtility.GetHandleSize(previewPoint) * 0.12f;
                        Handles.DrawSolidDisc(previewPoint, Vector3.up, handleSize);
                    }

                    if (previewValid)
                        SceneView.currentDrawingSceneView.Repaint();
                }

                // ---- Ctrl + Left Click: append a new node at the end ----
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    Vector3 hitPoint;
                    if (RaycastTerrain(ray, body, out hitPoint))
                    {
                        // Double-click prevention: ignore if too close to last node
                        Transform t = body.transform;
                        if (line.Count > 0)
                        {
                            Vector3 lastWorld = t.TransformPoint(line.points[line.Count - 1]);
                            if (Vector3.Distance(lastWorld, hitPoint) < 2f)
                            {
                                e.Use();
                                return;
                            }
                        }

                        Undo.RecordObject(body, "Append river node");
                        line.points.Add(t.InverseTransformPoint(hitPoint));

                        // Keep weights in sync
                        if (line.weights != null && line.weights.Count < line.points.Count)
                            line.weights.Add(line.weights.Count > 0 ? line.weights[line.weights.Count - 1] : 1f);

                        // Keep per-node depth/width in sync (0 = inherit)
                        SyncNodeArrays(body, line);

                        selectedNodeIndex = line.points.Count - 1;
                        EditorUtility.SetDirty(body);

                        // Always rebuild so the river mesh appears immediately
                        Rebuild(body);
                        e.Use();
                        return;
                    }
                }

                // Prevent default scene interactions (orbit, selection) while Ctrl is held
                if (e.type == EventType.Layout)
                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }

            // ---- Draw existing spline and handles ----
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
        /// Ensures nodeDepthMetres and nodeWidthMetres have the same count as the
        /// course points. New entries default to 0 (inherit from the global value).
        /// </summary>
        private static void SyncNodeArrays(UCWaterBody body, Polyline line)
        {
            if (body.nodeDepthMetres == null) body.nodeDepthMetres = new System.Collections.Generic.List<float>();
            if (body.nodeWidthMetres == null) body.nodeWidthMetres = new System.Collections.Generic.List<float>();
            while (body.nodeDepthMetres.Count < line.points.Count) body.nodeDepthMetres.Add(0f);
            while (body.nodeWidthMetres.Count < line.points.Count) body.nodeWidthMetres.Add(0f);
            while (body.nodeDepthMetres.Count > line.points.Count) body.nodeDepthMetres.RemoveAt(body.nodeDepthMetres.Count - 1);
            while (body.nodeWidthMetres.Count > line.points.Count) body.nodeWidthMetres.RemoveAt(body.nodeWidthMetres.Count - 1);
        }

        /// <summary>
        /// Draws the line the body is actually built from.
        ///
        /// For a river that is the CURVE, not the control polygon. Drawing straight
        /// segments between the nodes was why the river read as a separate object
        /// from its own spline: the screen showed chords, the carve dug the curve
        /// through them, and on a bend those are metres apart. Both now come out of
        /// UCWaterBody.PreviewCourseWorld, so there is one shape on screen and in
        /// the ground.
        ///
        /// A polygon outline stays straight, because a polygon edge IS straight -
        /// bending it would be inventing geometry the fill does not use.
        /// </summary>
        private static void DrawSegments(UCWaterBody body, Polyline line, Color tint)
        {
            if (Event.current.type != EventType.Repaint || line.Count < 2) return;

            Transform t = body.transform;
            Handles.color = tint;

            List<Vector3> curve = body.shape == UCWaterBody.ShapeMode.RiverSpline
                ? body.PreviewCourseWorld()
                : null;

            if (curve != null && curve.Count >= 2)
            {
                // One AA polyline rather than a DrawLine per span: the tessellation
                // is hundreds of points and this is a repaint that runs whenever the
                // mouse moves.
                Handles.DrawAAPolyLine(4f, curve.ToArray());

                // Control polygon behind it, thin and faint. Without it there is no
                // way to see which part of the curve a node is responsible for, and
                // dragging a node to fix a bend becomes guesswork.
                Handles.color = new Color(tint.r, tint.g, tint.b, 0.28f);
                int hull = line.closed ? line.Count : line.Count - 1;
                for (int i = 0; i < hull; i++)
                    Handles.DrawLine(t.TransformPoint(line.PointAt(i)),
                                     t.TransformPoint(line.PointAt(i + 1)));

                Handles.color = tint;
                DrawFlowArrow(curve[Mathf.Max(0, curve.Count / 2 - 1)],
                              curve[curve.Count / 2]);
                return;
            }

            int segments = line.closed ? line.Count : line.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3 a = t.TransformPoint(line.PointAt(i));
                Vector3 b = t.TransformPoint(line.PointAt(i + 1));
                Handles.DrawLine(a, b, 3f);
            }

            if (body.shape == UCWaterBody.ShapeMode.RiverSpline && line.Count >= 2)
            {
                int mid = line.Count / 2;
                DrawFlowArrow(t.TransformPoint(line.PointAt(Mathf.Max(0, mid - 1))),
                              t.TransformPoint(line.PointAt(mid)));
            }
        }

        // Which way the water runs is the one thing about a course that is invisible
        // from its geometry, and building a river backwards is a whole afternoon.
        private static void DrawFlowArrow(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            if (dir.sqrMagnitude <= 0.01f) return;

            Handles.ConeHandleCap(0, to, Quaternion.LookRotation(dir.normalized),
                                  HandleUtility.GetHandleSize(to) * 0.25f, EventType.Repaint);
        }

        // =====================================================================
        //  NODE MARKERS
        //
        //  Read against Road & City Master, the road tool vendored beside this one,
        //  which solves the same authoring problem for
        //  roads: a node there is a coloured cube post standing on the ground, and
        //  a node that carries a connection is drawn bigger and in a different
        //  colour so a junction is visible from across the map without selecting
        //  anything. That tool gets it for free because its nodes are real
        //  GameObjects with gizmos - it has no PositionHandle anywhere - and a
        //  water course is a list of points on one component, so the same read has
        //  to be built out of Handles here.
        //
        //  What was here before drew a full three-axis PositionHandle for EVERY
        //  point. On a forty-node river that is forty overlapping gizmos, the
        //  course invisible underneath them, and no way to click a node without
        //  dragging it - the first click of a select was already a move. So:
        //  every node is a post you can click, and only the selected one gets a
        //  handle to drag.
        //
        //  The posts stand on the WATER SURFACE (UCWaterBody.NodeSurfacePoint), not
        //  on the stored point's Y, which in every ground-following mode is a
        //  leftover from wherever the spline was dropped. Drawing at the stored Y
        //  left the whole course hanging in the air above its own river or buried
        //  under it. A dragged node re-reads the ground for the same reason: the
        //  spline never floats.
        // =====================================================================

        private static readonly Color NodePlain = new Color(0.42f, 0.72f, 1f);      // river blue
        private static readonly Color NodeEnd = new Color(1f, 0.55f, 0.15f);        // head/mouth
        private static readonly Color NodeJoined = new Color(0.35f, 1f, 0.45f);     // carries a junction
        private static readonly Color NodeSelected = new Color(1f, 0.92f, 0.25f);

        private void DrawPointHandles(UCWaterBody body, Polyline line)
        {
            Transform t = body.transform;
            bool isCourse = line == body.course;

            Color entry = Handles.color;

            for (int i = 0; i < line.Count; i++)
            {
                // The course knows where its water is; a polygon outline does not, so it
                // is drawn at the point as stored.
                Vector3 p = isCourse ? body.NodeSurfacePoint(i) : t.TransformPoint(line.points[i]);

                bool selected = i == selectedNodeIndex;
                bool end = isCourse && body.IsEndNode(i);
                UCWaterBody joined = end ? body.JunctionAt(i) : null;

                float hs = HandleUtility.GetHandleSize(p);
                float post = hs * (end ? 0.85f : 0.6f);
                float cap = hs * (selected ? 0.16f : end ? 0.14f : 0.09f);

                Vector3 top = p + Vector3.up * post;

                Handles.color = selected ? NodeSelected
                              : joined != null ? NodeJoined
                              : end ? NodeEnd
                              : NodePlain;

                Handles.DrawLine(p, top);

                if (joined != null) DrawJunctionLink(top, joined);

                if (selected)
                {
                    Handles.CubeHandleCap(0, top, Quaternion.identity, cap, EventType.Repaint);
                    DrawNodeLabel(body, i, top, cap, isCourse);
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
                    isConnectingJunction = false;
                    Repaint();
                }

                if (end) DrawNodeLabel(body, i, top, cap, isCourse);
            }

            Handles.color = entry;

            DrawSelectedNodeHandle(body, line, isCourse);
        }

        /// <summary>The drag handle, for the selected node only.</summary>
        private void DrawSelectedNodeHandle(UCWaterBody body, Polyline line, bool isCourse)
        {
            int i = selectedNodeIndex;
            if (i < 0 || i >= line.Count) return;

            Transform t = body.transform;
            Vector3 at = isCourse ? body.NodeSurfacePoint(i) : t.TransformPoint(line.points[i]);

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(at, t.rotation);
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(body, "Move water point");

            // NEVER FLOATING. A course node dragged across the ground takes the ground's
            // elevation, the way a road node snaps down to the terrain rather than
            // hanging over it at whatever height the mouse happened to be at. The one
            // exception is Flat Floor, where the drawn Y IS the river's datum and the
            // solve reads it - snapping there would erase what the mode is for.
            if (isCourse && body.bedSource != UCWaterBody.BedSource.FlatFloor)
                moved.y = UCWaterBody.SampleGround(Terrain.activeTerrains, moved, at.y);

            line.points[i] = t.InverseTransformPoint(moved);
            dragging = true;
        }

        /// <summary>Index, and the grade a road node would carry.</summary>
        private static void DrawNodeLabel(UCWaterBody body, int i, Vector3 top, float cap,
                                         bool isCourse)
        {
            string text = "[" + i + "]";

            if (isCourse && body.course != null && body.course.Count >= 2)
            {
                int last = body.course.Count - 1;
                float g = i < last ? body.NodeGradePercentToNext(i)
                                   : body.NodeGradePercentFromPrev(i);

                text += "  " + g.ToString("0.##") + "%";
            }

            Handles.Label(top + Vector3.up * (cap * 2.2f), text);
        }

        /// <summary>
        /// The line from a joined end to the body it empties into or comes out of.
        /// </summary>
        /// <remarks>
        /// A junction is the one relationship in a water network that has no geometry of
        /// its own: the river reaches the lake and the connection exists in a serialised
        /// field, invisible. Which meant the only way to tell an authored junction from a
        /// river that merely stops near a lake was to select it and read the inspector -
        /// and an auto-connected one could not be told apart at all.
        /// </remarks>
        private static void DrawJunctionLink(Vector3 from, UCWaterBody joined)
        {
            Vector3 to = joined.transform.position;

            Handles.DrawDottedLine(from, to, 4f);
            Handles.SphereHandleCap(0, to, Quaternion.identity,
                                    HandleUtility.GetHandleSize(to) * 0.12f, EventType.Repaint);
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

            Undo.RecordObject(body, "Insert river node");

            int insertAt = bestSegment + 1;
            line.points.Insert(insertAt, t.InverseTransformPoint(bestPoint));
            if (line.weights != null && line.weights.Count >= insertAt)
                line.weights.Insert(insertAt, line.WeightAt(bestSegment));

            // Insert matching entries in the per-node arrays
            if (body.nodeDepthMetres != null && body.nodeDepthMetres.Count >= insertAt)
                body.nodeDepthMetres.Insert(insertAt, 0f);
            if (body.nodeWidthMetres != null && body.nodeWidthMetres.Count >= insertAt)
                body.nodeWidthMetres.Insert(insertAt, 0f);

            SyncNodeArrays(body, line);
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

            int best = -1;
            float bestDistance = PickPixels * 2f;

            for (int i = 0; i < line.Count; i++)
            {
                Vector2 gui = HandleUtility.WorldToGUIPoint(t.TransformPoint(line.points[i]));
                float distance = Vector2.Distance(gui, e.mousePosition);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = i;
            }

            if (best < 0) return;

            Undo.RecordObject(body, "Remove water point");

            line.points.RemoveAt(best);
            if (line.weights != null && line.weights.Count == line.points.Count + 1)
                line.weights.RemoveAt(best);
                
            if (body.nodeDepthMetres != null && body.nodeDepthMetres.Count == line.points.Count + 1)
                body.nodeDepthMetres.RemoveAt(best);
            if (body.nodeWidthMetres != null && body.nodeWidthMetres.Count == line.points.Count + 1)
                body.nodeWidthMetres.RemoveAt(best);

            SyncNodeArrays(body, line);
                
            if (selectedNodeIndex == best) selectedNodeIndex = -1;
            else if (selectedNodeIndex > best) selectedNodeIndex--;

            EditorUtility.SetDirty(body);
            Rebuild(body);

            e.Use();
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
