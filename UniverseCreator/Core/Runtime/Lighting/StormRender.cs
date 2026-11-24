// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    StormRender.cs
//  Purpose: The two things a cumulonimbus needs that no other deck does: a silhouette
//           that towers, darkens at the base and brightens on its sunlit flank, and a
//           lightning flash that floods the whole sky - and the ground under it - for
//           a fraction of a second. Both are derived here so the storm is drawn from
//           the same one cloud sheet as every other deck, only shaped harder, and so
//           the flash is a photometric spike the exposure model can be shown to lag
//           rather than a painted white frame.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The cumulonimbus-only draw terms and the lightning flash, both pure. The storm
    /// silhouette rises with its coverage and carries the enormous lit-to-shaded
    /// contrast of a real storm; the flash adds a brief daylight-order illuminance to
    /// the scene's key, which - because it is far shorter than eye adaptation - lights
    /// the ground while the exposure stays dark-adapted around it.
    /// </summary>
    public static class StormRender
    {
        /// <summary>
        /// How far the storm's silhouette rises above its base altitude, scaled by
        /// coverage: a small cell barely towers, a full storm reaches for the
        /// tropopause. Capped at the real tower extent so it never runs away.
        /// </summary>
        public static float TowerExtentM(float coverage01)
        {
            return LightingConstants.CloudCumulonimbusTowerExtentM * Mathf.Clamp01(coverage01);
        }

        /// <summary>
        /// The near-black storm base, greened toward the hail tint when the cell is
        /// carrying heavy hail - the light that survives a deep wet-ice column. Without
        /// hail it is a neutral dark grey at the base-darkness value.
        /// </summary>
        public static Color BaseColor(bool heavyHail)
        {
            float d = LightingConstants.CloudCumulonimbusBaseDarkness01;
            Color neutral = new Color(d, d, d);
            return heavyHail ? Color.Lerp(neutral, LightingConstants.CloudHailBaseTint, 0.8f) : neutral;
        }

        /// <summary>The sunlit-flank gain, the bright end of the storm's contrast (&gt; 1).</summary>
        public static float FlankBrightness
        {
            get { return LightingConstants.CloudCumulonimbusFlankBrightness; }
        }

        /// <summary>
        /// The scene's key illuminance during a lightning stroke: the ambient key plus
        /// the flash peak scaled by how bright this stroke is. Because it is added to the
        /// key - the term that lights the terrain - a stroke lights the ground, not only
        /// the sky. Flash01 outside 0..1 is clamped.
        /// </summary>
        public static float FlashKeyLux(float ambientKeyLux, float flash01)
        {
            return ambientKeyLux + LightingConstants.CloudLightningPeakLux * Mathf.Clamp01(flash01);
        }

        /// <summary>
        /// The exposure EV after a full flash has been present for its whole brief
        /// duration, adapting from the pre-flash EV toward the flash's own EV at the
        /// module's rates. The point is what it is <em>not</em>: over so short a window
        /// the rate-limited adaptation barely moves, so the flash stays far above the
        /// exposed white point and reads as a flash rather than a compensated brightening.
        /// </summary>
        public static float ExposureEvDuringFlash(float preFlashEv, float flashEv)
        {
            return ExposureModel.AdaptEv100(preFlashEv, flashEv, LightingConstants.CloudLightningFlashSeconds);
        }
    }
}
