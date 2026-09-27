// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWeatherSystemManager.Weather.cs   (partial of UCWeatherSystemManager)
//  Purpose: The single WeatherState source the lighting driver reads, mirroring
//           ResolveClockFor/DefaultClock exactly (W-04). Until the full pressure-
//           field weather sim publishes a live snapshot, the sky must still be driven
//           by the world's AUTHORED air - cloud cover, humidity, temperature - not by
//           a guess. This resolves those authored scalars into a WeatherState so
//           AtmosphereState.From has real inputs, and hands back a documented clear-sky
//           default when a world has no weather system at all.
//
//  WHY HERE, AND WHY ONE OWNER
//  ---------------------------
//  UCLightingSystemManager already reads the clock through ResolveClockFor so it never
//  declares a latitude of its own. The weather it needs must come the same way, from
//  the same owner, or the sky and the phenology would read two different skies. This is
//  that accessor: one place the authored air becomes a snapshot, so nothing downstream
//  re-derives it a second way. (Matches the ResolveClockFor / DefaultClock pattern in
//  UCWeatherSystemManager.cs.)
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        /// <summary>
        /// The weather snapshot governing a transform's world: this manager's authored
        /// air when there is a weather system, otherwise the documented clear-sky
        /// default. What <c>UCLightingSystemManager</c>'s driver reads so it never
        /// invents its own cloud cover or humidity. Mirrors <see cref="ResolveClockFor"/>.
        /// </summary>
        public static WeatherState ResolveWeatherFor(Transform context)
        {
            UCWeatherSystemManager weather = FindForWorld(context);
            return weather != null ? weather.ResolveWeather() : DefaultWeather();
        }

        /// <summary>
        /// This world's authored air as a resolved snapshot. The full pressure-field
        /// sim does not yet publish a live WeatherState (see the :1056 note that
        /// cloudCoverage "is still a declared placeholder that nothing renders"); until
        /// it does, the sky is driven by the three authored numbers that DO exist -
        /// cloud cover, relative humidity and sea-level temperature - which is real
        /// authored air, not a guess. The authored coverage is carried as a single low
        /// deck so the combined cover the sun is dimmed by equals it exactly
        /// (CombinedCover01 = 1-(1-low)(1-0)(1-0) = low); the mid and high decks stay
        /// clear until the sim resolves them per layer.
        /// </summary>
        public WeatherState ResolveWeather()
        {
            float cover = Mathf.Clamp01(cloudCoverage);
            float rh = Mathf.Clamp01(humidity);

            WeatherState.Builder b = new WeatherState.Builder();
            b.cloudCover = cover;
            b.cloudCoverLow = cover;   // one authored deck; combined cover == cloudCoverage
            b.cloudCoverMid = 0f;
            b.cloudCoverHigh = 0f;
            b.humidity = rh;
            b.relativeHumidity01 = rh;
            b.temperature = temperatureCelsius;
            // wetness/precipitation/wind left at zero: no live sim publishes them yet, and
            // a zero here is the honest "dry, calm" default rather than an invented storm.
            return b.Build();
        }

        /// <summary>
        /// The documented fallback weather when a world has no weather system, built the
        /// same way as <see cref="ResolveWeather"/> so it cannot drift from it: a clear,
        /// lightly-humid, calm day. Pairs with <see cref="DefaultClock"/> (latitude 45,
        /// midsummer noon), so a lighting rig dropped loose into a scene still lights up
        /// against a real sky instead of a black one.
        /// </summary>
        public static WeatherState DefaultWeather()
        {
            WeatherState.Builder b = new WeatherState.Builder();
            b.cloudCover = 0f;
            b.cloudCoverLow = 0f;
            b.cloudCoverMid = 0f;
            b.cloudCoverHigh = 0f;
            b.humidity = 0.5f;
            b.relativeHumidity01 = 0.5f;
            b.temperature = 15f;
            return b.Build();
        }
    }
}
