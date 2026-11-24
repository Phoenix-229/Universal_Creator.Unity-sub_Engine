#ifndef UC_TERRAIN_LIT_DEPTH_PASSES_INCLUDED
#define UC_TERRAIN_LIT_DEPTH_PASSES_INCLUDED

// -----------------------------------------------------------------------------
//  Universe Creator - UC_TerrainLitDepthPasses.hlsl
//  ShadowCaster, DepthOnly and DepthNormals.
//
//  These three share a file because they are the same vertex work with three
//  different outputs, and because they must agree with the forward pass exactly.
//  Any of them displacing instanced terrain differently from UCTerrainVert
//  produces shadows or depth that do not line up with the surface being drawn -
//  which looks like shadow acne or peter-panning and gets misdiagnosed as a
//  bias problem every time.
//
//  All of them clip holes. A hole that still writes depth is an invisible wall.
// -----------------------------------------------------------------------------

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct DepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct DepthVaryings
{
    float2 uvControl  : TEXCOORD0;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct DepthNormalsVaryings
{
    float2 uvControl  : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// =============================================================================
//  ShadowCaster
// =============================================================================

float3 _LightDirection;
float3 _LightPosition;

float4 UCGetShadowPositionHClip(float3 positionWS, half3 normalWS)
{
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    // A point or spot light's direction differs per fragment, so the normal
    // bias has to be computed against the direction to that light, not against
    // a single global direction.
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

    return positionCS;
}

DepthVaryings UCShadowVert(DepthAttributes input)
{
    DepthVaryings output = (DepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 normalOS = input.normalOS;
    UCTerrainInstancing(input.positionOS, normalOS, input.texcoord);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    half3  normalWS   = TransformObjectToWorldNormal(normalOS);

    output.uvControl  = TRANSFORM_TEX(input.texcoord, _Control);
    output.positionCS = UCGetShadowPositionHClip(positionWS, normalWS);
    return output;
}

half4 UCShadowFrag(DepthVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UCClipSurface(input.uvControl); // UC-T07-06: opacity clipping matches the forward surface.
#ifdef _ALPHATEST_ON
    ClipHoles(input.uvControl);
#endif
    return 0;
}

// =============================================================================
//  DepthOnly
// =============================================================================

DepthVaryings UCDepthOnlyVert(DepthAttributes input)
{
    DepthVaryings output = (DepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 normalOS = input.normalOS;
    UCTerrainInstancing(input.positionOS, normalOS, input.texcoord);

    output.uvControl  = TRANSFORM_TEX(input.texcoord, _Control);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

half4 UCDepthOnlyFrag(DepthVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UCClipSurface(input.uvControl); // UC-T07-06: clipped terrain writes no depth.
#ifdef _ALPHATEST_ON
    ClipHoles(input.uvControl);
#endif
    return 0;
}

// =============================================================================
//  DepthNormals - feeds SSAO and any depth-normals effect.
// =============================================================================

DepthNormalsVaryings UCDepthNormalsVert(DepthAttributes input)
{
    DepthNormalsVaryings output = (DepthNormalsVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 normalOS = input.normalOS;
    UCTerrainInstancing(input.positionOS, normalOS, input.texcoord);

    output.uvControl  = TRANSFORM_TEX(input.texcoord, _Control);
    output.normalWS   = TransformObjectToWorldNormal(normalOS);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

half4 UCDepthNormalsFrag(DepthNormalsVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UCClipSurface(input.uvControl); // UC-T07-06: SSAO sees the same clipped surface.
#ifdef _ALPHATEST_ON
    ClipHoles(input.uvControl);
#endif

    half3 normalWS = normalize(input.normalWS);

#ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
    float3 nMap = SAMPLE_TEXTURE2D(_TerrainNormalmapTexture, sampler_TerrainNormalmapTexture,
                                   input.uvControl).rgb * 2 - 1;
    normalWS = normalize(TransformObjectToWorldNormal(nMap));
#endif

    return half4(NormalizeNormalPerPixel(normalWS), 0.0);
}

#endif // UC_TERRAIN_LIT_DEPTH_PASSES_INCLUDED
