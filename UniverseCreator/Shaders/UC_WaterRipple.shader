// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UC_WaterRipple.shader
//  Purpose: The interactive water simulation. Everything that disturbs water -
//           a footstep, a rock, a boat, a raindrop, a waterfall landing in a lake -
//           writes an impulse into this simulation, and this shader propagates it
//           outward as a real travelling wave.
//
//  WHY A SIMULATION AND NOT A SCROLLING TEXTURE
//  --------------------------------------------
//  Scrolling normal maps give you the ambient chop of open water and nothing else.
//  They cannot answer the only question that makes water read as water when
//  something touches it: where did the disturbance come from, and where has it got
//  to since. A ring that expands, slows, weakens, reflects off a bank and
//  interferes with the ring from the drop that landed next to it is not a texture
//  effect - it is the 2D wave equation, and it is cheap enough to just solve.
//
//  THE MATHS
//  ---------
//  Discretised wave equation on a regular grid:
//
//      h(t+1) = 2h(t) - h(t-1) + C * ( laplacian(h) )
//      C      = (c * dt / dx)^2
//
//  Stability needs C <= 0.5 on a 4-neighbour stencil; above that the grid
//  oscillates and blows up into a flickering checkerboard within a second. It is
//  clamped in the propagate pass rather than trusted to the C# side, because a
//  user dragging a "wave speed" slider must not be able to detonate the sim.
//
//  Two heights are needed - now and one step ago - because the equation is second
//  order in time. They live in R and G of a single RGHalf target, so one texture
//  fetch carries the whole state.
//
//  Half precision is enough and is chosen on purpose: the field is a displacement
//  in metres, typically under 0.3, and the visible artefact of running out of
//  mantissa is a ripple that stops decaying smoothly - which is exactly what the
//  damping term does anyway. Full float doubles the bandwidth of the hottest loop
//  in the water system for no visible gain.
//
//  THE MOVING WINDOW
//  -----------------
//  The simulation covers a square of world around the viewer, not the whole world.
//  When the viewer moves, the Scroll pass shifts the field by a whole number of
//  texels and the sim stays world-locked - ripples do not slide along with the
//  camera, which is the tell that gives away every naive implementation. Whole
//  texels matter: a fractional shift resamples the field every frame and the
//  bilinear filtering eats the ripples faster than the damping term does.
// -----------------------------------------------------------------------------

Shader "Hidden/UniverseCreator/WaterRipple"
{
    Properties
    {
        _MainTex ("State", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);        SAMPLER(sampler_MainTex);
        TEXTURE2D(_ObstacleTex);    SAMPLER(sampler_ObstacleTex);

        float4 _MainTex_TexelSize;

        // x propagation constant C, y damping per step,
        // z texel size, w border fade width in texels
        float4 _RippleParams;

        // xy scroll offset in texels, z resolution, w unused
        float4 _RippleScroll;

        struct BlitAttributes
        {
            float4 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
        };

        struct BlitVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        BlitVaryings BlitVert(BlitAttributes input)
        {
            BlitVaryings o;
            o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            o.uv = input.uv;
            return o;
        }
        ENDHLSL

        // -------------------------------------------------------------------
        //  Pass 0 - Propagate
        // -------------------------------------------------------------------
        Pass
        {
            Name "Propagate"

            HLSLPROGRAM
            #pragma vertex BlitVert
            #pragma fragment PropagateFrag

            half4 PropagateFrag(BlitVaryings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 t = _MainTex_TexelSize.xy;

                half2 centre = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rg;

                half l = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - float2(t.x, 0)).r;
                half r = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(t.x, 0)).r;
                half d = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - float2(0, t.y)).r;
                half u = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(0, t.y)).r;

                half laplacian = (l + r + d + u) - 4.0h * centre.r;

                // Clamped here, not on the C# side. A slider that can be dragged
                // past the CFL limit would turn the whole surface into a
                // flickering checkerboard, and the user would have no way to tell
                // that the number they moved was the cause.
                half C = clamp(_RippleParams.x, 0.0h, 0.5h);

                half next = 2.0h * centre.r - centre.g + C * laplacian;

                // Damping. Water loses ripple energy to viscosity and to the air,
                // and without this term a single splash rings forever - which
                // reads as a bug long before it reads as a physical property.
                next *= _RippleParams.y;

                // Absorbing border. A reflecting edge would draw the simulation
                // window as a visible square of standing waves out in open water,
                // where no bank exists to reflect off. Ripples fade to nothing in
                // the outer band instead, so they leave the window the way they
                // would leave into distance.
                float2 edge = min(uv, 1.0 - uv);
                float border = saturate(min(edge.x, edge.y) / max(1e-5, _RippleParams.w));
                next *= border;

                // Obstacles. Red channel of the obstacle map is dry land and boat
                // hulls: a wave that hits one bounces instead of passing through,
                // which is what makes a ripple in a bay behave like it is in a bay.
                half solid = SAMPLE_TEXTURE2D(_ObstacleTex, sampler_ObstacleTex, uv).r;
                next *= 1.0h - saturate(solid);

                return half4(next, centre.r, 0, 0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 1 - Impulse
        //
        //  Additive splat of every disturbance recorded this step, drawn as one
        //  batched mesh. Per-quad data arrives in the vertex stream: local disc
        //  coordinates in TEXCOORD0 and (strength, sharpness) in COLOR.
        //
        //  Blend One One rather than a read-modify-write pass, so two impulses
        //  landing on the same texel in the same step interfere the way two
        //  raindrops do instead of one winning.
        // -------------------------------------------------------------------
        Pass
        {
            Name "Impulse"
            Blend One One
            BlendOp Add
            ColorMask RG

            HLSLPROGRAM
            #pragma vertex ImpulseVert
            #pragma fragment ImpulseFrag

            struct ImpulseAttributes
            {
                float4 positionOS : POSITION;   // already in clip space, xy in -1..1
                float2 disc       : TEXCOORD0;  // -1..1 across the impulse
                float4 color      : COLOR;      // r strength, g sharpness, b ring
            };

            struct ImpulseVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 disc       : TEXCOORD0;
                float4 color      : COLOR;
            };

            ImpulseVaryings ImpulseVert(ImpulseAttributes input)
            {
                ImpulseVaryings o;
                o.positionCS = float4(input.positionOS.xy, 0.0, 1.0);
                o.disc = input.disc;
                o.color = input.color;
                return o;
            }

            half4 ImpulseFrag(ImpulseVaryings input) : SV_Target
            {
                float d = length(input.disc);
                if (d > 1.0) discard;

                // Two shapes, blended by the ring parameter.
                //
                //   dome  - a smooth depression, what a heavy body pushing into
                //           water leaves behind. Mass displacing volume.
                //   ring  - a crest with a trough inside it, what the crown of a
                //           raindrop impact looks like a millisecond after contact.
                //
                // Real impacts are a mixture and the ratio depends on how fast the
                // thing was going, so the emitter picks the blend rather than the
                // shader picking for it.
                float dome = cos(d * 1.5707963) ;
                dome = pow(saturate(dome), max(0.05, input.color.g));

                float ring = sin(d * 6.2831853) * (1.0 - d);

                float shape = lerp(dome, ring, saturate(input.color.b));

                // Written to R only: the impulse is a displacement of the current
                // height. Writing the previous height too would cancel the velocity
                // term and the ring would appear and then sit still.
                return half4(shape * input.color.r, 0, 0, 0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 2 - Scroll
        //
        //  Whole-texel shift so the field stays world-locked as the viewer moves.
        //  Newly exposed area is cleared rather than clamped: repeating the edge
        //  texel smears the last ripple into a stripe across the fresh strip.
        // -------------------------------------------------------------------
        Pass
        {
            Name "Scroll"

            HLSLPROGRAM
            #pragma vertex BlitVert
            #pragma fragment ScrollFrag

            half4 ScrollFrag(BlitVaryings input) : SV_Target
            {
                float2 uv = input.uv + _RippleScroll.xy * _MainTex_TexelSize.xy;

                if (any(uv < 0.0) || any(uv > 1.0)) return half4(0, 0, 0, 0);

                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 3 - Resolve
        //
        //  Turns the height field into what the water surface shader actually
        //  wants, so the surface shader costs one fetch instead of five:
        //
        //    R,G  surface slope from the ripple field
        //    B    height, for vertex displacement and for the CPU-free case
        //    A    disturbance energy, which drives dynamic foam
        //
        //  Energy is |dh/dt| and not |h|. A crest at the top of its travel has
        //  large height and zero velocity, and it is not where water goes white -
        //  the white is where the surface is being torn, which is where it is
        //  moving fastest. Using height instead puts foam rings in the wrong place
        //  by a quarter period, which reads as foam that lags behind its own splash.
        // -------------------------------------------------------------------
        Pass
        {
            Name "Resolve"

            HLSLPROGRAM
            #pragma vertex BlitVert
            #pragma fragment ResolveFrag

            // x height scale, y normal strength, z foam gain, w foam decay
            float4 _ResolveParams;

            half4 ResolveFrag(BlitVaryings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 t = _MainTex_TexelSize.xy;

                half2 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rg;

                half l = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - float2(t.x, 0)).r;
                half r = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(t.x, 0)).r;
                half d = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - float2(0, t.y)).r;
                half u = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(0, t.y)).r;

                half2 slope = half2(r - l, u - d) * _ResolveParams.y;

                half velocity = abs(c.r - c.g);
                half foam = saturate(velocity * _ResolveParams.z);

                return half4(slope.x, slope.y, c.r * _ResolveParams.x, foam);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 4 - Foam accumulate
        //
        //  Foam outlives the wave that made it. A boat's wake stays white behind
        //  the boat for several seconds after the ripple has passed, and a
        //  waterfall keeps a permanent white patch where it lands. Both are the
        //  same thing: entrained air rising back out of the water over time.
        //
        //  So foam is its own field with its own decay, fed by ripple energy and
        //  by direct emitters, rather than being read straight off the wave.
        // -------------------------------------------------------------------
        Pass
        {
            Name "FoamAccumulate"

            HLSLPROGRAM
            #pragma vertex BlitVert
            #pragma fragment FoamFrag

            TEXTURE2D(_FoamPrev);   SAMPLER(sampler_FoamPrev);

            // x gain from ripple energy, y decay per step, z spread, w unused
            float4 _FoamParams;

            half4 FoamFrag(BlitVaryings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 t = _MainTex_TexelSize.xy;

                half prev = SAMPLE_TEXTURE2D(_FoamPrev, sampler_FoamPrev, uv).r;

                // Spread. Foam is buoyant and drifts outward from where it was
                // made; a purely local decay leaves hard-edged discs that read as
                // decals rather than as froth.
                half n = SAMPLE_TEXTURE2D(_FoamPrev, sampler_FoamPrev, uv + float2(t.x, 0)).r
                       + SAMPLE_TEXTURE2D(_FoamPrev, sampler_FoamPrev, uv - float2(t.x, 0)).r
                       + SAMPLE_TEXTURE2D(_FoamPrev, sampler_FoamPrev, uv + float2(0, t.y)).r
                       + SAMPLE_TEXTURE2D(_FoamPrev, sampler_FoamPrev, uv - float2(0, t.y)).r;

                prev = lerp(prev, n * 0.25h, saturate(_FoamParams.z));

                half2 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rg;
                half energy = abs(c.r - c.g) * _FoamParams.x;

                // max, not add: foam saturates. Once a patch of water is fully
                // aerated, hitting it again does not make it whiter, it only
                // resets how long it stays.
                half foam = max(prev * _FoamParams.y, saturate(energy));

                return half4(foam, 0, 0, 0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
