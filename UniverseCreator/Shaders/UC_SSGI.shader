// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_SSGI.shader
//  Purpose: One screen-space indirect bounce, traced at half resolution, composited
//           as a DELTA over the irradiance grid so it only ever adds local colour
//           bleed where it has the on-screen information to - and changes nothing,
//           anywhere, when it does not.
//
//  THREE PASSES
//  ------------
//    0  Trace          per pixel, N cosine-weighted rays march screen space for a
//                      nearby lit surface. A ray that HITS contributes that surface's
//                      colour; a ray that MISSES contributes the grid value at this
//                      point (UCSampleIrradiance). The output is the average minus the
//                      grid baseline - a delta that is exactly zero where nothing hit.
//    1  Blur+Temporal  a depth-aware blur of the half-res delta, blended with the
//                      previous frame's result reprojected through last frame's
//                      view-projection; a depth mismatch rejects the stale sample.
//    2  Composite      the accumulated delta, upsampled and ADDED to the lit colour.
//
//  Additive over the grid (Blend One One) is the whole S-52 guarantee: where the delta
//  is zero the frame is untouched, so turning SSGI off never shifts overall brightness
//  or colour - it only removes the local refinement.
// -----------------------------------------------------------------------------

Shader "Hidden/UniverseCreator/SSGI"
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
        #include "UC_TemporalReproject.hlsl"   // S-80: shared motion reprojection + variance clamp + reject

        // Published by ScreenSpaceGIFeature each frame.
        float4   _UCSSGIParams;        // x=sampleCount, y=marchDistance(m), z=intensity, w=temporalWeight
        float4   _UCSSGIHalfTexel;     // xy = 1/halfSize, zw = halfSize (px)
        float4x4 _UCSSGIPrevViewProj;  // last frame's view-projection, for reprojection
        float    _UCSSGIHistoryValid;  // 0 the first frame / after a resize, 1 otherwise

        TEXTURE2D_X(_UCSSGITrace);     // pass 0 output  (rgb=delta, a=eye depth)
        TEXTURE2D_X(_UCSSGIHistory);   // previous-frame accumulated result
        TEXTURE2D_X(_UCSSGIResult);    // pass 1 output = this frame's accumulated result

        // Reversed-Z aware sky test: the far plane reads 0 (reversed) or 1 (classic).
        bool UCSkyDepth(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth <= 0.0;
        #else
            return rawDepth >= 1.0;
        #endif
        }

        // Deterministic, not random (per the real-world-physics mandate): the same pixel
        // yields the same rotation every frame, so the ray set is stable and only the
        // temporal pass moves it. Interleaved-gradient hash of the pixel coordinate.
        float UCPixelRotation(uint2 pix)
        {
            return frac(52.9829189 * frac(dot(float2(pix), float2(0.06711056, 0.00583715))));
        }

        // Hammersley low-discrepancy point - a fixed, evenly spread sequence, not a draw.
        float2 UCHammersley(uint i, uint n)
        {
            uint b = i;
            b = (b << 16u) | (b >> 16u);
            b = ((b & 0x55555555u) << 1u) | ((b & 0xAAAAAAAAu) >> 1u);
            b = ((b & 0x33333333u) << 2u) | ((b & 0xCCCCCCCCu) >> 2u);
            b = ((b & 0x0F0F0F0Fu) << 4u) | ((b & 0xF0F0F0F0u) >> 4u);
            b = ((b & 0x00FF00FFu) << 8u) | ((b & 0xFF00FF00u) >> 8u);
            return float2((i + 0.5) / n, float(b) * 2.3283064365386963e-10);
        }

        // Cosine-weighted hemisphere direction (z up). Cosine weighting means each sample
        // already carries its N.L, so the estimate is a plain average - no per-ray weight.
        float3 UCCosineHemisphere(float2 xi)
        {
            float phi = 6.2831853 * xi.x;
            float ct  = sqrt(saturate(1.0 - xi.y));
            float st  = sqrt(saturate(xi.y));
            return float3(cos(phi) * st, sin(phi) * st, ct);
        }
        ENDHLSL

        // ===== Pass 0 : TRACE (half res) ==========================================
        Pass
        {
            Name "SSGITrace"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTrace
            #pragma multi_compile _ UC_IRRADIANCE_ATLAS

            half4 FragTrace(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float rawD = SampleSceneDepth(uv);
                if (UCSkyDepth(rawD)) return half4(0.0, 0.0, 0.0, 0.0);  // sky: no bounce, no delta

                float3 wpos   = ComputeWorldSpacePosition(uv, rawD, UNITY_MATRIX_I_VP);
                float3 n      = normalize(SampleSceneNormals(uv));
                float  eye    = LinearEyeDepth(rawD, _ZBufferParams);

                // The value the base tier already lit this point with. Every missed ray
                // returns exactly this, so it cancels in the delta (S-51).
                float3 gridBaseline = UCSampleIrradiance(wpos, n);

                int    rayCount  = (int)_UCSSGIParams.x;
                float  marchDist = _UCSSGIParams.y;
                const int STEPS  = 8;
                float  stepLen   = marchDist / STEPS;
                float  thickness = stepLen * 3.0 + 0.1;   // hit acceptance band, in metres

                // Per-pixel deterministic rotation so neighbours trace different sets.
                uint2 pix = (uint2)(uv * _UCSSGIHalfTexel.zw);
                float rot = UCPixelRotation(pix);

                // Tangent frame about the surface normal for orienting the hemisphere.
                float3 up = abs(n.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
                float3 tx = normalize(cross(up, n));
                float3 ty = cross(n, tx);

                float3 acc = 0.0;
                [loop]
                for (int r = 0; r < rayCount; r++)
                {
                    float2 xi = UCHammersley((uint)r, (uint)rayCount);
                    xi.x = frac(xi.x + rot);                       // deterministic rotate
                    float3 h = UCCosineHemisphere(xi);
                    float3 dir = tx * h.x + ty * h.y + n * h.z;

                    float  hit = 0.0;
                    float3 hitCol = 0.0;
                    [loop]
                    for (int s = 1; s <= STEPS; s++)
                    {
                        float3 sp = wpos + n * 0.02 + dir * (stepLen * s);
                        float4 clip = TransformWorldToHClip(sp);
                        if (clip.w <= 0.0) break;
                        float2 suv = clip.xy / clip.w * 0.5 + 0.5;
                    #if UNITY_UV_STARTS_AT_TOP
                        suv.y = 1.0 - suv.y;
                    #endif
                        if (any(suv < 0.0) || any(suv > 1.0)) break;

                        float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                        float rayEye   = -TransformWorldToView(sp).z;
                        float diff     = rayEye - sceneEye;
                        if (diff > 0.02 && diff < thickness)
                        {
                            hit = 1.0;
                            hitCol = SampleSceneColor(suv);        // the lit bounce source
                            break;
                        }
                    }
                    // Hit -> that surface's colour; miss -> the grid value (cancels later).
                    acc += hit > 0.5 ? hitCol : gridBaseline;
                }

                float3 estimate = acc / max(1, rayCount);
                float3 delta = estimate - gridBaseline;            // zero where nothing hit
                return half4(delta, eye);
            }
            ENDHLSL
        }

        // ===== Pass 1 : BLUR + TEMPORAL (half res) ================================
        Pass
        {
            Name "SSGIBlurTemporal"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlurTemporal

            half4 FragBlurTemporal(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float centerRaw = SampleSceneDepth(uv);
                if (UCSkyDepth(centerRaw)) return half4(0.0, 0.0, 0.0, 0.0);
                float centerEye = LinearEyeDepth(centerRaw, _ZBufferParams);

                // Depth-aware 3x3 blur of the raw trace: neighbours on the same surface
                // contribute, an edge to a different depth is not blurred across. The same box
                // gives the mean/second-moment the variance clamp (S-80) needs below.
                float2 texel = _UCSSGIHalfTexel.xy;
                float3 sum = 0.0, m1 = 0.0, m2 = 0.0;
                float  wsum = 0.0; int cnt = 0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float2 o = uv + float2(x, y) * texel;
                    half4 t = SAMPLE_TEXTURE2D_X(_UCSSGITrace, sampler_LinearClamp, o);
                    float ne = LinearEyeDepth(SampleSceneDepth(o), _ZBufferParams);
                    float w = exp(-abs(ne - centerEye) * 4.0);
                    sum += t.rgb * w;
                    wsum += w;
                    m1 += t.rgb; m2 += t.rgb * t.rgb; cnt++;
                }
                float3 blurred = wsum > 1e-4 ? sum / wsum : 0.0;
                float3 lo, hi;
                UCVarianceBox(m1, m2, cnt, lo, hi);   // shared variance clamp box (S-80)

                // Reproject into last frame - motion vectors first (camera AND object motion), the
                // prev view-projection as the static fallback - reject a depth disocclusion, then
                // blend the CLAMPED history so fast motion cannot smear indirect light into a ghost.
                float3 wpos = ComputeWorldSpacePosition(uv, centerRaw, UNITY_MATRIX_I_VP);
                float3 accum = blurred;
                float2 puv;
                bool reproj = UCReprojectByMotion(uv, puv) || UCReprojectByViewProj(wpos, _UCSSGIPrevViewProj, puv);
                if (reproj && _UCSSGIHistoryValid > 0.5)
                {
                    half4 hist = SAMPLE_TEXTURE2D_X(_UCSSGIHistory, sampler_LinearClamp, puv);
                    if (UCHistoryDepthValid(hist.a, centerEye))     // not a disocclusion
                    {
                        float3 clamped = clamp(hist.rgb, lo, hi);   // variance clamp
                        accum = lerp(blurred, clamped, _UCSSGIParams.w);
                    }
                }
                return half4(accum, centerEye);
            }
            ENDHLSL
        }

        // ===== Pass 2 : COMPOSITE (full res, additive) ============================
        Pass
        {
            Name "SSGIComposite"
            Blend One One                 // add the delta onto the lit colour; 0 = no-op
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            half4 FragComposite(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float rawD = SampleSceneDepth(uv);
                if (UCSkyDepth(rawD)) return half4(0.0, 0.0, 0.0, 0.0);   // adds nothing to sky

                // Upsample the half-res accumulated delta and add it. Where nothing was
                // traced the delta is zero, so the composite is a straight pass-through.
                float3 delta = SAMPLE_TEXTURE2D_X(_UCSSGIResult, sampler_LinearClamp, uv).rgb;
                return half4(delta * _UCSSGIParams.z, 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
