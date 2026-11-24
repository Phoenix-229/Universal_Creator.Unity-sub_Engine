using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Utilities
{
    public static partial class UCTextureGraphApplier
    {
        private const string GeneratedTextureLabel = "UC.GeneratedGraphTexture";

        // UC-T02-06: generated textures become independently owned assets before terrain/material consumers receive them.
        // Reuse owned assets in place to preserve references; unrelated authored assets at the same path are refused.
        private static void PersistGeneratedTextures(SurfaceProduct surface, Material material, string folder, ProductMemoryBudget memory)
        {
            string scope = AssetDatabase.GetAssetPath(material) + "|" + material.name;
            foreach (var texture in surface.textures)
                PersistGeneratedTexture(texture.Value, folder, scope + "|property|" + texture.Key, memory);
            if (surface.splat != null)
                for (int i = 0; i < surface.splat.layers.Length; i++)
                {
                    LayerProduct layer = surface.splat.layers[i];
                    if (layer == null) continue;
                    string slot = scope + "|layer|" + i;
                    PersistGeneratedTexture(layer.albedo, folder, slot + "|albedo", memory);
                    PersistGeneratedTexture(layer.normal, folder, slot + "|normal", memory);
                    PersistGeneratedTexture(layer.mask, folder, slot + "|mask", memory);
                }
            if (surface.water != null)
            {
                PersistGeneratedTexture(surface.water.normalMap, folder, scope + "|water|normal", memory);
                PersistGeneratedTexture(surface.water.foamTexture, folder, scope + "|water|foam", memory);
                PersistGeneratedTexture(surface.water.causticsTexture, folder, scope + "|water|caustics", memory);
                PersistGeneratedTexture(surface.water.shorelineTexture, folder, scope + "|water|shoreline", memory);
            }
        }

        private static void PersistGeneratedTexture(TextureRef reference, string folder, string key, ProductMemoryBudget memory)
        {
            if (reference == null || !reference.isGenerated || reference.texture == null) return;
            Texture2D source = reference.texture;
            if (!source.isReadable) throw new InvalidOperationException("A generated graph texture must be readable before persistence.");
            string stem = "UC_Generated_" + Hash128.Compute(key);
            string path = folder + "/" + stem + ".asset";
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            Texture2D target = existing as Texture2D;
            if (existing != null && (target == null || Array.IndexOf(AssetDatabase.GetLabels(existing), GeneratedTextureLabel) < 0 || target.name != stem))
                throw new InvalidOperationException("Generated texture output conflicts with an authored asset: " + path);

            int width = source.width, height = source.height;
            // UC-T07-02: explicit float images keep HDR/data precision and their mip policy; legacy bakes retain their byte format.
            bool floatImage = reference.ImageDescription.HasValue;
            TextureFormat format = floatImage ? reference.ImageDescription.Value.Format : TextureFormat.RGBA32;
            bool mips = !floatImage || reference.ImageDescription.Value.Mips == TextureImageMipPolicy.Generate;
            if (floatImage && (source.format != format || source.width != reference.ImageDescription.Value.Width ||
                source.height != reference.ImageDescription.Value.Height))
                throw new ArgumentException("UC-T07-02: generated native image does not match its persistence contract.");
            using ProductMemoryBudget.Lease staging = memory?.Reserve(64 + (long)width * height * (floatImage ? 16 : 4),
                floatImage ? reference.ImageDescription.Value.FloatNativeBytes : 1024 + (long)width * height * 12);
            Color32[] pixels = floatImage ? null : source.GetPixels32();
            Color[] floatPixels = floatImage ? source.GetPixels() : null;
            bool created = target == null;
            try
            {
                if (created) target = new Texture2D(width, height, format, mips, true);
                else if ((target.width != width || target.height != height || target.format != format ||
                    (width * height > 1 && (target.mipmapCount > 1) != mips)) && !target.Reinitialize(width, height, format, mips))
                    throw new InvalidOperationException("Unable to resize the owned generated texture: " + path);
                target.name = stem;
                if (floatImage) target.SetPixels(floatPixels); else target.SetPixels32(pixels);
                target.Apply(mips, false);
                target.wrapMode = source.wrapMode; target.filterMode = source.filterMode; target.mipMapBias = source.mipMapBias;
                if (created)
                {
                    AssetDatabase.CreateAsset(target, path);
                    AssetDatabase.SetLabels(target, new[] { GeneratedTextureLabel });
                }
                else EditorUtility.SetDirty(target);
                reference.texture = target; reference.isGenerated = false;
                reference.resourceOwner = null; // UC-T07-02: the persistent asset no longer borrows the evaluation's temporary native owner.
            }
            finally
            {
                // Asset ownership replaces transient admission. Never destroy an existing or successfully persisted asset.
                if (created && target != null && !AssetDatabase.Contains(target)) UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
