// UC-T07-05: physical derivatives and semantic normal blending share immutable pixels and their admitted lifetime.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Converts R-channel metre heights into linear encoded tangent normals with explicit metre texel spacing.</summary>
    /// <remarks>U and V follow increasing image X and Y; a rising height tilts the normal toward negative U/V.
    /// An optional one-texel halo surrounds the core on all sides. Without a halo, Clamp uses one-sided edge derivatives;
    /// Repeat/Mirror use the declared edge policy. Output alpha is one for the shader's RGB/AG normal unpacking contract.</remarks>
    [Serializable]
    public sealed class HeightToNormalNode : TextureNode
    {
        private static readonly Port[] Inputs =
        {
            new Port("Height (m)", PortType.Image, PortDirection.Input),
            new Port("One-texel halo", PortType.Image, PortDirection.Input)
        };
        /// <summary>Positive finite U/V texel spacing in metres, measured between source samples.</summary>
        [Tooltip("Distance between adjacent U/V source samples, in metres; both must be positive.")]
        public Vector2 texelSpacingMetres = Vector2.one;
        /// <summary>Finite nonnegative multiplier on physical height derivatives; one preserves the authored height.</summary>
        [Tooltip("Height derivative multiplier; one preserves physical relief and zero produces a flat normal.")]
        public float strength = 1;
        public override string NodeName => "Height To Normal";
        public override string Tooltip => "Metre heights and metre sample spacing produce correctly scaled tangent normals.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;

        /// <summary>Builds normals from the physical height input and optional surrounding one-texel halo.</summary>
        /// <param name="outputIndex">Sole image output, zero.</param>
        /// <param name="tile">Required image allocation/lifetime owner.</param>
        /// <param name="stop">Optional cancellation checked before validation and throughout output construction.</param>
        /// <returns>Linear tangent-normal image, or null for absent/cancelled height input.</returns>
        /// <exception cref="ArgumentException">Height semantics, spacing, strength or halo dimensions are incompatible.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            var height = GetInputImage(tile, 0, stop);
            return height == null ? null : Convert(height, GetInputImage(tile, 1, stop), tile, stop);
        }

        internal TextureImageProduct Convert(TextureImageProduct height, TextureImageProduct halo, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            var d = height.Descriptor;
            TextureImageSampling.RequireFinite(texelSpacingMetres.x); TextureImageSampling.RequireFinite(texelSpacingMetres.y);
            TextureImageSampling.RequireFinite(strength);
            if (d.Semantic != TextureImageSemantic.HeightMetres || texelSpacingMetres.x <= 0 || texelSpacingMetres.y <= 0 || strength < 0)
                throw new ArgumentException("Height To Normal requires HeightMetres, positive metre spacing and nonnegative strength.");
            if (halo != null && (halo.Descriptor.Semantic != TextureImageSemantic.HeightMetres ||
                halo.Descriptor.Width != d.Width + 2 || halo.Descriptor.Height != d.Height + 2))
                throw new ArgumentException("Height halo must surround the core with exactly one texel on each side.");
            var normal = new TextureImageDescriptor(d.Width, d.Height, TextureImageColourSpace.Linear,
                TextureImageSemantic.TangentNormal, d.Mips, d.Filter, d.Wrap);
            return tile.GenerateTextureImage(normal, i =>
            {
                int x = i % d.Width, y = i / d.Width;
                float dx = Derivative(height, halo, x, y, true), dy = Derivative(height, halo, x, y, false);
                TextureImageSampling.RequireFinite(dx); TextureImageSampling.RequireFinite(dy);
                return TextureNormalBlendNode.Encode(new Vector3(-dx, -dy, 1));
            }, stop);
        }

        private float Derivative(TextureImageProduct height, TextureImageProduct halo, int x, int y, bool horizontal)
        {
            var d = height.Descriptor; int count = horizontal ? d.Width : d.Height, p = horizontal ? x : y;
            float spacing = horizontal ? texelSpacingMetres.x : texelSpacingMetres.y;
            if (halo != null)
            {
                int centre = (y + 1) * halo.Descriptor.Width + x + 1, step = horizontal ? 1 : halo.Descriptor.Width;
                return (halo[centre + step].r - halo[centre - step].r) * strength / (2 * spacing);
            }
            if (count == 1) return 0;
            int lo = p - 1, hi = p + 1; float interval = 2;
            if (d.Wrap == TextureWrapKind.Clamp) { lo = Mathf.Max(0, lo); hi = Mathf.Min(count - 1, hi); interval = hi - lo; }
            else { lo = TextureImageSampling.Index(lo, count, d.Wrap); hi = TextureImageSampling.Index(hi, count, d.Wrap); }
            int a = horizontal ? y * d.Width + lo : lo * d.Width + x;
            int b = horizontal ? y * d.Width + hi : hi * d.Width + x;
            return (height[b].r - height[a].r) * strength / (interval * spacing);
        }
    }

    /// <summary>Coverage mixes separate surfaces; Detail rotates relief from the flat tangent frame onto the base normal.</summary>
    public enum TextureNormalBlend { Coverage = 0, Detail = 1 }

    /// <summary>Blends normalized upper-hemisphere tangent normals with a finite 0…1 amount and independent blend semantics.</summary>
    [Serializable]
    public sealed class TextureNormalBlendNode : TextureNode
    {
        private static readonly Port[] Inputs =
        {
            new Port("Base", PortType.Image, PortDirection.Input), new Port("Detail", PortType.Image, PortDirection.Input)
        };
        /// <summary>Coverage for material membership, or Detail for relief layered over the base.</summary>
        [Tooltip("Coverage mixes surface directions; Detail rotates detail relief onto the base tangent normal.")]
        public TextureNormalBlend mode;
        /// <summary>Finite blend amount, 0…1; zero preserves the base.</summary>
        [Range(0, 1), Tooltip("Coverage or detail amount, 0…1.")]
        public float amount = 0.5f;
        public override string NodeName => "Blend Normals";
        public override string Tooltip => "Normalized tangent-normal coverage or rotated detail; colour blend equations do not apply.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => TexturePorts.ImageOut;

        /// <summary>Builds a normal image from two matching linear tangent-normal images.</summary>
        /// <param name="outputIndex">Sole image output, zero.</param>
        /// <param name="tile">Required image allocation/lifetime owner.</param>
        /// <param name="stop">Optional cooperative cancellation.</param>
        /// <returns>Normalized encoded normal image, or null for missing/cancelled inputs.</returns>
        /// <exception cref="ArgumentException">Inputs, directions, amount or blend mode are incompatible.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            var a = GetInputImage(tile, 0, stop); var b = GetInputImage(tile, 1, stop);
            return a == null || b == null ? null : Blend(a, b, tile, stop);
        }

        internal TextureImageProduct Blend(TextureImageProduct a, TextureImageProduct b, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            a.Descriptor.RequireCompatible(b.Descriptor); TextureImageSampling.RequireFinite(amount);
            if (a.Descriptor.Semantic != TextureImageSemantic.TangentNormal || amount < 0 || amount > 1 || !Enum.IsDefined(typeof(TextureNormalBlend), mode))
                throw new ArgumentException("Normal blend requires matching tangent normals, a supported mode and amount 0…1.");
            return tile.GenerateTextureImage(a.Descriptor, i =>
            {
                Vector3 baseNormal = Decode(a[i]), detail = Decode(b[i]);
                if (mode == TextureNormalBlend.Coverage) return Encode(Vector3.Lerp(baseNormal, detail, amount));
                detail = Normalize(Vector3.Lerp(Vector3.forward, detail, amount));
                // Shortest-arc Rodrigues rotation from +Z to the upper-hemisphere base; denominator is at least one.
                Vector3 axis = new Vector3(-baseNormal.y, baseNormal.x, 0), cross = Vector3.Cross(axis, detail);
                return Encode(detail + cross + Vector3.Cross(axis, cross) / (1 + baseNormal.z));
            }, stop);
        }

        internal static Color Encode(Vector3 normal)
        {
            normal = Normalize(normal);
            if (normal.z < 0) throw new ArgumentException("Combined relief leaves the supported upper tangent hemisphere; reduce detail strength.");
            return new Color(normal.x * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, normal.z * 0.5f + 0.5f, 1);
        }
        internal static Vector3 Decode(Color value)
        {
            Vector3 normal = new Vector3(value.r * 2 - 1, value.g * 2 - 1, value.b * 2 - 1);
            if (normal.sqrMagnitude < 0.000001f || normal.z < 0) throw new ArgumentException("Terrain tangent normals require a nonzero upper-hemisphere direction.");
            return Normalize(normal);
        }

        private static Vector3 Normalize(Vector3 normal)
        {
            TextureImageSampling.RequireFinite(normal.x); TextureImageSampling.RequireFinite(normal.y); TextureImageSampling.RequireFinite(normal.z);
            float scale = Mathf.Max(Mathf.Abs(normal.x), Mathf.Max(Mathf.Abs(normal.y), Mathf.Abs(normal.z)));
            if (scale < 0.000001f) throw new ArgumentException("A normal blend produced an undefined zero direction.");
            return (normal / scale).normalized; // UC-T07-05: scaling avoids overflow for finite but very steep physical derivatives.
        }
    }
}
