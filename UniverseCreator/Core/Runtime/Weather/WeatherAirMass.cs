// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherAirMass.cs
//  Purpose: The identity a weather system carries with it - the air it is made
//           of, not the ground it happens to sit over. Where the air came from
//           sets two things: the origin latitude fixes the thermal class
//           (tropical, polar or arctic) and the origin surface fixes the moisture
//           source (maritime or continental). Those travel with the system, so a
//           cold front swaps in genuinely colder, drier air and does not merely
//           switch the rain on over the same temperature.
//
//  WHY AN IDENTITY AND NOT A LOCAL RECOMPUTE
//  -----------------------------------------
//  If temperature and dew point were recomputed from the arriving point every
//  step, a front would be a line where the rain starts and nothing else changes.
//  Tagging the system with an air mass and carrying its anomalies is what makes
//  the passage a step change in the air itself - the thing a barometer, a
//  thermometer and a windowpane all agree happened.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Classifies from an origin latitude and a continentality scalar,
//  and reads its anomalies from AtmoConstants; nothing here reaches into the
//  ecology layer or the terrain module.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Air mass classification and the temperature and dew-point anomalies each
    /// kind carries. Every method is a pure function of its arguments.
    /// </summary>
    public static class WeatherAirMass
    {
        /// <summary>
        /// The air mass of a system from where its air formed: poleward of the Arctic
        /// Circle it is Arctic; equatorward of the subtropical ridge it is Tropical,
        /// otherwise Polar; and a dry origin surface makes it Continental over Maritime.
        /// </summary>
        public static AirMassKind Classify(float originLatitudeDeg, float continentality01)
        {
            float absLatitudeDeg = Mathf.Abs(originLatitudeDeg);
            if (absLatitudeDeg >= AtmoConstants.ArcticAirMassLatitudeDeg)
                return AirMassKind.Arctic;

            bool maritime = continentality01 < 0.5f;
            if (absLatitudeDeg <= AtmoConstants.SubtropicalHighLatitudeDeg)
                return maritime ? AirMassKind.MaritimeTropical
                                : AirMassKind.ContinentalTropical;

            return maritime ? AirMassKind.MaritimePolar : AirMassKind.ContinentalPolar;
        }

        /// <summary>degC. How much warmer or colder this air mass is than the local climatology.</summary>
        public static float TemperatureAnomalyC(AirMassKind kind)
        {
            switch (kind)
            {
                case AirMassKind.MaritimeTropical:
                case AirMassKind.ContinentalTropical:
                    return AtmoConstants.TropicalAirTempAnomalyC;
                case AirMassKind.MaritimePolar:
                case AirMassKind.ContinentalPolar:
                    return AtmoConstants.PolarAirTempAnomalyC;
                case AirMassKind.Arctic:
                    return AtmoConstants.ArcticAirTempAnomalyC;
                default:
                    return 0f;
            }
        }

        /// <summary>degC of dew-point offset. Maritime air is moist, continental dry, arctic driest.</summary>
        public static float DewPointAnomalyC(AirMassKind kind)
        {
            switch (kind)
            {
                case AirMassKind.MaritimeTropical:
                case AirMassKind.MaritimePolar:
                    return AtmoConstants.MaritimeAirDewAnomalyC;
                case AirMassKind.ContinentalTropical:
                case AirMassKind.ContinentalPolar:
                    return AtmoConstants.ContinentalAirDewAnomalyC;
                case AirMassKind.Arctic:
                    return AtmoConstants.ArcticAirDewAnomalyC;
                default:
                    return 0f;
            }
        }

        /// <summary>The kind and both anomalies in one read, for a system that carries its air with it.</summary>
        public struct AirMassAnomaly
        {
            public AirMassKind Kind;
            public float TemperatureAnomalyC;
            public float DewPointAnomalyC;
        }

        /// <summary>Classifies the origin and packages the anomalies the system carries.</summary>
        public static AirMassAnomaly Resolve(float originLatitudeDeg, float continentality01)
        {
            AirMassAnomaly anomaly;
            anomaly.Kind = Classify(originLatitudeDeg, continentality01);
            anomaly.TemperatureAnomalyC = TemperatureAnomalyC(anomaly.Kind);
            anomaly.DewPointAnomalyC = DewPointAnomalyC(anomaly.Kind);
            return anomaly;
        }
    }
}
