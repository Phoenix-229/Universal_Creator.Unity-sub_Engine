// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    SwissNoiseNode.cs
//  Purpose: Swiss turbulence creator. Erosion-like ridges without running an
//           erosion simulation.
//
//  Each octave is displaced by the running gradient of the octaves before it, so
//  detail bends along the slope it sits on instead of being stamped straight
//  down. That is what produces the fanned, water-carved look on a ridge flank -
//  cheaply, since it is still just noise and never touches the erosion solver.
//
//  WORLD-SPACE SAMPLING, as every creator: the noise coordinate comes from the
//  pixel's absolute world position, so neighbouring chunks agree along a seam.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class SwissNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Swiss Noise"; } }
        public override string Tooltip { get { return "Erosion-like turbulence. Ridges that fan along their own slope."; } }

        // Swiss turbulence builds its own octave stack, so the basis only matters
        // to inherited code paths. Ridged is the closest match to what it emits.
        protected override NoiseType BasisType { get { return NoiseType.Ridged; } }

        /// <summary>
        /// How far each octave is pushed by the accumulated gradient. 0 degrades
        /// to plain ridged fBm; past about 0.3 the field starts to smear.
        /// </summary>
        public float warp = 0.15f;

        /// <summary>Frequency step per octave. 2 is one octave in the musical sense.</summary>
        public float lacunarity = 2f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 16);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x + worldOffset.x;
            float originZ = m.worldPos.z + worldOffset.y;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float v = noise.SwissTurbulence(wx, wz, safeSize, iterations,
                                                    detail, warp, lacunarity);

                    m.arr[rowStart + x] = Mathf.Clamp01(v * intensity + offset);
                }
            }
        }
    }
}
