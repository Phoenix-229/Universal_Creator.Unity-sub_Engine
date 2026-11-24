// UC-T07-08: one transient image/material preview; native objects and captured inputs share explicit retirement.
using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Editor.Drawing;

namespace UniverseCreator.Editor.Utilities
{
    internal sealed class GraphTexturePreview : IDisposable
    {
        internal float[] Field;
        internal Color[] Pixels;
        internal TextureImageColourSpace Encoding;
        internal TextureImageSemantic Semantic;
        internal SurfaceProduct Surface;
        internal TileData Tile;
        internal Material Material;
        internal UnityEditor.Editor MaterialEditor;
        private ProductMemoryBudget.Lease materialMemory; // UC-T07-10: transient material allocation stays charged until its final preview consumer retires.

        internal static GraphTexturePreview Sample(object product, TileData tile)
        {
            var result = new GraphTexturePreview();
            if (product is Matrix matrix) result.Field = GraphPreviewScheduler.SampleField(matrix);
            else if (product is TextureImageProduct image)
            {
                result.Encoding = image.Descriptor.ColourSpace; result.Semantic = image.Descriptor.Semantic;
                int res = GraphPreviewScheduler.Resolution;
                result.Pixels = new Color[res * res];
                for (int y = 0; y < res; y++) for (int x = 0; x < res; x++)
                    result.Pixels[y * res + x] = TextureImageSampling.Sample(image, (x + 0.5f) / res, (y + 0.5f) / res,
                        TextureFilterKind.Bilinear, TextureWrapKind.Clamp);
            }
            else if (product is SurfaceProduct surface) result.Surface = surface.Copy();
            else if (product is LayerProduct layer)
            { result.Surface = new SurfaceProduct { splat = new SplatProduct() }; result.Surface.splat.layers[0] = layer.Copy(); }
            else if (product is SplatProduct splat) result.Surface = new SurfaceProduct { splat = splat.Copy() };
            else if (product is TextureRef reference)
            {
                // Native sampling stays on the main thread. Unreadable imports retain a useful material preview
                // without a blocking readback; known normal/data semantics are never displayed as colour.
                result.Surface = new SurfaceProduct { splat = new SplatProduct() };
                var previewLayer = new LayerProduct();
                if (reference.ImageDescription?.Semantic == TextureImageSemantic.TangentNormal) previewLayer.normal = reference.Copy();
                else if (reference.ImageDescription?.Semantic == TextureImageSemantic.TerrainMask) previewLayer.mask = reference.Copy();
                else if (reference.ImageDescription == null || reference.ImageDescription.Value.Semantic == TextureImageSemantic.Colour) previewLayer.albedo = reference.Copy();
                else throw new InvalidOperationException("Preview this data texture before Image Upload to inspect its declared channels.");
                result.Surface.splat.layers[0] = previewLayer;
            }
            if (result.Surface != null) result.Tile = tile;
            return result;
        }

        internal void CreateMaterial()
        {
            GraphExecutionGuard.RequireMainThread("Lit material preview");
            if (Surface == null) return;
            if (Material != null) return;
            if (Tile == null) throw new InvalidOperationException("A material preview requires its captured tile/resource owner."); // UC-T07-10: native swatches cannot escape the evaluation lifetime.
            Shader shader = Shader.Find(UCShaderProps.ShaderName);
            if (shader == null) throw new InvalidOperationException("Restore the UniverseCreator/TerrainLit shader to preview this material.");
            if (Surface.bakedMaps.Count + Surface.bakedGradients.Count > 32) throw new InvalidOperationException("Material preview supports at most 32 baked maps/gradients.");
            Surface.ValidateTextureChannels(); Surface.PublishLayerParameters();
            ShaderOutputDescriptor.ValidateFeatureKeywords(Surface);
            materialMemory = Tile.ReserveTextureMemory(4096); // UC-T07-10: explicit estimate; editor/driver overhead still requires native profiling.
            Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = false };
            ShaderOutputDescriptor.ApplyParameters(Surface, Material);
            foreach (var pair in Surface.textures) if (pair.Value?.texture != null && Material.HasProperty(pair.Key))
                Material.SetTexture(pair.Key, pair.Value.texture); // Borrow only; preview never changes imported sampling settings.
            foreach (var pair in Surface.bakedMaps)
            {
                float[] samples = GraphPreviewScheduler.SampleField(pair.Value);
                if (samples == null || !Material.HasProperty(pair.Key)) continue;
                var pixels = new Color[samples.Length];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(samples[i], samples[i], samples[i], 1);
                Material.SetTexture(pair.Key, CreateTexture(pixels, true));
            }
            foreach (var pair in Surface.bakedGradients)
            {
                if (pair.Value == null || !Material.HasProperty(pair.Key)) continue;
                int res = GraphPreviewScheduler.Resolution; var pixels = new Color[res * res];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = pair.Value.Evaluate((i % res) / (float)(res - 1));
                Material.SetTexture(pair.Key, CreateTexture(pixels, true));
            }
            int count = 0; var coverage = new Color(0, 0, 0, 0);
            for (int i = 0; i < 4; i++)
            {
                LayerProduct layer = Surface.splat?.layers[i];
                if (layer == null) continue;
                count++; coverage[i] = 1;
                Material.SetTexture("_Splat" + i, layer.albedo?.texture ?? Texture2D.whiteTexture);
                if (layer.normal?.texture != null) Material.SetTexture("_Normal" + i, layer.normal.texture);
                if (layer.mask?.texture != null) Material.SetTexture("_Mask" + i, layer.mask.texture);
                Material.SetFloat("_LayerHasMask" + i, layer.mask?.texture != null ? 1 : 0);
                Material.SetFloat("_NormalScale" + i, layer.normalStrength);
                Material.SetFloat("_Metallic" + i, layer.metallic); Material.SetFloat("_Smoothness" + i, layer.smoothness);
                Material.SetVector("_DiffuseRemapScale" + i, layer.tint);
                Material.SetVector("_MaskMapRemapOffset" + i, Vector4.zero); Material.SetVector("_MaskMapRemapScale" + i, Vector4.one);
                if (layer.uv != null)
                {
                    Vector2 size = layer.uv.SafeTileSize;
                    Vector2 extent = Tile != null ? new Vector2(Tile.worldSize.x, Tile.worldSize.z) : Vector2.one;
                    Material.SetTextureScale("_Splat" + i, new Vector2(extent.x / size.x, extent.y / size.y));
                    Material.SetTextureOffset("_Splat" + i, new Vector2(-layer.uv.offset.x / size.x, -layer.uv.offset.y / size.y));
                }
            }
            if (count > 0)
            {
                coverage /= count; var pixels = new Color[GraphPreviewScheduler.Resolution * GraphPreviewScheduler.Resolution];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = coverage;
                Material.SetTexture("_Control", CreateTexture(pixels, true)); Material.SetFloat("_NumLayersCount", count);
            }
            UCTerrainShaderGUI.SyncAllKeywords(Material);
            MaterialEditor = UnityEditor.Editor.CreateEditor(Material);
        }

        private Texture2D CreateTexture(Color[] pixels, bool linear)
        {
            int res = GraphPreviewScheduler.Resolution;
            ProductMemoryBudget.Lease native = Tile.ReserveTextureMemory(1024L + res * res * 16L); // UC-T07-10: the existing tile owns every generated swatch map.
            Texture2D texture = null; bool published = false;
            try
            {
                texture = new Texture2D(res, res, TextureFormat.RGBAFloat, false, linear)
                { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                texture.SetPixels(pixels); texture.Apply(false, true); Tile.OwnTexture(texture, native); native = null; published = true; return texture;
            }
            finally { if (!published) TileData.RetireTexture(texture, native); }
        }

        public void Dispose()
        {
            GraphExecutionGuard.RequireMainThread("Material preview retirement"); // UC-T07-10: editor/native consumers retire before releasing their texture inputs.
            if (MaterialEditor != null) UnityEngine.Object.DestroyImmediate(MaterialEditor);
            if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
            materialMemory?.Dispose(); materialMemory = null; MaterialEditor = null; Material = null;
            Tile?.ReleaseProductMemory(); Tile = null; Surface = null; Pixels = null; Field = null;
        }
    }
}
