// UC-T07-03: missing analytical patterns share the image allocation owner; existing NoiseTextureNode remains the noise producer.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Analytical image patterns: world-metre checker, UV horizontal gradient or UV centred radial gradient.</summary>
    public enum TextureImagePattern { CheckerMetres = 0, GradientU = 1, RadialUV = 2 }

    /// <summary>Builds a bounded linear colour image with known analytical UV/world-metre patterns.</summary>
    [Serializable]
    public sealed class TexturePatternNode : TextureNode
    {
        [Tooltip("Analytical basis. Checker uses absolute X/Z metres; gradients use normalized UV.")]
        public TextureImagePattern pattern;
        [Tooltip("Width in pixels, 1…2048.")]
        public int width = 256;
        [Tooltip("Height in pixels, 1…2048.")]
        public int height = 256;
        [Tooltip("Positive checker cell size in world metres; the full colour pair repeats every two cells.")]
        public float cellMetres = 8f;
        [Tooltip("Linear-light low colour; alpha remains linear.")]
        public Color low = Color.black;
        [Tooltip("Linear-light high colour; alpha remains linear.")]
        public Color high = Color.white;
        public override string NodeName => "Image Pattern";
        public override string Tooltip => "Analytical colour patterns with explicit UV or absolute world-metre coordinates.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (!Enum.IsDefined(typeof(TextureImagePattern), pattern)) throw new ArgumentException("Unsupported image pattern.");
            TextureImageSampling.RequireFinite(cellMetres); TextureImageSampling.RequireFinite(low); TextureImageSampling.RequireFinite(high);
            if (cellMetres <= 0) throw new ArgumentException("Checker cell size must be positive metres.");
            var descriptor = new TextureImageDescriptor(width, height, TextureImageColourSpace.Linear, TextureImageSemantic.Colour);
            return tile.GenerateTextureImage(descriptor, i =>
            {
                float u = (i % width + 0.5f) / width, v = (i / width + 0.5f) / height;
                float t;
                if (pattern == TextureImagePattern.CheckerMetres)
                {
                    double x = Math.Floor((tile.worldPos.x + u * tile.worldSize.x) / cellMetres);
                    double z = Math.Floor((tile.worldPos.z + v * tile.worldSize.z) / cellMetres);
                    t = (float)((x + z) - Math.Floor((x + z) / 2) * 2);
                }
                else t = pattern == TextureImagePattern.GradientU ? u : Mathf.Clamp01(new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f);
                return Color.LerpUnclamped(low, high, t);
            }, stop);
        }
    }
}
