// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    AtmoMath.cs
//  Purpose: The thermodynamics of moist air, and the climate laws that follow
//           from it. Pure static, no state, no Unity object touched.
//
//  WHY THESE LIVE IN Weather/ AND NOT IN Ecology/EcoMath.cs
//  --------------------------------------------------------
//  Six of the functions below were first written in EcoMath, because the
//  vegetation layer needed them first. They are not ecology: a lapse rate, an
//  evaporative demand and a share of the year above freezing are properties of
//  the atmosphere that plants happen to read. They own their bodies here now and
//  EcoMath forwards to them, which matters for one hard reason - the dependency
//  direction is Ecology -> Weather and never the reverse, so a climate normal
//  computed in this folder cannot reach into EcoMath at all. Two copies would
//  have been the only alternative, and two copies of one formula is not
//  redundancy: one of them eventually gets a fix and the other does not.
//
//  EVERY VALUE IS A NAMED CONSTANT FROM AtmoConstants.
//  A bare 17.27 in a saturation curve is unreviewable. The names carry the unit
//  and the source with them.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Moist-air thermodynamics and the climate laws built on it. Every method is
    /// a pure function of its arguments.
    /// </summary>
    public static class AtmoMath
    {
        // =====================================================================
        //  SECTION 1  -  WATER IN THE AIR  (L-05, L-06)
        // =====================================================================

        /// <summary>
        /// kPa. Saturation vapour pressure over water (Tetens). How much water
        /// air at this temperature can hold - the denominator of every humidity
        /// question, and the reason warm air can carry a storm's worth of rain.
        /// </summary>
        public static float SaturationVapourPressureKPa(float tempC)
        {
            return AtmoConstants.TetensCoefficientKPa
                   * Mathf.Exp(AtmoConstants.TetensA * tempC
                               / (tempC + AtmoConstants.TetensB));
        }

        /// <summary>kPa. Actual vapour pressure: the saturation value scaled by relative humidity.</summary>
        public static float VapourPressureKPa(float tempC, float rh01)
        {
            return SaturationVapourPressureKPa(tempC) * Mathf.Clamp01(rh01);
        }

        /// <summary>
        /// 0-1 relative humidity from the temperature and dew point, which is the
        /// pair a weather station actually reports.
        /// </summary>
        public static float RelativeHumidity01(float tempC, float dewPointC)
        {
            float saturation = SaturationVapourPressureKPa(tempC);
            if (saturation <= 0f) return 0f;

            return Mathf.Clamp01(SaturationVapourPressureKPa(dewPointC) / saturation);
        }

        /// <summary>
        /// degC. Dew point (L-06): Tetens solved backwards for the temperature at
        /// which today's moisture would saturate. Below the air temperature by
        /// definition, and equal to it in fog.
        /// </summary>
        public static float DewPointC(float tempC, float rh01)
        {
            float e = VapourPressureKPa(tempC, rh01);

            // Bone-dry air has no dew point. Reported as the wet-bulb floor
            // rather than as minus infinity, which is what the log would give.
            if (e <= 0f) return AtmoConstants.WetBulbValidMinC;

            float gamma = Mathf.Log(e / AtmoConstants.TetensCoefficientKPa);

            // The denominator vanishes only far above any air temperature a world
            // reaches; guarded so a bad input cannot divide by zero silently.
            float denominator = AtmoConstants.TetensA - gamma;
            if (Mathf.Abs(denominator) < AtmoConstants.TetensMinDenominator) return tempC;

            return AtmoConstants.TetensB * gamma / denominator;
        }

        // =====================================================================
        //  SECTION 2  -  WET BULB AND CLOUD BASE  (L-07, L-08)
        // =====================================================================

        /// <summary>
        /// degC. Wet-bulb temperature (Stull's single-pass fit). This is the number
        /// that decides rain against sleet against snow, because it accounts for the
        /// cooling a falling drop does to itself by evaporating - which is why snow
        /// reaches the ground at an air temperature above freezing.
        ///
        /// INPUTS ARE CLAMPED, DELIBERATELY. The fit holds from about -20 to +50 degC
        /// and above about 5 % humidity; outside that the polynomial diverges, and a
        /// diverged wet bulb makes it snow at +30 degC. Clamping bends the answer at
        /// the extremes, which is visible and wrong by a degree; not clamping breaks
        /// it, which is invisible and wrong by thirty.
        /// </summary>
        public static float WetBulbC(float tempC, float rh01)
        {
            float t = Mathf.Clamp(tempC,
                                  AtmoConstants.WetBulbValidMinC,
                                  AtmoConstants.WetBulbValidMaxC);

            float rhPercent = Mathf.Clamp(Mathf.Clamp01(rh01) * AtmoConstants.PercentPerFraction,
                                          AtmoConstants.WetBulbValidMinRhPercent,
                                          AtmoConstants.PercentPerFraction);

            return t * Mathf.Atan(AtmoConstants.StullA
                                  * Mathf.Sqrt(rhPercent + AtmoConstants.StullB))
                   + Mathf.Atan(t + rhPercent)
                   - Mathf.Atan(rhPercent - AtmoConstants.StullC)
                   + AtmoConstants.StullD
                     * Mathf.Pow(rhPercent, AtmoConstants.StullRhExponent)
                     * Mathf.Atan(AtmoConstants.StullE * rhPercent)
                   - AtmoConstants.StullF;
        }

        /// <summary>
        /// m above ground. Lifting condensation level (Espy): where rising air first
        /// saturates, which is the height a cloud base sits at. The spread between
        /// temperature and dew point is the whole input - dry air lifts further
        /// before it clouds over, which is why desert cloud decks are high.
        /// </summary>
        public static float LiftingCondensationLevelM(float tempC, float dewPointC)
        {
            float spread = tempC - dewPointC;
            if (spread <= 0f) return 0f;   // already saturated: cloud is on the ground

            return AtmoConstants.LclMetresPerCelsius * spread;
        }

        // =====================================================================
        //  SECTION 3  -  PRESSURE AND DENSITY  (L-10)
        // =====================================================================

        /// <summary>
        /// hPa. Pressure at altitude by the hypsometric relation for the standard
        /// atmosphere. Falls to about half of sea level by 5.5 km.
        /// </summary>
        public static float PressureHpa(float seaLevelHpa, float altitudeM)
        {
            // Above roughly 44 km the base goes negative and the power is complex.
            // No world reaches it, but a clamp costs nothing and a NaN pressure
            // would propagate into density, stability and cloud in one step.
            float base01 = 1f - AtmoConstants.HypsometricCoefficient * altitudeM;
            if (base01 <= 0f) return 0f;

            return seaLevelHpa * Mathf.Pow(base01, AtmoConstants.HypsometricExponent);
        }

        /// <summary>kg/m3. Dry-air density from the ideal gas law. Thinner air aloft is why lift and drag fall with altitude.</summary>
        public static float AirDensityKgPerM3(float tempC, float pressureHpa)
        {
            float tempK = tempC + AtmoConstants.KelvinAtZeroCelsius;
            if (tempK <= 0f) return 0f;

            return pressureHpa * AtmoConstants.PascalsPerHectopascal
                   / (AtmoConstants.DryAirGasConstantJPerKgK * tempK);
        }

        /// <summary>
        /// K. Potential temperature: the temperature this air would have if brought
        /// to 1000 hPa. Two air masses at different heights can only be compared
        /// once their pressure difference is taken out, which is what makes this the
        /// input to a stability test rather than the raw temperature.
        /// </summary>
        public static float PotentialTemperatureK(float tempK, float pressureHpa)
        {
            if (pressureHpa <= 0f) return tempK;

            return tempK * Mathf.Pow(AtmoConstants.PotentialTemperatureReferenceHpa / pressureHpa,
                                     AtmoConstants.PoissonExponentDryAir);
        }

        // =====================================================================
        //  SECTION 4  -  THE CLIMATE LAWS MOVED HERE FROM EcoMath
        //
        //  Same bodies, same constants, one owner. EcoMath now forwards to these,
        //  so a fix lands in both layers at once.
        // =====================================================================

        /// <summary>degC. Sea-level temperature cooled by the standard environmental lapse rate. (L-04)</summary>
        public static float LapseAdjustedTempC(float seaLevelC, float altitudeM)
        {
            return LapseAdjustedTempC(seaLevelC, altitudeM, AtmoConstants.LapseRateCPerKm);
        }

        /// <summary>
        /// degC, with the rate supplied. Taking the rate as an argument is what lets
        /// an authored lapse rate on the weather manager drive the ecology too,
        /// rather than the two disagreeing about how cold a mountain is. (L-04)
        /// </summary>
        public static float LapseAdjustedTempC(float seaLevelC, float altitudeM, float lapseRateCPerKm)
        {
            return seaLevelC - lapseRateCPerKm * (altitudeM / AtmoConstants.MetresPerKilometre);
        }

        /// <summary>
        /// mm/day. Potential evapotranspiration (Hargreaves-Samani) from temperature
        /// and top-of-atmosphere radiation alone. Chosen over Penman-Monteith because
        /// that needs wind, humidity and net radiation fields - which are exactly the
        /// fields a procedural world would have to invent. (L-13)
        /// </summary>
        public static float HargreavesPetMmPerDay(float tMinC, float tMaxC, float tMeanC,
                                                  float raMjPerM2Day)
        {
            float range = tMaxC - tMinC;
            if (range < 0f) range = 0f;

            // Radiation arrives as energy; PET is a depth of water. The latent heat
            // of vaporisation is the exchange rate between the two.
            float raMm = raMjPerM2Day / AtmoConstants.LatentHeatVaporisationMjPerKg;

            float pet = AtmoConstants.HargreavesCoefficient
                        * raMm
                        * (tMeanC + AtmoConstants.HargreavesTempOffsetC)
                        * Mathf.Sqrt(range);

            return pet > 0f ? pet : 0f;
        }

        /// <summary>
        /// P / PET. Under about 0.2 is desert, 0.2 to 0.5 semi-arid, 0.5 to 0.65 dry
        /// sub-humid, above that humid. One number, and it is the one that decides
        /// whether a place carries forest or grass. (L-14)
        /// </summary>
        public static float AridityIndex(float precipMmPerYear, float petMmPerYear)
        {
            if (petMmPerYear < AtmoConstants.AridityMinPetMmPerYear)
                petMmPerYear = AtmoConstants.AridityMinPetMmPerYear;

            float ai = precipMmPerYear / petMmPerYear;
            if (ai > AtmoConstants.AridityMaxIndex) ai = AtmoConstants.AridityMaxIndex;
            return ai > 0f ? ai : 0f;
        }

        /// <summary>
        /// degC-days. Accumulated warmth above a base temperature. Budburst fires on
        /// this rather than on a calendar date, which is why a cold spring is
        /// genuinely late instead of merely cooler. (L-24)
        /// </summary>
        public static float GrowingDegreeDays(float tMeanC, float baseC, float days)
        {
            float above = tMeanC - baseC;
            if (above <= 0f) return 0f;
            return above * days;
        }

        /// <summary>
        /// 0-1 share of the year above freezing, solved analytically from the annual
        /// temperature sinusoid: acos(-mean / amplitude) / pi. Exact, and it costs one
        /// inverse cosine rather than a twelve-month loop. (L-26)
        /// </summary>
        public static float FrostFreeFraction(float meanAnnualTempC, float annualHalfAmplitudeC)
        {
            float mean = meanAnnualTempC - AtmoConstants.WaterFreezingPointC;

            // No seasonal swing: the year is either wholly above or wholly below.
            if (annualHalfAmplitudeC <= 0f) return mean > 0f ? 1f : 0f;

            float ratio = Mathf.Clamp(-mean / annualHalfAmplitudeC, -1f, 1f);
            return Mathf.Acos(ratio) / Mathf.PI;
        }

        /// <summary>0-1 share of the year above freezing, from the two monthly extremes. (L-26)</summary>
        public static float FrostFreeFractionFromExtremes(float coldestMonthC, float warmestMonthC)
        {
            float mean = (coldestMonthC + warmestMonthC) * 0.5f;
            float amplitude = (warmestMonthC - coldestMonthC) * 0.5f;
            return FrostFreeFraction(mean, amplitude);
        }
    }
}
