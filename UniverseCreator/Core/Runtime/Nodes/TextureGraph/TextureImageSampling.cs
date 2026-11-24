// UC-T07-03/04: shared pixel-centre sampling, with explicit edge/filter rules rather than silent dimension conversion.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    internal static class TextureImageSampling
    {
        internal static void RequireFinite(float value)
        { if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Texture input must be finite."); }
        internal static void RequireFinite(Color value)
        { RequireFinite(value.r); RequireFinite(value.g); RequireFinite(value.b); RequireFinite(value.a); }

        internal static ulong NativeContentHash(Texture2D texture)
        {
            GraphExecutionGuard.RequireMainThread("Texture pixel content identity");
            if (texture == null) return PreparedRasterInputs.Hash(0, 0, null);
            // Installed Unity 6000.5 CoreModule XML verifies both properties. Import/revision identity avoids full-image hashing/readback.
            ulong hash = PreparedRasterInputs.Hash(texture.width, texture.height, null);
            hash = PreparedRasterInputs.Mix(hash, texture.updateCount);
            hash = PreparedRasterInputs.Mix(hash, (uint)texture.format);
            hash = PreparedRasterInputs.Mix(hash, texture.isReadable ? 1U : 0U);
            string content = texture.imageContentsHash.ToString();
            for (int i = 0; i < content.Length; i++) hash = PreparedRasterInputs.Mix(hash, content[i]);
            return hash;
        }

        internal static Color Sample(TextureImageProduct image, float u, float v, TextureFilterKind filter, TextureWrapKind wrap)
        {
            RequireFinite(u); RequireFinite(v);
            TextureImageDescriptor descriptor = image.Descriptor;
            // Map normalized coordinates before multiplying so distant metre coordinates cannot overflow an integer texel index.
            u = WrapCoordinate(u, wrap); v = WrapCoordinate(v, wrap);
            float x = u * descriptor.Width - 0.5f, y = v * descriptor.Height - 0.5f;
            if (filter == TextureFilterKind.Point)
                return Pixel(image, Mathf.FloorToInt(x + 0.5f), Mathf.FloorToInt(y + 0.5f), wrap);
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            Color a = Color.LerpUnclamped(Pixel(image, ix, iy, wrap), Pixel(image, ix + 1, iy, wrap), x - ix);
            Color b = Color.LerpUnclamped(Pixel(image, ix, iy + 1, wrap), Pixel(image, ix + 1, iy + 1, wrap), x - ix);
            return Color.LerpUnclamped(a, b, y - iy);
        }

        private static float WrapCoordinate(float value, TextureWrapKind wrap)
        {
            if (wrap == TextureWrapKind.Clamp) return Mathf.Clamp01(value);
            double period = wrap == TextureWrapKind.Mirror ? 2 : 1;
            double reduced = value - Math.Floor(value / period) * period;
            return (float)(wrap == TextureWrapKind.Mirror && reduced > 1 ? 2 - reduced : reduced);
        }

        private static Color Pixel(TextureImageProduct image, int x, int y, TextureWrapKind wrap)
        { return image[Index(y, image.Descriptor.Height, wrap) * image.Descriptor.Width + Index(x, image.Descriptor.Width, wrap)]; }
        internal static int Index(int value, int count, TextureWrapKind wrap) // UC-T07-04: shared edge rules also apply to separable pixel scratch.
        {
            if (wrap == TextureWrapKind.Clamp) return Mathf.Clamp(value, 0, count - 1);
            int period = wrap == TextureWrapKind.Mirror ? count * 2 : count;
            int index = value % period; if (index < 0) index += period;
            return wrap == TextureWrapKind.Mirror && index >= count ? period - 1 - index : index;
        }
    }
}
