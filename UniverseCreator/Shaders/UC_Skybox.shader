// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_Skybox.shader
//  Purpose: The procedural sky, drawn by sampling the small precomputed sky table
//           rather than evaluating the analytic model per pixel, then adding the
//           sun and moon as discs at their true angular size. The table already
//           holds the circumsolar brightening, the horizon and the blue gradient
//           for the current sun and turbidity, so the fragment does almost nothing:
//           it turns a view direction into a table coordinate, reads the colour,
//           and paints the two bodies on top. That is the whole reason the table
//           exists - the sky changes slowly, so paying for it once a bake and
//           reading it per pixel is far cheaper than the model every pixel.
//
//  WHY DISCS BY ANGLE, NOT BY A SPRITE
//  -----------------------------------
//  The sun and moon are the same tiny angular size in the real sky, which is why
//  an eclipse is possible; drawing them from that angle - a soft-edged cap where
//  the view direction is within a half-degree of the body - keeps that truth and
//  needs no billboard, no texture and no per-body mesh.
//
//  PIPELINE
//  --------
//  URP skybox, assigned to the scene's sky by the lighting controller. The sky
//  colour comes from _SkyLUT (a 32x16 table the controller bakes and binds); the
//  body directions, colours and angular sizes arrive as globals the controller
//  publishes each frame. All default to inert - an unbound table is black and a
//  zero body direction draws no disc - so an unconfigured scene is never wrong,
//  only empty, until the controller drives it.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Skybox"
{
    Properties
    {
        [NoScaleOffset] _SkyLUT ("Sky table (azimuth-from-sun x elevation)", 2D) = "black" {}
        _SkyExposure ("Sky exposure multiplier", Float) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Name "Skybox"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "UC_Sky.hlsl"   // _SkyLUT / _SkyExposure / _UCSunDir + UCSkyUV live here (one home)

            // Published by the lighting controller each frame; zero by default, which
            // leaves both bodies undrawn and the sky whatever the table holds.
            float4 _UCMoonDir;       // xyz = normalised world direction toward the moon
            float4 _UCSunColor;      // rgb = sun disc colour
            float4 _UCMoonColor;     // rgb = moon disc colour
            float4 _UCSkyDiscParams; // x = sun half-angle, y = sun limb, z = moon half-angle, w = moon limb (radians)

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 viewDirWS : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs v = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = v.positionCS;
                OUT.viewDirWS = v.positionWS - _WorldSpaceCameraPos;
                return OUT;
            }

            // A view direction to a table coordinate lives in UC_Sky.hlsl (UCSkyUV), shared
            // with the reflection fallback so the dome and a puddle read the identical sky.

            // A soft-edged cap where the view is within a body's angular radius, limb
            // softened across the outer band so the edge is not a hard circle.
            float BodyDisc(float3 dir, float3 bodyDir, float halfAngle, float limb)
            {
                if (halfAngle <= 0.0 || dot(bodyDir, bodyDir) < 1e-6) return 0.0;
                float ang = acos(clamp(dot(dir, normalize(bodyDir)), -1.0, 1.0));
                float outer = halfAngle + max(limb, 1e-4);
                return 1.0 - smoothstep(halfAngle, outer, ang);
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.viewDirWS);
                float3 sunDir = _UCSunDir.xyz;

                float3 sky = UCSampleSkyRadiance(dir);   // the one shared sky read (UC_Sky.hlsl)

                // The sun sits in front of the sky; the moon behind the sun, so an
                // eclipse hides it. Both fade at the limb rather than cut a hard hole.
                float moon = BodyDisc(dir, _UCMoonDir.xyz, _UCSkyDiscParams.z, _UCSkyDiscParams.w);
                float sun = BodyDisc(dir, sunDir, _UCSkyDiscParams.x, _UCSkyDiscParams.y);
                sky = lerp(sky, _UCMoonColor.rgb, moon);
                sky = lerp(sky, _UCSunColor.rgb, sun);

                // Cloud layers (the cloud shaders) composite over this sky at the
                // render stage; the skybox itself carries only the clear-air dome.
                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }

    CustomEditor "UniverseCreator.Editor.Drawing.UCSkyboxShaderGUI"
    Fallback Off
}
