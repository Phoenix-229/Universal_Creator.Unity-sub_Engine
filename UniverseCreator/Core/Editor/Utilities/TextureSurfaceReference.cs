// UC-T07-12: preserve the applied world's coordinates, accepted height and authored materials in resolution comparisons.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    internal static class TextureSurfaceReference
    {
        internal sealed class Result : IDisposable
        {
            internal Scene Scene;
            internal string SavedPath;
            internal readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
            internal ProductMemoryBudget.Lease Memory;
            internal bool Preview;

            /// <summary>Closes this reference scene and retires unsaved generated objects; saved assets and borrowed source textures survive.</summary>
            public void Dispose()
            {
                if (Scene.IsValid() && Scene.isLoaded)
                { if (Preview) EditorSceneManager.ClosePreviewScene(Scene); else EditorSceneManager.CloseScene(Scene, true); }
                foreach (var item in Owned) if (item != null && !AssetDatabase.Contains(item)) UnityEngine.Object.DestroyImmediate(item);
                Owned.Clear(); Memory?.Dispose(); Memory = null;
            }
        }

        [MenuItem("Tools/Universe Creator/Texture/Create Applied Surface Reference Scene")]
        private static void CreateSelected()
        {
            var terrain = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<UnityEngine.Terrain>() : null;
            using (var result = Create(terrain, true))
                Debug.Log("UC-T07-12 saved applied-world surface comparison: " + result.SavedPath +
                    ". Open this scene by itself; select one resolution group and one camera. Calibrate lighting/exposure and inspect actual authored content before claiming realism.");
        }

        /// <summary>Creates three native resolution groups at the selected applied terrain's original world coordinates using its accepted ground and material inputs.</summary>
        /// <param name="source">Terrain belonging to a UC world with current accepted ground and an applied material; at most four layers and a 512² source control map.</param>
        /// <param name="persist">True saves generated copies below Assets/UniverseCreator/ReferenceScenes; false returns a temporary owned preview scene.</param>
        /// <returns>Scene/data/material ownership for explicit disposal; only the first resolution group starts active.</returns>
        /// <exception cref="InvalidOperationException">Missing/stale accepted inputs, unsupported mapping/content, resource admission or scene save failure.</exception>
        /// <remarks>Main-thread explicit authoring. Groups use 33/65/129 heights and 16/32/64 controls, with 20/60/160 metre cameras.
        /// Declared per-terrain shader overrides are frozen into the copied material; engine layer sampler textures are frozen into copied TerrainLayers so reload uses the actual native recipient.
        /// Textures remain borrowed; live terrain, graphs, materials, lighting and saved user scenes are not edited. Fixed lighting still requires exposure calibration.</remarks>
        internal static Result Create(UnityEngine.Terrain source, bool persist)
        {
            GraphExecutionGuard.RequireMainThread("Applied surface reference creation");
            var world = source != null ? source.GetComponentInParent<UniverseCreatorObject>(true) : null; // UC-T07-12: explicit authoring also accepts inactive loaded world regions.
            var original = source != null ? source.terrainData : null;
            if (world == null || world.graph == null || original == null || source.materialTemplate == null) throw new InvalidOperationException("Select an applied terrain under its UC world with a bound Main Graph.");
            Vector3 position = source.transform.position, size = original.size;
            if (source.transform.rotation != Quaternion.identity || source.transform.lossyScale != Vector3.one ||
                !RiverChannelSnapshot.Finite(position.x) || !RiverChannelSnapshot.Finite(position.y) || !RiverChannelSnapshot.Finite(position.z) ||
                !RiverChannelSnapshot.Finite(size.x) || !RiverChannelSnapshot.Finite(size.y) || !RiverChannelSnapshot.Finite(size.z) || size.x <= 0 || size.y <= 0 || size.z <= 0)
                throw new InvalidOperationException("The reference requires the native terrain's unrotated, unscaled world-metre mapping.");
            Vector3 centre = position + new Vector3(size.x * 0.5f, 0, size.z * 0.5f);
            if (!RiverChannelRegistry.TryGetCommittedSnapshot(world, centre.x, centre.z, out var ground) || !ground.Run.Equals(world.GenerationScheduler.CurrentRun))
                throw new InvalidOperationException("Run the bound Main Graph to publish current accepted ground, then Run/Apply the Texture Graph.");
            var sourceLayers = original.terrainLayers;
            if (sourceLayers.Length == 0 || sourceLayers.Length > 4 || original.alphamapWidth > 512 || original.alphamapHeight > 512)
                throw new InvalidOperationException("Choose a reference region with 1…4 material layers and source controls at most 512²; larger captures need a separately admitted region.");
            var result = new Result { Preview = !persist };
            try
            {
                // UC-T07-12: property blocks are not scene assets; freeze declared overrides in the owned copy before persistence validation.
                var material = new Material(source.materialTemplate); result.Owned.Add(material);
                var block = new MaterialPropertyBlock(); source.GetSplatMaterialPropertyBlock(block);
                // UC-T07-12: native height/control snapshots cannot silently substitute for independently overridden renderer geometry/coverage textures.
                if (block.HasProperty("_Control") || block.HasProperty("_TerrainHolesTexture"))
                    throw new InvalidOperationException("Apply control/holes overrides to the native TerrainData before creating its surface reference.");
                for (int i = sourceLayers.Length; i < 4; i++)
                    if (block.HasProperty("_Splat" + i) || block.HasProperty("_Normal" + i) || block.HasProperty("_Mask" + i))
                        throw new InvalidOperationException("A layer texture override selects an absent native TerrainLayer.");
                for (int property = 0; property < material.shader.GetPropertyCount(); property++)
                {
                    string name = material.shader.GetPropertyName(property);
                    if (!block.HasProperty(name)) continue;
                    switch (material.shader.GetPropertyType(property))
                    {
                        case ShaderPropertyType.Float: case ShaderPropertyType.Range: material.SetFloat(name, block.GetFloat(name)); break;
                        case ShaderPropertyType.Int: material.SetInteger(name, block.GetInteger(name)); break;
                        case ShaderPropertyType.Color: material.SetColor(name, block.GetColor(name)); break;
                        case ShaderPropertyType.Vector: material.SetVector(name, block.GetVector(name)); break;
                        case ShaderPropertyType.Texture: material.SetTexture(name, block.GetTexture(name)); break;
                    }
                }
                if (persist)
                {
                    foreach (string property in material.GetTexturePropertyNames()) RequirePersistentTexture(material.GetTexture(property));
                    foreach (TerrainLayer layer in sourceLayers)
                        if (layer != null)
                        { RequirePersistentTexture(layer.diffuseTexture); RequirePersistentTexture(layer.normalMapTexture); RequirePersistentTexture(layer.maskMapTexture); }
                    // UC-T07-12: Unity refuses additive authoring beside untitled scenes; diagnose before admitting terrain copies or creating scene assets.
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                        if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                            throw new InvalidOperationException("Save open untitled scenes before saving a reusable surface reference.");
                }
                long sourceBytes = (long)original.alphamapWidth * original.alphamapHeight * sourceLayers.Length * 8;
                result.Memory = world.ProductMemory.Reserve(sourceBytes + 2 * 1024 * 1024, 8 * 1024 * 1024);
                float[,,] controls = original.GetAlphamaps(0, 0, original.alphamapWidth, original.alphamapHeight);
                var surface = new SurfaceProduct { maxLayers = sourceLayers.Length, splat = new SplatProduct() };
                var layers = new TerrainLayer[sourceLayers.Length];
                for (int layer = 0; layer < layers.Length; layer++)
                {
                    if (sourceLayers[layer] == null) throw new InvalidOperationException("The applied terrain contains an unresolved material layer.");
                    layers[layer] = UnityEngine.Object.Instantiate(sourceLayers[layer]); result.Owned.Add(layers[layer]);
                    // UC-T07-12: Unity rebuilds _Splat/_Normal/_Mask from TerrainLayers on reload; a material-only copy loses effective per-terrain texture overrides.
                    string slot = layer.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (block.HasProperty("_Splat" + slot)) layers[layer].diffuseTexture = LayerTextureOverride(block, "_Splat" + slot);
                    if (block.HasProperty("_Normal" + slot)) layers[layer].normalMapTexture = LayerTextureOverride(block, "_Normal" + slot);
                    if (block.HasProperty("_Mask" + slot)) layers[layer].maskMapTexture = LayerTextureOverride(block, "_Mask" + slot);
                    var map = new Matrix(new CoordRect(0, 0, original.alphamapWidth, original.alphamapHeight)) { worldPos = position, worldSize = size };
                    for (int z = 0; z < original.alphamapHeight; z++) for (int x = 0; x < original.alphamapWidth; x++) map.arr[z * original.alphamapWidth + x] = controls[z, x, layer];
                    surface.splat.layers[layer] = new LayerProduct(); surface.splat.coverage[layer] = map;
                }
                result.Scene = persist ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : EditorSceneManager.NewPreviewScene();
                var root = new GameObject("UC applied surface reference — " + source.name); SceneManager.MoveGameObjectToScene(root, result.Scene);
                for (int variant = 0; variant < 3; variant++)
                {
                    int resolution = (32 << variant) + 1;
                    var data = new TerrainData { name = "Surface " + resolution, heightmapResolution = resolution,
                        alphamapResolution = 16 << variant, baseMapResolution = 128, size = size, terrainLayers = layers };
                    result.Owned.Add(data); var heights = new float[resolution, resolution];
                    for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++)
                    {
                        if (!ground.TryGround(position.x + x * size.x / (resolution - 1), position.z + z * size.z / (resolution - 1), out float y))
                            throw new InvalidOperationException("Accepted ground does not cover the complete reference terrain.");
                        float normalized = (y - position.y) / size.y;
                        if (normalized < 0 || normalized > 1) throw new InvalidOperationException("Accepted ground and the applied terrain disagree on vertical metre mapping.");
                        heights[z, x] = normalized;
                    }
                    data.SetHeights(0, 0, heights); data.SetAlphamaps(0, 0, UCTextureGraphApplier.BuildCoverage(surface, data, position));
                    var terrainRoot = UnityEngine.Terrain.CreateTerrainGameObject(data); terrainRoot.transform.SetParent(root.transform, false);
                    terrainRoot.name = "Height " + resolution + " / control " + data.alphamapResolution; terrainRoot.transform.position = position;
                    terrainRoot.GetComponent<UnityEngine.Terrain>().materialTemplate = material; terrainRoot.SetActive(variant == 0);
                }
                if (!ground.TryGround(centre.x, centre.z, out float centreY)) throw new InvalidOperationException("Reference centre has no accepted ground.");
                Vector3 target = new Vector3(centre.x, centreY + 2, centre.z);
                foreach (float distance in new[] { 20f, 60f, 160f })
                {
                    var cameraRoot = new GameObject("Surface camera — " + distance + " m"); cameraRoot.transform.SetParent(root.transform, false);
                    var camera = cameraRoot.AddComponent<Camera>(); camera.enabled = distance == 20; camera.fieldOfView = 60; camera.farClipPlane = Mathf.Max(500, size.magnitude * 2);
                    cameraRoot.transform.position = target + new Vector3(0, distance * 0.4f, -distance); cameraRoot.transform.LookAt(target);
                }
                var lightRoot = new GameObject("Fixed reference light — calibrate exposure"); lightRoot.transform.SetParent(root.transform, false);
                var light = lightRoot.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1; lightRoot.transform.rotation = Quaternion.Euler(50, -30, 0);
                var textureEvidence = new StringBuilder();
                foreach (string property in material.GetTexturePropertyNames()) AppendTextureEvidence(textureEvidence, "Material " + property, material.GetTexture(property));
                for (int layer = 0; layer < sourceLayers.Length; layer++)
                {
                    AppendTextureEvidence(textureEvidence, "Layer " + layer + " albedo", sourceLayers[layer].diffuseTexture);
                    AppendTextureEvidence(textureEvidence, "Layer " + layer + " normal", sourceLayers[layer].normalMapTexture);
                    AppendTextureEvidence(textureEvidence, "Layer " + layer + " mask", sourceLayers[layer].maskMapTexture);
                }
                var evidence = new TextAsset("UC-T07-12 applied world: " + world.name + "; graph: " + world.graph.name + "; accepted ground: " + ground.GroundVersion +
                    "\nOriginal world coordinates and declared per-terrain material overrides retained. Shader: " + material.shader.name +
                    ". Height/control groups: 33/16, 65/32, 129/64. Fixed Unity light intensity 1; calibrate exposure/illuminance separately. " +
                    "Inspect actual plains/hills/desert/rock/snow and river/lake/ocean plots selected from the authored world; missing habitats/content require another real source region. " +
                    "No generated pattern, headless checksum or scene creation proves realistic material quality. Capture matched CPU/GPU/native memory on the target device.\n" + textureEvidence);
                result.Owned.Add(evidence);
                if (persist)
                {
                    const string parent = "Assets/UniverseCreator/ReferenceScenes";
                    if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets/UniverseCreator", "ReferenceScenes");
                    string name = "Surface_" + Guid.NewGuid().ToString("N"), folder = parent + "/" + name;
                    AssetDatabase.CreateFolder(parent, name);
                    for (int i = 0; i < result.Owned.Count; i++) AssetDatabase.CreateAsset(result.Owned[i], folder + "/Input_" + i + ".asset");
                    result.SavedPath = folder + "/SurfaceReference.unity";
                    if (!EditorSceneManager.SaveScene(result.Scene, result.SavedPath)) throw new InvalidOperationException("Unity could not save the applied surface reference scene.");
                    AssetDatabase.SaveAssets();
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static Texture2D LayerTextureOverride(MaterialPropertyBlock block, string property)
        {
            Texture texture = block.GetTexture(property);
            if (texture != null && !(texture is Texture2D))
                throw new InvalidOperationException("Native TerrainLayer texture override " + property + " requires Texture2D; bake unsupported texture dimensions before creating a reference.");
            return texture as Texture2D;
        }

        private static void RequirePersistentTexture(Texture texture)
        {
            if (texture != null && !AssetDatabase.Contains(texture))
                throw new InvalidOperationException("Persist the applied material and terrain layers' generated texture inputs before saving a reusable reference scene.");
        }

        private static void AppendTextureEvidence(StringBuilder evidence, string role, Texture texture)
        {
            // UC-T07-12: record dimensions/format for matched captures without reading pixels or inventing GPU residency/cost.
            evidence.Append(role).Append(": ");
            if (texture == null) { evidence.AppendLine("unbound"); return; }
            evidence.Append(texture.name).Append(" / ").Append(texture.width).Append('x').Append(texture.height)
                .Append(" / ").Append(texture.graphicsFormat).Append(" / ").Append(texture.dimension)
                .Append(" / asset ").AppendLine(AssetDatabase.GetAssetPath(texture));
        }
    }
}
