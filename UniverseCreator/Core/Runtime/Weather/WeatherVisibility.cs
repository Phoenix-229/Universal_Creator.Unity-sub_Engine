// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherVisibility.cs
//  Purpose: How far the eye can see, in metres, set by the two things that take
//           the distance away: fog and falling precipitation. They are combined
//           by adding their extinctions, not by multiplying them - two veils in
//           front of you are a little worse than the worse one alone, never their
//           product, which is what would turn light rain in light haze into an
//           absurd fifty-metre whiteout.
//
//  WHY EXTINCTIONS ADD
//  -------------------
//  Visibility is the reciprocal of how fast light is scattered out of the line of
//  sight - the extinction. Two scatterers in the same air scatter independently,
//  so their extinctions add; the combined visibility is therefore the smooth,
//  physical form of "whichever is worse" - it tracks the murkier of the two and
//  eases through the crossover instead of cornering, and it can never collapse
//  below what a real double veil would do.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Fog density, precipitation rate and the snow fraction arrive as
//  plain scalars; it computes none of them and reaches into no other layer.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Visibility distance in metres from fog and precipitation. Every method is a
    /// pure function of its arguments.
    /// </summary>
    public static class WeatherVisibility
    {
        /// <summary>
        /// Visibility through fog alone, in metres. Inverse in the fog density, so
        /// the thickest fog sits at its reference floor and thinner fog opens up
        /// from there; no fog returns the clear-air ceiling.
        /// </summary>
        public static float FogVisibilityMetres(float fogDensity01)
        {
            float density = Mathf.Clamp01(fogDensity01);
            if (density <= 1e-4f) return AtmoConstants.VisibilityClearMetres;

            float v = AtmoConstants.FogVisibilityAtFullDensityMetres / density;
            return Mathf.Min(v, AtmoConstants.VisibilityClearMetres);
        }

        /// <summary>
        /// Visibility through falling precipitation alone, in metres. A power law in
        /// the rate, anchored at the reference heavy rate, and interpolated between
        /// the rain and snow references by the snow fraction - snow scatters far
        /// harder than rain, so the same rate as snow sees much less far. No
        /// precipitation returns the clear-air ceiling.
        /// </summary>
        public static float PrecipVisibilityMetres(float precipMmPerHour, float snowFraction01)
        {
            float rate = Mathf.Max(0f, precipMmPerHour);
            if (rate <= 1e-4f) return AtmoConstants.VisibilityClearMetres;

            float snow = Mathf.Clamp01(snowFraction01);
            float atReference = Mathf.Lerp(AtmoConstants.RainVisibilityAtReferenceMetres,
                                           AtmoConstants.SnowVisibilityAtReferenceMetres, snow);

            float ratio = AtmoConstants.PrecipVisibilityReferenceMmPerH / rate;
            float v = atReference * Mathf.Pow(ratio, AtmoConstants.PrecipVisibilityRateExponent);
            return Mathf.Min(v, AtmoConstants.VisibilityClearMetres);
        }

        /// <summary>
        /// The two combined, in metres, by adding their extinctions above the clear-air
        /// baseline: the murkier one dominates, both together are a little worse, and an
        /// absent veil adds nothing so two clear-air terms stay at the clear ceiling.
        /// </summary>
        public static float CombineMetres(float fogVisibilityMetres, float precipVisibilityMetres)
        {
            float clearExtinction = 1f / AtmoConstants.VisibilityClearMetres;

            float fogExtinction = Mathf.Max(0f,
                1f / Mathf.Max(1e-4f, fogVisibilityMetres) - clearExtinction);
            float precipExtinction = Mathf.Max(0f,
                1f / Mathf.Max(1e-4f, precipVisibilityMetres) - clearExtinction);

            float total = clearExtinction + fogExtinction + precipExtinction;
            return Mathf.Min(1f / total, AtmoConstants.VisibilityClearMetres);
        }

        /// <summary>
        /// Visibility from the world's fog and precipitation together, in metres - the
        /// two component visibilities combined. The single call the driver publishes as
        /// the shipped <c>visibility</c>.
        /// </summary>
        public static float VisibilityMetres(float fogDensity01, float precipMmPerHour,
                                             float snowFraction01)
        {
            float fog = FogVisibilityMetres(fogDensity01);
            float precip = PrecipVisibilityMetres(precipMmPerHour, snowFraction01);
            return CombineMetres(fog, precip);
        }
    }
}
