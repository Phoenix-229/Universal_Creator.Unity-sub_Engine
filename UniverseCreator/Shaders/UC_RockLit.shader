// -----------------------------------------------------------------------------
//  Universe Creator - rock, cliff, gravel and bare ground.
//
//  WHY THIS SHADER EXISTS
//  ----------------------
//  The rock and ground surfaces that shipped inside Content/ were authored by
//  their original vendors as general-purpose Shader Graphs and hand-written
//  "ALP" shaders. They render, but they are outside this system: they read no
//  season, no weather and no underwater state, so a boulder stayed bone dry in a
//  downpour, held no snow above the snow line, and kept its dry albedo when a
//  river rose over it. Nothing in them was wrong - they were simply written for
//  a generic scene rather than for a world that has weather.
//
//  This shader replaces them with one surface that reads the same globals the
//  terrain and vegetation already read, so a rock, the ground it sits on, and the
//  grass beside it all agree about what the weather is doing.
//
//  THE PHYSICS IT COMMITS TO
//  -------------------------
//  Rock is a dielectric. Its metallic value is zero and that is not a style
//  choice - a non-metal has no free electrons to give it a tinted specular, so
//  authoring metallic above zero on stone is physically meaningless. Only genuine
//  ore veins with exposed native metal deserve otherwise, which is why _Metallic
//  exists at all but defaults to 0.
//
//  Measured diffuse reflectance of dry rock sits between roughly 0.20 and 0.40:
//  granite about 0.30, limestone nearer 0.40, basalt as low as 0.12. Smoothness
//  defaults to 0.28 because a dry fracture face is rough at every scale a normal
//  map can describe.
//
//  Wet rock is the single largest visual change weather makes to stone, and it is
//  a real optical effect rather than a tint: a water film fills the surface
//  micro-relief, so it both lowers the diffuse albedo (light that used to scatter
//  straight back now enters the film and is partly absorbed) and raises the
//  specular smoothness (the film's own surface is far flatter than the rock's).
//  UCApplyWetness does exactly those two things, from the same global the terrain
//  reads, so a cliff and the terrain at its base darken together.
//
//  Snow is gated on the surface normal, not painted uniformly. Snow lands on what
//  faces the sky; an overhang or a vertical face carries none. UCSnowAt does the
//  normal gate and the altitude gate together, which matters more on rock than on
//  anything else in the world because rock is the only surface that routinely
//  presents a true vertical face to the camera.
//
//  Lichen is off by default and is not decoration. Crustose lichen colonises
//  upward-facing, non-vertical stone below the snow line, because it needs the
//  water that sits there and cannot hold onto a face that sheds it. That is the
//  gate used here. It defaults to 0 so no existing material changes appearance
//  until someone authors it on.
//
//  PARALLAX IS OPT-IN, DELIBERATELY
//  --------------------------------
//  _PARALLAX_ON is a shader_feature, not a branch, and ships off. Height-offset
//  sampling costs a dependent texture read, which is the single most expensive
//  thing a mobile fragment shader can do. Rock is where parallax pays off most
//  visually, so the feature is here and correct - but a phone build gets it only
//  where the author decided a given cliff deserves it. _ParallaxMetres is a real
//  depth in metres so the relief matches the rock's actual scale rather than
//  being dialled by eye.
//
//  SPECULAR-GLOSS INPUTS ARE CONVERTED AT AUTHORING TIME, NOT HERE
//  --------------------------------------------------------------
//  Several of the vendor materials were specular-gloss. Carrying a second
//  lighting model in this shader to accommodate them would mean two code paths
//  doing one job. Instead the conversion happens once when the material is
//  written: a dielectric's specular colour carries no information a metallic
//  workflow needs, so metallic becomes 0 and the gloss channel becomes
//  smoothness. Nothing is lost because there was nothing there to lose.
//
//  URP 17.5 / Unity 6000.5. The keyword set below is the current one - note
//  _CLUSTER_LIGHT_LOOP, which replaced _FORWARD_PLUS. The old spelling still
//  compiles and silently yields no lights beyond the sun, so it is worth being
//  explicit that this is the new name.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/RockLit"
{
    Properties
    {
        [MainTexture] _BaseMap   ("Albedo", 2D) = "white" {}
        [MainColor]   _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        // Clipping is unconditional in every pass, with no _ALPHATEST_ON keyword.
        // That keyword is obsolete in current URP and a shader still declaring it
        // errors, so the cutoff value alone decides. Default 0 means a solid rock
        // never loses a pixel; raise it only for a genuinely cut-out stone.
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.0

        // Normals. Detail normal is a second, higher-frequency map that fades out
        // with distance - it is what stops a cliff going flat when you walk up to it.
        _BumpMap        ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0.0, 4.0)) = 1.0
        _DetailNormalMap      ("Detail Normal", 2D) = "bump" {}
        _DetailNormalStrength ("Detail Normal Strength", Range(0.0, 2.0)) = 1.0
        _DetailFadeStart ("Detail Fade Start (m)", Float) = 8.0
        _DetailFadeEnd   ("Detail Fade End (m)", Float) = 25.0

        // Occlusion. Rock is the surface where a baked AO map does the most work,
        // because its own crevices shadow it far more than its surroundings do.
        _OcclusionMap      ("Occlusion", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0.0, 1.0)) = 1.0

        // Metallic (R) / Smoothness (A), matching URP's own mask packing.
        _MetallicGlossMap ("Metallic (R) Smoothness (A)", 2D) = "white" {}
        _Metallic   ("Metallic", Range(0.0, 1.0)) = 0.0
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.28

        // Height relief in real metres, not an arbitrary scalar.
        _ParallaxMap    ("Height", 2D) = "black" {}
        _ParallaxMetres ("Height Relief (m)", Range(0.0, 0.25)) = 0.03

        _EmissionMap        ("Emission", 2D) = "black" {}
        [HDR] _EmissionColor ("Emission Colour", Color) = (0, 0, 0, 1)

        // Weather link. Both read globals that are zero with no weather manager,
        // so a scene without one renders exactly as a dry summer scene.
        _WetDarkening     ("Wet Darkening", Range(0.0, 1.0)) = 0.55
        _SnowAccumulation ("Snow Accumulation", Range(0.0, 1.0)) = 1.0

        // Lichen: upward-facing stone below the snow line. Off unless authored.
        _LichenColor  ("Lichen Tint", Color) = (0.42, 0.46, 0.34, 1)
        _LichenAmount ("Lichen Amount", Range(0.0, 1.0)) = 0.0
        _LichenUpBias ("Lichen Up Bias", Range(0.0, 1.0)) = 0.65

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2

        [Toggle(_NORMALMAP)]        _NormalMapOn     ("Normal Map", Float) = 1
        [Toggle(_PARALLAX_ON)]      _ParallaxOn      ("Parallax (costly on mobile)", Float) = 0
        [Toggle(_DETAILNORMAL_ON)]  _DetailNormalOn  ("Detail Normal", Float) = 0
        [Toggle(_OCCLUSIONMAP)]     _OcclusionOn     ("Occlusion Map", Float) = 0
        [Toggle(_METALLICSPECGLOSSMAP)] _MaskMapOn   ("Metallic/Smoothness Map", Float) = 0
        [Toggle(_EMISSION)]         _EmissionOn      ("Emission", Float) = 0
        [Toggle(_WEATHER_ON)]       _WeatherOn       ("Season / Weather Link", Float) = 1
        [Toggle(_LICHEN_ON)]        _LichenOn        ("Lichen", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"        = "Opaque"
            "RenderPipeline"    = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector"   = "True"
        }
        LOD 300

        // ---------------------------------------------------------------------
        //  Shared material state. One CBUFFER for every pass, which is what keeps
        //  this SRP Batcher compatible - a property declared outside it, or
        //  declared differently in two passes, silently drops the batch.
        // ---------------------------------------------------------------------
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _DetailNormalMap_ST;
            half4  _BaseColor;
            half4  _EmissionColor;
            half4  _LichenColor;
            half   _Cutoff;
            half   _NormalStrength;
            half   _DetailNormalStrength;
            float  _DetailFadeStart;
            float  _DetailFadeEnd;
            half   _OcclusionStrength;
            half   _Metallic;
            half   _Smoothness;
            half   _ParallaxMetres;
            half   _WetDarkening;
            half   _SnowAccumulation;
            half   _LichenAmount;
            half   _LichenUpBias;
            half   _Cull;
        CBUFFER_END

        // DOTS/BRG instanced overrides. Colour and the two scalars an author is
        // most likely to vary per instance; the rest stay per material.
        #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
                UNITY_DOTS_INSTANCED_PROP(float,  _Smoothness)
                UNITY_DOTS_INSTANCED_PROP(float,  _Metallic)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

            static float4 unity_DOTS_Sampled_BaseColor;
            static float  unity_DOTS_Sampled_Smoothness;
            static float  unity_DOTS_Sampled_Metallic;

            void SetupDOTSRockMaterialPropertyCaches()
            {
                unity_DOTS_Sampled_BaseColor  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
                unity_DOTS_Sampled_Smoothness = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Smoothness);
                unity_DOTS_Sampled_Metallic   = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Metallic);
            }

            #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
            #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSRockMaterialPropertyCaches()

            #define _BaseColor  unity_DOTS_Sampled_BaseColor
            #define _Smoothness unity_DOTS_Sampled_Smoothness
            #define _Metallic   unity_DOTS_Sampled_Metallic
        #endif

        TEXTURE2D(_BaseMap);          SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap);          SAMPLER(sampler_BumpMap);
        TEXTURE2D(_DetailNormalMap);  SAMPLER(sampler_DetailNormalMap);
        TEXTURE2D(_OcclusionMap);     SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_ParallaxMap);      SAMPLER(sampler_ParallaxMap);
        TEXTURE2D(_EmissionMap);      SAMPLER(sampler_EmissionMap);

        #include "UC_Season.hlsl"
        #include "UC_Underwater.hlsl"
        #include "UC_Lighting.hlsl"   // the shared irradiance grid, so a prop is lit by the same ambient the ground reads
        #include "UC_SurfaceReflectance.hlsl"   // the ray-free wet-sky reflection twin, shared with the mobile path

        // Lichen: upward-facing, non-vertical, below the snow line. Returns 0..1.
        // Gated the same way the real organism is - it needs standing moisture, so
        // it colonises what holds water and not what sheds it.
        half UCRockLichen(float3 normalWS, float snow)
        {
            half up = saturate(normalWS.y);
            // Bias moves the cut between "flat enough to hold water" and "sheds it".
            half hold = saturate((up - (1.0h - _LichenUpBias)) / max(_LichenUpBias, 0.001h));
            return _LichenAmount * hold * (1.0h - saturate(snow));
        }
        ENDHLSL

        // ---------------------------------------------------------------------
        //  Forward lighting.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   RockVert
            #pragma fragment RockFrag
            #pragma target 3.0

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAX_ON
            #pragma shader_feature_local _DETAILNORMAL_ON
            #pragma shader_feature_local _OCCLUSIONMAP
            #pragma shader_feature_local _METALLICSPECGLOSSMAP
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _WEATHER_ON
            #pragma shader_feature_local _LICHEN_ON

            // URP 17.5 lighting keywords. _CLUSTER_LIGHT_LOOP is the current name
            // for what used to be _FORWARD_PLUS.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                float2 dynamicLightmapUV : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  tangentWS  : TEXCOORD3;
                half3  viewDirTS  : TEXCOORD4;
                half4  fogFactorAndVertexLight : TEXCOORD5;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD7;
            #endif
            #ifdef DYNAMICLIGHTMAP_ON
                float2 dynamicLightmapUV : TEXCOORD8;
            #endif
            #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion : TEXCOORD9;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings RockVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS   = nrm.normalWS;
                OUT.tangentWS  = half4(nrm.tangentWS, IN.tangentOS.w * GetOddNegativeScale());

                // Tangent-space view vector, needed by parallax. Built here rather
                // than in the fragment so it costs one transform per vertex.
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(pos.positionWS);
                half3 bitangent = OUT.tangentWS.w * cross(nrm.normalWS, nrm.tangentWS);
                OUT.viewDirTS  = half3(dot(nrm.tangentWS, viewDirWS),
                                       dot(bitangent,     viewDirWS),
                                       dot(nrm.normalWS,  viewDirWS));

                OUT.fogFactorAndVertexLight = half4(ComputeFogFactor(pos.positionCS.z),
                                                    VertexLighting(pos.positionWS, nrm.normalWS));

                OUTPUT_LIGHTMAP_UV(IN.staticLightmapUV, unity_LightmapST, OUT.staticLightmapUV);
            #ifdef DYNAMICLIGHTMAP_ON
                OUT.dynamicLightmapUV = IN.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
            #endif
                OUTPUT_SH4(pos.positionWS, OUT.normalWS.xyz, GetWorldSpaceNormalizeViewDir(pos.positionWS), OUT.vertexSH, OUT.probeOcclusion);

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                OUT.shadowCoord = GetShadowCoord(pos);
            #endif
                return OUT;
            }

            half4 RockFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.uv;

            #ifdef _PARALLAX_ON
                // One-step height offset. The height map's green channel matches
                // Unity's own convention so vendor height maps drop straight in.
                half h = SAMPLE_TEXTURE2D(_ParallaxMap, sampler_ParallaxMap, uv).g;
                uv += ParallaxOffset1Step(h, _ParallaxMetres, normalize(IN.viewDirTS));
            #endif

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
                half3 albedo = baseSample.rgb * _BaseColor.rgb;
                half  alpha  = baseSample.a   * _BaseColor.a;

                clip(alpha - _Cutoff);

                // Normal. Detail normal is blended in whitespace-preserving form
                // and faded by distance so it costs nothing at range.
                half3 normalTS = half3(0, 0, 1);
            #ifdef _NORMALMAP
                normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _NormalStrength);
            #endif
            #ifdef _DETAILNORMAL_ON
                {
                    float camDist = length(GetCurrentViewPosition() - IN.positionWS);
                    half  fade = 1.0h - saturate((camDist - _DetailFadeStart)
                                 / max(_DetailFadeEnd - _DetailFadeStart, 0.001));
                    if (fade > 0.0h)
                    {
                        float2 duv = TRANSFORM_TEX(IN.uv, _DetailNormalMap);
                        half3 dn = UnpackNormalScale(
                            SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, duv),
                            _DetailNormalStrength * fade);
                        normalTS = BlendNormal(normalTS, dn);
                    }
                }
            #endif

                half occlusion = 1.0h;
            #ifdef _OCCLUSIONMAP
                half rawOcc = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).g;
                occlusion = LerpWhiteTo(rawOcc, _OcclusionStrength);
            #endif

                half metallic   = _Metallic;
                half smoothness = _Smoothness;
            #ifdef _METALLICSPECGLOSSMAP
                half4 mask = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv);
                metallic   = mask.r * _Metallic;
                smoothness = mask.a * _Smoothness;
            #endif

                half3 emission = half3(0, 0, 0);
            #ifdef _EMISSION
                emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv).rgb
                         * _EmissionColor.rgb;
            #endif

                // World normal, needed by the weather gates before lighting runs.
                float sgn = IN.tangentWS.w;
                float3 bitangent = sgn * cross(IN.normalWS.xyz, IN.tangentWS.xyz);
                half3x3 tbn = half3x3(IN.tangentWS.xyz, bitangent.xyz, IN.normalWS.xyz);
                half3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tbn));

            #ifdef _WEATHER_ON
                // Wetness first, then snow. Order matters: snow sits on top of a wet
                // rock, and a thawing pack is what makes snow glossy, so snow reads
                // the wetness value rather than being darkened by it.
                half wetness = UCWetnessAt(normalWS);
                UCApplyWetness(albedo, smoothness, wetness, _WetDarkening);

                half snow = UCSnowAt(IN.positionWS, normalWS, _SnowAccumulation);
                UCApplySnow(albedo, smoothness, metallic, snow, wetness);
            #else
                half snow = 0.0h;
            #endif

            #ifdef _LICHEN_ON
                half lichen = UCRockLichen(normalWS, snow);
                albedo = lerp(albedo, albedo * _LichenColor.rgb, lichen);
                // Lichen is a dry crust: it dulls the stone it grows on.
                smoothness = lerp(smoothness, min(smoothness, 0.12h), lichen);
            #endif

                InputData inputData = (InputData)0;
                inputData.positionWS      = IN.positionWS;
                inputData.positionCS      = IN.positionCS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.fogCoord        = InitializeInputDataFog(float4(IN.positionWS, 1.0), IN.fogFactorAndVertexLight.x);
                inputData.vertexLighting  = IN.fogFactorAndVertexLight.yzw;

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = IN.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
            #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
            #endif

            #if defined(DYNAMICLIGHTMAP_ON)
                inputData.bakedGI = SAMPLE_GI(IN.staticLightmapUV, IN.dynamicLightmapUV,
                                              IN.vertexSH, normalWS);
            #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                inputData.bakedGI = SAMPLE_GI(IN.vertexSH,
                                              GetAbsolutePositionWS(IN.positionWS),
                                              normalWS, inputData.viewDirectionWS,
                                              IN.positionCS.xy, IN.probeOcclusion,
                                              inputData.shadowMask);
            #else
                inputData.bakedGI = SAMPLE_GI(IN.staticLightmapUV, IN.vertexSH, normalWS);
            #endif
                // Props read the same grid terrain, vegetation and water do, so a boulder is
                // lit by the hollow it sits in and not by a flat sky - the one ambient that
                // keeps a rock from floating brighter than the ground it rests on. No grid
                // bound yet, and the engine ambient sampled above stands unchanged.
                inputData.bakedGI = UCGridAmbientOr(inputData.bakedGI, IN.positionWS, normalWS);

                inputData.shadowMask = SAMPLE_SHADOWMASK(IN.staticLightmapUV);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);

            #if defined(_SCREEN_SPACE_OCCLUSION) && !defined(_SURFACE_TYPE_TRANSPARENT)
                AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(inputData.normalizedScreenSpaceUV);
                occlusion = min(occlusion, aoFactor.indirectAmbientOcclusion);
            #endif

                SurfaceData surface = (SurfaceData)0;
                surface.albedo     = albedo;
                surface.metallic   = saturate(metallic);
                surface.specular   = half3(0, 0, 0);
                surface.smoothness = saturate(smoothness);
                surface.occlusion  = occlusion;
                surface.emission   = emission;
                surface.alpha      = 1.0h;   // opaque; alpha is only ever a clip mask here
                surface.clearCoatMask       = 0.0h;
                surface.clearCoatSmoothness = 0.0h;
                surface.normalTS   = normalTS;

            #if defined(_DBUFFER_MRT1) || defined(_DBUFFER_MRT2) || defined(_DBUFFER_MRT3)
                // Decals land on rock and ground more than on anything else -
                // footprints, scorch, moss patches - so this pass accepts them.
                ApplyDecalToSurfaceData(IN.positionCS, surface, inputData);
            #endif

                half4 color = UniversalFragmentPBR(inputData, surface);

            #ifdef _WEATHER_ON
                // S-81: the ray-free wet-surface sky/grid reflection reaches props too, the same
                // UCMobileEnvironmentReflection the phone path shows. It is weighted to nothing
                // when dry (Fresnel x wetness) so it only ever adds the gloss of a wet rock and
                // never double-counts URP's own probe specular; the tier gate keeps it inert
                // until S-82's ladder turns it on. Added before the medium below, since a
                // reflection is part of the surface's radiance, not something the water tints.
                color.rgb += UCEnvReflectionContribution(IN.positionWS, normalWS,
                    inputData.viewDirectionWS, 1.0h - saturate(smoothness), wetness, 0.0,
                    surface.occlusion, normalWS);
            #endif

                // Absorption last: it is the medium between the eye and the surface,
                // so it must apply after the surface is lit, not to its albedo.
                color.rgb = UCApplyUnderwater(color.rgb, IN.positionWS);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return half4(color.rgb, 1.0h);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        //  Shadows. _CASTING_PUNCTUAL_LIGHT_SHADOW is what makes a point or spot
        //  light's shadow leave from the right place; without it the bias is
        //  computed against the directional light and a lantern throws a rock's
        //  shadow from entirely the wrong direction.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.0

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // URP sets these two from the shadow pass on the C++ side. They are
            // normally declared by ShadowCasterPass.hlsl, which this pass does not
            // include because it needs its own vertex stage - so they have to be
            // declared here or the bias below has nothing to work with.
            // UC_VegetationLit, UC_WindDetail and UC_TerrainLitDepthPasses all
            // declare them the same way; this keeps the four identical.
            float3 _LightDirection;
            float3 _LightPosition;

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirWS));

            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif

                OUT.positionCS = positionCS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        //  Depth prepass.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma target 3.0

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        //  Depth + normals. Screen-space ambient occlusion and screen-space decals
        //  both read this buffer. The oct-encoded branch is required because the
        //  same prepass feeds URP's deferred path, where the normal buffer is
        //  packed rather than stored raw - writing a raw normal there corrupts
        //  every effect downstream of it.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma target 3.0

            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
            #ifdef _NORMALMAP
                half4  tangentWS  : TEXCOORD2;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = pos.positionCS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS   = nrm.normalWS;
            #ifdef _NORMALMAP
                OUT.tangentWS  = half4(nrm.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
            #endif
                return OUT;
            }

            void DepthNormalsFrag(Varyings IN, out half4 outNormalWS : SV_Target0)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);

                half3 normalWS = NormalizeNormalPerPixel(IN.normalWS);
            #ifdef _NORMALMAP
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _NormalStrength);
                float sgn = IN.tangentWS.w;
                float3 bitangent = sgn * cross(IN.normalWS.xyz, IN.tangentWS.xyz);
                half3x3 tbn = half3x3(IN.tangentWS.xyz, bitangent.xyz, IN.normalWS.xyz);
                normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tbn));
            #endif

            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remapped    = saturate(octNormalWS * 0.5 + 0.5);
                half3  packed      = PackFloat2To888(remapped);
                outNormalWS = half4(packed, 0.0);
            #else
                outNormalWS = half4(normalWS, 0.0);
            #endif
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        //  Meta. Rock and ground are the static geometry in a scene, so they are
        //  exactly what a lightmap bake needs to read albedo and emission from.
        //  Without this pass a baked scene treats every rock as black and loses
        //  all the bounce light a cliff face actually contributes.
        // ---------------------------------------------------------------------
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }

            Cull Off

            HLSLPROGRAM
            #pragma vertex   UniversalVertexMeta
            #pragma fragment RockFragMeta
            #pragma target 3.0

            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _METALLICSPECGLOSSMAP
            #pragma shader_feature EDITOR_VISUALIZATION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            #ifdef EDITOR_VISUALIZATION
                float2 vizUV        : TEXCOORD1;
                float4 lightCoord   : TEXCOORD2;
            #endif
            };

            Varyings UniversalVertexMeta(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = UnityMetaVertexPosition(IN.positionOS.xyz, IN.uv1, IN.uv2);
                OUT.uv         = TRANSFORM_TEX(IN.uv0, _BaseMap);
            #ifdef EDITOR_VISUALIZATION
                UnityEditorVizData(IN.positionOS.xyz, IN.uv0, IN.uv1, IN.uv2,
                                   OUT.vizUV, OUT.lightCoord);
            #endif
                return OUT;
            }

            half4 RockFragMeta(Varyings IN) : SV_Target
            {
                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                clip(baseSample.a * _BaseColor.a - _Cutoff);

                half metallic   = _Metallic;
                half smoothness = _Smoothness;
            #ifdef _METALLICSPECGLOSSMAP
                half4 mask = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, IN.uv);
                metallic   = mask.r * _Metallic;
                smoothness = mask.a * _Smoothness;
            #endif

                half3 emission = half3(0, 0, 0);
            #ifdef _EMISSION
                emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb
                         * _EmissionColor.rgb;
            #endif

                MetaInput meta = (MetaInput)0;
                meta.Albedo   = albedo;
                meta.Emission = emission;
            #ifdef EDITOR_VISUALIZATION
                meta.VizUV        = IN.vizUV;
                meta.LightCoord   = IN.lightCoord;
            #endif
                return UnityMetaFragment(meta);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
    CustomEditor "UnityEditor.Rendering.Universal.ShaderGUI.LitShader"
}
