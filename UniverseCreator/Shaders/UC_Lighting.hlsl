// -----------------------------------------------------------------------------
//  Universe Creator - shared atmospheric extinction.
//
//  One definition of "how much light survives the beam" on the GPU side, included
//  by every shader that thins light through air: the sky, the sun disc, aerial
//  perspective and the fog tint. Kept in one file for the same reason the wind is:
//  when the sky and the fog each carried their own exp(-k * path), a distant ridge
//  at sunset reddened on a different curve than the sky directly behind it, and the
//  seam between them read as fake. Its CPU twin is SkyModel.AtmosphericTransmittance,
//  and both sides read the SAME per-channel coefficients (the L-27 triple) so the
//  ridge and the sky it sits against are lit by one law, not two that drift.
//
//  The coefficients themselves live in exactly one place - LightingConstants on the
//  C# side - and reach this file as the global below, set by the lighting manager.
//  Nothing here is a literal, so there is no second copy of the triple to fall out
//  of step. A scene with no lighting manager reads a zero global, and a zero
//  extinction is a perfectly clear, lossless air - correct, just not atmospheric.
// -----------------------------------------------------------------------------

#ifndef UC_LIGHTING_INCLUDED
#define UC_LIGHTING_INCLUDED

// Per-channel beam-extinction coefficients, published by the lighting manager.
// rgb = extinction per unit (air mass * turbidity); blue is the highest, which is
// what reddens a low sun and blues the sky. Zero until the manager sets it.
float3 _UCBeamExtinction;

/// Per-channel transmittance along a beam through the atmosphere: the fraction of
/// each colour that survives exp(-k * airMass * turbidity). Returns 1 (lossless)
/// at zero air mass or zero turbidity, and falls toward 0 as either grows - blue
/// fastest. This is the ONE extinction the GPU side calls; the sky, sun disc,
/// aerial perspective and fog tint all thin their light through it rather than
/// each rolling the exponential, so they agree along a shared horizon.
float3 UCAtmosphericExtinction(float airMass, float turbidity)
{
    float m = max(0.0, airMass) * max(0.0, turbidity);
    return exp(-_UCBeamExtinction * m);
}

// ---- The irradiance grid: one ambient read for terrain, vegetation and water ----
// The stored bounce light, sampled here and nowhere else. A second copy of this in a
// second shader is exactly how terrain and the grass on it end up lit differently at
// one point and the grass reads as floating; so this is the single definition every
// surface calls. The two textures carry the six ambient-cube faces three to a texture
// (positives in A, negatives in B), stacked along the depth axis - the CPU packer's
// mirror. Where Texture3D is unavailable the same bytes arrive as a 2-D atlas behind
// UC_IRRADIANCE_ATLAS, sampled at the matching row.

// Grid placement, published by the lighting manager. xy = world origin of column (0,0),
// z = horizontal spacing (m), w = vertical spacing (m). Dims xyz = column counts.
float4 _IrradianceGridOrigin;
float4 _IrradianceGridDims;

// Per-column terrain base height, so the vertical read follows the surface rather than a
// flat box - r channel, one texel per (x,z) column.
TEXTURE2D(_IrradianceBaseY);   SAMPLER(sampler_IrradianceBaseY);

#ifdef UC_IRRADIANCE_ATLAS
    TEXTURE2D(_IrradianceA);   SAMPLER(sampler_IrradianceA);
    TEXTURE2D(_IrradianceB);   SAMPLER(sampler_IrradianceB);
#else
    TEXTURE3D(_IrradianceA);   SAMPLER(sampler_IrradianceA);
    TEXTURE3D(_IrradianceB);   SAMPLER(sampler_IrradianceB);
#endif

// Fetch one stacked face (0,1,2) from texture A or B at grid coords (gx,gy,gz). The two
// storage shapes carry the identical layout, so the same (x,y,z,face) lands on the same
// texel either way - the "render identically" contract, resolved to a UV here.
float3 UCFetchIrradianceFace(float gx, float gy, float gz, float3 dims, int face, bool useB)
{
    float slabZ = face * dims.z + gz;      // the face's slab along the stacked depth
#ifdef UC_IRRADIANCE_ATLAS
    float atlasH = dims.y * dims.z * 3.0;
    float row = slabZ * dims.y + gy;       // (face*dimZ + z)*dimY + y
    float2 uv = float2((gx + 0.5) / dims.x, (row + 0.5) / atlasH);
    float4 s = useB ? SAMPLE_TEXTURE2D_LOD(_IrradianceB, sampler_IrradianceB, uv, 0)
                    : SAMPLE_TEXTURE2D_LOD(_IrradianceA, sampler_IrradianceA, uv, 0);
#else
    float depth = dims.z * 3.0;
    float3 uvw = float3((gx + 0.5) / dims.x, (gy + 0.5) / dims.y, (slabZ + 0.5) / depth);
    float4 s = useB ? SAMPLE_TEXTURE3D_LOD(_IrradianceB, sampler_IrradianceB, uvw, 0)
                    : SAMPLE_TEXTURE3D_LOD(_IrradianceA, sampler_IrradianceA, uvw, 0);
#endif
    return s.rgb;
}

// Blend six faces by the squared normal. The squared components of a unit normal sum to
// one, so this is a convex blend - no ringing, and no seam: on an axis (say n.x = 0 at a
// side) that face's weight is zero, so the posX/negX choice there changes nothing and the
// result is continuous across the axis.
float3 UCAmbientCube(float3 n, float3 px, float3 nx, float3 py, float3 ny, float3 pz, float3 nz)
{
    float3 w = n * n;
    float3 x = n.x >= 0.0 ? px : nx;
    float3 y = n.y >= 0.0 ? py : ny;
    float3 z = n.z >= 0.0 ? pz : nz;
    return w.x * x + w.y * y + w.z * z;
}

// The one ambient read. Maps a world position to surface-following grid coords (horizontal
// by spacing, vertical from the per-column base), trilinearly samples the six faces, and
// blends them by the squared surface normal. Returns zero where no grid is bound, which is
// a lossless "no stored ambient here" rather than a wrong value.
float3 UCSampleIrradiance(float3 worldPos, float3 normal)
{
    float spacing = _IrradianceGridOrigin.z;
    float vspacing = _IrradianceGridOrigin.w;
    float3 dims = _IrradianceGridDims.xyz;
    if (spacing <= 0.0 || vspacing <= 0.0 || dims.x < 1.0) return float3(0.0, 0.0, 0.0);

    float gx = (worldPos.x - _IrradianceGridOrigin.x) / spacing;
    float gz = (worldPos.z - _IrradianceGridOrigin.y) / spacing;

    float2 colUv = float2((clamp(gx, 0.0, dims.x - 1.0) + 0.5) / dims.x,
                          (clamp(gz, 0.0, dims.z - 1.0) + 0.5) / dims.z);
    float baseY = SAMPLE_TEXTURE2D_LOD(_IrradianceBaseY, sampler_IrradianceBaseY, colUv, 0).r;
    float gy = (worldPos.y - baseY) / vspacing;

    gx = clamp(gx, 0.0, dims.x - 1.0);
    gy = clamp(gy, 0.0, dims.y - 1.0);
    gz = clamp(gz, 0.0, dims.z - 1.0);

    float3 nrm = normalize(normal);
    float3 px = UCFetchIrradianceFace(gx, gy, gz, dims, 0, false);
    float3 py = UCFetchIrradianceFace(gx, gy, gz, dims, 1, false);
    float3 pz = UCFetchIrradianceFace(gx, gy, gz, dims, 2, false);
    float3 nx = UCFetchIrradianceFace(gx, gy, gz, dims, 0, true);
    float3 ny = UCFetchIrradianceFace(gx, gy, gz, dims, 1, true);
    float3 nz = UCFetchIrradianceFace(gx, gy, gz, dims, 2, true);
    return UCAmbientCube(nrm, px, nx, py, ny, pz, nz);
}

// True once the lighting manager has bound a grid. Lets a surface tell "no grid here"
// (a legitimately dark probe reads zero too) from "no grid at all", so it can keep its
// engine ambient until the grid goes live rather than going black the moment it compiles.
bool UCHasIrradianceGrid()
{
    return _IrradianceGridDims.x >= 1.0 && _IrradianceGridOrigin.z > 0.0;
}

// The one entry every lit surface uses for its ambient term: the grid's bounce when a grid
// is bound, otherwise the engine ambient it was already carrying. Terrain, vegetation and
// water all call this at the same point with the same args, so a tree in a valley is lit by
// the valley the ground under it reads - they cannot drift, because it is one function and
// one grid. Feed the result into inputData.bakedGI (the indirect slot) so occlusion still
// lands on it and only it (see UCComposeSurfaceLight).
float3 UCGridAmbientOr(float3 engineAmbient, float3 worldPos, float3 normalWS)
{
    return UCHasIrradianceGrid() ? UCSampleIrradiance(worldPos, normalWS) : engineAmbient;
}

// ---- Ambient occlusion belongs to the bounced light, and only to it ----
// AO measures how much of the sky and the bounce a crevice can see; it says nothing about
// the sun, which either reaches a point in full or is in shadow. Multiplying the direct
// term by AO is the single most common lighting error in real-time rendering - it paints
// black grime into sunlit cracks where reality has none. So the one law lives here: the
// occlusion factor scales the ambient/indirect term and is never handed the direct term.
// URP's own UniversalFragmentPBR already obeys this (occlusion reaches GlobalIllumination,
// never the direct pass); this is the single owner for any surface that folds the grid's
// ambient in by hand instead.
float3 UCComposeSurfaceLight(float3 directLight, float3 ambientLight, float ao)
{
    return directLight + ambientLight * saturate(ao);
}

#endif // UC_LIGHTING_INCLUDED
