// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WaterQuality.cs
//  Purpose: Turns one recommended quality tier into the concrete water settings for
//           that tier - how much of the interactive ripple system runs, how many
//           ambient wave trains the surface sums, and whether the underwater caustics
//           and god-rays are drawn. It is the single per-tier table for water, the
//           exact analogue of WeatherQuality for the sky and LightingTier for light.
//
//  ONE TIER SOURCE, NOT TWO
//  ------------------------
//  The tier itself is NOT decided here. The shipped AdaptiveQuality samples frame
//  time and recommends a QualityTier; this reads that enum (plus whether the build is
//  a mobile one) and maps it to a settings bundle. A second, water-only tier ladder is
//  exactly what this file exists to avoid - two ladders would drift and a world would
//  end up scaling its grass on one and its sea on another.
//
//  DEGRADE, NEVER DROP
//  -------------------
//  Every tier still has interactive water, ambient swell and an underwater medium. A
//  lower tier THINS each - the wave solve becomes a stamped decal, the ocean's four
//  wave trains become two, the analytic caustics switch off - it never removes the
//  feature outright. The lowest mobile row is still a complete, shippable water world.
//
//  PURE DATA
//  ---------
//  For() is static and side-effect free: the same (tier, mobile) pair always yields
//  the same bundle, so the driver and the self-test never disagree about what a tier
//  means. The driver (UCWaterSystemManager.QualityDriver) is the only thing that reads
//  a bundle and APPLIES it; this file owns no Unity objects and touches no material.
// -----------------------------------------------------------------------------

using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>The concrete water budget for one tier on one device class.</summary>
    public struct WaterQuality
    {
        /// <summary>How much of the interactive ripple system this tier runs (full wave
        /// solve, halved-resolution solve, stamped decal, or nothing).</summary>
        public UCWaterRippleSim.RippleTier ripple;

        /// <summary>Ceiling on the ambient wave trains a surface sums, 1..4. Capped
        /// against each body's authored count, so a lake (2) is never raised, only the
        /// ocean's broad spectrum (4) is trimmed on lower tiers.</summary>
        public float waveOctaveCeiling;

        /// <summary>Multiplier on the underwater caustics and god-rays: 1 draws the
        /// authored high-tier intensities, 0 switches both off (the depth-darkening
        /// carries the look). Never scales anything the author did not already enable.</summary>
        public float underwaterEffectScale;

        private static WaterQuality Make(
            UCWaterRippleSim.RippleTier ripple, float waveOctaveCeiling, float underwaterEffectScale)
        {
            WaterQuality q = new WaterQuality();
            q.ripple = ripple;
            q.waveOctaveCeiling = waveOctaveCeiling;
            q.underwaterEffectScale = underwaterEffectScale;
            return q;
        }

        /// <summary>
        /// The settings for one recommended tier on one device class. Mobile tops out at
        /// a flagship-phone row that still keeps the analytic caustics but a smaller swell
        /// spectrum than a desktop; desktop High and Ultra run the full solve, four wave
        /// trains and the extras. Every row is reachable by choosing the tier and class.
        /// </summary>
        public static WaterQuality For(AdaptiveQuality.QualityTier tier, bool mobile)
        {
            if (mobile)
            {
                switch (tier)
                {
                    case AdaptiveQuality.QualityTier.Low:
                        // Cheapest complete water: stamped ring decal, a single wave train,
                        // no caustics. Still interactive, still swells, still an underwater view.
                        return Make(UCWaterRippleSim.RippleTier.Low, 1f, 0f);
                    case AdaptiveQuality.QualityTier.Medium:
                        // Mid-range phone: the real wave solve at a quarter of the texels
                        // (RippleTier.Mid halves the grid edge), two wave trains, no extras.
                        return Make(UCWaterRippleSim.RippleTier.Mid, 2f, 0f);
                    default: // High and Ultra both land on the flagship-phone row.
                        // Full-resolution solve and three wave trains; the analytic caustics
                        // and god-rays are a handful of maths ops a flagship phone can carry.
                        return Make(UCWaterRippleSim.RippleTier.High, 3f, 1f);
                }
            }

            switch (tier)
            {
                case AdaptiveQuality.QualityTier.Low:
                    // Low desktop / weak GPU: decal fallback, but a richer swell than a
                    // phone can afford and still no per-pixel caustics.
                    return Make(UCWaterRippleSim.RippleTier.Low, 2f, 0f);
                case AdaptiveQuality.QualityTier.Medium:
                    // Mid desktop: halved-resolution wave solve, three trains, no extras.
                    return Make(UCWaterRippleSim.RippleTier.Mid, 3f, 0f);
                default:
                    // High and Ultra desktop: the full solve, the full four-train ocean
                    // spectrum and the caustics + god-rays.
                    return Make(UCWaterRippleSim.RippleTier.High, 4f, 1f);
            }
        }

        /// <summary>
        /// The live bundle for a scene's AdaptiveQuality, or the flagship desktop row when
        /// none is present. The mobile axis follows the running platform, which is what an
        /// automatic choice should track; an author overrides by calling For directly.
        /// </summary>
        public static WaterQuality Current(AdaptiveQuality source)
        {
            AdaptiveQuality.QualityTier tier = source != null
                ? source.CurrentTier
                : AdaptiveQuality.QualityTier.High;

            return For(tier, UnityEngine.Application.isMobilePlatform);
        }
    }
}
