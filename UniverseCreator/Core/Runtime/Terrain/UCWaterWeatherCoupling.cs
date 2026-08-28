// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    UCWaterWeatherCoupling.cs
//  Purpose: The one wire from the weather to the shipped interactive water. The
//           ripple simulation already knows how to stipple a surface with rain -
//           it has a full drop-emission path - it was simply never told when it
//           is raining. This hands it the precipitation rate, so a downpour
//           dimples every lake and the sea, through the same impulse path a boat
//           wake uses, which is what lets rain and wake interfere instead of one
//           drawing over the other.
//
//  WHY THE WIND HALF IS NOT HERE
//  -----------------------------
//  Wind chop and whitecaps already rise with the wind: the water shader scales
//  both by the wind strength UCWindManager publishes, and the weather feeds that
//  manager through SetWeatherWind. There is nothing to add for wind without
//  duplicating a chain that already runs end to end - so this file drives only
//  the rain, and the wind response is left where it already lives.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  This is the terrain/water side reaching UP to the weather's numbers, which is
//  the allowed direction. Weather never reaches down here. Precipitation arrives
//  as a plain scalar; nothing in the weather layer references this class.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Drives the interactive water simulation's rain from the weather's
    /// precipitation rate. Every method is safe to call when no simulation exists.
    /// </summary>
    public static class UCWaterWeatherCoupling
    {
        /// <summary>mm/h that counts as the reference downpour the drop density is tuned to.</summary>
        private const float RainReferenceMmPerHour = 10f;

        /// <summary>
        /// Drops per second per square kilometre of water at the reference rain. The
        /// window's own area scales this to a drop count, so a wide window and a
        /// narrow one stipple to the same density rather than the same total.
        /// </summary>
        private const float RainDropsPerSecondPerKm2 = 2500f;

        /// <summary>Square metres in a square kilometre; the window area is measured in metres.</summary>
        private const float SquareMetresPerSquareKilometre = 1000000f;

        /// <summary>Surface temperature, in Celsius, at which still water begins to skin over.</summary>
        private const float IceOnsetTempC = 0f;

        /// <summary>
        /// Degrees below the onset over which a still surface goes from open water to
        /// a locked sheet. The skin forms at the onset and thickens as the cold deepens,
        /// so the fraction ramps in rather than snapping.
        /// </summary>
        private const float IceFormBandC = 4f;

        /// <summary>
        /// Drops per second a window of the given side should emit under a given rain
        /// rate: linear in the rain and in the window's area, zero when it is dry.
        /// </summary>
        public static float RainDropsPerSecond(float precipMmPerHour, float windowSizeMetres)
        {
            float rain = Mathf.Max(0f, precipMmPerHour);
            float side = Mathf.Max(0f, windowSizeMetres);
            float areaKm2 = side * side / SquareMetresPerSquareKilometre;

            return rain / RainReferenceMmPerHour * RainDropsPerSecondPerKm2 * areaKm2;
        }

        /// <summary>
        /// Tells the active water simulation how hard it is raining. A no-op when no
        /// simulation is in the scene, so the weather driver can call it every frame
        /// without holding a reference to the water system or null-checking it.
        /// </summary>
        public static void DriveRain(float precipMmPerHour)
        {
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim == null) return;

            sim.rainDropsPerSecond = RainDropsPerSecond(precipMmPerHour, sim.worldSize);
        }

        /// <summary>
        /// Stops the rain on the water. Called when precipitation ceases or the
        /// weather world goes away, so a surface does not keep dimpling under a dry sky.
        /// </summary>
        public static void Clear()
        {
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim == null) return;

            sim.rainDropsPerSecond = 0f;
        }

        /// <summary>
        /// How frozen a still surface at this temperature is: open above the onset,
        /// fully iced once the cold is a few degrees past it, ramping between. Warming
        /// back through the band melts it out along the same curve.
        /// </summary>
        public static float IceCoverFraction01(float surfaceTempC)
        {
            return Mathf.Clamp01((IceOnsetTempC - surfaceTempC) / IceFormBandC);
        }

        /// <summary>
        /// Whether a body of this kind can freeze at all. Only still water skins over:
        /// a lake or a pool locks in the cold, while a river's current and the sea's
        /// swell and salt keep them open beside it however hard it freezes.
        /// </summary>
        public static bool FreezesWhenStill(WaterBodyKind kind)
        {
            return kind == WaterBodyKind.Lake || kind == WaterBodyKind.Pool;
        }

        /// <summary>
        /// The ice cover a body of this kind carries at this surface temperature: the
        /// freeze fraction on still water, and always zero on moving or open water, so
        /// one call decides both whether a body freezes and how far.
        /// </summary>
        public static float IceCoverFor(WaterBodyKind kind, float surfaceTempC)
        {
            return FreezesWhenStill(kind) ? IceCoverFraction01(surfaceTempC) : 0f;
        }
    }
}
