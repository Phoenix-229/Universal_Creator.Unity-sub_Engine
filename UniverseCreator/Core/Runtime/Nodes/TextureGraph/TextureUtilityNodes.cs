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
    public class TextureSamplerNode : TextureNode, IPreparedGraphInputHash
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

        /// <summary>UC-T07-03: fingerprints actual native pixel revision/format metadata without CPU/GPU readback.</summary>
        /// <returns>Main-thread stable content identity, including a missing texture.</returns>
        public ulong GetPreparedInputHash() => TextureImageSampling.NativeContentHash(texture);

        /// <summary>UC-T07-10: borrows the selected native texture on the main thread without pixel copies or changing its sampling settings.</summary>
        /// <param name="outputIndex">The sole texture output, zero.</param>
        /// <param name="tile">Evaluation context; no tile allocation occurs.</param>
        /// <param name="stop">Optional cancellation token handled by the evaluator.</param>
        /// <returns>A texture descriptor, or null for a missing native texture.</returns>
        /// <exception cref="System.InvalidOperationException">Called on a worker.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.RequireMainThread("Texture sampler reference"); // UC-T07-10: direct callers must satisfy the native-object owner before even a null comparison.
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
        { return Calculate(operation, x, y, lerpFactor); } // UC-T07-04: constants and image math use the same existing equations.

        internal static float Calculate(ColorMathOp operation, float x, float y, float factor)
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
                case ColorMathOp.Lerp: return InterpolationMath.Lerp(x, y, Mathf.Clamp01(factor));
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
            GraphExecutionGuard.RequireMainThread("Noise texture generation"); // UC-T02-06: native allocation has an explicit evaluation owner.
            if (stop != null && stop.StopRequested) return null;
            using ProductMemoryBudget.Lease scratch = tile?.ReserveProductMemory(64 + (long)res * res * 20);
            ProductMemoryBudget.Lease native = tile?.ReserveTextureMemory(1024 + (long)res * res * 12);
            Texture2D tex = null;
            try
            {

                float[] field = BuildField(res, stop);
                if (field == null) return null;

                tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true);
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

                if (stop != null && stop.StopRequested) return null;
                TextureRef result = tile != null ? tile.OwnTexture(tex, native) : new TextureRef(tex) { isGenerated = true };
                result.filter = TextureFilterKind.Trilinear; result.wrap = TextureWrapKind.Repeat;
                tex = null; native = null;
                return result;
            }
            finally { TileData.RetireTexture(tex, native); }
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

    // -------------------------------------------------------------------------
    //  T19 - Mask Map Packer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Packs four separate greyscale maps into one URP terrain mask map.
    ///
    /// WHY THIS NODE EXISTS
    /// --------------------
    /// A downloaded PBR set is four separate greyscale PNGs - metallic, ambient
    /// occlusion, height, roughness - because that is how texture sites ship them.
    /// URP's terrain layer wants ONE RGBA mask: R metallic, G occlusion, B height,
    /// A smoothness. Every other node in the graph already consumes that packed
    /// form (Normal Map Layer's Mask port, the applier's maskMapTexture, the
    /// shader's _Mask0..3), so without this node a user has to open an image
    /// editor and hand-pack the channels before the set will drop in. This does
    /// that pack in the graph, and does the one conversion people always forget:
    /// smoothness = 1 - roughness.
    ///
    /// UC-T07-10: sources must be readable Texture2D data. Enable Read/Write on the input importer,
    /// or use a prepacked mask through Normal Map Layer. Unsupported inputs report a diagnostic
    /// before allocation; previews and Apply never synchronously read a GPU texture back.
    ///
    /// IMPORT NOTE: metallic / AO / height / roughness are DATA, not colour.
    /// Import them with sRGB unchecked or the values are gamma-mangled before they
    /// ever reach this node - a thing this node cannot undo, only pass through.
    /// </summary>
    [System.Serializable]
    public class MaskMapPackerNode : TextureNode
    {
        public override string NodeName { get { return "Mask Map Packer"; } }
        /// <summary>UC-T07-10: native source access/upload is main-thread only; channel packing requires no GPU readback.</summary>
        public override NodeExecutionDomain ExecutionDomain { get { return NodeExecutionDomain.MainThread; } }
        /// <summary>UC-T02-05: readable source maps can be packed through managed sampling without GPU readback.</summary>
        public override bool HasCpuFallback => true;
        public override NodeCategory Category { get { return NodeCategory.Texture; } }
        public override string Tooltip
        {
            get { return "Packs metallic + AO + height + roughness into one URP mask (A = 1 - roughness). Wire into a layer's Mask port."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Metallic",  PortType.Texture, PortDirection.Input),
            new Port("Occlusion", PortType.Texture, PortDirection.Input),
            new Port("Height",    PortType.Texture, PortDirection.Input),
            new Port("Roughness", PortType.Texture, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return TexturePorts.TextureOut; }

        [Tooltip("Metallic greyscale map. R metallic in the packed mask. Wired port wins over this slot.")]
        public Texture2D metallicMap;

        [Tooltip("Ambient Occlusion (AO) greyscale map. G occlusion in the packed mask. Bright = fully lit.")]
        public Texture2D occlusionMap;

        [Tooltip("Height / displacement greyscale map. B height, used by the height blend.")]
        public Texture2D heightMap;

        [Tooltip("Roughness greyscale map. Inverted to smoothness (A). Uncheck 'Roughness is smoothness' if the map is already smoothness.")]
        public Texture2D roughnessMap;

        [Tooltip("The map above is smoothness already, not roughness. Then A is used as-is with no invert.")]
        public bool roughnessIsSmoothness;

        [Tooltip("Value used for metallic where no map is supplied. Terrain is almost never metallic.")]
        public float metallicDefault;

        [Tooltip("Value used for occlusion where no map is supplied. 1 is fully lit.")]
        public float occlusionDefault = 1f;

        [Tooltip("Value used for height where no map is supplied. 0.5 is the neutral mid-height.")]
        public float heightDefault = 0.5f;

        [Tooltip("Value used for roughness where no map is supplied.")]
        public float roughnessDefault = 0.5f;

        [Tooltip("Output size in pixels. Rounded to a power of two. All four inputs are resampled to this.")]
        public int resolution = 1024;

        /// <summary>UC-T07-10: captures readable map inputs on the main thread and packs their linear PBR channels under tile admission.</summary>
        /// <param name="outputIndex">The sole texture output, zero.</param>
        /// <param name="tile">Bound evaluation/native owner with optional wired map overrides.</param>
        /// <param name="stop">Optional cooperative cancellation; no texture is published when stopped.</param>
        /// <returns>Owned packed texture descriptor, or null for absent/cancelled source maps.</returns>
        /// <exception cref="System.InvalidOperationException">Called on a worker, a source is unreadable, or memory admission fails.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.RequireMainThread("Mask source capture"); // UC-T07-10: guard direct calls before dereferencing input textures.
            Texture mMap = ResolveMap(tile, 0, metallicMap, stop);
            Texture oMap = ResolveMap(tile, 1, occlusionMap, stop);
            Texture hMap = ResolveMap(tile, 2, heightMap, stop);
            Texture rMap = ResolveMap(tile, 3, roughnessMap, stop);

            // The pack is shared with Normal Map Layer's inline PBR slots so the two
            // paths can never disagree about channel order or the
            // smoothness = 1 - roughness conversion. One implementation, two callers.
            return PackMask(
                mMap, oMap, hMap, rMap,
                metallicDefault, occlusionDefault, heightDefault, roughnessDefault,
                roughnessIsSmoothness, resolution, stop, tile);
        }

        /// <summary>
        /// Packs four greyscale channels into one URP mask (R metallic, G occlusion,
        /// B height, A smoothness = 1 - roughness). Shared by this node and Normal
        /// Map Layer's inline PBR slots. Returns null when no channel is supplied so
        /// a layer keeps whatever mask it already had. UC-T02-05 reads CPU-readable maps directly;
        /// unreadable maps require graphics and an explicit main-thread action outside Editor repaint.
        /// </summary>
        /// <param name="metallic">Optional linear red-channel source for metallic.</param>
        /// <param name="occlusion">Optional linear red-channel source for occlusion.</param>
        /// <param name="height">Optional linear red-channel source for height.</param>
        /// <param name="roughness">Optional linear red-channel source for roughness/smoothness.</param>
        /// <param name="metallicDefault">Fallback metallic, clamped to 0–1.</param>
        /// <param name="occlusionDefault">Fallback occlusion, clamped to 0–1.</param>
        /// <param name="heightDefault">Fallback height, clamped to 0–1.</param>
        /// <param name="roughnessDefault">Fallback roughness/smoothness, clamped to 0–1.</param>
        /// <param name="roughnessIsSmoothness">True when the alpha source already expresses smoothness.</param>
        /// <param name="resolution">Pixels per side; rounded up to a power of two within 32–2048.</param>
        /// <param name="stop">Optional cancellation, checked between source reads and during CPU packing.</param>
        /// <param name="memoryOwner">Optional evaluation owner; reserves payload bytes and retires the generated texture after its final consumer.</param>
        /// <returns>A new packed descriptor, or null for no sources/cancellation. Without memoryOwner the caller owns the native texture.</returns>
        /// <exception cref="System.InvalidOperationException">The caller is a worker, a source is not a readable Texture2D, or admitted memory is exhausted.</exception>
        public static TextureRef PackMask(
            Texture metallic, Texture occlusion, Texture height, Texture roughness,
            float metallicDefault, float occlusionDefault, float heightDefault, float roughnessDefault,
            bool roughnessIsSmoothness, int resolution, StopToken stop, TileData memoryOwner = null)
        {
            GraphExecutionGuard.RequireMainThread("Mask map packing"); // UC-T02-05: also protect callers that bypass GraphEvaluator.
            if (stop != null && stop.StopRequested) return null;
            if (metallic == null && occlusion == null && height == null && roughness == null)
                return null;

            RequireReadable(metallic); RequireReadable(occlusion); RequireReadable(height); RequireReadable(roughness); // UC-T07-10: reject absent CPU data before channel/native admission.

            int res = NextPow2(Mathf.Clamp(resolution, 32, 2048));
            // UC-T07-10: admit bounded CPU channel/pixel scratch; no temporary GPU/readback allocations remain.
            using ProductMemoryBudget.Lease scratch = memoryOwner?.ReserveProductMemory(64 + (long)res * res * 64);
            float[] mData = ReadChannel(metallic, res, stop, memoryOwner);
            float[] oData = ReadChannel(occlusion, res, stop, memoryOwner);
            float[] hData = ReadChannel(height, res, stop, memoryOwner);
            float[] rData = ReadChannel(roughness, res, stop, memoryOwner);

            if (stop != null && stop.StopRequested) return null;

            float mDef = Mathf.Clamp01(metallicDefault), oDef = Mathf.Clamp01(occlusionDefault);
            float hDef = Mathf.Clamp01(heightDefault), rDef = Mathf.Clamp01(roughnessDefault);

            Color[] pixels = new Color[res * res];
            for (int i = 0; i < pixels.Length; i++)
            {
                if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                float mv = mData != null ? mData[i] : mDef;
                float ov = oData != null ? oData[i] : oDef;
                float hv = hData != null ? hData[i] : hDef;
                float rv = rData != null ? rData[i] : rDef;
                // URP's mask A is smoothness; downloaded sets ship roughness, its inverse.
                pixels[i] = new Color(mv, ov, hv, roughnessIsSmoothness ? rv : 1f - rv);
            }

            // UC-T02-05: allocate the native result only after cancellable CPU preparation has completed.
            ProductMemoryBudget.Lease native = memoryOwner?.ReserveTextureMemory(1024 + (long)res * res * 12);
            Texture2D packed = null;
            bool published = false;
            try
            {
                packed = new Texture2D(res, res, TextureFormat.RGBA32, true, true);
                packed.name = "UC_MaskPacked";
                packed.wrapMode = TextureWrapMode.Repeat;
                packed.filterMode = FilterMode.Trilinear;
                packed.SetPixels(pixels);
                packed.Apply(true, false);
                packed.hideFlags = HideFlags.DontSave;
                if (stop != null && stop.StopRequested) return null;
                var result = memoryOwner != null ? memoryOwner.OwnTexture(packed, native) : new TextureRef(packed) { isGenerated = true };
                result.filter = TextureFilterKind.Trilinear; result.wrap = TextureWrapKind.Repeat;
                native = null;
                published = true;
                return result;
            }
            finally
            {
                if (!published)
                {
                    TileData.RetireTexture(packed, native);
                }
            }
        }

        /// <summary>A wired texture port wins over the inline slot, as everywhere else.</summary>
        private Texture ResolveMap(TileData tile, int port, Texture2D inline, StopToken stop)
        {
            TextureRef wired = GetInputTexture(tile, port, stop);
            if (wired != null && wired.texture != null) return wired.texture;
            return inline;
        }

        /// <summary>
        /// UC-T07-10: resamples the readable red data channel on the CPU; source import values remain linear data.
        /// </summary>
        private static float[] ReadChannel(Texture src, int res, StopToken stop, TileData memoryOwner)
        {
            if (stop != null && stop.StopRequested) return null;
            if (src == null) return null;

            // UC-T02-05: mask channels are authored linear data; readable sources need no graphics device or readback.
            if (src is Texture2D readable && readable.isReadable)
            {
                int width = readable.width, height = readable.height;
                // UC-T02-06: GetPixels allocates at SOURCE size, even when packing a tiny output.
                using ProductMemoryBudget.Lease sourcePixels = memoryOwner?.ReserveProductMemory(64 + (long)width * height * 16);
                Color[] pixels = readable.GetPixels();
                var values = new float[res * res];
                bool nearest = readable.filterMode == FilterMode.Point;
                for (int z = 0; z < res; z++)
                {
                    if (stop != null && stop.StopRequested) return null;
                    float py = (z + 0.5f) * height / res - 0.5f;
                    int y0 = Mathf.FloorToInt(py), y1 = Mathf.Clamp(y0 + 1, 0, height - 1);
                    float ty = py - y0; y0 = Mathf.Clamp(y0, 0, height - 1);
                    for (int x = 0; x < res; x++)
                    {
                        float px = (x + 0.5f) * width / res - 0.5f;
                        if (nearest)
                        {
                            values[z * res + x] = pixels[Mathf.Clamp(Mathf.RoundToInt(py), 0, height - 1) * width + Mathf.Clamp(Mathf.RoundToInt(px), 0, width - 1)].r;
                            continue;
                        }
                        int x0 = Mathf.FloorToInt(px), x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
                        float tx = px - x0; x0 = Mathf.Clamp(x0, 0, width - 1);
                        values[z * res + x] = Mathf.Lerp(Mathf.Lerp(pixels[y0 * width + x0].r, pixels[y0 * width + x1].r, tx),
                            Mathf.Lerp(pixels[y1 * width + x0].r, pixels[y1 * width + x1].r, tx), ty);
                    }
                }
                return values;
            }
            throw new System.InvalidOperationException("Mask input is not readable Texture2D data."); // UC-T07-10: no synchronous GPU fallback.
        }

        private static void RequireReadable(Texture source)
        {
            if (source != null && (!(source is Texture2D texture) || !texture.isReadable))
                throw new System.InvalidOperationException("Mask input '" + source.name + "' is not CPU readable. Enable Read/Write on its importer, or supply a prepacked mask through Normal Map Layer. GPU readback is unavailable during graph execution.");
        }

        private static int NextPow2(int v)
        {
            int p = 32;
            while (p < v) p <<= 1;
            return p;
        }
    }
}
