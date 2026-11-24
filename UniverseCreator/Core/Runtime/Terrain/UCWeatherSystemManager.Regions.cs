// UC-T08-06: fixed local space/time cache for the existing pressure lattice, never a continent-sized simulation.
using System;
using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        /// <summary>Pressure/front sampling interval in simulated seconds, 1 through 3600; adjacent endpoints interpolate on the shared clock.</summary>
        [Range(1, 3600), Tooltip("Simulated seconds between deterministic regional endpoints. Queries interpolate; time jumps do not run catch-up ticks.")]
        public float regionalSampleIntervalSeconds = 300;
        /// <summary>Regional pressure sample spacing in world metres, 100 through 100000; authored region edges retain their exact separate bounds.</summary>
        [Range(100, 100000), Tooltip("Coarse pressure/front lattice in world metres. Weather Region blend distances are sampled exactly, independently of this grid.")]
        public float regionalSampleSpacingMetres = 10000;
        private const int RegionalCacheCapacity = 128;
        private readonly RegionalCacheEntry[] regionalCache = new RegionalCacheEntry[RegionalCacheCapacity];
        private int regionalCacheNext;
        private Vector4 regionalCacheSettings;
        private bool regionalCacheReady;
        private WeatherRegionProduct activeWeatherRegions;
        private long regionalEndpointSamples;
        /// <summary>Cumulative expensive local endpoint evaluations since enable/reset; each query misses at most eight endpoints and storage stays at 128 entries.</summary>
        public long RegionalEndpointSamples => regionalEndpointSamples;

        private struct RegionalReading
        {
            internal WeatherState State;
            internal Vector2 Wind;
            internal float Depth, MoistureBoost;
            internal static RegionalReading Blend(RegionalReading a, RegionalReading b, float k) => new RegionalReading {
                State = WeatherMachine.Blend(a.State, b.State, k), Wind = Vector2.Lerp(a.Wind, b.Wind, k),
                Depth = Mathf.Lerp(a.Depth, b.Depth, k), MoistureBoost = Mathf.Lerp(a.MoistureBoost, b.MoistureBoost, k)
            };
        }
        private struct RegionalCacheEntry
        {
            internal bool Valid;
            internal int X, Z;
            internal long Tick;
            internal RegionalReading Reading;
        }
        private void ResetRegionalSampling()
        {
            Array.Clear(regionalCache, 0, regionalCache.Length); regionalCacheNext = 0;
            regionalCacheReady = false; regionalEndpointSamples = 0;
        }
        private RegionalReading SampleRegionalReading(Vector3 position)
        {
            if (!ClimateBaselineProduct.Finite(regionalSampleIntervalSeconds) || regionalSampleIntervalSeconds < 1 || regionalSampleIntervalSeconds > 3600 ||
                !ClimateBaselineProduct.Finite(regionalSampleSpacingMetres) || regionalSampleSpacingMetres < 100 || regionalSampleSpacingMetres > 100000)
                throw new ArgumentOutOfRangeException(nameof(regionalSampleIntervalSeconds), "Regional cadence must be 1..3600 simulated seconds and spacing 100..100000 metres.");
            // A finite authored world range also prevents integer lattice overflow on arbitrary external sample positions.
            if (Mathf.Abs(position.x) > 1e9f || Mathf.Abs(position.z) > 1e9f)
                throw new ArgumentOutOfRangeException(nameof(position), "Regional sampling supports world XZ within +/-1,000,000,000 metres.");
            Vector4 settings = new Vector4(latitudeDeg, axialTiltDeg, longitudeDeg, standardMeridianDeg);
            if (regionalField == null) regionalField = new WeatherCellField();
            if (!regionalCacheReady || regionalSeed != regionalWeatherSeed || regionalNorth != northBearingDeg ||
                !regionalCacheSettings.Equals(settings) || cachedRegionalSpacing != regionalSampleSpacingMetres || cachedRegionalInterval != regionalSampleIntervalSeconds)
            {
                ResetRegionalSampling(); regionalField.Bind(regionalWeatherSeed, northBearingDeg);
                regionalSeed = regionalWeatherSeed; regionalNorth = northBearingDeg; regionalCacheSettings = settings;
                cachedRegionalSpacing = regionalSampleSpacingMetres; cachedRegionalInterval = regionalSampleIntervalSeconds; regionalCacheReady = true;
            }
            double x = position.x / (double)regionalSampleSpacingMetres, z = position.z / (double)regionalSampleSpacingMetres;
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            double tick = WorldInstantSeconds / regionalSampleIntervalSeconds;
            long it = (long)Math.Floor(tick); float phase = (float)(tick - it);
            RegionalReading a = SampleRegionalPlane(ix, iz, it, (float)(x - ix), (float)(z - iz));
            if (phase == 0) return a;
            return RegionalReading.Blend(a, SampleRegionalPlane(ix, iz, it + 1, (float)(x - ix), (float)(z - iz)), phase);
        }
        private float cachedRegionalSpacing, cachedRegionalInterval;
        private RegionalReading SampleRegionalPlane(int x, int z, long tick, float u, float v)
        {
            RegionalReading a = SampleRegionalEndpoint(x, z, tick);
            RegionalReading b = u > 0 ? SampleRegionalEndpoint(x + 1, z, tick) : a;
            RegionalReading lower = RegionalReading.Blend(a, b, u);
            if (v == 0) return lower;
            RegionalReading c = SampleRegionalEndpoint(x, z + 1, tick);
            RegionalReading d = u > 0 ? SampleRegionalEndpoint(x + 1, z + 1, tick) : c;
            return RegionalReading.Blend(lower, RegionalReading.Blend(c, d, u), v);
        }
        private RegionalReading SampleRegionalEndpoint(int x, int z, long tick)
        {
            for (int i = 0; i < regionalCache.Length; i++)
                if (regionalCache[i].Valid && regionalCache[i].X == x && regionalCache[i].Z == z && regionalCache[i].Tick == tick) return regionalCache[i].Reading;
            double instant = tick * (double)regionalSampleIntervalSeconds;
            int day = (int)(Math.Floor(instant / ClockState.SecondsPerDay) % 365) + 1;
            float seconds = (float)(instant % ClockState.SecondsPerDay), days = (float)(instant / ClockState.SecondsPerDay);
            float declination = ClockMath.DeclinationDegFromWinterPhase01(ClockMath.WinterPhase01(day, seconds), axialTiltDeg);
            float hourAngle = ClockMath.SolarHourAngleDeg(seconds, longitudeDeg, ClockMath.EquationOfTimeMinutes(day, seconds), clockReferenceMeridianDeg);
            float itcz = WeatherCellField.ItczLatitudeDeg(day, axialTiltDeg);
            float wx = x * regionalSampleSpacingMetres, wz = z * regionalSampleSpacingMetres;
            float pressure = regionalField.PressureHpa(wx, wz, latitudeDeg, itcz, days);
            var front = WeatherFront.ResolveFront(regionalField, wx, wz, latitudeDeg, days);
            var stability = WeatherStability.Resolve(regionalField, wx, wz, latitudeDeg, declination, hourAngle, itcz, days);
            var decks = CloudCover.ResolveDecks(regionalField, wx, wz, latitudeDeg, itcz, days);
            WeatherState.Builder air = default;
            air.pressureHpa = pressure; air.pressureTrendHpaPerHour = front.TendencyHpaPerDay / 24;
            air.frontKind = front.Kind; air.stabilityLapseCPerKm = stability.LapseCPerKm;
            air.cloudCoverLow = decks.Low; air.cloudCoverMid = decks.Mid; air.cloudCoverHigh = decks.High;
            air.cloudCover = stability.ConvectiveCover01;
            air.cloudType = CloudCover.ClassifyType(decks.Stratiform01, stability.ConvectiveCover01);
            var reading = new RegionalReading {
                State = air.Build(), Depth = front.DepthHpa,
                MoistureBoost = WeatherHumidity.FrontMoistureBoostC(regionalField, wx, wz, latitudeDeg, days),
                Wind = WeatherFront.AnomalyGradientHpaPerM(regionalField, wx, wz, latitudeDeg, days)
            };
            regionalCache[regionalCacheNext] = new RegionalCacheEntry { Valid = true, X = x, Z = z, Tick = tick, Reading = reading };
            regionalCacheNext = (regionalCacheNext + 1) % regionalCache.Length; regionalEndpointSamples++;
            return reading;
        }
    }
}
