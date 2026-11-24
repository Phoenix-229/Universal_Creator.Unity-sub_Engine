// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WeatherPreset.cs
//  Purpose: Nine climate archetypes, each a small bag of GEOGRAPHY - latitude,
//           the maritime-to-continental axis, an annual precipitation total, a
//           precipitation regime and a few atmospheric figures. The climate then
//           follows from the § 7.2 physics; a preset never authors a weather mood
//           or a monthly curve, only the place the physics runs in.
//
//  WHY THIS LIVES IN TerrainSystem AND NOT Weather
//  -----------------------------------------------
//  A preset table is, by its nature, a page of real-world numbers - 2° at the
//  equator, 2400 mm in a rainforest, 75° at the pole. The Weather folder is held
//  to a no-bare-literal rule so its physics cannot hide a magic constant; a table
//  of places would violate that rule on every line. TerrainSystem is exempt and
//  may reference Weather (the manager here already builds ClimateRequests), so the
//  numbers live on this side and feed the pure physics through ClimateRequest.
//
//  APPLY IS THE MANAGER'S JOB
//  --------------------------
//  A preset only carries the numbers. UCWeatherSystemManager.ApplyPreset copies
//  the geography onto its own serialized fields, after which the author tunes and
//  the existing climate path derives the year. ToClimateRequest is the same bag
//  handed straight to ClimateNormals.Build for a position, which is what the
//  self-test and any per-chunk producer use.
// -----------------------------------------------------------------------------

using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>The nine shipped climate archetypes an author starts a world from.</summary>
    public enum WeatherPresetKind
    {
        EquatorialRainforest = 0,
        MonsoonTropics = 1,
        HotDesert = 2,
        Mediterranean = 3,
        TemperateOceanic = 4,
        Continental = 5,
        Boreal = 6,
        Polar = 7,
        Alpine = 8,
    }
    /// <summary>
    /// One archetype's geography. Every figure is an input to the § 7.2 climate
    /// physics, not an output of it: set them and the year's shape is derived.
    /// </summary>
    public struct WeatherPreset
    {
        /// <summary>Which archetype this bag was filled from.</summary>
        public WeatherPresetKind kind;

        /// <summary>Signed latitude of the world centre. Its sign picks the hemisphere.</summary>
        public float latitudeDeg;

        /// <summary>Metres above sea level the archetype is characterised at (alpine sits high).</summary>
        public float altitudeM;

        /// <summary>0 fully maritime, 1 fully continental. Scales the annual temperature swing.</summary>
        public float continentality01;

        /// <summary>mm/yr annual precipitation total. Only its seasonal distribution is derived.</summary>
        public float annualPrecipMm;

        /// <summary>0-1 mean relative humidity. Damps the diurnal range.</summary>
        public float humidity01;

        /// <summary>0-1 mean cloud cover. Damps the diurnal range and the surface radiation.</summary>
        public float cloudCover01;

        /// <summary>
        /// 0-1 atmospheric turbidity (aerosol load). Carried for the sky when it lands;
        /// no climate field consumes it yet, so it is preset data, not applied state.
        /// </summary>
        public float turbidity01;

        /// <summary>How precipitation is spread across the year for this archetype.</summary>
        public PrecipitationRegime regime;

        private static WeatherPreset Make(
            WeatherPresetKind kind, float latitudeDeg, float altitudeM,
            float continentality01, float annualPrecipMm, float humidity01,
            float cloudCover01, float turbidity01, PrecipitationRegime regime)
        {
            WeatherPreset p = new WeatherPreset();
            p.kind = kind;
            p.latitudeDeg = latitudeDeg;
            p.altitudeM = altitudeM;
            p.continentality01 = continentality01;
            p.annualPrecipMm = annualPrecipMm;
            p.humidity01 = humidity01;
            p.cloudCover01 = cloudCover01;
            p.turbidity01 = turbidity01;
            p.regime = regime;
            return p;
        }

        /// <summary>The shipped figures for one archetype. Real places, real numbers.</summary>
        public static WeatherPreset For(WeatherPresetKind kind)
        {
            switch (kind)
            {
                case WeatherPresetKind.EquatorialRainforest:
                    return Make(kind, 2f, 0f, 0.15f, 2400f, 0.85f, 0.70f, 0.40f,
                                PrecipitationRegime.Equatorial);
                case WeatherPresetKind.MonsoonTropics:
                    return Make(kind, 15f, 0f, 0.45f, 1600f, 0.70f, 0.50f, 0.45f,
                                PrecipitationRegime.Monsoon);
                case WeatherPresetKind.HotDesert:
                    return Make(kind, 27f, 0f, 0.85f, 90f, 0.25f, 0.10f, 0.55f,
                                PrecipitationRegime.PolarOrDesert);
                case WeatherPresetKind.Mediterranean:
                    return Make(kind, 38f, 0f, 0.35f, 600f, 0.60f, 0.40f, 0.30f,
                                PrecipitationRegime.Mediterranean);
                case WeatherPresetKind.TemperateOceanic:
                    return Make(kind, 52f, 0f, 0.20f, 900f, 0.80f, 0.65f, 0.25f,
                                PrecipitationRegime.TemperateOceanic);
                case WeatherPresetKind.Continental:
                    return Make(kind, 48f, 0f, 0.85f, 550f, 0.60f, 0.50f, 0.30f,
                                PrecipitationRegime.Continental);
                case WeatherPresetKind.Boreal:
                    return Make(kind, 62f, 0f, 0.80f, 450f, 0.70f, 0.60f, 0.20f,
                                PrecipitationRegime.Continental);
                case WeatherPresetKind.Polar:
                    return Make(kind, 75f, 0f, 0.70f, 200f, 0.75f, 0.60f, 0.15f,
                                PrecipitationRegime.PolarOrDesert);
                case WeatherPresetKind.Alpine:
                    return Make(kind, 46f, 2200f, 0.50f, 1100f, 0.70f, 0.60f, 0.20f,
                                PrecipitationRegime.TemperateOceanic);
                default:
                    return Make(kind, 45f, 0f, 0.50f, 800f, 0.50f, 0.50f, 0.30f,
                                PrecipitationRegime.TemperateOceanic);
            }
        }

        /// <summary>
        /// This preset as a ClimateRequest for one position, ready for
        /// ClimateNormals.Build. The regime is authored so the geography, not a
        /// re-derivation, chooses the year's precipitation shape; the coast distance
        /// comes from the maritime axis through the same map the manager uses.
        /// </summary>
        public ClimateRequest ToClimateRequest(float gddBaseC)
        {
            ClimateRequest r = ClimateRequest.Default(latitudeDeg, altitudeM,
                                                      annualPrecipMm, gddBaseC);
            r.distanceToCoastM =
                ClimateNormals.CoastDistanceMFromContinentality01(continentality01);
            r.cloudCover01 = cloudCover01;
            r.humidity01 = humidity01;
            r.regime = regime;
            r.regimeIsAuthored = true;
            return r;
        }
    }

}
