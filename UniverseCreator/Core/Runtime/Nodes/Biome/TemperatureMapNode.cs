// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TemperatureMapNode.cs
//  Purpose: Generates a temperature field from latitude and elevation, the two
//           dominant controls on real climate.
//
//  Latitude comes from world Z, so a world spanning north-south gets polar caps
//  and a warm equator for free. Elevation applies the environmental lapse rate:
//  temperature falls roughly 6.5 degC per 1000 m of altitude, which is why
//  mountains hold snow at the same latitude as forest.
//
//  Output is normalised 0-1, not degrees, so downstream selectors treat it like
//  any other mask. The lapse rate is expressed as a fraction of the full range.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    [System.Serializable]
    public class TemperatureMapNode : ModifierNode
    {
        public override string NodeName { get { return "Temperature Map"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Temperature from latitude and elevation lapse rate."; } }

        /// <summary>World Z at which latitude reads as the equator.</summary>
        public float equatorZ = 0f;

        /// <summary>World Z distance from the equator to the pole.</summary>
        public float poleDistance = 20000f;

        /// <summary>0-1 temperature at the equator at sea level.</summary>
        public float equatorTemperature = 1f;

        /// <summary>0-1 temperature at the pole at sea level.</summary>
        public float poleTemperature = 0f;

        /// <summary>
        /// Temperature drop per unit of normalised height. 0.7 means the highest
        /// terrain reads 0.7 colder than sea level at the same latitude.
        /// </summary>
        public float lapseRate = 0.7f;

        /// <summary>Local variation so isotherms are not perfectly straight bands.</summary>
        public float noiseStrength = 0.05f;
        public float noiseSize = 2000f;
        public int seed = 555;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);
            float safeNoiseSize = Mathf.Max(0.0001f, noiseSize);
            float safePole = Mathf.Max(1f, poleDistance);

            Vector2 pixelSize = m.PixelSize;

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = m.worldPos.z + z * pixelSize.y;

                // Latitude 0 at the equator, 1 at the pole. Absolute, so the model
                // is symmetric about the equator.
                float latitude = Mathf.Clamp01(Mathf.Abs(wz - equatorZ) / safePole);
                float baseTemp = Mathf.Lerp(equatorTemperature, poleTemperature, latitude);

                int rowStart = z * m.rect.size.x;

                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float elevation = InterpolationMath.Clamp01(m.arr[i]);

                    float temp = baseTemp - elevation * lapseRate;

                    if (noiseStrength > 1e-5f)
                    {
                        float wx = m.worldPos.x + x * pixelSize.x;
                        float n = noise.Fractal(wx, wz, safeNoiseSize, 3, 0.5f, 0f, NoiseType.Perlin);
                        temp += (n - 0.5f) * 2f * noiseStrength;
                    }

                    m.arr[i] = InterpolationMath.Clamp01(temp);
                }
            }
        }
    }
}
