// UC-T07-04: actual pixel adjustments use admitted immutable images; shader Colour Grade remains a separate cheap material owner.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Pixel adjustments and explicit colour/channel conversions; alpha is retained unchanged.</summary>
    public enum TextureImageAdjustment
    {
        Levels = 0, AutoLevels = 1, BrightnessContrast = 2, Luminance = 3, Threshold = 4,
        SRgbToLinear = 5, LinearToSRgb = 6, RgbToHsv = 7, HsvToRgb = 8
    }

    /// <summary>Applies bounded known image adjustments; linear colour/data levels remain distinct from explicit sRGB/HSV conversions.</summary>
    /// <remarks>Auto levels reduces each RGB channel independently, retaining constant channels. Manual levels clamp the input range
    /// before the inverse-gamma curve; alpha is never graded. HDR result clipping is an explicit setting.</remarks>
    [Serializable]
    public sealed class TextureImageAdjustmentNode : TextureNode
    {
        [Tooltip("Levels, per-channel automatic levels, brightness/contrast, linear luminance/threshold or explicit encoding conversion.")]
        public TextureImageAdjustment operation;
        [Tooltip("Manual input black point; high must be greater.")]
        public float low;
        [Tooltip("Manual input white point; must exceed low.")]
        public float high = 1;
        [Tooltip("Positive levels gamma; values above one brighten midtones through exponent 1/gamma.")]
        public float gamma = 1;
        [Tooltip("Linear-light brightness multiplier, nonnegative.")]
        public float brightness = 1;
        [Tooltip("Linear contrast multiplier about 0.5, nonnegative.")]
        public float contrast = 1;
        [Tooltip("Linear Rec.709 luminance threshold; values at or above this become one.")]
        public float threshold = 0.5f;
        [Tooltip("Explicitly clamp adjusted RGB to 0…1. Alpha stays unchanged.")]
        public bool clampResult;
        public override string NodeName => "Image Adjustment";
        public override string Tooltip => "Known pixel levels, grading and explicit colour conversions; alpha is retained.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => TexturePorts.ImageIn;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;

        /// <summary>Builds a new immutable image with explicit output encoding and semantics.</summary>
        /// <param name="outputIndex">Sole image output, zero.</param>
        /// <param name="tile">Required image/memory owner.</param>
        /// <param name="stop">Cancellation checked during reduction and output construction.</param>
        /// <returns>Adjusted image or null for an absent/cancelled input.</returns>
        /// <exception cref="ArgumentException">The operation, parameters or input encoding/channel meaning is incompatible.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TextureImageProduct input = GetInputImage(tile, 0, stop);
            return input == null ? null : Adjust(input, tile, stop);
        }

        internal TextureImageProduct Adjust(TextureImageProduct input, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null; // UC-T07-04: obsolete requests stop before validating a now-unused branch.
            if (!Enum.IsDefined(typeof(TextureImageAdjustment), operation)) throw new ArgumentException("Unsupported image adjustment.");
            TextureImageDescriptor source = input.Descriptor;
            TextureImageSemantic semantic = source.Semantic; TextureImageColourSpace encoding = source.ColourSpace;
            bool colour = semantic == TextureImageSemantic.Colour;
            if (operation == TextureImageAdjustment.SRgbToLinear)
            {
                if (!colour || encoding != TextureImageColourSpace.SRgb) throw new ArgumentException("sRGB conversion requires encoded colour.");
                encoding = TextureImageColourSpace.Linear;
            }
            else if (operation == TextureImageAdjustment.LinearToSRgb)
            {
                if (!colour || encoding != TextureImageColourSpace.Linear) throw new ArgumentException("sRGB encoding requires linear colour.");
                encoding = TextureImageColourSpace.SRgb;
            }
            else if (operation == TextureImageAdjustment.HsvToRgb)
            {
                if (semantic != TextureImageSemantic.Hsv) throw new ArgumentException("HSV conversion requires declared HSV channel data.");
                semantic = TextureImageSemantic.Colour;
            }
            else
            {
                if (encoding != TextureImageColourSpace.Linear || (!colour && semantic != TextureImageSemantic.Data))
                    throw new ArgumentException("UC-T07-04: image grading requires linear colour/data; convert encoding or channels explicitly first.");
                if (operation == TextureImageAdjustment.RgbToHsv || operation == TextureImageAdjustment.Luminance || operation == TextureImageAdjustment.Threshold)
                {
                    if (!colour) throw new ArgumentException("Colour conversion requires linear RGB colour.");
                    semantic = operation == TextureImageAdjustment.RgbToHsv ? TextureImageSemantic.Hsv : TextureImageSemantic.Data;
                }
            }
            TextureImageSampling.RequireFinite(low); TextureImageSampling.RequireFinite(high); TextureImageSampling.RequireFinite(gamma);
            TextureImageSampling.RequireFinite(brightness); TextureImageSampling.RequireFinite(contrast); TextureImageSampling.RequireFinite(threshold);
            if (gamma <= 0 || brightness < 0 || contrast < 0 || (operation == TextureImageAdjustment.Levels && high <= low))
                throw new ArgumentException("UC-T07-04: levels need ordered endpoints/positive gamma and grading multipliers must be nonnegative.");
            Color min = new Color(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 0);
            Color max = new Color(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, 0);
            if (operation == TextureImageAdjustment.AutoLevels)
                for (int i = 0; i < source.PixelCount; i++)
                {
                    if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                    Color p = input[i]; min.r = Mathf.Min(min.r, p.r); min.g = Mathf.Min(min.g, p.g); min.b = Mathf.Min(min.b, p.b);
                    max.r = Mathf.Max(max.r, p.r); max.g = Mathf.Max(max.g, p.g); max.b = Mathf.Max(max.b, p.b);
                }
            var descriptor = new TextureImageDescriptor(source.Width, source.Height, encoding, semantic, source.Mips, source.Filter, source.Wrap);
            return tile.GenerateTextureImage(descriptor, i =>
            {
                Color p = input[i], result = p;
                switch (operation)
                {
                    case TextureImageAdjustment.SRgbToLinear: return p.linear;
                    case TextureImageAdjustment.LinearToSRgb: return p.gamma;
                    case TextureImageAdjustment.RgbToHsv:
                        if (p.r < 0 || p.g < 0 || p.b < 0) throw new ArgumentException("HSV needs nonnegative colour channels.");
                        Color.RGBToHSV(p, out float h, out float s, out float v); return new Color(h, s, v, p.a);
                    case TextureImageAdjustment.HsvToRgb:
                        if (p.g < 0 || p.g > 1 || p.b < 0) throw new ArgumentException("HSV needs saturation 0…1 and nonnegative value.");
                        result = Color.HSVToRGB(Mathf.Repeat(p.r, 1), p.g, p.b, true); result.a = p.a; return result;
                    case TextureImageAdjustment.Luminance:
                    case TextureImageAdjustment.Threshold:
                        float grey = p.r * 0.2126f + p.g * 0.7152f + p.b * 0.0722f;
                        if (operation == TextureImageAdjustment.Threshold) grey = grey >= threshold ? 1 : 0;
                        return new Color(grey, grey, grey, p.a);
                    case TextureImageAdjustment.BrightnessContrast:
                        result.r = ((p.r - 0.5f) * contrast + 0.5f) * brightness;
                        result.g = ((p.g - 0.5f) * contrast + 0.5f) * brightness;
                        result.b = ((p.b - 0.5f) * contrast + 0.5f) * brightness; break;
                    default:
                        bool automatic = operation == TextureImageAdjustment.AutoLevels;
                        result.r = Levels(p.r, automatic ? min.r : low, automatic ? max.r : high);
                        result.g = Levels(p.g, automatic ? min.g : low, automatic ? max.g : high);
                        result.b = Levels(p.b, automatic ? min.b : low, automatic ? max.b : high); break;
                }
                if (clampResult) { result.r = Mathf.Clamp01(result.r); result.g = Mathf.Clamp01(result.g); result.b = Mathf.Clamp01(result.b); }
                return result;
            }, stop);
        }

        private float Levels(float value, float black, float white)
        { return white - black <= 0.000001f ? value : Mathf.Pow(Mathf.Clamp01((value - black) / (white - black)), 1f / gamma); }
    }
}
