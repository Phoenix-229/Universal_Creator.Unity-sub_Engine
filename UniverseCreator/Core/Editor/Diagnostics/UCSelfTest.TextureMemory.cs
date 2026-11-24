using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T02-06: this fixture owns every temporary object and a unique asset folder; it never edits an owner's assets.
        private static void TestGeneratedTextureMemory()
        {
            string folder = "Assets/UC_TextureMemory_" + Guid.NewGuid().ToString("N");
            var memory = new ProductMemoryBudget(4 * 1024 * 1024);
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            TileData tile = null;
            Texture2D borrowed = null;
            TerrainData data = null;
            GameObject terrainObject = null;
            Material material = null;
            try
            {
                var noise = new NoiseTextureNode { id = "texture-memory-noise", resolution = 32, contentHash = 81 };
                graph.nodes.Add(noise);
                var evaluator = new GraphEvaluator(graph);
                tile = MemoryTestTile(memory);
                var generated = (TextureRef)evaluator.PullNode(noise, 0, tile, new StopToken());
                Texture2D original = generated.texture;
                Color originalPixel = original.GetPixel(11, 7);
                Check(memory.NativeBytes > 0 && ReferenceEquals(generated, evaluator.PullNode(noise, 0, tile, new StopToken())),
                    "UC-T02-06 repeated texture pulls share one charged native allocation within an evaluation.");
                var retained = new Dictionary<string, RetainedProduct>(tile.retained);
                Check(retained[noise.id + ":0"].product == null,
                    "UC-T02-06 retained dictionaries cannot publish descriptors of evaluation-owned native textures.");

                borrowed = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
                var tinyMemory = new ProductMemoryBudget(128 * 1024);
                TileData refusedTile = MemoryTestTile(tinyMemory);
                bool refused = false;
                try { MaskMapPackerNode.PackMask(borrowed, null, null, null, 0, 1, 0.5f, 0.5f, false, 32, null, refusedTile); }
                catch (InvalidOperationException) { refused = true; }
                finally { refusedTile.ReleaseProductMemory(); }
                Check(refused && borrowed != null && tinyMemory.ManagedBytes == 0 && tinyMemory.NativeBytes == 0,
                    "UC-T02-06 tiny mask output rejects oversized source-pixel demand and preserves the borrowed input.");

                var mask = MaskMapPackerNode.PackMask(original, null, null, null, 0, 1, 0.5f, 0.5f, false, 32, null, tile);
                Texture2D originalMask = mask.texture;
                Check(memory.NativeBytes > 2 * 32 * 32 * 4,
                    "UC-T02-06 inline mask and noise textures retain independent native reservations after scratch release.");

                Check(!string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", folder.Substring(7))),
                    "UC-T02-06 texture persistence fixture creates its isolated asset folder.");
                Shader shader = Shader.Find(UCShaderProps.ShaderName);
                Check(shader != null, "UC-T02-06 texture persistence uses the installed UC terrain shader.");
                if (shader == null) return;
                material = new Material(shader) { name = "UC_TextureMemoryMaterial" };
                AssetDatabase.CreateAsset(material, folder + "/UC_TextureMemoryMaterial.mat");
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 10, 32) };
                terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(data);
                var terrain = terrainObject.GetComponent<UnityEngine.Terrain>();
                terrain.materialTemplate = material;
                var surface = new SurfaceProduct { splat = new SplatProduct() };
                surface.textures[UCShaderProps.DetailTex] = generated;
                surface.splat.layers[0] = new LayerProduct { albedo = generated, mask = mask };
                tile.outputs.surface = surface;
                tile.outputs.surfaceOutputFolder = folder;
                tile.outputs.surfaceShaderTarget = (int)ShaderTarget.ExistingMaterial;
                tile.outputs.surfaceWriteLayers = true;
                Check(UCTextureGraphApplier.Apply(tile.outputs, terrain, memory), "UC-T02-06 generated texture application succeeds through the production applier.");
                Texture2D persisted = material.GetTexture(UCShaderProps.DetailTex) as Texture2D;
                Texture2D layerTexture = data.terrainLayers[0].diffuseTexture;
                Check(persisted != null && AssetDatabase.Contains(persisted) && AssetDatabase.Contains(layerTexture) &&
                      !ReferenceEquals(original, persisted) && ReferenceEquals(surface.textures[UCShaderProps.DetailTex].texture, original),
                    "UC-T02-06 persistent material/layer copies leave the published graph descriptor unchanged.");
                string path = AssetDatabase.GetAssetPath(persisted);
                Check(UCTextureGraphApplier.Apply(tile.outputs, terrain, memory) && ReferenceEquals(persisted, material.GetTexture(UCShaderProps.DetailTex)),
                    "UC-T02-06 repeated texture Apply updates the same persistent asset without leaking replacement identities.");
                tile.ReleaseProductMemory();
                Check(original == null && originalMask == null && persisted != null && layerTexture != null && borrowed != null &&
                      memory.NativeBytes == 0 && memory.ManagedBytes == 0,
                    "UC-T02-06 final evaluation release destroys temporary textures once while renderer assets and borrowed inputs survive.");
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Texture2D reloaded = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                Check(reloaded != null && reloaded.GetPixel(11, 7) == originalPixel,
                    "UC-T02-06 generated texture pixels survive native asset save and reimport after their evaluation is gone.");

                tile = MemoryTestTile(memory); tile.retained = retained;
                var regenerated = (TextureRef)evaluator.PullNode(noise, 0, tile, new StopToken());
                Check(regenerated.texture != null && regenerated.texture.GetPixel(11, 7) == originalPixel,
                    "UC-T02-06 a later evaluation recomputes deterministic pixels instead of returning a destroyed retained texture.");
                var stop = new StopToken(); stop.RequestStop();
                long before = memory.NativeBytes;
                Check(noise.Generate(0, tile, stop) == null && memory.NativeBytes == before,
                    "UC-T02-06 cancelled texture generation allocates no new native resource.");
                tile.outputs.surface = new SurfaceProduct { splat = new SplatProduct() };
                tile.outputs.surface.splat.layers[0] = new LayerProduct { tint = Color.green };
                tile.outputs.surfaceOutputFolder = folder;
                tile.outputs.surfaceShaderTarget = (int)ShaderTarget.ExistingMaterial;
                tile.outputs.surfaceWriteLayers = true;
                UCTextureGraphApplier.Apply(tile.outputs, terrain, memory);
                Texture2D standIn = data.terrainLayers[0].diffuseTexture;
                tile.outputs.surface.splat.layers[0].tint = Color.blue;
                UCTextureGraphApplier.Apply(tile.outputs, terrain, memory);
                Check(AssetDatabase.Contains(standIn) && ReferenceEquals(standIn, data.terrainLayers[0].diffuseTexture) && standIn.GetPixel(0, 0).b > 0.8f,
                    "UC-T02-06 tint-only layers update a persistent stand-in in place instead of growing a native colour cache.");
                AssetDatabase.SetLabels(reloaded, Array.Empty<string>());
                tile.outputs.surface = new SurfaceProduct();
                tile.outputs.surface.textures[UCShaderProps.DetailTex] = regenerated;
                tile.outputs.surfaceOutputFolder = folder;
                tile.outputs.surfaceShaderTarget = (int)ShaderTarget.ExistingMaterial;
                refused = false;
                try { UCTextureGraphApplier.Apply(tile.outputs, terrain, memory); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused && AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null,
                    "UC-T02-06 persistence refuses an asset without the generated-owner marker without deleting it.");
            }
            finally
            {
                tile?.ReleaseProductMemory();
                if (terrainObject != null) UnityEngine.Object.DestroyImmediate(terrainObject);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (borrowed != null) UnityEngine.Object.DestroyImmediate(borrowed);
                if (material != null && !AssetDatabase.Contains(material)) UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(graph);
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC-T02-06 texture fixture teardown releases all admitted transient bytes.");
                if (AssetDatabase.IsValidFolder(folder))
                    Check(AssetDatabase.DeleteAsset(folder), "UC-T02-06 texture fixture removes only its unique asset folder.");
            }
        }
    }
}
