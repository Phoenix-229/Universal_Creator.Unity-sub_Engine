// UC-T07-02: CPU pixels have an immutable, explicit contract separate from borrowed native textures.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>Colour encoding of CPU image samples; alpha always remains linear.</summary>
    public enum TextureImageColourSpace
    {
        /// <summary>Linear-light RGB or linear non-colour data.</summary>
        Linear = 0,
        /// <summary>sRGB-encoded RGB; a conversion is required before a linear float upload.</summary>
        SRgb = 1
    }

    /// <summary>Meaning of image channels, independent of their float RGBA storage.</summary>
    public enum TextureImageSemantic
    {
        /// <summary>RGB colour and linear alpha, possibly HDR in linear encoding.</summary>
        Colour = 0,
        /// <summary>Independent linear data channels with no colour decoding.</summary>
        Data = 1,
        /// <summary>Tangent-space XYZ normal encoded from [-1,1] into RGB [0,1].</summary>
        TangentNormal = 2,
        /// <summary>Signed height in metres in R; remaining channels are explicitly unused.</summary>
        HeightMetres = 3,
        /// <summary>Independent nonnegative material membership weights in RGBA.</summary>
        Membership = 4,
        /// <summary>Linear terrain mask: R metallic, G occlusion, B normalized height, A smoothness.</summary>
        TerrainMask = 5,
        /// <summary>HSV in RGB: wrapped hue 0…1, saturation 0…1, nonnegative HDR value; linear alpha.</summary>
        Hsv = 6
    }

    /// <summary>Native upload mip policy; the CPU payload contains only the base level.</summary>
    public enum TextureImageMipPolicy
    {
        /// <summary>Keep only the supplied base level.</summary>
        BaseOnly = 0,
        /// <summary>Generate the full native mip chain during an explicit upload.</summary>
        Generate = 1
    }

    /// <summary>Immutable dimensions, encoding, channel meaning and sampling intent for float RGBA pixels.</summary>
    /// <remarks>Rows run left-to-right from the bottom row, matching Unity SetPixels. Dimensions are 1…2048;
    /// no native texture or mip data is owned here. A default struct is invalid and rejected by consumers.</remarks>
    public readonly struct TextureImageDescriptor
    {
        /// <summary>Maximum width or height in pixels; byte admission may impose a smaller limit.</summary>
        public const int MaxDimension = 2048;
        /// <summary>Base width in pixels, 1…2048.</summary>
        public int Width { get; }
        /// <summary>Base height in pixels, 1…2048.</summary>
        public int Height { get; }
        /// <summary>RGB encoding; non-colour semantics require Linear.</summary>
        public TextureImageColourSpace ColourSpace { get; }
        /// <summary>Meaning of stored channels; pixels are always four float32 values.</summary>
        public TextureImageSemantic Semantic { get; }
        /// <summary>Storage format: four float32 channels per pixel, independent of colour encoding.</summary>
        public TextureFormat Format => TextureFormat.RGBAFloat;
        /// <summary>Native mip intent; no CPU mip arrays are implied.</summary>
        public TextureImageMipPolicy Mips { get; }
        /// <summary>Sampling filter intent for a generated native texture.</summary>
        public TextureFilterKind Filter { get; }
        /// <summary>Sampling wrap intent for a generated native texture.</summary>
        public TextureWrapKind Wrap { get; }
        /// <summary>Number of base-level RGBA pixels.</summary>
        public int PixelCount => Width * Height;
        /// <summary>Conservative managed payload bytes: sixteen per pixel plus 256 metadata/array bytes.</summary>
        public long ManagedBytes => 256L + PixelCount * 16L;

        /// <summary>Creates a validated immutable image contract without allocating pixels.</summary>
        /// <param name="width">Width in pixels, 1…2048.</param>
        /// <param name="height">Height in pixels, 1…2048.</param>
        /// <param name="colourSpace">Defined RGB encoding; data/normal/height/membership require Linear.</param>
        /// <param name="semantic">Defined channel interpretation.</param>
        /// <param name="mips">Base-only or generated native mips.</param>
        /// <param name="filter">Defined filter; Trilinear requires generated mips.</param>
        /// <param name="wrap">Defined wrap mode.</param>
        /// <exception cref="ArgumentException">Dimensions, enums or encoding/mip combinations are invalid.</exception>
        public TextureImageDescriptor(int width, int height, TextureImageColourSpace colourSpace,
            TextureImageSemantic semantic, TextureImageMipPolicy mips = TextureImageMipPolicy.BaseOnly,
            TextureFilterKind filter = TextureFilterKind.Bilinear, TextureWrapKind wrap = TextureWrapKind.Clamp)
        {
            Width = width; Height = height; ColourSpace = colourSpace; Semantic = semantic;
            Mips = mips; Filter = filter; Wrap = wrap;
            Validate();
        }

        internal void Validate()
        {
            if (Width < 1 || Height < 1 || Width > MaxDimension || Height > MaxDimension ||
                !Enum.IsDefined(typeof(TextureImageColourSpace), ColourSpace) ||
                !Enum.IsDefined(typeof(TextureImageSemantic), Semantic) ||
                !Enum.IsDefined(typeof(TextureImageMipPolicy), Mips) ||
                !Enum.IsDefined(typeof(TextureFilterKind), Filter) || !Enum.IsDefined(typeof(TextureWrapKind), Wrap) ||
                (Semantic != TextureImageSemantic.Colour && ColourSpace != TextureImageColourSpace.Linear) ||
                (Filter == TextureFilterKind.Trilinear && Mips != TextureImageMipPolicy.Generate))
                throw new ArgumentException("UC-T07-02: invalid image dimensions, encoding, semantics or sampling/mip contract.");
        }

        internal long FloatNativeBytes
        {
            get
            {
                long texels = PixelCount;
                if (Mips == TextureImageMipPolicy.Generate)
                    for (int w = Width, h = Height; w > 1 || h > 1;)
                    { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); texels += (long)w * h; }
                return 1024L + texels * 32L; // UC-T07-02: include readable native storage and the device copy, including mips.
            }
        }

        /// <summary>Rejects images that cannot be combined without explicit resampling or semantic conversion.</summary>
        /// <param name="other">Other validated float RGBA base-image contract.</param>
        /// <exception cref="ArgumentException">Dimensions, encoding or channel meanings differ, or either contract is invalid.</exception>
        public void RequireCompatible(TextureImageDescriptor other)
        {
            Validate(); other.Validate();
            if (Width != other.Width || Height != other.Height || ColourSpace != other.ColourSpace || Semantic != other.Semantic)
                throw new ArgumentException("UC-T07-02: images require explicit resampling or colour/channel conversion before combination.");
        }
    }

    /// <summary>Immutable row-major float RGBA image shared by graph consumers without native Unity ownership.</summary>
    /// <remarks>Create through TileData.CreateTextureImage to reserve bytes before allocation. Published buffers
    /// are never exposed; consumer pins use the existing tile/retained/accepted-product lifetime owners.</remarks>
    public sealed class TextureImageProduct
    {
        private readonly Color[] pixels;
        /// <summary>Validated immutable image metadata.</summary>
        public TextureImageDescriptor Descriptor { get; }
        /// <summary>Returns a pixel by value; no mutable array alias is exposed.</summary>
        /// <param name="index">Row-major index, 0…PixelCount-1.</param>
        /// <exception cref="ArgumentOutOfRangeException">Index is outside the base image.</exception>
        public Color this[int index] => index >= 0 && index < pixels.Length ? pixels[index] :
            throw new ArgumentOutOfRangeException(nameof(index));

        private TextureImageProduct(TextureImageDescriptor descriptor, Color[] ownedPixels)
        { Descriptor = descriptor; pixels = ownedPixels; }

        internal static TextureImageProduct Capture(TextureImageDescriptor descriptor, Color[] source, StopToken stop)
        {
            descriptor.Validate();
            if (source == null || source.Length != descriptor.PixelCount)
                throw new ArgumentException("UC-T07-02: image pixel count must match its declared dimensions.", nameof(source));
            if (stop != null && stop.StopRequested) return null;
            var copy = new Color[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                Color value = source[i];
                ValidatePixel(descriptor, value);
                copy[i] = value;
            }
            if (stop != null && stop.StopRequested) return null;
            return new TextureImageProduct(descriptor, copy);
        }

        internal static TextureImageProduct Build(TextureImageDescriptor descriptor, Func<int, Color> sample, StopToken stop)
        {
            descriptor.Validate();
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (stop != null && stop.StopRequested) return null;
            var buffer = new Color[descriptor.PixelCount];
            for (int i = 0; i < buffer.Length; i++)
            {
                if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                Color value = sample(i); ValidatePixel(descriptor, value); buffer[i] = value;
            }
            return stop != null && stop.StopRequested ? null : new TextureImageProduct(descriptor, buffer);
        }

        private static void ValidatePixel(TextureImageDescriptor descriptor, Color value)
        {
            bool unitChannels = descriptor.Semantic == TextureImageSemantic.TerrainMask;
            if (!Finite(value.r) || !Finite(value.g) || !Finite(value.b) || !Finite(value.a) ||
                (descriptor.Semantic == TextureImageSemantic.Membership &&
                    (value.r < 0f || value.g < 0f || value.b < 0f || value.a < 0f)) ||
                ((descriptor.Semantic == TextureImageSemantic.TangentNormal || unitChannels) &&
                    (value.r < 0f || value.r > 1f || value.g < 0f || value.g > 1f || value.b < 0f || value.b > 1f)) ||
                (unitChannels && (value.a < 0f || value.a > 1f)))
                throw new ArgumentException("UC-T07-02: image contains nonfinite or invalid semantic channel data.");
            if (descriptor.Semantic == TextureImageSemantic.Hsv && (value.g < 0 || value.g > 1 || value.b < 0))
                throw new ArgumentException("UC-T07-04: HSV requires saturation 0…1 and nonnegative value.");
        }

        /// <summary>Copies base pixels into a caller-owned upload/scratch buffer without changing encoding.</summary>
        /// <param name="destination">Non-null array with exactly PixelCount entries; reserve its bytes before allocation.</param>
        /// <exception cref="ArgumentException">Destination dimensions do not match.</exception>
        public void CopyPixelsTo(Color[] destination)
        {
            if (destination == null || destination.Length != pixels.Length)
                throw new ArgumentException("Image destination must match the declared pixel count.", nameof(destination));
            Array.Copy(pixels, destination, pixels.Length);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
