// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    BasemapConsistency.cs
//  Purpose: Keep the basemap and the splat cache telling the same story (§ 9.7 G-47).
//           Past `basemapDistance` Unity stops drawing the composited layers and draws the
//           low-resolution basemap instead. If that basemap is generated from different
//           rules than the cache, crossing the distance changes the ground's colour - and
//           because G-05 derives `basemapDistance` from the camera, the boundary MOVES WITH
//           THE PLAYER, so the world appears to change colour underfoot as they walk. The
//           fix is not to tune the two to look similar but to make them the SAME source:
//           the basemap is a straight area-average downsample of the very composite the
//           cache samples, regenerated whenever that composite changes.
//
//  WHY AVERAGING IS THE WHOLE TRICK
//  --------------------------------
//  A basemap texel covers many composite texels; its colour is their mean. Averaging is
//  energy-conserving and unbiased, so the low-res basemap has no systematic colour offset
//  from the high-res composite - and over a locally uniform patch the mean is exactly the
//  patch colour, so the boundary is invisible. Any OTHER basemap rule (a separate colour
//  ramp, a different layer set) introduces an offset the moving boundary then reveals.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Colours and weights in, a colour out; no Texture, no Terrain, no GPU. The self-test
//  pins that a uniform patch downsamples to itself (no shift), that the downsample is the
//  mean (unbiased), that the composite albedo is the weight·layer sum both sides share,
//  and that a changed composite version marks the basemap stale so it is regenerated. The
//  actual basemap texture build and the `basemapDistance` draw are the scene layer above.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure basemap/cache consistency arithmetic: the composite albedo both the cache and the basemap are built
    /// from, the area-average downsample that turns that composite into the basemap, and the staleness test that
    /// forces a regenerate when the composite changes. Device-free; keeps the two representations bit-consistent.
    /// </summary>
    public static class BasemapConsistency
    {
        /// <summary>
        /// The ground albedo at a texel: the weight·layer-albedo sum over the composited layers. This is the single
        /// source both the splat cache and the basemap read, so they cannot disagree. Returns black on bad input.
        /// </summary>
        public static Color CompositeAlbedo(float[] weights, Color[] layerAlbedos)
        {
            if (weights == null || layerAlbedos == null) return Color.black;
            int n = weights.Length < layerAlbedos.Length ? weights.Length : layerAlbedos.Length;
            Color c = Color.black;
            for (int i = 0; i < n; i++) c += layerAlbedos[i] * weights[i];
            return c;
        }

        /// <summary>
        /// The basemap texel: the unweighted mean of the composite colours it covers. Energy-conserving and
        /// unbiased, so a uniform patch downsamples to itself (no colour shift at the boundary) and a varying patch
        /// to its true average. Returns black on an empty or null block.
        /// </summary>
        public static Color DownsampleAverage(Color[] block)
        {
            if (block == null || block.Length == 0) return Color.black;
            Color sum = Color.black;
            for (int i = 0; i < block.Length; i++) sum += block[i];
            return sum / block.Length;
        }

        /// <summary>
        /// Whether the basemap must be regenerated: true when its source composite version no longer matches the
        /// current one. Regenerating on every composite change is what keeps the two telling the same story - a
        /// stale basemap is exactly the moving-boundary colour shift this task exists to prevent.
        /// </summary>
        public static bool NeedsRegen(int compositeVersion, int basemapSourceVersion)
            => compositeVersion != basemapSourceVersion;
    }
}
