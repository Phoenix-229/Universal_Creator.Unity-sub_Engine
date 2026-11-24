// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_PrecipParticle.hlsl
//  Purpose: The shared vertex and fragment for the rain and snow particle
//           materials. Both draw an alpha-blended, unlit, fogged particle whose
//           opacity is scaled by two terms - a soft depth fade so it dissolves
//           into the surface behind it rather than clipping through, and a canopy
//           openness so it thins out under leaves that have already caught it.
// -----------------------------------------------------------------------------

#ifndef UC_PRECIP_PARTICLE_INCLUDED
#define UC_PRECIP_PARTICLE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4  _BaseColor;
    float  _SoftFadeMetres;
    float  _CanopyOpen01;
CBUFFER_END

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv         : TEXCOORD0;
    half4  color      : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    half4  color      : COLOR;
    float4 screenPos  : TEXCOORD1;
    float  eyeDepth   : TEXCOORD2;
    float  fogFactor  : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// APPEND-HLSL

Varyings UCPrecipVert(Attributes IN)
{
    Varyings OUT = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
    OUT.positionCS = pos.positionCS;
    OUT.screenPos  = ComputeScreenPos(pos.positionCS);
    OUT.eyeDepth   = -pos.positionVS.z;
    OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
    OUT.color      = IN.color;
    OUT.fogFactor  = ComputeFogFactor(pos.positionCS.z);
    return OUT;
}

half4 UCPrecipFrag(Varyings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);

    half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
    half4 col = tex * _BaseColor * IN.color;

    // Soft depth fade: dissolve the particle as it nears the surface behind it,
    // so precipitation never draws through the ground or a roof.
    float2 screenUV = IN.screenPos.xy / max(IN.screenPos.w, 1e-4);
    float  rawDepth = SampleSceneDepth(screenUV);
    float  sceneEye = LinearEyeDepth(rawDepth, _ZBufferParams);
    float  soft     = saturate((sceneEye - IN.eyeDepth) / max(_SoftFadeMetres, 1e-4));

    // Canopy openness thins the particle under a closed canopy.
    col.a *= soft * saturate(_CanopyOpen01);

    col.rgb = MixFog(col.rgb, IN.fogFactor);
    return col;
}

#endif

