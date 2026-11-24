// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherHumidity.cs
//  Purpose: Relative humidity as a property of the air mass rather than the
//           ground under it. The moisture is a dew point carried in by the air -
//           maritime and high, or continental and low - raised where an
//           approaching low advects moist air ahead of its front. Relative
//           humidity is then e_s(dew)/e_s(T), so it rises to a dawn maximum and
//           falls to an afternoon minimum on its own as the temperature swings.
//
//  WHY DEW POINT AND NOT RELATIVE HUMIDITY DIRECTLY
//  ------------------------------------------------
//  Relative humidity is the wrong thing to store, because it is not conserved:
//  the same parcel is 60 % at noon and 100 % at dawn without gaining a gram of
//  water. The dew point is what the air mass actually carries and holds through
//  the day, so storing that and deriving RH from the hour's temperature gives
//  the daily cycle for free - no authored curve, and it cannot disagree with the
//  temperature field it is read against.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Thermodynamics from AtmoMath, the systems from WeatherCellField,
//  both in this folder; nothing here reaches into the ecology layer.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// The humidity field. Every method is a pure function of its arguments;
    /// nothing here holds state.
    /// </summary>
    public static class WeatherHumidity
    {
        /// <summary>
        /// degC. Dew point of the air mass at a point: interpolated between a moist
        /// maritime source and a dry continental one by continentality (0 maritime, 1
        /// continental), then raised by any moisture an approaching low advects in.
        /// </summary>
        public static float AirMassDewPointC(float continentality01,
                                             float frontMoistureBoostC)
        {
            float baseDewC = Mathf.Lerp(AtmoConstants.MaritimeAirDewPointC,
                                        AtmoConstants.ContinentalAirDewPointC,
                                        Mathf.Clamp01(continentality01));

            return baseDewC + frontMoistureBoostC;
        }
        /// <summary>
        /// degC of dew-point rise. An approaching low advects moist air ahead of its
        /// front; the deeper it sits below the mean pressure, the more it carries.
        /// A ridge (pressure above the mean, so a non-positive depth) advects nothing
        /// and a null field returns zero, so a settled sky adds no moisture.
        /// </summary>
        public static float FrontMoistureBoostC(WeatherCellField field, float worldX,
            float worldZ, float latitudeDeg, float timeDays)
        {
            if (field == null || !field.IsBound) return 0f;

            float depthHpa = -field.PressureAnomalyHpa(worldX, worldZ, latitudeDeg, timeDays);
            if (depthHpa <= 0f) return 0f;

            return Mathf.Clamp(depthHpa * AtmoConstants.FrontMoistureBoostCPerHpa,
                               0f, AtmoConstants.FrontMoistureMaxBoostC);
        }

        /// <summary>
        /// 0..1. Relative humidity of the air at a point: the air mass dew point read
        /// against the hour's air temperature, e_s(dew)/e_s(T). The dew point is capped
        /// at the air temperature so a cold night saturates at 100 % rather than passing
        /// through it, and AtmoMath clamps the ratio into range regardless.
        /// </summary>
        public static float Resolve01(WeatherCellField field, float worldX, float worldZ,
            float latitudeDeg, float timeDays, float continentality01, float airTempC)
        {
            float boostC = FrontMoistureBoostC(field, worldX, worldZ, latitudeDeg, timeDays);
            float dewC = AirMassDewPointC(continentality01, boostC);
            dewC = Mathf.Min(dewC, airTempC);

            return AtmoMath.RelativeHumidity01(airTempC, dewC);
        }
    }
}
