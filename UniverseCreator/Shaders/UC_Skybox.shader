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
        [NoScaleOffset] _UCPreviousSkyLUT ("Previous sky table", 2D) = "black" {}
        _UCSkyBlend ("Sky transition weight", Float) = 1.0
        [NoScaleOffset] _UCCloudNoise ("Cloud shape field", 2D) = "black" {} // UC-T08-08: explicit material binding for the existing shared cloud pass.
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
            #include "UC_Lighting.hlsl" // UC celestial follow-up: reuse the published atmospheric extinction.

            // Published by the lighting controller each frame; zero by default, which
            // leaves both bodies undrawn and the sky whatever the table holds.
            float4 _UCMoonDir;       // xyz = normalised world direction toward the moon
            float4 _UCSunColor;      // rgb = sun disc colour
            float4 _UCMoonColor;     // rgb = moon disc colour, a = authoritative illuminated fraction
            float4 _UCSkyDiscParams; // x = sun half-angle, y = sun limb, z = moon half-angle, w = moon limb (radians)
            TEXTURE2D(_UCStars); SAMPLER(sampler_UCStars);
            float4x4 _UCWorldToEquatorial;
            float4 _UCStarParams; // x = exposed catalog radiance, y = captured turbidity, z = bounded air-path floor

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 viewDirWS : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs v = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = v.positionCS;
                // UC Scene/Play: the sky is infinitely distant; camera translation must never change its celestial bearings.
                OUT.viewDirWS = TransformObjectToWorldDir(IN.positionOS.xyz, false);
                return OUT;
            }

            // A view direction to a table coordinate lives in UC_Sky.hlsl (UCSkyUV), shared
            // with the reflection fallback so the dome and a puddle read the identical sky.

            // A soft-edged cap where the view is within a body's angular radius, limb
            // softened across the outer band so the edge is not a hard circle.
            float BodyDisc(float3 dir, float3 bodyDir, float halfAngle, float limb)
            {
                if (halfAngle <= 0.0 || dot(bodyDir, bodyDir) < 1e-6) return 0.0;
                // UC celestial repair: chord distance stays precise at sub-degree angles; coverage is centred on the authored edge.
                float distance = length(dir - normalize(bodyDir));
                float radius = 2.0 * sin(halfAngle * 0.5);
                float width = max(fwidth(distance), max(min(limb, radius * 0.05), 1e-5));
                return 1.0 - smoothstep(max(0.0, radius - width * 0.5), radius + width * 0.5, distance);
            }

            // The near surface of a lunar sphere is lit from the sun's actual direction.
            // This yields continuous new/crescent/quarter/gibbous/full phases, with the bright limb facing the sun in either hemisphere.
            float MoonLitMask(float3 dir, float3 moonDir, float halfAngle)
            {
                float3 moon = normalize(moonDir);
                float3 tangent = (dir - moon * dot(dir, moon)) / max(sin(halfAngle), 1e-5);
                float r2 = dot(tangent, tangent);
                float3 normal = tangent - moon * sqrt(saturate(1.0 - r2));
                // UC celestial repair: retain the sun-facing limb but use the clock's phase for the terminator.
                float phase = saturate(_UCMoonColor.a);
                if (phase <= 0.0) return 0.0;
                if (phase >= 1.0) return 1.0; // BodyDisc owns the antialiased edge for every phase.
                float3 towardSun = _UCSunDir.xyz - moon * dot(_UCSunDir.xyz, moon);
                if (dot(towardSun, towardSun) < 1e-8)
                    towardSun = abs(moon.y) < 0.99 ? cross(moon, float3(0, 1, 0)) : cross(moon, float3(1, 0, 0));
                float axial = 1.0 - 2.0 * phase;
                float3 phaseLight = normalize(towardSun) * sqrt(saturate(1.0 - axial * axial)) + moon * axial;
                float incident = dot(normal, phaseLight);
                float width = max(fwidth(incident), 0.002);
                return smoothstep(-width, width, incident);
            }

            float4 Frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.viewDirWS);
                float3 sunDir = _UCSunDir.xyz;

                float3 sky = UCSampleSkyRadiance(dir);   // the one shared sky read (UC_Sky.hlsl)

                // UC celestial repair: no translation or world-size dependence; catalog stars rotate with the game sky.
                if (_UCStarParams.x > 0.0 && dir.y > 0.0)
                {
                    float3 equatorial = normalize(mul((float3x3)_UCWorldToEquatorial, dir));
                    float2 uv = float2(atan2(equatorial.z, equatorial.x) / (2.0 * PI),
                        asin(clamp(equatorial.y, -1.0, 1.0)) / PI + 0.5);
                    float poleArea = max(sqrt(saturate(1.0 - equatorial.y * equatorial.y)), 0.003);
                    float horizonFade = smoothstep(0.0, 0.08, dir.y);
                    // UC celestial follow-up: bounded plane-parallel path relative to zenith; clouds still composite afterward.
                    float extraAirMass = max(0.0, rcp(max(dir.y, max(_UCStarParams.z, 1e-5))) - 1.0);
                    float3 transmission = UCAtmosphericExtinction(extraAirMass, _UCStarParams.y);
                    sky += SAMPLE_TEXTURE2D(_UCStars, sampler_UCStars, uv).rgb * transmission * (_UCStarParams.x * horizonFade / poleArea);
                }

                // The sun sits in front of the sky; the moon behind the sun, so an
                // eclipse hides it. Both fade at the limb rather than cut a hard hole.
                float moon = BodyDisc(dir, _UCMoonDir.xyz, _UCSkyDiscParams.z, _UCSkyDiscParams.w);
                if (moon > 0) moon *= MoonLitMask(dir, _UCMoonDir.xyz, _UCSkyDiscParams.z);
                float sun = BodyDisc(dir, sunDir, _UCSkyDiscParams.x, _UCSkyDiscParams.y);
                // Discs crossing the horizon disappear progressively; a below-horizon sky ray must not show either body.
                float horizon = smoothstep(0.0, max(fwidth(dir.y), 1e-5), dir.y);
                moon *= horizon; sun *= horizon;
                sky = lerp(sky, _UCMoonColor.rgb, moon);
                sky = lerp(sky, _UCSunColor.rgb, sun);

                // Cloud layers (the cloud shaders) composite over this sky at the
                // render stage; the skybox itself carries only the clear-air dome.
                // UC Scene/Play: exposed solar radiance can exceed half/HDR render-target range at a manual stop.
                // Keep the physical LUT unchanged; bound only the display output below the HDR target's finite limit.
                return float4(min(sky, 60000.0), 1.0);
            }
            ENDHLSL
        }
        UsePass "UniverseCreator/Clouds/Clouds" // UC-T08-08: sky and moving decks use one draw owner and the existing sheet shader.
    }

    CustomEditor "UniverseCreator.Editor.Drawing.UCSkyboxShaderGUI"
    Fallback Off
}
