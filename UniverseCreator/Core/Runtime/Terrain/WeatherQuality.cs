// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WeatherQuality.cs
//  Purpose: Turns one recommended quality tier into the concrete weather settings
//           for that tier - how many precipitation emitters, how opaque the sheet,
//           which fog, whether cloud shadows and volumetrics run, and how much of
//           a storm's lightning is drawn.
//
//  ONE TIER SOURCE, NOT TWO
//  ------------------------
//  The tier itself is NOT decided here. The shipped AdaptiveQuality samples frame
//  time and recommends a QualityTier; this reads that enum (plus whether the build
//  is a mobile one) and maps it to a settings bundle. Adding a second weather-only
//  tier enum is exactly what this file exists to avoid - two ladders would drift and
//  a world would end up scaling its grass on one and its rain on another.
//
//  WHY IT LIVES IN TerrainSystem
//  -----------------------------
//  The table is a page of real numbers (2..12 emitters, 0.6..1.0 opacity). The
//  Weather folder forbids bare literals so its physics cannot hide a constant;
//  TerrainSystem is exempt and already reads the Optimization tier, so the mapping
//  sits here and the emitters and passes read the bundle it returns.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>How much fog machinery a tier runs.</summary>
    public enum WeatherFogQuality { HeightOnly, HeightPlusMechanism, Full }

    /// <summary>Whether and how sharply clouds cast a moving shadow.</summary>
    public enum WeatherCloudShadowQuality { Off, ProjectedLowRes, Projected, ProjectedHighRes }

    /// <summary>How much of a storm's lightning is presented.</summary>
    public enum WeatherLightningQuality { FlashOnly, FlashAndThunder, Full }

    /// <summary>The concrete weather budget for one tier on one device class.</summary>
    public struct WeatherQuality
    {
        /// <summary>Simultaneous precipitation emitters this tier allows.</summary>
        public int maxEmitters;

        /// <summary>0-1 opacity of the precipitation sheet.</summary>
        public float precipOpacity;

        public WeatherFogQuality fog;
        public WeatherCloudShadowQuality cloudShadows;

        /// <summary>Volumetric lighting. Desktop High leaves it optional (off by default); Ultra turns it on.</summary>
        public bool volumetric;

        public WeatherLightningQuality lightning;

        private static WeatherQuality Make(
            int maxEmitters, float precipOpacity, WeatherFogQuality fog,
            WeatherCloudShadowQuality cloudShadows, bool volumetric,
            WeatherLightningQuality lightning)
        {
            WeatherQuality q = new WeatherQuality();
            q.maxEmitters = maxEmitters;
            q.precipOpacity = precipOpacity;
            q.fog = fog;
            q.cloudShadows = cloudShadows;
            q.volumetric = volumetric;
            q.lightning = lightning;
            return q;
        }

        /// <summary>
        /// The settings for one recommended tier on one device class. Mobile tops out at
        /// the flagship row, so an Ultra recommendation on a phone still runs the Mobile
        /// High budget rather than a desktop one; on desktop only Ultra unlocks volumetrics.
        /// Every one of the five table rows is reachable by choosing the tier and class.
        /// </summary>
        public static WeatherQuality For(AdaptiveQuality.QualityTier tier, bool mobile)
        {
            if (mobile)
            {
                switch (tier)
                {
                    case AdaptiveQuality.QualityTier.Low:
                        return Make(2, 0.6f, WeatherFogQuality.HeightOnly,
                                    WeatherCloudShadowQuality.Off, false,
                                    WeatherLightningQuality.FlashOnly);
                    case AdaptiveQuality.QualityTier.Medium:
                        return Make(4, 0.8f, WeatherFogQuality.HeightPlusMechanism,
                                    WeatherCloudShadowQuality.ProjectedLowRes, false,
                                    WeatherLightningQuality.FlashAndThunder);
                    default: // High and Ultra both land on the flagship mobile row.
                        return Make(6, 0.9f, WeatherFogQuality.Full,
                                    WeatherCloudShadowQuality.Projected, false,
                                    WeatherLightningQuality.Full);
                }
            }

            if (tier == AdaptiveQuality.QualityTier.Ultra)
                return Make(12, 1.0f, WeatherFogQuality.Full,
                            WeatherCloudShadowQuality.ProjectedHighRes, true,
                            WeatherLightningQuality.Full);

            // Every other desktop recommendation runs the Desktop High row.
            return Make(10, 1.0f, WeatherFogQuality.Full,
                        WeatherCloudShadowQuality.ProjectedHighRes, false,
                        WeatherLightningQuality.Full);
        }

        /// <summary>
        /// The live bundle for a scene's AdaptiveQuality, or the flagship desktop row when
        /// none is present. The mobile axis follows the running platform, which is what an
        /// automatic choice should track; an author overrides by calling For directly.
        /// </summary>
        public static WeatherQuality Current(AdaptiveQuality source)
        {
            AdaptiveQuality.QualityTier tier = source != null
                ? source.CurrentTier
                : AdaptiveQuality.QualityTier.High;

            return For(tier, Application.isMobilePlatform);
        }
    }
}
