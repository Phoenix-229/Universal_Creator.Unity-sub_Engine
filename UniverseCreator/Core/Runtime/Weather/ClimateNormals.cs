// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    ClimateNormals.cs
//  Purpose: Twelve monthly means for one world position, and the annual
//           aggregates the vegetation layer reads. This is the bridge between
//           instantaneous weather and ecology: a plant does not care what the
//           weather is doing, it cares what the weather usually does.
//
//  WHAT IS COMPUTED HERE AND WHAT ARRIVES AS AN INPUT
//  --------------------------------------------------
//  Computed: the shape of the year. Monthly temperature from a zonal profile,
//  an altitude lapse, a seasonal harmonic whose amplitude comes from Conrad
//  continentality, and a diurnal range from cloud and humidity. Monthly
//  radiation from the closed-form FAO-56 Ra. Monthly precipitation as a
//  normalised regime shape. Then PET, aridity, growing degree days and the
//  frost-free share of the year.
//
//  Arrives as an input, deliberately: the annual precipitation TOTAL, the
//  distance to the coast, the aspect warming, cloud and humidity, and the
//  growing-degree base temperature. Every one of those already has an owner
//  elsewhere - the shipped orographic precipitation field, the shipped water
//  masks, the aspect law in the vegetation layer, and the species table's
//  biological threshold. Recomputing any of them here would be a second answer
//  to a settled question, and the base temperature in particular is a property
//  of a plant rather than of the air, so it cannot live in this folder at all.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Nothing in this file references the ecology layer. That is a hard rule, not
//  a preference: the ecology layer reads this one, and a cycle between them
//  would mean neither could be evaluated first. Where a value is genuinely
//  owned over there, it comes in through ClimateRequest.
//
//  COST
//  ----
//  Built once per chunk on the worker thread and cached, never per candidate.
//  Twelve months of eight terms is nothing once; at forty thousand candidates
//  it is a millisecond of pure waste. Rebuild exists so a chunk can refill an
//  existing instance instead of allocating six arrays again.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// How precipitation is distributed across the year. Derived from latitude,
    /// continentality and aridity; authorable per world as an override.
    /// </summary>
    public enum PrecipitationRegime
    {
        /// <summary>Wet all year with two maxima, one per ITCZ passage.</summary>
        Equatorial = 0,

        /// <summary>One strong summer maximum and a near-dry winter.</summary>
        Monsoon = 1,

        /// <summary>Winter maximum and summer drought.</summary>
        Mediterranean = 2,

        /// <summary>Rain in every month, slightly more in winter.</summary>
        TemperateOceanic = 3,

        /// <summary>A summer convective maximum.</summary>
        Continental = 4,

        /// <summary>Low and almost flat, whether from cold or from drought.</summary>
        PolarOrDesert = 5,
    }

    /// <summary>
    /// Everything the builder needs about one position. A struct because it is
    /// filled, passed once and forgotten; nothing retains it.
    /// </summary>
    public struct ClimateRequest
    {
        /// <summary>Signed latitude. The sign selects the hemisphere, so the seasons invert below the equator.</summary>
        public float latitudeDeg;

        /// <summary>Metres above sea level.</summary>
        public float altitudeM;

        /// <summary>
        /// Metres to the nearest ocean, from the shipped water masks. Drives the
        /// whole maritime-to-continental axis, so a missing value should be large
        /// rather than zero - an unmeasured site is inland, not an island.
        /// </summary>
        public float distanceToCoastM;

        /// <summary>mm/yr. Annual total from the shipped orographic precipitation field. Only its distribution is decided here.</summary>
        public float annualPrecipMm;

        /// <summary>degC. Aspect warming already resolved by the vegetation layer, which owns that law.</summary>
        public float aspectBiasC;

        /// <summary>0-1 mean cloud cover. Damps the diurnal range and the surface radiation.</summary>
        public float cloudCover01;

        /// <summary>0-1 mean relative humidity. Damps the diurnal range.</summary>
        public float humidity01;

        /// <summary>degC. Growing-degree base temperature, owned by the species table.</summary>
        public float gddBaseC;

        /// <summary>degC/km. Lapse rate, so an authored one reaches the normals.</summary>
        public float lapseRateCPerKm;

        /// <summary>Regime override. Ignored unless <see cref="regimeIsAuthored"/> is set.</summary>
        public PrecipitationRegime regime;

        /// <summary>True to use <see cref="regime"/> instead of deriving one from geography.</summary>
        public bool regimeIsAuthored;

        /// <summary>
        /// A request with the physical defaults filled in: standard lapse rate, half
        /// cloud, half humidity, a temperate growing base, and a distance to coast
        /// that reads as deep interior until something measures it.
        /// </summary>
        public static ClimateRequest Default(float latitudeDeg, float altitudeM,
                                             float annualPrecipMm, float gddBaseC)
        {
            ClimateRequest r = new ClimateRequest();

            r.latitudeDeg = latitudeDeg;
            r.altitudeM = altitudeM;
            r.annualPrecipMm = annualPrecipMm;
            r.gddBaseC = gddBaseC;

            r.distanceToCoastM = AtmoConstants.MarineModerationScaleM;
            r.cloudCover01 = DefaultCloudCover01;
            r.humidity01 = DefaultHumidity01;
            r.lapseRateCPerKm = AtmoConstants.LapseRateCPerKm;

            r.regime = PrecipitationRegime.TemperateOceanic;
            r.regimeIsAuthored = false;

            return r;
        }

        /// <summary>Cloud cover assumed before a cloud field exists.</summary>
        public const float DefaultCloudCover01 = 0.5f;

        /// <summary>Relative humidity assumed before a humidity field exists.</summary>
        public const float DefaultHumidity01 = 0.5f;
    }

    /// <summary>
    /// The twelve monthly means for one position and the annual aggregates derived
    /// from them.
    ///
    /// A class rather than a struct on purpose. Six twelve-element arrays are six
    /// references, so a copied struct would share them: two chunks would appear to
    /// have their own normals and would in fact overwrite each other's months. One
    /// instance per chunk, held by reference, cannot do that.
    /// </summary>
    public class ClimateNormals
    {
        /// <summary>Days attributed to each month: the year divided evenly, matching the month midpoints used to sample it.</summary>
        public const float DaysPerMonth =
            AtmoConstants.DaysPerCalendarYear / AtmoConstants.MonthsPerYear;

        // ---- the year, month by month ------------------------------------------

        /// <summary>degC. Mean temperature of each month.</summary>
        public readonly float[] monthlyMeanTempC = new float[AtmoConstants.MonthsPerYear];

        /// <summary>degC. Mean daily minimum of each month.</summary>
        public readonly float[] monthlyMinTempC = new float[AtmoConstants.MonthsPerYear];

        /// <summary>degC. Mean daily maximum of each month.</summary>
        public readonly float[] monthlyMaxTempC = new float[AtmoConstants.MonthsPerYear];

        /// <summary>mm. Precipitation total of each month.</summary>
        public readonly float[] monthlyPrecipMm = new float[AtmoConstants.MonthsPerYear];

        /// <summary>MJ/m2/day. Top-of-atmosphere radiation of each month. This is what Hargreaves PET consumes.</summary>
        public readonly float[] monthlyRadiationMj = new float[AtmoConstants.MonthsPerYear];

        /// <summary>
        /// MJ/m2/day. Radiation reaching the ground each month, after Angstrom-Prescott.
        /// Separate from the array above because PET wants the top-of-atmosphere value
        /// and everything visual wants this one; returning one number for both is how
        /// a cloudy world ends up with a desert's evaporation.
        /// </summary>
        public readonly float[] monthlySurfaceRadiationMj = new float[AtmoConstants.MonthsPerYear];

        // ---- the aggregates the ecology layer reads ------------------------------

        /// <summary>degC. Mean of the twelve monthly means.</summary>
        public float meanAnnualTempC;

        /// <summary>degC. Warmest monthly mean.</summary>
        public float warmestMonthTempC;

        /// <summary>degC. Coldest monthly mean.</summary>
        public float coldestMonthTempC;

        /// <summary>mm/yr. Annual precipitation total.</summary>
        public float annualPrecipMm;

        /// <summary>mm/yr. Annual potential evapotranspiration.</summary>
        public float annualPetMm;

        /// <summary>P/PET.</summary>
        public float aridity;

        /// <summary>0-1 share of the year above freezing.</summary>
        public float frostFreeFraction;

        /// <summary>degC-days accumulated above the requested base temperature across the year.</summary>
        public float gddAnnual;

        /// <summary>Conrad continentality index of the realised annual range.</summary>
        public float continentality;

        /// <summary>Which regime shaped the monthly precipitation, derived or authored.</summary>
        public PrecipitationRegime regime;

        /// <summary>degC. Warmest monthly mean minus coldest: the annual range Conrad measures.</summary>
        public float AnnualRangeC { get { return warmestMonthTempC - coldestMonthTempC; } }

        /// <summary>True below the equator, where the seasonal harmonic is inverted.</summary>
        public bool IsSouthernHemisphere { get { return latitudeDeg < 0f; } }

        /// <summary>Signed latitude these normals were built for.</summary>
        public float latitudeDeg;

        /// <summary>Altitude these normals were built for.</summary>
        public float altitudeM;

        private const float Tau = Mathf.PI * 2f;

        /// <summary>Builds a fresh set of normals. Allocates once; prefer <see cref="Rebuild"/> per chunk.</summary>
        public static ClimateNormals Build(ClimateRequest request)
        {
            ClimateNormals n = new ClimateNormals();
            n.Rebuild(request);
            return n;
        }

        /// <summary>
        /// Refills this instance from a request. Every array is overwritten in full,
        /// so no month can survive from a previous position.
        /// </summary>
        public void Rebuild(ClimateRequest request)
        {
            latitudeDeg = request.latitudeDeg;
            altitudeM = request.altitudeM;

            float lapseRate = request.lapseRateCPerKm != 0f
                            ? request.lapseRateCPerKm
                            : AtmoConstants.LapseRateCPerKm;

            // The annual mean, before any seasonal swing: the zonal profile cooled by
            // altitude and nudged by aspect.
            float annualMeanC = AtmoMath.LapseAdjustedTempC(
                                    ZonalMeanSeaLevelTempC(request.latitudeDeg),
                                    request.altitudeM,
                                    lapseRate)
                              + request.aspectBiasC;

            float conradIndex = ConradIndexFromCoastDistance(request.distanceToCoastM);
            float annualRangeC = AnnualRangeFromConradIndex(conradIndex, request.latitudeDeg);
            float halfRange = annualRangeC * 0.5f;

            float diurnalRangeC = DiurnalRangeC(request.cloudCover01, request.humidity01);
            float halfDiurnal = diurnalRangeC * 0.5f;

            float lagPhase = AtmoConstants.SeasonalPeakLagDays
                             / AtmoConstants.DaysPerCalendarYear;

            // Below the equator the warm half of the year is the other half.
            float hemisphereSign = request.latitudeDeg < 0f ? -1f : 1f;

            float sunshineFraction = 1f - Mathf.Clamp01(request.cloudCover01);
            float angstrom = AtmoConstants.AngstromA
                             + AtmoConstants.AngstromB * sunshineFraction;

            float petTotal = 0f;
            float gddTotal = 0f;

            warmestMonthTempC = float.MinValue;
            coldestMonthTempC = float.MaxValue;

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                int dayOfYear = MonthMidpointDayOfYear(month);
                float winterPhase = ClockMath.WinterPhase01(dayOfYear);

                // Peak warmth trails the solstice by the lag, which is why the hottest
                // month is July and not June.
                float seasonal = -hemisphereSign
                                 * Mathf.Cos(Tau * (winterPhase - lagPhase));

                float meanC = annualMeanC + halfRange * seasonal;

                monthlyMeanTempC[month] = meanC;
                monthlyMinTempC[month] = meanC - halfDiurnal;
                monthlyMaxTempC[month] = meanC + halfDiurnal;

                float declination = ClockMath.DeclinationDeg(dayOfYear);
                float ra = ClockMath.ExtraterrestrialRadiationMjPerM2Day(
                               request.latitudeDeg, declination, winterPhase);

                monthlyRadiationMj[month] = ra;
                monthlySurfaceRadiationMj[month] = ra * angstrom;

                petTotal += AtmoMath.HargreavesPetMmPerDay(
                                monthlyMinTempC[month],
                                monthlyMaxTempC[month],
                                meanC,
                                ra) * DaysPerMonth;

                gddTotal += AtmoMath.GrowingDegreeDays(meanC, request.gddBaseC, DaysPerMonth);

                if (meanC > warmestMonthTempC) warmestMonthTempC = meanC;
                if (meanC < coldestMonthTempC) coldestMonthTempC = meanC;
            }

            // The annual mean is the pre-swing zonal mean itself. Averaging the twelve
            // monthly means would only approximate it, and because the month midpoints
            // are unevenly spaced the residual does not cancel - it lands with opposite
            // sign in each hemisphere, so two mirrored latitudes would disagree on a
            // number they must share. Taking annualMeanC directly is both the exact
            // annual mean of the seasonal cosine and identical north and south.
            meanAnnualTempC = annualMeanC;
            annualPetMm = petTotal;
            gddAnnual = gddTotal;

            frostFreeFraction = AtmoMath.FrostFreeFractionFromExtremes(
                                    coldestMonthTempC, warmestMonthTempC);

            annualPrecipMm = request.annualPrecipMm > 0f ? request.annualPrecipMm : 0f;
            aridity = AtmoMath.AridityIndex(annualPrecipMm, annualPetMm);

            // Aridity is settled before the regime because a desert is flat whatever
            // its latitude would otherwise prescribe, and the annual TOTAL is an input
            // rather than something the distribution produces - so there is no circle
            // here even though it reads like one.
            regime = request.regimeIsAuthored
                   ? request.regime
                   : DeriveRegime(request.latitudeDeg, conradIndex, aridity);

            FillMonthlyPrecipitation(hemisphereSign, lagPhase);

            continentality = ConradIndexFromAnnualRange(AnnualRangeC, request.latitudeDeg);
        }

        // =====================================================================
        //  THE TERMS
        //  Pure static, so every one of them can be checked on its own against a
        //  known latitude without building a whole year first.
        // =====================================================================

        /// <summary>
        /// degC. Zonal mean sea-level temperature: the Budyko-North profile
        /// T0 + T2*P2(sin phi).
        ///
        /// A straight line in latitude was tried first and cannot work. The named
        /// meridional gradient is a mid-latitude figure, so run linearly it gives
        /// about -4 degC at 45 deg against an observed +11. The second Legendre
        /// polynomial is flat at the equator and steep in the mid-latitudes, which
        /// is the actual shape of the atmosphere, and its amplitude is DERIVED from
        /// that same gradient so the gradient stays the only authored number.
        /// </summary>
        public static float ZonalMeanSeaLevelTempC(float latitudeDeg)
        {
            float amplitudeC = -AtmoConstants.LatitudeGradientCPerDeg * Mathf.Rad2Deg
                             / AtmoConstants.LegendreP2MidLatitudeSlope;

            float sinLat = Mathf.Sin(latitudeDeg * Mathf.Deg2Rad);
            float legendreP2 = (3f * sinLat * sinLat - 1f) * 0.5f;

            return AtmoConstants.GlobalMeanSeaLevelTempC + amplitudeC * legendreP2;
        }

        /// <summary>
        /// Conrad continentality index from the distance to open water. Saturating
        /// exponential: the first hundred kilometres inland do most of the work,
        /// which is why a coastal strip is mild and the width of that strip barely
        /// depends on how big the continent behind it is.
        /// </summary>
        public static float ConradIndexFromCoastDistance(float distanceToCoastM)
        {
            float distance = distanceToCoastM > 0f ? distanceToCoastM : 0f;

            return AtmoConstants.ConradMaxIndex
                   * (1f - Mathf.Exp(-distance / AtmoConstants.MarineModerationScaleM));
        }

        /// <summary>
        /// m. The distance a 0-1 "how continental is this place" dial stands for,
        /// which is the inverse of the saturating curve above with the index divided
        /// out by its own ceiling.
        ///
        /// Exists because the weather manager authors continentality as a dial and
        /// the normals need a distance, and one of the two had to convert. Putting
        /// the conversion here rather than at the call site keeps it beside the curve
        /// it inverts, so the pair cannot be changed apart.
        ///
        /// A dial at 1 means infinitely far from any sea, which is not a number. It
        /// is reported as the distance at which the curve has already reached its
        /// ceiling to within float precision - far enough inland that nothing
        /// downstream can tell the difference.
        /// </summary>
        public static float CoastDistanceMFromContinentality01(float continentality01)
        {
            float c = Mathf.Clamp01(continentality01);
            if (c <= 0f) return 0f;

            float remaining = 1f - c;
            if (remaining < AtmoConstants.ContinentalitySaturationFloor)
                remaining = AtmoConstants.ContinentalitySaturationFloor;

            return -AtmoConstants.MarineModerationScaleM * Mathf.Log(remaining);
        }

        /// <summary>degC. Conrad inverted for the annual range it implies at a latitude.</summary>
        public static float AnnualRangeFromConradIndex(float conradIndex, float latitudeDeg)
        {
            return (conradIndex + AtmoConstants.ConradOffset)
                   * ConradLatitudeFactor(latitudeDeg)
                   / AtmoConstants.ConradMultiplier;
        }

        /// <summary>
        /// Conrad in its forward direction: the index a realised annual range
        /// implies. Present so the pair can be round-tripped in a test - an
        /// inverse nobody ever checks against its forward is an inverse that
        /// drifts.
        /// </summary>
        public static float ConradIndexFromAnnualRange(float annualRangeC, float latitudeDeg)
        {
            return AtmoConstants.ConradMultiplier * annualRangeC
                   / ConradLatitudeFactor(latitudeDeg)
                   - AtmoConstants.ConradOffset;
        }

        /// <summary>
        /// sin(|phi| + 10 deg), the latitude weight both Conrad directions share.
        /// Latitude is clamped at the pole guard so the two can never disagree
        /// about a bad input, and the offset keeps the equator off zero - without
        /// it the forward form divides by nothing at latitude 0.
        /// </summary>
        private static float ConradLatitudeFactor(float latitudeDeg)
        {
            float absLat = Mathf.Clamp(Mathf.Abs(latitudeDeg), 0f,
                                       AtmoConstants.LatitudeTanGuardDeg);

            return Mathf.Sin((absLat + AtmoConstants.ConradLatitudeOffsetDeg)
                             * Mathf.Deg2Rad);
        }

        /// <summary>
        /// degC. Day-night range. Both cloud and humidity damp it, for the same
        /// physical reason by two different paths: cloud stops the ground radiating
        /// at night, water vapour absorbs what does escape. A clear dry desert keeps
        /// nearly the full base range; an overcast humid coast keeps about half.
        /// </summary>
        public static float DiurnalRangeC(float cloudCover01, float humidity01)
        {
            float cloudDamping = 1f - AtmoConstants.DiurnalCloudFactor
                                      * Mathf.Clamp01(cloudCover01);

            float humidityDamping = 1f - AtmoConstants.DiurnalHumidityFactor
                                         * Mathf.Clamp01(humidity01);

            return AtmoConstants.DiurnalBaseRangeC * cloudDamping * humidityDamping;
        }

        /// <summary>
        /// Day of year at the middle of a month, the year divided evenly. Midpoints
        /// rather than starts because a monthly mean sampled at the first of the
        /// month is biased half a month cold in spring and half a month warm in
        /// autumn.
        /// </summary>
        public static int MonthMidpointDayOfYear(int month)
        {
            int wrapped = month % AtmoConstants.MonthsPerYear;
            if (wrapped < 0) wrapped += AtmoConstants.MonthsPerYear;

            return (int)((wrapped + 0.5f) * DaysPerMonth);
        }

        /// <summary>
        /// The regime geography implies. Tested in this order on purpose: aridity
        /// wins over latitude, because a desert at a monsoon latitude is still a
        /// desert, and the latitude bands are then walked equator-outwards so each
        /// test only has to state its own poleward edge.
        /// </summary>
        public static PrecipitationRegime DeriveRegime(float latitudeDeg,
                                                       float conradIndex,
                                                       float aridity)
        {
            if (aridity < AtmoConstants.DesertAridityThreshold)
                return PrecipitationRegime.PolarOrDesert;

            float absLat = Mathf.Abs(latitudeDeg);

            if (absLat >= AtmoConstants.PolarRegimeLatitudeDeg)
                return PrecipitationRegime.PolarOrDesert;

            if (absLat <= AtmoConstants.EquatorialRegimeLatitudeDeg)
                return PrecipitationRegime.Equatorial;

            if (absLat <= AtmoConstants.MonsoonRegimeLatitudeDeg)
                return PrecipitationRegime.Monsoon;

            // The Mediterranean band, but only its maritime side. Push the same
            // latitude far enough inland and the subtropical high stops reaching
            // it and summer convection takes over - which is the difference
            // between Spain and Kazakhstan at one latitude.
            if (absLat >= AtmoConstants.MediterraneanRegimeMinLatitudeDeg &&
                absLat <= AtmoConstants.MediterraneanRegimeMaxLatitudeDeg &&
                conradIndex < AtmoConstants.ContinentalRegimeMinConradIndex)
                return PrecipitationRegime.Mediterranean;

            return conradIndex >= AtmoConstants.ContinentalRegimeMinConradIndex
                 ? PrecipitationRegime.Continental
                 : PrecipitationRegime.TemperateOceanic;
        }

        /// <summary>
        /// Spreads the annual total across the twelve months by the regime's shape.
        ///
        /// The weights are summed and divided out, which is what makes the annual
        /// total survive exactly: whatever shape is chosen, and however hard a dry
        /// season is clamped to zero, the twelve months still add back to the
        /// number the precipitation field handed in. Any scheme that scaled each
        /// month independently would quietly invent or destroy rain.
        /// </summary>
        private void FillMonthlyPrecipitation(float hemisphereSign, float lagPhase)
        {
            float weightTotal = 0f;

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                float weight = RegimeShapeWeight(regime, month, hemisphereSign, lagPhase);

                monthlyPrecipMm[month] = weight;
                weightTotal += weight;
            }

            // Only reachable if a shape clamps every month to zero. A flat year is
            // the honest answer there; scaling by zero would lose the total.
            if (weightTotal <= 0f)
            {
                float flat = annualPrecipMm / AtmoConstants.MonthsPerYear;

                for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
                    monthlyPrecipMm[month] = flat;

                return;
            }

            float scale = annualPrecipMm / weightTotal;

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
                monthlyPrecipMm[month] *= scale;
        }

        /// <summary>
        /// The unnormalised share of the year's rain a regime puts in one month.
        /// Returned before normalisation, and floored at zero: an amplitude above
        /// one drives the dry half of the year to nothing, which is what a dry
        /// season is. Negative rain is not a smaller amount of rain.
        /// </summary>
        public static float RegimeShapeWeight(PrecipitationRegime regime, int month,
                                              float hemisphereSign, float lagPhase)
        {
            float winterPhase = ClockMath.WinterPhase01(MonthMidpointDayOfYear(month));

            // +1 at local high summer, -1 at local midwinter. One term, and every
            // solstice-driven regime below is a sign and a scale on it.
            float summer = -hemisphereSign * Mathf.Cos(Tau * (winterPhase - lagPhase));

            float weight;

            switch (regime)
            {
                case PrecipitationRegime.Equatorial:
                    // Two maxima a year rather than one, because the ITCZ passes
                    // overhead twice - so this harmonic is semiannual and peaks at
                    // the equinoxes, and it does not care which hemisphere it is in.
                    weight = 1f - AtmoConstants.EquatorialSemiannualAmplitude
                                  * Mathf.Cos(2f * Tau * winterPhase);
                    break;

                case PrecipitationRegime.Monsoon:
                    weight = 1f + AtmoConstants.MonsoonAnnualAmplitude * summer;
                    break;

                case PrecipitationRegime.Mediterranean:
                    weight = 1f - AtmoConstants.MediterraneanAnnualAmplitude * summer;
                    break;

                case PrecipitationRegime.TemperateOceanic:
                    weight = 1f - AtmoConstants.OceanicAnnualAmplitude * summer;
                    break;

                case PrecipitationRegime.Continental:
                    weight = 1f + AtmoConstants.ContinentalAnnualAmplitude * summer;
                    break;

                default:
                    weight = 1f + AtmoConstants.FlatRegimeAmplitude * summer;
                    break;
            }

            return weight > 0f ? weight : 0f;
        }

        // =====================================================================
        //  READING THE YEAR BACK
        // =====================================================================

        /// <summary>Month index containing a day of the year, clamped into range.</summary>
        public static int MonthIndexForDayOfYear(int dayOfYear)
        {
            int month = (int)(dayOfYear / DaysPerMonth);

            if (month < 0) return 0;
            if (month >= AtmoConstants.MonthsPerYear) return AtmoConstants.MonthsPerYear - 1;
            return month;
        }

        /// <summary>
        /// mm. Evaporative demand of one month, recomputed from that month's stored
        /// temperatures and radiation rather than cached in a seventh array. Twelve
        /// Hargreaves evaluations cost nothing next to the array, and a cache that
        /// nobody refills after a Rebuild is a wrong answer waiting to be read.
        /// </summary>
        public float MonthlyPetMm(int month)
        {
            int m = Mathf.Clamp(month, 0, AtmoConstants.MonthsPerYear - 1);

            return AtmoMath.HargreavesPetMmPerDay(monthlyMinTempC[m],
                                                  monthlyMaxTempC[m],
                                                  monthlyMeanTempC[m],
                                                  monthlyRadiationMj[m]) * DaysPerMonth;
        }

        /// <summary>
        /// P/PET for one month. This is the number a summer drought actually shows
        /// up in - a Mediterranean year can be humid on the annual index and still
        /// spend August below the desert threshold, which is why the plants there
        /// look the way they do.
        /// </summary>
        public float MonthlyAridity(int month)
        {
            int m = Mathf.Clamp(month, 0, AtmoConstants.MonthsPerYear - 1);

            return AtmoMath.AridityIndex(monthlyPrecipMm[m], MonthlyPetMm(m));
        }

        /// <summary>Index of the driest month, by monthly aridity rather than by rainfall alone.</summary>
        public int DriestMonthIndex()
        {
            int driest = 0;
            float lowest = float.MaxValue;

            for (int month = 0; month < AtmoConstants.MonthsPerYear; month++)
            {
                float value = MonthlyAridity(month);
                if (value < lowest) { lowest = value; driest = month; }
            }

            return driest;
        }
    }
}
