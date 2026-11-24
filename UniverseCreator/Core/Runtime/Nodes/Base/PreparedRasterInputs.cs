using System;
using System.Runtime.InteropServices;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T01-07: hashes the exact grayscale raster captured for a node's next bake.</summary>
    /// <remarks>Main-thread preparation owns the array; consumers must not mutate it during a bake.</remarks>
    [Serializable]
    internal sealed class PreparedRasterInputs
    {
        /// <summary>Captured width in texels; zero when no raster is available.</summary>
        public int width;
        /// <summary>Captured height in texels; zero when no raster is available.</summary>
        public int height;
        /// <summary>Row-major captured grayscale values, without extra clamping; null for an absent raster.</summary>
        public float[] pixels;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] internal float value;
            [FieldOffset(0)] internal uint bits;
        }

        // UC-T01-07: canonical byte order and float bits avoid per-texel strings/byte arrays.
        internal static ulong Hash(int width, int height, float[] pixels)
        {
            ulong hash = 14695981039346656037UL;
            hash = Mix(hash, unchecked((uint)width));
            hash = Mix(hash, unchecked((uint)height));
            hash = Mix(hash, pixels == null ? uint.MaxValue : (uint)pixels.Length);
            if (pixels != null)
                for (int i = 0; i < pixels.Length; i++)
                {
                    hash = MixFloat(hash, pixels[i]);
                }
            return hash;
        }

        // UC-T01-07: the same exact ordered fingerprint also covers world-course coordinates.
        internal static ulong MixFloat(ulong hash, float value)
        {
            FloatBits sample = new FloatBits { value = value };
            return Mix(hash, value == 0f ? 0U : float.IsNaN(value) ? 0x7FC00000U : sample.bits);
        }

        internal static ulong Mix(ulong hash, uint value)
        {
            unchecked
            {
                for (int shift = 0; shift < 32; shift += 8)
                    hash = (hash ^ ((value >> shift) & 255U)) * 1099511628211UL;
                return hash;
            }
        }
    }
}
