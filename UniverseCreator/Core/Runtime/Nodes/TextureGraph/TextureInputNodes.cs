// UC-T07-03: add catalog-confirmed value/pixel/coordinate gaps; retain the existing texture/noise/accepted-world owners.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Reusable finite scalar parameter; changing it does not allocate image pixels.</summary>
    [Serializable]
    public sealed class TextureScalarParameterNode : TextureNode
    {
        [Tooltip("Finite material/math value; units are declared by the consuming node.")]
        public float value;
        public override string NodeName => "Scalar Parameter";
        public override string Tooltip => "Reusable scalar for image operations or material parameters.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => TexturePorts.ScalarOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        { TextureImageSampling.RequireFinite(value); return stop != null && stop.StopRequested ? null : (object)value; }
    }

    /// <summary>Reusable finite RGBA colour constant; its consuming node declares linear/sRGB interpretation.</summary>
    [Serializable]
    public sealed class TextureColourParameterNode : TextureNode
    {
        [Tooltip("RGBA colour; alpha is linear. The consuming image/material node declares RGB encoding.")]
        public Color value = Color.white;
        public override string NodeName => "Colour Parameter";
        public override string Tooltip => "Reusable RGBA colour without allocating an image.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => TexturePorts.ColorOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        { TextureImageSampling.RequireFinite(value); return stop != null && stop.StopRequested ? null : (object)value; }
    }

    /// <summary>Reusable four-component value parameter; vector components are never decoded as colour.</summary>
    [Serializable]
    public sealed class TextureVectorParameterNode : TextureNode
    {
        [Tooltip("Finite XYZW value; the consumer declares its units.")]
        public Vector4 value;
        public override string NodeName => "Vector Parameter";
        public override string Tooltip => "Reusable XYZW shader/math parameter with no colour conversion.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => TexturePorts.VectorOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            TextureImageSampling.RequireFinite(new Color(value.x, value.y, value.z, value.w));
            return stop != null && stop.StopRequested ? null : (object)value;
        }
    }

    /// <summary>Captures readable imported/native texture pixels on the main thread with explicitly authored encoding and channel meaning.</summary>
    /// <remarks>Imported assets are borrowed. No automatic resizing, decompression, GPU readback or colour reinterpretation occurs.
    /// For a described generated reference the authored metadata must match; use explicit conversion/resampling nodes for a different contract.</remarks>
    [Serializable]
    public sealed class TextureImageImportNode : TextureNode, IPreparedGraphInputHash
    {
        private static readonly Port[] In = { new Port("Texture", PortType.Texture, PortDirection.Input) };
        [Tooltip("Imported texture when the Texture input is unwired. Requires readable uncompressed pixels, at most 2048 per side.")]
        public Texture2D texture;
        [Tooltip("Encoding of stored RGB samples; non-colour channels require Linear.")]
        public TextureImageColourSpace colourSpace = TextureImageColourSpace.SRgb;
        [Tooltip("Meaning of the imported RGBA channels; choosing an interpretation is explicit authoring.")]
        public TextureImageSemantic semantic = TextureImageSemantic.Colour;
        [Tooltip("Mip intent for a later Image to Texture upload; CPU image stores only the base level.")]
        public TextureImageMipPolicy mips;
        [Tooltip("Sampling filter for image operations and later upload.")]
        public TextureFilterKind filter = TextureFilterKind.Bilinear;
        [Tooltip("Edge rule for image operations and later upload.")]
        public TextureWrapKind wrap = TextureWrapKind.Clamp;
        public override string NodeName => "Texture to Image";
        public override string Tooltip => "Capture readable texture pixels with declared encoding and channels; no blocking GPU readback.";
        public override Port[] GetInputs() => In;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;

        /// <summary>Hashes the authored fallback texture's native revision; a wired sampler supplies its own dependency hash.</summary>
        /// <returns>Main-thread identity without pixel copies or GPU readback.</returns>
        public ulong GetPreparedInputHash() => TextureImageSampling.NativeContentHash(texture);

        /// <summary>Captures only readable supported base pixels, reserving read/capture buffers before native access.</summary>
        /// <param name="outputIndex">Sole image output, zero.</param>
        /// <param name="tile">Required evaluation/memory owner.</param>
        /// <param name="stop">Optional cancellation, checked before readback and publication.</param>
        /// <returns>Immutable CPU image or null for an absent/cancelled texture.</returns>
        /// <exception cref="ArgumentException">Dimensions, encoding/channel intent or declared input semantics are incompatible.</exception>
        /// <exception cref="InvalidOperationException">Worker execution, unreadable/unsupported pixels or insufficient admitted memory.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.RequireMainThread("Texture image import");
            if (stop != null && stop.StopRequested) return null;
            TextureRef reference = GetInputTexture(tile, 0, stop);
            Texture2D source = reference != null ? reference.texture : texture;
            if (source == null) return null;
            if (!source.isReadable) throw new InvalidOperationException("UC-T07-03: texture image import requires Read/Write enabled; use a readable bake rather than blocking GPU readback.");
            if (source.format != TextureFormat.RGBA32 && source.format != TextureFormat.ARGB32 && source.format != TextureFormat.RGB24 &&
                source.format != TextureFormat.RGBAFloat && source.format != TextureFormat.RGBAHalf && source.format != TextureFormat.RFloat && source.format != TextureFormat.RHalf)
                throw new InvalidOperationException("UC-T07-03: unsupported pixel import format " + source.format + "; explicitly convert the asset to an uncompressed readable format.");
            var descriptor = new TextureImageDescriptor(source.width, source.height, colourSpace, semantic, mips, filter, wrap);
            if (reference?.ImageDescription != null) reference.ImageDescription.Value.RequireCompatible(descriptor);
            using ProductMemoryBudget.Lease readPixels = tile.ReserveProductMemory(64L + descriptor.PixelCount * 16L);
            Color[] pixels = source.GetPixels();
            return tile.CreateTextureImage(descriptor, pixels, stop);
        }
    }

    /// <summary>Converts a spatial Matrix into a declared linear red-channel image without changing its sample units.</summary>
    /// <remarks>Use accepted World Graph Input fields for final ground, native coverage and water masks. HeightMetres requires
    /// normalizedHeightToMetres; category IDs remain Data and need explicit membership selection before blending.</remarks>
    [Serializable]
    public sealed class TextureFieldImageNode : TextureNode
    {
        private static readonly Port[] In = { new Port("Field", PortType.Matrix, PortDirection.Input) };
        [Tooltip("Data for arbitrary fields, HeightMetres for physical height, Membership for nonnegative coverage; no implicit biome decoding.")]
        public TextureImageSemantic semantic = TextureImageSemantic.Data;
        [Tooltip("Convert normalized terrain height using the field's world height and origin; valid only for HeightMetres output.")]
        public bool normalizedHeightToMetres;
        public override string NodeName => "Field to Image";
        public override string Tooltip => "Convert accepted spatial fields/masks into a linear image with explicit red-channel units.";
        public override Port[] GetInputs() => In;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix field = GetInputMap(tile, 0, stop); if (field == null) return null;
            if (semantic != TextureImageSemantic.Data && semantic != TextureImageSemantic.HeightMetres && semantic != TextureImageSemantic.Membership)
                throw new ArgumentException("UC-T07-03: a scalar field cannot implicitly become colour, a normal or a packed material mask.");
            if (normalizedHeightToMetres && semantic != TextureImageSemantic.HeightMetres)
                throw new ArgumentException("UC-T07-03: normalized height conversion requires HeightMetres semantics.");
            var descriptor = new TextureImageDescriptor(field.rect.size.x, field.rect.size.z, TextureImageColourSpace.Linear, semantic);
            return tile.GenerateTextureImage(descriptor, i => new Color(normalizedHeightToMetres ?
                field.worldPos.y + field.arr[i] * field.worldHeight : field.arr[i], 0, 0, 0), stop);
        }
    }

    /// <summary>Coordinate output units: normalized tile UV or absolute world XZ metres.</summary>
    public enum TextureCoordinateSpace { TileUV = 0, WorldMetres = 1 }

    /// <summary>Produces explicitly declared RG coordinates sampled at pixel centres; world mapping remains continuous across negative/world cell origins.</summary>
    [Serializable]
    public sealed class TextureCoordinatesNode : TextureNode
    {
        [Tooltip("Output width in pixels, 1…2048.")]
        public int width = 256;
        [Tooltip("Output height in pixels, 1…2048.")]
        public int height = 256;
        [Tooltip("Tile UV in RG, or absolute world X/Z metres in RG; B and A are unused.")]
        public TextureCoordinateSpace space;
        public override string NodeName => "Texture Coordinates";
        public override string Tooltip => "Pixel-centre UV or absolute X/Z metres; coordinate data has no colour encoding.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (space != TextureCoordinateSpace.TileUV && space != TextureCoordinateSpace.WorldMetres) throw new ArgumentException("Unsupported texture coordinate space.");
            var descriptor = new TextureImageDescriptor(width, height, TextureImageColourSpace.Linear, TextureImageSemantic.Data);
            return tile.GenerateTextureImage(descriptor, i =>
            {
                float u = (i % width + 0.5f) / width, v = (i / width + 0.5f) / height;
                return space == TextureCoordinateSpace.TileUV ? new Color(u, v, 0, 0) :
                    new Color(tile.worldPos.x + u * tile.worldSize.x, tile.worldPos.z + v * tile.worldSize.z, 0, 0);
            }, stop);
        }
    }
}
