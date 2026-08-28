// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    CloudNoiseBaker.cs
//  Purpose: Bake the one seamless noise field the three cloud sheets sample, using
//           the toolkit's own Noise generator rather than a second implementation on
//           the GPU. Three channels carry the shape at three scales - a broad base
//           that coverage thresholds into deck and gap, a mid detail that erodes the
//           base edge into cauliflower, and a fine wisp that draws the high streaks -
//           so one texture fetch in the shader yields a genus's whole silhouette.
//           The field is made to tile by a four-corner toroidal blend, so a domed
//           sheet wraps it with no visible seam however the camera turns.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Bakes the tiling cloud-shape texture the sheets read. The generator is the
    /// shipped <see cref="Noise"/>; this only samples it seamlessly and packs the
    /// three scales into one RGBA texture. Deterministic in the seed, so a world's
    /// sky is the same field on every machine.
    /// </summary>
    public static class CloudNoiseBaker
    {
        // The one baked cloud texture's GPU footprint: a single RGBA32 tile, no mips,
        // packing base/detail/wisp into its channels. Named here so a memory readout
        // prices the exact allocation Bake makes rather than a restated guess.
        public const int ByteSize =
            LightingConstants.CloudNoiseResolution * LightingConstants.CloudNoiseResolution * 4;

        /// <summary>
        /// One channel of the field at (u,v) in [0,1), sampled so opposite edges match.
        /// The four-corner blend evaluates the fractal at the sample and at its three
        /// wrapped neighbours a whole tile away, then bilinearly weights them by the
        /// position in the tile - which forces the value at u=0 to equal u=1 (and the
        /// same in v) by construction, whatever the underlying octaves do.
        /// </summary>
        public static float SeamlessFractal(Noise n, float u, float v, float cells,
                                            int octaves, float detail, float turbulence,
                                            NoiseType type)
        {
            float x = u * cells;
            float z = v * cells;

            float a = n.Fractal(x, z, 1f, octaves, detail, turbulence, type);
            float b = n.Fractal(x - cells, z, 1f, octaves, detail, turbulence, type);
            float c = n.Fractal(x, z - cells, 1f, octaves, detail, turbulence, type);
            float d = n.Fractal(x - cells, z - cells, 1f, octaves, detail, turbulence, type);

            float wu = 1f - u;
            float wv = 1f - v;
            return a * wu * wv + b * u * wv + c * wu * v + d * u * v;
        }

        /// <summary>
        /// The three cloud scales as cell counts across one tile, derived from the bake
        /// resolution and the feature sizes so a coarser sheet and a finer one stay in
        /// proportion when the resolution changes.
        /// </summary>
        public static float BaseCells
        {
            get { return LightingConstants.CloudNoiseResolution / LightingConstants.CloudNoiseBaseFeatureTexels; }
        }
        public static float DetailCells
        {
            get { return LightingConstants.CloudNoiseResolution / LightingConstants.CloudNoiseDetailFeatureTexels; }
        }
        public static float WispCells
        {
            get { return LightingConstants.CloudNoiseResolution / LightingConstants.CloudNoiseWispFeatureTexels; }
        }

        /// <summary>
        /// Bake the RGBA cloud-shape texture: R the broad coverage base (billowed
        /// Perlin, rounded lobes), G the mid erosion detail (billow), B the fine wisp
        /// streaks (ridged). Alpha is left at the base so a shader with no channel
        /// swizzle still reads a sensible shape. Repeat-wrapped and bilinear, linear
        /// colour - it is a shape field, not an sRGB image.
        /// </summary>
        public static Texture2D Bake(int seed)
        {
            int res = LightingConstants.CloudNoiseResolution;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true)
            {
                name = "UC_CloudNoise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };

            var baseN = new Noise(seed);
            var detailN = baseN.WithSubSeed(1);
            var wispN = baseN.WithSubSeed(2);

            float baseCells = BaseCells, detailCells = DetailCells, wispCells = WispCells;
            var pixels = new Color32[res * res];

            for (int j = 0; j < res; j++)
            {
                float v = (float)j / res;
                for (int i = 0; i < res; i++)
                {
                    float u = (float)i / res;

                    float r = SeamlessFractal(baseN, u, v, baseCells, 4, 0.55f, 0.4f, NoiseType.Perlin);
                    float g = SeamlessFractal(detailN, u, v, detailCells, 4, 0.5f, 0.6f, NoiseType.Billow);
                    float b = SeamlessFractal(wispN, u, v, wispCells, 5, 0.6f, 1f, NoiseType.Ridged);

                    pixels[j * res + i] = new Color32(
                        ToByte(r), ToByte(g), ToByte(b), ToByte(r));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        private static byte ToByte(float v01)
        {
            return (byte)(Mathf.Clamp01(v01) * 255f + 0.5f);
        }
    }
}
