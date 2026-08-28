// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherMachine.cs
//  Purpose: Turns the instantaneous physics into a weather you can watch change.
//           The resolvers give a fresh snapshot every tick; this classifies it
//           into one of four states, holds that state for a minimum dwell so a
//           value sitting on a threshold cannot flicker, and eases every visible
//           field from the old state to the new one so the sky never snaps.
//
//  WHY A DWELL AND A BLEND
//  -----------------------
//  Precipitation crossing 0.1 mm/h or cloud crossing 0.65 would otherwise toggle
//  the state on and off frame to frame right at the boundary. The dwell holds the
//  last state briefly so the boundary is crossed once, not oscillated across; the
//  SmoothStep blend then carries the continuous fields across the change over a
//  transition window rather than jumping them.
//
//  WHY CLOCK TIME AND NOT FRAME TIME
//  ---------------------------------
//  The dwell and the blend are measured in simulated seconds handed in by the
//  caller, never in frames: two runs stepped to the same simulated time land on
//  the same state, whatever the frame rate did in between. (C-06)
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Reads a WeatherState and thresholds from AtmoConstants; nothing
//  here reaches into the ecology layer or the terrain module.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>The four weather states, classified from the physics in a fixed order.</summary>
    public enum WeatherKind { Clear, Overcast, Rain, Snow }

    /// <summary>
    /// The stateful smoothing layer over the resolvers. One instance per weather
    /// cell; <see cref="Tick"/> is the only entry point.
    /// </summary>
    public sealed class WeatherMachine
    {
        private readonly float transitionSeconds;
        private readonly float minDwellSeconds;

        private WeatherKind currentKind;
        private WeatherState output;
        private WeatherState blendFrom;
        private WeatherState blendTo;
        private float transitionStartSeconds;
        private float lastChangeSeconds;
        private bool initialised;

        /// <summary>Uses the default transition and dwell windows.</summary>
        public WeatherMachine()
            : this(AtmoConstants.WeatherTransitionSeconds, AtmoConstants.WeatherMinDwellSeconds) { }

        /// <summary>Both windows are in simulated seconds.</summary>
        public WeatherMachine(float transitionSeconds, float minDwellSeconds)
        {
            this.transitionSeconds = transitionSeconds;
            this.minDwellSeconds = minDwellSeconds;
        }

        /// <summary>The state the machine currently holds.</summary>
        public WeatherKind CurrentKind { get { return currentKind; } }

        /// <summary>The last blended snapshot the machine produced.</summary>
        public WeatherState Current { get { return output; } }

        /// <summary>
        /// The state a raw snapshot implies, in fixed order: a wet sky is Snow or Rain
        /// by the wet-bulb, an otherwise cloudy sky is Overcast, everything else Clear.
        /// </summary>
        public static WeatherKind Classify(in WeatherState s)
        {
            if (s.precipitationMmPerHour > AtmoConstants.PrecipDetectableMmPerH)
                return s.wetBulbC < AtmoConstants.SnowWetBulbThresholdC
                       ? WeatherKind.Snow : WeatherKind.Rain;

            if (s.cloudCover > AtmoConstants.OvercastCloudCover01)
                return WeatherKind.Overcast;

            return WeatherKind.Clear;
        }

        /// <summary>
        /// Advances the machine to <paramref name="simSeconds"/> against the freshest raw
        /// snapshot and returns the blended state. A new state is entered only once the
        /// dwell has elapsed since the last change; the visible fields then ease across
        /// the transition window. Idempotent at a fixed time: called twice with the same
        /// arguments it returns the same snapshot.
        /// </summary>
        public WeatherState Tick(in WeatherState target, float simSeconds)
        {
            if (!initialised)
            {
                currentKind = Classify(target);
                blendFrom = target;
                blendTo = target;
                output = target;
                transitionStartSeconds = simSeconds;
                lastChangeSeconds = simSeconds;
                initialised = true;
                return output;
            }

            WeatherKind targetKind = Classify(target);
            bool dwellElapsed = simSeconds - lastChangeSeconds >= minDwellSeconds;

            if (targetKind != currentKind && dwellElapsed)
            {
                blendFrom = output;
                transitionStartSeconds = simSeconds;
                currentKind = targetKind;
                lastChangeSeconds = simSeconds;
            }

            blendTo = target;

            float t = transitionSeconds > 0f
                      ? Mathf.Clamp01((simSeconds - transitionStartSeconds) / transitionSeconds)
                      : 1f;
            output = Blend(blendFrom, blendTo, Mathf.SmoothStep(0f, 1f, t));
            return output;
        }

        /// <summary>
        /// Eases the continuous fields from <paramref name="a"/> to <paramref name="b"/>
        /// by <paramref name="k"/>; the categorical fields snap to <paramref name="b"/>,
        /// since a state, a front kind or a cell index has no half-way value. Wind
        /// direction eases the short way around the compass rather than through north.
        /// </summary>
        private static WeatherState Blend(in WeatherState a, in WeatherState b, float k)
        {
            WeatherState.Builder o = default;

            o.precipitation = Mathf.Lerp(a.precipitation, b.precipitation, k);
            o.wetness = Mathf.Lerp(a.wetness, b.wetness, k);
            o.windSpeed = Mathf.Lerp(a.windSpeed, b.windSpeed, k);
            o.fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, k);
            o.cloudCover = Mathf.Lerp(a.cloudCover, b.cloudCover, k);
            o.temperature = Mathf.Lerp(a.temperature, b.temperature, k);
            o.visibility = Mathf.Lerp(a.visibility, b.visibility, k);
            o.stormActivity = Mathf.Lerp(a.stormActivity, b.stormActivity, k);
            o.humidity = Mathf.Lerp(a.humidity, b.humidity, k);
            o.pressureHpa = Mathf.Lerp(a.pressureHpa, b.pressureHpa, k);
            o.pressureTrendHpaPerHour =
                Mathf.Lerp(a.pressureTrendHpaPerHour, b.pressureTrendHpaPerHour, k);
            o.windDirectionDeg = Mathf.LerpAngle(a.windDirectionDeg, b.windDirectionDeg, k);
            o.gustSpeed = Mathf.Lerp(a.gustSpeed, b.gustSpeed, k);
            o.dewPointC = Mathf.Lerp(a.dewPointC, b.dewPointC, k);
            o.wetBulbC = Mathf.Lerp(a.wetBulbC, b.wetBulbC, k);
            o.relativeHumidity01 = Mathf.Lerp(a.relativeHumidity01, b.relativeHumidity01, k);
            o.cloudBaseM = Mathf.Lerp(a.cloudBaseM, b.cloudBaseM, k);
            o.cloudBaseMidM = Mathf.Lerp(a.cloudBaseMidM, b.cloudBaseMidM, k);
            o.cloudBaseHighM = Mathf.Lerp(a.cloudBaseHighM, b.cloudBaseHighM, k);
            o.cloudCoverLow = Mathf.Lerp(a.cloudCoverLow, b.cloudCoverLow, k);
            o.cloudCoverMid = Mathf.Lerp(a.cloudCoverMid, b.cloudCoverMid, k);
            o.cloudCoverHigh = Mathf.Lerp(a.cloudCoverHigh, b.cloudCoverHigh, k);
            o.snowLineM = Mathf.Lerp(a.snowLineM, b.snowLineM, k);
            o.snowDepthM = Mathf.Lerp(a.snowDepthM, b.snowDepthM, k);
            o.precipitationMmPerHour =
                Mathf.Lerp(a.precipitationMmPerHour, b.precipitationMmPerHour, k);
            o.frontDistanceKm = Mathf.Lerp(a.frontDistanceKm, b.frontDistanceKm, k);
            o.stabilityLapseCPerKm =
                Mathf.Lerp(a.stabilityLapseCPerKm, b.stabilityLapseCPerKm, k);
            o.lightningActivity = Mathf.Lerp(a.lightningActivity, b.lightningActivity, k);

            // Categorical fields have no half-way value: they snap to the target.
            o.precipitationType = b.precipitationType;
            o.cloudType = b.cloudType;
            o.frontKind = b.frontKind;
            o.airMassKind = b.airMassKind;
            o.weatherCellX = b.weatherCellX;
            o.weatherCellZ = b.weatherCellZ;

            return o.Build();
        }
    }
}
