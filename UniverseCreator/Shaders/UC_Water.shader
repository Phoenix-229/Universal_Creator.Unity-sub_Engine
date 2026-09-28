// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UC_Water.shader
//  Purpose: The water surface shader. Oceans, lakes and rivers are all drawn by
//           this one material, driven by the T19-T21 water chain in the texture
//           graph and by the vertex stream UCWaterBuilder writes.
//
//  WHY IT IS NOT PART OF UC_TerrainLit
//  -----------------------------------
//  Transparency. The terrain shader is opaque, sorts in Geometry-100, and writes
//  depth for every pixel; water needs the transparent queue, a depth read of the
//  ground behind it, and a blend. Bolting that onto the terrain shader costs
//  every opaque terrain pixel a branch it never takes - and it still would not
//  work, because a terrain cannot sample the depth of itself.
//
//  WHAT DRIVES THE LOOK
//  --------------------
//  One number: how much water the view ray passes through before it hits the
//  ground. That is the scene depth minus the surface's own depth, in metres, and
//  it drives four things at once - the shallow-to-deep colour ramp, the opacity,
//  the refraction strength and the caustic fade. Real water works the same way
//  (Beer-Lambert absorption), which is why driving them all from one term is what
//  makes shallows read as shallow instead of as a separate tint someone painted.
//
//  REQUIREMENTS
//  ------------
//  Opaque Texture and Depth Texture must be enabled on the URP asset. Without
//  them the shader falls back to a flat deep colour - legible, but wrong.
//  UCWaterApplier turns both on when it builds the material and
//  UCShaderDiagnostics reports it if they were switched off afterwards.
//
//  The properties here mirror WaterProduct field for field, because
//  UCWaterApplier copies one onto the other and a name that exists on only one
//  side is a control the user can move that does nothing.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Water"
{
    Properties
    {
        [Header(Colour)]
        _ShallowColor       ("Shallow Colour", Color) = (0.29, 0.61, 0.66, 0.72)
        _DeepColor          ("Deep Colour", Color) = (0.03, 0.13, 0.20, 0.95)
        _DepthFadeMetres    ("Depth Fade (m)", Range(0.05, 60.0)) = 6.0
        _Transparency       ("Transparency", Range(0.0, 1.0)) = 0.75
        _Smoothness         ("Smoothness", Range(0.0, 1.0)) = 0.92
        _FresnelPower       ("Fresnel Power", Range(0.5, 16.0)) = 5.0
        _SpecularTint       ("Specular Tint", Color) = (1,1,1,1)

        [Header(Waves)]
        [NoScaleOffset] _WaveNormalMap ("Wave Normal", 2D) = "bump" {}
        _WaveTileMetres     ("Wave Tile (m)", Range(0.5, 200.0)) = 12.0
        _NormalScale        ("Normal Strength", Range(0.0, 4.0)) = 1.0
        _FlowSpeed          ("Uniform Flow (m/s XZ)", Vector) = (0.06, 0.03, 0, 0)
        _SecondaryScale     ("Secondary Tile Ratio", Range(0.05, 1.0)) = 0.37
        _Refraction         ("Refraction", Range(0.0, 0.2)) = 0.03
        [ToggleUI] _UseVertexFlow ("Use Mesh Flow", Float) = 1.0
        _FlowStrength       ("Mesh Flow Scale", Range(0.0, 8.0)) = 1.0

        [Header(Foam)]
        [ToggleUI] _FoamEnabled ("Foam", Float) = 0.0
        [NoScaleOffset] _FoamTexture ("Foam Texture", 2D) = "white" {}
        _FoamColor          ("Foam Colour", Color) = (1,1,1,1)
        _FoamIntensity      ("Foam Intensity", Range(0.0, 4.0)) = 1.0
        _FoamEdgeWidth      ("Foam Edge Width (m)", Range(0.01, 12.0)) = 1.2
        _FoamScrollSpeed    ("Foam Scroll", Range(0.0, 2.0)) = 0.15
        _FoamTileMetres     ("Foam Tile (m)", Range(0.1, 40.0)) = 4.0
        _FlowFoam           ("Whitewater From Flow", Range(0.0, 2.0)) = 1.0

        [Header(Caustics)]
        [ToggleUI] _CausticsEnabled ("Caustics", Float) = 0.0
        [NoScaleOffset] _CausticsTexture ("Caustics Texture", 2D) = "black" {}
        _CausticsColor      ("Caustics Colour", Color) = (1,1,1,1)
        _CausticsIntensity  ("Caustics Intensity", Range(0.0, 4.0)) = 0.6
        _CausticsTileMetres ("Caustics Tile (m)", Range(0.1, 40.0)) = 3.0
        _CausticsSpeed      ("Caustics Speed", Range(0.0, 2.0)) = 0.05
        _CausticsDepthFade  ("Caustics Depth Fade (m)", Range(0.1, 60.0)) = 8.0

        [Header(Surface motion)]
        _WaveHeight         ("Vertex Wave Height (m)", Range(0.0, 4.0)) = 0.12
        _WaveLength         ("Vertex Wave Length (m)", Range(0.5, 200.0)) = 26.0
        _WaveSpeed          ("Vertex Wave Speed", Range(0.0, 4.0)) = 0.6
        // Active wave-train count and weather scale. Set per body kind / by the weather
        // layer, not hand-tuned; defaults reproduce the original three-octave calm swell.
        _WaveOctaves        ("Wave Octaves", Range(1.0, 4.0)) = 3.0
        _WaveStormAmp       ("Storm Swell x", Range(1.0, 4.0)) = 1.0
        _WaveFlowAdvect     ("Swell Flow Advection", Range(0.0, 2.0)) = 0.0

        [Header(Shore)]
        _EdgeSoftenMetres   ("Shoreline Soften (m)", Range(0.0, 8.0)) = 0.6

        [Header(Interactive ripples)]
        _RippleStrength     ("Ripple Strength", Range(0.0, 4.0)) = 1.0
        _RippleFoam         ("Ripple Foam", Range(0.0, 4.0)) = 1.0

        [Header(Wind)]
        _WindRipple         ("Wind Chop", Range(0.0, 4.0)) = 1.0
        _WindChopLength     ("Wind Chop Length (m)", Range(0.15, 6.0)) = 0.9
        _WindFoam           ("Wind Whitecaps", Range(0.0, 2.0)) = 0.35

        // Frozen fraction, set by the weather layer per body and only on still water.
        // Authored default is open water; it is not a tuning control.
        [HideInInspector] _UCIce ("Ice Cover", Range(0.0, 1.0)) = 0.0
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }

        // -------------------------------------------------------------------
        //  ForwardLit - the only pass that shades. Water deliberately casts no
        //  shadow: a transparent caster darkens the very bed it is meant to be
        //  showing through, and since it writes no depth here, nothing behind it
        //  is wrongly occluded either.
        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual

            // Two-sided, so the surface is still there when you are under it.
            //
            // Back-face culled water is invisible from below: you dive in and the
            // ceiling of the pool disappears, leaving the sky drawn straight over
            // the top of the world. The underside is the single most recognisable
            // thing about being underwater - it is the mirror you look up into -
            // and it costs nothing but the cull state to keep it.
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCWaterVert
            #pragma fragment UCWaterFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "UC_WaterInput.hlsl"
            #include "UC_WaterPasses.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  DepthOnly. Water must NOT end up in _CameraDepthTexture - the
        //  ForwardLit pass reads that texture to work out how deep the water is,
        //  and a surface that wrote its own depth there would measure zero
        //  everywhere and render as one uniform shoreline. URP's depth prepass
        //  only collects opaque geometry, and this material is in the
        //  Transparent queue, so the pass never runs there. It exists so that a
        //  renderer feature which explicitly asks for it - an underwater mask, a
        //  planar reflection clip - has something to draw.
        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            // Matches ForwardLit. A mask that culls the underside would disagree with
            // the pass it exists to mask.
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCWaterDepthVert
            #pragma fragment UCWaterDepthFrag
            #pragma multi_compile_instancing

            #include "UC_WaterInput.hlsl"
            #include "UC_WaterPasses.hlsl"
            ENDHLSL
        }
    }

    // No FallBack, for the same reason UC_TerrainLit has none: a Built-in
    // fallback under URP draws magenta and hides where the failure came from.

    CustomEditor "UniverseCreator.Editor.Drawing.UCWaterShaderGUI"
}
