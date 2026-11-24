// UC-T06-18: explicit authoring saves intent; the existing graph/native replacement remains the only application pass.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor
{
    internal static class UCVegetationManualAuthoring
    {
        internal sealed class State
        {
            internal int Mode, Category, Slot, Page;
            internal Vector3 Position, Euler, Scale = Vector3.one;
            internal Rect Bounds = new Rect(0, 0, 4, 4);
            internal float Radius = 1, Density = 1;
            internal VegetationInstanceKey Key;
            internal List<ScatterRemovalStore.ManualEdit> Entries;
            internal string Error;
        }
        private static readonly string[] Modes = { "Locked pose", "Exclusion", "Grass patch" };
        private static readonly string[] Categories = { "trees", "shrubs", "objects" };

        internal static void Draw(UCVegetationSystemManager manager, State state)
        {
            var world = manager.World;
            if (world == null) { EditorGUILayout.HelpBox("Bind a UC world before saving manual vegetation intent.", MessageType.Info); return; }
            EditorGUILayout.HelpBox("Save exact world-metre poses or grass/exclusion rectangles, then use the existing Run / Apply control. Locked poses retain their transforms after terrain changes; inspect contact warnings and move/re-lock explicitly. Overrides reserve bounded native resources.", MessageType.Info);
            EditorGUILayout.LabelField("Saved overrides", manager.RemovedInstances.ManualIntentCount.ToString());
            state.Mode = EditorGUILayout.Popup("Intent", state.Mode, Modes);
            state.Category = EditorGUILayout.Popup("Pose category", state.Category, Categories);
            state.Slot = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Prototype slot", "Zero-based slot in this world's category library; saved edits retain the prototype identity when slots reorder."), state.Slot));
            if (state.Mode == 0)
            {
                state.Position = EditorGUILayout.Vector3Field("World position (m)", state.Position);
                state.Euler = EditorGUILayout.Vector3Field("Rotation (degrees)", state.Euler);
                state.Scale = EditorGUILayout.Vector3Field("Prototype scale multipliers", state.Scale);
                state.Radius = EditorGUILayout.FloatField("Exclusion radius (m, 0–64)", state.Radius);
            }
            else
            {
                state.Bounds = EditorGUILayout.RectField(new GUIContent("World X/Z bounds (m)", "Half-open rectangle, positive extents up to 128 metres; exclusion applies to every procedural category."), state.Bounds);
                if (state.Mode == 2) state.Density = EditorGUILayout.FloatField("Grass density (blades/m², 0–64)", state.Density);
            }
            if (GUILayout.Button("New override")) { state.Key = default; state.Error = null; }
            if (GUILayout.Button("Use selected generated object's identity and pose"))
                Act(state, () => CaptureSelected(manager, state));
            if (GUILayout.Button("Save override (pending Run / Apply)"))
                Act(state, () => Save(manager, state));
            using (new EditorGUI.DisabledScope(!state.Key.IsAssigned))
            {
                if (GUILayout.Button("Remove addressed instance")) Act(state, () => EditAddressed(manager, state.Key, () => manager.RemovedInstances.Remove(state.Key)));
                if (GUILayout.Button("Restore addressed instance")) Act(state, () => EditAddressed(manager, state.Key, () => manager.RemovedInstances.Restore(state.Key)));
                if (GUILayout.Button("Clear addressed override")) Act(state, () => EditAddressed(manager, state.Key, () => manager.RemovedInstances.ClearManualIntent(state.Key)));
            }
            if (GUILayout.Button("Refresh saved override list")) Act(state, () => state.Entries = manager.RemovedInstances.CaptureManualEdits(world.ReadVegetationIdentity()));
            if (state.Entries != null)
            {
                int pages = Mathf.Max(1, (state.Entries.Count + 63) / 64);
                state.Page = Mathf.Clamp(state.Page, 0, pages - 1);
                EditorGUILayout.LabelField("Override page", (state.Page + 1) + " / " + pages);
                using (new EditorGUI.DisabledScope(state.Page == 0)) if (GUILayout.Button("Previous page")) state.Page--;
                using (new EditorGUI.DisabledScope(state.Page + 1 >= pages)) if (GUILayout.Button("Next page")) state.Page++;
                int start = state.Page * 64, end = Mathf.Min(start + 64, state.Entries.Count); // UC-T06-18: every saved tree/grass override remains reachable with bounded repaint work.
                for (int i = start; i < end; i++)
                {
                    var entry = state.Entries[i];
                    if (!GUILayout.Button("Edit " + Modes[entry.Kind] + " · " + entry.Key.Prototype)) continue;
                    state.Key = entry.Key; state.Mode = entry.Kind; state.Position = entry.Pose.position;
                    state.Euler = entry.Pose.rotation.eulerAngles; state.Scale = entry.Pose.scale;
                    state.Radius = entry.Pose.footprintRadius; state.Bounds = entry.Bounds; state.Density = entry.Density;
                    state.Category = Mathf.Max(0, Array.IndexOf(Categories, entry.Channel));
                }
            }
            if (!string.IsNullOrEmpty(state.Error)) EditorGUILayout.HelpBox(state.Error, MessageType.Error);
        }

        internal static void Save(UCVegetationSystemManager manager, State state)
        {
            var world = manager.World;
            if (world == null) throw new InvalidOperationException("A bound UC world is required.");
            string channel = state.Mode == 2 ? "grass" : Categories[state.Category];
            var library = world.scatterLibrary;
            string prototype = state.Key.IsAssigned ? state.Key.Prototype : state.Mode == 1 ? "manual-exclusion" :
                channel == "trees" ? (state.Slot < library.trees.Count ? library.trees[state.Slot]?.persistentId : null) :
                channel == "shrubs" ? (state.Slot < library.shrubs.Count ? library.shrubs[state.Slot]?.persistentId : null) :
                channel == "grass" ? (state.Slot < library.details.Count ? library.details[state.Slot]?.persistentId : null) :
                state.Slot < library.objects.Count ? library.objects[state.Slot]?.persistentId : null;
            if (string.IsNullOrEmpty(prototype)) throw new InvalidOperationException("Choose an existing prototype with a persistent identity; use Initialize Legacy Identities for old libraries.");
            var key = state.Key.IsAssigned ? state.Key : VegetationInstanceKey.Create(world.ReadVegetationIdentity(),
                "manual:" + Guid.NewGuid().ToString("N"), prototype, state.Mode == 0 ? state.Position.x : state.Bounds.x, state.Mode == 0 ? state.Position.z : state.Bounds.y);
            if (key.World != world.ReadVegetationIdentity()) throw new InvalidOperationException("The addressed override belongs to another world.");
            Edit(manager, () =>
            {
                if (state.Mode == 0) manager.RemovedInstances.SetManualPlacement(key, channel,
                    new ScatterInstance(state.Position, Quaternion.Euler(state.Euler), state.Scale, state.Slot) { footprintRadius = state.Radius });
                else if (state.Mode == 1) manager.RemovedInstances.SetManualExclusion(key, state.Bounds);
                else manager.RemovedInstances.SetManualDetailPatch(key, state.Bounds, state.Density);
            });
            state.Key = key; state.Entries = null;
        }

        private static void CaptureSelected(UCVegetationSystemManager manager, State state)
        {
            var selected = Selection.activeGameObject;
            var tag = selected != null ? selected.GetComponentInParent<UCScatterRegionResource>() : null;
            if (tag == null || tag.World != manager.World || !tag.Identity.IsAssigned) throw new InvalidOperationException("Select a generated shrub or object root from this world.");
            var library = manager.World.scatterLibrary;
            int slot = library.shrubs.FindIndex(item => item != null && item.persistentId == tag.Identity.Prototype);
            GameObject prefab;
            if (slot >= 0) { state.Category = 1; prefab = library.shrubs[slot].prefab; }
            else { slot = library.objects.FindIndex(item => item != null && item.persistentId == tag.Identity.Prototype); state.Category = 2; prefab = slot >= 0 ? library.objects[slot].prefab : null; }
            if (prefab == null || prefab.transform.localScale.x == 0 || prefab.transform.localScale.y == 0 || prefab.transform.localScale.z == 0) throw new InvalidOperationException("The selected prototype is unavailable or has zero root scale.");
            state.Key = tag.Identity; state.Mode = 0; state.Slot = slot; state.Position = tag.transform.position; state.Euler = tag.transform.rotation.eulerAngles;
            Vector3 scale = tag.transform.lossyScale, basis = prefab.transform.localScale;
            state.Scale = new Vector3(scale.x / basis.x, scale.y / basis.y, scale.z / basis.z);
        }
        private static void Edit(UCVegetationSystemManager manager, Action action)
        {
            Undo.RecordObject(manager, "Edit persistent vegetation intent"); action(); EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }
        private static void EditAddressed(UCVegetationSystemManager manager, VegetationInstanceKey key, Action action)
        {
            if (manager.World == null || key.World != manager.World.ReadVegetationIdentity()) throw new InvalidOperationException("The addressed instance belongs to another world.");
            Edit(manager, action);
        }
        private static void Act(State state, Action action)
        {
            try { action(); state.Error = null; }
            catch (ArgumentException error) { state.Error = error.Message; }
            catch (InvalidOperationException error) { state.Error = error.Message; }
        }
    }
}
