// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_TerrainDetailTess.shader
//  Purpose: The micro-relief overlay's own surface shader. The camera-following
//           patch ring feeds this a flat grid; the hull stage subdivides each patch
//           edge by a factor read from that edge's own midpoint (so neighbours agree
//           and the seam closes), and the domain stage displaces the new vertices by
//           the micro-relief height field baked into _DetailHeightTex, sampled in
//           world XZ. The point of the whole exercise is sub-heightmap geometry that
//           a grazing light can pick out - so the surface normal is NOT taken from
//           the tessellated triangle (which only knows the coarse facets) but
//           recomputed analytically from the height field's own gradient, so lighting
//           reads the relief the geometry just gained instead of flattening across it.
//
//  WHY ANALYTIC NORMALS, NOT TRIANGLE NORMALS
//  ------------------------------------------
//  A displaced vertex moves, but the per-triangle normal only turns as sharply as the
//  tessellation is fine; between two subdivided vertices the facet is flat and a
//  grazing light glances off it as if the relief were not there. The height field, by
//  contrast, has a defined slope everywhere. Taking the normal from d(height)/dx and
//  d(height)/dz (central differences in world metres) gives the true micro-slope at
//  every fragment, independent of how coarse the tessellation happens to be - which is
//  exactly the done-when: relief the un-tessellated terrain does not show, with the
//  normals agreeing with the displacement.
//
//  OVERLAY, NOT TERRAIN
//  --------------------
//  This never touches TerrainData (C-04). It draws the ring's floating meshes, which
//  sit a hair above the native terrain and are world-horizontal, so the height field
//  is a true y = H(x,z) heightfield and its analytic normal is world-space directly.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/TerrainDetailTess"
{
    Properties
    {
        [Header(Surface)]
        _BaseColor      ("Base Colour", Color) = (0.5, 0.5, 0.5, 1)
        _Smoothness     ("Smoothness", Range(0,1)) = 0.15
        _Metallic       ("Metallic", Range(0,1)) = 0.0

        [Header(Micro relief height field (world XZ))]
        _DetailHeightTex ("Detail Height (R, 0.5 = neutral)", 2D) = "grey" {}
        _DetailWorldOrigin ("Field Origin XZ (m)", Vector) = (0,0,0,0)
        _DetailWorldSize ("Field Size (m)", Float) = 256.0
        _DetailHeightScale ("Height Scale (m, full R range)", Float) = 0.5
        _DetailGradEpsilonM ("Normal Gradient Step (m)", Float) = 0.05

        [Header(Tessellation (edge midpoint driven))]
        _TessSegmentAngle ("Target Segment Angle (rad)", Float) = 0.0075
        _TessMaxFactor    ("Max Tess Factor", Range(1,64)) = 32
    }

    HLSLINCLUDE
    // Shared by every pass: the height field, the displacement, the analytic normal, and the
    // edge-midpoint tessellation factor. Declaring them once here keeps the forward and shadow
    // passes displacing the SAME surface, so a shadow can never disagree with the lit relief.
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_DetailHeightTex);   SAMPLER(sampler_DetailHeightTex);

    CBUFFER_START(UnityPerMaterial)
        float4 _BaseColor;
        float  _Smoothness;
        float  _Metallic;
        float4 _DetailWorldOrigin;
        float  _DetailWorldSize;
        float  _DetailHeightScale;
        float  _DetailGradEpsilonM;
        float  _TessSegmentAngle;
        float  _TessMaxFactor;
    CBUFFER_END

    // World XZ -> field UV. The field spans _DetailWorldSize metres from its origin corner.
    float2 UC_FieldUV(float2 worldXZ)
    {
        float inv = 1.0 / max(1e-3, _DetailWorldSize);
        return (worldXZ - _DetailWorldOrigin.xz) * inv;
    }

    // Signed displacement in metres. R = 0.5 is neutral, so relief can push up or cut down.
    float UC_DisplacementM(float2 worldXZ)
    {
        float h = SAMPLE_TEXTURE2D_LOD(_DetailHeightTex, sampler_DetailHeightTex, UC_FieldUV(worldXZ), 0).r;
        return (h - 0.5) * _DetailHeightScale;
    }

    // The surface normal taken from the field's own slope, not the coarse triangle: central
    // differences of the displacement in world metres give the true micro-slope at any fragment.
    float3 UC_AnalyticNormalWS(float2 worldXZ)
    {
        float e = max(1e-3, _DetailGradEpsilonM);
        float hx1 = UC_DisplacementM(worldXZ + float2(e, 0));
        float hx0 = UC_DisplacementM(worldXZ - float2(e, 0));
        float hz1 = UC_DisplacementM(worldXZ + float2(0, e));
        float hz0 = UC_DisplacementM(worldXZ - float2(0, e));
        float dHdx = (hx1 - hx0) / (2.0 * e);
        float dHdz = (hz1 - hz0) / (2.0 * e);
        return normalize(float3(-dHdx, 1.0, -dHdz));
    }

    // Distance -> subdivision factor, the GPU twin of PatchEdgeTessellation.FactorFromDistance: an
    // edge of length L at distance d subtends ~L/d, so holding each segment to _TessSegmentAngle asks
    // for L/(d*angle) segments, clamped to [1, ceiling]. Fed the edge MIDPOINT so neighbours agree.
    float UC_EdgeFactor(float3 midWS, float edgeLenM)
    {
        float angle = max(1e-5, _TessSegmentAngle);
        float d = max(1e-3, distance(_WorldSpaceCameraPos, midWS));
        float segments = edgeLenM / (d * angle);
        return clamp(segments, 1.0, max(1.0, _TessMaxFactor));
    }

    // One control point carried vertex -> hull -> domain: world position, world normal, field UV.
    struct UCTessCP
    {
        float3 positionWS : INTERNALTESSPOS;
        float3 normalWS   : NORMAL;
        float2 worldXZ    : TEXCOORD0;
    };

    struct UCTessFactors
    {
        float edge[3]  : SV_TessFactor;
        float inside   : SV_InsideTessFactor;
    };

    // Per-patch constant function. Triangle edge i joins the two vertices that are NOT vertex i;
    // its factor comes from that edge's own world midpoint, which the neighbouring patch sharing
    // the edge computes from the same two shared boundary vertices - so the seam factor matches.
    UCTessFactors UC_PatchConstant(InputPatch<UCTessCP, 3> patch)
    {
        float3 p0 = patch[0].positionWS;
        float3 p1 = patch[1].positionWS;
        float3 p2 = patch[2].positionWS;

        UCTessFactors f;
        f.edge[0] = UC_EdgeFactor(0.5 * (p1 + p2), distance(p1, p2));
        f.edge[1] = UC_EdgeFactor(0.5 * (p2 + p0), distance(p2, p0));
        f.edge[2] = UC_EdgeFactor(0.5 * (p0 + p1), distance(p0, p1));
        f.inside  = (f.edge[0] + f.edge[1] + f.edge[2]) / 3.0;
        return f;
    }
    ENDHLSL

    SubShader
    {
        // Sits just after terrain in the opaque queue - it is a skin over the ground, drawn on top.
        Tags
        {
            "Queue"          = "Geometry"
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
        }

        // -------------------------------------------------------------------
        //  ForwardLit - tessellate, displace, and shade with analytic normals.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex UCVert
            #pragma hull UCHull
            #pragma domain UCDomain
            #pragma fragment UCFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 worldXZ    : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
            };

            // Vertex: to world space only. Displacement waits for the domain stage, after subdivision.
            UCTessCP UCVert(Attributes IN)
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                UCTessCP cp;
                cp.positionWS = posWS;
                cp.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                cp.worldXZ    = posWS.xz;
                return cp;
            }

            [domain("tri")]
            [outputcontrolpoints(3)]
            [outputtopology("triangle_cw")]
            [partitioning("fractional_odd")]
            [patchconstantfunc("UC_PatchConstant")]
            UCTessCP UCHull(InputPatch<UCTessCP, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            // Domain: barycentric-interpolate the world position, displace it by the field along world
            // up (the overlay is horizontal, so up IS the base normal), then project to clip space.
            [domain("tri")]
            Varyings UCDomain(UCTessFactors factors, OutputPatch<UCTessCP, 3> patch, float3 bary : SV_DomainLocation)
            {
                float3 posWS = patch[0].positionWS * bary.x + patch[1].positionWS * bary.y + patch[2].positionWS * bary.z;
                float2 worldXZ = posWS.xz;
                posWS.y += UC_DisplacementM(worldXZ);

                Varyings OUT;
                OUT.positionWS = posWS;
                OUT.worldXZ    = worldXZ;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 UCFrag(Varyings IN) : SV_Target
            {
                float3 normalWS = UC_AnalyticNormalWS(IN.worldXZ);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo     = _BaseColor.rgb;
                surface.metallic   = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion  = 1.0;
                surface.alpha      = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS      = IN.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord     = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord        = IN.fogFactor;
                inputData.bakedGI         = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  ShadowCaster - tessellate and displace the SAME surface, so the
        //  relief casts the shadow the lit surface shows, not the flat patch.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex UCShadowVert
            #pragma hull UCShadowHull
            #pragma domain UCShadowDomain
            #pragma fragment UCShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct ShadowV { float4 positionCS : SV_POSITION; };

            UCTessCP UCShadowVert(Attributes IN)
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                UCTessCP cp;
                cp.positionWS = posWS;
                cp.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                cp.worldXZ    = posWS.xz;
                return cp;
            }

            [domain("tri")]
            [outputcontrolpoints(3)]
            [outputtopology("triangle_cw")]
            [partitioning("fractional_odd")]
            [patchconstantfunc("UC_PatchConstant")]
            UCTessCP UCShadowHull(InputPatch<UCTessCP, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            [domain("tri")]
            ShadowV UCShadowDomain(UCTessFactors factors, OutputPatch<UCTessCP, 3> patch, float3 bary : SV_DomainLocation)
            {
                float3 posWS = patch[0].positionWS * bary.x + patch[1].positionWS * bary.y + patch[2].positionWS * bary.z;
                posWS.y += UC_DisplacementM(posWS.xz);
                float3 nWS = UC_AnalyticNormalWS(posWS.xz);

            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirWS = normalize(_LightPosition - posWS);
            #else
                float3 lightDirWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nWS, lightDirWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                ShadowV OUT;
                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 UCShadowFrag(ShadowV IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    // -----------------------------------------------------------------------
    //  FALLBACK SUB-SHADER (G-24) - no hull/domain stages. Unity selects this
    //  automatically on a GPU that cannot run the SM 4.6 sub-shader above (GLES
    //  3.0 and the like); the ring pairs it with a denser fixed patch mesh so
    //  displaced relief still shows, at a fixed density, without tessellation.
    // -----------------------------------------------------------------------
    SubShader
    {
        Tags
        {
            "Queue"          = "Geometry"
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex UCVertLo
            #pragma fragment UCFragLo

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct AttributesLo { float4 positionOS : POSITION; };
            struct VaryingsLo
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 worldXZ    : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
            };

            // No tessellation: the mesh is already dense, so we displace each real vertex here by the
            // same field the hardware domain stage uses. Vertex texture fetch is fine on SM 3.5 / GLES 3.0.
            VaryingsLo UCVertLo(AttributesLo IN)
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS.y += UC_DisplacementM(posWS.xz);
                VaryingsLo OUT;
                OUT.positionWS = posWS;
                OUT.worldXZ    = posWS.xz;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 UCFragLo(VaryingsLo IN) : SV_Target
            {
                float3 normalWS = UC_AnalyticNormalWS(IN.worldXZ);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo     = _BaseColor.rgb;
                surface.metallic   = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion  = 1.0;
                surface.alpha      = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS      = IN.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord     = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord        = IN.fogFactor;
                inputData.bakedGI         = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex UCShadowVertLo
            #pragma fragment UCShadowFragLo
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct AttributesLo { float4 positionOS : POSITION; };
            struct ShadowVLo { float4 positionCS : SV_POSITION; };

            ShadowVLo UCShadowVertLo(AttributesLo IN)
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS.y += UC_DisplacementM(posWS.xz);
                float3 nWS = UC_AnalyticNormalWS(posWS.xz);

            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirWS = normalize(_LightPosition - posWS);
            #else
                float3 lightDirWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nWS, lightDirWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                ShadowVLo OUT;
                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 UCShadowFragLo(ShadowVLo IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    // No FallBack: under URP a fallback to a Built-in shader draws magenta, and swallowing the
    // error hides the cause. If tessellation is unsupported, the ring selects the G-24 mesh
    // fallback instead of this shader - that decision is made on the CPU, not here.
}

