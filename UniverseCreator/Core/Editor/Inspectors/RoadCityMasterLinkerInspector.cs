// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RoadCityMasterLinkerInspector.cs
//  Purpose: Draws the road bridge, separating the half that already answers
//           queries from the half that is only declared.
//
//  The provider side is live: height, normal, chunk bounds and tunnel carving all
//  resolve against the real grid today, so this inspector proves it by sampling
//  the terrain under the scene view pivot and showing the answer. If that readout
//  says "no chunk manager", the bridge is genuinely not wired and the road module
//  will get zeros - which is worth finding here rather than in Module 6.
//
//  The road behaviour fields are declared only and are drawn disabled.
//
//  NO DIALOGS.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Integration;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(RoadCityMasterLinker))]
    public class RoadCityMasterLinkerInspector : UnityEditor.Editor
    {
        private static bool linkFoldout = true;
        private static bool probeFoldout = true;
        private static bool plannedFoldout;

        private RoadCityMasterLinker Linker { get { return (RoadCityMasterLinker)target; } }

        public override void OnInspectorGUI()
        {
            RoadCityMasterLinker linker = Linker;
            if (linker == null) return;

            UCInspectorUI.HeaderBar("Road Bridge", "d_NavMeshData Icon",
                                    UCInspectorUI.HeaderAmber, StatusText(linker));

            EditorGUILayout.Space(4);

            serializedObject.Update();

            linkFoldout = Group(linkFoldout, "Link  (live)", UCInspectorUI.HeaderGreen);
            if (linkFoldout) DrawLink(linker);

            probeFoldout = Group(probeFoldout, "Probe  (live)", UCInspectorUI.HeaderGreen);
            if (probeFoldout) DrawProbe(linker);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(4);

            plannedFoldout = Group(plannedFoldout, "Road Behaviour  (not implemented)",
                                   UCInspectorUI.HeaderGrey);
            if (plannedFoldout) DrawPlanned();
        }

        private void DrawLink(RoadCityMasterLinker linker)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("universeCreator"));

            UniverseCreatorObject world = linker.universeCreator != null
                ? linker.universeCreator
                : linker.GetComponentInParent<UniverseCreatorObject>();

            if (world == null)
            {
                EditorGUILayout.HelpBox(
                    "No world linked and none found above this object in the hierarchy. " +
                    "Every query will return zero. Drag the world root into the field " +
                    "above, or move this object under it.", MessageType.Error);
                return;
            }

            if (linker.universeCreator == null)
                EditorGUILayout.HelpBox(
                    "Resolved '" + world.name + "' from the hierarchy. It works, but " +
                    "assigning it explicitly above survives being reparented.",
                    MessageType.Info);

            if (world.Chunks == null)
                EditorGUILayout.HelpBox(
                    "The linked world has no chunk manager, so there is no heightfield " +
                    "to query yet. Generate the world first.", MessageType.Warning);
        }

        /// <summary>
        /// Samples the live provider at the scene view pivot. This is the whole
        /// contract in four numbers - if these are right, Module 6 gets right
        /// answers; if they are zeros, it will not.
        /// </summary>
        private void DrawProbe(RoadCityMasterLinker linker)
        {
            Vector3 pivot = SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.pivot
                : Vector3.zero;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                "Sampled at the scene view pivot, through the same calls the road " +
                "module makes.", EditorStyles.wordWrappedMiniLabel);

            Readout("Sample point",
                    pivot.x.ToString("0.0") + ", " + pivot.z.ToString("0.0"));
            Readout("Height", linker.SampleHeight(pivot.x, pivot.z).ToString("0.00") + " m");

            Vector3 n = linker.SampleNormal(pivot.x, pivot.z);
            Readout("Normal", n.x.ToString("0.00") + ", " + n.y.ToString("0.00") + ", " +
                              n.z.ToString("0.00"));
            Readout("Slope", Vector3.Angle(n, Vector3.up).ToString("0.0") + " deg");
            Readout("Loaded chunks", linker.GetActiveChunkBounds().Count.ToString());

            EditorGUILayout.EndVertical();

            // The pivot moves whenever the user navigates the scene, and IMGUI only
            // repaints an inspector on interaction, so without this the numbers go
            // stale the moment the camera moves.
            Repaint();
        }

        private void DrawPlanned()
        {
            EditorGUILayout.HelpBox(
                "The provider half above is live. What is not written is the consumer " +
                "half - how a road decides where to go and what it does to the ground " +
                "it crosses. The fields below name it, with the units and thresholds " +
                "the terrain side already implies. Nothing reads them yet.",
                MessageType.Warning);

            using (new EditorGUI.DisabledScope(true))
            {
                DrawPropertiesExcluding(serializedObject, "m_Script", "universeCreator");
            }
        }

        private static void Readout(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(120));
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static string StatusText(RoadCityMasterLinker linker)
        {
            UniverseCreatorObject world = linker.universeCreator != null
                ? linker.universeCreator
                : linker.GetComponentInParent<UniverseCreatorObject>();

            if (world == null) return "not linked";
            return world.Chunks != null ? "live" : "no chunks";
        }

        private static bool Group(bool state, string title, Color accent)
        {
            bool before = GUI.changed;
            bool next = UCInspectorUI.Section(state, title, accent);
            GUI.changed = before;
            return next;
        }
    }
}
