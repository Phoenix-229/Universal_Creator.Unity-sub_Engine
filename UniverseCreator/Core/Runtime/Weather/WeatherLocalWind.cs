// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherLocalWind.cs
//  Purpose: The winds the ground itself makes - the sea breeze, the up- and
//           down-slope flow, and the speed-up through a valley gap - as closed
//           forms added to the synoptic surface wind. Each is keyed off geography
//           the world already owns and reverses on the same diurnal forcing the
//           stability lapse rides, so the coast and the sky keep one clock.
//
//  WHY ADD RATHER THAN REPLACE
//  ---------------------------
//  The large-scale wind is right out at sea and high on a ridge; these are the
//  corrections geography makes to it near a coast or on a slope. Adding a vector
//  keeps the synoptic flow the single owner of the background wind and lets the
//  local terms vanish smoothly where their geography does - far inland, on flat
//  ground, in an open valley - instead of a second wind fighting the first.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. The caller supplies the geometry (coast normal, downhill
//  direction, slope, valley constriction) as plain vectors and scalars; nothing
//  here reaches into the terrain or ecology layers to fetch it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// The local circulations, each a pure function of its geometry and the sun.
    /// Vectors are world XZ in m/s, so they add straight onto the surface wind.
    /// </summary>
    public static class WeatherLocalWind
    {
        /// <summary>
        /// −1..+1 diurnal forcing shared by every local circulation: the lagged
        /// insolation by day, a weaker fixed negative at night. Positive drives the
        /// onshore and upslope sense, negative the nocturnal reversal - so all three
        /// winds turn together on the one clock.
        /// </summary>
        public static float DiurnalForcingSigned(float latitudeDeg,
            float declinationDeg, float hourAngleDeg)
        {
            float insolation = ClockMath.LaggedInsolation01(
                latitudeDeg, declinationDeg, hourAngleDeg);

            return insolation > 0f
                ? insolation
                : -AtmoConstants.NocturnalForcingFraction;
        }

        /// <summary>
        /// m/s world XZ. The sea breeze: onshore where the day heats the land above
        /// the sea, reversing to a weaker offshore land breeze at night, and falling
        /// off linearly to nothing at the inland range. The onshore direction is the
        /// coast normal pointing from sea to land; a point out of range or a coast
        /// with no thermal contrast gets no breeze.
        /// </summary>
        public static Vector2 SeaBreezeWorldMPerS(Vector2 onshoreDirWorld,
            float distanceToCoastM, float landSeaTempDiffC, float forcingSigned)
        {
            if (distanceToCoastM >= AtmoConstants.SeaBreezeRangeM) return Vector2.zero;

            float proximity = 1f - distanceToCoastM / AtmoConstants.SeaBreezeRangeM;
            float speed = proximity * forcingSigned * landSeaTempDiffC
                          * AtmoConstants.SeaBreezeSpeedMPerSPerC;

            return onshoreDirWorld * speed;
        }

        /// <summary>
        /// m/s world XZ. The slope wind: upslope (anabatic) by day, downslope
        /// (katabatic) by night, its magnitude set by the steepness and the forcing.
        /// The downhill direction is the way water would run; day forcing sends the
        /// wind against it, night forcing with it, which is the cold air draining into
        /// the valley that pools fog and frost on the floor after sunset.
        /// </summary>
        public static Vector2 SlopeWindWorldMPerS(Vector2 downhillDirWorld,
            float slope01, float forcingSigned)
        {
            float magnitude = Mathf.Clamp01(slope01) * forcingSigned
                              * AtmoConstants.SlopeWindReferenceMPerS;

            // Uphill is the reverse of downhill, so day forcing (positive) blows up
            // and night forcing (negative) blows down - one expression, both senses.
            return -downhillDirWorld * magnitude;
        }

        /// <summary>
        /// m/s world XZ. The gap wind: where a valley narrows, mass conservation
        /// speeds the flow already moving along it. This is a venturi on the existing
        /// along-valley component, not a wind of its own - an open valley or a
        /// crosswind through the gap adds nothing.
        /// </summary>
        public static Vector2 GapWindWorldMPerS(Vector2 alongValleyDirWorld,
            Vector2 ambientWorldMPerS, float valleyConstriction01)
        {
            Vector2 axis = alongValleyDirWorld.normalized;
            float alongComponent = Vector2.Dot(ambientWorldMPerS, axis);
            float speedUp = alongComponent * Mathf.Clamp01(valleyConstriction01)
                            * AtmoConstants.GapWindGain;

            return axis * speedUp;
        }

        /// <summary>
        /// The geometry a point needs for its local winds, all of it terrain the world
        /// already has: the coast normal and distance, the day's land-sea contrast, the
        /// downhill direction and steepness, and the valley axis and how far it narrows.
        /// The caller fills what applies and leaves the rest zero, so a point that is
        /// neither coastal nor sloped simply gets no local wind.
        /// </summary>
        public struct LocalGeography
        {
            public Vector2 onshoreDirWorld;
            public float distanceToCoastM;
            public float landSeaTempDiffC;
            public Vector2 downhillDirWorld;
            public float slope01;
            public Vector2 alongValleyDirWorld;
            public float valleyConstriction01;
        }

        /// <summary>
        /// m/s world XZ. The three local circulations summed into the one vector to add
        /// to the synoptic surface wind. The gap term reads the ambient flow it speeds,
        /// so it is passed the surface wind the sea and slope terms will join.
        /// </summary>
        public static Vector2 ResolveWorldMPerS(LocalGeography geo,
            Vector2 ambientWorldMPerS, float latitudeDeg,
            float declinationDeg, float hourAngleDeg)
        {
            float forcing =
                DiurnalForcingSigned(latitudeDeg, declinationDeg, hourAngleDeg);

            Vector2 sea = SeaBreezeWorldMPerS(geo.onshoreDirWorld,
                geo.distanceToCoastM, geo.landSeaTempDiffC, forcing);
            Vector2 slope = SlopeWindWorldMPerS(geo.downhillDirWorld,
                geo.slope01, forcing);
            Vector2 gap = GapWindWorldMPerS(geo.alongValleyDirWorld,
                ambientWorldMPerS, geo.valleyConstriction01);

            return sea + slope + gap;
        }
    }
}
