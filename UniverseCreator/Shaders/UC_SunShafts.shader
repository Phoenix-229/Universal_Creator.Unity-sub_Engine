// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_SunShafts.shader
//  Purpose: Crepuscular rays as a screen-space radial term, not a volumetric march.
//           From each pixel the fragment steps a short way toward the sun's screen
//           position, accumulating the scene's brightness along that line with a decay,
//           so light that streams through a gap in the deck smears into a shaft. The
//           whole term is scaled by a master intensity the lighting driver publishes -
//           a product of a coverage window (peaks in broken cloud, zero at clear and
//           overcast) and a low-sun window (zero once the sun is high) - so a clear sky,
//           a solid overcast and a high sun each draw no shaft at all, and the effect is
//           a clean platform toggle: publish zero intensity and the pass is a copy.
//
//  PIPELINE
//  --------
//  URP fullscreen pass, composited additively over the camera colour. Inert until the
//  driver publishes a non-zero intensity and the sun's on-screen position, so an
//  unconfigured or off scene passes the image through unchanged.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/SunShafts"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "SunShafts"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Published by the lighting driver each frame; all zero by default, which
            // leaves the intensity at 0 and the whole pass a straight copy of the image.
            float2 _UCShaftSunUv;      // the sun's position in screen UV (0..1)
            float  _UCShaftIntensity;  // master strength 0..1 (coverage x low-sun window)
            float  _UCShaftDecay;      // per-step falloff along the ray (0..1)
            float4 _UCShaftColor;      // rgb = the ray tint (the lit-cloud / sun colour)

            // Fixed, cheap sample count - a short screen-space smear, never a march.
            #define UC_SHAFT_SAMPLES 24

            half4 Frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                half4 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                if (_UCShaftIntensity <= 0.0) return scene;

                // Step from this pixel toward the sun's screen position, shrinking the
                // offset each sample so the accumulation trails off into a shaft.
                float2 delta = (_UCShaftSunUv - uv) / (float)UC_SHAFT_SAMPLES;
                float2 coord = uv;
                float  weight = 1.0;
                float3 shaft = 0.0;
                float  wsum = 0.0;

                [unroll]
                for (int i = 0; i < UC_SHAFT_SAMPLES; i++)
                {
                    coord += delta;
                    float3 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, coord).rgb;
                    // Only the bright part of the scene streams into a ray.
                    float luma = dot(s, float3(0.2126, 0.7152, 0.0722));
                    shaft += s * saturate(luma - 0.6) * weight;
                    wsum  += weight;
                    weight *= _UCShaftDecay;
                }
                shaft /= max(wsum, 1e-4);

                float3 rays = shaft * _UCShaftColor.rgb * _UCShaftIntensity;
                return half4(scene.rgb + rays, scene.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
