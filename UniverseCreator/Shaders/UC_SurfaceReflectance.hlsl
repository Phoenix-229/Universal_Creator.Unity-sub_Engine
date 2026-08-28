// -----------------------------------------------------------------------------
//  Universe Creator - GPU twins of the surface-reflectance physics (S-75 / S-76), and the
//  cheap mobile environment reflection built from them (S-79).
//
//  These are the shader mirrors of SpecularOcclusion.cs and WetSurfaceOptics.cs - the SAME
//  maths, so the mobile reflection a phone shows and the CPU probe logic agree. The three wet
//  constants have their home in LightingConstants; the driver publishes them in
//  _UCWetOpticsParams so there is one source, and each read falls back to that home's authored
//  value when no driver has published yet, so a scene with no manager still reflects plausibly
//  rather than going matte-black.
//
//  MOBILE HAS NO RAY (S-79): below Ultra there is no screen-space march. The environment
//  reflection is the analytic sky along the reflect vector where it clears the horizon, the
//  irradiance grid where it does not, occluded by S-75 and weighted by S-76's wetness Fresnel.
//  None of it traces a ray, so it is cheap; and it is never black, so a phone shows sky in a
//  puddle and gloss on wet rock. Requires UC_Lighting.hlsl (the grid) already included.
// -----------------------------------------------------------------------------

#ifndef UC_SURFACE_REFLECTANCE_INCLUDED
#define UC_SURFACE_REFLECTANCE_INCLUDED

#include "UC_Sky.hlsl"

// x = wet albedo darkening, y = wet floor roughness, z = water F0, w = unused. Published by the
// driver from LightingConstants; the fallbacks below are those same authored values so an
// unpublished (zero) uniform still yields the correct physics rather than a degenerate surface.
float4 _UCWetOpticsParams;

float UCWetAlbedoDarkening() { return _UCWetOpticsParams.x > 0.0 ? _UCWetOpticsParams.x : 0.35; }
float UCWetFloorRoughness()  { return _UCWetOpticsParams.y > 0.0 ? _UCWetOpticsParams.y : 0.06; }
float UCWaterF0()            { return _UCWetOpticsParams.z > 0.0 ? _UCWetOpticsParams.z : 0.02; }

// --- S-76 wet-surface optics (mirror of WetSurfaceOptics.cs) ------------------
float UCEffectiveWetness(float wetness01, float puddle01)
{
    return saturate(max(saturate(wetness01), saturate(puddle01)));
}

float3 UCWetAlbedo(float3 dryAlbedo, float wetness01, float puddle01)
{
    float w = UCEffectiveWetness(wetness01, puddle01);
    return dryAlbedo * (1.0 - UCWetAlbedoDarkening() * w);
}

// Wetting only ever glosses a surface (never rougher than dry); w^2 sharpens the gloss fast.
float UCWetRoughness(float dryRoughness01, float wetness01, float puddle01)
{
    float w = UCEffectiveWetness(wetness01, puddle01);
    float wet = lerp(saturate(dryRoughness01), UCWetFloorRoughness(), w * w);
    return min(saturate(dryRoughness01), wet);
}

float UCFresnelWater(float nDotV)
{
    float f0 = UCWaterF0();
    float m = saturate(1.0 - saturate(nDotV));
    float m2 = m * m;
    return f0 + (1.0 - f0) * (m2 * m2 * m);
}

float UCSkyReflectionWeight(float nDotV, float wetness01, float puddle01)
{
    return UCFresnelWater(nDotV) * UCEffectiveWetness(wetness01, puddle01);
}

// --- S-75 specular occlusion (mirror of SpecularOcclusion.cs) -----------------
// A narrower cone than diffuse: rough surfaces collapse to the diffuse visibility, smooth ones
// are gated by whether the dominant reflected direction aims at the open (visible) hemisphere.
float UCSpecularOcclusion(float diffuseVisibility01, float3 bentNormal, float3 reflectDir, float roughness01)
{
    float vis = saturate(diffuseVisibility01);
    float r   = saturate(roughness01);
    float3 b = dot(bentNormal, bentNormal) > 1e-12 ? normalize(bentNormal) : float3(0.0, 1.0, 0.0);
    float3 d = dot(reflectDir, reflectDir) > 1e-12 ? normalize(reflectDir) : float3(0.0, 1.0, 0.0);
    float align = saturate(dot(d, b));
    float exponent = pow(2.0, -16.0 * r - 1.0);
    float f = pow(align + vis, exponent) - 1.0 + vis;
    return saturate(f);
}

// The cheap mobile environment reflection (S-79): sky where the reflect ray clears the horizon,
// grid where it does not, occluded by S-75 and weighted by S-76's wetness Fresnel. No ray march.
float3 UCMobileEnvironmentReflection(float3 worldPos, float3 N, float3 V, float dryRoughness01,
                                     float wetness01, float puddle01,
                                     float diffuseVisibility01, float3 bentNormal)
{
    float3 R = reflect(-normalize(V), normalize(N));
    float3 env = R.y > 0.0 ? UCSampleSkyRadiance(R) : UCSampleIrradiance(worldPos, R);
    float rough = UCWetRoughness(dryRoughness01, wetness01, puddle01);
    float so = UCSpecularOcclusion(diffuseVisibility01, bentNormal, R, rough);
    float w  = UCSkyReflectionWeight(saturate(dot(normalize(N), normalize(V))), wetness01, puddle01);
    return env * (so * w);
}

// The one strength dial for the ray-free reflection, published by the quality ladder (S-82).
// Per the S-83 budget this term is a baseline on every device class, so once a manager sets a
// tier the ladder drives this positive at all of them; its own 0 default is only the pre-manager
// safe state (no manager in scene keeps the term inert). The reflection is Fresnel x wetness
// weighted downstream, so even at full strength it costs and shows nothing on a dry surface.
// Kept here so terrain, props, grass and the overlay share ONE dial rather than each inventing a
// keyword, and so S-81 can wire the term into every consumer while the ladder owns when it lights.
float _UCEnvReflectionStrength;

// The single entry a surface adds after its BRDF: the wet-sky/grid reflection, already weighted
// to nothing when dry (Fresnel x wetness) so it never double-counts the engine's own probe
// reflection, and scaled by the tier gate so it costs nothing where the ladder leaves it off.
// bentNormal falls back to the geometric normal until a per-pixel sky-visibility bake feeds it,
// and puddle01 is 0 until the S 9.5 micro-relief lands - both honest, both only soften the term.
float3 UCEnvReflectionContribution(float3 worldPos, float3 N, float3 V, float dryRoughness01,
                                   float wetness01, float puddle01,
                                   float diffuseVisibility01, float3 bentNormal)
{
    if (_UCEnvReflectionStrength <= 0.0) return float3(0.0, 0.0, 0.0);
    return _UCEnvReflectionStrength *
        UCMobileEnvironmentReflection(worldPos, N, V, dryRoughness01, wetness01, puddle01,
                                      diffuseVisibility01, bentNormal);
}

#endif // UC_SURFACE_REFLECTANCE_INCLUDED
