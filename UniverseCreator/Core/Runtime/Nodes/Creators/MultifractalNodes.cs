// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    MultifractalNodes.cs
//  Purpose: Musgrave multifractal bases and the domain-warp field source.
//
//  A plain fBm sums octaves at a fixed amplitude ratio, so roughness is uniform
//  everywhere and the result reads as noise rather than as land. A multifractal
//  weights each octave by what the previous one produced, which makes detail
//  follow elevation the way real erosion does: crests stay sharp, valleys smooth.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    /// <summary>Musgrave ridged multifractal. Sharp crests, smooth valleys.</summary>
    [System.Serializable]
    public class RidgedMultifractalNode : CreatorNode
    {
        public override string NodeName { get { return "Ridged Multifractal"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Musgrave ridged multifractal. Alpine crests with smooth valley floors."; } }

        public int seed = 12345;

        /// <summary>Feature size in world metres.</summary>
        public float size = 800f;

        public int octaves = 6;

        /// <summary>Per-octave amplitude falloff. Higher is rougher.</summary>
        public float roughness = 0.5f;

        /// <summary>Ridge sharpness. Higher gives thinner, harder crest lines.</summary>
        public float sharpness = 2f;

        /// <summary>How strongly one octave gates the next. 0 collapses to plain fBm.</summary>
        public float weightFalloff = 0.8f;

        public float amplitude = 1f;
        public float offset;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSize = Mathf.Max(1f, size);
            int safeOctaves = Mathf.Clamp(octaves, 1, 12);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float v = noise.RidgedMultifractal(wx, wz, safeSize, safeOctaves,
                                                       roughness, sharpness, weightFalloff);

                    m.arr[rowStart + x] = Mathf.Clamp01(offset + v * amplitude);
                }
            }
        }
    }

    /// <summary>
    /// Musgrave hybrid multifractal. Roughness rises with accumulated elevation,
    /// so lowlands stay smooth while uplands break up - the band structure the
    /// design document asks for is intrinsic to the weighting, not painted on.
    /// </summary>
    [System.Serializable]
    public class HybridMultifractalNode : CreatorNode
    {
        public override string NodeName { get { return "Hybrid Multifractal"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Musgrave hybrid multifractal. Smooth lowlands, rough uplands."; } }

        public int seed = 24680;

        public float size = 1200f;

        /// <summary>Octave count. Each band adds one level of elevation-gated detail.</summary>
        public int bands = 6;

        /// <summary>Fractal increment H. Lower is rougher across all bands.</summary>
        public float roughness = 0.35f;

        /// <summary>Frequency multiplier per band. 2 is one octave.</summary>
        public float lacunarity = 2f;

        /// <summary>Lifts the signed basis before weighting. Controls land/sea balance.</summary>
        public float offset = 0.7f;

        public float amplitude = 1f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSize = Mathf.Max(1f, size);
            int safeBands = Mathf.Clamp(bands, 1, 12);
            float safeLac = Mathf.Max(1.01f, lacunarity);

            // Precompute per-band amplitude: lacunarity^(-i*H).
            float[] exponents = new float[safeBands];
            for (int i = 0; i < safeBands; i++)
                exponents[i] = Mathf.Pow(safeLac, -i * roughness);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;
                    m.arr[rowStart + x] = Mathf.Clamp01(
                        Hybrid(noise, wx / safeSize, wz / safeSize, exponents, safeLac) * amplitude);
                }
            }
        }

        private float Hybrid(Noise noise, float x, float z, float[] exponents, float lac)
        {
            // Signed basis in -1..1; the offset is what turns a symmetric noise into
            // a landmass with a sea floor.
            float signal = (noise.Perlin(x, z) * 2f - 1f + offset) * exponents[0];
            float result = signal;
            float weight = signal;

            for (int i = 1; i < exponents.Length; i++)
            {
                if (weight > 1f) weight = 1f;
                if (weight < 0f) weight = 0f;

                x *= lac;
                z *= lac;

                signal = (noise.Perlin(x, z) * 2f - 1f + offset) * exponents[i];

                // The weight gate is the whole point: low ground contributes little
                // weight, so its higher octaves never accumulate.
                result += weight * signal;
                weight *= signal;
            }

            return result * 0.5f;
        }
    }

    /// <summary>
    /// Emits a two-channel warp field for Domain Warp Apply. Split from the
    /// existing single-node Domain Warp so one field can drive several consumers
    /// without each recomputing its own offsets.
    /// </summary>
    [System.Serializable]
    public class DomainWarpFieldNode : Generator
    {
        public override string NodeName { get { return "Domain Warp Field"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Emits X/Z warp offsets for Domain Warp Apply."; } }

        private static readonly Port[] Outs =
        {
            new Port("Warp X", PortType.Matrix, PortDirection.Output, true),
            new Port("Warp Z", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return Outs; }

        public int seed = 909;

        /// <summary>Warp feature size in world metres.</summary>
        public float size = 400f;

        /// <summary>UC-T03-03: recursive input displacement in world metres; unused when recursive is false.</summary>
        [Tooltip("Recursive input displacement in world metres. Used only when Recursive is enabled; Domain Warp Apply sets the final terrain displacement.")]
        public float intensity = 60f;

        public int octaves = 3;

        /// <summary>Feeds the warp through itself once. Produces curled, fibrous flow.</summary>
        public bool recursive;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix m = tile.CreateMatrix();

            Noise noise = new Noise(seed);
            // Channel offset so X and Z do not sample the same field, which would
            // warp everything along a single diagonal.
            Noise channel = noise.WithSubSeed(outputIndex == 0 ? 101 : 202);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSize = Mathf.Max(1f, size);
            int safeOct = Mathf.Clamp(octaves, 1, 8);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float sx = wx;
                    float sz = wz;

                    if (recursive)
                    {
                        float pre = channel.Fractal(wx, wz, safeSize * 2f, safeOct) - 0.5f;
                        sx += pre * intensity;
                        sz += pre * intensity;
                    }

                    float v = channel.Fractal(sx, sz, safeSize, safeOct) - 0.5f;

                    // Stored normalised to 0..1 so it survives the Matrix contract;
                    // Domain Warp Apply recentres and rescales by its own intensity.
                    m.arr[rowStart + x] = Mathf.Clamp01(0.5f + v * 2f);
                }
            }

            // UC-T03-03: this output is normalized; recursive displacement and the
            // applier's final displacement are separate authored controls.
            return m;
        }
    }
}
