// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    MarbleNoiseNode.cs
//  Purpose: Banded strata creator. Sedimentary layering, folded rock, and the
//           swirled bands exposed on a canyon wall.
//
//  A sine wave across X gives the bands; noise added to its phase is what bends
//  them into folds rather than leaving them as ruled lines. Feed the output to a
//  selector to drive strata-coloured texturing, or blend it in at low opacity to
//  emboss the layering into the surface itself.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class MarbleNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Marble Noise"; } }
        public override string Tooltip { get { return "Folded bands. Sedimentary strata and canyon layering."; } }

        protected override NoiseType BasisType { get { return NoiseType.Perlin; } }

        /// <summary>Number of band repeats per noise period. Higher is finer layering.</summary>
        public float bandFrequency = 4f;

        /// <summary>
        /// How hard the noise bends the bands. 0 leaves dead-straight lines,
        /// which almost never reads as rock; around 1 gives natural folding.
        /// </summary>
        public float distortion = 1f;

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

                    float v = noise.Marble(wx, wz, safeSize, bandFrequency,
                                           distortion, iterations);

                    m.arr[rowStart + x] = Mathf.Clamp01(v * intensity + offset);
                }
            }
        }
    }
}
