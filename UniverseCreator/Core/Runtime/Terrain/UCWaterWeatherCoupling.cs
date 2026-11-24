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
using UniverseCreator.Weather;

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
        /// How much taller than a calm swell a full storm drives the surface. One is
        /// the calm baseline the shader and the buoyancy solve already use; a full gale
        /// multiplies the swell by StormAmpCalm + StormAmpGain. Capped at StormAmpMax so
        /// a runaway weather value cannot displace the mesh so far it tears through the
        /// bank or drowns the shore - a storm sea is bigger, not unbounded.
        /// </summary>
        private const float StormAmpCalm = 1f;
        private const float StormAmpGain = 2f;   // calm 1 -> full storm 3x swell
        private const float StormAmpMax = 3f;

        /// <summary>
        /// The clearest weather can never make water; the murk floor a full storm drives
        /// underwater visibility down to, as a fraction of the body's own clear range. A
        /// storm stirs the bed and suspends sediment, so the diver sees a third as far -
        /// it does not black out, and it never clears PAST what the author set.
        /// </summary>
        private const float MurkVisibilityFloor01 = 0.3f;

        /// <summary>
        /// Rain rate, mm/h, that counts as the heaviest downpour for murk. At or above it
        /// the rain alone drives visibility to the floor; below it the murk eases off with
        /// the rain. Storm activity and rain both stir the water, so the stronger of the
        /// two wins rather than the two compounding past the floor.
        /// </summary>
        private const float MurkHeavyRainMmPerHour = 25f;

        // Cached shader property ids for the per-material weather uniforms, and one reused
        // property block so driving every body each frame allocates nothing. A block
        // rather than the shared material: the shared material is the ASSET, and writing
        // it would mutate it on disk in edit mode and change every body that shares it at
        // once - the block is per-renderer, per-frame, and leaves the asset untouched.
        private static readonly int IdWaveStormAmp = Shader.PropertyToID(UCWaterProps.WaveStormAmp);
        private static readonly int IdIce = Shader.PropertyToID(UCWaterProps.Ice);
        private static MaterialPropertyBlock s_block;

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

        /// <summary>
        /// The swell multiplier a body carries under this much storm: calm at rest,
        /// rising with the storm to a capped maximum. This is the one number that must
        /// reach both the shader (<c>_WaveStormAmp</c>, what you see) and the buoyancy
        /// solve (<c>surface.stormAmp</c>, what floats on it), so it lives here as one
        /// function and both are set from it - a boat and the wave under it cannot then
        /// disagree about how big the sea is.
        /// </summary>
        public static float StormAmpFor(float stormActivity01)
        {
            float s = Mathf.Clamp01(stormActivity01);
            return Mathf.Min(StormAmpMax, StormAmpCalm + StormAmpGain * s);
        }

        /// <summary>
        /// The murk multiplier the weather drives underwater visibility by: 1 in clear
        /// weather, easing toward the floor as a storm or heavy rain stirs the water. The
        /// stronger of storm and rain wins rather than the two compounding, because both
        /// are the same physical cause - a churned surface suspending sediment - and a
        /// stormy downpour is not murkier than the sum of a storm and a downpour.
        /// </summary>
        public static float MurkVisibilityScaleFor(float stormActivity01, float precipMmPerHour)
        {
            float rain = Mathf.Clamp01(Mathf.Max(0f, precipMmPerHour) / MurkHeavyRainMmPerHour);
            float stir = Mathf.Clamp01(Mathf.Max(Mathf.Clamp01(stormActivity01), rain));
            return Mathf.Lerp(1f, MurkVisibilityFloor01, stir);
        }

        /// <summary>
        /// Tells every underwater view in the scene how murky the water is right now.
        /// A scene-wide static, because murk is a property of the sky over the world and
        /// not of one camera; the view multiplies it onto its own authored silt dial.
        /// </summary>
        public static void DriveMurk(float stormActivity01, float precipMmPerHour)
        {
            UCUnderwaterView.WeatherVisibilityScale =
                MurkVisibilityScaleFor(stormActivity01, precipMmPerHour);
        }

        /// <summary>
        /// The one per-frame wire from a resolved weather snapshot to every drop of
        /// water in the world: rain stipples the surface, the storm raises the swell on
        /// the shader AND under the buoyancy solve, a hard freeze skins still bodies over,
        /// and the murk closes the underwater view down. Owns no weather math - it reads
        /// the finished snapshot the same resolver the sky and the ground read, and only
        /// applies it - and it is safe to call every frame whether or not any water,
        /// simulation or view exists.
        /// </summary>
        public static void Apply(in WeatherState weather)
        {
            float precip = weather.precipitationMmPerHour;
            float stormAmp = StormAmpFor(weather.stormActivity);
            float tempC = weather.temperature;

            // Rain and murk are scene-wide singles - the simulation and the views - so they
            // are driven once, outside the per-body loop.
            DriveRain(precip);
            DriveMurk(weather.stormActivity, precip);

            // Storm swell and ice are per body: the swell rides every open surface, while
            // only still fresh water (lake, pool) skins over, so the freeze fraction is
            // decided per kind. One walk of the registry sets both.
            System.Collections.Generic.IReadOnlyList<UCWaterVolume> all = UCWaterVolume.All;
            for (int i = 0; i < all.Count; i++)
            {
                UCWaterVolume v = all[i];
                if (v == null) continue;

                float ice = IceCoverFor((WaterBodyKind)(int)v.kind, tempC);
                PushBodyWeather(v, stormAmp, ice);
            }
        }

        // UC-T08-05: production adapters sample each registered body's location and filter the existing world ownership.
        // The legacy explicit scene-wide Apply(in WeatherState) remains available for standalone callers and fixtures.
        internal static void Apply(UniverseCreatorObject world, UCWeatherSystemManager weather)
        {
            if (weather != null && !ReferenceEquals(weather.GetComponentInParent<UniverseCreatorObject>(true), world))
                throw new System.ArgumentException("Water weather owner belongs to another world.", nameof(weather));
            var all = UCWaterVolume.All;
            for (int i = 0; i < all.Count; i++)
            {
                UCWaterVolume volume = all[i];
                if (volume == null || !ReferenceEquals(volume.WorldOwner, world)) continue;
                WeatherState local = weather != null ? weather.SampleEnvironmentAt(volume.transform.position).Weather : UCWeatherSystemManager.DefaultWeather();
                PushBodyWeather(volume, StormAmpFor(local.stormActivity), IceCoverFor((WaterBodyKind)(int)volume.kind, local.temperature));
            }
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim != null && ReferenceEquals(sim.GetComponentInParent<UniverseCreatorObject>(true), world))
            {
                float rain = weather != null ? weather.SampleEnvironmentAt(sim.transform.position).Weather.precipitationMmPerHour : 0;
                sim.rainDropsPerSecond = RainDropsPerSecond(rain, sim.worldSize);
            }
        }

        internal static void Calm(UniverseCreatorObject world)
        {
            var all = UCWaterVolume.All;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && ReferenceEquals(all[i].WorldOwner, world)) PushBodyWeather(all[i], StormAmpCalm, 0);
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim != null && ReferenceEquals(sim.GetComponentInParent<UniverseCreatorObject>(true), world)) sim.rainDropsPerSecond = 0;
        }
        /// <summary>
        /// Returns the whole world's water to calm: no rain, no storm swell, no ice, and
        /// a clear underwater view. Called when the weather world goes away, so a surface
        /// does not keep storming under a driver that has stopped feeding it.
        /// </summary>
        public static void Calm()
        {
            Clear();
            UCUnderwaterView.WeatherVisibilityScale = 1f;

            System.Collections.Generic.IReadOnlyList<UCWaterVolume> all = UCWaterVolume.All;
            for (int i = 0; i < all.Count; i++)
            {
                UCWaterVolume v = all[i];
                if (v == null) continue;
                PushBodyWeather(v, StormAmpCalm, 0f);
            }
        }

        /// <summary>
        /// Writes one body's storm swell and ice cover to both places they must agree:
        /// <c>surface.stormAmp</c>, which the CPU buoyancy solve reads, and the render
        /// material's <c>_WaveStormAmp</c> and <c>_UCIce</c>, which the surface shader
        /// reads - pushed through a per-renderer property block so the shared material
        /// asset is never touched. A body with no renderer (not yet built) still gets its
        /// buoyancy value, so a boat floats correctly the frame before the mesh appears.
        /// </summary>
        private static void PushBodyWeather(UCWaterVolume v, float stormAmp, float ice)
        {
            // Buoyancy source of truth. surface is a field, so this assignment lands on
            // the volume; UCWaterSurface.WaveOffset reads it the same frame.
            v.surface.stormAmp = stormAmp;

            Renderer r = v.GetComponent<Renderer>();
            if (r == null) return;

            if (s_block == null) s_block = new MaterialPropertyBlock();
            r.GetPropertyBlock(s_block);
            s_block.SetFloat(IdWaveStormAmp, stormAmp);
            s_block.SetFloat(IdIce, ice);
            r.SetPropertyBlock(s_block);
        }
    }
}
