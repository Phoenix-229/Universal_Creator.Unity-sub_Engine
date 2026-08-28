// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherState.cs
//  Purpose: The one snapshot every consumer reads - the whole sky at a point and
//           an instant, resolved once into plain values so nothing downstream
//           reaches back into the pressure field to re-derive its own answer. A
//           renderer, an audio bed and an ecology tick all read the same struct
//           and cannot disagree about what the weather is doing.
//
//  WHY A READONLY VALUE WITH NO REFERENCES
//  ---------------------------------------
//  A weather snapshot is data, not a live handle: copying it must be free and
//  safe to hold across a frame, so it is a readonly struct with no reference
//  fields. It is assembled through a mutable Builder and frozen by Build(); the
//  private constructor is the only door in, so a finished snapshot cannot drift.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. The enum types come from the resolvers in this same folder;
//  nothing here reaches into the ecology layer or the terrain module.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>The air mass over a point, by moisture source and thermal origin.</summary>
    public enum AirMassKind
    {
        Unclassified, MaritimeTropical, MaritimePolar,
        ContinentalTropical, ContinentalPolar, Arctic
    }

    /// <summary>
    /// An immutable weather snapshot. Assemble one through <see cref="Builder"/>
    /// and call <see cref="Builder.Build"/>; the fields are read-only thereafter.
    /// </summary>
    public readonly struct WeatherState
    {
        // Shipped names, kept verbatim so existing consumers bind unchanged.
        public readonly float precipitation;
        public readonly WeatherPrecipType.PrecipKind precipitationType;
        public readonly float wetness;
        public readonly float windSpeed;
        public readonly float fogDensity;
        public readonly float cloudCover;
        public readonly float temperature;
        public readonly float visibility;
        public readonly float stormActivity;
        public readonly float humidity;
        public readonly int weatherCellX;
        public readonly int weatherCellZ;

        // Appended: the physics the resolvers now produce.
        public readonly float pressureHpa;
        public readonly float pressureTrendHpaPerHour;
        public readonly float windDirectionDeg;
        public readonly float gustSpeed;
        public readonly float dewPointC;
        public readonly float wetBulbC;
        public readonly float relativeHumidity01;
        public readonly float cloudBaseM;
        public readonly float cloudBaseMidM;
        public readonly float cloudBaseHighM;
        public readonly float cloudCoverLow;
        public readonly float cloudCoverMid;
        public readonly float cloudCoverHigh;
        public readonly CloudCover.CloudType cloudType;
        public readonly float snowLineM;
        public readonly float snowDepthM;
        public readonly float precipitationMmPerHour;
        public readonly WeatherFront.FrontKind frontKind;
        public readonly float frontDistanceKm;
        public readonly float stabilityLapseCPerKm;
        public readonly float lightningActivity;
        public readonly AirMassKind airMassKind;

        /// <summary>0..1 grip the surface hands to vehicle and character systems: dry is one, wet less, snow less, glaze ice least.</summary>
        public readonly float surfaceFriction01;

        private WeatherState(in Builder b)
        {
            precipitation = b.precipitation;
            precipitationType = b.precipitationType;
            wetness = b.wetness;
            windSpeed = b.windSpeed;
            fogDensity = b.fogDensity;
            cloudCover = b.cloudCover;
            temperature = b.temperature;
            visibility = b.visibility;
            stormActivity = b.stormActivity;
            humidity = b.humidity;
            weatherCellX = b.weatherCellX;
            weatherCellZ = b.weatherCellZ;
            pressureHpa = b.pressureHpa;
            pressureTrendHpaPerHour = b.pressureTrendHpaPerHour;
            windDirectionDeg = b.windDirectionDeg;
            gustSpeed = b.gustSpeed;
            dewPointC = b.dewPointC;
            wetBulbC = b.wetBulbC;
            relativeHumidity01 = b.relativeHumidity01;
            cloudBaseM = b.cloudBaseM;
            cloudBaseMidM = b.cloudBaseMidM;
            cloudBaseHighM = b.cloudBaseHighM;
            cloudCoverLow = b.cloudCoverLow;
            cloudCoverMid = b.cloudCoverMid;
            cloudCoverHigh = b.cloudCoverHigh;
            cloudType = b.cloudType;
            snowLineM = b.snowLineM;
            snowDepthM = b.snowDepthM;
            precipitationMmPerHour = b.precipitationMmPerHour;
            frontKind = b.frontKind;
            frontDistanceKm = b.frontDistanceKm;
            stabilityLapseCPerKm = b.stabilityLapseCPerKm;
            lightningActivity = b.lightningActivity;
            airMassKind = b.airMassKind;
            surfaceFriction01 = b.surfaceFriction01;
        }

        /// <summary>
        /// The mutable scratch a resolver fills field by field, then freezes with
        /// <see cref="Build"/> into an immutable <see cref="WeatherState"/>.
        /// </summary>
        public struct Builder
        {
            public float precipitation;
            public WeatherPrecipType.PrecipKind precipitationType;
            public float wetness;
            public float windSpeed;
            public float fogDensity;
            public float cloudCover;
            public float temperature;
            public float visibility;
            public float stormActivity;
            public float humidity;
            public int weatherCellX;
            public int weatherCellZ;
            public float pressureHpa;
            public float pressureTrendHpaPerHour;
            public float windDirectionDeg;
            public float gustSpeed;
            public float dewPointC;
            public float wetBulbC;
            public float relativeHumidity01;
            public float cloudBaseM;
            public float cloudBaseMidM;
            public float cloudBaseHighM;
            public float cloudCoverLow;
            public float cloudCoverMid;
            public float cloudCoverHigh;
            public CloudCover.CloudType cloudType;
            public float snowLineM;
            public float snowDepthM;
            public float precipitationMmPerHour;
            public WeatherFront.FrontKind frontKind;
            public float frontDistanceKm;
            public float stabilityLapseCPerKm;
            public float lightningActivity;
            public AirMassKind airMassKind;
            public float surfaceFriction01;

            /// <summary>Freezes the filled scratch into an immutable snapshot.</summary>
            public WeatherState Build() { return new WeatherState(in this); }
        }
    }
}
