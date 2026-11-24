// UC-T08-03: immutable authored climate precedes terrain; live precipitation never changes this product.
using System;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Weather
{
    /// <summary>Long-term sea-level climate at one position and season, independent of live weather.</summary>
    public readonly struct ClimateBaselineSample
    {
        /// <summary>Mean air temperature in degrees Celsius, -100 through 70.</summary>
        public readonly float TemperatureC;
        /// <summary>Available moisture, zero through one; this is not instantaneous relative humidity.</summary>
        public readonly float Moisture01;
        /// <summary>Wind velocity in world XZ metres per second, bounded to 100 m/s.</summary>
        public readonly Vector2 WindMetresPerSecond;
        /// <summary>Complement of available moisture, zero through one.</summary>
        public float Aridity01 => 1f - Moisture01;
        internal ClimateBaselineSample(float temperature, float moisture, Vector2 wind)
        { TemperatureC = temperature; Moisture01 = moisture; WindMetresPerSecond = wind; }
        internal static ClimateBaselineSample Blend(ClimateBaselineSample a, ClimateBaselineSample b, float weight) =>
            new ClimateBaselineSample(Mathf.Lerp(a.TemperatureC, b.TemperatureC, weight),
                Mathf.Lerp(a.Moisture01, b.Moisture01, weight), Vector2.Lerp(a.WindMetresPerSecond, b.WindMetresPerSecond, weight));
    }

    /// <summary>Worker-safe climate recipe with captured seasonal tables and at most 64 deterministic regional overrides.</summary>
    /// <remarks>World XZ is in metres. Regions blend inward from their bounds; priority then stable node ID resolves overlaps.
    /// Immutable tables can be shared under the existing snapshot/product-memory leases; no Unity assets are sampled on workers.</remarks>
    public sealed class ClimateBaselineProduct
    {
        internal const int SeasonalSamples = 65;
        private readonly Band[] bands;
        /// <summary>Fingerprint of exactly the captured climate values, bounds, masks and seasonal samples.</summary>
        public ulong ContentHash { get; }

        internal sealed class Band
        {
            internal readonly string Id;
            internal readonly int Priority;
            internal readonly Rect Bounds;
            internal readonly float BlendMetres, Temperature, Moisture;
            internal readonly Vector2 Wind;
            private readonly float[] temperatureSeason, moistureSeason, mask;
            private readonly Vector2Int maskSize;
            private readonly Vector2 maskMin, maskExtent;
            internal readonly ulong Hash;

            internal Band(string id, int priority, Rect bounds, float blend, float temperature, float moisture,
                Vector2 wind, float[] temperatures, float[] moistures, Matrix regionMask)
            {
                Id = id ?? ""; Priority = priority; Bounds = bounds; BlendMetres = blend;
                Temperature = temperature; Moisture = moisture; Wind = wind;
                temperatureSeason = (float[])temperatures.Clone(); moistureSeason = (float[])moistures.Clone();
                if (regionMask != null)
                {
                    maskSize = new Vector2Int(regionMask.rect.size.x, regionMask.rect.size.z);
                    maskMin = new Vector2(regionMask.worldPos.x, regionMask.worldPos.z);
                    maskExtent = new Vector2(regionMask.worldSize.x, regionMask.worldSize.z);
                    long count = (long)maskSize.x * maskSize.y;
                    if (maskSize.x < 2 || maskSize.y < 2 || count > 1048576 || regionMask.arr == null || regionMask.arr.LongLength != count ||
                        !Finite(maskMin.x) || !Finite(maskMin.y) || !Finite(maskExtent.x) || !Finite(maskExtent.y) || maskExtent.x <= 0 || maskExtent.y <= 0)
                        throw new ArgumentException("Climate region masks require a finite metre grid with 2 through 1,048,576 samples.", nameof(regionMask));
                    mask = (float[])regionMask.arr.Clone();
                    for (int i = 0; i < mask.Length; i++)
                    {
                        if (!Finite(mask[i])) throw new ArgumentException("Climate region masks must contain finite samples.", nameof(regionMask));
                        mask[i] = Mathf.Clamp01(mask[i]);
                    }
                }
                ulong hash = PreparedRasterInputs.Hash(maskSize.x, maskSize.y, mask);
                foreach (char letter in Id) hash = PreparedRasterInputs.Mix(hash, letter);
                hash = PreparedRasterInputs.Mix(hash, unchecked((uint)priority));
                foreach (float value in new[] { bounds.x, bounds.y, bounds.width, bounds.height, blend, temperature, moisture, wind.x, wind.y,
                    maskMin.x, maskMin.y, maskExtent.x, maskExtent.y }) hash = PreparedRasterInputs.MixFloat(hash, value);
                foreach (float value in temperatureSeason) hash = PreparedRasterInputs.MixFloat(hash, value);
                foreach (float value in moistureSeason) hash = PreparedRasterInputs.MixFloat(hash, value);
                Hash = hash;
            }

            internal ClimateBaselineSample Sample(float season) => new ClimateBaselineSample(
                Mathf.Clamp(Temperature + Seasonal(temperatureSeason, season), -100, 70),
                Mathf.Clamp01(Moisture * Seasonal(moistureSeason, season)), Wind);

            internal float Weight(Vector2 position)
            {
                if (position.x < Bounds.xMin || position.y < Bounds.yMin || position.x > Bounds.xMax || position.y > Bounds.yMax) return 0;
                float weight = RegionWeight(Bounds, BlendMetres, position); // UC-T08-06: climate and weather share the same inward metre boundary rule.
                if (mask == null) return weight;
                float u = (position.x - maskMin.x) / maskExtent.x, v = (position.y - maskMin.y) / maskExtent.y;
                if (u < 0 || v < 0 || u > 1 || v > 1) return 0;
                float x = u * (maskSize.x - 1), z = v * (maskSize.y - 1);
                int ix = Math.Min((int)x, maskSize.x - 2), iz = Math.Min((int)z, maskSize.y - 2), at = iz * maskSize.x + ix;
                return weight * Mathf.Lerp(Mathf.Lerp(mask[at], mask[at + 1], x - ix),
                    Mathf.Lerp(mask[at + maskSize.x], mask[at + maskSize.x + 1], x - ix), z - iz);
            }

            private static float Seasonal(float[] table, float season)
            { float at = season * (SeasonalSamples - 1); int i = Math.Min((int)at, SeasonalSamples - 2); return Mathf.Lerp(table[i], table[i + 1], at - i); }
        }

        internal ClimateBaselineProduct(Band baseline) : this(new[] { baseline }) { }
        private ClimateBaselineProduct(Band[] captured)
        {
            bands = captured;
            ulong hash = 14695981039346656037UL;
            foreach (Band band in bands)
            { hash = PreparedRasterInputs.Mix(hash, (uint)band.Hash); hash = PreparedRasterInputs.Mix(hash, (uint)(band.Hash >> 32)); }
            ContentHash = hash == 0 ? 1 : hash;
        }

        internal ClimateBaselineProduct WithRegion(Band region)
        {
            if (bands.Length > 64) throw new InvalidOperationException("Climate exceeds the 64-region budget.");
            for (int i = 1; i < bands.Length; i++) if (bands[i].Id == region.Id)
                throw new InvalidOperationException("Climate regional node IDs must be unique.");
            var copied = new Band[bands.Length + 1]; Array.Copy(bands, copied, bands.Length); copied[bands.Length] = region;
            Array.Sort(copied, 1, copied.Length - 1, System.Collections.Generic.Comparer<Band>.Create((a, b) =>
            { int priority = a.Priority.CompareTo(b.Priority); return priority != 0 ? priority : string.CompareOrdinal(a.Id, b.Id); }));
            return new ClimateBaselineProduct(copied);
        }

        /// <summary>Samples sea-level climate at world XZ metres and a repeating annual phase; no allocation or live weather input.</summary>
        /// <param name="position">Finite world XZ in metres; outside overrides the baseline remains available.</param>
        /// <param name="yearFraction">Finite annual phase; zero is model day 1, one wraps to the next year.</param>
        /// <returns>Finite temperature, moisture/aridity and prevailing wind.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Position or annual phase is nonfinite.</exception>
        public ClimateBaselineSample Sample(Vector2 position, double yearFraction)
        {
            if (!Finite(position.x) || !Finite(position.y) || double.IsNaN(yearFraction) || double.IsInfinity(yearFraction))
                throw new ArgumentOutOfRangeException(nameof(position), "Climate coordinates and annual phase must be finite.");
            float season = (float)(yearFraction - Math.Floor(yearFraction));
            var sample = bands[0].Sample(season);
            for (int i = 1; i < bands.Length; i++)
            { float weight = bands[i].Weight(position); if (weight > 0) sample = ClimateBaselineSample.Blend(sample, bands[i].Sample(season), weight); }
            return sample;
        }
        internal static float RegionWeight(Rect bounds, float blendMetres, Vector2 position)
        {
            if (position.x < bounds.xMin || position.y < bounds.yMin || position.x > bounds.xMax || position.y > bounds.yMax) return 0;
            float edge = Mathf.Min(Mathf.Min(position.x - bounds.xMin, bounds.xMax - position.x),
                Mathf.Min(position.y - bounds.yMin, bounds.yMax - position.y));
            return blendMetres > 0 ? Mathf.SmoothStep(0, 1, Mathf.Clamp01(edge / blendMetres)) : 1;
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
