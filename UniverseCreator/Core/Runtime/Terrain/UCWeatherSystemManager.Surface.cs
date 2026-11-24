// UC-T08-09: bounded local stores on the existing weather owner; no terrain generation or second clock.
using System;
using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        /// <summary>Opt-in sampled surface storage; off preserves authored wetness and seasonal snow behavior.</summary>
        [Tooltip("Store rain film and snow water equivalent in at most 128 local 64 m cells. Seeks or gaps over one simulated hour reset to authored initial state; no historical weather replay.")]
        public bool simulateSurfaceResponse;
        /// <summary>Fraction of precipitation reaching these sampled surfaces, 0 sheltered..1 exposed; no automatic shelter geometry is inferred.</summary>
        [Range(0, 1), Tooltip("Authored precipitation exposure, 0 fully sheltered to 1 open sky. Does not raycast buildings or canopy.")]
        public float surfaceExposure01 = 1;
        private readonly SurfaceStore[] surfaceStores = new SurfaceStore[128];
        private int nextSurfaceStore;
        private struct SurfaceStore
        {
            internal bool Active;
            internal Vector2 Cell;
            internal double Instant;
            internal float Wetness, SweMm, AgeDays;
        }

        /// <summary>Resident sampled surface cells, bounded to 128; this is a capacity diagnostic, not a whole-world weather simulation.</summary>
        public int CachedSurfaceCells
        {
            get { int count = 0; for (int i = 0; i < surfaceStores.Length; i++) if (surfaceStores[i].Active) count++; return count; }
        }

        private void ResetSurfaceStores()
        {
            Array.Clear(surfaceStores, 0, surfaceStores.Length); nextSurfaceStore = 0;
        }

        private WeatherState ApplySurfaceResponse(in WeatherState weather, Vector3 position, in ClockState clock)
        {
            if (!simulateSurfaceResponse) return weather;
            if (!ClimateBaselineProduct.Finite(surfaceExposure01) || !ClimateBaselineProduct.Finite(dryingSeconds) || dryingSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(dryingSeconds), "Surface exposure and reference dry time must be finite; dry time must be positive.");
            Vector2 cell = new Vector2(Mathf.Floor(position.x / 64), Mathf.Floor(position.z / 64));
            int index = -1;
            for (int i = 0; i < surfaceStores.Length; i++) if (surfaceStores[i].Active && surfaceStores[i].Cell == cell) { index = i; break; }
            double instant = WorldInstantSeconds;
            if (index < 0) { index = nextSurfaceStore; nextSurfaceStore = (nextSurfaceStore + 1) % surfaceStores.Length; }
            SurfaceStore store = surfaceStores[index];
            // Sparse observation is not a weather archive: bounded resets prevent a catch-up loop or fabricated historical rain.
            if (!store.Active || store.Cell != cell || instant < store.Instant || instant - store.Instant > 3600)
                store = new SurfaceStore { Active = true, Cell = cell, Instant = instant, Wetness = Mathf.Clamp01(surfaceWetness),
                    SweMm = Mathf.Clamp(weather.snowDepthM * SnowModel.DensityKgPerM3(0), 0, 300) };
            float dt = (float)(instant - store.Instant);
            float radiation = ClockMath.ExtraterrestrialRadiationMjPerM2Day(clock.latitudeDeg, clock.declinationDeg, ClockMath.WinterPhase01(clock.dayOfYear));
            float range = Mathf.Clamp(diurnalTempRangeCelsius, 0, 100);
            float pet = AtmoMath.HargreavesPetMmPerDay(weather.temperature - range * 0.5f, weather.temperature + range * 0.5f, weather.temperature, radiation);
            float rainFraction = WeatherPrecipType.RainFraction01(weather.wetBulbC);
            float precip = Mathf.Clamp(weather.precipitationMmPerHour, 0, 1000) * Mathf.Clamp01(surfaceExposure01);
            float beforeMelt = store.SweMm;
            store.SweMm = SnowModel.ApplyMelt(store.SweMm, SnowModel.MeltMmPerDay(weather.temperature, radiation, SnowModel.SnowAlbedo(store.AgeDays)), dt / ClockState.SecondsPerDay);
            float meltRate = dt > 0 ? (beforeMelt - store.SweMm) * ClockState.SecondsPerHour / dt : 0;
            store.SweMm = Mathf.Clamp(store.SweMm + precip * (1 - rainFraction) *
                SnowModel.GroundRetention01(weather.temperature) * dt / ClockState.SecondsPerHour, 0, 300);
            store.AgeDays = store.SweMm > 0 ? Mathf.Min(365, store.AgeDays + dt / ClockState.SecondsPerDay) : 0;
            store.Wetness = WetnessSimulation.Step(store.Wetness, precip * rainFraction + meltRate,
                pet * (AtmoConstants.WetnessDryTimeSeconds / dryingSeconds), dt);
            // Authored wetness remains the initial/minimum supply; the runtime store owns subsequent dry-down.
            store.Wetness = Mathf.Max(Mathf.Clamp01(surfaceWetness), store.Wetness);
            bool onWater = World != null && RiverChannelRegistry.TrySampleCommitted(World, position.x, position.z, 0, out var channel, out _) &&
                channel.SignedBankDistanceMetres <= 0 && position.y <= channel.WaterSurfaceY;
            store.Instant = instant; surfaceStores[index] = store;
            // UC-T08-09: water excludes snow only in this reading; it must not erase nearby land storage in the same cell.
            return weather.WithSurfaceResponse(onWater ? 1 : store.Wetness, onWater ? 0 : SnowModel.DepthMetresAtAge(store.SweMm, store.AgeDays));
        }
    }
}
