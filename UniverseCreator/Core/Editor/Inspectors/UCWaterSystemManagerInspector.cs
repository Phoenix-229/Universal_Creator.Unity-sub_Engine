// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWaterSystemManagerInspector.cs
//  Purpose: The authoring surface for a world's water. Creates bodies, edits the
//           graph's water settings, and acts on every body at once.
//
//  THE CREATE BUTTONS
//  ------------------
//  Ocean, Lake, River and Pool are the same four presets as the GameObject menu,
//  and they call the same code - UniverseCreatorMenus.SpawnWater. They are here
//  because the menu parents a new body under "the selected world's WaterSystem",
//  and when this inspector is open the selection IS a WaterSystem, so the menu's
//  own lookup would have to walk back up to find what the user is already looking
//  at. Passing the transform directly is both shorter and unambiguous.
//
//  BATCHED ACTIONS DO NOT BLOCK
//  ----------------------------
//  Rebuild All over a world of rivers goes through UCBatchRunner: one body per
//  editor tick, with a cancellable entry in the bottom-right task area. A straight
//  loop would freeze the editor for as long as it took and give no way out.
//
//  NO DIALOGS. Nothing here opens a modal window; anything worth saying is a
//  HelpBox or a console line.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(UCWaterSystemManager))]
    public class UCWaterSystemManagerInspector : UnityEditor.Editor
    {
        private static bool createFoldout = true;
        private static bool sourceFoldout = true;
        private static bool seaLevelFoldout = true;
        private static bool defaultsFoldout;
        private static bool dynamicsFoldout;
        private static bool sharedFoldout;
        private static bool bodyListFoldout;

        private Vector2 bodyScroll;

        private UCWaterSystemManager Manager { get { return (UCWaterSystemManager)target; } }

        // Batch keys. Distinct per manager instance so two worlds open in one scene
        // do not cancel each other's rebuild.
        private string BatchKey(string op)
        {
            return "UCWater." + op + "." + Manager.GetEntityId().GetHashCode();
        }

        // =====================================================================
        //  INSPECTOR
        // =====================================================================

        public override void OnInspectorGUI()
        {
            UCWaterSystemManager manager = Manager;
            if (manager == null) return;

            UCInspectorUI.HeaderBar("Water System", "d_Particle Effect",
                                    UCInspectorUI.HeaderBlue, StatusText(manager));

            EditorGUILayout.Space(4);

            createFoldout = Group(createFoldout, "Create Water", UCInspectorUI.HeaderGreen);
            if (createFoldout) DrawCreate(manager);

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            sourceFoldout = Group(sourceFoldout, "Source", UCInspectorUI.HeaderTeal);
            if (sourceFoldout) DrawSource(manager);

            seaLevelFoldout = Group(seaLevelFoldout, "Sea Level", UCInspectorUI.HeaderPurple);
            if (seaLevelFoldout) DrawSeaLevel(manager);

            defaultsFoldout = Group(defaultsFoldout, "Defaults For New Bodies",
                                    UCInspectorUI.HeaderGrey);
            if (defaultsFoldout) DrawDefaults();

            dynamicsFoldout = Group(dynamicsFoldout, "Interactive Dynamics",
                                    UCInspectorUI.HeaderTeal);
            if (dynamicsFoldout) DrawInteractiveDynamics();

            sharedFoldout = Group(sharedFoldout, "Shared Water Description",
                                  UCInspectorUI.HeaderTeal);
            if (sharedFoldout) DrawShared();

            EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            DrawActions(manager);

            EditorGUILayout.Space(4);
            bodyListFoldout = Group(bodyListFoldout, "Bodies", UCInspectorUI.HeaderGrey);
            if (bodyListFoldout) DrawBodyList(manager);

            EditorGUILayout.Space(4);
            DrawReadout(manager);
        }

        // =====================================================================
        //  CREATE
        // =====================================================================

        private void DrawCreate(UCWaterSystemManager manager)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                "Adds a body under this object, preset and built once. The same four " +
                "presets as GameObject > 3D Object > Universe Creator > Water.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            if (ActionButton(new GUIContent("Ocean", "A rectangular sea wide enough to " +
                    "reach past the last chunk, with long swell and shoreline foam."),
                    UCInspectorUI.ButtonBlue, 26))
                Create(manager, UniverseCreatorMenus.WaterPreset.Ocean);

            if (ActionButton(new GUIContent("Lake", "A closed polygon shoreline, seeded " +
                    "with twelve points and snapped to the ground under it."),
                    UCInspectorUI.ButtonBlue, 26))
                Create(manager, UniverseCreatorMenus.WaterPreset.Lake);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (ActionButton(new GUIContent("Pool", "A small flat-floored basin. Reads " +
                    "its own floor depth rather than the terrain, so it works indoors."),
                    UCInspectorUI.ButtonBlue, 26))
                Create(manager, UniverseCreatorMenus.WaterPreset.Pool);
            EditorGUILayout.EndHorizontal();

            if (!manager.HandPlacedEnabled)
                EditorGUILayout.HelpBox(
                    "Source is set to " + manager.source + ", so hand-placed bodies are " +
                    "switched off. A body created now is kept and configured but stays " +
                    "disabled until Source includes Hand Placed.",
                    MessageType.Warning);

            EditorGUILayout.EndVertical();
        }

        private void Create(UCWaterSystemManager manager, UniverseCreatorMenus.WaterPreset preset)
        {
            UCWaterBody body = UniverseCreatorMenus.SpawnWater(preset, manager.transform);

            // The new body is selected by SpawnWater, which swaps the inspector to it.
            // That is what the user wants - a river is useless until its course is
            // dragged - but it means this editor is about to be torn down, so nothing
            // may be drawn after here this frame.
            if (body != null) GUIUtility.ExitGUI();
        }

        // =====================================================================
        //  SECTIONS
        // =====================================================================

        private void DrawSource(UCWaterSystemManager manager)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("source"));

            SerializedProperty layer = serializedObject.FindProperty("waterLayer");
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUILayout.LayerField(
                new GUIContent("Water Layer", "Layer every generated water surface is " +
                    "put on, so a camera can exclude it and character rays can pass " +
                    "through it."),
                layer.intValue);
            if (EditorGUI.EndChangeCheck()) layer.intValue = picked;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("driveWorldWaterLayer"));

            switch (manager.source)
            {
                case UCWaterSystemManager.WaterSource.Disabled:
                    EditorGUILayout.HelpBox(
                        "No water is produced by either path. Existing bodies keep their " +
                        "settings and their spline points; nothing is deleted.",
                        MessageType.Info);
                    break;

                case UCWaterSystemManager.WaterSource.NodeGraph:
                    EditorGUILayout.HelpBox(
                        "Only water the Hydrology Spawner made is kept. Hand-placed bodies " +
                        "are disabled, not deleted - switch back to Both to bring them home.",
                        MessageType.Info);
                    break;

                case UCWaterSystemManager.WaterSource.HandPlaced:
                    EditorGUILayout.HelpBox(
                        "Only the bodies you placed under this object are kept. A Hydrology " +
                        "Spawner in the graph is ignored, and water it made earlier is " +
                        "switched off.",
                        MessageType.Info);
                    break;
            }

            EditorGUI.indentLevel--;
        }

        // The one number that is not a per-body setting. Every water body owns its own
        // depth, mesh and flow, so there is nothing here to push into a node for them -
        // but the shoreline elevation is shared with the terrain's own texturing, and
        // that has to be told, because the nodes that paint the beach run on a worker
        // thread before any water exists and cannot ask the sea where it settled.
        private void DrawSeaLevel(UCWaterSystemManager manager)
        {
            EditorGUI.indentLevel++;

            Graph graph = GraphOf(manager);
            int readers = UCWaterSystemManager.CountSeaLevelReaders(graph);

            if (graph == null)
            {
                EditorGUILayout.HelpBox(
                    "This world has no graph, so there is nothing to push this into. It " +
                    "is still saved on this component and will apply once a graph is " +
                    "assigned.",
                    MessageType.Info);
            }
            else if (readers == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing in '" + graph.name + "' measures anything from sea level, so " +
                    "this changes only the water itself. Add an Ocean Level node to mask " +
                    "the sea and its beach band, or a Biome Splat Output to paint sand at " +
                    "the waterline.",
                    MessageType.Info);
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("nodeSeaLevel"));
            SeaLevelReadout(manager);

            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(readers == 0))
            {
                if (ActionButton(new GUIContent("Push To Graph",
                        "Writes sea level into the " + readers + " node(s) that read it, " +
                        "clears their cached results, then marks the graph asset dirty."),
                        UCInspectorUI.ButtonGreen))
                    PushToGraph(manager, graph);
            }

            // No Read From Graph. Sea level has exactly one writer on purpose - Snap To
            // Ocean below derives it from the sea's own settled surface - and a button
            // that copied a node's value back here would give the shoreline two authors
            // that can disagree.
            EditorGUILayout.LabelField(
                readers == 0
                    ? "No node in the graph reads sea level."
                    : readers + " node(s) in the graph read sea level.",
                EditorStyles.miniLabel);

            EditorGUI.indentLevel--;
        }

        /// <summary>Sea level as a fraction is unreadable on its own; this says what
        /// it is in metres on the world it is actually attached to.</summary>
        private static void SeaLevelReadout(UCWaterSystemManager manager)
        {
            UniverseCreatorObject world = manager.World;
            if (world == null) return;

            float metres = manager.nodeSeaLevel * world.maxHeightMeters;
            EditorGUILayout.LabelField(" ",
                metres.ToString("0.0") + " m of " + world.maxHeightMeters.ToString("0") + " m",
                EditorStyles.miniLabel);
        }

        private void DrawDefaults()
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("newBodyBedSource"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("newBodyBuildTrigger"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultFlowForce"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultViscosityScale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultEmitRipples"));

            EditorGUILayout.LabelField(
                "These apply to bodies created from here. Existing bodies keep what they " +
                "have - use Apply Physics To All below to change them too.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUI.indentLevel--;
        }

        private void DrawInteractiveDynamics()
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("spawnRippleSim"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("underwaterOnMainCamera"));

            EditorGUILayout.LabelField(
                "On Play the manager creates the scene-wide ripple simulation (so a " +
                "body's Emit Ripples is visible) and, if asked, adds the underwater " +
                "view to the main camera. Buoyancy and the swimming character attach " +
                "to your own floating/swimming objects.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUI.indentLevel--;
        }

        private void DrawShared()
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("sharedWater"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sharedMaterialOverride"));

            EditorGUI.indentLevel--;
        }

        // =====================================================================
        //  ACTIONS
        // =====================================================================

        private void DrawActions(UCWaterSystemManager manager)
        {
            int bodies = manager.Bodies.Length;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(bodies == 0))
            {
                EditorGUILayout.BeginHorizontal();

                if (ActionButton(new GUIContent("Rebuild All",
                        "Rebuilds every body, one per editor tick. Cancellable from the " +
                        "task area in the bottom right."),
                        UCInspectorUI.ButtonGreen, 24))
                    RebuildAll(manager);

                if (ActionButton(new GUIContent("Snap All To Ground",
                        "Re-reads the terrain height under every spline point of every " +
                        "body, then rebuilds it."),
                        UCInspectorUI.ButtonAmber, 24))
                    SnapAll(manager);

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (ActionButton(new GUIContent("Apply Shared Description",
                        "Copies the shared water description onto every body and rebuilds " +
                        "them. Each body gets its own copy, so tuning one afterwards does " +
                        "not change the rest."),
                        UCInspectorUI.ButtonBlue, 24))
                    ApplyShared(manager);

                if (ActionButton(new GUIContent("Apply Physics To All",
                        "Pushes flow force, viscosity and ripples from Defaults onto " +
                        "every existing body."),
                        UCInspectorUI.ButtonBlue, 24))
                    ApplyPhysics(manager);

                EditorGUILayout.EndHorizontal();

                if (ActionButton(new GUIContent("Clear All Surfaces",
                        "Drops every built mesh. The bodies, their settings and their " +
                        "spline points are kept - press Rebuild All to bring them back."),
                        UCInspectorUI.ButtonRed, 24))
                    ClearAll(manager);
            }

            if (bodies == 0)
                EditorGUILayout.LabelField("No hand-placed bodies under this object yet.",
                                           EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        private void RebuildAll(UCWaterSystemManager manager)
        {
            UCWaterBody[] bodies = manager.Bodies;

            UCBatchRunner.Run(BatchKey("Rebuild"), "Rebuilding water", bodies.Length,
                i => UCWaterSystemManager.RebuildBody(bodies[i]),
                () => Repaint());
        }

        private void SnapAll(UCWaterSystemManager manager)
        {
            UCWaterBody[] bodies = manager.Bodies;

            Undo.RecordObjects(bodies, "Snap water to ground");

            UCBatchRunner.Run(BatchKey("Snap"), "Snapping water to ground", bodies.Length,
                i =>
                {
                    UCWaterBody b = bodies[i];
                    if (b == null) return;

                    b.SnapCourseToGround();
                    EditorUtility.SetDirty(b);
                    UCWaterSystemManager.RebuildBody(b);
                },
                () => Repaint());
        }

        private void ApplyShared(UCWaterSystemManager manager)
        {
            UCWaterBody[] bodies = manager.Bodies;

            Undo.RecordObjects(bodies, "Apply shared water description");
            manager.ApplySharedDescriptionToAll();

            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null) EditorUtility.SetDirty(bodies[i]);

            // The description only reaches the material through a rebuild, so the
            // button that sets it is also the button that shows it.
            UCBatchRunner.Run(BatchKey("Shared"), "Applying water description", bodies.Length,
                i => UCWaterSystemManager.RebuildBody(bodies[i]),
                () => Repaint());
        }

        private void ApplyPhysics(UCWaterSystemManager manager)
        {
            UCWaterBody[] bodies = manager.Bodies;

            Undo.RecordObjects(bodies, "Apply water physics defaults");
            manager.ApplyPhysicsDefaultsToAll();

            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null) EditorUtility.SetDirty(bodies[i]);

            UCBatchRunner.Run(BatchKey("Physics"), "Applying water physics", bodies.Length,
                i => UCWaterSystemManager.RebuildBody(bodies[i]),
                () => Repaint());
        }

        private void ClearAll(UCWaterSystemManager manager)
        {
            // Cancels anything still stepping, so a rebuild in flight does not put
            // surfaces back moments after they were cleared.
            UCBatchRunner.Cancel(BatchKey("Rebuild"));
            UCBatchRunner.Cancel(BatchKey("Shared"));
            UCBatchRunner.Cancel(BatchKey("Physics"));
            UCBatchRunner.Cancel(BatchKey("Snap"));

            manager.ClearAll();
            Repaint();
        }

        private void PushToGraph(UCWaterSystemManager manager, Graph graph)
        {
            if (graph == null) return;

            Undo.RecordObject(graph, "Push sea level to graph");
            int touched = manager.ApplyToGraph(graph);
            EditorUtility.SetDirty(graph);

            Debug.Log("Universe Creator: sea level written to " + touched +
                      " node(s) in '" + graph.name + "'.", graph);
        }

        // =====================================================================
        //  BODY LIST AND READOUT
        // =====================================================================

        private void DrawBodyList(UCWaterSystemManager manager)
        {
            UCWaterBody[] bodies = manager.Bodies;

            if (bodies.Length == 0)
            {
                EditorGUILayout.LabelField("Nothing here yet. Use the Create buttons above.",
                                           EditorStyles.miniLabel);
                return;
            }

            // Scrolls past about a dozen rows rather than pushing the readout off
            // the bottom of a world with forty rivers in it.
            float height = Mathf.Min(bodies.Length, 12) * 20f + 4f;
            bodyScroll = EditorGUILayout.BeginScrollView(bodyScroll, GUILayout.Height(height));

            for (int i = 0; i < bodies.Length; i++)
            {
                UCWaterBody b = bodies[i];
                if (b == null) continue;

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(b.name, EditorStyles.miniButton, GUILayout.Width(130)))
                {
                    Selection.activeGameObject = b.gameObject;
                    EditorGUIUtility.PingObject(b.gameObject);
                }

                EditorGUILayout.LabelField(b.kind.ToString(), EditorStyles.miniLabel,
                                           GUILayout.Width(52));

                EditorGUILayout.LabelField(
                    b.LastBuild == null ? "not built" : b.BuiltTriangleCount + " tris",
                    EditorStyles.miniLabel, GUILayout.Width(70));

                if (!b.enabled)
                    UCInspectorUI.Badge("off", UCInspectorUI.HeaderAmber, 28f);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawReadout(UCWaterSystemManager manager)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Totals", EditorStyles.boldLabel);

            Readout("Bodies", manager.Bodies.Length.ToString());
            Readout("Built", manager.BuiltBodyCount.ToString());
            Readout("Vertices", manager.TotalVertexCount.ToString());
            Readout("Triangles", manager.TotalTriangleCount.ToString());
            Readout("Connected spawners",
                    UCWaterSystemManager.CountConnectedHydrology(GraphOf(manager)).ToString());

            EditorGUILayout.EndVertical();
        }

        private static void Readout(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(140));
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static string StatusText(UCWaterSystemManager manager)
        {
            if (manager.source == UCWaterSystemManager.WaterSource.Disabled) return "disabled";

            int bodies = manager.Bodies.Length;
            if (bodies == 0 && !manager.NodeWaterEnabled) return "empty";

            return bodies + " bodies";
        }

        private static Graph GraphOf(UCWaterSystemManager manager)
        {
            UniverseCreatorObject world = manager.World;
            return world != null ? world.graph : null;
        }

        // =====================================================================
        //  IMGUI HELPERS
        //
        //  Foldouts and buttons both set GUI.changed, which would otherwise make
        //  every collapse and every click read as a field edit.
        // =====================================================================

        private static bool Group(bool state, string title, Color accent)
        {
            bool before = GUI.changed;
            bool next = UCInspectorUI.Section(state, title, accent);
            GUI.changed = before;
            return next;
        }

        private static bool ActionButton(GUIContent content, Color tint, float height = 22f)
        {
            bool before = GUI.changed;
            bool clicked = UCInspectorUI.TintedButton(content, tint, height);
            if (clicked) GUI.changed = before;
            return clicked;
        }
    }
}
