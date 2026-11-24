// UC-T07-04: only present categories allocate planes; blur/warp never create another encoded biome.
using System;
using UniverseCreator.Ecology;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    internal sealed class BiomeMembershipField : IDisposable
    {
        private readonly Matrix labels;
        private readonly Matrix[] planes;
        private ProductMemoryBudget.Lease memory;
        internal readonly int[] Categories;
        private BiomeMembershipField(Matrix labels, Matrix[] planes, int[] categories, ProductMemoryBudget.Lease memory)
        { this.labels = labels; this.planes = planes; Categories = categories; this.memory = memory; }

        internal static BiomeMembershipField Build(Matrix labels, float sigma, TileData tile, StopToken stop)
        {
            TextureImageSampling.RequireFinite(sigma);
            if (sigma < 0 || sigma > 8 || labels == null || labels.rect.size.x < 1 || labels.rect.size.z < 1 || labels.arr.Length > 2048 * 2048)
                throw new ArgumentException("UC-T07-04: biome membership filtering supports sigma 0…8 samples and at most 2048² source samples; choose a smaller categorical source explicitly.");
            int mask = 0;
            for (int i = 0; i < labels.arr.Length; i++)
            {
                if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                int category = WhittakerBiome.Decode(labels.arr[i]); BiomeMembershipSampling.IsCategory(labels.arr[i], category);
                mask |= 1 << category;
            }
            int count = 0; for (int i = 0; i < WhittakerBiome.Count; i++) if ((mask & (1 << i)) != 0) count++;
            int radius = Mathf.CeilToInt(sigma * 3);
            long work = labels.arr.LongLength * count * (sigma < 0.05f ? 4L : 2L * (2 * radius + 1));
            if (work > 512L * 1024 * 1024)
                throw new InvalidOperationException("UC-T07-04: biome membership work exceeds the 512 Mi tap envelope; reduce source resolution or softening explicitly.");
            // One plane per present category plus the separable blur's two transient planes; source labels and splat outputs have their own pins.
            ProductMemoryBudget.Lease reserved = tile?.ReserveProductMemory(1024L + labels.arr.LongLength * (count + 2) * 4L);
            try
            {
                var categories = new int[count]; var planes = new Matrix[WhittakerBiome.Count]; int slot = 0;
                for (int category = 0; category < WhittakerBiome.Count; category++)
                {
                    if ((mask & (1 << category)) == 0) continue;
                    categories[slot++] = category;
                    Matrix membership = labels.Blank();
                    for (int i = 0; i < membership.arr.Length; i++)
                    {
                        if ((i & 4095) == 0 && stop != null && stop.StopRequested) return null;
                        membership.arr[i] = BiomeMembershipSampling.IsCategory(labels.arr[i], category);
                    }
                    planes[category] = sigma < 0.05f ? membership : MatrixOperations.GaussianBlur(membership, sigma, stop);
                    if (planes[category] == null) return null;
                }
                var result = new BiomeMembershipField(labels, planes, categories, reserved); reserved = null; return result;
            }
            finally { reserved?.Dispose(); }
        }

        internal int NearestCategory(float x, float z)
        {
            Vector2 pixel = labels.WorldToPixel(x, z);
            return WhittakerBiome.Decode(labels.GetClamped(Mathf.RoundToInt(Mathf.Clamp(pixel.x, labels.rect.offset.x, labels.rect.offset.x + labels.rect.size.x - 1)),
                Mathf.RoundToInt(Mathf.Clamp(pixel.y, labels.rect.offset.z, labels.rect.offset.z + labels.rect.size.z - 1))));
        }

        internal float Sample(int category, float x, float z)
        {
            Matrix plane = planes[category]; if (plane == null) return 0;
            Vector2 pixel = plane.WorldToPixel(x, z);
            return Mathf.Max(0, plane.GetInterpolated(Mathf.Clamp(pixel.x, plane.rect.offset.x, plane.rect.offset.x + plane.rect.size.x - 1),
                Mathf.Clamp(pixel.y, plane.rect.offset.z, plane.rect.offset.z + plane.rect.size.z - 1)));
        }
        public void Dispose() { memory?.Dispose(); memory = null; }
    }
}
