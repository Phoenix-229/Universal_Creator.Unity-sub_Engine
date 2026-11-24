// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SplatMipSelection.cs
//  Purpose: Mip selection for the splat cache at the Nyquist limit (§ 9.7 G-43, L-50):
//           choose the mip so one cached splat texel maps to about one screen pixel.
//           At a grazing view the screen footprint of a texel is an ellipse - long
//           along the view ray, short across it - and getting this wrong fails in both
//           directions: pick the SHORT axis and the long axis is under-sampled and
//           SHIMMERS; pick nothing and the near ground over-blurs into mush. The rule:
//           select the sampling mip from the LONG axis (so the worst-sampled direction
//           is covered and cannot shimmer), let hardware anisotropic filtering resolve
//           the short axis, and budget page REQUESTS from the short axis so a near-
//           horizon view does not ask for the whole world at mip 0.
//
//  THE NYQUIST FORMULA - AND A CORRECTED DIRECTION
//  -----------------------------------------------
//  One texel ≈ one pixel means texelWorldSize · 2^mip ≈ metresPerPixel, so
//
//       mip = log2( metresPerPixel / texelWorldSizeM )        // finer near, coarser far
//
//  which is the physically correct reading: as the ground recedes metresPerPixel grows,
//  so the mip rises and the page coarsens - fewer, larger pages far away. The task's
//  boxed formula writes the ratio the other way up (texelWorldSize / metresPerPixel),
//  which would coarsen the NEAR ground and finen the FAR ground - exactly the blur-near,
//  shimmer-far failure its own prose warns against. This implements the ratio the
//  one-texel-per-pixel criterion actually gives, and the self-test pins that direction;
//  the inversion is disclosed in the task note rather than fake-passed.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Every method is a static function of numbers - texel size, metres-per-pixel, mip
//  count - so the self-test pins the Nyquist match, the monotone-with-distance rise, the
//  long-axis-is-coarser rule and the clamp without a camera or a texture. The footprint's
//  long and short metres-per-pixel come from the scene layer's projection.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure Nyquist mip selection for the splat cache: the continuous and clamped-integer mip that maps one
    /// cached texel to about one screen pixel, selected from the long footprint axis so the ground neither blurs
    /// nor shimmers. Device-free arithmetic; the anisotropy the short axis carries is reported for the sampler.
    /// </summary>
    public static class SplatMipSelection
    {
        /// <summary>
        /// The continuous (fractional) mip where one texel maps to one pixel: log2(metresPerPixel /
        /// texelWorldSizeM). Rises as the ground recedes (metresPerPixel grows), so far ground coarsens. Returns
        /// 0 on degenerate input.
        /// </summary>
        public static float SelectMipContinuous(float texelWorldSizeM, float metresPerPixel)
        {
            if (texelWorldSizeM <= 0f || metresPerPixel <= 0f) return 0f;
            return Mathf.Log(metresPerPixel / texelWorldSizeM, 2f);
        }

        /// <summary>
        /// The integer mip to sample: the floor of the continuous mip, clamped to [0, mipCount − 1]. Floor keeps
        /// at-least-this-much resolution (biased against blur); the clamp keeps it a valid level. Fails to 0.
        /// </summary>
        public static int SelectMip(float texelWorldSizeM, float metresPerPixel, int mipCount)
        {
            if (mipCount <= 0) return 0;
            float m = SelectMipContinuous(texelWorldSizeM, metresPerPixel);
            int mi = Mathf.FloorToInt(m);
            if (mi < 0) mi = 0;
            if (mi > mipCount - 1) mi = mipCount - 1;
            return mi;
        }

        /// <summary>
        /// The sampling mip for an anisotropic footprint: selected from the LONG axis (the larger metres-per-pixel),
        /// so the worst-sampled direction is covered and cannot shimmer. The short axis is left to hardware
        /// anisotropic filtering. Coarser than a short-axis selection, never finer.
        /// </summary>
        public static int SelectMipFromFootprint(float texelWorldSizeM, float metresPerPixelLong, float metresPerPixelShort, int mipCount)
        {
            return SelectMip(texelWorldSizeM, metresPerPixelLong, mipCount);
        }

        /// <summary>
        /// The anisotropy ratio of the footprint, long / short metres-per-pixel (≥ 1): the degree of anisotropic
        /// filtering the sampler must apply on the short axis once the mip is chosen from the long one. Guards a
        /// non-positive short axis to 1.
        /// </summary>
        public static float AnisotropyRatio(float metresPerPixelLong, float metresPerPixelShort)
        {
            if (metresPerPixelShort <= 0f || metresPerPixelLong <= 0f) return 1f;
            float r = metresPerPixelLong / metresPerPixelShort;
            return r < 1f ? 1f : r;
        }
    }
}
