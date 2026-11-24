// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_SSR.shader
//  Purpose: Sharp screen-space reflections at half resolution - a wet road that mirrors the
//           building above it. A hierarchical-depth march per pixel with a roughness-aware ray
//           count, variance-clamped temporal accumulation, and a HARD per-ray step budget.
//
//  THREE PASSES
//  ------------
//    0  Trace              per pixel, the reflect vector is marched in screen space with a
//                          coarse-to-fine (hierarchical-depth) step under the step budget. A
//                          HIT contributes the lit colour at the hit; a MISS - off-screen, no
//                          hit, or budget exhausted - contributes the grid radiance along the
//                          reflect direction, never black (the S-78 ordering layers the
//                          height-field trace and the analytic sky in front of the grid).
//    1  Resolve+Temporal   the half-res trace blended with the reprojected history under a
//                          variance clamp (history clamped to the neighbourhood mean +/- its
//                          deviation); a depth mismatch on reprojection rejects it outright.
//    2  Composite          the accumulated reflection, upsampled and ADDED to the lit colour,
//                          weighted by the Fresnel-and-wetness term so only wet/smooth grazing
//                          surfaces take a reflection.
//
//  Additive (Blend One One) means a missed pixel adds its fallback radiance, not a hole, and a
//  surface the pass skips (too rough) adds exactly zero - so below Ultra, where the feature never
//  runs, the image is byte-identical to before SSR existed.
// -----------------------------------------------------------------------------

Shader "Hidden/UniverseCreator/SSR"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
        #include "UC_Lighting.hlsl"
        #include "UC_ReflectionFallback.hlsl"
        #include "UC_SurfaceReflectance.hlsl"   // one GPU home for S-76's water Fresnel (UCFresnelWater)
        #include "UC_TemporalReproject.hlsl"    // S-80: shared motion reprojection + variance clamp + reject

        // Published by ScreenSpaceReflectionFeature each frame.
        float4   _UCSSRParams;        // x=stepBudget, y=intensity, z=temporalWeight, w=maxRoughness
        float4   _UCSSRHalfTexel;     // xy = 1/halfSize, zw = halfSize (px)
        float4x4 _UCSSRPrevViewProj;  // last frame's view-projection, for reprojection
        float    _UCSSRHistoryValid;  // 0 the first frame / after a resize, 1 otherwise

        TEXTURE2D_X(_UCSSRTrace);     // pass 0 output  (rgb=reflected radiance, a=eye depth)
        TEXTURE2D_X(_UCSSRHistory);   // previous-frame accumulated result
        TEXTURE2D_X(_UCSSRResult);    // pass 1 output = this frame's accumulated result
        TEXTURE2D_X(_UCSSRSurface);   // material pass: r=roughness, g=wet/puddle coverage; unregistered surfaces clear to zero

        // Reversed-Z aware sky test: the far plane reads 0 (reversed) or 1 (classic).
        bool UCSkyDepth(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth <= 0.0;
        #else
            return rawDepth >= 1.0;
        #endif
        }

        // UC midday repair: URP normals contain no material smoothness. Point sampling keeps
        // a dry pixel ineligible even beside a wet surface or a reprojected bright reflection.
        float2 UCSceneReflectionSurface(float2 uv)
        {
            return saturate(SAMPLE_TEXTURE2D_X(_UCSSRSurface, sampler_PointClamp, uv).rg);
        }

        // The non-black fallback radiance for a missed ray, in the S-78 order: height-field
        // trace against the terrain, then the irradiance grid, then the analytic sky. The whole
        // ordering lives in UC_ReflectionFallback.hlsl so there is one home for it.
        float3 UCReflectionFallback(float3 worldPos, float3 reflectDir)
        {
            return UCReflectionFallbackOrdered(worldPos, reflectDir);
        }

        // Schlick Fresnel for a water-grade F0 lives in UC_SurfaceReflectance.hlsl (UCFresnelWater),
        // the same S-76 twin the mobile path uses - one home, so the wet term reads identically
        // whether a puddle is drawn by the SSR composite or by the ray-free mobile reflection.
        // Deterministic per-pixel rotation (not random, per the real-world-physics mandate):
        // the same pixel yields the same value every frame, so the ray spread is stable and only
        // the temporal pass moves it. Interleaved-gradient hash of the pixel coordinate.
        float UCPixelRotation(uint2 pix)
        {
            return frac(52.9829189 * frac(dot(float2(pix), float2(0.06711056, 0.00583715))));
        }
        ENDHLSL

        // ===== Pass 0 : TRACE (half res) ==========================================
        Pass
        {
            Name "SSRTrace"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTrace
            #pragma multi_compile _ UC_IRRADIANCE_ATLAS

            // Roughness-aware ray count: a mirror gets one sharp ray, a satin surface a small
            // spread. Capped so cost stays bounded regardless of surface.
            static const int UC_SSR_MAX_RAYS = 4;

            half4 FragTrace(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float rawD = SampleSceneDepth(uv);
                if (UCSkyDepth(rawD)) return half4(0.0, 0.0, 0.0, 0.0);   // sky: nothing to reflect off

                float3 wpos = ComputeWorldSpacePosition(uv, rawD, UNITY_MATRIX_I_VP);
                float3 n    = normalize(SampleSceneNormals(uv));
                float  eye  = LinearEyeDepth(rawD, _ZBufferParams);

                float maxRough = _UCSSRParams.w;
                float2 surface = UCSceneReflectionSurface(uv);
                float rough    = surface.r;
                // Too rough to hold a sharp reflection -> add nothing; grid+sky already lit it.
                if (surface.g <= 0.0 || rough > maxRough) return half4(0.0, 0.0, 0.0, eye);

                float3 camPos = GetCameraPositionWS();
                float3 V = normalize(camPos - wpos);
                float3 baseR = reflect(-V, n);

                int   budget   = (int)_UCSSRParams.x;                 // HARD per-ray step ceiling
                int   rayCount = (int)round(lerp(1.0, (float)UC_SSR_MAX_RAYS,
                                                 saturate(rough / max(maxRough, 1e-3))));
                rayCount = clamp(rayCount, 1, UC_SSR_MAX_RAYS);

                uint2 pix = (uint2)(uv * _UCSSRHalfTexel.zw);
                float rot = UCPixelRotation(pix);

                // Tangent frame for spreading the ray set within the reflection lobe.
                float3 up = abs(baseR.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
                float3 tx = normalize(cross(up, baseR));
                float3 ty = cross(baseR, tx);
                float  spread = rough * 0.25;                          // lobe half-width, roughness-scaled

                float3 acc = 0.0;
                [loop]
                for (int r = 0; r < rayCount; r++)
                {
                    // Golden-angle spread, deterministically rotated per pixel.
                    float ang = 6.2831853 * frac(rot + r * 0.61803399);
                    float rad = spread * ((r + 0.5) / rayCount);
                    float3 dir = normalize(baseR + (tx * cos(ang) + ty * sin(ang)) * rad);

                    // Hierarchical-depth march: a coarse stride that DOUBLES each step (so a far
                    // hit is reached in log steps), then one binary refinement across the crossing.
                    // The step counter is the hard budget: the loop cannot exceed it whatever it finds.
                    float  stride = 0.25;                              // metres, coarse start
                    float  t = stride;
                    float  prevT = 0.0;
                    float  hit = 0.0;
                    float3 hitCol = 0.0;
                    float2 hitUV = 0.0;
                    [loop]
                    for (int s = 0; s < budget; s++)
                    {
                        float3 sp = wpos + n * 0.02 + dir * t;
                        float4 clip = TransformWorldToHClip(sp);
                        if (clip.w <= 0.0) break;                      // behind the eye -> miss
                        float2 suv = clip.xy / clip.w * 0.5 + 0.5;
                    #if UNITY_UV_STARTS_AT_TOP
                        suv.y = 1.0 - suv.y;
                    #endif
                        if (any(suv < 0.0) || any(suv > 1.0)) break;   // off-screen -> miss (fallback)

                        float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                        float rayEye   = -TransformWorldToView(sp).z;
                        float diff     = rayEye - sceneEye;
                        float thick    = stride * 2.0 + 0.05;
                        if (diff > 0.02 && diff < thick)
                        {
                            // Binary refine once between prevT and t for a crisp hit point.
                            float lo = prevT, hi = t;
                            [unroll]
                            for (int k = 0; k < 4; k++)
                            {
                                float mid = 0.5 * (lo + hi);
                                float3 mp = wpos + n * 0.02 + dir * mid;
                                float4 mc = TransformWorldToHClip(mp);
                                float2 muv = mc.xy / mc.w * 0.5 + 0.5;
                            #if UNITY_UV_STARTS_AT_TOP
                                muv.y = 1.0 - muv.y;
                            #endif
                                float me = LinearEyeDepth(SampleSceneDepth(muv), _ZBufferParams);
                                float rd = -TransformWorldToView(mp).z;
                                if (rd - me > 0.02) hi = mid; else lo = mid;
                                hitUV = muv;
                            }
                            hit = 1.0;
                            hitCol = SampleSceneColor(hitUV);          // the lit reflected surface
                            break;
                        }
                        prevT = t;
                        stride *= 2.0;                                 // hierarchical: coarsen with distance
                        t += stride;
                    }

                    // Hit -> the reflected surface colour; miss -> the grid fallback (never black).
                    acc += hit > 0.5 ? hitCol : UCReflectionFallback(wpos, dir);
                }

                float3 reflected = acc / max(1, rayCount);
                return half4(reflected, eye);
            }
            ENDHLSL
        }

        // ===== Pass 1 : RESOLVE + TEMPORAL (half res) =============================
        Pass
        {
            Name "SSRResolveTemporal"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragResolveTemporal

            half4 FragResolveTemporal(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float centerRaw = SampleSceneDepth(uv);
                if (UCSkyDepth(centerRaw)) return half4(0.0, 0.0, 0.0, 0.0);
                float centerEye = LinearEyeDepth(centerRaw, _ZBufferParams);

                // 3x3 neighbourhood of the raw trace: its mean and second moment give the AABB
                // the history is clamped into (the variance clamp that stops a moving reflection
                // ghosting). Depth-aware weights keep the neighbourhood on one surface.
                float2 texel = _UCSSRHalfTexel.xy;
                float3 m1 = 0.0, m2 = 0.0, sum = 0.0;
                float  wsum = 0.0; int cnt = 0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float2 o = uv + float2(x, y) * texel;
                    float3 t = SAMPLE_TEXTURE2D_X(_UCSSRTrace, sampler_LinearClamp, o).rgb;
                    float ne = LinearEyeDepth(SampleSceneDepth(o), _ZBufferParams);
                    float w = exp(-abs(ne - centerEye) * 4.0);
                    sum += t * w; wsum += w;
                    m1 += t; m2 += t * t; cnt++;
                }
                float3 current = wsum > 1e-4 ? sum / wsum : 0.0;
                float3 lo, hi;
                UCVarianceBox(m1, m2, cnt, lo, hi);   // shared variance clamp box (S-80)

                // Reproject into last frame - motion vectors first (camera AND object motion), the
                // prev view-projection as the static fallback - then reject a depth disocclusion and
                // blend the CLAMPED history so it steadies the result without ghosting a moving object.
                float3 wpos = ComputeWorldSpacePosition(uv, centerRaw, UNITY_MATRIX_I_VP);
                float3 accum = current;
                float2 puv;
                bool reproj = UCReprojectByMotion(uv, puv) || UCReprojectByViewProj(wpos, _UCSSRPrevViewProj, puv);
                if (reproj && _UCSSRHistoryValid > 0.5)
                {
                    half4 hist = SAMPLE_TEXTURE2D_X(_UCSSRHistory, sampler_LinearClamp, puv);
                    if (UCHistoryDepthValid(hist.a, centerEye))
                    {
                        float3 clamped = clamp(hist.rgb, lo, hi);   // variance clamp
                        accum = lerp(current, clamped, _UCSSRParams.z);
                    }
                }
                return half4(accum, centerEye);
            }
            ENDHLSL
        }

        // ===== Pass 2 : COMPOSITE (full res, additive) ============================
        Pass
        {
            Name "SSRComposite"
            Blend One One                 // add the reflection onto the lit colour; 0 = no-op
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            half4 FragComposite(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float rawD = SampleSceneDepth(uv);
                if (UCSkyDepth(rawD)) return half4(0.0, 0.0, 0.0, 0.0);   // adds nothing to sky

                // UC midday repair: gate at full resolution too; half-res filtering/history
                // must never spill a wet reflection onto the neighbouring dry material.
                float2 surface = UCSceneReflectionSurface(uv);
                if (surface.g <= 0.0 || surface.r > _UCSSRParams.w) return half4(0.0, 0.0, 0.0, 0.0);

                // Fresnel weight: grazing views reflect strongly, head-on barely. The reflection
                // that was skipped (too rough) is zero here, so the composite is a pass-through.
                float3 n = normalize(SampleSceneNormals(uv));
                float3 wpos = ComputeWorldSpacePosition(uv, rawD, UNITY_MATRIX_I_VP);
                float3 V = normalize(GetCameraPositionWS() - wpos);
                float  fresnel = UCFresnelWater(saturate(dot(n, V)));

                float3 refl = SAMPLE_TEXTURE2D_X(_UCSSRResult, sampler_LinearClamp, uv).rgb;
                return half4(refl * (_UCSSRParams.y * fresnel * surface.g), 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
