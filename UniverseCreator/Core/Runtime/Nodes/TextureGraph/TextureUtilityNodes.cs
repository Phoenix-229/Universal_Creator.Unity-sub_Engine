// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TextureUtilityNodes.cs
//  Purpose: Texture Graph T15-T18. The small parts: UV transforms, sampling
//           intent, colour arithmetic, and a procedural texture baker.
//
//  WHY THESE EXIST AT ALL
//  ----------------------
//  Every value these nodes produce could be typed into a field on the node that
//  consumes it. The reason to make them nodes is reuse: one UV Scaler wired into
//  six layers retiles a whole material from one place, and one Noise Texture feeds
//  the albedo of three layers without three copies of the same seed drifting apart
//  over a month of edits. A field is faster to author once; a node is faster to
//  change forever, which is the side of that trade a terrain tool wants.
//
//  T18 is the only node in this file that costs anything. It bakes a real
//  Texture2D on the main thread, so it is written to be lazy about it - the bake
//  is cached in the product cache like every other node's output, and a slider
//  drag that does not touch T18 does not re-bake it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    // -------------------------------------------------------------------------
    //  T15 - UV Scaler
    // -------------------------------------------------------------------------

    /// <summary>
    /// A tiling, offset and rotation that several layers can share.
    ///
    /// Tiling is the world size of one tile in METRES, not a repeat count. That
    /// choice is worth restating here because this is the node where a user is
    /// most likely to expect a repeat count: a repeat count means something
    /// different on every terrain size, so changing the world from 1 km to 4 km
    /// would silently quadruple the scale of every material authored against it.
    /// Metres survive the change.
    ///
    /// Rotation is applied about the tile centre, so rotating does not also
    /// translate the pattern - which it would if the pivot were the tile corner,
    /// and which is the reason a rotated texture usually appears to slide.
    /// </summary>
    [System.Serializable]
    public class UVScalerNode : TextureNode
    {
        public override string NodeName { get { return "UV Scaler"; } }
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "A shared tiling / offset / rotation, in metres. Wire it into any layer's UV port."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("UV", PortType.UV, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.UVOut; }

        [Tooltip("World size of one tile, in metres. 8 means the texture repeats every 8 m.")]
        public Vector2 tileSizeMetres = new Vector2(8f, 8f);

        [Tooltip("Shifts the tiling origin, in metres.")]
        public Vector2 offsetMetres = Vector2.zero;

        [Tooltip("Rotates the tiling about the tile centre, in degrees.")]
        public float rotationDegrees;

        [Tooltip("Multiply an upstream UV rather than replacing it. Chains two scalers.")]
        public bool multiplyUpstream;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            UVTransform upstream = GetInputUV(tile, 0, stop);
            UVTransform uv = new UVTransform();

            if (upstream != null && multiplyUpstream)
            {
                // Componentwise, because tiling is a size: doubling X and leaving
                // Y alone is a legitimate thing to want on a striped material.
                uv.tileSizeMetres = new Vector2(
                    upstream.tileSizeMetres.x * tileSizeMetres.x,
                    upstream.tileSizeMetres.y * tileSizeMetres.y);
                uv.offset = upstream.offset + offsetMetres;
                uv.rotationDegrees = upstream.rotationDegrees + rotationDegrees;
            }
            else
            {
                uv.tileSizeMetres = tileSizeMetres;
                uv.offset = offsetMetres;
                uv.rotationDegrees = rotationDegrees;
            }

            return uv;
        }
    }

    // -------------------------------------------------------------------------
    //  T16 - Texture Sampler
    // -------------------------------------------------------------------------

    /// <summary>
    /// A texture plus how it should be read.
    ///
    /// WHY SAMPLING INTENT TRAVELS WITH THE TEXTURE
    /// --------------------------------------------
    /// The correct filter and wrap for a texture are properties of what it MEANS,
    /// not of the slot it lands in. A gradient LUT must clamp or its two ends
    /// bleed into each other and the highest altitude takes the colour of the
    /// lowest. A tiling ground albedo must repeat. A pixel-art texture must use
    /// point filtering wherever it is used, every time. Attaching the intent here
    /// means the answer is decided once, by whoever knows, rather than re-derived
    /// at each of the four places the texture is consumed.
    ///
    /// MIP BIAS, AND WHY IT IS NOT A SHARPNESS SLIDER
    /// ----------------------------------------------
    /// Negative bias picks a higher-resolution mip than the hardware calculated,
    /// which does look sharper in a still screenshot and aliases in motion,
    /// because the hardware's choice was the one that matched the pixel footprint.
    /// The honest use is small positive bias on a noisy detail texture to stop it
    /// crawling. -0.5 is about as far negative as is defensible; the field is
    /// clamped to +/-2 so a typo cannot pin the whole terrain to mip 0 and blow
    /// the texture cache.
    /// </summary>
    [System.Serializable]
    public class TextureSamplerNode : TextureNode
    {
        public override string NodeName { get { return "Texture Sampler"; } }
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "A texture plus its filter, wrap and mip bias. Wire into any texture port."; }
        }

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return TexturePorts.TextureOut; }

        [Tooltip("The texture.")]
        public Texture2D texture;

        [Tooltip("Point for pixel art, Bilinear for most things, Trilinear when mips visibly pop.")]
        public TextureFilterKind filterMode = TextureFilterKind.Bilinear;

        [Tooltip("Repeat for tiling ground. Clamp for gradients and masks that must not wrap.")]
        public TextureWrapKind wrapMode = TextureWrapKind.Repeat;

        [Tooltip("Shifts which mip is chosen. Positive is blurrier and stabler; negative aliases.")]
        public float mipBias;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (texture == null) return null;

            return new TextureRef(texture)
            {
                filter = filterMode,
                wrap = wrapMode,
                mipBias = Mathf.Clamp(mipBias, -2f, 2f)
            };
        }
    }

    // -------------------------------------------------------------------------
    //  T17 - Colour Math
    // -------------------------------------------------------------------------

    public enum ColorMathOp
    {
        Add = 0,
        Subtract = 1,
        Multiply = 2,
        /// <summary>1 - (1-a)(1-b). Brightens, never clips past white.</summary>
        Screen = 3,
        /// <summary>Multiply in shadow, screen in highlight. Preserves contrast.</summary>
        Overlay = 4,
        Lerp = 5,
        Min = 6,
        Max = 7,
        Difference = 8
    }

    /// <summary>
    /// Arithmetic on two colours.
    ///
    /// Operates per channel including alpha, and does NOT clamp the result. That
    /// is deliberate: an Add that saturates at 1 quietly destroys the headroom an
    /// emissive colour needs, and the consumers that require a 0-1 value clamp at
    /// the point where the requirement exists. Clamping in the middle of a chain
    /// is a lossy operation applied where nobody asked for it.
    ///
    /// Overlay is the one operation people misremember. It is not "multiply and
    /// screen averaged": it selects between them per channel by whether the base
    /// is below or above 0.5, which is what makes it preserve the base's shadows
    /// and highlights instead of flattening toward mid grey.
    /// </summary>
    [System.Serializable]
    public class ColorMathNode : TextureNode
    {
        public override string NodeName { get { return "Colour Math"; } }
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "Add, multiply, screen, overlay, lerp and friends on two colours."; }
        }

        public override Port[] GetInputs() { return TexturePorts.TwoColorIn; }
        public override Port[] GetOutputs() { return TexturePorts.ColorOut; }

        [Tooltip("The operation. Overlay preserves the base's contrast; Multiply only darkens.")]
        public ColorMathOp operation = ColorMathOp.Multiply;

        [Tooltip("Used when input A is not wired.")]
        public Color a = Color.white;

        [Tooltip("Used when input B is not wired.")]
        public Color b = Color.white;

        [Tooltip("Blend factor for Lerp. 0 is all A, 1 is all B.")]
        public float lerpFactor = 0.5f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Color ca = GetInputColorOr(tile, 0, stop, a);
            Color cb = GetInputColorOr(tile, 1, stop, b);

            return new Color(
                Apply(ca.r, cb.r),
                Apply(ca.g, cb.g),
                Apply(ca.b, cb.b),
                Apply(ca.a, cb.a));
        }

        private float Apply(float x, float y)
        {
            switch (operation)
            {
                case ColorMathOp.Add: return x + y;
                case ColorMathOp.Subtract: return x - y;
                case ColorMathOp.Multiply: return x * y;
                case ColorMathOp.Screen: return 1f - (1f - x) * (1f - y);
                case ColorMathOp.Overlay:
                    return x < 0.5f
                        ? 2f * x * y
                        : 1f - 2f * (1f - x) * (1f - y);
                case ColorMathOp.Lerp: return InterpolationMath.Lerp(x, y, Mathf.Clamp01(lerpFactor));
                case ColorMathOp.Min: return x < y ? x : y;
                case ColorMathOp.Max: return x > y ? x : y;
                case ColorMathOp.Difference: return x > y ? x - y : y - x;
                default: return x;
            }
        }
    }

    // -------------------------------------------------------------------------
    //  T18 - Noise Texture
    // -------------------------------------------------------------------------

    public enum NoiseTextureType
    {
        Perlin = 0,
        Simplex = 1,
        Voronoi = 2,
        White = 3,
        Ridged = 4,
        Billow = 5
    }

    public enum NoiseTextureOutput
    {
        /// <summary>Greyscale. Detail overlays, roughness variation, masks.</summary>
        Greyscale = 0,
        /// <summary>Three decorrelated channels. Use as a colour-variation source.</summary>
        RGB = 1,
        /// <summary>Packed as a tangent-space normal derived from the noise slope.</summary>
        NormalMap = 2
    }

    /// <summary>
    /// Bakes a tiling procedural texture to a real Texture2D.
    ///
    /// WHY BAKE RATHER THAN EVALUATE IN THE SHADER
    /// -------------------------------------------
    /// Six octaves of Perlin per pixel per frame is roughly a hundred ALU
    /// operations on every terrain pixel forever. One texture fetch is a handful.
    /// Baking trades memory - 512x512 RGBA is 1 MB - for a cost that would
    /// otherwise be paid sixty times a second for the life of the application.
    /// The only reason not to bake is when the pattern must not repeat, and this
    /// node is explicitly for patterns that tile.
    ///
    /// HOW IT TILES SEAMLESSLY
    /// -----------------------
    /// Plain fBm sampled over a square does not wrap: the left edge and the right
    /// edge are unrelated, so the seam shows as a hard line every tile. The fix
    /// used here is the pairwise wrap - blend each sample with the same noise
    /// sampled one full texture width back, weighted by position across the tile:
    ///
    ///     f(u,v) = bilinear( n(x,y), n(x-R,y), n(x,y-R), n(x-R,y-R), u, v )
    ///
    /// At u = 0 that returns n(x,y); at u = 1 - which is the same pixel as x = R -
    /// it returns n(0,y), the value the left edge had. The edges are equal by
    /// construction rather than by blurring, so there is no seam to find.
    ///
    /// Four samples instead of one at bake time, and zero cost afterwards.
    ///
    /// MAIN THREAD
    /// -----------
    /// new Texture2D and SetPixels are Unity API calls. Legal here because the
    /// texture graph runs on the main thread; the equivalent in the height graph
    /// would deadlock the editor, which is exactly why the two graphs are separate.
    /// </summary>
    [System.Serializable]
    public class NoiseTextureNode : TextureNode
    {
        public override string NodeName { get { return "Noise Texture"; } }
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "Bakes a seamlessly tiling procedural texture. Cheaper than evaluating noise per pixel."; }
        }

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return TexturePorts.TextureOut; }

        [Tooltip("Which basis. Voronoi gives cells, Ridged gives creases, White gives grain.")]
        public NoiseTextureType noiseType = NoiseTextureType.Perlin;

        [Tooltip("What the texture holds. Normal Map derives a tangent-space normal from the slope.")]
        public NoiseTextureOutput outputMode = NoiseTextureOutput.Greyscale;

        [Tooltip("Texture size in pixels. Rounded to a power of two.")]
        public int resolution = 512;

        [Tooltip("Feature size, as a fraction of the texture. 0.25 fits four features across.")]
        public float scale = 0.25f;

        [Tooltip("Octaves. Each adds finer structure at half the amplitude.")]
        public int detail = 4;

        [Tooltip("Per-octave amplitude falloff. 0.5 is the classic fBm value.")]
        public float persistence = 0.5f;

        [Tooltip("Seed. Change it to get a different pattern at the same settings.")]
        public int seed = 12345;

        [Tooltip("Remaps the result. 1 is linear; above 1 pushes toward black, below toward white.")]
        public float contrast = 1f;

        [Tooltip("Only for Normal Map output: how deep the derived normal reads.")]
        public float normalDepth = 1f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            int res = NextPowerOfTwo(Mathf.Clamp(resolution, 32, 2048));

            float[] field = BuildField(res, stop);
            if (field == null) return null;

            Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true);
            tex.name = "UC_Noise_" + noiseType + "_" + seed;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;

            Color[] pixels = new Color[res * res];

            if (outputMode == NoiseTextureOutput.NormalMap)
                PackNormals(field, pixels, res);
            else if (outputMode == NoiseTextureOutput.RGB)
                PackRGB(field, pixels, res);
            else
                PackGreyscale(field, pixels);

            tex.SetPixels(pixels);
            tex.Apply(true, false);

            // hideFlags.DontSave: this texture is generated from the graph every
            // time the graph runs. Letting it be saved into the scene would put a
            // megabyte of regenerable data in the scene file and leave a second
            // copy behind on every re-evaluation - the exact leak a baking node is
            // most likely to introduce.
            tex.hideFlags = HideFlags.DontSave;

            return new TextureRef(tex)
            {
                filter = TextureFilterKind.Trilinear,
                wrap = TextureWrapKind.Repeat
            };
        }

        // ---- field ---------------------------------------------------------

        private float[] BuildField(int res, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float[] field = new float[res * res];

            // Period in lattice units. The wrap blend below needs the sample
            // coordinate and the sample coordinate plus one period to land on the
            // same lattice cell shape, so the period is what everything is
            // measured against.
            float period = Mathf.Max(1f, res * Mathf.Clamp(scale, 0.001f, 1f));
            int octaves = Mathf.Clamp(detail, 1, 10);
            float falloff = Mathf.Clamp(persistence, 0.05f, 0.95f);

            float min = float.MaxValue;
            float max = float.MinValue;

            for (int y = 0; y < res; y++)
            {
                if (stop != null && stop.StopRequested) return null;

                int row = y * res;

                for (int x = 0; x < res; x++)
                {
                    float v = SampleTiling(noise, x, y, res, period, octaves, falloff);
                    field[row + x] = v;

                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            // Normalise to the actual range rather than to the theoretical one.
            // Perlin almost never reaches its bounds, so a theoretical normalise
            // produces a texture that uses about 60% of its dynamic range - which
            // then gets stretched back by whoever consumes it, amplifying the
            // 8-bit quantisation that baking introduced.
            float span = max - min;
            if (span < 1e-6f) span = 1f;

            float invSpan = 1f / span;
            float power = Mathf.Max(0.01f, contrast);

            for (int i = 0; i < field.Length; i++)
            {
                float t = (field[i] - min) * invSpan;
                field[i] = power == 1f ? t : Mathf.Pow(t, power);
            }

            return field;
        }

        /// <summary>
        /// Seamless sample. Blends the sample at (x, y) with the samples one full
        /// texture width to the left and one height below, weighted by u and v.
        ///
        /// The identity that makes it work: at u = 0 the blend returns n(x, y),
        /// and at u = 1 it returns n(x - res, y). Since u = 1 IS x = res, and
        /// n(res - res, y) = n(0, y), the right edge evaluates to exactly what the
        /// left edge evaluated to. Same argument in v. The seam is not softened,
        /// it is arithmetically absent.
        ///
        /// Four evaluations instead of one, paid once at bake time.
        /// </summary>
        private float SampleTiling(Noise noise, int x, int y, int res,
                                   float period, int octaves, float falloff)
        {
            float u = x / (float)res;
            float v = y / (float)res;

            float fx = x;
            float fy = y;
            float wx = x - res;   // one period back, not forward: see above
            float wy = y - res;

            float v00 = Basis(noise, fx, fy, period, octaves, falloff);
            float v10 = Basis(noise, wx, fy, period, octaves, falloff);
            float v01 = Basis(noise, fx, wy, period, octaves, falloff);
            float v11 = Basis(noise, wx, wy, period, octaves, falloff);

            return InterpolationMath.Bilinear(v00, v10, v01, v11, u, v);
        }

        private float Basis(Noise noise, float x, float z, float period,
                            int octaves, float falloff)
        {
            switch (noiseType)
            {
                case NoiseTextureType.Voronoi:
                    return noise.Voronoi(x / period, z / period);

                case NoiseTextureType.White:
                    return noise.Random((int)x, (int)z);

                case NoiseTextureType.Simplex:
                    return noise.Fractal(x, z, period, octaves, falloff, 0f, NoiseType.Simplex);

                case NoiseTextureType.Ridged:
                    return noise.Fractal(x, z, period, octaves, falloff, 0f, NoiseType.Ridged);

                case NoiseTextureType.Billow:
                    return noise.Fractal(x, z, period, octaves, falloff, 0f, NoiseType.Billow);

                default:
                    return noise.Fractal(x, z, period, octaves, falloff, 0f, NoiseType.Perlin);
            }
        }

        // ---- packing -------------------------------------------------------

        private static void PackGreyscale(float[] field, Color[] pixels)
        {
            for (int i = 0; i < field.Length; i++)
            {
                float v = field[i];
                pixels[i] = new Color(v, v, v, v);
            }
        }

        /// <summary>
        /// Three channels offset from each other along the field so they are not
        /// the same pattern in three colours. A quarter and a half of the width
        /// are far enough apart that the eye reads them as independent.
        /// </summary>
        private static void PackRGB(float[] field, Color[] pixels, int res)
        {
            int shiftG = res / 4;
            int shiftB = res / 2;

            for (int y = 0; y < res; y++)
            {
                int row = y * res;
                for (int x = 0; x < res; x++)
                {
                    int i = row + x;
                    int ig = row + ((x + shiftG) % res);
                    int ib = row + ((x + shiftB) % res);

                    pixels[i] = new Color(field[i], field[ig], field[ib], 1f);
                }
            }
        }

        /// <summary>
        /// Derives a tangent-space normal from the field's gradient and packs it
        /// the way Unity's DXT5nm expects: X in alpha, Y in green, and the blue
        /// and red channels carrying the rest.
        ///
        /// The sign on Y is negative because texture V runs downward while
        /// tangent-space Y runs up. Getting that backwards produces a normal map
        /// that lights correctly until the sun crosses the zenith and then inverts
        /// every bump into a dent - the hardest normal-map bug to spot, because
        /// half the day it looks right.
        /// </summary>
        private void PackNormals(float[] field, Color[] pixels, int res)
        {
            float depth = Mathf.Max(0.01f, normalDepth);

            for (int y = 0; y < res; y++)
            {
                int row = y * res;
                int rowUp = ((y + 1) % res) * res;
                int rowDown = ((y - 1 + res) % res) * res;

                for (int x = 0; x < res; x++)
                {
                    int xr = (x + 1) % res;
                    int xl = (x - 1 + res) % res;

                    float dx = (field[row + xr] - field[row + xl]) * depth;
                    float dy = (field[rowUp + x] - field[rowDown + x]) * depth;

                    // Normal of the surface z = f(x, y) is (-df/dx, -df/dy, 1).
                    float nx = -dx;
                    float ny = -dy;
                    float nz = 1f;

                    float inv = 1f / Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx *= inv;
                    ny *= inv;
                    nz *= inv;

                    // 0..1 encoding.
                    float ex = nx * 0.5f + 0.5f;
                    float ey = -ny * 0.5f + 0.5f;
                    float ez = nz * 0.5f + 0.5f;

                    pixels[row + x] = new Color(ex, ey, ez, ex);
                }
            }
        }

        private static int NextPowerOfTwo(int v)
        {
            int p = 32;
            while (p < v) p <<= 1;
            return p;
        }
    }
}
