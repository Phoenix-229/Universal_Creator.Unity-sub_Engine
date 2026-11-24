// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_ContactShadow.shader
//  Purpose: The passes ContactShadowFeature drives. Pass 0 ray-marches the depth
//           buffer a short way toward the sun and writes how occluded that march
//           was - the near-field contact darkening a cascade shadow map is too
//           coarse to draw. Pass 1 accumulates that raw occlusion across frames
//           (motion-vector reprojection + a neighbourhood-variance clamp + a
//           disocclusion reject, the shared S-80 home) so the dithered march stops
//           shimmering and a fast pan leaves no ghost trail. Pass 2 combines the
//           accumulated occlusion with URP's cascade shadow by MINIMUM, never
//           product, so a pixel already in cast shadow is not darkened twice, and
//           writes the result back under the screen-space shadow texture's own
//           name so it reaches the sun's direct term and nothing else.
// -----------------------------------------------------------------------------

Shader "Hidden/UniverseCreator/ContactShadow"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #pragma target 3.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "UC_TemporalReproject.hlsl"   // S-80: shared motion reprojection + variance clamp + reject

        // x = march length (m), y = 1/steps, z = steps, w = intensity (0..1).
        float4 _UCContactShadowParams;

        // Temporal accumulation, published by ContactShadowFeature each frame.
        float4x4 _UCContactPrevViewProj;  // last frame's view-projection, static reprojection fallback
        float    _UCContactHistoryValid;  // 0 the first frame / after a resize, 1 otherwise
        float    _UCContactTemporalWeight;// how much reprojected history to keep (0..0.98)

        // Pass-0 result, rebound as a global for the temporal pass.
        TEXTURE2D_X(_UCContactOcclusion);
        // Previous-frame accumulated result, bound for the temporal read.
        TEXTURE2D_X(_UCContactHistory);
        // Pass-1 result: rg = (accumulated occlusion, eye depth), rebound for the combine.
        TEXTURE2D_X(_UCContactAccum);
        // URP's cascade result, still bound from the Screen Space Shadows pass.
        TEXTURE2D_X(_ScreenSpaceShadowmapTexture);

        // Ordered 4x4 Bayer matrix. Dithering the march's START offset by this
        // spreads the 16-step banding into a fine pattern the temporal pass then
        // resolves - far cheaper than the extra steps that would smooth it directly.
        static const float kUCBayer4x4[16] =
        {
             0.0,  8.0,  2.0, 10.0,
            12.0,  4.0, 14.0,  6.0,
             3.0, 11.0,  1.0,  9.0,
            15.0,  7.0, 13.0,  5.0
        };
        float UCBayer(uint2 p)
        {
            uint idx = (p.y & 3u) * 4u + (p.x & 3u);
            return (kUCBayer4x4[idx] + 0.5) / 16.0;
        }

        // True where the depth buffer holds sky rather than a surface.
        bool UCIsSky(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth <= 0.0;
        #else
            return rawDepth >= 1.0;
        #endif
        }
        ENDHLSL

        Pass
        {
            Name "ContactMarch"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMarch

            half4 FragMarch(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                if (UCIsSky(rawDepth)) return half4(0, 0, 0, 1); // no surface -> no contact

                float3 wpos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 L = normalize(_MainLightPosition.xyz); // direction toward the sun

                float len      = _UCContactShadowParams.x;
                float invSteps = _UCContactShadowParams.y;
                int   steps    = (int)_UCContactShadowParams.z;
                float stepLen  = len * invSteps;

                // Dither the start offset so a fixed step count does not band.
                float dither = UCBayer((uint2)IN.positionCS.xy);
                float3 origin = wpos + L * (stepLen * dither);

                // A world-space slab: an occluder must sit in front of the marched
                // point but within this depth, or a far surface past a gap would read
                // as contact.
                float thickness = stepLen * 2.0 + 0.05;

                float occ = 0.0;
                [loop]
                for (int i = 1; i <= steps; i++)
                {
                    float3 sp = origin + L * (stepLen * i);
                    float4 clip = TransformWorldToHClip(sp);
                    if (clip.w <= 0.0) break;
                    float2 sUV = (clip.xy / clip.w) * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    sUV.y = 1.0 - sUV.y;
                #endif
                    if (any(sUV < 0.0) || any(sUV > 1.0)) break;

                    float sceneEye = LinearEyeDepth(SampleSceneDepth(sUV), _ZBufferParams);
                    float rayEye   = -TransformWorldToView(sp).z; // positive toward the scene
                    float diff = rayEye - sceneEye;                // >0: a surface is in front of the ray
                    if (diff > 0.01 && diff < thickness) { occ = 1.0; break; }
                }
                return half4(occ, 0, 0, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ContactTemporal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTemporal

            half4 FragTemporal(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                if (UCIsSky(rawDepth)) return half4(0, 0, 0, 1);
                float centerEye = LinearEyeDepth(rawDepth, _ZBufferParams);

                // Depth-aware 3x3 of the raw march. The weighted mean is the denoise
                // that hides the Bayer dither; the running moments give the variance
                // box (S-80) the reprojected history is clamped into below.
                float2 texel = _ScreenSize.zw; // (1/width, 1/height)
                float sum = 0.0, wsum = 0.0;
                float m1 = 0.0, m2 = 0.0; int cnt = 0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float2 o = uv + float2(x, y) * texel;
                    float occ = SAMPLE_TEXTURE2D_X(_UCContactOcclusion, sampler_LinearClamp, o).r;
                    float ne = LinearEyeDepth(SampleSceneDepth(o), _ZBufferParams);
                    float w = exp(-abs(ne - centerEye) * 4.0);
                    sum += occ * w; wsum += w;
                    m1 += occ; m2 += occ * occ; cnt++;
                }
                float current = wsum > 1e-4 ? sum / wsum : 0.0;
                float lo, hi;
                UCVarianceBox(m1, m2, cnt, lo, hi);   // shared scalar variance clamp box (S-80)

                // Reproject into last frame - motion vectors first (camera AND object motion),
                // the prev view-projection as the static fallback - reject a depth disocclusion,
                // then blend the CLAMPED history so a fast pan cannot ghost a contact shadow.
                float3 wpos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float accum = current;
                float2 puv;
                bool reproj = UCReprojectByMotion(uv, puv) || UCReprojectByViewProj(wpos, _UCContactPrevViewProj, puv);
                if (reproj && _UCContactHistoryValid > 0.5)
                {
                    half4 hist = SAMPLE_TEXTURE2D_X(_UCContactHistory, sampler_LinearClamp, puv);
                    if (UCHistoryDepthValid(hist.g, centerEye))     // not a disocclusion
                    {
                        float clamped = clamp(hist.r, lo, hi);      // variance clamp
                        accum = lerp(current, clamped, _UCContactTemporalWeight);
                    }
                }
                return half4(accum, centerEye, 0, 1);   // rg = (accumulated occlusion, eye depth)
            }
            ENDHLSL
        }

        Pass
        {
            Name "ContactCombine"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCombine

            half4 FragCombine(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float cascade = SAMPLE_TEXTURE2D_X(_ScreenSpaceShadowmapTexture, sampler_LinearClamp, uv).r;

                float rawDepth = SampleSceneDepth(uv);
                if (UCIsSky(rawDepth)) return half4(cascade, cascade, cascade, 1);

                // The temporally accumulated, dither-free occlusion.
                float occ = SAMPLE_TEXTURE2D_X(_UCContactAccum, sampler_LinearClamp, uv).r;

                float intensity = _UCContactShadowParams.w;
                float contactAtten = 1.0 - saturate(occ) * intensity;

                // The law S-49 asks for: minimum, not product. A pixel already dark in
                // the cascade stays exactly that dark; contact only adds where it was lit.
                return half4(min(cascade, contactAtten), 0, 0, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
