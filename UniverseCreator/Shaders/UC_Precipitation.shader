// UC-T08-08: analytic soft drops/flakes for camera-near particles; no bundled content texture is required.
Shader "UniverseCreator/Precipitation"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Precipitation"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color; output.uv = input.uv;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float2 radius = input.uv * 2 - 1;
                half alpha = saturate(1 - dot(radius, radius));
                half3 illumination = max(GetMainLight().color + SampleSH(half3(0, 1, 0)), 0);
                return half4(input.color.rgb * illumination, input.color.a * alpha * alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
