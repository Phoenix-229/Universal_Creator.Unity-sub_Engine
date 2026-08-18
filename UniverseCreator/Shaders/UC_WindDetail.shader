// -----------------------------------------------------------------------------
//  Universe Creator - wind for grass, bushes and ground cover.
//
//  This is the piece that was missing. The Wind Sway Parameter Bake node has
//  always produced correct amplitude and phase maps, but nothing sampled them at
//  render time, so the variation they describe never reached the screen. This
//  shader samples them, which is what makes grass in a sheltered hollow move
//  less than grass on an exposed ridge.
//
//  USE IT ON: grass, ground cover, bushes, ferns, reeds - anything light enough
//  to be pushed around. Assign this material to a detail prototype prefab and
//  tick Use Instancing on the prototype.
//
//  SET _VegetationClass TO MATCH THE PLANT. It is not a style dial: it selects a
//  different motion model. Grass is a cantilever that lies right over in a
//  breeze and flutters fast. A shrub is woody - its lower quarter does not move
//  at all, it needs more wind before it gives, and it flutters slower because a
//  leaf cluster carries more mass than a blade. Setting a bush to grass is the
//  single most common way to make a scene look wrong.
//
//  DO NOT USE IT ON TREES. A tree is not a big blade of grass: the trunk holds
//  and only the canopy shifts. Trees are driven by the WindZone that
//  UCWindManager creates, which is what Unity's own tree shaders read.
//
//  Everything animated here comes from global uniforms set by UCWindManager, so
//  a scene with no wind manager renders perfectly still rather than breaking.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/WindDetail"
{
    Properties
    {
        [MainTexture] _BaseMap  ("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (1,1,1,1)

        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.4

        [Header(Wind)]
        [Enum(Grass,0,Shrub,1)] _VegetationClass ("Vegetation Class", Float) = 0
        _WindResponse ("Wind Response", Range(0.0, 3.0)) = 1.0
        _BendHeight   ("Bend From Height", Range(0.0, 1.0)) = 1.0
        _Flutter      ("Flutter", Range(0.0, 2.0)) = 0.6
        _PivotHeight  ("Mesh Height (m)", Range(0.05, 10.0)) = 1.0

        [Header(Interaction)]
        _TouchResponse ("Push Aside When Walked Through", Range(0.0, 2.0)) = 1.0

        [Header(Shading)]
        _DryColor      ("Dry Tint", Color) = (0.82, 0.78, 0.55, 1)
        _DryBlend      ("Dry Blend", Range(0.0, 1.0)) = 0.0
        _AmbientWrap   ("Ambient Wrap", Range(0.0, 1.0)) = 0.5
        _Translucency  ("Translucency", Range(0.0, 1.0)) = 0.35

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "TransparentCutout"
            "Queue"          = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector"= "True"
        }

        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "UC_Wind.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half4  _DryColor;
            half   _Cutoff;
            half   _WindResponse;
            half   _BendHeight;
            half   _Flutter;
            half   _PivotHeight;
            half   _VegetationClass;
            half   _TouchResponse;
            half   _DryBlend;
            half   _AmbientWrap;
            half   _Translucency;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // One displacement for all three passes. Shared and not copied because a
        // shadow pass that bends by even slightly less detaches the shadow from
        // the blade and slides it across the ground as the wind moves.
        float3 UCDetailDisplace(float3 positionWS, float3 positionOS)
        {
            // Object-space Y over the authored mesh height gives 0 at the root and
            // 1 at the tip, which is the only sane way to anchor a base while the
            // tip travels.
            float heightFrac = saturate(positionOS.y / max(0.001, _PivotHeight));
            heightFrac = lerp(1.0, heightFrac, _BendHeight);

            // The pivot, not the vertex, decides which gust this plant is in -
            // otherwise a tall blade would straddle two gusts and shear.
            float3 pivotWS = TransformObjectToWorld(float3(0, 0, 0));

            int vegClass = (int)(_VegetationClass + 0.5);

            float3 offset = UCWindDisplacementClass(pivotWS, heightFrac,
                                                    _WindResponse, _Flutter, vegClass);

            // Someone walking through parts the plant regardless of wind, so this
            // is added and not blended: a still day still gets a parting.
            offset += UCWindPush(positionWS, heightFrac, _TouchResponse);

            return offset;
        }
        ENDHLSL

        // ---------------------------------------------------------------------
        //  Forward lit
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma vertex   WindDetailVert
            #pragma fragment WindDetailFrag
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "UC_Underwater.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  color      : TEXCOORD3;
                float  fogCoord   : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings WindDetailVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCDetailDisplace(positionWS, IN.positionOS.xyz);

                OUT.positionWS = positionWS;
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.color      = IN.color;
                OUT.fogCoord   = ComputeFogFactor(OUT.positionCS.z);

                return OUT;
            }

            half4 WindDetailFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half4 albedo = tex * _BaseColor * IN.color;

                clip(albedo.a - _Cutoff);

                albedo.rgb = lerp(albedo.rgb, albedo.rgb * _DryColor.rgb, _DryBlend);

                half3 normalWS = normalize(IN.normalWS);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // Wrapped diffuse. Grass lit as a hard surface goes black on every
                // blade facing away; wrapping keeps the far side readable, which is
                // closer to how a thin translucent blade actually behaves.
                half ndotl = dot(normalWS, mainLight.direction);
                half wrapped = saturate((ndotl + _AmbientWrap) / (1.0 + _AmbientWrap));

                // Light passing through the blade from behind.
                half back = saturate(-ndotl) * _Translucency;

                half3 lighting = mainLight.color * mainLight.shadowAttenuation
                               * (wrapped + back);

                half3 ambient = SampleSH(normalWS);

                half3 color = albedo.rgb * (lighting + ambient);

                color = MixFog(color, IN.fogCoord);

                // Water plants are the main thing this shader draws below a
                // waterline, so they must be filtered by the same medium the seabed
                // is - a bright green weed against a blue-green bed is the tell that
                // the plants were composited in rather than growing there.
                color = UCApplyUnderwater(color, IN.positionWS);

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        //  Shadows. Must apply the same displacement or the shadow detaches from
        //  the blade and slides across the ground as the wind moves.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCDetailDisplace(positionWS, IN.positionOS.xyz);

                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));

#if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

                OUT.positionCS = positionCS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        //  Depth only. Same displacement again, for the same reason.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma target 3.0
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCDetailDisplace(positionWS, IN.positionOS.xyz);

                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
