// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SplatCacheModel.cs
//  Purpose: The layer-count budget that states WHY the virtual splat cache exists,
//           in numbers a self-test can pin (§ 9.7 G-41). URP's terrain shader samples
//           four layers per pass, eight with _TERRAIN_8_LAYERS, and each group of four
//           layers costs another full-resolution alphamap in TerrainData - so the
//           per-pixel sampling cost and the texture-memory cost BOTH grow with the
//           layer count, and eight is a hard ceiling on the sampler side. A world that
//           wants bedrock, talus, alluvium, sand, gravel, three soils, grass, moss,
//           litter and snow - which § 9.4 lithology and § 9.5 cover directly imply -
//           is at twelve layers, past that ceiling on every axis.
//
//  WHAT THE CACHE CHANGES
//  ----------------------
//  The cache composites all source layers ONCE into a camera-following tiled atlas, and
//  the terrain shader then samples the ATLAS through a page table - a FIXED two textures
//  (indirection + physical atlas) per pixel, no matter how many source layers fed the
//  composite. So the per-pixel sampling cost stops growing with the layer count, and the
//  achievable layer count is bounded only by the compositing budget per tier, not by the
//  8-layer sampler limit. That is the whole justification, and it is arithmetic: without
//  the cache, sampled textures = ceil(layers/4) alphamaps and layers is capped at 8; with
//  it, sampled textures = 2 constant and the layer count runs to the tier's composite
//  budget (Mobile 8, Desktop 16, Ultra 24 in this design).
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Every figure is a static function of the layer count and tier, so the self-test
//  reproduces the manual's per-tier layer table exactly - the done-when is that the
//  manual states the achievable layer count per tier and why, and these are the numbers
//  it states. The actual atlas composite and page-table sample are the scene/shader
//  layer System D builds on top.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>The quality tiers the splat-cache layer budget is stated for, mirroring the impostor budget tiers.</summary>
    public enum SplatCacheTier
    {
        MobileLow = 0,
        MobileHigh = 1,
        Desktop = 2,
        Ultra = 3
    }

    /// <summary>
    /// Pure arithmetic of the splat-layer budget: the sampler and memory cost of layers WITHOUT the cache versus
    /// the fixed cost WITH it, and the layer count each tier's composite budget affords. The numbers the § 9.7
    /// manual paragraph states. No Texture, no camera, no allocation.
    /// </summary>
    public static class SplatCacheModel
    {
        /// <summary>URP terrain samples four layers per pass by default.</summary>
        public const int LayersPerPass = 4;

        /// <summary>The hard ceiling on layers URP's terrain shader can sample, with _TERRAIN_8_LAYERS enabled.</summary>
        public const int MaxLayersWithoutCache = 8;

        /// <summary>Textures the cached path samples per pixel: the indirection page table plus the physical atlas.
        /// Constant - it does NOT grow with the source layer count, which is the point of the cache.</summary>
        public const int SampledTexturesWithCache = 2;

        /// <summary>
        /// Alphamap textures a layer count costs WITHOUT the cache: ceil(layers / 4), since each group of four
        /// layers needs another full-resolution alphamap in TerrainData. Grows with the layer count. Fails to 0.
        /// </summary>
        public static int AlphamapsForLayers(int layerCount)
        {
            if (layerCount <= 0) return 0;
            return (layerCount + LayersPerPass - 1) / LayersPerPass;
        }

        /// <summary>Whether a layer count is past URP's un-cached ceiling and therefore needs the cache to render
        /// at all. True above eight layers.</summary>
        public static bool ExceedsUncachedLimit(int layerCount)
        {
            return layerCount > MaxLayersWithoutCache;
        }

        /// <summary>
        /// The layer count a tier's composite budget affords WITH the cache - bounded by compositing, not by the
        /// sampler limit, so Desktop and Ultra run well past eight. These are the figures the manual states.
        /// </summary>
        public static int MaxLayersWithCache(SplatCacheTier tier)
        {
            switch (tier)
            {
                case SplatCacheTier.MobileLow: return 8;
                case SplatCacheTier.MobileHigh: return 10;
                case SplatCacheTier.Desktop: return 16;
                case SplatCacheTier.Ultra: return 24;
                default: return MaxLayersWithoutCache;
            }
        }
    }
}
