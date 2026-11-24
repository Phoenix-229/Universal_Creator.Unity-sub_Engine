// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherPrecipType.cs
//  Purpose: What falls - rain, sleet or snow - decided by the wet-bulb
//           temperature and not the air temperature. A drop cools itself as it
//           evaporates on the way down, so snow reaches the ground at an air
//           temperature well above freezing whenever the air beneath the cloud
//           is dry. Air temperature alone misses exactly the case people notice.
//
//  WHY WET BULB AND NOT AIR TEMPERATURE
//  ------------------------------------
//  Wet-bulb is the temperature a falling hydrometeor actually reaches, because
//  evaporation into unsaturated air cools it below the dry-bulb reading. On a
//  dry, cold day the wet-bulb sits a degree or two under the air temperature,
//  which is why wet snow falls at +3 degC and the naive air-temperature test
//  turns it to rain. This reads the wet-bulb the humidity field already implies.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. The wet-bulb fit is AtmoMath's, in this same folder; the caller
//  supplies air temperature, humidity and the ground temperature as plain
//  scalars, and nothing here reaches into the ecology layer.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Precipitation type from the wet-bulb temperature. Every method is a pure
    /// function of its arguments; nothing here holds state.
    /// </summary>
    public static class WeatherPrecipType
    {
        /// <summary>What is falling: nothing, or one of the three phases.</summary>
        public enum PrecipKind { None, Snow, Sleet, Rain }
        /// <summary>
        /// 0..1 across the sleet band: 0 at or below the snow threshold, 1 at or above
        /// the rain threshold, linear between. This is the mix a shader blends, so the
        /// transition is a visible band of wet snow rather than a hard switch.
        /// </summary>
        public static float RainFraction01(float wetBulbC)
        {
            float span = AtmoConstants.SleetUpperWetBulbC
                         - AtmoConstants.SnowWetBulbThresholdC;

            return Mathf.Clamp01((wetBulbC - AtmoConstants.SnowWetBulbThresholdC) / span);
        }

        /// <summary>
        /// The phase from the wet-bulb: snow below the lower threshold, rain above the
        /// upper, sleet in the band between where snow and rain fall mixed.
        /// </summary>
        public static PrecipKind Classify(float wetBulbC)
        {
            if (wetBulbC < AtmoConstants.SnowWetBulbThresholdC) return PrecipKind.Snow;
            if (wetBulbC < AtmoConstants.SleetUpperWetBulbC) return PrecipKind.Sleet;
            return PrecipKind.Rain;
        }

        /// <summary>
        /// The phase, its wet-bulb, the snow/rain mix, and whether it accumulates.
        /// Frozen precipitation landing on ground already above freezing melts on
        /// contact - a wet road, no snowpack - so accumulation needs both a frozen
        /// phase and a cold enough surface.
        /// </summary>
        public struct PrecipReading
        {
            public PrecipKind Kind;
            public float WetBulbC;
            public float RainFraction01;
            public bool Accumulates;
        }

        /// <summary>
        /// The precipitation type at a point: the wet-bulb from the air temperature and
        /// humidity decides the phase and the mix, and the ground temperature decides
        /// whether a frozen phase survives to accumulate.
        /// </summary>
        public static PrecipReading Resolve(float airTempC, float relativeHumidity01,
                                            float surfaceTempC)
        {
            PrecipReading reading = default;
            float wetBulbC = AtmoMath.WetBulbC(airTempC, relativeHumidity01);
            reading.WetBulbC = wetBulbC;
            reading.Kind = Classify(wetBulbC);
            reading.RainFraction01 = RainFraction01(wetBulbC);

            bool frozen = reading.Kind != PrecipKind.Rain;
            reading.Accumulates = frozen && surfaceTempC <= 0f;
            return reading;
        }

        /// <summary>
        /// m above sea level. The altitude at which the wet-bulb falls to the snow
        /// threshold, walked up the lapse rate from the sample point: rain below the
        /// line, snow above it. Accumulation, albedo and snow load all read this one
        /// number, so rain and snow meet at exactly one altitude across every chunk.
        /// </summary>
        public static float SnowLineAltitudeM(float surfaceWetBulbC, float sampleAltitudeM)
        {
            float riseM = (surfaceWetBulbC - AtmoConstants.SnowWetBulbThresholdC)
                          / AtmoConstants.LapseRateCPerKm
                          * AtmoConstants.MetresPerKilometre;

            return sampleAltitudeM + riseM;
        }

        /// <summary>
        /// mm/h. The frontal rain rate from the front's kind and depth: a warm front is
        /// wide and gentle, a cold front narrow and hard, an occlusion between them, and
        /// the deeper the low the harder it rains, up to a reference depth.
        /// </summary>
        public static float FrontalRateMmPerH(WeatherFront.FrontKind kind, float depthHpa)
        {
            float reference;
            switch (kind)
            {
                case WeatherFront.FrontKind.Cold:
                    reference = AtmoConstants.ColdFrontRainMmPerH; break;
                case WeatherFront.FrontKind.Warm:
                    reference = AtmoConstants.WarmFrontRainMmPerH; break;
                case WeatherFront.FrontKind.Occluded:
                    reference = AtmoConstants.OccludedFrontRainMmPerH; break;
                case WeatherFront.FrontKind.Stationary:
                    reference = AtmoConstants.WarmFrontRainMmPerH; break;
                default:
                    return 0f;
            }

            float depthFraction =
                Mathf.Clamp01(depthHpa / AtmoConstants.FrontRainReferenceDepthHpa);
            return reference * depthFraction;
        }

        /// <summary>
        /// mm/h. The convective rain rate from the instability the stability test found:
        /// nothing in a stable column, up to a heavy afternoon reference when the column
        /// is deeply unstable and moist. The diurnal peak is already in the cover.
        /// </summary>
        public static float ConvectiveRateMmPerH(float convectiveCover01)
        {
            return Mathf.Clamp01(convectiveCover01)
                   * AtmoConstants.ConvectiveRainReferenceMmPerH;
        }

        /// <summary>
        /// mm/h. The three mechanisms combined: the strongest sets the rate and the other
        /// two add only a small fraction of themselves. Three 5 mm/h mechanisms make a
        /// light shower, not a deluge - a maximum with a blend, never a sum.
        /// </summary>
        public static float CombineRateMmPerH(float orographicMmPerH,
            float frontalMmPerH, float convectiveMmPerH)
        {
            float a = Mathf.Max(0f, orographicMmPerH);
            float b = Mathf.Max(0f, frontalMmPerH);
            float c = Mathf.Max(0f, convectiveMmPerH);

            float strongest = Mathf.Max(a, Mathf.Max(b, c));
            float others = a + b + c - strongest;
            return strongest + AtmoConstants.PrecipBlendFraction * others;
        }
    }
}
