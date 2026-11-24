// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UC_Rain.shader
//  Purpose: The raindrop and streak material. Alpha-blended, unlit and cheap, it
//           draws the particles the emitter spawns. Two things stop it looking
//           like a decal pasted over the world: it reads the scene depth so a
//           drop dissolves as it meets the ground or a roof instead of punching
//           through, and it fades under a canopy so it does not rain inside a
//           forest that has already caught the water in its leaves.
//
//  WHY DEPTH-AWARE AND CANOPY-AWARE
//  --------------------------------
//  A camera-parented rain volume has particles everywhere in front of the eye,
//  including behind walls and under eaves. Without the soft depth fade they draw
//  on top of solid geometry; without the canopy term they fall through a closed
//  canopy that L-? interception says never let them reach the ground. Both are
//  one multiply on the particle's alpha.
//
//  PIPELINE
//  --------
//  URP only ("RenderPipeline" = "UniversalPipeline", C-03). Requires the Depth
//  Texture on the URP asset for the soft fade; without it the fade term is 1 and
//  the drops simply stop dissolving, which is legible but harder.
// -----------------------------------------------------------------------------

Shader "UniverseCreator/Rain"
{
    Properties
    {
        [NoScaleOffset] _BaseMap    ("Streak Texture", 2D) = "white" {}
        _BaseColor                  ("Tint", Color) = (0.72, 0.76, 0.82, 0.35)
        _SoftFadeMetres             ("Soft Depth Fade (m)", Range(0.01, 5.0)) = 0.4
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
