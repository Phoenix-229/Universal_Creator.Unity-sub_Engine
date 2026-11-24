// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    FogSimulation.cs
//  Purpose: Fog, resolved by the mechanism that forms it rather than as one global
//           density. A world has four kinds of fog and they look different and sit
//           in different places: dawn fog pooling in a valley, sea fog rolling off
//           cold water, cloud sitting on a windward mountainside, and wisps of
//           steam off a warm lake in autumn. Each is a gate that opens only under
//           its own conditions; the strongest present wins, so a valley is under
//           radiation fog OR sea fog, never the sum of the two.
//
//  WHY AN EXPONENTIAL VERTICAL PROFILE
//  -----------------------------------
//  Density falls off with height as exp(-(h - groundH) / scaleHeight), not
//  linearly and not as a plane. That single shape is what lets fog fill a valley
//  to a level and leave the ridge above it clear: the ridge is simply higher up
//  the same curve. A plane-based fog cannot do that - it fogs the ridge and the
//  valley floor by the same amount.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Everything the mechanisms need - temperatures, dew point,
//  humidity, wind, sun elevation, cloud base, the wetness index and the windward
//  exposure - arrives as plain scalars. It does not compute them, and it never
//  reaches into the ecology layer or the terrain.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Surface fog density on a 0..1 scale, resolved per mechanism, plus the
    /// exponential fall-off with height. Every method is a pure function of its
    /// arguments.
    /// </summary>
    public static class FogSimulation
    {
        /// <summary>
        /// 0..1. Radiation fog: the dawn valley fog. It needs a clear sky to cool to,
        /// calm air so the cold layer can settle, air that has reached its dew point,
        /// and night - the four conditions all have to hold, so they multiply. It
        /// pools in hollows through the wetness index, and it burns off as the sun
        /// climbs, which is why it is a dawn phenomenon and not a permanent one.
        /// </summary>
        public static float RadiationFog01(float cloudCover01, float windSpeedMps,
                                           float airTempC, float dewPointC,
                                           float sunElevationDeg, float twi)
        {
            // Clear sky: the ground can only radiate its heat away under an open sky.
            float clear = 1f - Mathf.Clamp01(cloudCover01);

            // Calm: any real wind mixes the cold surface layer back into the air above.
            float calm = 1f - Mathf.Clamp01(Mathf.Max(0f, windSpeedMps) / AtmoConstants.FogCalmWindMps);

            // Saturated: fog is water condensing, so the air must be at its dew point.
            float depression = Mathf.Max(0f, airTempC - dewPointC);
            float saturated = 1f - Mathf.Clamp01(depression / AtmoConstants.FogDewpointDepressionBandC);

            // Night: the sun undoes the radiative cooling, so fog thins as it rises
            // and is gone above the burn-off elevation.
            float night = Mathf.Clamp01(
                (AtmoConstants.FogBurnoffSunElevationDeg - sunElevationDeg)
                / AtmoConstants.FogBurnoffSunElevationDeg);

            float forms = clear * calm * saturated * night;

            // Pooling: an open flat still fogs, but a hollow with a high wetness index
            // draws in the cold drainage air and fogs far deeper.
            float span = AtmoConstants.FogValleyTwiFull - AtmoConstants.FogValleyTwiMin;
            float valley01 = span > 0f
                ? Mathf.Clamp01((twi - AtmoConstants.FogValleyTwiMin) / span) : 0f;
            float pooling = Mathf.Lerp(AtmoConstants.FogValleyBase01, 1f, valley01);

            return Mathf.Clamp01(forms * pooling);
        }

        /// <summary>
        /// 0..1. Advection fog: warm moist air blown over a cold surface, which chills
        /// it from below to its dew point. It is the sea fog that rolls in and sits
        /// there - it depends on the air being warmer than the ground and humid, not
        /// on the time of day, so unlike radiation fog it does not burn off at noon.
        /// </summary>
        public static float AdvectionFog01(float airTempC, float surfaceTempC,
                                           float relativeHumidity01)
        {
            // Warm air over a cold surface: the cooling that condenses the fog.
            float warmOverCold = Mathf.Clamp01(
                (airTempC - surfaceTempC) / AtmoConstants.FogAdvectionTempSpreadC);

            // Moist: there has to be enough water in the air to reach saturation as it
            // cools. Below the threshold no amount of cooling fogs it.
            float headroom = 1f - AtmoConstants.FogHumidityThreshold01;
            float moist = headroom > 0f
                ? Mathf.Clamp01((relativeHumidity01 - AtmoConstants.FogHumidityThreshold01) / headroom)
                : 0f;

            return Mathf.Clamp01(warmOverCold * moist);
        }

        /// <summary>
        /// 0..1. Upslope fog: air forced up a windward slope reaches its lifting
        /// condensation level and the sky's cloud sits on the ground. It is the same
        /// cloud as the one overhead, just met at altitude - so it thickens with how
        /// far the ground rises above the cloud base and with how exposed the slope is
        /// to the wind driving the air up it.
        /// </summary>
        public static float UpslopeFog01(float cellAltitudeM, float cloudBaseM,
                                         float windwardExposure01)
        {
            float aboveBase = Mathf.Clamp01(
                (cellAltitudeM - cloudBaseM) / AtmoConstants.FogUpslopeBandM);
            float windward = Mathf.Clamp01(windwardExposure01);

            return Mathf.Clamp01(aboveBase * windward);
        }

        /// <summary>
        /// 0..1. Steam fog: cold air over warm water, so the water evaporates into air
        /// that cannot hold it and it re-condenses as wisps. Very local, and only over
        /// water - the autumn-morning smoke off a lake. Gated by the surface being
        /// water at all, so it never appears over land.
        /// </summary>
        public static float SteamFog01(float waterTempC, float airTempC, float isWater01)
        {
            float coldAirOverWarmWater = Mathf.Clamp01(
                (waterTempC - airTempC) / AtmoConstants.FogSteamTempSpreadC);

            return Mathf.Clamp01(coldAirOverWarmWater * Mathf.Clamp01(isWater01));
        }

        /// <summary>
        /// The surface fog density a cell resolves to: the strongest of the four
        /// mechanisms, never their sum. Two kinds of fog in one place are still just
        /// fog, and adding them would drive an already-white valley past white.
        /// </summary>
        public static float SurfaceDensity01(float radiation01, float advection01,
                                             float upslope01, float steam01)
        {
            float density = Mathf.Max(radiation01, advection01);
            density = Mathf.Max(density, upslope01);
            density = Mathf.Max(density, steam01);
            return Mathf.Clamp01(density);
        }

        /// <summary>
        /// Fog density at a height above the fog's ground level. Exponential, so the
        /// layer fills to a level and thins with height rather than cutting off at a
        /// plane: this is what leaves a ridge clear above a fogged valley floor.
        /// </summary>
        public static float DensityAtHeight(float surfaceDensity01, float heightAboveGroundM,
                                            float scaleHeightM)
        {
            if (scaleHeightM <= 0f) return 0f;
            float h = Mathf.Max(0f, heightAboveGroundM);
            return Mathf.Clamp01(surfaceDensity01) * Mathf.Exp(-h / scaleHeightM);
        }
    }
}
