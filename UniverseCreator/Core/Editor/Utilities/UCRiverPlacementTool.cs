// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCRiverPlacementTool.cs
//  Purpose: Hand-placing a river in the Scene view the way Road Architect places a road:
//             CTRL + Click   adds a node at the end of the river, on the ground under the cursor
//             SHIFT + Click  inserts a node into the nearest part of the river
//           Nothing is placed automatically - only on a click with the modifier held. A ghost line
//           and marker show where the next node will land while CTRL is held.
//
//  WHY THIS IS A SEPARATE, ALWAYS-ON TOOL (the "I click and it does not place" bug)
//  -------------------------------------------------------------------------------
//  Placement used to live in the water body's OnSceneGUI, which Unity only calls while that
//  body is the SELECTED object. The first click created a node and then SELECTED THE NEW NODE,
//  so the body was deselected, its OnSceneGUI stopped running, and every later click did nothing
//  (or just changed the selection). Clicking a node in the hierarchy had the same effect.
//  This tool listens to the Scene view directly and works whenever the river body OR any of
//  its nodes is selected, and it leaves the body selected so the next click keeps working.
//  It also registers a default control so the click is not eaten by Unity's object picking.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    [InitializeOnLoad]
    public static class UCRiverPlacementTool
    {
        private static readonly Color CourseColour = new Color(0.30f, 0.80f, 1f, 1f);
        private static readonly Color GhostColour  = new Color(1f, 0.85f, 0.25f, 1f);
        // UC-T04-05B: one editor owner coalesces point edits, hierarchy changes and Undo.
        private static readonly Dictionary<UCWaterBody, int> watched = new Dictionary<UCWaterBody, int>();
        private static readonly HashSet<UCWaterBody> pending = new HashSet<UCWaterBody>();
        private static bool editFlushQueued;

        static UCRiverPlacementTool()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
            Undo.postprocessModifications += OnModifications;
            EditorApplication.hierarchyChanged += QueueHierarchyReview;
        }

        /// <summary>The river body being edited: the selected body, or the body owning a selected node.</summary>
        private static UCWaterBody ActiveRiver()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null) return null;

            UCWaterBody body = go.GetComponent<UCWaterBody>();
            if (body == null) body = go.GetComponentInParent<UCWaterBody>(true);
            if (body == null) return null;
            if (body.shape != UCWaterBody.ShapeMode.RiverSpline) return null;
            return body;
        }

        private static void OnSceneGUI(SceneView view)
        {
            UCWaterBody body = ActiveRiver();
            if (body == null) return;
            Watch(body);

            Event e = Event.current;
            bool ctrl  = e.control || e.command;
            bool shift = e.shift;

            DrawCourse(body);

            // Keep Unity's own click-to-select from swallowing our click.
            if (e.type == EventType.Layout && (ctrl || shift))
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (ctrl && !shift)
            {
                if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag) view.Repaint();
                DrawGhost(body, e.mousePosition);
            }

            if (e.type != EventType.MouseDown || e.button != 0 || e.alt) return;
            if (!ctrl && !shift) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            Vector3 point;
            if (!RaycastGround(body, ray, out point)) return;

            if (shift && !ctrl)
            {
                if (!InsertNode(body, point, e.mousePosition)) return;
            }
            else
            {
                AppendNode(body, point);
            }
            e.Use();
        }

        // ---------------------------------------------------------------- picking the ground

        /// <summary>Picks terrain belonging to the selected river's world and scene, without a synthetic ground plane.</summary>
        /// <param name="ray">Finite world-space ray; direction must be nonzero.</param>
        /// <param name="point">World-metre ground hit, or zero when no owned surface is found.</param>
        /// <returns>False if no river is selected or owned ground is missing.</returns>
        public static bool RaycastGround(Ray ray, out Vector3 point)
        {
            return RaycastGround(ActiveRiver(), ray, out point);
        }

        /// <summary>Picks the nearest owned native terrain, retaining the exact ray hit and rejecting holes/foreign worlds.</summary>
        /// <param name="body">River whose world and scene own the ground; null fails.</param>
        /// <param name="ray">Finite world ray with a nonzero direction; range is capped at 20 km.</param>
        /// <param name="point">Ground position in world metres, or zero on failure.</param>
        /// <returns>True for a terrain-collider hit or bounded heightfield fallback when no enabled collider exists.</returns>
        public static bool RaycastGround(UCWaterBody body, Ray ray, out Vector3 point)
        {
            // UC-T04-05B: roofs, water volumes, a Y=0 plane and other worlds are not placement ground.
            point = Vector3.zero;
            if (body == null || !Finite(ray.origin) || !Finite(ray.direction) || ray.direction.sqrMagnitude < 1e-10f ||
                float.IsInfinity(ray.direction.sqrMagnitude)) return false;
            ray.direction = ray.direction.normalized;
            var world = body.CarveWorld;
            float nearest = 20000f; bool found = false;
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null || terrain.gameObject.scene != body.gameObject.scene ||
                    terrain.GetComponentInParent<UniverseCreatorObject>(true) != world) continue;
                var collider = terrain.GetComponent<TerrainCollider>();
                Vector3 hit;
                if (collider != null && collider.enabled && collider.terrainData == terrain.terrainData)
                {
                    if (!collider.Raycast(ray, out RaycastHit nativeHit, nearest)) continue;
                    hit = nativeHit.point;
                }
                else if (!SampleRay(terrain, ray, nearest, out hit)) continue;
                float distance = Vector3.Dot(hit - ray.origin, ray.direction);
                if (distance < 0 || distance > nearest) continue;
                nearest = distance; point = hit; found = true;
            }
            return found;
        }

        private static bool SampleRay(Terrain terrain, Ray ray, float limit, out Vector3 hit)
        {
            hit = Vector3.zero;
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position, size = data.size;
            Bounds bounds = new Bounds(origin + size * 0.5f, size + Vector3.up * 0.02f);
            if (!bounds.IntersectRay(ray, out float enter) || enter > limit) return false;
            float start = Mathf.Max(0, enter), end = limit;
            // Reverse entry finds the far side of this tile's box, avoiding a 20 km march per repaint.
            if (bounds.IntersectRay(new Ray(ray.GetPoint(limit), -ray.direction), out float reverse)) end = limit - reverse;
            if (end < start) return false;
            float cell = Mathf.Min(size.x, size.z) / Mathf.Max(1, data.heightmapResolution - 1);
            int steps = Mathf.Clamp(Mathf.CeilToInt((end - start) / Mathf.Max(0.05f, cell * 0.5f)), 1, 4096);
            float previousT = start; bool previousValid = false; float previousAbove = 0;
            for (int i = 0; i <= steps; i++)
            {
                float t = Mathf.Lerp(start, end, (float)i / steps);
                Vector3 p = ray.GetPoint(t);
                if (!Height(p, out float y)) { previousValid = false; continue; }
                float above = p.y - y;
                if (Mathf.Abs(above) < 0.0001f) { hit = p; hit.y = y; return true; }
                if (previousValid && previousAbove > 0 && above < 0)
                {
                    float low = previousT, high = t;
                    for (int j = 0; j < 16; j++)
                    {
                        float mid = (low + high) * 0.5f; p = ray.GetPoint(mid);
                        if (!Height(p, out y)) break;
                        if (p.y > y) low = mid; else high = mid;
                    }
                    p = ray.GetPoint((low + high) * 0.5f);
                    if (Height(p, out y) && Mathf.Abs(p.y - y) < 0.01f) { hit = p; hit.y = y; return true; }
                }
                previousValid = true; previousT = t; previousAbove = above;
            }
            return false;
            bool Height(Vector3 p, out float y)
            {
                y = 0;
                float u = (p.x - origin.x) / size.x, v = (p.z - origin.z) / size.z;
                if (u < 0 || v < 0 || u > 1 || v > 1) return false;
                int holes = data.holesResolution;
                if (holes > 0 && data.IsHole(Mathf.Min(holes - 1, (int)(u * holes)), Mathf.Min(holes - 1, (int)(v * holes)))) return false;
                y = origin.y + data.GetInterpolatedHeight(u, v);
                return !float.IsNaN(y) && !float.IsInfinity(y);
            }
        }

        // ---------------------------------------------------------------- editing

        private static UCRiverNode[] Nodes(UCWaterBody body)
        {
            return body.OwnedRiverNodes(); // UC-T04-05B: match runtime hierarchy order and nested-body ownership.
        }

        internal static void Watch(UCWaterBody body)
        {
            if (body != null && body.shape == UCWaterBody.ShapeMode.RiverSpline && !watched.ContainsKey(body))
                watched.Add(body, CourseSignature(body));
        }

        internal static void RebuildAfterEdit(UCWaterBody body)
        {
            if (body == null) return;
            Watch(body);
            if (!pending.Remove(body)) body.InvalidatePendingWaterAuthoring(); // UC-T04-07: direct clicks/inspector commits supersede old workers too.
            try { body.Rebuild(); EditorUtility.SetDirty(body); }
            catch (System.Exception error)
            { Debug.LogError("Universe Creator: river rebuild failed on '" + body.name + "'. " + error.Message, body); }
            watched[body] = CourseSignature(body);
        }

        internal static void RequestRebuild(UCWaterBody body)
        {
            if (body == null) return;
            Watch(body);
            if (pending.Add(body)) body.InvalidatePendingWaterAuthoring(); // UC-T04-07: invalidate immediately, rebuild once after the drag.
            QueueHierarchyReview();
        }

        private static UndoPropertyModification[] OnModifications(UndoPropertyModification[] modifications)
        {
            bool relevant = false;
            foreach (var modification in modifications)
            {
                var component = modification.currentValue?.target as Component;
                if (!(component is UCRiverNode) && !(component is Transform) && !(component is UCWaterBody)) continue;
                var body = component.GetComponentInParent<UCWaterBody>(true);
                if (body == null || body.shape != UCWaterBody.ShapeMode.RiverSpline) continue;
                RequestRebuild(body); relevant = true; // UC-T04-07: Undo-recorded edits cannot leave a captured course publishable until the drag ends.
            }
            if (relevant) QueueHierarchyReview();
            return modifications;
        }

        private static void OnUndoRedo()
        {
            // An unrelated editor's Undo must not regenerate unchanged UC rivers.
            QueueHierarchyReview();
        }

        private static void QueueHierarchyReview()
        {
            if (editFlushQueued || watched.Count == 0) return;
            editFlushQueued = true;
            EditorApplication.delayCall += FlushPendingEdits;
        }

        // UC-T04-05B: no polling, native writes in repaint, or rebuild for generated mesh hierarchy changes.
        internal static void FlushPendingEdits()
        {
            EditorApplication.delayCall -= FlushPendingEdits;
            editFlushQueued = false;
            if (GUIUtility.hotControl != 0 || EditorGUIUtility.editingTextField) { QueueHierarchyReview(); return; }
            var bodies = new List<UCWaterBody>(watched.Keys);
            foreach (var body in bodies)
            {
                if (body == null) { watched.Remove(body); pending.Remove(body); continue; }
                if (pending.Contains(body) || watched[body] != CourseSignature(body)) RebuildAfterEdit(body);
            }
            pending.Clear();
        }

        private static int CourseSignature(UCWaterBody body)
        {
            unchecked
            {
                int value = body.transform.localToWorldMatrix.GetHashCode();
                value = value * 31 + body.digTerrain.GetHashCode();
                value = value * 31 + (int)body.TerrainMode;
                value = value * 31 + (body.joinsRiver != null ? body.joinsRiver.GetEntityId().GetHashCode() : 0); // UC-T04-07: body-level target edits participate in Undo/hierarchy review.
                foreach (var node in Nodes(body))
                {
                    value = value * 31 + node.GetEntityId().GetHashCode(); // UC-T04-07: use the installed Unity identity API for coalesced edit signatures.
                    value = value * 31 + node.transform.position.GetHashCode();
                    value = value * 31 + node.halfWidthM.GetHashCode(); value = value * 31 + node.depthM.GetHashCode();
                    value = value * 31 + node.dischargeM3PerS.GetHashCode(); value = value * 31 + node.velocityMps.GetHashCode();
                    value = value * 31 + node.floodPlainStart.GetHashCode(); value = value * 31 + node.floodPlainEnd.GetHashCode();
                    value = value * 31 + node.connectJunction.GetHashCode();
                    value = value * 31 + (node.junctionTarget != null ? node.junctionTarget.GetEntityId().GetHashCode() : 0);
                }
                return value;
            }
        }

        private static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
                !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
        }

        /// <summary>A brand-new river starts as a 30 cm-wide, 20 cm-deep brook at node 0.</summary>
        public const float SourceWidthM = 0.3f;
        public const float SourceDepthM = 0.2f;

        /// <summary>Legacy growth multiplier retained for source compatibility; new points inherit without automatic growth.</summary>
        public const float GrowthPerNode = 1.12f;

        private static GameObject MakeNode(UCWaterBody body, Vector3 worldPos)
        {
            if (body == null || body.shape != UCWaterBody.ShapeMode.RiverSpline || !Finite(worldPos))
                throw new System.ArgumentException("A river owner and finite world position are required.");
            UCRiverNode[] existing = Nodes(body);
            Watch(body);
            Undo.RecordObject(body, "Edit river course");

            GameObject go = new GameObject("River Node");
            go.transform.SetParent(body.transform, true);
            go.transform.position = worldPos;
            Undo.RegisterCreatedObjectUndo(go, "Add river node"); // UC-T04-05B: Redo must capture the owned parent and exact click, not an unparented origin.
            UCRiverNode node = Undo.AddComponent<UCRiverNode>(go);
            Undo.RecordObject(node, "Set river point defaults"); // UC-T04-05B: Redo retains the authored source defaults.

            // UC-T04-05B: zero retains inheritance provenance; inserting/editing a point must carry forward.
            if (existing.Length == 0)
            {
                node.halfWidthM = SourceWidthM * 0.5f;
                node.depthM = SourceDepthM;
            }

            Rename(body);
            return go;
        }

        /// <summary>Names nodes by their order - "River Node 0 (source)", "River Node 1", ... - so the
        /// hierarchy reads like Road Architect's node list.</summary>
        private static void Rename(UCWaterBody body)
        {
            UCRiverNode[] nodes = Nodes(body);
            for (int i = 0; i < nodes.Length; i++)
            {
                string name = i == 0 ? "River Node 0 (source)" : "River Node " + i;
                if (nodes[i].gameObject.name == name) continue;
                Undo.RecordObject(nodes[i].gameObject, "Name river point");
                nodes[i].gameObject.name = name;
            }
        }

        private static void Commit(UCWaterBody body, GameObject select = null)
        {
            RebuildAfterEdit(body); // UC-T04-05B: Rebuild gathers once inside BuildDig.

            // Select the new node so its 3D move gizmo appears, exactly like placing a Road Architect
            // node. The tool listens to the Scene view directly, so CTRL/SHIFT + Click keeps working
            // with a node selected.
            Selection.activeGameObject = select != null ? select : body.gameObject;
        }

        /// <summary>Appends a point at the supplied ground hit, selects it and rebuilds once under Undo.</summary>
        /// <param name="body">River owner; must use RiverSpline shape.</param>
        /// <param name="worldPos">Finite world-metre position picked by the caller; never moved by fitting.</param>
        /// <returns>The new child point; later points retain zero/unset dimensions.</returns>
        /// <exception cref="System.ArgumentException">The owner, shape or position is invalid.</exception>
        public static UCRiverNode AppendNode(UCWaterBody body, Vector3 worldPos)
        {
            GameObject go = MakeNode(body, worldPos);
            Commit(body, go);
            return go.GetComponent<UCRiverNode>();
        }

        /// <summary>Inserts a node into the segment nearest the click (screen space). False if the click is
        /// not near the river, so a stray SHIFT+Click does not add a node somewhere unexpected.</summary>
        private static bool InsertNode(UCWaterBody body, Vector3 worldPos, Vector2 mouseGui)
        {
            UCRiverNode[] nodes = Nodes(body);
            if (nodes.Length < 2) { AppendNode(body, worldPos); return true; }

            int bestIndex = -1;
            float bestDist = 60f;                         // pixels
            for (int i = 0; i + 1 < nodes.Length; i++)
            {
                Vector2 a = HandleUtility.WorldToGUIPoint(nodes[i].transform.position);
                Vector2 b = HandleUtility.WorldToGUIPoint(nodes[i + 1].transform.position);
                float d = DistancePointSegment(mouseGui, a, b);
                if (d < bestDist) { bestDist = d; bestIndex = i; }
            }
            if (bestIndex < 0) return false;

            InsertBefore(body, nodes[bestIndex + 1], worldPos);
            return true;
        }

        // UC-T04-05B: the scene insertion and its regression use the same hierarchy mutation.
        internal static UCRiverNode InsertBefore(UCWaterBody body, UCRiverNode next, Vector3 worldPos)
        {
            if (next == null || next.GetComponentInParent<UCWaterBody>(true) != body)
                throw new System.ArgumentException("Insertion requires a point owned by this river.");
            Transform anchor = next.transform;
            GameObject go = MakeNode(body, worldPos);
            go.transform.SetParent(anchor.parent, true);
            go.transform.SetSiblingIndex(anchor.GetSiblingIndex());
            Rename(body);
            Commit(body, go);
            return go.GetComponent<UCRiverNode>();
        }

        private static float DistancePointSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        // ---------------------------------------------------------------- drawing

        private static void DrawCourse(UCWaterBody body)
        {
            if (Event.current.type != EventType.Repaint) return;
            UCRiverNode[] nodes = Nodes(body);
            if (nodes.Length == 0) return;

            Handles.color = CourseColour;
            for (int i = 0; i + 1 < nodes.Length; i++)
                Handles.DrawLine(nodes[i].transform.position, nodes[i + 1].transform.position, 2f);
            for (int i = 0; i < nodes.Length; i++)
            {
                Vector3 p = nodes[i].transform.position;
                float size = HandleUtility.GetHandleSize(p);
                Handles.color = i == 0 ? new Color(0.3f, 1f, 0.4f, 1f) : CourseColour;       // green = source
                Handles.SphereHandleCap(0, p, Quaternion.identity, size * (i == 0 ? 0.12f : 0.08f), EventType.Repaint);

                string label = i == 0 ? "0 (source)" : i.ToString();
                if (nodes[i].connectJunction) label += "  [junction]";
                Handles.Label(p + Vector3.up * size * 0.25f, label);
            }
        }

        private static void DrawGhost(UCWaterBody body, Vector2 mouseGui)
        {
            if (Event.current.type != EventType.Repaint) return;
            Ray ray = HandleUtility.GUIPointToWorldRay(mouseGui);
            Vector3 p;
            if (!RaycastGround(body, ray, out p)) return;

            Handles.color = GhostColour;
            UCRiverNode[] nodes = Nodes(body);
            if (nodes.Length > 0)
                Handles.DrawDottedLine(nodes[nodes.Length - 1].transform.position, p, 4f);
            Handles.DrawWireDisc(p, Vector3.up, HandleUtility.GetHandleSize(p) * 0.15f);
        }
    }
}
