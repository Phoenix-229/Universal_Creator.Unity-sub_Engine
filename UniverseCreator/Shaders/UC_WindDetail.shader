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
//
//  WHAT WAS WRONG WITH IT, AND IS NOW FIXED
//
//  This shader was written against an older Universal Render Pipeline and three
//  things had quietly stopped being true on URP 17:
//
//    1. It declared none of the clustered-lighting keywords, so on Unity 6's
//       default Forward+ renderer it received the sun and nothing else. A
//       campfire, a lantern, a window - anything that is not the directional
//       light - lit the ground beside the grass and left the grass itself dark.
//       Note that URP renamed the keyword: _FORWARD_PLUS became
//       _CLUSTER_LIGHT_LOOP, and the old spelling compiles quietly to no lights
//       at all rather than erroring, which is why this went unnoticed.
//
//    2. It had no DepthNormals pass. Screen-space ambient occlusion and decals
//       both read the normal buffer, and every blade of grass punched a hole in
//       it, so grass neither occluded nor received either effect.
//
//    3. Its shadow pass ignored punctual lights, so grass cast a directional
//       shadow correctly and cast a point or spot light's shadow from the wrong
//       place entirely.
//
//  While it was open it also gained the season and weather link it never had:
//  grass now darkens in rain and holds snow, from the same globals the rest of
//  the world reads. Those read zero with no weather manager, so a scene without
//  one looks exactly as it did before.
//
//  LOD_FADE_CROSSFADE is deliberately NOT declared. Terrain detail instances are
//  not in LODGroups, so it would only double the variant count on the platform
//  this shader exists to be cheap on. Mesh vegetation that does need crossfade
//  belongs on UC_VegetationLit, which declares it.
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
        // Cure is tip-first, not uniform. A grass leaf grows from a basal
        // meristem, so the tip is the oldest tissue on the blade and also the
        // furthest point along the transpiration path - it senesces first on age
        // and dries first under stress. 0 spreads the cure flat over the blade,
        // which is what this shader used to do and what never read as a real
        // sward; 1 keeps green in the crown while the tips go straw.
        _DryGradient   ("Dry Runs Tip First", Range(0.0, 1.0)) = 1.0
        _AmbientWrap   ("Ambient Wrap", Range(0.0, 1.0)) = 0.5
        _Translucency  ("Translucency", Range(0.0, 1.0)) = 0.35
        _Smoothness    ("Smoothness", Range(0.0, 1.0)) = 0.15

        [Header(Season and Weather)]
        // _DryBlend above is the authored, fixed amount of cure in the sward.
        // This is the part the world drives on top of it, so a meadow can be
        // green in June and straw in September without anyone editing a material.
        [Toggle(_SEASONTINT_ON)] _SeasonTintEnable ("Enable Seasonal Colour", Float) = 0
        _SpringColor ("Spring Tint", Color) = (0.72, 0.86, 0.46, 1)
        _SummerColor ("Summer Tint", Color) = (1.00, 1.00, 1.00, 1)
        _AutumnColor ("Autumn Tint", Color) = (0.84, 0.72, 0.40, 1)
        _WinterColor ("Winter Tint", Color) = (0.62, 0.58, 0.46, 1)
        _SeasonTintStrength ("Seasonal Colour Strength", Range(0.0, 1.0)) = 1.0

        // What a season does to a graminoid is CURE it, not shed it - a meadow in
        // September is straw standing up, not bare ground. So the same leaf-off
        // figure UC_VegetationLit uses to thin a canopy drives the cure here
        // instead. Held under the same name on purpose: it is the same number off
        // the same ecological envelope, and two names for it would drift.
        _LeafOffFraction ("Cures To Straw In Winter", Range(0.0, 1.0)) = 0.85

        // ONE OWNER PER EFFECT - now settled in code, not by hand.
        //
        // ScatterApplier writes the season, rain and snow chain into each
        // DetailPrototype's healthyColor and dryColor, and Unity's detail renderer
        // delivers that to the shader as vertex colour - which this shader
        // multiplies into albedo. Both sides doing it means rained on twice, black
        // in a storm.
        //
        // The tie-break no longer asks the author to zero anything: ScatterApplier
        // checks whether this material is on our detail shader with _SEASONTINT_ON
        // live, and where it is, it stops writing the seasonal colours and hands the
        // season to the shader. Where it is not - a vendor material, or the keyword
        // off - the CPU keeps it. Turning _SEASONTINT_ON on is therefore a HANDOVER,
        // not a second copy.
        //
        // The shader is the better owner where it can be: per pixel, hue rotated
        // rather than multiplied, cured tip-first, and gated on the surface normal
        // so snow lands on what faces the sky instead of coating the plant evenly.
        // A DetailPrototype colour is a flat multiply and can only ever darken.
        //
        // It still defaults off, so an existing sward keeps the CPU path until
        // someone opts it in.
        _WetDarkening ("Wet Darkening", Range(0.0, 1.0)) = 0.5
        // Ground cover buries readily - it is the first thing a snowfall hides,
        // which is why this defaults high where a tree canopy's defaults low.
        _SnowAccumulation ("Snow Accumulation", Range(0.0, 1.0)) = 1.0

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
        #include "UC_Season.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half4  _DryColor;
            half4  _SpringColor;
            half4  _SummerColor;
            half4  _AutumnColor;
            half4  _WinterColor;
            half   _Cutoff;
            half   _WindResponse;
            half   _BendHeight;
            half   _Flutter;
            half   _PivotHeight;
            half   _VegetationClass;
            half   _TouchResponse;
            half   _DryBlend;
            half   _DryGradient;
            half   _LeafOffFraction;
            half   _AmbientWrap;
            half   _Translucency;
            half   _Smoothness;
            half   _SeasonTintStrength;
            half   _WetDarkening;
            half   _SnowAccumulation;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // 0 at the root, 1 at the tip: object-space Y over the authored mesh
        // height, so it is a real distance in metres up the plant rather than a
        // texture coordinate that depends on how the card was laid out.
        //
        // ONE OWNER. The wind bend and the cure gradient both need to know where
        // the tip is. If they measured it differently the blade would bend from
        // one place and cure from another.
        float UCDetailHeightFrac(float3 positionOS)
        {
            return saturate(positionOS.y / max(0.001, _PivotHeight));
        }

        // One displacement for all three passes. Shared and not copied because a
        // shadow pass that bends by even slightly less detaches the shadow from
        // the blade and slides it across the ground as the wind moves.
        float3 UCDetailDisplace(float3 positionWS, float3 positionOS)
        {
            // _BendHeight decides how much of the plant is anchored - at 0 the
            // whole thing leans rigidly, at 1 only the tip travels. That remap
            // belongs to the bend, which is why the cure reads the raw fraction.
            float heightFrac = lerp(1.0, UCDetailHeightFrac(positionOS), _BendHeight);

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

            #pragma shader_feature_local _SEASONTINT_ON

            // URP 17 keyword set. Each line below buys a behaviour this shader was
            // silently going without; nothing is here for completeness.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            // Deliberately absent, each for a reason:
            //   _ADDITIONAL_LIGHTS_VERTEX - a grass blade has a handful of
            //     vertices, so per-vertex lighting on it is a gradient across the
            //     whole plant. Interpolating that is worse than not having it.
            //   EVALUATE_SH_MIXED / EVALUATE_SH_VERTEX - those switch URP's
            //     SAMPLE_GI path, and this shader takes ambient straight from
            //     SampleSH rather than through SAMPLE_GI. Declaring them would
            //     multiply the variant count and change nothing.
            //   _REFLECTION_PROBE_* - there is no environment specular here. Wet
            //     sheen comes off the direct lights, which is where the sheen a
            //     player actually reads on wet grass comes from.

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "UC_Underwater.hlsl"

            /// Direct specular only, no environment term.
            ///
            /// A wet blade is a dielectric under a water film, and water's normal-
            /// incidence reflectance is about 0.02-0.04. That is the whole budget
            /// this term is allowed to spend, which is why the sheen shows up as a
            /// glint near grazing angles and never as a shiny surface.
            half3 UCDetailSpecular(half3 lightColor, half3 lightDir, half3 normalWS,
                                   half3 viewDirWS, half smoothness, half f0)
            {
                half3 halfVec = SafeNormalize(lightDir + viewDirWS);
                half  nh = saturate(dot(normalWS, halfVec));
                half  power = exp2(10.0h * smoothness + 1.0h);
                // (n + 8) / 8pi keeps total reflected energy roughly constant as the
                // lobe tightens, so raising smoothness sharpens the highlight
                // instead of adding light to the scene.
                half  norm = (power + 8.0h) * 0.0397887h;
                return lightColor * (f0 * norm * pow(nh, power));
            }

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
                // xyz = world position, w = 0 at the root to 1 at the tip.
                // Packed into the spare component rather than taking its own
                // interpolator: this shader draws tens of thousands of instances
                // on hardware where an interpolator slot is a real cost.
                float4 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  color      : TEXCOORD3;
                float  fogCoord   : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings WindDetailVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCDetailDisplace(positionWS, IN.positionOS.xyz);

                OUT.positionWS = float4(positionWS, UCDetailHeightFrac(IN.positionOS.xyz));
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
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half4 albedo = tex * _BaseColor * IN.color;

                clip(albedo.a - _Cutoff);

                // How far this sward has cured. The authored figure is its
                // baseline - a material may be written already strawy - and the
                // season can only take it further, so this is a max and not a sum.
                // Adding them would cure an autumn meadow twice over.
                half cureDemand = _DryBlend;

#ifdef _SEASONTINT_ON
                // A graminoid does not shed, it CURES: a meadow in September is
                // straw standing up, not bare ground. So the envelope's leaf-off
                // figure drives the straw here, where on UC_VegetationLit the same
                // number thins a canopy. Deciduous passes as 1 because any sward
                // with a non-zero leaf-off cures - there is no evergreen grass
                // exemption to apply, and the authored 0 is the way to opt out.
                cureDemand = max(cureDemand,
                                 half(1.0 - UCLeafOnFraction(IN.positionWS.xyz,
                                                             _LeafOffFraction, 1.0h)));
#endif

                // Tip first. positionWS.w is 0 at the root and 1 at the tip.
                half cure = UCCureAlongBlade(cureDemand, half(IN.positionWS.w), _DryGradient);

#ifdef _SEASONTINT_ON
                // Curing is a PIGMENT change, not a dimming. A sward going over to
                // straw - including the extra cure the altitude term above adds as
                // the ground climbs - replaces its chlorophyll with the pale
                // carotenoid residue; it does not merely lose light. Multiplying the
                // green albedo by the straw _DryColor only ever darkens it toward
                // dirty green, which is the "grass reads yellow / dried-out up the
                // hill" bug: the hill raises cure, and a darken is all a multiply can
                // do. So the cure is REPIGMENTED toward _DryColor through the same
                // single owner the season tint uses just below - one green->gold
                // operator, never two - and UCRepigment keeps the blade's own value,
                // so a cured sward reads as pale gold rather than muddy green.
                albedo.rgb = UCRepigment(albedo.rgb, _DryColor.rgb, cure);

                // Then the world's position in the year, on top of it. Repigment
                // rather than multiply: a multiply cannot turn green straw amber,
                // it can only darken it. See UCRepigment in UC_Season.hlsl.
                half3 tint = UCSeasonTint(_SpringColor.rgb, _SummerColor.rgb,
                                          _AutumnColor.rgb, _WinterColor.rgb);
                albedo.rgb = UCRepigment(albedo.rgb, tint, _SeasonTintStrength);
#else
                // Season off: only the authored _DryBlend baseline cures - the
                // altitude term above is season-gated, so nothing height-driven
                // reaches here - and it stays the cheap multiply it always was. No
                // HSV cost on a mid-range phone for a material with the tint off (the
                // case the perf floor needs kept cheap), and a sward authored before
                // this fix does not shift: a static authored strawiness has no
                // green->gold climb to show, so the darken is the shipped behaviour.
                albedo.rgb = lerp(albedo.rgb, albedo.rgb * _DryColor.rgb, cure);
#endif

                half3 normalWS = normalize(IN.normalWS);
                half3 viewDirWS = half3(GetWorldSpaceNormalizeViewDir(IN.positionWS.xyz));

                half smoothness = _Smoothness;

                // Wet before snow, because a surface gets wet and snow then settles
                // on top of it. The other order darkens the snow and leaves the
                // blade under it dry, which is backwards.
                half wetness = UCWetnessAt(normalWS);
                UCApplyWetness(albedo.rgb, smoothness, wetness, _WetDarkening);

                half snowMetallic = 0.0h;   // ground cover is never metallic; discarded
                half snow = UCSnowAt(IN.positionWS.xyz, normalWS, _SnowAccumulation);
                UCApplySnow(albedo.rgb, smoothness, snowMetallic, snow, wetness);

                // Water's Fresnel F0. Dry vegetation reflects a little too, and this
                // is the only place the two differ enough to matter.
                half f0 = lerp(0.02h, 0.04h, saturate(wetness));

                // LIGHT_LOOP_BEGIN reads a local literally named inputData, so this
                // has to exist even though only two of its fields are used.
                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS.xyz;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);

                // Detail instances are not lightmapped, so there is no shadowmask to
                // sample and an unshadowed mask is the honest value.
                half4 shadowMask = half4(1, 1, 1, 1);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS.xyz);
                Light mainLight = GetMainLight(shadowCoord, IN.positionWS.xyz, shadowMask);

                half3 ambient = SampleSH(normalWS);

#if defined(_SCREEN_SPACE_OCCLUSION)
                // This only does anything now that the shader has a DepthNormals
                // pass to write into the buffer SSAO reads.
                AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(inputData.normalizedScreenSpaceUV);
                ambient *= aoFactor.indirectAmbientOcclusion;
#endif

                half3 diffuseLight = half3(0, 0, 0);
                half3 specularLight = half3(0, 0, 0);

                // --- main light ----------------------------------------------
                {
                    half3 energy = mainLight.color * mainLight.shadowAttenuation
                                 * mainLight.distanceAttenuation;
                    half ndotl = dot(normalWS, mainLight.direction);

                    // Wrapped diffuse. Grass lit as a hard surface goes black on
                    // every blade facing away; wrapping keeps the far side readable,
                    // which is closer to how a thin translucent blade behaves.
                    half wrapped = saturate((ndotl + _AmbientWrap) / (1.0h + _AmbientWrap));

                    // Light that passed through the blade from behind.
                    half back = saturate(-ndotl) * _Translucency;

                    diffuseLight += energy * (wrapped + back);
                    specularLight += UCDetailSpecular(energy, mainLight.direction,
                                                      normalWS, viewDirWS, smoothness, f0);
                }

                // --- every other light ----------------------------------------
                // This is the fix. On URP 17's default clustered renderer the loop
                // below is what a campfire, a lantern or a lit window reaches the
                // grass through; without _CLUSTER_LIGHT_LOOP declared it did not
                // run at all and the grass stayed lit by the sun alone.
#if defined(_ADDITIONAL_LIGHTS)
                uint lightCount = GetAdditionalLightsCount();

                LIGHT_LOOP_BEGIN(lightCount)
                    Light light = GetAdditionalLight(lightIndex, IN.positionWS.xyz, shadowMask);

                    half3 energy = light.color * light.shadowAttenuation
                                 * light.distanceAttenuation;
                    half ndotl = dot(normalWS, light.direction);

                    half wrapped = saturate((ndotl + _AmbientWrap) / (1.0h + _AmbientWrap));
                    half back = saturate(-ndotl) * _Translucency;

                    diffuseLight += energy * (wrapped + back);
                    specularLight += UCDetailSpecular(energy, light.direction,
                                                      normalWS, viewDirWS, smoothness, f0);
                LIGHT_LOOP_END
#endif

#if defined(_SCREEN_SPACE_OCCLUSION)
                diffuseLight *= aoFactor.directAmbientOcclusion;
#endif

                half3 color = albedo.rgb * (diffuseLight + ambient) + specularLight;

                color = MixFog(color, IN.fogCoord);

                // Water plants are the main thing this shader draws below a
                // waterline, so they must be filtered by the same medium the seabed
                // is - a bright green weed against a blue-green bed is the tell that
                // the plants were composited in rather than growing there.
                color = UCApplyUnderwater(color, IN.positionWS.xyz);

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
            // Without this a point or spot light biases the blade along the
            // directional light's axis instead of along its own, and the grass
            // casts a shadow from a direction the light is not in.
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

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
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCDetailDisplace(positionWS, IN.positionOS.xyz);

                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
                float3 lightDirectionWS = _LightDirection;
#endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

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
            ColorMask R
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

        // ---------------------------------------------------------------------
        //  Depth normals. This pass did not exist, and its absence was visible:
        //  screen-space ambient occlusion and decals both read the normal buffer,
        //  and grass wrote nothing into it. So a field of grass neither darkened
        //  where it met the ground nor took a decal, while the bare soil either
        //  side of it did both - the grass read as stickers laid on the terrain.
        //
        //  Same displacement as the other three passes. A normal buffer built from
        //  undisplaced geometry would put the occlusion where the grass was before
        //  the wind moved it.
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
            #pragma multi_compile_instancing
            // The deferred renderer asks the depth-normals prepass for
            // octahedral-packed normals rather than raw world-space ones. Without
            // this the buffer is written in the wrong encoding and every blade of
            // grass feeds SSAO and decals a wrong normal. UC_RockLit and
            // UC_VegetationLit do the same thing the same way.
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                half3  normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCDetailDisplace(positionWS, IN.positionOS.xyz);

                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);

                half3 normalWS = NormalizeNormalPerPixel(IN.normalWS);

#if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remapped    = saturate(octNormalWS * 0.5 + 0.5);
                half3  packed      = PackFloat2To888(remapped);
                return half4(packed, 0.0);
#else
                return half4(normalWS, 0.0);
#endif
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
