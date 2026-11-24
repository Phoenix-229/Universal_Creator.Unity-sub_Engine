// UC-T07-02: exercise actual pixel admission/upload/lifetime owners, including rejection and cancellation.
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTextureImageContracts()
        {
            var memory = new ProductMemoryBudget(1024 * 1024);
            TileData tile = MemoryTestTile(memory);
            IDisposable left = null, right = null;
            try
            {
                var descriptor = new TextureImageDescriptor(2, 2, TextureImageColourSpace.SRgb, TextureImageSemantic.Colour);
                var source = new[] { new Color(0.5f, 0.25f, 0.75f, 0.4f), Color.red, Color.green, Color.blue };
                TextureImageProduct image = tile.CreateTextureImage(descriptor, source);
                source[0] = Color.black;
                Check(image[0].r == 0.5f && image[0].a == 0.4f && image.Descriptor.Format == TextureFormat.RGBAFloat &&
                    memory.ManagedBytes == descriptor.ManagedBytes && ReferenceEquals(image, PortalRouting.CopyProduct(image)),
                    "UC-T07-02 captured pixels are immutable, independently owned and charged once while portal consumers share the same image.");
                bool size = false, semantic = false, invalid = false;
                try { descriptor.RequireCompatible(new TextureImageDescriptor(1, 2, TextureImageColourSpace.SRgb, TextureImageSemantic.Colour)); }
                catch (ArgumentException) { size = true; }
                try { descriptor.RequireCompatible(new TextureImageDescriptor(2, 2, TextureImageColourSpace.Linear, TextureImageSemantic.Data)); }
                catch (ArgumentException) { semantic = true; }
                try { new TextureImageDescriptor(0, 2, TextureImageColourSpace.Linear, TextureImageSemantic.Colour); }
                catch (ArgumentException) { invalid = true; }
                Check(size && semantic && invalid, "UC-T07-02 incompatible sizes/encodings/channels and invalid dimensions require explicit conversion.");
                long before = memory.ManagedBytes;
                bool missing = false, nonfinite = false;
                try { tile.CreateTextureImage(descriptor, null); } catch (ArgumentException) { missing = true; }
                try { tile.GenerateTextureImage(descriptor, _ => new Color(float.NaN, 0, 0, 1)); } catch (ArgumentException) { nonfinite = true; }
                var stop = new StopToken(); stop.RequestStop();
                Check(missing && nonfinite && tile.CreateTextureImage(descriptor, source, stop) == null && memory.ManagedBytes == before,
                    "UC-T07-02 null/nonfinite/cancelled image capture releases its reservation without publishing pixels.");
                var tiny = new ProductMemoryBudget(8); TileData refusedTile = MemoryTestTile(tiny);
                bool refused = false, sampled = false;
                try { refusedTile.GenerateTextureImage(descriptor, _ => { sampled = true; return Color.white; }); }
                catch (InvalidOperationException) { refused = true; }
                finally { refusedTile.ReleaseProductMemory(); }
                Check(refused && !sampled && tiny.ManagedBytes == 0, "UC-T07-02 oversized pixels are rejected before sampling or buffer allocation.");

                Task<bool> worker = Task.Run(() =>
                {
                    try { TextureImageUploadNode.Upload(image, tile, null); return false; }
                    catch (InvalidOperationException) { return true; }
                });
                Check(worker.Wait(5000) && worker.Result, "UC-T07-02 image upload rejects worker execution before native APIs.");
                TextureRef native = TextureImageUploadNode.Upload(image, tile, null);
                Color uploaded = native.texture.GetPixel(0, 0);
                Check(Mathf.Abs(uploaded.r - Mathf.GammaToLinearSpace(0.5f)) < 0.0001f && Mathf.Abs(uploaded.a - 0.4f) < 0.0001f &&
                    native.ImageDescription.Value.ColourSpace == TextureImageColourSpace.Linear && image[0].r == 0.5f,
                    "UC-T07-02 explicit upload decodes sRGB once, keeps linear alpha and leaves CPU input unchanged.");
                var layer = new LayerProduct { normal = native };
                bool wrongChannel = false;
                try { layer.ValidateTextureChannels(); } catch (ArgumentException) { wrongChannel = true; }
                layer.normal = null; layer.albedo = native; layer.ValidateTextureChannels();
                var surface = new SurfaceProduct { splat = new SplatProduct(), ShaderOutput = new ShaderOutputDescriptor(ShaderTarget.UniverseCreatorTerrainLit) };
                surface.splat.layers[0] = layer;
                SurfaceProduct sibling = surface.Copy(); sibling.splat.layers[0].albedo.wrap = TextureWrapKind.Repeat;
                Check(wrongChannel && native.wrap == TextureWrapKind.Clamp && ReferenceEquals(surface.ShaderOutput, sibling.ShaderOutput),
                    "UC-T07-02 material slots reject explicit wrong channels while sibling metadata and immutable shader contracts remain independent.");
                left = native.AcquireConsumer(); right = sibling.splat.layers[0].albedo.AcquireConsumer();
                Texture2D texture = native.texture; long liveBytes = memory.NativeBytes;
                tile.ReleaseProductMemory();
                Check(texture != null && memory.NativeBytes == liveBytes && memory.ManagedBytes == 0,
                    "UC-T07-02 two native consumers keep one allocation alive and charged after the evaluation tile releases.");
                left.Dispose(); left = null;
                Check(texture != null && memory.NativeBytes == liveBytes, "UC-T07-02 retiring the first consumer preserves the remaining texture consumer.");
                right.Dispose(); right.Dispose(); right = null;
                Check(texture == null && memory.NativeBytes == 0, "UC-T07-02 final consumer retirement destroys one generated texture and releases its native bytes once.");
                bool retired = false;
                try { native.AcquireConsumer(); } catch (InvalidOperationException) { retired = true; }
                Check(retired, "UC-T07-02 a retired generated texture cannot be reacquired through a stale descriptor.");
            }
            finally
            {
                left?.Dispose(); right?.Dispose(); tile.ReleaseProductMemory();
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC-T07-02 image fixture retires every admitted allocation.");
            }
            TestFloatImagePersistence();
        }

        private static void TestFloatImagePersistence()
        {
            string folder = "Assets/UC_FloatImage_" + Guid.NewGuid().ToString("N");
            var memory = new ProductMemoryBudget(1024 * 1024);
            TileData tile = MemoryTestTile(memory);
            Material material = null; TerrainData data = null; GameObject root = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                Shader shader = Shader.Find(UCShaderProps.ShaderName);
                Check(shader != null, "UC-T07-02 float persistence fixture resolves the actual UC terrain shader.");
                if (shader == null) return;
                material = new Material(shader); AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 10, 32) };
                root = UnityEngine.Terrain.CreateTerrainGameObject(data);
                var terrain = root.GetComponent<UnityEngine.Terrain>(); terrain.materialTemplate = material;
                var descriptor = new TextureImageDescriptor(2, 1, TextureImageColourSpace.Linear, TextureImageSemantic.Colour);
                TextureImageProduct image = tile.GenerateTextureImage(descriptor, _ => new Color(2.5f, 0.123456f, 0.3f, 0.4f));
                TextureRef native = TextureImageUploadNode.Upload(image, tile, null);
                tile.outputs.surface = new SurfaceProduct(); tile.outputs.surface.textures[UCShaderProps.DetailTex] = native;
                tile.outputs.surfaceOutputFolder = folder; tile.outputs.surfaceWriteLayers = false;
                tile.outputs.surfaceShaderTarget = (int)ShaderTarget.ExistingMaterial;
                bool applied = UniverseCreator.Editor.Utilities.UCTextureGraphApplier.Apply(tile.outputs, terrain, memory, out SurfaceProduct accepted);
                TextureRef persisted = accepted?.textures[UCShaderProps.DetailTex];
                tile.ReleaseProductMemory();
                Check(applied && persisted != null && persisted.texture != null && persisted.texture.format == TextureFormat.RGBAFloat &&
                    persisted.texture.mipmapCount == 1 && persisted.texture.GetPixel(0, 0).r == 2.5f &&
                    Mathf.Abs(persisted.texture.GetPixel(0, 0).g - 0.123456f) < 0.000001f && persisted.AcquireConsumer() == null,
                    "UC-T07-02 production persistence preserves float HDR/data precision and base-only mips while transferring native lifetime to the asset owner.");
            }
            finally
            {
                tile.ReleaseProductMemory();
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (material != null && !AssetDatabase.Contains(material)) UnityEngine.Object.DestroyImmediate(material);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC-T07-02 float persistence fixture releases its transient allocations.");
            }
        }
    }
}
