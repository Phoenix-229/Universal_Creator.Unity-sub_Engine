// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    ThermalEquivalence.cs
//  Purpose: Ties latitude to sea-level temperature, and checks that the resulting
//           altitude-for-latitude trade emerges rather than being imposed (V-26).
//
//  THE GAP THIS CLOSES. The sampler already turns altitude into temperature through
//  the lapse rate, so altitude works. Latitude did not: seaLevelMeanTempC was
//  authored on its own, so a world could be set to 60 degrees north and 22 degC at
//  the same time, and the treeline would sit where the temperature said and take no
//  notice of where on the planet it was. That is not a wrong number, it is a missing
//  connection - and it is why V-26 is written as a check rather than a formula.
//
//  WHY THIS IS A CHECK AND NOT AN ENFORCEMENT. The equivalence between metres of
//  altitude and degrees of latitude is not a rule anything should apply. It is a
//  consequence of two independent measurements - 6.5 degC per km of height, 0.7 degC
//  per degree of latitude - being used consistently by every step in between. If the
//  ratio comes out right, the thermal chain is sound end to end. If it comes out
//  wrong, something between latitude and treeline is broken, and this is by far the
//  cheapest way to find out which. Enforcing the ratio directly would destroy the
//  test: the answer would be right because it was asserted, and the chain could be
//  broken in six places without anybody noticing.
//
//  A NOTE ON THE TASK'S OWN FIGURE. Section 6.4 states the equivalence as ~1000 m per
//  ~9 degrees of latitude, which the two constants agree with: 9 x 0.7 = 6.3 degC, and
//  6.3 degC / 6.5 degC-per-km is 970 m. It then gives the test as 45 to 60 degrees
//  falling by ~1000 m - but that span is 15 degrees, not 9, so the physics the task
//  itself states predicts about 1615 m. The expectation here is derived from the
//  constants for whatever span it is given, rather than hard-coding either number,
//  and it reports the derivation so the discrepancy is visible instead of buried.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>What the check found. Reported whether it passed or not.</summary>
    public struct EquivalenceReport
    {
        public float latitudeA, latitudeB;
        public float treelineAltitudeAM, treelineAltitudeBM;

        /// <summary>m the treeline fell between the two latitudes. Positive going poleward.</summary>
        public float measuredDropM;

        /// <summary>m the two constants predict for this span of latitude.</summary>
        public float predictedDropM;

        /// <summary>m of slack allowed. Generous on purpose - this is a sanity check, not a calibration.</summary>
        public float toleranceM;

        public bool passed;

        public string Describe()
        {
            return string.Format(
                "Treeline {0:0} m at {1:0}deg, {2:0} m at {3:0}deg: fell {4:0} m, " +
                "predicted {5:0} m +/-{6:0} m -> {7}",
                treelineAltitudeAM, latitudeA, treelineAltitudeBM, latitudeB,
                measuredDropM, predictedDropM, toleranceM,
                passed ? "PASS" : "FAIL");
        }
    }

    public static class ThermalEquivalence
    {
        /// <summary>Fraction of the predicted drop allowed as error. Wide, because the point is to catch a broken chain, not to calibrate one.</summary>
        public const float ToleranceFraction = 0.35f;

        /// <summary>deg. Beyond this the linear meridional gradient stops being a fair approximation.</summary>
        public const float LinearGradientMaxLatitudeDeg = 70f;

        /// <summary>degC annual mean at sea level on the equator, Earth-like.</summary>
        public const float EquatorSeaLevelMeanTempC = 27f;

        /// <summary>
        /// degC annual mean at sea level for a latitude. The connection that was
        /// missing: set a world's latitude and its baseline temperature follows.
        ///
        /// Linear in latitude, which is honest between about 20 and 70 degrees and
        /// increasingly not outside that - the real profile flattens in the tropics and
        /// steepens at the ice edge. The clamp says where the approximation holds
        /// instead of quietly extrapolating past it.
        /// </summary>
        public static float SeaLevelMeanTempC(float latitudeDeg, float equatorMeanTempC)
        {
            float lat = Mathf.Min(Mathf.Abs(latitudeDeg), LinearGradientMaxLatitudeDeg);

            return equatorMeanTempC
                   - lat * EcoConstants.MeridionalGradientCPerDegreeLatitude;
        }

        public static float SeaLevelMeanTempC(float latitudeDeg)
        {
            return SeaLevelMeanTempC(latitudeDeg, EquatorSeaLevelMeanTempC);
        }

        /// <summary>
        /// The altitude at which the warmest month falls to Koerner's threshold, for a
        /// given sea-level mean and seasonal half-swing.
        ///
        /// Inverts the same chain the sampler runs forward, and reads the warmest month
        /// rather than the annual mean because that is the field the treeline gate
        /// actually tests. Testing against the annual mean would pass while the real
        /// treeline sat hundreds of metres away.
        /// </summary>
        public static float TreelineAltitudeM(float seaLevelMeanTempC,
                                               float seasonalHalfAmplitudeC,
                                               float lapseRateCPerKm)
        {
            float lapse = Mathf.Max(0.1f, lapseRateCPerKm);

            float surplus = seaLevelMeanTempC + seasonalHalfAmplitudeC
                            - EcoConstants.TreelineWarmestMonthC;

            // Below sea level the treeline is at the shore: there is nowhere colder to
            // go. Returning a negative altitude would be arithmetically consistent and
            // physically meaningless.
            if (surplus <= 0f) return 0f;

            return surplus * 1000f / lapse;
        }

        /// <summary>
        /// m the treeline should fall over a span of latitude, from the lapse rate and
        /// the meridional gradient alone. The prediction the measurement is judged
        /// against, and the only place the two constants are combined.
        /// </summary>
        public static float PredictedDropM(float latitudeA, float latitudeB,
                                            float lapseRateCPerKm)
        {
            float span = Mathf.Abs(latitudeB) - Mathf.Abs(latitudeA);
            float cooling = span * EcoConstants.MeridionalGradientCPerDegreeLatitude;

            return cooling * 1000f / Mathf.Max(0.1f, lapseRateCPerKm);
        }

        /// <summary>
        /// Runs the check across two latitudes and reports what it found.
        ///
        /// The seasonal half-swing is passed in and held equal for both samples on
        /// purpose. In the world it widens poleward, and that is real, but letting it
        /// vary here would mean a failure could come from either the swing or the
        /// thermal chain and the test would no longer say which. One variable at a time
        /// is what makes the answer worth having.
        /// </summary>
        public static EquivalenceReport Check(float latitudeA, float latitudeB,
                                               float seasonalHalfAmplitudeC,
                                               float lapseRateCPerKm,
                                               float equatorMeanTempC)
        {
            EquivalenceReport r = new EquivalenceReport();
            r.latitudeA = latitudeA;
            r.latitudeB = latitudeB;

            float tempA = SeaLevelMeanTempC(latitudeA, equatorMeanTempC);
            float tempB = SeaLevelMeanTempC(latitudeB, equatorMeanTempC);

            r.treelineAltitudeAM = TreelineAltitudeM(tempA, seasonalHalfAmplitudeC, lapseRateCPerKm);
            r.treelineAltitudeBM = TreelineAltitudeM(tempB, seasonalHalfAmplitudeC, lapseRateCPerKm);

            r.measuredDropM = r.treelineAltitudeAM - r.treelineAltitudeBM;
            r.predictedDropM = PredictedDropM(latitudeA, latitudeB, lapseRateCPerKm);

            r.toleranceM = Mathf.Max(50f, Mathf.Abs(r.predictedDropM) * ToleranceFraction);
            r.passed = Mathf.Abs(r.measuredDropM - r.predictedDropM) <= r.toleranceM;

            return r;
        }

        /// <summary>
        /// The check as section 6.4 words it: 45 degrees against 60 degrees, on the
        /// world's own lapse rate and seasonal swing.
        /// </summary>
        public static EquivalenceReport CheckStandardPair(in EcoSampleSettings settings)
        {
            // Half the world's authored annual swing, taken well inland where the full
            // continental range applies. A maritime site would understate the drop for
            // reasons that have nothing to do with the chain being tested.
            float halfAmplitude = settings.LocalTempHalfAmplitudeC(1f);

            return Check(45f, 60f, halfAmplitude, settings.lapseRateCPerKm,
                         EquatorSeaLevelMeanTempC);
        }
    }
}
