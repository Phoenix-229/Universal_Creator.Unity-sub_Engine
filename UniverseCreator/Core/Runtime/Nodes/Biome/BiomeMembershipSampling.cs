// UC-T07-03/04: filter membership of actual source labels, never the arithmetic value of a biome enum.
using System;
using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.Nodes
{
    internal static class BiomeMembershipSampling
    {
        internal static float SampleWorld(Matrix labels, float x, float z, int category)
        {
            Vector2 pixel = labels.WorldToPixel(x, z);
            return SamplePixel(labels, pixel.x, pixel.y, category);
        }

        internal static float SamplePixel(Matrix labels, float x, float z, int category)
        {
            if (category < 0 || category >= WhittakerBiome.Count) throw new ArgumentOutOfRangeException(nameof(category));
            TextureImageSampling.RequireFinite(x); TextureImageSampling.RequireFinite(z);
            x = Mathf.Clamp(x, labels.rect.offset.x, labels.rect.offset.x + labels.rect.size.x - 1);
            z = Mathf.Clamp(z, labels.rect.offset.z, labels.rect.offset.z + labels.rect.size.z - 1);
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            float fx = x - ix, fz = z - iz;
            return Mathf.Lerp(Mathf.Lerp(IsCategory(labels.GetSafe(ix, iz), category), IsCategory(labels.GetSafe(ix + 1, iz), category), fx),
                Mathf.Lerp(IsCategory(labels.GetSafe(ix, iz + 1), category), IsCategory(labels.GetSafe(ix + 1, iz + 1), category), fx), fz);
        }

        internal static float IsCategory(float encoded, int category)
        {
            TextureImageSampling.RequireFinite(encoded);
            int label = WhittakerBiome.Decode(encoded);
            if (Mathf.Abs(encoded - WhittakerBiome.Encode(label)) > 0.0001f)
                throw new ArgumentException("UC-T07-03/04: expected a categorical biome field; explicit membership channels are required for graded data.");
            return label == category ? 1f : 0f;
        }
    }
}
