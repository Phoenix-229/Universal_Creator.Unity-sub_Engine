// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    EcoMath.cs
//  Purpose: The ecological laws L-12 ... L-26 as pure static functions, plus the
//           L-04 lapse adjustment the ecology sampler needs before the weather
//           layer exists.
//
//  CONTRACT
//  --------
//  Every method here is static, side-effect free, allocation free, and safe to
//  call from a worker thread. Nothing reads a field, a frame count or a random
//  source, so the same arguments give the same answer on every machine and in
//  every build - which is the determinism requirement, not a nicety.
//
//  Not in this file, deliberately: saturation vapour pressure, dew point, the
//  lifting condensation level and wet-bulb temperature. Those are L-05 ... L-10
//  and they belong to AtmoMath in the weather layer, which owns them exactly
//  once. Duplicating them here would give two answers to the same question.
//
//  LIEBIG, NOT MULTIPLICATION
//  --------------------------
//  Suitability is combined with LiebigMinimum, never with a running product.
//  Seven factors each at a healthy 0.8 multiply to 0.21, so a good site reads as
//  hostile and an author spends a day raising tolerances to compensate. Real
//  limitation is by the scarcest resource. Multiplicative terms exist - salt
//  spray, browse pressure - but they are penalties applied after the minimum and
//  they are named penalty, never suitability.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;   // WaterBodyKind, for L-25b body salinity
using UniverseCreator.Weather; // AtmoMath, which owns the atmospheric laws below

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Pure ecological mathematics. Static, stateless, thread-safe.
    /// </summary>
    public static class EcoMath
    {
        // =====================================================================
        //  L-15  Gaussian niche tolerance
        // =====================================================================

        /// <summary>
        /// L-15. exp(-(v - opt)^2 / (2 sigma^2)) with sigma from the authored
        /// half-width, so the curve reads about 0.135 one tolerance from the
        /// optimum: the edge of the range, not the end of the species.
        /// </summary>
        public static float GaussianTolerance(float value, float optimum, float tolerance)
        {
            float sigma = tolerance * EcoConstants.ToleranceToSigma;
            if (sigma < EcoConstants.ToleranceMinSigma) sigma = EcoConstants.ToleranceMinSigma;

            float d = value - optimum;
            return Mathf.Exp(-(d * d) / (2f * sigma * sigma));
        }

        // =====================================================================
        //  L-16  Liebig's law of the minimum
        //
        //  The fixed-arity overloads exist because the params form allocates an
        //  array per call, and this is called once per candidate. Forty thousand
        //  candidates per chunk is forty thousand allocations, which does not
        //  show up in an average frame time - it shows up as a collection pause
        //  on a phone.
        // =====================================================================

        /// <summary>L-16. The scarcest of two factors.</summary>
        public static float LiebigMinimum(float a, float b)
        {
            return a < b ? a : b;
        }

        /// <summary>L-16. The scarcest of three factors.</summary>
        public static float LiebigMinimum(float a, float b, float c)
        {
            float m = a < b ? a : b;
            return m < c ? m : c;
        }

        /// <summary>L-16. The scarcest of four factors.</summary>
        public static float LiebigMinimum(float a, float b, float c, float d)
        {
            float m = a < b ? a : b;
            if (c < m) m = c;
            return m < d ? m : d;
        }

        /// <summary>L-16. The scarcest of five factors.</summary>
        public static float LiebigMinimum(float a, float b, float c, float d, float e)
        {
            float m = a < b ? a : b;
            if (c < m) m = c;
            if (d < m) m = d;
            return m < e ? m : e;
        }

        /// <summary>L-16. The scarcest of six factors.</summary>
        public static float LiebigMinimum(float a, float b, float c, float d, float e, float f)
        {
            float m = a < b ? a : b;
            if (c < m) m = c;
            if (d < m) m = d;
            if (e < m) m = e;
            return m < f ? m : f;
        }

        /// <summary>L-16. The scarcest of seven factors.</summary>
        public static float LiebigMinimum(float a, float b, float c, float d, float e, float f, float g)
        {
            float m = a < b ? a : b;
            if (c < m) m = c;
            if (d < m) m = d;
            if (e < m) m = e;
            if (f < m) m = f;
            return m < g ? m : g;
        }

        /// <summary>
        /// L-16, variable arity. Allocates, so it is for editor, debug and test
        /// paths only - never for the per-candidate scatter path. An empty set
        /// returns 0 rather than 1: no factor evaluated is a wiring fault, and
        /// "nothing grows" is diagnosable where "everything grows" is not.
        /// </summary>
        public static float LiebigMinimum(params float[] factors)
        {
            if (factors == null || factors.Length == 0) return 0f;

            float m = factors[0];
            for (int i = 1; i < factors.Length; i++)
                if (factors[i] < m) m = factors[i];

            return m;
        }

        // =====================================================================
        //  L-04  Environmental lapse rate
        //
        //  FORWARDED. The body lives in AtmoMath, because a lapse rate is a
        //  property of the atmosphere and the weather folder may not read this
        //  one. Kept here so existing ecology call sites keep compiling and keep
        //  meaning the same thing.
        // =====================================================================

        /// <summary>L-04. Sea-level temperature cooled by the standard environmental lapse rate.</summary>
        public static float LapseAdjustedTempC(float seaLevelC, float altitudeM)
        {
            return AtmoMath.LapseAdjustedTempC(seaLevelC, altitudeM);
        }

        /// <summary>L-04, with the rate supplied, so an authored lapse rate drives ecology too.</summary>
        public static float LapseAdjustedTempC(float seaLevelC, float altitudeM, float lapseRateCPerKm)
        {
            return AtmoMath.LapseAdjustedTempC(seaLevelC, altitudeM, lapseRateCPerKm);
        }

        // =====================================================================
        //  L-12  Topographic wetness index
        // =====================================================================

        /// <summary>
        /// L-12. ln(a / tan(beta)): high in hollows that collect drainage, low on
        /// ridges that shed it. Both arguments are floored, because flat ground
        /// drives the tangent to zero and the index to infinity.
        /// </summary>
        public static float TopographicWetnessIndex(float catchmentArea, float slopeRad)
        {
            if (catchmentArea < EcoConstants.TwiMinCatchmentArea)
                catchmentArea = EcoConstants.TwiMinCatchmentArea;

            if (slopeRad < EcoConstants.TwiMinSlopeRadians)
                slopeRad = EcoConstants.TwiMinSlopeRadians;

            return Mathf.Log(catchmentArea / Mathf.Tan(slopeRad));
        }

        // =====================================================================
        //  L-13  Potential evapotranspiration (Hargreaves-Samani)
        // =====================================================================

        /// <summary>
        /// L-13. mm/day from temperature and extraterrestrial radiation alone.
        /// Forwarded: the body lives in AtmoMath. Chosen over Penman-Monteith
        /// because that needs wind, humidity and net-radiation fields.
        /// </summary>
        public static float HargreavesPetMmPerDay(float tMinC, float tMaxC, float tMeanC, float raMjPerM2Day)
        {
            return AtmoMath.HargreavesPetMmPerDay(tMinC, tMaxC, tMeanC, raMjPerM2Day);
        }

        // =====================================================================
        //  L-14  Aridity index
        // =====================================================================

        /// <summary>
        /// L-14. P / PET, forwarded to AtmoMath. Under about 0.2 is desert, 0.2 to
        /// 0.5 semi-arid, 0.5 to 0.65 dry sub-humid, above that humid.
        /// </summary>
        public static float AridityIndex(float precipMmPerYear, float petMmPerYear)
        {
            return AtmoMath.AridityIndex(precipMmPerYear, petMmPerYear);
        }

        // =====================================================================
        //  L-17  Beer-Lambert canopy extinction
        // =====================================================================

        /// <summary>
        /// L-17. Fraction of light reaching the ground under a canopy of this
        /// leaf area index. This is what lets a shade-tolerant understory exist
        /// under a closed canopy instead of a single-species mat.
        /// </summary>
        public static float BeerLambertTransmittance(float leafAreaIndex, float k)
        {
            if (leafAreaIndex <= 0f) return 1f;
            if (leafAreaIndex > EcoConstants.LeafAreaIndexMax) leafAreaIndex = EcoConstants.LeafAreaIndexMax;
            if (k < 0f) k = 0f;

            return Mathf.Exp(-k * leafAreaIndex);
        }

        /// <summary>L-17 with the spherical-leaf-distribution default extinction coefficient.</summary>
        public static float BeerLambertTransmittance(float leafAreaIndex)
        {
            return BeerLambertTransmittance(leafAreaIndex, EcoConstants.BeerLambertDefaultK);
        }

        // =====================================================================
        //  L-18  Reineke self-thinning
        // =====================================================================

        /// <summary>
        /// L-18. Stems per hectare a stand of this mean trunk diameter can carry.
        /// A stand denser than this does not exist in nature, so it is the cap
        /// that keeps a mature forest from reading as a plantation.
        /// </summary>
        public static float ReinekeMaxStemsPerHa(float meanDbhM, float k)
        {
            if (meanDbhM < EcoConstants.ReinekeMinDbhM) meanDbhM = EcoConstants.ReinekeMinDbhM;
            if (k < 0f) k = 0f;

            return k * Mathf.Pow(meanDbhM, EcoConstants.ReinekeExponent);
        }

        /// <summary>L-18 with the temperate default coefficient.</summary>
        public static float ReinekeMaxStemsPerHa(float meanDbhM)
        {
            return ReinekeMaxStemsPerHa(meanDbhM, EcoConstants.ReinekeDefaultK);
        }

        /// <summary>Stems per hectare into stems per square metre, which is what the sampler works in.</summary>
        public static float StemsPerHaToPerSquareMetre(float stemsPerHa)
        {
            return stemsPerHa / EcoConstants.SquareMetresPerHectare;
        }

        // =====================================================================
        //  L-19  Crown allometry
        // =====================================================================

        /// <summary>
        /// L-19. Crown radius from trunk diameter. Spacing follows this rather
        /// than an authored minimum distance, which is why a stand of large trees
        /// is open and a stand of saplings is dense without either being tuned.
        /// </summary>
        public static float CrownRadiusM(float dbhM, float a, float b)
        {
            if (dbhM <= 0f) return 0f;
            return a * Mathf.Pow(dbhM, b);
        }

        /// <summary>L-19 with the broadleaf default coefficients.</summary>
        public static float CrownRadiusM(float dbhM)
        {
            return CrownRadiusM(dbhM, EcoConstants.CrownAllometryDefaultA, EcoConstants.CrownAllometryDefaultB);
        }

        // =====================================================================
        //  L-20  Poisson-disc spacing from density
        // =====================================================================

        /// <summary>
        /// L-20. sqrt(1 / (pi * rho)): the disc radius whose packing gives this
        /// many stems per square metre. Converting density to a radius here means
        /// the shipped Poisson sampler is driven by ecology rather than by an
        /// authored spacing that has to be re-tuned per biome.
        /// </summary>
        public static float PoissonRadiusFromDensity(float stemsPerSquareMetre)
        {
            if (stemsPerSquareMetre < EcoConstants.PoissonMinDensityPerSquareMetre)
                stemsPerSquareMetre = EcoConstants.PoissonMinDensityPerSquareMetre;

            return Mathf.Sqrt(1f / (Mathf.PI * stemsPerSquareMetre));
        }

        // =====================================================================
        //  L-21  Seed dispersal kernel
        // =====================================================================

        /// <summary>
        /// L-21. exp(-d / lambda). Weight of a site at this distance from a
        /// parent. Small lambda produces clumps and gaps, large lambda produces
        /// an even field; it is the single field that decides whether a forest
        /// reads as grown or as sprinkled.
        /// </summary>
        public static float DispersalWeight(float distanceM, float lambdaM)
        {
            if (lambdaM <= 0f) return distanceM <= 0f ? 1f : 0f;
            if (distanceM <= 0f) return 1f;

            return Mathf.Exp(-distanceM / lambdaM);
        }

        /// <summary>Distance past which L-21's weight is negligible, so a search can stop.</summary>
        public static float DispersalCutoffM(float lambdaM)
        {
            return lambdaM * EcoConstants.DispersalCutoffLambdaMultiple;
        }

        // =====================================================================
        //  L-24  Growing degree days
        // =====================================================================

        /// <summary>
        /// L-24. Accumulated warmth above a base temperature, forwarded to AtmoMath.
        /// Budburst fires on this rather than on a calendar date, which is why a cold
        /// spring is genuinely late instead of merely cooler.
        /// </summary>
        public static float GrowingDegreeDays(float tMeanC, float baseC, float days)
        {
            return AtmoMath.GrowingDegreeDays(tMeanC, baseC, days);
        }

        // =====================================================================
        //  L-25  Salt spray decay
        // =====================================================================

        /// <summary>
        /// L-25. exp(-d / d0) inland from the shoreline. The reason coastal
        /// vegetation is a band of scrub, then pine, then forest rather than a
        /// single edge.
        /// </summary>
        public static float SaltSprayFraction(float distanceM, float decayM)
        {
            if (decayM <= 0f) return 0f;
            if (distanceM <= 0f) return 1f;

            return Mathf.Exp(-distanceM / decayM);
        }

        /// <summary>L-25 with the measured 120 m e-folding distance.</summary>
        public static float SaltSprayFraction(float distanceM)
        {
            return SaltSprayFraction(distanceM, EcoConstants.SaltSprayDecayMetres);
        }

        /// <summary>
        /// L-25b. Salinity of a water body itself, as a fraction of open seawater.
        ///
        /// Only the ocean is saline - 35 g/kg, hence 1. Lake, river and pool are the
        /// fresh class and report 0.5/35, the top of that class rather than zero.
        ///
        /// This is the quantity to use for a SUBMERGED site. Spray decay answers a
        /// different question - how much salt the wind has carried onto dry ground -
        /// and the two must not be blended, because taking the larger of them would
        /// make every lake within spray range read as seawater.
        ///
        /// KNOWN LIMIT: WaterBodyKind carries no saline-lake flag, so a salt lake or
        /// a tidal estuary authored as Lake or River reads fresh here. Adding that
        /// flag changes serialised graph data and belongs with the water system, not
        /// with this function.
        /// </summary>
        public static float BodySalinityFraction(WaterBodyKind kind)
        {
            return kind == WaterBodyKind.Ocean
                 ? 1f
                 : EcoConstants.FreshWaterSalinityFraction;
        }

        // =====================================================================
        //  L-26  Frost-free fraction
        // =====================================================================

        /// <summary>
        /// L-26. Share of the year above freezing, forwarded to AtmoMath, where it is
        /// solved analytically from the annual temperature sinusoid.
        /// </summary>
        public static float FrostFreeFraction(float meanAnnualTempC, float annualHalfAmplitudeC)
        {
            return AtmoMath.FrostFreeFraction(meanAnnualTempC, annualHalfAmplitudeC);
        }

        /// <summary>
        /// L-26 from the two monthly extremes, which is the form the environment
        /// sample carries.
        /// </summary>
        public static float FrostFreeFractionFromExtremes(float coldestMonthC, float warmestMonthC)
        {
            return AtmoMath.FrostFreeFractionFromExtremes(coldestMonthC, warmestMonthC);
        }
    }
}
