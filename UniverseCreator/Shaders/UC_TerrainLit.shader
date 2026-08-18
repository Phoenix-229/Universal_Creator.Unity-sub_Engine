// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    UC_TerrainLit.shader
//  Purpose: The terrain uber-shader. Every surface the texture graph authors is
//           drawn by this one shader, driven by properties and keywords.
//
//  WHY AN UBER-SHADER
//  ------------------
//  The alternative - generating HLSL per graph and compiling it at edit time -
//  means shipping a shader compiler, a variant cache, and a class of failure
//  where a user's graph produces code that will not compile on their platform.
//  A fixed shader with keyword-gated features has a variant count we can count,
//  compiles once, and fails visibly rather than at import time.
//
//  RELATIONSHIP TO URP's TerrainLit
//  --------------------------------
//  This is a standalone terrain shader, not a variant of URP's. It keeps URP's
//  contract with the terrain engine exactly - the same _Control/_Splat/_Normal/
//  _Mask property names, the same instancing path, the same hole handling - so
//  Unity's own terrain system, basemap generation and painting tools all work
//  against it unchanged. What it adds on top is the UC control surface: height
//  blending, altitude/slope gradient tinting, mud, slope-aware triplanar cliffs,
//  snow, a distance-faded detail overlay, macro variation, wetness and puddles,
//  baked landform occlusion, global PBR remapping, emission, distance fade and
//  colour grading - all of which the texture graph drives.
//
//  VARIANT BUDGET
//  --------------
//  Two keywords, not fourteen. Every feature above is gated, but only slope
//  blending and snow are gated by a shader keyword; the rest branch on a uniform
//  float. Fourteen keyword toggles would be 16384 variants multiplied by URP's
//  own lighting matrix. The full reasoning, including why these two are the
//  exceptions, is at the top of UC_TerrainLitInput.hlsl.
//
//  Keeping the engine-facing names identical is deliberate. A terrain component
//  writes _Control and _Splat0..3 by name; rename them and Unity silently paints
//  into properties nothing reads.
//
//  PASSES
//  ------
//  ForwardLit / ShadowCaster / DepthOnly / DepthNormals. All four are required:
//  omitting DepthNormals breaks SSAO and any depth-normals-based effect, and
//  omitting DepthOnly breaks the depth prepass the renderer may run.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/TerrainLit"
{
    Properties
    {
        // ---- set by the terrain engine, by name. Do not rename. ----
        [HideInInspector] _Control       ("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat0        ("Layer 0 (R)", 2D) = "grey" {}
        [HideInInspector] _Splat1        ("Layer 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Splat2        ("Layer 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Splat3        ("Layer 3 (A)", 2D) = "grey" {}
        [HideInInspector] _Normal0       ("Normal 0 (R)", 2D) = "bump" {}
        [HideInInspector] _Normal1       ("Normal 1 (G)", 2D) = "bump" {}
        [HideInInspector] _Normal2       ("Normal 2 (B)", 2D) = "bump" {}
        [HideInInspector] _Normal3       ("Normal 3 (A)", 2D) = "bump" {}
        [HideInInspector] _Mask0         ("Mask 0 (R)", 2D) = "grey" {}
        [HideInInspector] _Mask1         ("Mask 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Mask2         ("Mask 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Mask3         ("Mask 3 (A)", 2D) = "grey" {}

        [HideInInspector][Gamma] _Metallic0 ("Metallic 0", Range(0.0, 1.0)) = 0.0
        [HideInInspector][Gamma] _Metallic1 ("Metallic 1", Range(0.0, 1.0)) = 0.0
        [HideInInspector][Gamma] _Metallic2 ("Metallic 2", Range(0.0, 1.0)) = 0.0
        [HideInInspector][Gamma] _Metallic3 ("Metallic 3", Range(0.0, 1.0)) = 0.0
        [HideInInspector] _Smoothness0   ("Smoothness 0", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Smoothness1   ("Smoothness 1", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Smoothness2   ("Smoothness 2", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Smoothness3   ("Smoothness 3", Range(0.0, 1.0)) = 0.5

        [HideInInspector] _MainTex       ("BaseMap (RGB)", 2D) = "grey" {}
        [HideInInspector] _BaseColor     ("Main Color", Color) = (1,1,1,1)
        [HideInInspector] _TerrainHolesTexture ("Holes Map (RGB)", 2D) = "white" {}
        [HideInInspector] [PerRendererData] _NumLayersCount ("Total Layer Count", Float) = 1.0

        // ---- UC control surface. The texture graph writes these. ----
        //
        // Almost every _Enable* below is a plain float the fragment shader
        // branches on rather than a shader keyword. The reasoning is recorded in
        // full in UC_TerrainLitInput.hlsl; the short form is that ten keyword
        // toggles would be 1024 variants on top of URP's own lighting matrix,
        // and a uniform branch costs one scalar compare with no wave divergence.
        // Only Slope and Snow - six texture fetches each - earn a real keyword.

        [Header(Blending)]
        [ToggleUI] _EnableHeightBlend ("Height Blend", Float) = 0.0
        _HeightTransition   ("Height Transition", Range(0.0, 1.0)) = 0.2

        [Header(Occlusion)]
        _OcclusionStrength  ("Occlusion Strength", Range(0.0, 1.0)) = 1.0
        _CavityStrength     ("Cavity Darkening", Range(0.0, 2.0)) = 0.0
        [ToggleUI] _EnableAOMap ("Baked AO Map", Float) = 0.0
        _AOMap              ("Ambient Occlusion", 2D) = "white" {}
        _AOMapStrength      ("AO Map Strength", Range(0.0, 1.0)) = 1.0

        [Header(Macro Variation)]
        [ToggleUI] _EnableMacroVariation ("Macro Variation", Float) = 0.0
        _MacroVariationScale ("Macro Scale (m)", Float) = 320.0
        _MacroVariationAmount("Macro Amount", Range(0.0, 1.0)) = 0.35
        _MacroVariationColor ("Macro Tint", Color) = (0.72, 0.76, 0.62, 1.0)

        [Header(Slope Blending)]
        [ToggleUI] _EnableSlopeBlend ("Slope Blend", Float) = 0.0
        _SlopeTex           ("Slope Albedo", 2D) = "grey" {}
        _SlopeNormal        ("Slope Normal", 2D) = "bump" {}
        _SlopeColor         ("Slope Tint", Color) = (1,1,1,1)
        _SlopeAngleStart    ("Slope Start (deg)", Range(0.0, 90.0)) = 35.0
        _SlopeAngleEnd      ("Slope Full (deg)", Range(0.0, 90.0)) = 55.0
        _SlopeSmoothness    ("Slope Smoothness", Range(0.0, 1.0)) = 0.35
        _SlopeMetallic      ("Slope Metallic", Range(0.0, 1.0)) = 0.0
        _SlopeNormalStrength("Slope Normal Strength", Range(0.0, 4.0)) = 1.0
        _SlopeTriplanarScale("Slope Triplanar Scale (m)", Float) = 6.0

        [Header(Snow)]
        [ToggleUI] _EnableSnow ("Snow Cover", Float) = 0.0
        _SnowTex            ("Snow Albedo", 2D) = "white" {}
        _SnowNormal         ("Snow Normal", 2D) = "bump" {}
        _SnowColor          ("Snow Tint", Color) = (0.92, 0.95, 1.0, 1.0)
        _SnowAltitude       ("Snow Line (m)", Float) = 120.0
        _SnowFalloff        ("Snow Line Falloff (m)", Float) = 30.0
        _SnowSlopeLimit     ("Snow Slope Limit (deg)", Range(0.0, 90.0)) = 42.0
        _SnowSlopeFalloff   ("Snow Slope Falloff (deg)", Range(0.1, 45.0)) = 8.0
        _SnowCoverage       ("Snow Coverage", Range(0.0, 1.0)) = 1.0
        _SnowSmoothness     ("Snow Smoothness", Range(0.0, 1.0)) = 0.55
        _SnowNormalStrength ("Snow Normal Strength", Range(0.0, 4.0)) = 0.6
        _SnowTriplanarScale ("Snow Triplanar Scale (m)", Float) = 8.0

        [Header(Wetness)]
        [ToggleUI] _EnableWetness ("Wetness", Float) = 0.0
        _WetnessAmount      ("Global Wetness", Range(0.0, 1.0)) = 0.0
        _WetDarkening       ("Wet Darkening", Range(0.0, 0.9)) = 0.45
        _WetSmoothness      ("Wet Smoothness", Range(0.0, 1.0)) = 0.85
        _WetSpecBoost       ("Puddle Gloss Boost", Range(0.0, 0.5)) = 0.15
        _PuddleThreshold    ("Puddle Cavity Threshold", Range(0.0, 1.0)) = 0.45
        _PuddleLevel        ("Puddle Water Level (m)", Float) = 0.0
        _PuddleFalloff      ("Puddle Depth Falloff (m)", Float) = 2.0

        [Header(Mud and Dirt)]
        [ToggleUI] _EnableMud ("Mud", Float) = 0.0
        _MudTex             ("Mud Albedo", 2D) = "grey" {}
        _MudColor           ("Mud Tint", Color) = (0.42, 0.33, 0.24, 1.0)
        _MudScale           ("Mud Scale (m)", Float) = 4.0
        _MudThreshold       ("Mud Cavity Threshold", Range(0.0, 1.0)) = 0.4
        _MudBlend           ("Mud Amount", Range(0.0, 1.0)) = 0.0
        _MudSmoothness      ("Mud Smoothness", Range(0.0, 1.0)) = 0.25
        _MudSlopeLimit      ("Mud Slope Limit (deg)", Range(1.0, 90.0)) = 18.0

        [Header(Detail Overlay)]
        [ToggleUI] _EnableDetail ("Detail Overlay", Float) = 0.0
        _DetailTex          ("Detail Texture", 2D) = "grey" {}
        _DetailScale        ("Detail Scale (m)", Float) = 1.5
        _DetailStrength     ("Detail Strength", Range(0.0, 1.0)) = 0.5
        [Enum(Overlay,0,Multiply,1,Add,2)] _DetailBlendMode ("Detail Blend", Float) = 0.0
        _DetailFadeStart    ("Detail Fade Start (m)", Float) = 20.0
        _DetailFadeEnd      ("Detail Fade End (m)", Float) = 60.0

        [Header(Colour Gradient)]
        [ToggleUI] _EnableGradient ("Colour Gradient", Float) = 0.0
        _GradientTex        ("Gradient LUT", 2D) = "white" {}
        [Enum(Altitude,0,Slope,1)] _GradientMode ("Gradient Source", Float) = 0.0
        [Enum(Multiply,0,Overlay,1,Replace,2)] _GradientBlendMode ("Gradient Blend", Float) = 0.0
        _GradientMin        ("Gradient Min", Float) = 0.0
        _GradientMax        ("Gradient Max", Float) = 200.0
        _GradientStrength   ("Gradient Strength", Range(0.0, 1.0)) = 1.0

        [Header(Emission)]
        [ToggleUI] _EnableEmission ("Emission", Float) = 0.0
        _EmissionMap        ("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor("Emission Colour", Color) = (0,0,0,1)
        _EmissionIntensity  ("Emission Intensity", Range(0.0, 16.0)) = 1.0

        [Header(PBR Remap)]
        _SmoothnessRemapMin ("Smoothness Min", Range(0.0, 1.0)) = 0.0
        _SmoothnessRemapMax ("Smoothness Max", Range(0.0, 1.0)) = 1.0
        _MetallicScale      ("Metallic Scale", Range(0.0, 2.0)) = 1.0
        _RoughnessVariationScale  ("Roughness Drift Scale (m)", Float) = 60.0
        _RoughnessVariationAmount ("Roughness Drift", Range(0.0, 0.5)) = 0.0

        [Header(Distance)]
        [ToggleUI] _EnableDistanceFade ("Distance Fade", Float) = 0.0
        _DistanceFadeStart  ("Fade Start (m)", Float) = 180.0
        _DistanceFadeEnd    ("Fade End (m)", Float) = 900.0

        [Header(Colour Grade)]
        _Saturation         ("Saturation", Range(0.0, 2.0)) = 1.0
        _Brightness         ("Brightness", Range(0.0, 2.0)) = 1.0
        _Contrast           ("Contrast", Range(0.0, 2.0)) = 1.0
        _TintColor          ("Global Tint", Color) = (1,1,1,1)

        [ToggleUI] _EnableInstancedPerPixelNormal ("Instanced per-pixel normal", Float) = 1.0
    }

    HLSLINCLUDE
    #pragma multi_compile_fragment __ _ALPHATEST_ON
    ENDHLSL

    SubShader
    {
        // Geometry-100 matches URP's terrain queue so terrain sorts before the
        // opaque geometry that sits on it. TerrainCompatible tells the terrain
        // system this shader may be assigned to a Terrain at all.
        Tags
        {
            "Queue"           = "Geometry-100"
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "False"
            "TerrainCompatible" = "True"
        }

        // -------------------------------------------------------------------
        //  ForwardLit - the only pass that shades. The rest write depth.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCTerrainVert
            #pragma fragment UCTerrainFrag

            // Terrain layers carry smoothness in the albedo alpha and metallic
            // in a mask map; both are URP conventions this shader keeps.
            #define _METALLICSPECGLOSSMAP 1
            #define _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A 1

            // ---- URP lighting keywords ----
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            // _FORWARD_PLUS is deprecated in URP 17.5 in favour of
            // _CLUSTER_LIGHT_LOOP, declared above. Declaring both makes the
            // compiler warn and doubles this axis of the variant matrix.
            #pragma multi_compile_fragment _ DEBUG_DISPLAY

            // ---- Unity built-ins ----
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ _WRITE_RENDERING_LAYERS

            // ---- terrain ----
            #pragma multi_compile_local_fragment __ _ALPHATEST_ON
            #pragma multi_compile_fragment __ _TERRAIN_INSTANCED_PERPIXEL_NORMAL
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            // ---- UC features. multi_compile_local keeps these out of the
            //      global keyword budget, which is a hard 256 per project.
            //
            //      Only two. Each of these costs six extra texture fetches,
            //      which a uniform branch cannot avoid paying for in register
            //      pressure even when the branch is not taken. Height blend,
            //      macro variation, distance fade, detail, gradient, mud,
            //      wetness, AO and emission are all uniform branches instead:
            //      arithmetic-only or a single fetch, and each keyword added
            //      here would double the variant count for the whole shader. ----
            #pragma multi_compile_local_fragment __ _UC_SLOPEBLEND_ON
            #pragma multi_compile_local_fragment __ _UC_SNOW_ON

            #include "UC_TerrainLitInput.hlsl"
            #include "UC_TerrainLitPasses.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  ShadowCaster - terrain casting onto itself and onto scene geometry.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCShadowVert
            #pragma fragment UCShadowFrag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "UC_TerrainLitInput.hlsl"
            #include "UC_TerrainLitDepthPasses.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  DepthOnly - depth prepass and anything sampling the depth texture.
        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCDepthOnlyVert
            #pragma fragment UCDepthOnlyFrag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "UC_TerrainLitInput.hlsl"
            #include "UC_TerrainLitDepthPasses.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  DepthNormals - SSAO and any depth-normal effect reads this. A
        //  terrain missing it shows up as terrain-shaped holes in the AO.
        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCDepthNormalsVert
            #pragma fragment UCDepthNormalsFrag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile_fragment __ _TERRAIN_INSTANCED_PERPIXEL_NORMAL
            #pragma multi_compile_fragment _ _WRITE_RENDERING_LAYERS

            #include "UC_TerrainLitInput.hlsl"
            #include "UC_TerrainLitDepthPasses.hlsl"
            ENDHLSL
        }
    }

    // No FallBack. A fallback to a Built-in shader under URP draws magenta
    // anyway, and swallowing the error makes the cause harder to find than
    // letting the shader fail to compile loudly.

    // The terrain engine resolves these three by exact string, and a name it
    // cannot find is reported once at import and then silently ignored - the
    // symptom is distant terrain going untextured, or a fifth painted layer not
    // drawing, neither of which points at a missing dependency.
    //
    //   AddPassShader   - used when a terrain carries more than four layers. Our
    //                     graph caps the splat set at four, but the native
    //                     Terrain inspector is part of the deliverable and a user
    //                     may paint a fifth by hand.
    //   BaseMapShader   - the low-detail pass beyond Basemap Distance.
    //   BaseMapGenShader- bakes that basemap.
    //
    // All three delegate to URP's own terrain shaders: they render the standard
    // four-layer splat without the UC features, which is exactly right for
    // terrain far enough away that snow lines and cliff blending are subpixel.
    Dependency "AddPassShader"    = "Hidden/Universal Render Pipeline/Terrain/Lit (Add Pass)"
    Dependency "BaseMapShader"    = "Hidden/Universal Render Pipeline/Terrain/Lit (Base Pass)"
    Dependency "BaseMapGenShader" = "Hidden/Universal Render Pipeline/Terrain/Lit (Basemap Gen)"

    CustomEditor "UniverseCreator.Editor.Drawing.UCTerrainShaderGUI"
}
