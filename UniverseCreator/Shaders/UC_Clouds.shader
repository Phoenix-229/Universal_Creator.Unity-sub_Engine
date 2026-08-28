// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_Clouds.shader
//  Purpose: The three cloud decks, drawn as domed sheets that sample one baked,
//           seamless noise field rather than a per-pixel volumetric march. Each deck
//           is a horizontal plane at its own altitude; the fragment intersects the
//           view ray with that plane and reads the field at the world point it hits,
//           so the sheet reads as a plane seen in perspective - close overhead, far
//           toward the horizon - instead of a texture painted on a dome. Coverage
//           thresholds the field into cloud and gap, the deck's genus bends that one
//           field into its own silhouette (a cumulus hardens into cauliflower, a
//           stratus flattens to an even sheet, a cirrus stretches along the wind),
//           the wind scrolls the field, and a single sun-side lookup shades a heap
//           from its lit flank to its base. A handful of texture fetches, no march.
//
//  PIPELINE
//  --------
//  URP only. A transparent material on a large camera-locked sky dome, composited
//  over the skybox and occluded by opaque terrain (ZTest LEqual against the depth
//  the scene already wrote, so a ridge hides the cloud behind it). Inert until the
//  lighting driver publishes a non-zero per-deck coverage and the metres-per-UV
//  scale, so an unconfigured scene draws no cloud at all.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Clouds"
{
    Properties
    {
        [NoScaleOffset] _UCCloudNoise ("Cloud shape field (RGBA: base, detail, wisp)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Cull Front
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Clouds"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_UCCloudNoise);
            SAMPLER(sampler_UCCloudNoise);

            // Published by the lighting driver each frame; all zero by default, which
            // leaves every deck's coverage at 0 and the whole pass a pass-through.
            // Each deck packs its shape into one float4 and its drift into another:
            //   shape = ( coverage01, edgeSharpness, flatten01, stretch )
            //   drift = ( scrollU, scrollV, baseAltitudeM, selfShadowDepth )
            float4 _UCCloudShape0; float4 _UCCloudDrift0;   // low deck
            float4 _UCCloudShape1; float4 _UCCloudDrift1;   // mid deck
            float4 _UCCloudShape2; float4 _UCCloudDrift2;   // high deck

            float  _UCCloudMetresPerUv;  // world metres one field tile covers; 0 = inert
            float4 _UCSunDir;            // xyz = normalised world direction toward the sun
            float4 _UCSunColor;          // rgb = sun disc / lit-cloud colour
            float4 _UCCloudAmbient;      // rgb = shaded-side sky fill (default black)

            // Cumulonimbus draws from the same sheet, only harder: a per-deck flag turns
            // the storm shaping on, and one shared pair of terms carries its contrast.
            float4 _UCCloudStorm;        // per-deck flag (x=low, y=mid, z=high); 0 = ordinary
            float4 _UCCloudStormParams;  // x = sunlit-flank gain, y = base darkness (toward black)

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

            // The field UV for a world XZ, with the deck's wind scroll added and, for a
            // stretched genus, the along-wind axis elongated: dividing the component of
            // the UV along the wind by the stretch ratio draws round lobes out into
            // wind-parallel streaks, which is the cirrus silhouette.
            float2 FieldUV(float2 worldXZ, float4 shape, float4 drift)
            {
                float2 uv = worldXZ / _UCCloudMetresPerUv + drift.xy;
                float stretch = shape.w;
                float2 wind = drift.xy;
                float wlen = length(wind);
                if (stretch > 1.0 && wlen > 1e-5)
                {
                    float2 axis = wind / wlen;
                    float along = dot(uv, axis);
                    uv -= axis * along * (1.0 - 1.0 / stretch);
                }
                return uv;
            }

            // The field sampled and thresholded into this deck's density at one UV.
            // Coverage lowers the threshold; edgeSharpness steepens the cloud/gap
            // transition and erodes it with the mid detail channel into cauliflower;
            // the wisp channel is folded in for a stretched genus; flatten01 lerps the
            // result toward a uniform sheet at the coverage level for a stratus.
            float Density(float2 uv, float4 shape)
            {
                float cover = shape.x;
                float edge  = shape.y;
                float flat  = shape.z;
                float stretch = shape.w;

                float4 f = SAMPLE_TEXTURE2D(_UCCloudNoise, sampler_UCCloudNoise, uv);
                float shapeField = f.r;
                shapeField -= f.g * saturate(edge - 1.0) * 0.25;          // erode edges
                shapeField = lerp(shapeField, max(shapeField, f.b), saturate(stretch - 1.0) * 0.5); // wisps

                float threshold = 1.0 - cover;
                float d = saturate((shapeField - threshold) * edge * 4.0);
                d = lerp(d, cover, flat);                                  // flatten toward a deck
                return d;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                if (_UCCloudMetresPerUv <= 0.0) return half4(0, 0, 0, 0);

                float3 dir = normalize(IN.viewDirWS);
                if (dir.y <= 1e-3) return half4(0, 0, 0, 0);   // clouds live above the horizon

                float3 camPos = _WorldSpaceCameraPos;
                float2 sunXZ  = _UCSunDir.xz;
                float slen = length(sunXZ);
                float2 sunUvDir = slen > 1e-5 ? sunXZ / slen : float2(0, 0);

                // Far to near: the high deck first, the low deck composited over it, so
                // a nearer sheet correctly hides the one behind. Each is a plane the
                // view ray strikes at t = (altitude - eyeHeight) / dir.y.
                float3 accum = 0;
                float  accumA = 0;

                [unroll]
                for (int k = 2; k >= 0; k--)
                {
                    float4 shape = (k == 0) ? _UCCloudShape0 : (k == 1) ? _UCCloudShape1 : _UCCloudShape2;
                    float4 drift = (k == 0) ? _UCCloudDrift0 : (k == 1) ? _UCCloudDrift1 : _UCCloudDrift2;
                    if (shape.x <= 0.0) continue;

                    float t = (drift.z - camPos.y) / dir.y;
                    if (t <= 0.0) continue;

                    float2 worldXZ = camPos.xz + dir.xz * t;
                    float2 uv = FieldUV(worldXZ, shape, drift);
                    float d = Density(uv, shape);
                    if (d <= 0.0) continue;

                    // Self-shadow: sample the base field one small step toward the sun.
                    // If more cloud stands between this point and the sun, this point is
                    // shaded - the lit flank to shaded base spread that reads a heap as
                    // three-dimensional from a flat sheet with no second light.
                    float sunSide = 0.0;
                    float depth = drift.w;
                    if (depth > 0.0 && slen > 1e-5)
                    {
                        float2 su = uv + sunUvDir * 0.02;
                        float neighbour = SAMPLE_TEXTURE2D(_UCCloudNoise, sampler_UCCloudNoise, su).r;
                        float here = SAMPLE_TEXTURE2D(_UCCloudNoise, sampler_UCCloudNoise, uv).r;
                        sunSide = saturate((neighbour - here) * depth * 4.0);
                    }
                    float3 lit = lerp(_UCSunColor.rgb, _UCCloudAmbient.rgb, sunSide);

                    // A cumulonimbus deck is drawn from the same field, harder: its base
                    // (the shaded side) is pushed toward black and its sunlit flank gained
                    // up, which is the enormous lit-to-shaded contrast a storm really has.
                    float storm = (k == 0) ? _UCCloudStorm.x : (k == 1) ? _UCCloudStorm.y : _UCCloudStorm.z;
                    if (storm > 0.0)
                    {
                        float flankGain = max(_UCCloudStormParams.x, 1.0);
                        float baseDark  = saturate(_UCCloudStormParams.y);
                        float3 dark  = _UCSunColor.rgb * baseDark;
                        float3 flank = _UCSunColor.rgb * flankGain;
                        lit = lerp(flank, dark, sunSide);
                    }

                    // Fade each deck out toward the horizon: a grazing ray skims the
                    // sheet edge-on, so the sheet has no business drawing a hard ring.
                    float horizonFade = saturate(dir.y * 6.0);
                    float a = d * horizonFade;

                    // Composite this (nearer) deck over what is already behind it.
                    accum = lerp(accum, lit, a);
                    accumA = a + accumA * (1.0 - a);
                }

                return half4(accum, accumA);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
