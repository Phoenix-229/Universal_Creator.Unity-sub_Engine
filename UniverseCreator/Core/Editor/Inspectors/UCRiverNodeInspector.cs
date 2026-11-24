// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    UCRiverNodeInspector.cs
//  Purpose: The per-node inspector for the rebuilt river - one UCRiverNode child
//           GameObject per course control point, each editable on its own. Shows
//           the node's hydraulic payload (half-width, depth, discharge, velocity),
//           gives it a scene move handle, and lets you delete it. Any edit (a
//           field, a move, a delete) re-gathers the parent body's course from its
//           child nodes and rebuilds, so what you author is what builds.
//
//  SINGLE SOURCE OF TRUTH
//  ----------------------
//  The nodes are the authoring front-end; UCWaterBody.GatherCourseFromNodes bakes
//  them into the body's outline + riverProfile, which is the one thing the solver,
//  carve and mesh read. This inspector never writes outline/riverProfile directly
//  - it edits the node and asks the body to re-gather, so the two can never drift.
//
//  Everything goes through Undo, and a build failure is logged, never thrown, so
//  a malformed course cannot take the inspector down on every repaint.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities; // UC-T04-05B: the placement owner also coalesces edit/Undo rebuilds.

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(UCRiverNode))]
    [CanEditMultipleObjects]
    public class UCRiverNodeInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UCRiverNode node = target as UCRiverNode;
            UCWaterBody body = node != null ? node.GetComponentInParent<UCWaterBody>(true) : null; // UC-T04-01: inactive authored parents retain mode authority.
            UCRiverPlacementTool.Watch(body);

            if (body == null)
            {
                EditorGUILayout.HelpBox(
                    "This river node is not under a water body. Move it beneath the " +
                    "UCWaterBody GameObject so the course can gather it.",
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "A control point of the river course. Its position is this GameObject's " +
                "transform. 0 in any channel means \"inherit the previous node\"; a channel " +
                "left 0 on every node is derived by the solver. Changing a value carries " +
                "forward through later unset points. Leading unset points use the first authored value.", MessageType.None);

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            // UC-T04-05B: saved manual dimensions cannot override accepted procedural geometry.
            bool readsTrench = false;
            foreach (Object item in targets)
            {
                var owner = (item as UCRiverNode)?.GetComponentInParent<UCWaterBody>(true);
                readsTrench |= owner != null && owner.TerrainMode == RiverTerrainMode.ReadProceduralTrench;
            }
            using (new EditorGUI.DisabledScope(readsTrench))
            {
                HydraulicField("halfWidthM", "Width (m)", 2f, "Full channel width. 0 inherits; the first new source defaults to 0.3 m.");
                HydraulicField("depthM", "Depth (m)", 1f, "Channel depth. 0 inherits; the first new source defaults to 0.2 m.");
                HydraulicField("dischargeM3PerS", "Discharge (m³/s)", 1f, "0 inherits, or derives discharge when all points are unset.");
            }
            if (readsTrench) EditorGUILayout.HelpBox("Read mode uses accepted trench dimensions; these saved values apply in manual mode.", MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
                HydraulicField("velocityMps", "Saved velocity (m/s)", 1f, "Saved point velocity. Surface flow currently uses the river body's Flow Speed.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("River Terrain", EditorStyles.boldLabel);
            bool modeChanged = !serializedObject.isEditingMultipleObjects && UCRiverTerrainModeGUI.Draw(body); // UC-T04-01: edit the owner, never a child flag.

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Flood Plain", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("floodPlainStart"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("floodPlainEnd"));

            // ---- Junction: offered only when this end node is touching another river ----
            if (body != null && !serializedObject.isEditingMultipleObjects)
                DrawJunction(node, body);

            bool changed = EditorGUI.EndChangeCheck() || modeChanged;
            serializedObject.ApplyModifiedProperties();

            if (changed)
            {
                var owners = new System.Collections.Generic.HashSet<UCWaterBody>(); // UC-T04-05B: multi-selection edits every affected river once.
                foreach (Object item in targets)
                {
                    var owner = (item as UCRiverNode)?.GetComponentInParent<UCWaterBody>(true);
                    if (owner != null && owners.Add(owner)) RebuildBody(owner);
                }
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Delete This Node"))
            {
                // Removing the child IS how you remove a node (the author's model).
                // Record on the body so the undo restores the course too.
                UCWaterBody owner = body;
                Undo.DestroyObjectImmediate(node.gameObject);
                RebuildBody(owner);
                GUIUtility.ExitGUI();   // the target is gone; stop drawing this inspector
            }
        }

        private void HydraulicField(string propertyName, string label, float scale, string tooltip)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            bool mixed = EditorGUI.showMixedValue; EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float value = EditorGUILayout.FloatField(new GUIContent(label, tooltip), property.floatValue * scale);
            if (EditorGUI.EndChangeCheck() && !float.IsNaN(value) && !float.IsInfinity(value))
                property.floatValue = Mathf.Max(0, value) / scale;
            EditorGUI.showMixedValue = mixed;
        }

        // How close (metres, plan) this node must be to another river's channel for the junction
        // tick to appear: the channel's own width plus a little, so "just touching" counts.
        private const float TouchSlackMetres = 4f;

        private void DrawJunction(UCRiverNode node, UCWaterBody body)
        {
            UCRiverNode[] all = body.OwnedRiverNodes(); // UC-T04-05B: endpoints belong to this river only.
            if (all.Length < 2) return;
            bool isLast = all[all.Length - 1] == node;
            bool isFirst = all[0] == node;
            if (!isLast && !isFirst) return;

            UCWaterBody other = FindTouchingRiver(node, body);
            if (other == null && !node.connectJunction) return;
            if (other == null) other = node.junctionTarget;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Junction", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                // UC-T04-06: a candidate marker is not proof of accepted shared geometry.
                "Candidate connection to '" + (other != null ? other.name : "another river") + "'. Tick to select this " +
                "endpoint target. The parent validates bed clearance and shared surface geometry during rebuild; " +
                "the Junction child marks the authored connection. Nodes retain their positions.", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            bool tick = EditorGUILayout.Toggle("Connect Junction", node.connectJunction);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(node, "Connect river junction");
                node.connectJunction = tick;
                node.junctionTarget = tick ? other : null;
                EnsureJunctionChild(node, tick, other);

                if (isLast)
                {
                    Undo.RecordObject(body, "Connect river junction");
                    body.joinsRiver = tick ? other : null;
                }
                EditorUtility.SetDirty(node);
                EditorUtility.SetDirty(body);
            }
        }

        private static UCWaterBody FindTouchingRiver(UCRiverNode node, UCWaterBody self)
        {
            Vector3 p = node.transform.position;
            UCWaterBody[] bodies = Object.FindObjectsByType<UCWaterBody>();
            UCWaterBody best = null;
            float bestMargin = float.MaxValue;          // distance beyond the touch limit (negative = touching)

            for (int b = 0; b < bodies.Length; b++)
            {
                UCWaterBody o = bodies[b];
                if (o == null || o == self || o.shape != UCWaterBody.ShapeMode.RiverSpline) continue;
                if (o.CarveWorld != self.CarveWorld || o.gameObject.scene != self.gameObject.scene) continue; // UC-T04-05B: agree with junction scope.
                Polyline c = o.GetSmoothCourseLocal(4f);
                if (c == null || c.Count < 2) continue;

                float halfW = 0.5f;
                UCRiverNode[] on = o.OwnedRiverNodes();
                for (int i = 0; i < on.Length; i++) if (on[i].halfWidthM > halfW) halfW = on[i].halfWidthM;
                float limit = halfW + TouchSlackMetres;

                float nearest = float.MaxValue;
                for (int i = 0; i + 1 < c.Count; i++)
                {
                    Vector3 a = o.transform.TransformPoint(c.points[i]);
                    Vector3 d = o.transform.TransformPoint(c.points[i + 1]);
                    Vector2 ab = new Vector2(d.x - a.x, d.z - a.z);
                    Vector2 ap = new Vector2(p.x - a.x, p.z - a.z);
                    float len2 = ab.sqrMagnitude;
                    float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / len2) : 0f;
                    float dist = (ap - ab * t).magnitude;
                    if (dist < nearest) nearest = dist;
                }

                float margin = nearest - limit;
                if (margin <= 0f && margin < bestMargin) { bestMargin = margin; best = o; }
            }
            return best;
        }

        private static void EnsureJunctionChild(UCRiverNode node, bool want, UCWaterBody target)
        {
            Transform existing = node.transform.Find("Junction");
            if (want && existing == null)
            {
                GameObject go = new GameObject("Junction");
                Undo.RegisterCreatedObjectUndo(go, "Add junction");
                go.transform.SetParent(node.transform, false);
                UCRiverJunction j = Undo.AddComponent<UCRiverJunction>(go);
                j.target = target;
            }
            else if (want && existing != null)
            {
                UCRiverJunction j = existing.GetComponent<UCRiverJunction>();
                if (j != null) j.target = target;
            }
            else if (!want && existing != null)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
            }
        }

        private void OnSceneGUI()
        {
            UCRiverNode node = target as UCRiverNode;
            if (node == null) return;

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(node.transform.position, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(node.transform, "Move river node");
                node.transform.position = moved;
                RebuildBody(node.GetComponentInParent<UCWaterBody>(true));
            }
        }

        // UC-T04-05B: rebuild after the field/drag completes, through the sole editor edit owner.
        private static void RebuildBody(UCWaterBody body)
        {
            UCRiverPlacementTool.RequestRebuild(body);
        }
    }
}
