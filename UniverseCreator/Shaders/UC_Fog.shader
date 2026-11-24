// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_Fog.shader
//  Purpose: Depth-based atmospheric fog, composited over the scene as a fullscreen
//           pass. It reconstructs each pixel's world position from the depth
//           texture, integrates an exponential-with-height density along the view
//           ray, and blends the scene toward the sky's own colour by how much fog
//           lies between the eye and that pixel. That single height profile is what
//           lets fog fill a valley and leave a ridge above it clear. The same pass
//           also applies aerial perspective - the always-present haze that turns a
//           distant ridge blue and seats it in the sky behind it - because both are
//           the one question "how does the air between the eye and this pixel change
//           its colour", and answering it twice would tint distant ground twice.
//
//  WHY IT FADES TO THE SKY COLOUR, NOT TO GREY
//  -------------------------------------------
//  The colour a distant surface fades into is the horizon it stands against, and at
//  sunrise that horizon is orange - so the fog is orange because it is *lit* orange,
//  never because a colour was keyed in. The lit colour and the sun-facing colour
//  arrive as globals the atmosphere layer publishes from one shared extinction; this
//  shader consumes them and never computes scattering itself, so the fog can never
//  drift away from the sky it fades into.
//
//  PIPELINE
//  --------
//  URP only ("RenderPipeline" = "UniversalPipeline"). A fullscreen blit that reads
//  the scene colour and depth targets, so it needs the Depth Texture enabled on the
//  URP asset. Inert until a driver publishes a non-zero density, so an unconfigured
//  scene is never fogged.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Fog"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "Fog"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // Published by the weather driver each frame; the colours come from the
            // atmosphere layer's shared extinction once it exists. All zero by
            // default, which leaves density at 0 and the pass a pass-through.
            float4 _UCFogColor;     // rgb = horizon / sky colour the scene fades to
            float4 _UCFogSunColor;  // rgb = sun-lit inscattering colour
            float4 _UCFogSunDir;    // xyz = normalised direction toward the sun (world)
            float4 _UCFogParams;    // x = surfaceDensity01, y = scaleHeightM,
                                    // z = groundHeightM, w = visibilityMetres

            // Aerial perspective: the always-present Rayleigh haze, distinct from the
            // fog medium and present on a perfectly clear day. Both the per-metre
            // extinction and the inscatter colour come from the atmosphere layer's one
            // shared extinction. Zero extinction leaves it inert.
            float4 _UCAerialExtinction; // rgb = per-metre extinction (blue the highest)
            float4 _UCAerialColor;      // rgb = sky inscatter a distant surface gains

            // Optical depth of the exponential-height fog along the ray from the eye
            // to a world point. Closed form of the integral of exp(-(y-ground)/H):
            // a horizontal ray sees uniform density, a ray climbing out of the fog
            // sees it thin toward the top.
            float UCFogOpticalDepth(float3 camPos, float3 worldPos)
            {
                float density = _UCFogParams.x;
                if (density <= 0.0) return 0.0;

                float H       = max(1e-3, _UCFogParams.y);
                float groundH = _UCFogParams.z;
                float vis     = max(1.0, _UCFogParams.w);

                float3 ray = worldPos - camPos;
                float L  = length(ray);
                float dy = ray.y;

                float baseD = exp(-(camPos.y - groundH) / H);
                float t     = dy / H;
                float shape = abs(t) > 1e-4 ? (1.0 - exp(-t)) / t : 1.0;

                // 3/vis is the extinction that drops a horizontal unit-density ray to
                // ~5% transmittance over the visibility distance.
                float k = 3.0 / vis;
                return k * density * baseD * shape * L;
            }

            half4 Frag(Varyings input) : SV_Target0
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord.xy;

                half4 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, _BlitMipLevel);

                float rawDepth = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                bool isSky = rawDepth <= 0.0;
            #else
                bool isSky = rawDepth >= 1.0;
            #endif

                // The sky is at infinity and is already the horizon colour from the
                // atmosphere layer; fogging it again would only darken the horizon.
                if (isSky) return scene;

                float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 camPos   = _WorldSpaceCameraPos;

                float3 ray = worldPos - camPos;
                float L = length(ray);
                float3 viewDir = ray / max(L, 1e-4);

                // Aerial perspective first: per-channel extinction fades the surface and
                // replaces it with sky inscatter, so a distant ridge goes blue-hazed and
                // settles onto the sky behind it. Blue is attenuated most and gains most
                // inscatter, which is where the haze's colour comes from.
                float3 Ta = exp(-_UCAerialExtinction.rgb * L);
                scene.rgb = scene.rgb * Ta + _UCAerialColor.rgb * (1.0 - Ta);

                // Then the fog medium on top: sky colour by default, warming toward the
                // sun's own colour along the line of sight - the forward-scatter lobe
                // that reddens fog at sunrise.
                float tau = UCFogOpticalDepth(camPos, worldPos);
                float fog = 1.0 - exp(-tau);
                float sun = saturate(dot(viewDir, normalize(_UCFogSunDir.xyz + float3(0.0, 1e-4, 0.0))));
                float3 fogCol = lerp(_UCFogColor.rgb, _UCFogSunColor.rgb, sun * sun);

                scene.rgb = lerp(scene.rgb, fogCol, fog);
                return scene;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
