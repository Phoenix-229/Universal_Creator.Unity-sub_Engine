// UC-T06-13: calibrated scene creation uses selected buyer content and existing native contact/detail owners.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Editor.Inspectors;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    internal static class VegetationPrototypeReference
    {
        internal sealed class Result : IDisposable
        {
            internal Scene scene;
            internal GameObject root;
            internal TerrainData ground;
            internal TextAsset evidence;
            internal int modelCount, detailCount;
            internal string savedPath;
            internal bool preview; // UC-T06-13: temporary reference scenes must preserve unsaved live authoring.

            /// <summary>Closes this generated scene and destroys only its unsaved native data/evidence; saved assets and borrowed content survive.</summary>
            public void Dispose()
            {
                if (scene.IsValid() && scene.isLoaded)
                { if (preview) EditorSceneManager.ClosePreviewScene(scene); else EditorSceneManager.CloseScene(scene, true); }
                if (ground != null && !AssetDatabase.Contains(ground)) UnityEngine.Object.DestroyImmediate(ground);
                if (evidence != null && !AssetDatabase.Contains(evidence)) UnityEngine.Object.DestroyImmediate(evidence);
            }
        }

        [MenuItem("Tools/Universe Creator/Vegetation/Create Prototype Reference Scene")]
        private static void CreateSelected()
        {
            var world = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<UniverseCreatorObject>() : null;
            Create(world, true);
        }

        /// <summary>Creates a 64 m control plot with real prototype roots, native grass patches and close/medium/far cameras.</summary>
        /// <param name="world">Selected source world; its library, materials, graphs and managers remain unchanged.</param>
        /// <param name="persist">True saves unique generated terrain/evidence/scene assets under Assets/UniverseCreator/ReferenceScenes; false creates an owned temporary fixture.</param>
        /// <returns>Generated scene ownership for explicit cleanup; at most eight valid slots per category are previewed.</returns>
        /// <exception cref="InvalidOperationException">Missing usable content, an exceeded inspection envelope or native creation/save failure.</exception>
        /// <remarks>Main-thread explicit invocation. Calibration previews do not evaluate placement graphs or certify habitat/density/performance.</remarks>
        internal static Result Create(UniverseCreatorObject world, bool persist)
        {
            if (world == null || world.scatterLibrary == null) throw new InvalidOperationException("Select a Universe Creator world with authored prototype content.");
            var report = UCVegetationValidation.InspectPrototypeContent(world.scatterLibrary);
            if (report.truncated) throw new InvalidOperationException("Select a reference library within the 256-slot inspection envelope.");
            var selected = report.prototypes.Where(p => !p.invalid && p.source != null).GroupBy(p => p.kind).SelectMany(g => g.Take(8)).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("The selected library has no usable mesh or detail texture. Inspect prototype content for the missing slots and geometry errors.");
            var result = new Result();
            try
            {
                result.preview = !persist;
                result.scene = result.preview ? EditorSceneManager.NewPreviewScene() : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive); // UC-T06-13: temporary previews do not depend on an already saved startup scene.
                result.root = new GameObject("UC Prototype Reference — 64 m control plot");
                SceneManager.MoveGameObjectToScene(result.root, result.scene);
                result.ground = new TerrainData { name = "UC Prototype Reference Ground", heightmapResolution = 33, size = new Vector3(64, 20, 64) };
                result.ground.SetDetailResolution(64, 8);
                var terrainRoot = Terrain.CreateTerrainGameObject(result.ground); terrainRoot.transform.SetParent(result.root.transform, false);
                var terrain = terrainRoot.GetComponent<Terrain>();
                var contact = new ScatterApplier.FinalGroundContext(terrain, result.ground);
                var detailLibrary = new ScatterPrototypeLibrary();
                var columns = new Dictionary<UCScatterKind, int>();
                foreach (var item in selected)
                {
                    columns.TryGetValue(item.kind, out int column); columns[item.kind] = column + 1;
                    if (item.kind == UCScatterKind.Detail)
                    { detailLibrary.details.Add(world.scatterLibrary.details[item.slot]); continue; }
                    float z = item.kind == UCScatterKind.Tree ? 12 : item.kind == UCScatterKind.Shrub ? 32 : 52;
                    var placement = new ScatterInstance(new Vector3(4 + column * 8, 0, z), Quaternion.identity, Vector3.one, item.slot)
                    { ground = new ScatterGroundBinding(0, 0, Quaternion.identity, Vector3.up, 0) };
                    if (!contact.TrySeat(placement, item.prefab, Vector3.one, 0, 0, out Vector3 position, out Quaternion rotation))
                        throw new InvalidOperationException("Prototype root contact failed for " + item.kind + " slot " + item.slot + ".");
                    GameObject instance = PrefabUtility.IsPartOfPrefabAsset(item.prefab)
                        ? (GameObject)PrefabUtility.InstantiatePrefab(item.prefab, result.scene) : UnityEngine.Object.Instantiate(item.prefab);
                    SceneManager.MoveGameObjectToScene(instance, result.scene); instance.transform.SetParent(result.root.transform, true);
                    instance.transform.SetPositionAndRotation(position, rotation); instance.SetActive(true);
                    instance.name = item.kind + " slot " + item.slot + " — " + item.prefab.name;
                    result.modelCount++;
                }
                // The same constructor owns production detail sizes/render modes; source materials are never rewritten here.
                ScatterApplier.ApplyDetailPrototypes(result.ground, detailLibrary, null, 0);
                result.ground.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
                result.detailCount = result.ground.detailPrototypes.Length;
                for (int layer = 0; layer < result.detailCount; layer++)
                {
                    var density = new int[64, 64];
                    for (int z = 20; z < 24; z++) for (int x = 2 + layer * 8; x < 6 + layer * 8; x++) density[z, x] = 8;
                    result.ground.SetDetailLayer(0, 0, layer, density);
                }
                var lightRoot = new GameObject("Reference directional light"); lightRoot.transform.SetParent(result.root.transform, false);
                var light = lightRoot.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1;
                lightRoot.transform.rotation = Quaternion.Euler(50, -30, 0);
                float height = Mathf.Max(4, selected.Max(p => p.heightM) * 0.5f);
                foreach (float distance in new[] { 20f, 60f, 160f })
                {
                    var cameraRoot = new GameObject("Reference camera — " + distance + " m"); cameraRoot.transform.SetParent(result.root.transform, false);
                    var camera = cameraRoot.AddComponent<Camera>(); camera.enabled = distance == 20; camera.fieldOfView = 60; camera.farClipPlane = Mathf.Max(400, height * 4);
                    cameraRoot.transform.position = new Vector3(32, height, 32 - distance); cameraRoot.transform.LookAt(new Vector3(32, height, 32));
                }
                result.evidence = new TextAsset(report.Describe() + "\nReference: first eight valid slots/category; 64 m flat native control plot; grass patches 4 m × 4 m at 8 instances per 1 m² cell; cameras at 20/60/160 m, vertical FOV 60°.\n" +
                    "Check root/collider contact, canopy overlap, understory/grass appearance, wind root/tip motion and near/far LOD continuity. Preview instances retain actual source materials/LOD settings. Wind uses active UC globals or the source shader's own contract; no frame-rate or visual pass is inferred.\n") { name = "UC Prototype Reference Evidence" };
                if (persist)
                {
                    const string parent = "Assets/UniverseCreator/ReferenceScenes";
                    if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets/UniverseCreator", "ReferenceScenes");
                    string folder = AssetDatabase.GenerateUniqueAssetPath(parent + "/VegetationPrototypeReference");
                    AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
                    AssetDatabase.CreateAsset(result.ground, folder + "/Ground.asset");
                    AssetDatabase.CreateAsset(result.evidence, folder + "/ContentEvidence.asset");
                    AssetDatabase.SaveAssetIfDirty(result.ground); AssetDatabase.SaveAssetIfDirty(result.evidence);
                    result.savedPath = folder + "/PrototypeReference.unity";
                    if (!EditorSceneManager.SaveScene(result.scene, result.savedPath)) throw new InvalidOperationException("Unity could not save the generated prototype reference scene.");
                }
                Undo.RegisterCreatedObjectUndo(result.root, "Create UC prototype reference scene"); Selection.activeGameObject = result.root;
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
}
