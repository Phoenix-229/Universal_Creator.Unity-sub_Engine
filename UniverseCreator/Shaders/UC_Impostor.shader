// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_Impostor.shader
//  Purpose: The runtime billboard impostor. It samples one baked octahedral atlas
//           tile for the view direction the camera currently sees the prop from,
//           displaces the fragment's depth by that tile's baked depth channel so the
//           card sinks into terrain along the shape of its own trunk instead of
//           cutting the ground on a straight line, repigments and thins its leaf
//           texels for the season without a second atlas, and dissolves in and out
//           with a screen-space dither clip so both the mesh LOD and this impostor
//           stay in the opaque queue with no blend and no sort.
//
//  IT MIRRORS THE CPU REFERENCE, TEXEL FOR TEXEL
//  ---------------------------------------------
//  Every rule this shader applies has a device-free twin that a self-test already
//  pins: the octahedral encode is ImpostorSystem.EncodeHemiOctahedron /
//  EncodeOctahedronFull, the depth decode is DecodeImpostorDepthOffsetM (plane
//  centred at 0.5 over +/- halfDepth), the leaf repigment is ImpostorSeason's HSV
//  recolour and the leaf-off clip is IsLeafOffClipped keyed on a stable per-texel
//  hash. The shader is the GPU half of maths whose correctness is proven on the CPU,
//  so what remains to verify on device is only that the GPU reproduces those numbers.
//
//  DEPTH, NOT A CLIP PLANE
//  -----------------------
//  The fragment reconstructs its true surface point by pushing the card point along
//  the view ray by the decoded metric offset, re-projects it, and writes that clip
//  depth to SV_Depth. The ordinary z-test then occludes the impostor by terrain
//  exactly as it occludes the geometry LOD - no special occlusion path.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Impostor"
{
    Properties
    {
        [Header(Atlas)]
        _AtlasAlbedo   ("Albedo + Alpha Atlas", 2D) = "white" {}
        _AtlasDepth    ("Depth Atlas (R, 0.5 = plane)", 2D) = "grey" {}
        _AtlasMask     ("Leaf/Wood Mask (R=leaf) + Hash (A)", 2D) = "black" {}
        _ViewsPerAxis  ("Views Per Axis", Float) = 8
        _AlphaCutoff   ("Alpha Cutoff", Range(0,1)) = 0.5

        [Header(Framing)]
        _BoundsRadiusM ("Bounds Radius (m)", Float) = 1.0
        _HalfDepthM    ("Half Depth (m)", Float) = 0.5

        [Header(Season mirrors ImpostorSeason)]
        _SeasonColor      ("Season Leaf Colour", Color) = (0.30, 0.45, 0.12, 1)
        _SeasonTint       ("Season Tint Strength", Range(0,1)) = 0.0
        _LeafOffFraction  ("Leaf-off Fraction", Range(0,1)) = 0.0

        [Header(Dither crossfade mirrors DitherCrossFade)]
        _FadeT         ("Fade Coverage t (0=gone,1=full)", Range(0,1)) = 1.0
    }

    HLSLINCLUDE
    // Shared by the forward and shadow passes so both dissolve, displace and frame the
    // SAME card - a shadow can never disagree with the lit impostor.
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

    TEXTURE2D(_AtlasAlbedo);  SAMPLER(sampler_AtlasAlbedo);
    TEXTURE2D(_AtlasDepth);   SAMPLER(sampler_AtlasDepth);
    TEXTURE2D(_AtlasMask);    SAMPLER(sampler_AtlasMask);

    CBUFFER_START(UnityPerMaterial)
        float4 _AtlasAlbedo_ST;
        float  _ViewsPerAxis;
        float  _AlphaCutoff;
        float  _BoundsRadiusM;
        float  _HalfDepthM;
        float4 _SeasonColor;
        float  _SeasonTint;
        float  _LeafOffFraction;
        float  _FadeT;
    CBUFFER_END

    // sign that never returns 0 - the GPU twin of ImpostorSystem.SignNonZero.
    float UC_SignNZ(float v) { return v >= 0.0 ? 1.0 : -1.0; }

    // Full-octahedron encode of a unit direction over the whole sphere to UV in [0,1]^2.
    // Mirrors ImpostorSystem.EncodeOctahedronFull.
    float2 UC_EncodeOctaFull(float3 dir)
    {
        float l1 = abs(dir.x) + abs(dir.y) + abs(dir.z);
        if (l1 <= 1e-8) return float2(0.5, 0.5);
        float3 p = dir / l1;
        float2 uv = float2(p.x, p.z);
        if (p.y < 0.0)
            uv = float2((1.0 - abs(p.z)) * UC_SignNZ(p.x), (1.0 - abs(p.x)) * UC_SignNZ(p.z));
        return uv * 0.5 + 0.5;
    }

    // Hemi-octahedron encode of an upper-hemisphere direction to UV in [0,1]^2 via the
    // (x+z, x-z) diamond-to-square rotation. Mirrors ImpostorSystem.EncodeHemiOctahedron.
    float2 UC_EncodeOctaHemi(float3 dir)
    {
        if (dir.y < 0.0) dir = float3(dir.x, 0.0, dir.z);
        float l1 = abs(dir.x) + abs(dir.y) + abs(dir.z);
        if (l1 <= 1e-8) return float2(0.5, 0.5);
        float px = dir.x / l1, pz = dir.z / l1;
        return float2(px + pz, px - pz) * 0.5 + 0.5;
    }

    // The atlas UV a view direction reads: encode per the hemi/full keyword, snap to the
    // cell that view lands in, then place this fragment's card UV inside that cell. This is
    // nearest-cell sampling - the 3-cell parallax blend is a refinement layered on later.
    float2 UC_AtlasUV(float3 viewDirLocal, float2 cardUV, out float2 cellOrigin, float viewsPerAxis)
    {
        #if defined(_UC_IMPOSTOR_FULL)
            float2 vuv = UC_EncodeOctaFull(viewDirLocal);
        #else
            float2 vuv = UC_EncodeOctaHemi(viewDirLocal);
        #endif
        float N = max(1.0, viewsPerAxis);
        float2 cell = clamp(floor(vuv * N), 0.0, N - 1.0);
        cellOrigin = cell / N;
        return (cell + saturate(cardUV)) / N;
    }

    // Baked [0,1] depth back to a metric offset from the billboard plane (0.5 -> 0).
    // Mirrors ImpostorSystem.DecodeImpostorDepthOffsetM.
    float UC_DecodeDepthOffsetM(float depth01, float halfDepthM)
    {
        return (depth01 - 0.5) * 2.0 * halfDepthM;
    }

    // Shortest-path hue lerp on the wrapped hue circle (ImpostorSeason.LerpHue).
    float UC_LerpHue(float a, float b, float t)
    {
        float d = b - a;
        if (d > 0.5) d -= 1.0; else if (d < -0.5) d += 1.0;
        float h = a + d * t;
        return h - floor(h);
    }

    // HSV repigment of a leaf texel toward the season colour, VALUE preserved - a recolour,
    // not a re-shade. Wood passes through. Mirrors ImpostorSeason.RepigmentLeaf.
    float3 UC_RepigmentLeaf(float3 baseRGB, float3 seasonRGB, float tint, float isLeaf)
    {
        if (isLeaf < 0.5) return baseRGB;
        float t = saturate(tint);
        float3 bh = RgbToHsv(baseRGB);
        float3 sh = RgbToHsv(seasonRGB);
        float h = UC_LerpHue(bh.x, sh.x, t);
        float s = lerp(bh.y, sh.y, t);
        return HsvToRgb(float3(h, s, bh.z));
    }

    // A leaf texel is clipped once its stable hash exceeds 1 - leafOffFraction; wood never
    // clips. Mirrors ImpostorSeason.IsLeafOffClipped - monotone in the fraction, so thinning
    // never flickers. Returns 1 when the texel survives, 0 when it is gone.
    float UC_LeafKeep(float isLeaf, float texelHash01, float leafOffFraction)
    {
        if (isLeaf < 0.5) return 1.0;
        float f = saturate(leafOffFraction);
        return texelHash01 > (1.0 - f) ? 0.0 : 1.0;
    }

    // A cheap hashed screen-space threshold standing in for the blue-noise texture the bake
    // supplies: enough to keep the dither clip complementary per fragment. DitherCrossFade
    // keeps a fragment while its threshold is below the coverage t.
    float UC_DitherThreshold(float2 pixel)
    {
        float2 p = frac(pixel * float2(0.7548776662, 0.5698402909));
        float n = frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
        return n;
    }
    ENDHLSL

    SubShader
    {
        Tags
        {
            "Queue"          = "AlphaTest"
            "RenderType"     = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        // -------------------------------------------------------------------
        //  ForwardLit - billboard, pick the octahedral cell for the view, sample
        //  albedo/depth/mask, repigment + thin for the season, dither-clip, and
        //  write the depth-displaced SV_Depth so terrain occludes it correctly.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex UCImpVert
            #pragma fragment UCImpFrag
            #pragma multi_compile _ _UC_IMPOSTOR_FULL
            #pragma multi_compile _ _UC_IMPOSTOR_SEASON
            #pragma multi_compile_fog

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 cardUV     : TEXCOORD0;
                float3 viewDirOS  : TEXCOORD1; // impostor->camera in object (bake) space
                float3 cardWS     : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            // Camera-facing billboard: the card spans the bounds diameter, oriented to the
            // view matrix's right/up, centred on the object origin (the impostor centre).
            Varyings UCImpVert(Attributes IN)
            {
                float3 centerWS = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float3 camRight = float3(UNITY_MATRIX_V._m00, UNITY_MATRIX_V._m01, UNITY_MATRIX_V._m02);
                float3 camUp    = float3(UNITY_MATRIX_V._m10, UNITY_MATRIX_V._m11, UNITY_MATRIX_V._m12);
                float  size     = 2.0 * max(1e-3, _BoundsRadiusM);

                float3 posWS = centerWS + (camRight * IN.positionOS.x + camUp * IN.positionOS.y) * size;
                float3 dirWS = _WorldSpaceCameraPos - centerWS;

                Varyings OUT;
                OUT.cardWS     = posWS;
                OUT.cardUV     = IN.uv;
                OUT.viewDirOS  = normalize(TransformWorldToObjectDir(dirWS));
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 UCImpFrag(Varyings IN, out float outDepth : SV_Depth) : SV_Target
            {
                float2 cellOrigin;
                float2 auv = UC_AtlasUV(IN.viewDirOS, IN.cardUV, cellOrigin, _ViewsPerAxis);

                half4 albedo = SAMPLE_TEXTURE2D(_AtlasAlbedo, sampler_AtlasAlbedo, auv);
                half4 mask   = SAMPLE_TEXTURE2D(_AtlasMask,   sampler_AtlasMask,   auv);
                float depth01 = SAMPLE_TEXTURE2D(_AtlasDepth,  sampler_AtlasDepth,  auv).r;

                float alpha = albedo.a;
            #if defined(_UC_IMPOSTOR_SEASON)
                float keep = UC_LeafKeep(mask.r, mask.a, _LeafOffFraction);
                alpha *= keep;
                albedo.rgb = UC_RepigmentLeaf(albedo.rgb, _SeasonColor.rgb, _SeasonTint, mask.r);
            #endif
                // Alpha cutout, then the dither cross-fade clip: keep while the fragment's
                // blue-noise threshold is below the coverage t (DitherCrossFade.KeepIncoming).
                clip(alpha - _AlphaCutoff);
                clip(_FadeT - UC_DitherThreshold(IN.positionCS.xy));

                // Depth displacement: push the card point along the view ray by the decoded
                // metric offset, re-project, and write that clip depth so the ordinary z-test
                // occludes the impostor by terrain exactly as it occludes the mesh LOD.
                float offsetM = UC_DecodeDepthOffsetM(depth01, _HalfDepthM);
                float3 forwardWS = normalize(IN.cardWS - _WorldSpaceCameraPos);
                float4 dispCS = TransformWorldToHClip(IN.cardWS + forwardWS * offsetM);
                outDepth = dispCS.z / dispCS.w;

                half3 color = MixFog(albedo.rgb, IN.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  ShadowCaster - the same billboard and the same leaf-off / alpha clip,
        //  so the impostor casts the shadow of the canopy it actually shows.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex UCImpShadowVert
            #pragma fragment UCImpShadowFrag
            #pragma multi_compile _ _UC_IMPOSTOR_FULL
            #pragma multi_compile _ _UC_IMPOSTOR_SEASON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct ShadowV
            {
                float4 positionCS : SV_POSITION;
                float2 cardUV     : TEXCOORD0;
                float3 viewDirOS  : TEXCOORD1;
            };

            ShadowV UCImpShadowVert(Attributes IN)
            {
                float3 centerWS = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float3 camRight = float3(UNITY_MATRIX_V._m00, UNITY_MATRIX_V._m01, UNITY_MATRIX_V._m02);
                float3 camUp    = float3(UNITY_MATRIX_V._m10, UNITY_MATRIX_V._m11, UNITY_MATRIX_V._m12);
                float  size     = 2.0 * max(1e-3, _BoundsRadiusM);

                float3 posWS = centerWS + (camRight * IN.positionOS.x + camUp * IN.positionOS.y) * size;
                float3 nWS   = normalize(_WorldSpaceCameraPos - centerWS);

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
                OUT.cardUV     = IN.uv;
                OUT.viewDirOS  = normalize(TransformWorldToObjectDir(_WorldSpaceCameraPos - centerWS));
                return OUT;
            }

            half4 UCImpShadowFrag(ShadowV IN) : SV_Target
            {
                float2 cellOrigin;
                float2 auv = UC_AtlasUV(IN.viewDirOS, IN.cardUV, cellOrigin, _ViewsPerAxis);
                half4 albedo = SAMPLE_TEXTURE2D(_AtlasAlbedo, sampler_AtlasAlbedo, auv);
                float alpha = albedo.a;
            #if defined(_UC_IMPOSTOR_SEASON)
                half4 mask = SAMPLE_TEXTURE2D(_AtlasMask, sampler_AtlasMask, auv);
                alpha *= UC_LeafKeep(mask.r, mask.a, _LeafOffFraction);
            #endif
                clip(alpha - _AlphaCutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    // No FallBack: a Built-in fallback draws magenta under URP and hides the cause.
}
