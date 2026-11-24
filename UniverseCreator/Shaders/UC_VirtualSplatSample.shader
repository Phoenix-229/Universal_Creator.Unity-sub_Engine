// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_VirtualSplatSample.shader
//  Purpose: The GPU consumer that exercises UC_VirtualSplat.hlsl. The virtual splat
//           cache's sampling core is an include, and an include is only proven sound
//           when a real shader compiles it - so this thin surface shader samples the
//           cached ground through the page table exactly as the terrain material will,
//           deriving the screen footprint from world-position derivatives so the mip
//           selection and anisotropy run on live gradients rather than a constant.
//           It is the page-table sample the § 9.7 cache exists to feed, kept in its
//           own shader so the diagnostics sweep can compile-check the include directly.
//
//  HIDDEN, BECAUSE IT IS INFRASTRUCTURE
//  ------------------------------------
//  No author selects this in a material browser; it is the reusable sample path the
//  cached terrain draws with. Hidden keeps it out of the picker while the diagnostics
//  resolver (which reads Shader.name, not the menu) still finds and checks it.
// -----------------------------------------------------------------------------

Shader "Hidden/UniverseCreator/VirtualSplatSample"
{
    Properties { }

    SubShader
    {
        Tags
        {
            "Queue"          = "Geometry"
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex UCVSVert
            #pragma fragment UCVSFrag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "UC_VirtualSplat.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float  fogFactor  : TEXCOORD1;
            };

            Varyings UCVSVert(Attributes IN)
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                Varyings OUT;
                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 UCVSFrag(Varyings IN) : SV_Target
            {
                // Screen footprint of this fragment in world metres, from the world-position
                // derivatives: the long and short axes feed the cache's mip selection.
                float2 dX = ddx(IN.positionWS.xz);
                float2 dY = ddy(IN.positionWS.xz);
                float mppA = length(dX);
                float mppB = length(dY);
                float mppLong  = max(mppA, mppB);
                float mppShort = min(mppA, mppB);

                half4 ground = UC_SampleVirtualSplat(IN.positionWS.xz, mppLong, mppShort);
                ground.rgb = MixFog(ground.rgb, IN.fogFactor);
                return ground;
            }
            ENDHLSL
        }
    }

    // No FallBack: a Built-in fallback draws magenta under URP and hides the cause.
}
