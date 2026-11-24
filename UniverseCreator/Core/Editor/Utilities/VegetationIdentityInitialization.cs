// UC-T06-06: explicit, Undo-backed legacy identity initialization; capture remains read-only.
using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem; // UC-T06-06: resolve the existing world owner in its runtime namespace.

namespace UniverseCreator.Editor
{
    /// <summary>Initializes missing serialized world/prototype namespaces without changing placement or persistent removals.</summary>
    public static class VegetationIdentityInitialization
    {
        [MenuItem("Tools/Universe Creator/Vegetation/Initialize Legacy Identities")]
        private static void InitializeSelected()
        {
            var world = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<UniverseCreatorObject>() : null;
            if (world == null) throw new InvalidOperationException("Select a Universe Creator world before initializing legacy vegetation identities.");
            int changed = Initialize(world);
            Debug.Log("Universe Creator: initialized " + changed + " missing vegetation identities. Save the world, then explicitly migrate legacy removals from complete original placement or select each intended instance.", world);
        }

        /// <summary>Assigns IDs only to missing world/prototype fields through native serialization and Undo.</summary>
        /// <param name="world">Non-null scene/world owner; existing nonempty IDs and persistent edits are retained.</param>
        /// <returns>Number of missing fields initialized; zero when all fields already have IDs.</returns>
        /// <exception cref="ArgumentNullException">The world is absent.</exception>
        /// <remarks>Main thread only. This authoring action must precede read-only capture for legacy data. It does not resolve duplicate IDs or infer which colliding legacy edit was intended.</remarks>
        public static int Initialize(UniverseCreatorObject world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            var data = new SerializedObject(world); int changed = 0;
            void Assign(SerializedProperty property)
            {
                if (property == null || !string.IsNullOrEmpty(property.stringValue)) return;
                property.stringValue = Guid.NewGuid().ToString("N"); changed++;
            }
            Assign(data.FindProperty("vegetationWorldId"));
            var library = data.FindProperty("scatterLibrary");
            if (library != null)
                foreach (string channel in new[] { "trees", "shrubs", "details", "objects" })
                {
                    var roster = library.FindPropertyRelative(channel);
                    if (roster == null || !roster.isArray) continue;
                    for (int i = 0; i < roster.arraySize; i++) Assign(roster.GetArrayElementAtIndex(i).FindPropertyRelative("persistentId"));
                }
            if (changed > 0) data.ApplyModifiedProperties(); // Unity's native application records Undo and scene dirtiness.
            return changed;
        }
    }
}
