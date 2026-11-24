// -----------------------------------------------------------------------------
//  Universe Creator - shared analytic-sky sampling (the ONE home for the sky LUT read).
//
//  The sky is a precomputed table (§ 8.5): azimuth-from-sun across, horizon-to-zenith up,
//  folded because the clear sky is a mirror image either side of the sun's vertical plane.
//  The skybox draws it behind the world; the reflection fallback (S-78) reads the SAME table
//  along a reflected ray so a puddle holds the same sky the dome shows. One mapping, one read,
//  no second copy to drift - the skybox and the reflection cannot disagree about the sky.
//
//  A scene with no lighting controller leaves _SkyExposure at 0 and the table black, so the
//  read returns black sky, which the caller treats as "no sky contribution" - a safe default.
// -----------------------------------------------------------------------------

#ifndef UC_SKY_INCLUDED
#define UC_SKY_INCLUDED

TEXTURE2D(_SkyLUT);      SAMPLER(sampler_SkyLUT);
TEXTURE2D(_UCPreviousSkyLUT); SAMPLER(sampler_UCPreviousSkyLUT);
float _UCSkyBlend;
float  _SkyExposure;     // table scale; 0 with no controller present
float4 _UCSunDir;        // xyz = normalised world direction toward the sun

// A world direction to a table coordinate. Width is azimuth measured away from the sun,
// folded to [0,pi] (the sky mirrors either side of the sun's vertical plane); height is
// elevation from horizon to zenith. Both clamped so no direction is off-table.
float2 UCSkyUV(float3 dir, float3 sunDir)
{
    float el = asin(clamp(dir.y, -1.0, 1.0));
    float v  = saturate(el / (0.5 * PI));

    float2 hd = dir.xz;
    float2 hs = sunDir.xz;
    float lenD = length(hd);
    float lenS = length(hs);
    float cosAz = (lenD > 1e-4 && lenS > 1e-4) ? dot(hd, hs) / (lenD * lenS) : 1.0;
    float u = acos(clamp(cosAz, -1.0, 1.0)) / PI;
    return float2(u, v);
}

// The clear-sky radiance along a world direction, the same value the dome shows there.
float3 UCSampleSkyRadiance(float3 dir)
{
    float2 uv = UCSkyUV(normalize(dir), _UCSunDir.xyz);
    // UC-T08-14: the texture-wide exponent keeps physical daylight finite in a 4 KB half-float LUT; decode before applying the shared exposure.
    float4 sky = SAMPLE_TEXTURE2D_LOD(_SkyLUT, sampler_SkyLUT, uv, 0);
    float3 radiance = sky.rgb * exp2(sky.a);
    // UC Scene/Play: decode both storage exponents before interpolating physical light. Sky and reflections share this read.
    if (_UCSkyBlend < 1.0)
    {
        float4 previous = SAMPLE_TEXTURE2D_LOD(_UCPreviousSkyLUT, sampler_UCPreviousSkyLUT, uv, 0);
        radiance = lerp(previous.rgb * exp2(previous.a), radiance, saturate(_UCSkyBlend));
    }
    return radiance * max(_SkyExposure, 0.0);
}

#endif // UC_SKY_INCLUDED
