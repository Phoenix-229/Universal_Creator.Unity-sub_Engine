// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_VirtualSplat.hlsl
//  Purpose: The GPU sampling core of the virtual splat cache. The terrain layer
//           count a lithology world wants runs past URP's eight-layer sampler
//           ceiling, so all source layers are composited ONCE into a camera-
//           following tiled atlas and the surface then samples that atlas through a
//           page table - a fixed two textures per pixel, an indirection lookup then
//           a physical-atlas fetch, no matter how many source layers fed the
//           composite. This include is that two-texture sample, plus the Nyquist mip
//           selection and page-coordinate arithmetic it needs, written to mirror the
//           CPU references so the GPU reproduces numbers a self-test already pins.
//
//  IT MIRRORS THE CPU REFERENCE
//  ----------------------------
//  The mip selection is SplatMipSelection.SelectMipContinuous /
//  SelectMipFromFootprint (mip = log2(metresPerPixel / texelWorldSize), chosen from
//  the LONG footprint axis so the far ground cannot shimmer, the short axis left to
//  hardware anisotropy), the page size is SplatPageResidency.PageWorldSizeM (base
//  tile doubled per mip) and the page coordinate is its PageCoordForWorld (floor of
//  world / page size). Getting the mip ratio the right way up - finer near, coarser
//  far - is the corrected direction the model disclosed, and it is preserved here.
//
//  TWO TEXTURES, ALWAYS
//  --------------------
//  _UC_SplatPageTable is the indirection: each texel holds a resident page's physical
//  location in the atlas (xy) and its resident mip (z), with w a valid flag. _UC_SplatAtlas
//  is the physical page store. The per-pixel cost is one page-table sample and one atlas
//  sample regardless of the source layer count - the whole justification of the cache.
// -----------------------------------------------------------------------------

#ifndef UC_VIRTUAL_SPLAT_INCLUDED
#define UC_VIRTUAL_SPLAT_INCLUDED

// The two textures the cached path samples, and the atlas geometry the indirection needs.
TEXTURE2D(_UC_SplatPageTable);  SAMPLER(sampler_UC_SplatPageTable);
TEXTURE2D(_UC_SplatAtlas);      SAMPLER(sampler_UC_SplatAtlas);

float  _UC_SplatBaseTileWorldSizeM; // world size of a mip-0 page
float  _UC_SplatTexelWorldSizeM;    // world size of one atlas texel at mip 0
float  _UC_SplatMipCount;           // number of resident mip levels
float  _UC_SplatAtlasPagesPerAxis;  // physical atlas is this many pages square
float  _UC_SplatPageTablePagesPerAxis; // page-table texture is this many pages square

// mip = log2(metresPerPixel / texelWorldSize): rises as the ground recedes, so far
// ground coarsens. Mirrors SplatMipSelection.SelectMipContinuous. 0 on degenerate input.
float UC_SplatSelectMipContinuous(float texelWorldSizeM, float metresPerPixel)
{
    if (texelWorldSizeM <= 0.0 || metresPerPixel <= 0.0) return 0.0;
    return log2(metresPerPixel / texelWorldSizeM);
}

// The integer sampling mip: floor of the continuous mip, clamped to [0, mipCount-1].
// Mirrors SplatMipSelection.SelectMip.
float UC_SplatSelectMip(float texelWorldSizeM, float metresPerPixel, float mipCount)
{
    if (mipCount <= 0.0) return 0.0;
    float m = floor(UC_SplatSelectMipContinuous(texelWorldSizeM, metresPerPixel));
    return clamp(m, 0.0, mipCount - 1.0);
}

// Selected from the LONG footprint axis (the larger metres-per-pixel) so the worst-
// sampled direction is covered and cannot shimmer. Mirrors SelectMipFromFootprint.
float UC_SplatSelectMipFromFootprint(float texelWorldSizeM, float mppLong, float mppShort, float mipCount)
{
    return UC_SplatSelectMip(texelWorldSizeM, mppLong, mipCount);
}

// Long / short metres-per-pixel (>= 1): the anisotropy the sampler applies on the short
// axis once the mip is chosen from the long one. Mirrors SplatMipSelection.AnisotropyRatio.
float UC_SplatAnisotropy(float mppLong, float mppShort)
{
    if (mppShort <= 0.0 || mppLong <= 0.0) return 1.0;
    float r = mppLong / mppShort;
    return r < 1.0 ? 1.0 : r;
}

// The world size of a page at a mip: base tile doubled per level. SplatPageResidency.PageWorldSizeM.
float UC_SplatPageWorldSizeM(float baseTileWorldSizeM, float mip)
{
    if (baseTileWorldSizeM <= 0.0 || mip < 0.0) return 0.0;
    return baseTileWorldSizeM * exp2(mip);
}

// The page index a world position falls in: floor(world / pageSize). SplatPageResidency.PageCoordForWorld.
float2 UC_SplatPageCoord(float2 worldXZ, float pageWorldSizeM)
{
    if (pageWorldSizeM <= 0.0) return float2(0.0, 0.0);
    return floor(worldXZ / pageWorldSizeM);
}

// The cached ground sample: choose the mip from the footprint, find the page, read the
// page table for its physical atlas location, then fetch the atlas at the fragment's
// offset within that page. Two texture samples, whatever the source layer count.
half4 UC_SampleVirtualSplat(float2 worldXZ, float mppLong, float mppShort)
{
    float mip  = UC_SplatSelectMipFromFootprint(_UC_SplatTexelWorldSizeM, mppLong, mppShort, _UC_SplatMipCount);
    float size = UC_SplatPageWorldSizeM(_UC_SplatBaseTileWorldSizeM, mip);
    float2 page = UC_SplatPageCoord(worldXZ, size);

    // Fraction of the way across the page this fragment sits (its texel within the page).
    float2 inPage = (size > 0.0) ? frac(worldXZ / size) : float2(0.0, 0.0);

    // Indirection: sample the page table for this page's physical location + validity.
    float ptAxis = max(1.0, _UC_SplatPageTablePagesPerAxis);
    float2 ptUV  = (page + 0.5) / ptAxis;
    half4  entry = SAMPLE_TEXTURE2D_LOD(_UC_SplatPageTable, sampler_UC_SplatPageTable, frac(ptUV), 0);

    // entry.xy = physical page (normalised atlas coords), entry.w = valid flag.
    float aAxis = max(1.0, _UC_SplatAtlasPagesPerAxis);
    float2 atlasUV = entry.xy + inPage / aAxis;
    half4  ground  = SAMPLE_TEXTURE2D(_UC_SplatAtlas, sampler_UC_SplatAtlas, atlasUV);

    // An invalid (not-yet-resident) page reads as zero rather than sampling garbage.
    return entry.w > 0.5 ? ground : half4(0.0, 0.0, 0.0, 0.0);
}

#endif // UC_VIRTUAL_SPLAT_INCLUDED
