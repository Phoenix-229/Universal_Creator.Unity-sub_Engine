// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    CloudTypeTable.cs
//  Purpose: Decide the genus of each cloud deck from the physics the weather layer
//           already resolves - never from an authored field. Stability grows the
//           heaps on the low deck, an approaching front hangs the layered sheets
//           from the high deck down, humidity and wind settle a stable deck into
//           stratus or breaks it into stratocumulus, and stable flow forced over a
//           ridge parks a lenticular. One decision table, the same inputs the rest
//           of the sky reads, so the drawn cloud and the weather cannot disagree.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>The three decks the sky draws, low beneath mid beneath high.</summary>
    public enum CloudLayer { Low, Mid, High }

    /// <summary>
    /// The ten cloud genera the sky can render, each a distinct silhouette and
    /// behaviour rather than a coarse "how much grey". Decided per deck by
    /// <see cref="CloudTypeTable.Classify"/> from resolved weather.
    /// </summary>
    public enum CloudGenus
    {
        None,
        Cirrus, Cirrostratus,
        Altostratus, Altocumulus,
        Stratus, Stratocumulus,
        CumulusHumilis, CumulusCongestus, Cumulonimbus,
        Nimbostratus,
        Lenticular
    }

    /// <summary>
    /// The cloud-genus decision table. Pure function of resolved weather scalars, so
    /// the same column yields the same sky on every machine and nothing is authored.
    /// </summary>
    public static class CloudTypeTable
    {
        /// <summary>
        /// The genus of one deck. Stable orographic lift parks a lenticular at any deck;
        /// otherwise instability decides the low deck's heaps, an approaching warm or
        /// occluded front hangs the layered sheets from the top down, and a stable deck
        /// settles into stratus or breaks into stratocumulus by its humidity and wind.
        /// </summary>
        public static CloudGenus Classify(
            CloudLayer layer, float layerCover01,
            float stabilityLapseCPerKm,
            WeatherFront.FrontKind frontKind, float frontDistanceKm,
            float columnRh01, float windSpeedMPerS, bool orographicLift)
        {
            WeatherStability.StabilityState stability =
                WeatherStability.Classify(stabilityLapseCPerKm);

            // Stable air forced over a range stands as a stationary lens over the ridge.
            if (orographicLift && stability == WeatherStability.StabilityState.Stable)
                return CloudGenus.Lenticular;

            if (layerCover01 <= 0f) return CloudGenus.None;

            bool frontal = frontKind == WeatherFront.FrontKind.Warm
                        || frontKind == WeatherFront.FrontKind.Occluded;
            bool withinVeil = frontal && frontDistanceKm <= AtmoConstants.CloudFrontVeilRangeKm;
            bool withinRain = frontal && frontDistanceKm <= AtmoConstants.CloudFrontRainRangeKm;

            switch (layer)
            {
                case CloudLayer.High: return HighDeck(withinVeil);
                case CloudLayer.Mid:  return MidDeck(withinVeil);
                default:              return LowDeck(stability, stabilityLapseCPerKm,
                                                     withinRain, columnRh01, windSpeedMPerS);
            }
        }

        // The high deck leads a front: thin cirrus at the far edge, thickening to a
        // milky cirrostratus veil as the front closes in.
        private static CloudGenus HighDeck(bool withinVeil)
        {
            return withinVeil ? CloudGenus.Cirrostratus : CloudGenus.Cirrus;
        }

        // The mid deck: a layered altostratus sheet under frontal lift, a dappled
        // altocumulus under ordinary mid-level moisture.
        private static CloudGenus MidDeck(bool withinVeil)
        {
            return withinVeil ? CloudGenus.Altostratus : CloudGenus.Altocumulus;
        }

        // The low deck carries both mechanisms: convection builds the heaps, a close
        // front drops nimbostratus, and a stable humid deck is stratus or stratocumulus.
        private static CloudGenus LowDeck(WeatherStability.StabilityState stability,
            float stabilityLapseCPerKm, bool withinRain, float columnRh01, float windSpeedMPerS)
        {
            if (stability == WeatherStability.StabilityState.AbsolutelyUnstable)
                return CloudGenus.Cumulonimbus;

            if (stability == WeatherStability.StabilityState.ConditionallyUnstable)
                return stabilityLapseCPerKm >= AtmoConstants.CloudCongestusMinLapseCPerKm
                       ? CloudGenus.CumulusCongestus : CloudGenus.CumulusHumilis;

            // Stable or inverted: a close front rains as nimbostratus; failing that a
            // humid, near-calm deck is flat stratus, and anything else breaks lumpy.
            if (withinRain) return CloudGenus.Nimbostratus;

            if (columnRh01 >= AtmoConstants.CloudStratusMinRh01
                && windSpeedMPerS <= AtmoConstants.CloudStratusMaxWindMPerS)
                return CloudGenus.Stratus;

            return CloudGenus.Stratocumulus;
        }
    }
}
