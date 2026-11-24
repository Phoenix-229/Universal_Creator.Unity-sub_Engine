// UC-T08-06: immutable authored overrides; the existing weather manager remains the simulation authority.
using System;
using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>Snapshot-owned recipe of at most 64 priority-ordered local weather regions; sampling allocates nothing.</summary>
    /// <remarks>Bounds and velocities use world XZ metres and m/s. Higher priority wins; equal priority orders by ordinal node ID. Climate statistics are never changed.</remarks>
    public sealed class WeatherRegionProduct
    {
        private readonly Region[] regions;
        internal readonly struct Region
        {
            internal readonly string Id;
            internal readonly int Priority;
            internal readonly Rect Bounds;
            internal readonly float Blend;
            internal readonly Vector2 Velocity, Wind;
            internal readonly float Temperature, Humidity, Cover, Rain, Fog, Wetness, Visibility;
            internal Region(string id, int priority, Rect bounds, float blend, Vector2 velocity, Vector2 wind,
                float temperature, float humidity, float cover, float rain, float fog, float wetness, float visibility)
            {
                Id = id; Priority = priority; Bounds = bounds; Blend = blend; Velocity = velocity; Wind = wind;
                Temperature = temperature; Humidity = humidity; Cover = cover; Rain = rain; Fog = fog; Wetness = wetness; Visibility = visibility;
            }
        }
        internal WeatherRegionProduct(Region region) : this(new[] { region }) { }
        private WeatherRegionProduct(Region[] captured) { regions = captured; }
        internal WeatherRegionProduct WithRegion(Region region)
        {
            if (regions.Length >= 64) throw new InvalidOperationException("Weather exceeds the 64-region budget.");
            foreach (var existing in regions) if (existing.Id == region.Id) throw new InvalidOperationException("Weather region node IDs must be unique.");
            var copied = new Region[regions.Length + 1]; Array.Copy(regions, copied, regions.Length); copied[regions.Length] = region;
            Array.Sort(copied, (a, b) => { int order = a.Priority.CompareTo(b.Priority); return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id); });
            return new WeatherRegionProduct(copied);
        }
        internal void Apply(ref WeatherState.Builder air, Vector2 position, double instantSeconds)
        {
            foreach (var region in regions)
            {
                // Move the query instead of changing captured bounds; seeks reproduce the same moving front without integration drift.
                var local = new Vector2((float)(position.x - region.Velocity.x * instantSeconds), (float)(position.y - region.Velocity.y * instantSeconds));
                float weight = ClimateBaselineProduct.RegionWeight(region.Bounds, region.Blend, local);
                if (weight <= 0) continue;
                air.temperature = Mathf.Lerp(air.temperature, region.Temperature, weight);
                air.relativeHumidity01 = air.humidity = Mathf.Lerp(air.relativeHumidity01, region.Humidity, weight);
                air.cloudCoverLow = Mathf.Lerp(air.cloudCoverLow, region.Cover, weight); air.cloudCoverMid *= 1 - weight; air.cloudCoverHigh *= 1 - weight;
                air.cloudCover = CloudCover.CombinedCover01(air.cloudCoverLow, air.cloudCoverMid, air.cloudCoverHigh);
                air.precipitationMmPerHour = Mathf.Lerp(air.precipitationMmPerHour, region.Rain, weight);
                air.fogDensity = Mathf.Lerp(air.fogDensity, region.Fog, weight);
                air.wetness = Mathf.Lerp(air.wetness, region.Wetness, weight);
                air.visibility = Mathf.Lerp(air.visibility, region.Visibility, weight);
                float angle = air.windDirectionDeg * Mathf.Deg2Rad;
                Vector2 wind = Vector2.Lerp(new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * air.windSpeed, region.Wind, weight);
                air.windSpeed = Mathf.Min(100, wind.magnitude);
                if (wind.sqrMagnitude > 0) air.windDirectionDeg = Mathf.Repeat(Mathf.Atan2(wind.x, wind.y) * Mathf.Rad2Deg, 360);
            }
        }
    }
}
