// UC-T07-04: separable image filtering with explicit edge/alpha rules, admitted scratch and a bounded cancellable tap count.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Filters linear colour/data/membership images with a separable Gaussian; normals/HSV require semantic operations.</summary>
    /// <remarks>One scratch RGBA plane plus one owned output; radius is ceil(3×sigma), at most 24. Colour alpha is retained unless
    /// filterAlpha is selected; non-colour channels all filter independently. The declared image wrap rule handles borders.</remarks>
    [Serializable]
    public sealed class TextureImageBlurNode : TextureNode
    {
        [Range(0, 8), Tooltip("Gaussian sigma in pixels, 0…8. Below 0.05 returns the immutable source.")]
        public float sigma = 1;
        [Tooltip("Filter colour opacity too; otherwise retain source alpha. Data/membership channels all filter.")]
        public bool filterAlpha;
        public override string NodeName => "Blur Image";
        public override string Tooltip => "Bounded separable Gaussian in linear channels, using the image's edge policy.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => TexturePorts.ImageIn;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TextureImageProduct input = GetInputImage(tile, 0, stop); return input == null ? null : Blur(input, tile, stop);
        }
        internal TextureImageProduct Blur(TextureImageProduct input, TileData tile, StopToken stop)
        {
            TextureImageSampling.RequireFinite(sigma); var d = input.Descriptor;
            if (sigma < 0 || sigma > 8 || d.ColourSpace != TextureImageColourSpace.Linear ||
                d.Semantic == TextureImageSemantic.TangentNormal || d.Semantic == TextureImageSemantic.Hsv)
                throw new ArgumentException("UC-T07-04: blur requires linear scalar/colour channels and sigma 0…8; convert normals/HSV explicitly.");
            if (stop != null && stop.StopRequested) return null;
            if (sigma < 0.05f) return input;
            int radius = Mathf.CeilToInt(sigma * 3);
            if ((long)d.PixelCount * 2 * (radius * 2 + 1) > 128L * 1024 * 1024)
                throw new InvalidOperationException("UC-T07-04: image blur exceeds the 128 Mi tap envelope; reduce resolution or sigma explicitly.");
            using ProductMemoryBudget.Lease scratch = tile.ReserveProductMemory(256L + d.PixelCount * 16L + (radius * 2L + 1) * 4L);
            var horizontal = new Color[d.PixelCount]; var kernel = new float[radius * 2 + 1]; float sum = 0;
            for (int k = -radius; k <= radius; k++) { float value = Mathf.Exp(-k * k / (2f * sigma * sigma)); kernel[k + radius] = value; sum += value; }
            for (int k = 0; k < kernel.Length; k++) kernel[k] /= sum;
            for (int y = 0; y < d.Height; y++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < d.Width; x++)
                {
                    Color value = Color.clear;
                    for (int k = -radius; k <= radius; k++) value += input[y * d.Width + TextureImageSampling.Index(x + k, d.Width, d.Wrap)] * kernel[k + radius];
                    horizontal[y * d.Width + x] = value;
                }
            }
            return tile.GenerateTextureImage(d, i =>
            {
                int x = i % d.Width, y = i / d.Width; Color value = Color.clear;
                for (int k = -radius; k <= radius; k++) value += horizontal[TextureImageSampling.Index(y + k, d.Height, d.Wrap) * d.Width + x] * kernel[k + radius];
                if (!filterAlpha && d.Semantic == TextureImageSemantic.Colour) value.a = input[i].a;
                return value;
            }, stop);
        }
    }
}
