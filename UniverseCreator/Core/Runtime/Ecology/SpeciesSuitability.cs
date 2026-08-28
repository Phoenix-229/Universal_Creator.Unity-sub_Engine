// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesSuitability.cs
//  Purpose: Scores one species against one site, 0 to 1.
//
//  The order of the five steps below is a correctness decision and a performance
//  one at the same time, and it is not interchangeable.
//
//    1. Hard rejects, cheapest compare first. Most candidates are rejected, so
//       most candidates must cost a handful of compares and nothing more. A
//       spruce on a salt flat should not cost seven exponentials to turn down.
//    2. Tolerance curves for the factors a species has an optimum in.
//    3. Liebig's law of the minimum over those curves. The minimum, never a
//       product and never an average: a plant with everything it needs but water
//       is as dead as one with nothing, and seven factors at 0.8 multiplied give
//       0.21, which would say a near-ideal site is a bad one.
//    4. Multiplicative penalties, after the minimum. These are stresses, not
//       needs - shade, salt, browsing, snow, wind. A stress scales back what the
//       site could otherwise support; it does not cap it the way a shortage does.
//    5. Drought tolerance raises the floor of the moisture curve rather than
//       moving its peak, because a drought-tolerant species tolerates drought. It
//       does not prefer it, and shifting the optimum would put desert shrubs in
//       the desert and nowhere else.
//
//  Pure function of its two arguments. No member state, no clock, no random
//  source, and so identical on every machine and safe on any thread.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Evaluates how well one species suits one site.
    /// </summary>
    public static class SpeciesSuitability
    {
        // Past two tolerances the Gaussian is exp(-8), about three ten-thousandths
        // - below the threshold any usable species list sets, and below what a
        // float carries meaningfully through the rest of the evaluation. Rejecting
        // there is exact to within noise and skips the expensive half of the work.
        private const float RejectToleranceMultiple = 2f;

        // A site at the exact limit a species states for a stress costs it half
        // its vigour. One number for all four stresses, because the claim being
        // made is the same each time: the limit is where it can still live, not
        // where it does well.
        private const float PenaltyAtStatedLimit = 0.5f;

        // Warmth is one-sided: a species needs a minimum accumulation and does
        // better with more, up to a point. Full marks at twice the stated minimum.
        private const float GddComfortMultiple = 2f;

        // Drought tolerance buys a floor under the moisture curve, at half its
        // stated value - tolerance is endurance, not preference, so it lifts the
        // worst case rather than the best.
        private const float DroughtFloorShare = 0.5f;

        /// <summary>
        /// 0 when the site is impossible for this species, 1 when it is ideal.
        ///
        /// The world-level caps - the vegetation system's own maximum slope,
        /// altitude band and water avoidance - are deliberately not applied here.
        /// They belong to the placement pass, which applies them once per
        /// candidate rather than once per species per candidate, and duplicating
        /// them would mean two places to change when the author moves one slider.
        /// </summary>
        public static float Evaluate(EcoEnvelope eco, in EnvironmentSample site)
        {
            // A hole in a species list is an authoring accident, not a crash.
            if (eco == null || !eco.enabled) return 0f;

            // ---- 1. Hard rejects, cheapest first ----------------------------

            // Extremes kill, not averages. A species survives where its coldest
            // month stays above its lethal minimum, whatever the annual mean says.
            if (site.coldestMonthTempC < eco.tempLethalMinC) return 0f;
            if (site.warmestMonthTempC > eco.tempLethalMaxC) return 0f;

            // Korner's treeline: what decides whether wood can be grown is the
            // warmth of the warmest month, not the year's average.
            if (site.warmestMonthTempC < eco.warmestMonthMinC) return 0f;

            if (site.frostFreeFraction < eco.frostFreeFractionMin) return 0f;

            if (eco.gddMinRequired > 0f && site.gdd < eco.gddMinRequired) return 0f;

            // The altitude band is absolute only when the author says so. Left
            // relative, the thermal gates above already place the species by
            // climate, which is what actually stops a tree at a treeline - the
            // height above sea level is only ever a proxy for it.
            if (eco.useAbsoluteAltitude)
            {
                if (site.altitudeM < eco.minAltitudeM) return 0f;
                if (site.altitudeM > eco.maxAltitudeM) return 0f;
            }

            if (site.rockiness > eco.rockinessMax) return 0f;
            if (site.soilDepthM < eco.soilDepthMinM) return 0f;
            if (site.saltFraction > eco.saltTolerance) return 0f;
            if (site.windExposure > eco.windExposureMax) return 0f;
            if (site.snowLoadKgPerM2 > eco.snowLoadMaxKgPerM2) return 0f;

            if (site.aridity < eco.aridityMin) return 0f;
            if (site.aridity > eco.aridityMax) return 0f;

            // Beyond two tolerances of slope the curve has already collapsed, so
            // this is the cheap way to reach the same answer.
            float slopeReject = eco.slopeOptimumDeg + eco.slopeToleranceDeg * RejectToleranceMultiple;
            if (site.slopeDeg > slopeReject) return 0f;

            if (!TidalZoneAdmits(eco.tidalZone, in site)) return 0f;
            if (!SubmergenceAdmits(eco, in site)) return 0f;
            if (!WaterDistanceAdmits(eco, in site)) return 0f;

            // A negative maximum means the species does not care how deep the
            // water table lies. A stated one means its roots must reach it.
            if (eco.waterTableMaxDepthM >= 0f && site.waterTableDepthM > eco.waterTableMaxDepthM)
                return 0f;

            // ---- 2. Tolerance curves (L-15) ---------------------------------

            float fTemp = EcoMath.GaussianTolerance(
                site.meanAnnualTempC, eco.tempOptimumC, eco.tempToleranceC);

            float fMoisture = EcoMath.GaussianTolerance(
                site.soilMoisture, eco.moistureOptimum, eco.moistureTolerance);

            // Step 5, applied here because this is where the curve is: the floor
            // rises, the peak does not move.
            fMoisture = Mathf.Max(fMoisture, eco.droughtTolerance * DroughtFloorShare);

            float fPrecip = EcoMath.GaussianTolerance(
                site.annualPrecipMm, eco.precipOptimumMm, eco.precipToleranceMm);

            float fTwi = EcoMath.GaussianTolerance(
                site.twi, eco.twiOptimum, eco.twiTolerance);

            float fSlope = EcoMath.GaussianTolerance(
                site.slopeDeg, eco.slopeOptimumDeg, eco.slopeToleranceDeg);

            float fFertility = EcoMath.GaussianTolerance(
                site.soilFertility, eco.soilFertilityOptimum, eco.soilFertilityTolerance);

            float fWarmth = WarmthSufficiency(eco.gddMinRequired, site.gdd);

            // ---- 3. Liebig's law of the minimum (L-16) ----------------------

            float suitability = EcoMath.LiebigMinimum(
                fTemp, fMoisture, fPrecip, fTwi, fSlope, fFertility, fWarmth);

            if (suitability <= 0f) return 0f;

            // ---- 4. Multiplicative stress penalties -------------------------

            suitability *= AspectFactor(eco, in site);
            suitability *= CurvatureFactor(eco, in site);

            // The light this species competes in depends on where it stands in the
            // canopy, not on where the ground is (V-16). Scoring a canopy oak against
            // floor light would exclude it from the wood it forms - the shade is its
            // own - and that is the mistake a single light figure makes unavoidable.
            suitability *= LightFactor(
                eco.shadeTolerance, CanopyLight.AvailableTo(eco.canopyLayer, in site));

            suitability *= StressFactor(site.saltFraction, eco.saltTolerance);
            suitability *= StressFactor(site.windExposure, eco.windExposureMax);
            suitability *= StressFactor(site.snowLoadKgPerM2, eco.snowLoadMaxKgPerM2);
            suitability *= BrowseFactor(site.browsePressure, eco.browseTolerance);

            return Mathf.Clamp01(suitability);
        }

        /// <summary>
        /// One-sided warmth curve. A species with no stated requirement is
        /// indifferent and scores 1; one with a requirement reaches full marks at
        /// twice it, having already been rejected below it.
        /// </summary>
        private static float WarmthSufficiency(float gddMinRequired, float gdd)
        {
            if (gddMinRequired <= 0f) return 1f;

            float comfortable = gddMinRequired * GddComfortMultiple;
            return Mathf.Clamp01(gdd / comfortable);
        }

        /// <summary>
        /// A species' own preference for which way the ground faces, separate
        /// from the hemisphere-correct warming the sampler has already folded
        /// into the site temperature. The two are not the same thing and must not
        /// be conflated: one is a fact about sunlight, this is a preference.
        ///
        /// A negative preference means none stated. Flat ground has no aspect to
        /// match, so it is neither rewarded nor punished.
        /// </summary>
        private static float AspectFactor(EcoEnvelope eco, in EnvironmentSample site)
        {
            if (eco.aspectPreferenceDeg < 0f) return 1f;
            if (site.aspectDeg < 0f) return 1f;

            float offsetRad = (site.aspectDeg - eco.aspectPreferenceDeg) * Mathf.Deg2Rad;

            // cos runs -1 to 1; remap to 0 to 1 so a perfect match scores full
            // and the opposite bearing pays the whole of the stated strength.
            float alignment = (Mathf.Cos(offsetRad) + 1f) * 0.5f;

            return Mathf.Lerp(1f - Mathf.Clamp01(eco.aspectStrength), 1f, alignment);
        }

        /// <summary>
        /// Preference for ridge or hollow. Zero means indifferent, which is the
        /// default, so this costs nothing for the species that do not care.
        /// </summary>
        private static float CurvatureFactor(EcoEnvelope eco, in EnvironmentSample site)
        {
            if (Mathf.Approximately(eco.curvaturePreference, 0f)) return 1f;

            float siteCurve = Mathf.Clamp(
                site.curvature / EcoConstants.SoilDepthCurvatureSpanPerMetre, -1f, 1f);

            float wanted = Mathf.Clamp(eco.curvaturePreference, -1f, 1f);

            // Distance apart on a scale two units wide, so opposite ends of the
            // scale disagree completely and neighbours barely at all.
            float mismatch = Mathf.Abs(siteCurve - wanted) * 0.5f;

            return 1f - PenaltyAtStatedLimit * Mathf.Clamp01(mismatch);
        }

        /// <summary>
        /// Light against shade tolerance (L-17 supplies the light itself).
        ///
        /// A fully shade-tolerant species needs nothing and passes everywhere; an
        /// intolerant one needs full daylight. Between the two, the shortfall
        /// scales linearly, because a seedling at half the light it needs grows at
        /// roughly half the rate rather than dying outright.
        /// </summary>
        private static float LightFactor(float shadeTolerance, float canopyLightFraction)
        {
            float required = 1f - Mathf.Clamp01(shadeTolerance);
            if (required <= 0f) return 1f;

            return Mathf.Clamp01(canopyLightFraction / required);
        }

        /// <summary>
        /// A stress measured against the limit the species states for it.
        ///
        /// No stress is no penalty; the stated limit costs half. A species that
        /// states a limit of zero is saying it tolerates none, and anything above
        /// zero has already been rejected, so there is nothing to grade.
        /// </summary>
        private static float StressFactor(float load, float statedLimit)
        {
            if (load <= 0f) return 1f;
            if (statedLimit <= 0f) return 1f;

            float share = Mathf.Clamp01(load / statedLimit);
            return 1f - PenaltyAtStatedLimit * share;
        }

        /// <summary>
        /// Grazing pressure against browse tolerance. A fully tolerant species is
        /// untouched; an intolerant one loses the whole of the pressure applied.
        /// </summary>
        private static float BrowseFactor(float browsePressure, float browseTolerance)
        {
            float exposed = 1f - Mathf.Clamp01(browseTolerance);
            return Mathf.Clamp01(1f - Mathf.Clamp01(browsePressure) * exposed);
        }

        /// <summary>
        /// Whether the site sits in the tidal band the species requires.
        ///
        /// Height above the sea surface is the measure, since that is what being
        /// covered twice a day comes down to, and only the ocean has a tide -
        /// a lake at the same elevation does not.
        /// </summary>
        private static bool TidalZoneAdmits(TidalZone zone, in EnvironmentSample site)
        {
            if (zone == TidalZone.None) return true;

            if (site.nearestWaterKind != WaterBodyKind.Ocean) return false;

            switch (zone)
            {
                case TidalZone.Intertidal:
                    return Mathf.Abs(site.altitudeM) <= EcoConstants.TidalRangeMetres;

                case TidalZone.Supratidal:
                    return site.altitudeM > EcoConstants.TidalRangeMetres
                        && site.altitudeM <= EcoConstants.TidalRangeMetres + EcoConstants.SplashZoneHeightMetres;

                // Below low water. The depth is left to the species' submerged
                // range; all this adds is the sea itself, plus a ceiling that
                // rejects a freshwater pool perched above the shore whose nearest
                // water body happens to be the ocean - a real case on an island.
                case TidalZone.Subtidal:
                    return site.altitudeM <= EcoConstants.TidalRangeMetres;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether the site's depth under water matches what the species needs.
        ///
        /// An empty range means a land plant, and a land plant underwater is
        /// drowned. A stated range means the species is aquatic and needs to be in
        /// it - kelp on a beach is as wrong as a pine on the seabed.
        /// </summary>
        private static bool SubmergenceAdmits(EcoEnvelope eco, in EnvironmentSample site)
        {
            bool aquatic = eco.submergedDepthRangeM.y > 0f;

            if (!aquatic)
            {
                // Flood tolerance buys a little standing water, which is what
                // separates a willow on a wet bank from a pine that will not
                // stand it at all.
                return site.submergedDepthM <= eco.floodTolerance;
            }

            return site.submergedDepthM >= eco.submergedDepthRangeM.x
                && site.submergedDepthM <= eco.submergedDepthRangeM.y;
        }

        /// <summary>
        /// Whether the site lies in the species' band of distance from water.
        ///
        /// A negative maximum means no upper limit, which is the common case: most
        /// species do not care how far the nearest lake is.
        /// </summary>
        private static bool WaterDistanceAdmits(EcoEnvelope eco, in EnvironmentSample site)
        {
            if (site.distanceToWaterM < eco.distanceToWaterMinM) return false;

            if (eco.distanceToWaterMaxM >= 0f && site.distanceToWaterM > eco.distanceToWaterMaxM)
                return false;

            return true;
        }
    }
}
