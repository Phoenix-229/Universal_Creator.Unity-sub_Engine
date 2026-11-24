// UC-T07-04: explicit channels, image blend and resampling; share existing colour equations and the pixel/memory owners.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>RGBA channel selected for explicit extraction or composition.</summary>
    public enum TextureImageChannel { R = 0, G = 1, B = 2, A = 3 }

    /// <summary>Extracts a selected channel into linear Data in R; other RGB channels are zero and alpha is one.</summary>
    [Serializable]
    public sealed class TextureImageChannelNode : TextureNode
    {
        [Tooltip("Channel to extract; values are copied as stored. Convert colour encoding before extracting encoded RGB into linear data.")]
        public TextureImageChannel channel;
        public override string NodeName => "Image Channel";
        public override string Tooltip => "Extract one declared linear channel as red-channel data.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => TexturePorts.ImageIn;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TextureImageProduct input = GetInputImage(tile, 0, stop); if (input == null) return null;
            if (!Enum.IsDefined(typeof(TextureImageChannel), channel) || (input.Descriptor.ColourSpace != TextureImageColourSpace.Linear && channel != TextureImageChannel.A))
                throw new ArgumentException("UC-T07-04: convert encoded RGB before extracting a linear data channel.");
            var d = input.Descriptor;
            return tile.GenerateTextureImage(new TextureImageDescriptor(d.Width, d.Height, TextureImageColourSpace.Linear, TextureImageSemantic.Data,
                d.Mips, d.Filter, d.Wrap), i => new Color(Component(input[i], channel), 0, 0, 1), stop);
        }
        internal static float Component(Color value, TextureImageChannel channel)
        { return channel == TextureImageChannel.R ? value.r : channel == TextureImageChannel.G ? value.g : channel == TextureImageChannel.B ? value.b : value.a; }
    }

    /// <summary>Combines red channels of up to four same-sized linear images, with explicit output meaning and fallback values.</summary>
    [Serializable]
    public sealed class TextureImageCombineNode : TextureNode
    {
        private static readonly Port[] In =
        {
            new Port("R", PortType.Image, PortDirection.Input), new Port("G", PortType.Image, PortDirection.Input),
            new Port("B", PortType.Image, PortDirection.Input), new Port("A", PortType.Image, PortDirection.Input)
        };
        [Tooltip("Meaning of the composed channels. TerrainMask declares metallic/AO/height/smoothness; no inversion is implicit.")]
        public TextureImageSemantic semantic = TextureImageSemantic.Data;
        [Tooltip("Linear RGBA values used for absent channel images. At least one image supplies dimensions.")]
        public Vector4 defaults = new Vector4(0, 0, 0, 1);
        public override string NodeName => "Combine Image Channels";
        public override string Tooltip => "Combine red-channel images with explicit semantics, matching sizes and linear encoding.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => In;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            var inputs = new TextureImageProduct[4]; TextureImageProduct first = null;
            for (int j = 0; j < inputs.Length; j++) { inputs[j] = GetInputImage(tile, j, stop); if (first == null) first = inputs[j]; }
            if (first == null) return null;
            var d = first.Descriptor;
            foreach (var input in inputs)
                if (input != null && (input.Descriptor.Width != d.Width || input.Descriptor.Height != d.Height || input.Descriptor.ColourSpace != TextureImageColourSpace.Linear))
                    throw new ArgumentException("UC-T07-04: channel composition needs matching dimensions and linear channels; resample/convert explicitly.");
            var descriptor = new TextureImageDescriptor(d.Width, d.Height, TextureImageColourSpace.Linear, semantic, d.Mips, d.Filter, d.Wrap);
            return tile.GenerateTextureImage(descriptor, i => new Color(inputs[0] == null ? defaults.x : inputs[0][i].r,
                inputs[1] == null ? defaults.y : inputs[1][i].r, inputs[2] == null ? defaults.z : inputs[2][i].r,
                inputs[3] == null ? defaults.w : inputs[3][i].r), stop);
        }
    }

    /// <summary>Blends same-contract linear colour/data images using the existing colour math equations and an optional linear red-channel mask.</summary>
    /// <remarks>RGB operation opacity is 0…1; alpha always interpolates separately. Tangent normals/material membership need their own semantic blend.</remarks>
    [Serializable]
    public sealed class TextureImageBlendNode : TextureNode
    {
        private static readonly Port[] In =
        {
            new Port("A", PortType.Image, PortDirection.Input), new Port("B", PortType.Image, PortDirection.Input), new Port("Mask", PortType.Image, PortDirection.Input)
        };
        [Tooltip("Existing Colour Math RGB operation. Alpha is separately interpolated by opacity/mask.")]
        public ColorMathOp operation = ColorMathOp.Lerp;
        [Range(0, 1), Tooltip("Operation opacity, multiplied by optional red-channel linear mask.")]
        public float opacity = 0.5f;
        public override string NodeName => "Blend Images";
        public override string Tooltip => "Blend matching linear images with explicit equations, alpha and red-channel mask.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => In;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TextureImageProduct a = GetInputImage(tile, 0, stop), b = GetInputImage(tile, 1, stop), mask = GetInputImage(tile, 2, stop);
            return a == null || b == null ? null : Blend(a, b, mask, tile, stop);
        }
        internal TextureImageProduct Blend(TextureImageProduct a, TextureImageProduct b, TextureImageProduct mask, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null; // UC-T07-04: cancelled edits never validate or admit obsolete image work.
            a.Descriptor.RequireCompatible(b.Descriptor); var d = a.Descriptor;
            if (d.ColourSpace != TextureImageColourSpace.Linear || (d.Semantic != TextureImageSemantic.Colour && d.Semantic != TextureImageSemantic.Data))
                throw new ArgumentException("Image colour math requires linear colour/data; normal or membership blending must be explicit.");
            if (!Enum.IsDefined(typeof(ColorMathOp), operation)) throw new ArgumentException("Unsupported image blend.");
            TextureImageSampling.RequireFinite(opacity);
            if (opacity < 0 || opacity > 1) throw new ArgumentException("Image blend opacity must be 0…1.");
            if (mask != null && (mask.Descriptor.Width != d.Width || mask.Descriptor.Height != d.Height || mask.Descriptor.ColourSpace != TextureImageColourSpace.Linear ||
                (mask.Descriptor.Semantic != TextureImageSemantic.Data && mask.Descriptor.Semantic != TextureImageSemantic.Membership)))
                throw new ArgumentException("Image blend mask needs same-size linear red-channel Data/Membership.");
            return tile.GenerateTextureImage(d, i =>
            {
                float t = Mathf.Clamp01(opacity) * (mask == null ? 1 : Mathf.Clamp01(mask[i].r)); Color x = a[i], y = b[i];
                float factor = operation == ColorMathOp.Lerp ? 1 : t;
                return new Color(Mathf.LerpUnclamped(x.r, ColorMathNode.Calculate(operation, x.r, y.r, t), factor),
                    Mathf.LerpUnclamped(x.g, ColorMathNode.Calculate(operation, x.g, y.g, t), factor),
                    Mathf.LerpUnclamped(x.b, ColorMathNode.Calculate(operation, x.b, y.b, t), factor), Mathf.LerpUnclamped(x.a, y.a, t));
            }, stop);
        }
    }

    /// <summary>Explicit image resampling/UV transform around the image centre, with declared output size, filter and edge rule.</summary>
    [Serializable]
    public sealed class TextureImageResampleNode : TextureNode
    {
        [Tooltip("Output width in pixels, 1…2048.")]
        public int width = 256;
        [Tooltip("Output height in pixels, 1…2048.")]
        public int height = 256;
        [Tooltip("Nonzero normalized-UV scale; negative values explicitly mirror the image.")]
        public Vector2 scale = Vector2.one;
        [Tooltip("Sampling offset in normalized UV units.")]
        public Vector2 offset;
        [Tooltip("Rotation around UV (0.5,0.5), degrees.")]
        public float rotationDegrees;
        [Tooltip("CPU base-level filter; Point or Bilinear. Trilinear requires a native mip chain.")]
        public TextureFilterKind filter = TextureFilterKind.Bilinear;
        [Tooltip("Explicit transform edge rule.")]
        public TextureWrapKind wrap = TextureWrapKind.Clamp;
        public override string NodeName => "Resample Image";
        public override string Tooltip => "Explicit pixel size/UV conversion with chosen filtering and edge treatment.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => TexturePorts.ImageIn;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TextureImageProduct input = GetInputImage(tile, 0, stop); return input == null ? null : Resample(input, tile, stop);
        }
        internal TextureImageProduct Resample(TextureImageProduct input, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null; // UC-T07-04: cancellation precedes transform validation/allocation.
            TextureImageSampling.RequireFinite(new Color(scale.x, scale.y, offset.x, offset.y)); TextureImageSampling.RequireFinite(rotationDegrees);
            if (scale.x == 0 || scale.y == 0 || filter == TextureFilterKind.Trilinear || input.Descriptor.Semantic == TextureImageSemantic.TangentNormal)
                throw new ArgumentException("Resampling needs nonzero UV scale, a base-level filter and explicit normal conversion/blending.");
            var d = input.Descriptor;
            if (d.ColourSpace == TextureImageColourSpace.SRgb && filter != TextureFilterKind.Point)
                throw new ArgumentException("UC-T07-04: decode sRGB before filtered resampling so interpolation occurs in linear light.");
            var descriptor = new TextureImageDescriptor(width, height, d.ColourSpace, d.Semantic, d.Mips, filter, wrap);
            float radians = rotationDegrees * Mathf.Deg2Rad, cosine = Mathf.Cos(radians), sine = Mathf.Sin(radians);
            return tile.GenerateTextureImage(descriptor, i =>
            {
                float x = ((i % width + 0.5f) / width - 0.5f) / scale.x, y = ((i / width + 0.5f) / height - 0.5f) / scale.y;
                return TextureImageSampling.Sample(input, x * cosine + y * sine + 0.5f + offset.x, -x * sine + y * cosine + 0.5f + offset.y, filter, wrap);
            }, stop);
        }
    }
}
