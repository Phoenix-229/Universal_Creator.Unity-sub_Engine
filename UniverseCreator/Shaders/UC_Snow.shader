// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_Snow.shader
//  Purpose: The snowflake material. The same alpha-blended, depth-aware and
//           canopy-aware particle as rain, tinted white and opaque enough to read
//           as a flake rather than a streak. It shares its vertex and fragment
//           with the rain material - the only difference between falling rain and
//           falling snow the shader cares about is the tint and the softness; the
//           slow drift and the sideways blow are set by the emitter, not here.
//
//  PIPELINE
//  --------
//  URP only ("RenderPipeline" = "UniversalPipeline", C-03). Requires the Depth
//  Texture on the URP asset for the soft fade; without it the flakes simply stop
//  dissolving into the surfaces behind them.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Snow"
{
    Properties
    {
        [NoScaleOffset] _BaseMap    ("Flake Texture", 2D) = "white" {}
        _BaseColor                  ("Tint", Color) = (0.95, 0.96, 0.98, 0.85)
        _SoftFadeMetres             ("Soft Depth Fade (m)", Range(0.01, 5.0)) = 0.6
        _CanopyOpen01               ("Canopy Openness", Range(0.0, 1.0)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }

        Pass
        {
            Name "Precip"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex UCPrecipVert
            #pragma fragment UCPrecipFrag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "UC_PrecipParticle.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
