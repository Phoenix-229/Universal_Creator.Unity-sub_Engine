// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TectonicPlatesNode.cs
//  Purpose: Continental-scale landmass structure from a Voronoi plate model.
//
//  Each Voronoi cell is a plate with its own randomised base elevation (some
//  oceanic, some continental). Cell boundaries are read as plate margins and
//  raised into mountain belts, which is where real orogeny happens. This gives
//  coherent continents and coastlines that layered fBm cannot produce on its own.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class TectonicPlatesNode : CreatorNode
    {
        public override string NodeName { get { return "Tectonic Plates"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Voronoi plate model with uplifted margins. Continental structure."; } }

        public int seed = 4242;

        /// <summary>Average plate width in world metres.</summary>
        public float plateSize = 4000f;

        /// <summary>Fraction of plates that sit low (ocean basins), 0..1.</summary>
        public float oceanFraction = 0.45f;

        /// <summary>Elevation difference between continental and oceanic plates.</summary>
        public float plateRelief = 0.4f;

        /// <summary>Height added at plate boundaries.</summary>
        public float ridgeHeight = 0.5f;

        /// <summary>Width of the uplifted margin, as a fraction of plate size.</summary>
        public float ridgeWidth = 0.12f;

        /// <summary>Irregularity of plate borders. 0 gives straight polygon edges.</summary>
        public float borderWarp = 0.3f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);
            Noise warpNoise = noise.WithSubSeed(1337);
            Noise plateNoise = noise.WithSubSeed(7331);

            float safePlate = Mathf.Max(1f, plateSize);
            float invPlate = 1f / safePlate;
            float safeRidgeWidth = Mathf.Max(0.001f, ridgeWidth);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    // Warp the sample point so plate borders meander instead of
                    // reading as straight Voronoi edges.
                    float sx = wx * invPlate;
                    float sz = wz * invPlate;
                    if (borderWarp > 1e-5f)
                    {
                        sx += (warpNoise.Perlin(wx * invPlate * 2f, wz * invPlate * 2f) - 0.5f) * borderWarp;
                        sz += (warpNoise.Perlin(wx * invPlate * 2f + 31.7f, wz * invPlate * 2f - 17.3f) - 0.5f) * borderWarp;
                    }

                    // Cellular blend peaks at cell centres and dips at borders, so
                    // its inverse marks the margins.
                    float cellular = plateNoise.Voronoi(sx, sz, 0f, VoronoiBlend.Cellular);
                    float borderProximity = 1f - Mathf.Clamp01(cellular / safeRidgeWidth);

                    // Flat blend returns one constant per cell: the plate's identity.
                    float plateId = plateNoise.Voronoi(sx, sz, 0f, VoronoiBlend.Flat);
                    bool isOceanic = plateId < Mathf.Clamp01(oceanFraction);

                    float baseElevation = isOceanic
                        ? 0.5f - plateRelief * 0.5f
                        : 0.5f + plateRelief * 0.5f;

                    // Vary elevation within a plate so continents are not flat slabs.
                    baseElevation += (plateId - 0.5f) * plateRelief * 0.25f;

                    float ridge = InterpolationMath.SmoothStep(borderProximity) * ridgeHeight;

                    m.arr[rowStart + x] = Mathf.Clamp01(baseElevation + ridge);
                }
            }
        }
    }
}
