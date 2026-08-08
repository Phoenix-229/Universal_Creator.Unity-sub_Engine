// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    MoistureMapNode.cs
//  Purpose: Generates a moisture field from elevation, drainage, and orographic
//           rain shadow.
//
//  Three contributions, which is what separates this from plain noise:
//    - Drainage: water collects in low, concave ground, so flow accumulation and
//      low elevation both raise moisture.
//    - Rain shadow: prevailing wind drops its water on the windward slope and
//      arrives dry on the lee side. Sampling the height upwind of each pixel and
//      comparing it to the local height gives that asymmetry.
//    - Noise: local variation so isohyets are not mechanical.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    [System.Serializable]
    public class MoistureMapNode : ModifierNode
    {
        public override string NodeName { get { return "Moisture Map"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Moisture from drainage and orographic rain shadow."; } }

        /// <summary>Baseline moisture before any modifier applies.</summary>
        public float baseMoisture = 0.5f;

        /// <summary>How strongly low ground reads as wet. 0 disables.</summary>
        public float drainageInfluence = 0.4f;

        /// <summary>Compass bearing the prevailing wind blows toward, in degrees.</summary>
        public float windDirection = 270f;

        /// <summary>How strongly the lee side dries out. 0 disables rain shadow.</summary>
        public float rainShadowStrength = 0.5f;

        /// <summary>How far upwind to sample, in pixels.</summary>
        public float rainShadowDistance = 24f;

        public float noiseStrength = 0.1f;
        public float noiseSize = 1500f;
        public int seed = 777;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            int w = m.rect.size.x;
            int h = m.rect.size.z;

            // Snapshot the elevation before we overwrite it with moisture.
            float[] elevation = new float[m.arr.Length];
            System.Array.Copy(m.arr, elevation, m.arr.Length);

            Noise noise = new Noise(seed);
            float safeNoiseSize = Mathf.Max(0.0001f, noiseSize);

            const float DegToRad = 0.017453292519943295f;
            float rad = windDirection * DegToRad;

            // Step back along the wind vector to find where the air came from.
            float upwindX = -Mathf.Sin(rad) * Mathf.Max(0f, rainShadowDistance);
            float upwindZ = -Mathf.Cos(rad) * Mathf.Max(0f, rainShadowDistance);

            Vector2 pixelSize = m.PixelSize;

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = rowStart + x;
                    float elev = InterpolationMath.Clamp01(elevation[i]);

                    float moisture = baseMoisture;

                    // Drainage: low ground holds water.
                    moisture += (1f - elev) * drainageInfluence;

                    // Rain shadow: if the terrain upwind is higher, the air already
                    // dropped its moisture climbing it.
                    if (rainShadowStrength > 1e-5f)
                    {
                        int ux = Mathf.Clamp(x + Mathf.RoundToInt(upwindX), 0, w - 1);
                        int uz = Mathf.Clamp(z + Mathf.RoundToInt(upwindZ), 0, h - 1);

                        float upwindElev = elevation[uz * w + ux];
                        float barrier = Mathf.Max(0f, upwindElev - elev);
                        moisture -= barrier * rainShadowStrength;
                    }

                    if (noiseStrength > 1e-5f)
                    {
                        float wx = m.worldPos.x + x * pixelSize.x;
                        float wz = m.worldPos.z + z * pixelSize.y;
                        float n = noise.Fractal(wx, wz, safeNoiseSize, 4, 0.5f, 0f, NoiseType.Perlin);
                        moisture += (n - 0.5f) * 2f * noiseStrength;
                    }

                    m.arr[i] = InterpolationMath.Clamp01(moisture);
                }
            }
        }
    }
}
