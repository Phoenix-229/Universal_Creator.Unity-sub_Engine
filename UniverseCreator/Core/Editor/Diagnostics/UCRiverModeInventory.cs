// UC-T04-01: inspect serialized legacy flags before changing their runtime authority.
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    /// <summary>Read-only inventory of river-body and child mode flags in copied UC scenes and prefabs.</summary>
    public static class UCRiverModeInventory
    {
        /// <summary>Inspects assets under Assets/UniverseCreator through Editor serialization APIs and exits the isolated batch.</summary>
        /// <remarks>Does not save scenes or assets. Main-project open scenes and authoring outside the copied UC folder are not covered.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("River mode inventory requires isolated batch mode.");
            int scenes = 0, prefabs = 0, bodies = 0, points = 0, conflicts = 0;
            string[] roots = { "Assets/UniverseCreator" };
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", roots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                prefabs++;
                Inspect(prefab, path, ref bodies, ref points, ref conflicts);
            }
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", roots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var scene = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    scenes++;
                    foreach (GameObject root in scene.GetRootGameObjects())
                        Inspect(root, path, ref bodies, ref points, ref conflicts);
                }
                finally { EditorSceneManager.ClosePreviewScene(scene); }
            }
            Debug.Log($"UC-T04-01 serialized inventory: scenes={scenes}, prefabs={prefabs}, riverBodies={bodies}, points={points}, conflicts={conflicts}; assets unchanged.");
            EditorApplication.Exit(0);
        }

        private static void Inspect(GameObject root, string path, ref int bodies, ref int points, ref int conflicts)
        {
            foreach (UCWaterBody body in root.GetComponentsInChildren<UCWaterBody>(true))
            {
                var serialized = new SerializedObject(body);
                if (serialized.FindProperty("shape").intValue != (int)UCWaterBody.ShapeMode.RiverSpline) continue;
                bool bodyRead = serialized.FindProperty("followProceduralTrench").boolValue;
                bool dig = serialized.FindProperty("digTerrain").boolValue;
                int count = 0, read = 0;
                foreach (UCRiverNode point in body.GetComponentsInChildren<UCRiverNode>(true))
                {
                    // Nested water bodies own their own points; do not infer this body's intent from them.
                    if (point.GetComponentInParent<UCWaterBody>(true) != body) continue;
                    var data = new SerializedObject(point);
                    count++;
                    if (data.FindProperty("followProceduralTrench").boolValue) read++;
                }
                bool conflict = count > 0 && (bodyRead ? read != count : read > 0);
                bodies++; points += count; if (conflict) conflicts++;
                Debug.Log($"UC-T04-01 legacy asset={path}, body={body.name}, bodyRead={bodyRead}, dig={dig}, points={count}, readPoints={read}, conflict={conflict}.");
            }
        }
    }
}
