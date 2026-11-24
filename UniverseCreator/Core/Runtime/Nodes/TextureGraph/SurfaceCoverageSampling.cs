// UC-T07-11: validate world coordinates before continuous membership resampling; never clamp a foreign tile onto this terrain.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    internal static class SurfaceCoverageSampling
    {
        internal static void Validate(Matrix map, Vector3 position, Vector3 size, bool membership = true)
        {
            if (map == null || map.arr == null || map.rect.size.x < 2 || map.rect.size.z < 2 ||
                (long)map.rect.size.x * map.rect.size.z != map.arr.LongLength || map.worldSize.x <= 0 || map.worldSize.z <= 0)
                throw new ArgumentException("Surface fields require a valid two-dimensional physical lattice.");
            if (map.PixelSize.x <= 1e-9f || map.PixelSize.y <= 1e-9f) throw new ArgumentException("Surface field metre spacing is below the supported coordinate envelope.");
            TextureImageSampling.RequireFinite(map.worldPos.x); TextureImageSampling.RequireFinite(map.worldPos.z);
            TextureImageSampling.RequireFinite(map.worldSize.x); TextureImageSampling.RequireFinite(map.worldSize.z);
            TextureImageSampling.RequireFinite(position.x); TextureImageSampling.RequireFinite(position.z);
            TextureImageSampling.RequireFinite(size.x); TextureImageSampling.RequireFinite(size.z);
            if (size.x <= 0 || size.z <= 0 || position.x < map.worldPos.x || position.z < map.worldPos.z ||
                (double)position.x + size.x > (double)map.worldPos.x + map.worldSize.x ||
                (double)position.z + size.z > (double)map.worldPos.z + map.worldSize.z)
                throw new ArgumentException("Surface field coordinates do not cover this terrain. Publish/resample the correct accepted world region; edge clamping cannot repair a foreign mapping.");
            foreach (float value in map.arr)
            {
                TextureImageSampling.RequireFinite(value);
                if (membership && (value < 0 || value > 1)) throw new ArgumentException("Material membership requires independent continuous values 0…1, not encoded category IDs.");
            }
        }

        internal static float Sample(Matrix map, float x, float z)
        {
            Vector2 pixel = map.WorldToPixel(x, z);
            return map.GetInterpolated(pixel.x, pixel.y);
        }

        internal static bool HasCoverage(SplatProduct splat)
        {
            if (splat != null) for (int i = 0; i < 4; i++) if (splat.coverage[i] != null) return true;
            return false;
        }

        internal static Vector4 Weights(SplatProduct splat, float x, float z)
        {
            Vector4 weights = Vector4.zero; int fallback = -1;
            for (int i = 0; i < 4; i++)
            {
                if (splat.layers[i] == null) continue;
                if (fallback < 0) fallback = i;
                if (splat.coverage[i] != null) weights[i] = Sample(splat.coverage[i], x, z);
            }
            if (weights.x == 0 && weights.y == 0 && weights.z == 0 && weights.w == 0)
            {
                if (fallback < 0) throw new ArgumentException("Surface coverage requires at least one authored material.");
                weights[fallback] = 1;
            }
            return MaterialWeightMath.Normalize(weights);
        }
    }
}
