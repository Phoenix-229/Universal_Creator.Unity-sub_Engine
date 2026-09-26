// -----------------------------------------------------------------------------
//  Universe Creator - lit vegetation. Bark, branches, leaves, needles, flowers.
//
//  WHY THIS EXISTS
//
//  The shipped art came with its own shaders, authored to be general. They work,
//  but they are strangers to this world: their wind is a per-material intensity
//  with a local pulse frequency, so every plant in the scene invents its own
//  weather; their leaf graphs offset vertices by GradientNoise(position + Time),
//  which is motion without a wind direction; and the bark graphs sample three
//  textures and do nothing else, so a trunk stands rigid while its own leaves
//  thrash. Nothing in any of them can know that it is raining, that the season
//  has turned, or that this branch is above the snow line.
//
//  This shader is the same surface model, rebuilt to read the world instead of
//  guessing at it. Every behaviour the old shaders had is still here - both
//  light-transport terms, the smoothness-or-roughness source switch, occlusion
//  from a map or from vertex colour, alpha remapping, the detail coat. What
//  changed is where the numbers come from.
//
//  WHAT IT READS, AND FROM WHOM
//
//    UCWindManager       one wind for the whole world, through UC_Wind.hlsl.
//                        Direction, strength, travelling gust cells, the eight
//                        interactors, and the baked sway map that makes a
//                        sheltered hollow move less than an exposed ridge.
//    UCWeatherSystemManager \  season phase, surface wetness, snow line and
//    UCVegetationSystemManager/ intensity, through UC_Season.hlsl.
//    UCUnderwaterView    the medium the fragment is seen through.
//
//  All of it arrives as globals. A scene with none of those managers renders a
//  still, dry, snowless, high-summer plant - correct, not broken.
//
//  USE IT ON: any vegetation drawn as an ordinary mesh - trunk, bark, branch,
//  leaf, needle, frond, flower, bud.
//
//  ON TREES, YES - AND THAT IS NOT A CONTRADICTION OF UC_WindDetail.
//  UC_WindDetail says not to use it on trees because Unity's *tree* shaders are
//  driven by the WindZone UCWindManager creates, and a Unity Tree component
//  reads that and nothing else. This shader is for trees that are plain meshes
//  with a MeshRenderer, which is what every non-SpeedTree tree in the library
//  is. Those get no WindZone bending at all, so vertex displacement is the only
//  route by which they can move, and _VegetationClass = Tree is the right model
//  for them: UCWindHeightProfile holds the lower 55 % of a tree still and moves
//  only what is above it, which is a trunk that stays put under a canopy that
//  does not. SpeedTree materials are deliberately left on their own shader -
//  they already reach this world's wind through the WindZone, and their vertex
//  wind data and camera-facing billboards cannot be consumed by a generic pass.
//
//  SET _VegetationClass TO MATCH THE PLANT. It selects a motion model, not a
//  style: grass is a cantilever that lies over and flutters fast, a shrub is
//  woody with a dead lower quarter, a tree holds at the trunk. Getting it wrong
//  is the most reliable way to make a correct scene look wrong.
//
//  PHONE
//
//  Lighting goes through URP's own UniversalFragmentPBR, so every path URP
//  optimises for mobile - Forward+ clustering, vertex SH, light cookies, the
//  graded soft-shadow variants - is taken as written rather than reimplemented.
//  Everything optional is a shader_feature_local, so a bark material compiles
//  without the translucency, transmission and season branches at all. The season
//  and wind maths is a handful of scalar operations on uniforms already in
//  registers; it costs no texture fetch.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/VegetationLit"
{
    Properties
    {
        [MainTexture] _BaseMap   ("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Brightness ("Brightness", Range(0.0, 2.0)) = 1.0

        [Header(Alpha)]
        _Cutoff       ("Alpha Cutoff", Range(0.0, 1.0)) = 0.4
        _AlphaRemapMin("Alpha Remap Min", Range(0.0, 1.0)) = 0.0
        _AlphaRemapMax("Alpha Remap Max", Range(0.0, 1.0)) = 1.0

        // This library's base colour maps (_BC) are three-channel: there is no alpha
        // in them for the cutoff to clip against. Opacity ships beside them as its
        // own _M map - measured as a hard mask, 99% of its pixels at 0 or 255 with a
        // 1% antialiased edge. Reading alpha off a _BC sample instead hands the
        // cutoff a constant 1, which draws every leaf card as a full opaque rectangle.
        [Toggle(_OPACITYMAP)] _OpacityMapEnable ("Opacity From Separate Map", Float) = 0
        _OpacityMap ("Opacity Map (_M)", 2D) = "white" {}

        [Header(Surface)]
        [Toggle(_NORMALMAP)] _NormalMapEnable ("Use Normal Map", Float) = 0
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0.0, 3.0)) = 1.0

        [Toggle(_METALLICGLOSSMAP)] _MaskMapEnable ("Use Metallic/Smoothness Map", Float) = 0
        _MetallicGlossMap ("Packed Mask, or Standalone Roughness (_R)", 2D) = "white" {}
        _Metallic   ("Metallic", Range(0.0, 1.0)) = 0.0
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.25

        // Two independent questions, and this library answers both differently from
        // the packed HDRP-style mask a general shader expects.
        //
        // WHICH CHANNEL: a packed mask carries metallic in red and smoothness in
        // alpha. The library instead ships a single-channel _R map, which Unity
        // expands to r=g=b=value with alpha forced to 1 - so reading alpha there
        // reports every surface as perfectly smooth, whatever the map says.
        [Toggle(_SMOOTHNESS_FROM_RED)] _SmoothnessFromRed ("Map Is Single Channel (Read Red)", Float) = 0
        // WHICH DIRECTION: packs disagree about which way round the channel runs,
        // and a roughness map read as smoothness turns matt bark into wet plastic.
        [Toggle(_SMOOTHNESS_FROM_ROUGHNESS)] _SmoothnessFromRoughness ("Map Holds Roughness", Float) = 0

        [Toggle(_OCCLUSIONMAP)] _OcclusionMapEnable ("Use Occlusion Map", Float) = 0
        _OcclusionMap ("Occlusion Map", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0.0, 1.0)) = 1.0
        // Scanned foliage usually ships its ambient occlusion baked into vertex
        // colour rather than as a map, because a leaf card has no room for one.
        [Toggle(_OCCLUSION_FROM_VERTEXCOLOR)] _OcclusionFromVertexColor ("Occlusion From Vertex Color", Float) = 0

        [Header(Leaf Light Transport)]
        // Two separate physical effects. Transmission is light that has passed
        // through the blade and scattered, which you see from any angle. Translucency
        // is the forward lobe you only see looking towards the sun through the leaf.
        // A leaf needs both; collapsing them into one loses the glow at grazing sun.
        [Toggle(_TRANSMISSION_ON)] _TransmissionEnable ("Enable Transmission", Float) = 0
        _TransmissionMap   ("Transmission Mask, or Subsurface Colour (_SSC)", 2D) = "white" {}
        [HDR] _TransmissionColor ("Transmission Color", Color) = (0.32, 0.45, 0.18, 1)
        _TransmissionStrength ("Transmission Strength", Range(0.0, 4.0)) = 1.0
        // The library ships _SSC: a full-colour map of what light looks like once it
        // has been through the blade, which is a warmer, more saturated green than
        // the leaf's own face. Read as a single-channel mask it throws that colour
        // away and contributes only its brightness.
        [Toggle(_TRANSMISSION_COLORMAP)] _TransmissionColorMap ("Map Is Subsurface Colour (RGB)", Float) = 0

        [Toggle(_TRANSLUCENCY_ON)] _TranslucencyEnable ("Enable Translucency", Float) = 0
        _ThicknessMap ("Thickness Mask", 2D) = "white" {}
        [HDR] _TranslucencyColor ("Translucency Color", Color) = (0.55, 0.72, 0.30, 1)
        _TranslucencyStrength   ("Translucency Strength", Range(0.0, 8.0)) = 1.0
        _TranslucencyPower      ("Translucency Falloff", Range(1.0, 32.0)) = 6.0
        _TranslucencyDistortion ("Normal Distortion", Range(0.0, 1.0)) = 0.15

        // A thin leaf lit as an opaque surface goes black the moment it faces
        // away. Wrapping keeps the far side readable, which is what a translucent
        // blade actually does.
        _AmbientWrap ("Ambient Wrap", Range(0.0, 1.0)) = 0.45

        [Header(Leaf Detail Realism)]
        // Beer-Lambert. The thickness map is optical path length through the blade
        // in mean-free-paths; the fraction of backlight that survives it is
        // e^(-path/lambda). _LeafDensity is 1/lambda, so a thin edge (path -> 0)
        // glows near full while a thick midrib stays dark - the organic backlit
        // falloff a flat "* thickness" multiply cannot produce. Only read when
        // _TRANSLUCENCY_ON, so it costs nothing on bark.
        _LeafDensity ("Leaf Optical Density (Beer-Lambert 1/lambda)", Range(0.1, 8.0)) = 2.0

        // Retroreflective canopy sheen: the pale halo along a leaf edge turned to
        // the sun and the silver flash of back-lit grass. An inverted-Fresnel rim,
        // gated by how lit the face is so it never glows in shade. One pow, no map.
        [Toggle(_SHEEN_ON)] _SheenEnable ("Enable Canopy Sheen", Float) = 0
        [HDR] _SheenColor ("Sheen Color", Color) = (0.90, 0.95, 0.80, 1)
        _SheenStrength ("Sheen Strength", Range(0.0, 2.0)) = 0.5
        _SheenPower ("Sheen Falloff", Range(1.0, 16.0)) = 4.0

        // Alpha-to-coverage. A hard per-pixel cutoff erodes a leaf card's silhouette
        // into a boxy block on distant mips as the mask's antialiased rim drops below
        // the threshold. Feeding MSAA a sharpened coverage instead keeps the edge
        // sub-pixel accurate at range. Needs MSAA; inert without it. The one toggle
        // sets both the render state and the shader branch.
        [Toggle(_ALPHATOCOVERAGE)] _AlphaToMask ("Alpha To Coverage (needs MSAA)", Float) = 0

        [Header(Wind)]
        [Enum(Grass,0,Shrub,1,Tree,2)] _VegetationClass ("Vegetation Class", Float) = 2
        _WindResponse ("Wind Response", Range(0.0, 3.0)) = 1.0
        _BendHeight   ("Bend From Height", Range(0.0, 1.0)) = 1.0
        _Flutter      ("Flutter", Range(0.0, 2.0)) = 0.5
        _PivotHeight  ("Mesh Height (m)", Range(0.05, 60.0)) = 8.0
        _TouchResponse("Push Aside When Walked Through", Range(0.0, 2.0)) = 0.5

        [Header(Season and Weather)]
        [Toggle(_SEASONTINT_ON)] _SeasonTintEnable ("Enable Seasonal Colour", Float) = 0
        _SpringColor ("Spring Tint", Color) = (0.62, 0.78, 0.42, 1)
        _SummerColor ("Summer Tint", Color) = (1.00, 1.00, 1.00, 1)
        _AutumnColor ("Autumn Tint", Color) = (0.86, 0.62, 0.26, 1)
        _WinterColor ("Winter Tint", Color) = (0.66, 0.60, 0.48, 1)
        _SeasonTintStrength ("Seasonal Colour Strength", Range(0.0, 1.0)) = 1.0

        [Toggle] _Deciduous ("Deciduous (drops leaves)", Float) = 0
        _LeafOffFraction ("Leaf Off Fraction", Range(0.0, 1.0)) = 0.85

        _WetDarkening ("Wet Darkening", Range(0.0, 1.0)) = 0.4
        _SnowCanopyAccumulation ("Snow Canopy Accumulation", Range(0.0, 1.0)) = 0.5

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        // Off-cull foliage lit with one normal has half its cards lit backwards.
        [Toggle(_DOUBLESIDED_NORMALS)] _DoubleSidedNormals ("Flip Normal On Back Faces", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "TransparentCutout"
            "Queue"           = "AlphaTest"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "ShaderModel"     = "3.0"
        }

        LOD 300

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "UC_Wind.hlsl"
        #include "UC_Season.hlsl"
        #include "UC_Lighting.hlsl"
        #include "UC_SurfaceReflectance.hlsl"   // the ray-free wet-sky reflection twin, shared with terrain, props and the mobile path

        // One block, shared by every pass, so the SRP Batcher can bind it once.
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half4  _TransmissionColor;
            half4  _TranslucencyColor;
            half4  _SpringColor;
            half4  _SummerColor;
            half4  _AutumnColor;
            half4  _WinterColor;
            half4  _SheenColor;
            half   _Brightness;
            half   _Cutoff;
            half   _AlphaRemapMin;
            half   _AlphaRemapMax;
            half   _NormalStrength;
            half   _Metallic;
            half   _Smoothness;
            half   _OcclusionStrength;
            half   _TransmissionStrength;
            half   _TranslucencyStrength;
            half   _TranslucencyPower;
            half   _TranslucencyDistortion;
            half   _AmbientWrap;
            half   _LeafDensity;
            half   _SheenStrength;
            half   _SheenPower;
            half   _AlphaToMask;
            half   _VegetationClass;
            half   _WindResponse;
            half   _BendHeight;
            half   _Flutter;
            half   _PivotHeight;
            half   _TouchResponse;
            half   _SeasonTintStrength;
            half   _Deciduous;
            half   _LeafOffFraction;
            half   _WetDarkening;
            half   _SnowCanopyAccumulation;
            half   _Cull;
        CBUFFER_END

        TEXTURE2D(_BaseMap);          SAMPLER(sampler_BaseMap);
        TEXTURE2D(_OpacityMap);       SAMPLER(sampler_OpacityMap);
        TEXTURE2D(_BumpMap);          SAMPLER(sampler_BumpMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_OcclusionMap);     SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_TransmissionMap);  SAMPLER(sampler_TransmissionMap);
        TEXTURE2D(_ThicknessMap);     SAMPLER(sampler_ThicknessMap);

        // ---------------------------------------------------------------------
        //  Displacement. ONE function, called identically by all four passes.
        //  Copied into each pass instead and a shadow that bends by a fraction
        //  less detaches from the branch and crawls along the ground as the wind
        //  moves; depth that disagrees puts the plant's own outline through it.
        // ---------------------------------------------------------------------
        float3 UCVegetationDisplace(float3 positionWS, float3 positionOS)
        {
            // Object-space Y over the authored mesh height is 0 at the root and 1
            // at the tip. It is the only anchor that keeps a base planted while a
            // tip travels, and it is why _PivotHeight must be the real height of
            // the mesh in metres rather than a feel dial.
            float heightFrac = saturate(positionOS.y / max(0.001, _PivotHeight));
            heightFrac = lerp(1.0, heightFrac, _BendHeight);

            // The pivot, not the vertex, chooses the gust. A 20 m tree spanning
            // two gust cells would otherwise shear down its own trunk.
            float3 pivotWS = TransformObjectToWorld(float3(0, 0, 0));

            int vegClass = (int)(_VegetationClass + 0.5);

            float3 offset = UCWindDisplacementClass(pivotWS, heightFrac,
                                                    _WindResponse, _Flutter, vegClass);

            // Added, not blended: something walking through parts the foliage on a
            // still day too.
            offset += UCWindPush(positionWS, heightFrac, _TouchResponse);

            return offset;
        }

        /// Base colour and alpha, with the season applied. Shared so that the
        /// alpha the shadow and depth passes clip against is the same alpha the
        /// lit pass shades - a deciduous canopy going bare must lose its shadows
        /// at the same rate it loses its leaves.
        half4 UCSampleBaseAndSeason(float2 uv, float3 positionWS, half4 vertexColor)
        {
            half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);

            half4 albedo = tex * _BaseColor;
            albedo.rgb *= _Brightness * vertexColor.rgb;

#ifdef _OPACITYMAP
            // Assigned, not multiplied. The alpha this stands in for does not exist
            // in a three-channel _BC sample, so what came back was a constant 1 and
            // multiplying against it would only pass that constant through.
            albedo.a = SAMPLE_TEXTURE2D(_OpacityMap, sampler_OpacityMap, uv).r * _BaseColor.a;
#endif

            // Remap before the cutoff so a pack whose alpha never reaches 1 can be
            // brought back into range without moving the cutoff itself.
            albedo.a = saturate((albedo.a - _AlphaRemapMin) /
                               max(1e-4, _AlphaRemapMax - _AlphaRemapMin));

#ifdef _SEASONTINT_ON
            half3 tint = UCSeasonTint(_SpringColor.rgb, _SummerColor.rgb,
                                      _AutumnColor.rgb, _WinterColor.rgb);
            // Repigment, not multiply. A multiply cannot turn a green leaf
            // orange - green albedo has no red for the tint to scale, so it only
            // darkens. See UCRepigment in UC_Season.hlsl.
            albedo.rgb = UCRepigment(albedo.rgb, tint, _SeasonTintStrength);
#endif

            // Going bare thins the canopy out rather than fading it to grey, which
            // is what losing leaves looks like against a sky.
            albedo.a *= UCLeafOnFraction(positionWS, _LeafOffFraction, _Deciduous);

            return albedo;
        }

        ENDHLSL

        // =====================================================================
        //  Forward lit
        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma vertex   VegetationVert
            #pragma fragment VegetationFrag
            #pragma target 3.0

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _DOUBLESIDED_NORMALS
            #pragma shader_feature_local _SEASONTINT_ON
            #pragma shader_feature_local _OPACITYMAP
            #pragma shader_feature_local _ALPHATOCOVERAGE
            #pragma shader_feature_local_fragment _METALLICGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_FROM_RED
            #pragma shader_feature_local_fragment _SMOOTHNESS_FROM_ROUGHNESS
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _OCCLUSION_FROM_VERTEXCOLOR
            #pragma shader_feature_local_fragment _TRANSMISSION_ON
            #pragma shader_feature_local_fragment _TRANSMISSION_COLORMAP
            #pragma shader_feature_local_fragment _TRANSLUCENCY_ON
            #pragma shader_feature_local_fragment _SHEEN_ON

            // URP 17 keyword set, verified against the installed package rather
            // than remembered. _CLUSTER_LIGHT_LOOP is what Forward+ is called
            // now; the old _FORWARD_PLUS spelling silently compiles to no
            // additional lights at all, which is the bug this replaces.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ LINEAR_TO_SRGB_CONVERSION
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            // --- variant stripping (T-OOM) -------------------------------------
            // These families are declared above only because this pass mirrors
            // URP's stock Lit ForwardLit keyword set. They are dead weight HERE:
            // Universe Creator vegetation is procedurally scattered as runtime
            // GPU instances, never marked static and never contributing to a
            // baked lightmap, so Unity never selects a *_LIGHTMAP_* / shadowmask
            // variant for these draws - the ON branches at lines ~423/452/565 are
            // unreachable at runtime. Compiling all six baked-GI keywords still
            // multiplies the ForwardLit variant matrix, which is the memory the
            // first-placement compile ran out of (see [Step 2]: the OOM is the
            // built-in matrix under RAM pressure, not a UC keyword). Skipping them
            // removes only variants that could never run; the code keeps its
            // #ifdef fallbacks (SH / no shadowmask), so nothing breaks - a skipped
            // variant just resolves to the path instanced foliage already takes.
            // The graded soft-shadow tiers collapse to the base _SHADOWS_SOFT
            // (soft shadows still work, one quality tier), and DEBUG_DISPLAY is a
            // dev-only rendering-debugger view. All reversible: delete this line.
            #pragma skip_variants LIGHTMAP_ON DYNAMICLIGHTMAP_ON DIRLIGHTMAP_COMBINED USE_LEGACY_LIGHTMAPS LIGHTMAP_SHADOW_MIXING SHADOWS_SHADOWMASK _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH DEBUG_DISPLAY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            #include "UC_Underwater.hlsl"

            struct Attributes
            {
                float4 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float4 tangentOS   : TANGENT;
                float2 uv          : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                float2 dynamicLightmapUV : TEXCOORD2;
                half4  color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  tangentWS  : TEXCOORD3;
                half4  color      : TEXCOORD4;
                float  fogCoord   : TEXCOORD5;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
#ifdef DYNAMICLIGHTMAP_ON
                float2 dynamicLightmapUV : TEXCOORD7;
#endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings VegetationVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCVegetationDisplace(positionWS, IN.positionOS.xyz);

                VertexNormalInputs normalInput = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionWS = positionWS;
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS   = normalInput.normalWS;
                OUT.tangentWS  = half4(normalInput.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.color      = IN.color;
                OUT.fogCoord   = ComputeFogFactor(OUT.positionCS.z);

                OUTPUT_LIGHTMAP_UV(IN.staticLightmapUV, unity_LightmapST, OUT.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
                OUT.dynamicLightmapUV = IN.dynamicLightmapUV * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
                OUTPUT_SH4(positionWS, OUT.normalWS.xyz, GetWorldSpaceNormalizeViewDir(positionWS), OUT.vertexSH, OUT.staticLightmapUV);

                return OUT;
            }

            half4 VegetationFrag(Varyings IN
#ifdef _DOUBLESIDED_NORMALS
                , FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC
#endif
            ) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

#ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(IN.positionCS);
#endif

                half4 albedo = UCSampleBaseAndSeason(IN.uv, IN.positionWS, IN.color);

                // Coverage the fragment writes as its alpha. Opaque (1) unless
                // alpha-to-coverage is on, where it becomes the sharpened mask so
                // MSAA can resolve the leaf edge sub-pixel instead of clipping it
                // into a boxy block on distant mips.
                half coverage = 1.0h;
#ifdef _ALPHATOCOVERAGE
                // Sharpen the antialiased rim to ~one pixel around the cutoff, then
                // clip only what is fully outside. fwidth gives the per-pixel rate
                // of change of the mask, so the divide widens a hard step into a
                // one-texel ramp - exactly what a coverage mask wants.
                coverage = saturate((albedo.a - _Cutoff) / max(fwidth(albedo.a), 1e-5h) + 0.5h);
                clip(coverage - 0.0001h);
#else
                clip(albedo.a - _Cutoff);
#endif

                // --- geometry ------------------------------------------------
                half3 normalWS = normalize(IN.normalWS);

#ifdef _DOUBLESIDED_NORMALS
                // A leaf card is one triangle standing for two surfaces. Without
                // this the back half of every plant is lit as though its normal
                // pointed into the ground.
                normalWS *= IS_FRONT_VFACE(frontFace, 1.0, -1.0);
#endif

#ifdef _NORMALMAP
                half4 nSample = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv);
                half3 normalTS = UnpackNormalScale(nSample, _NormalStrength);

                half  sgn = IN.tangentWS.w;
                half3 tangentWS = normalize(IN.tangentWS.xyz);
                half3 bitangentWS = sgn * normalize(cross(normalWS, tangentWS));
                normalWS = normalize(mul(normalTS, half3x3(tangentWS, bitangentWS, normalWS)));
#endif

                float3 viewDirWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);

                // --- material ------------------------------------------------
                half metallic   = _Metallic;
                half smoothness = _Smoothness;

#ifdef _METALLICGLOSSMAP
                half4 mg = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, IN.uv);

    #ifdef _SMOOTHNESS_FROM_RED
                // A standalone map holds one channel of information and no metallic
                // at all, so red is the whole story and _Metallic stays scalar.
                half gloss = mg.r;
    #else
                half gloss = mg.a;
                metallic  *= mg.r;
    #endif

    #ifdef _SMOOTHNESS_FROM_ROUGHNESS
                gloss = 1.0h - gloss;
    #endif
                smoothness *= gloss;
#endif

                half occlusion = 1.0h;
#ifdef _OCCLUSIONMAP
                half ao = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, IN.uv).g;
                occlusion = lerp(1.0h, ao, _OcclusionStrength);
#elif defined(_OCCLUSION_FROM_VERTEXCOLOR)
                occlusion = lerp(1.0h, IN.color.a, _OcclusionStrength);
#endif

                // --- weather, in physical order ------------------------------
                // Wet first: a surface gets wet, and only then does snow settle on
                // the wet surface. Reversed, rain would darken the snow instead of
                // the bark under it.
                half wetness = UCWetnessAt(normalWS);
                UCApplyWetness(albedo.rgb, smoothness, wetness, _WetDarkening);

                half snow = UCSnowAt(IN.positionWS, normalWS, _SnowCanopyAccumulation);
                UCApplySnow(albedo.rgb, smoothness, metallic, snow, wetness);

                // --- URP lighting --------------------------------------------
                InputData inputData = (InputData)0;
                inputData.positionWS         = IN.positionWS;
                inputData.positionCS         = IN.positionCS;
                inputData.normalWS           = normalWS;
                inputData.viewDirectionWS    = viewDirWS;
                inputData.shadowCoord        = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord           = IN.fogCoord;
                inputData.vertexLighting     = half3(0, 0, 0);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask         = SAMPLE_SHADOWMASK(IN.staticLightmapUV);

#if defined(DYNAMICLIGHTMAP_ON)
                inputData.bakedGI = SAMPLE_GI(IN.staticLightmapUV, IN.dynamicLightmapUV,
                                              IN.vertexSH, normalWS);
#elif !defined(LIGHTMAP_ON) && defined(USE_APV_PROBE_OCCLUSION)
                inputData.bakedGI = SAMPLE_GI(IN.vertexSH, GetAbsolutePositionWS(IN.positionWS),
                                              normalWS, viewDirWS,
                                              inputData.normalizedScreenSpaceUV,
                                              half4(0, 0, 0, 0), inputData.shadowMask);
#else
                inputData.bakedGI = SAMPLE_GI(IN.staticLightmapUV, IN.vertexSH, normalWS);
#endif
                // A tree is lit by the valley it stands in: replace the engine ambient with
                // the shared irradiance grid where one is bound, so the trunk's ambient tracks
                // the ground beneath it continuously as it moves into shade. No grid, no change.
                inputData.bakedGI = UCGridAmbientOr(inputData.bakedGI, IN.positionWS, normalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = albedo.rgb;
                surfaceData.alpha      = 1.0h;
                surfaceData.metallic   = metallic;
                surfaceData.smoothness = smoothness;
                surfaceData.occlusion  = occlusion;
                surfaceData.normalTS   = half3(0, 0, 1);
                surfaceData.emission   = half3(0, 0, 0);
                surfaceData.specular   = half3(0, 0, 0);
                surfaceData.clearCoatMask       = 0.0h;
                surfaceData.clearCoatSmoothness = 0.0h;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                // --- leaf light transport ------------------------------------
                // Added after the BRDF because it is light that never reflected off
                // the surface at all - it came through the blade. Both terms are
                // shadow-attenuated: a leaf in shade is not backlit. The canopy
                // sheen shares the same main-light fetch rather than paying for a
                // second one.
#if defined(_TRANSMISSION_ON) || defined(_TRANSLUCENCY_ON) || defined(_SHEEN_ON)
                Light mainLight = GetMainLight(inputData.shadowCoord, IN.positionWS, inputData.shadowMask);
                half3 lightEnergy = mainLight.color * mainLight.shadowAttenuation * mainLight.distanceAttenuation;
                half  ndotl = dot(normalWS, mainLight.direction);

    #ifdef _TRANSMISSION_ON
                // View-independent: light scattered inside the leaf and out the
                // far side. Strongest where the surface faces away from the sun.
                half4 tm = SAMPLE_TEXTURE2D(_TransmissionMap, sampler_TransmissionMap, IN.uv);
    #ifdef _TRANSMISSION_COLORMAP
                // The map already *is* the colour of transmitted light, so it stands
                // in for the albedo rather than masking it. Feeding both in tints the
                // same light twice and drives the green towards black.
                half3 subsurface = tm.rgb;
    #else
                half3 subsurface = albedo.rgb * tm.r;
    #endif
                half wrapped = saturate((-ndotl + _AmbientWrap) / (1.0h + _AmbientWrap));
                color.rgb += subsurface * _TransmissionColor.rgb
                           * (lightEnergy * wrapped * _TransmissionStrength);
    #endif

    #ifdef _TRANSLUCENCY_ON
                // View-dependent forward lobe: the glow you only get looking
                // toward the sun through the leaf. Distorting the light vector by
                // the normal is what softens it off a flat card.
                half thickness = SAMPLE_TEXTURE2D(_ThicknessMap, sampler_ThicknessMap, IN.uv).r;
                // Beer-Lambert: the map is optical path in mean-free-paths, so the
                // surviving backlight fraction is e^(-path/lambda) = exp(-t*density).
                // A thin edge transmits almost fully; a thick midrib goes dark. This
                // replaces the old linear "* thickness", which lit the whole blade
                // evenly and read as a flat card.
                half transmit  = exp(-thickness * _LeafDensity);
                half3 transDir = normalize(mainLight.direction + normalWS * _TranslucencyDistortion);
                half  forward  = pow(saturate(dot(-viewDirWS, transDir)), _TranslucencyPower);
                color.rgb += _TranslucencyColor.rgb
                           * (lightEnergy * forward * transmit * _TranslucencyStrength);
    #endif

    #ifdef _SHEEN_ON
                // Retroreflective canopy sheen. Inverted Fresnel gives a rim that
                // brightens as the surface turns edge-on to the eye; gating it by
                // the lit side (saturate(ndotl)) keeps it a sunlit highlight, not a
                // uniform glow, so it reads as waxy leaf or silvered grass rather
                // than fog. lightEnergy already carries colour, shadow and falloff.
                half grazing = pow(1.0h - saturate(dot(normalWS, viewDirWS)), _SheenPower);
                color.rgb += _SheenColor.rgb
                           * (lightEnergy * saturate(ndotl) * grazing * _SheenStrength);
    #endif
#endif

                color.rgb += UCEnvReflectionContribution(IN.positionWS, normalWS, viewDirWS,
                    1.0h - saturate(smoothness), wetness, 0.0, occlusion, normalWS);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                // Last, because everything above happened at the surface and this
                // is the water between the surface and the eye. Aquatic plants that
                // skip it read as pasted over the seabed rather than growing in it.
                color.rgb = UCApplyUnderwater(color.rgb, IN.positionWS);

                // Coverage is 1.0 unless _ALPHATOCOVERAGE sharpened the cutout into
                // a per-sample MSAA fraction above; returning it lets the hardware
                // antialias the leaf silhouette so distant mips stop eroding cards
                // into hard boxes. Opaque queue + AlphaToMask, not alpha blending.
                return half4(color.rgb, coverage);
            }
            ENDHLSL
        }

        // =====================================================================
        //  Shadows. Same displacement, same alpha, for the reasons above.
        // =====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.0

            #pragma shader_feature_local _SEASONTINT_ON
            #pragma shader_feature_local _OPACITYMAP
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4  color      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCVegetationDisplace(positionWS, IN.positionOS.xyz);

                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
                float3 lightDirectionWS = _LightDirection;
#endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

                OUT.positionCS = positionCS;
                OUT.positionWS = positionWS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color      = IN.color;
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
#ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(IN.positionCS);
#endif
                half4 albedo = UCSampleBaseAndSeason(IN.uv, IN.positionWS, IN.color);
                clip(albedo.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        //  Depth only
        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma target 3.0

            #pragma shader_feature_local _SEASONTINT_ON
            #pragma shader_feature_local _OPACITYMAP
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4  color      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCVegetationDisplace(positionWS, IN.positionOS.xyz);

                OUT.positionWS = positionWS;
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color      = IN.color;
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
#ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(IN.positionCS);
#endif
                half4 albedo = UCSampleBaseAndSeason(IN.uv, IN.positionWS, IN.color);
                clip(albedo.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        //  Depth normals. Screen-space ambient occlusion and decals need a
        //  normal buffer; without this pass every plant would punch a hole in
        //  that buffer and those effects would skip vegetation entirely.
        // =====================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma target 3.0

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _DOUBLESIDED_NORMALS
            #pragma shader_feature_local _SEASONTINT_ON
            #pragma shader_feature_local _OPACITYMAP
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            // The deferred renderer asks the depth-normals prepass for
            // octahedral-packed normals rather than raw world-space ones. Without
            // this the buffer is written in the wrong encoding and every plant
            // feeds SSAO and decals a wrong normal. UC_RockLit already handles it;
            // this keeps the two identical rather than subtly divergent.
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  tangentWS  : TEXCOORD3;
                half4  color      : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                positionWS += UCVegetationDisplace(positionWS, IN.positionOS.xyz);

                VertexNormalInputs normalInput = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionWS = positionWS;
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS   = normalInput.normalWS;
                OUT.tangentWS  = half4(normalInput.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.color      = IN.color;
                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN
#ifdef _DOUBLESIDED_NORMALS
                , FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC
#endif
            ) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
#ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(IN.positionCS);
#endif
                half4 albedo = UCSampleBaseAndSeason(IN.uv, IN.positionWS, IN.color);
                clip(albedo.a - _Cutoff);

                half3 normalWS = normalize(IN.normalWS);
#ifdef _DOUBLESIDED_NORMALS
                normalWS *= IS_FRONT_VFACE(frontFace, 1.0, -1.0);
#endif
#ifdef _NORMALMAP
                half4 nSample = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv);
                half3 normalTS = UnpackNormalScale(nSample, _NormalStrength);
                half  sgn = IN.tangentWS.w;
                half3 tangentWS = normalize(IN.tangentWS.xyz);
                half3 bitangentWS = sgn * normalize(cross(normalWS, tangentWS));
                normalWS = normalize(mul(normalTS, half3x3(tangentWS, bitangentWS, normalWS)));
#endif
                normalWS = NormalizeNormalPerPixel(normalWS);

#if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remapped    = saturate(octNormalWS * 0.5 + 0.5);
                half3  packed      = PackFloat2To888(remapped);
                return half4(packed, 0.0);
#else
                return half4(normalWS, 0.0);
#endif
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
