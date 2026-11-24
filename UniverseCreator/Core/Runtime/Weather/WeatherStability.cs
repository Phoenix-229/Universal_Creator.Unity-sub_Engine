// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherStability.cs
//  Purpose: The one test that decides cumulus from stratus and shower from
//           drizzle: the observed near-surface lapse rate against the dry and
//           saturated adiabats. Surface heating drives it over the day, so the
//           same point storms in the afternoon and lies under flat grey at dawn.
//
//  WHY LAPSE AND NOT A WEATHER-TYPE ROLL
//  -------------------------------------
//  Stability is not a mood the sky is in; it is whether a lifted parcel keeps
//  rising. That is a comparison of the environmental lapse against the adiabat a
//  parcel would follow, and every convective cloud, every afternoon thunderstorm
//  and every fog-bound morning is the same comparison landing in a different band.
//  Reading it from the sun-driven lapse keeps all of them on one mechanism.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Solar geometry comes from ClockMath, moisture from CloudCover,
//  both in this same folder; nothing here reaches into the ecology layer.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Atmospheric stability from the near-surface lapse rate. Every method is a
    /// pure function of its arguments; nothing here holds state.
    /// </summary>
    public static class WeatherStability
    {
        /// <summary>
        /// The four bands of the stability table, ordered from most stable to most
        /// unstable so a cast to int rises with convective vigour.
        /// </summary>
        public enum StabilityState { Inversion, Stable, ConditionallyUnstable, AbsolutelyUnstable }

        /// <summary>
        /// Places an observed lapse in the table: below zero is an inversion, below
        /// the saturated adiabat is stable, below the dry adiabat is conditionally
        /// unstable, and steeper than the dry adiabat is absolute instability.
        /// </summary>
        public static StabilityState Classify(float lapseCPerKm)
        {
            if (lapseCPerKm < 0f) return StabilityState.Inversion;
            if (lapseCPerKm < AtmoConstants.SaturatedAdiabaticCPerKm)
                return StabilityState.Stable;
            if (lapseCPerKm < AtmoConstants.DryAdiabaticCPerKm)
                return StabilityState.ConditionallyUnstable;
            return StabilityState.AbsolutelyUnstable;
        }

        /// <summary>
        /// degC/km the sun adds to (or, at night, takes from) the near-surface lapse.
        /// The ground's response lags peak insolation, so the hour angle is shifted
        /// back before the sun's height is read: the boost then peaks in the early
        /// afternoon, and an hour or two after sunrise the ground is still on the
        /// night side of the lag - stably stratified, not yet convecting. Below the
        /// horizon the term is the fixed nocturnal inversion.
        /// </summary>
        public static float SurfaceHeatingBoostCPerKm(float latitudeDeg,
            float declinationDeg, float hourAngleDeg)
        {
            float insolation = ClockMath.LaggedInsolation01(
                latitudeDeg, declinationDeg, hourAngleDeg);

            if (insolation <= 0f)
                return -AtmoConstants.StabilityNocturnalInversionCPerKm;

            return insolation * AtmoConstants.StabilityMaxHeatingBoostCPerKm;
        }

        /// <summary>
        /// degC/km observed near the surface: the environmental rate the standing
        /// atmosphere holds, shifted by the sun. This is the number the whole table
        /// reads from, so the classification and the terrain temperature share one
        /// environmental lapse rather than authoring two that could disagree.
        /// </summary>
        public static float ObservedLapseCPerKm(float latitudeDeg,
            float declinationDeg, float hourAngleDeg)
        {
            return AtmoConstants.LapseRateCPerKm
                + SurfaceHeatingBoostCPerKm(latitudeDeg, declinationDeg, hourAngleDeg);
        }

        /// <summary>
        /// 0-1 convective cloud cover. It needs both halves of a thunderstorm: the
        /// instability to lift a parcel - how far the lapse runs past the saturated
        /// adiabat toward the dry one - and the column moisture to condense once it
        /// is lifted. Dry instability makes dust devils, not cumulus, so the moisture
        /// gate is what keeps a hot desert afternoon cloudless.
        /// </summary>
        public static float ConvectiveCover01(float lapseCPerKm, float columnHumidity01)
        {
            float span = AtmoConstants.DryAdiabaticCPerKm
                - AtmoConstants.SaturatedAdiabaticCPerKm;
            float instability = Mathf.Clamp01(
                (lapseCPerKm - AtmoConstants.SaturatedAdiabaticCPerKm) / span);

            return Mathf.Clamp01(instability * Mathf.Clamp01(columnHumidity01));
        }

        /// <summary>
        /// Everything a caller needs from one stability read: the band, the lapse it
        /// came from, the sun's contribution to it, and the convective cover that
        /// <see cref="CloudCover.TotalCover01"/> takes as its convective term.
        /// </summary>
        public struct StabilityReading
        {
            public StabilityState State;
            public float LapseCPerKm;
            public float SurfaceHeatingBoostCPerKm;
            public float ConvectiveCover01;
        }

        /// <summary>
        /// Resolves the stability at a point and time: the sun-driven lapse, its band,
        /// and the convective cover the column moisture supports. The moisture is read
        /// from the same column humidity the cloud decks use, so the convective term
        /// and the stratiform decks agree on how wet the air is.
        /// </summary>
        public static StabilityReading Resolve(WeatherCellField field,
            float worldX, float worldZ, float latitudeDeg, float declinationDeg,
            float hourAngleDeg, float itczLatitudeDeg, float timeDays)
        {
            StabilityReading reading = default;

            float boost =
                SurfaceHeatingBoostCPerKm(latitudeDeg, declinationDeg, hourAngleDeg);
            float lapse = AtmoConstants.LapseRateCPerKm + boost;

            reading.SurfaceHeatingBoostCPerKm = boost;
            reading.LapseCPerKm = lapse;
            reading.State = Classify(lapse);

            float columnRh = CloudCover.ColumnHumidity01(field, worldX, worldZ,
                latitudeDeg, itczLatitudeDeg, timeDays);
            reading.ConvectiveCover01 = ConvectiveCover01(lapse, columnRh);

            return reading;
        }
    }
}
