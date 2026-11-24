// UC-T07-02: pixel-to-native conversion is explicit, admitted and owned by the existing evaluation tile.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Uploads immutable float RGBA pixels to a readable linear RGBAFloat texture on the main thread.</summary>
    /// <remarks>sRGB colour is decoded once; alpha/data/normal channels remain linear. Native mip/filter/wrap intent is preserved.
    /// The evaluation tile owns the texture; a longer-lived consumer must acquire a TextureRef consumer pin or persist an asset copy.</remarks>
    [Serializable]
    public sealed class TextureImageUploadNode : TextureNode
    {
        public override string NodeName => "Image to Texture";
        public override string Tooltip => "Upload CPU image pixels for material use. Converts sRGB colour to linear and preserves data channels.";
        public override Port[] GetInputs() => TexturePorts.ImageIn;
        public override Port[] GetOutputs() => TexturePorts.TextureOut;

        /// <summary>Converts the connected image without changing its immutable source pixels.</summary>
        /// <param name="outputIndex">The sole output index, zero.</param>
        /// <param name="tile">Evaluation/memory/native owner; required for a connected image.</param>
        /// <param name="stop">Optional cancellation; returns null before publication when stopped.</param>
        /// <returns>Owned native reference, or null for an absent/cancelled image.</returns>
        /// <exception cref="InvalidOperationException">Called on a worker or admitted memory is exhausted.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.RequireMainThread("Image upload");
            TextureImageProduct image = GetInputImage(tile, 0, stop);
            return image == null ? null : Upload(image, tile, stop);
        }

        internal static TextureRef Upload(TextureImageProduct image, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.RequireMainThread("Image upload");
            if (image == null || (stop != null && stop.StopRequested)) return null;
            if (tile == null) throw new ArgumentNullException(nameof(tile));
            TextureImageDescriptor source = image.Descriptor;
            var linear = new TextureImageDescriptor(source.Width, source.Height, TextureImageColourSpace.Linear,
                source.Semantic, source.Mips, source.Filter, source.Wrap);
            using ProductMemoryBudget.Lease staging = tile.ReserveProductMemory(64L + source.PixelCount * 16L);
            ProductMemoryBudget.Lease native = tile.ReserveTextureMemory(source.FloatNativeBytes);
            Texture2D texture = null;
            try
            {
                var pixels = new Color[source.PixelCount];
                for (int i = 0; i < pixels.Length; i++)
                {
                    if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                    Color value = image[i];
                    pixels[i] = source.ColourSpace == TextureImageColourSpace.SRgb ? value.linear : value;
                }
                texture = new Texture2D(source.Width, source.Height, TextureFormat.RGBAFloat,
                    source.Mips == TextureImageMipPolicy.Generate, true) { name = "UC_Image" };
                texture.SetPixels(pixels); texture.Apply(source.Mips == TextureImageMipPolicy.Generate, false);
                if (stop != null && stop.StopRequested) return null;
                var reference = tile.OwnTexture(texture, native);
                texture = null; native = null;
                reference.ImageDescription = linear; reference.filter = source.Filter; reference.wrap = source.Wrap;
                reference.Apply(); return reference;
            }
            finally { TileData.RetireTexture(texture, native); }
        }
    }
}
