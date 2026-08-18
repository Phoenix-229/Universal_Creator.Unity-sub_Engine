#ifndef UC_TERRAIN_LIT_PASSES_INCLUDED
#define UC_TERRAIN_LIT_PASSES_INCLUDED

// -----------------------------------------------------------------------------
//  Universe Creator - UC_TerrainLitPasses.hlsl
//  The forward lit pass: splat blending, and the UC features layered on top.
//
//  ORDER OF OPERATIONS, and why it is this order
//  ---------------------------------------------
//     1. control map            decides how much of each layer
//     2. height blend           rock reads through gravel instead of cross-fading
//     3. sample the four layers albedo, normal, mask
//     4. gradient tint          altitude or slope colouring of the painted ground
//     5. mud                    collects in low, flat, concave ground
//     6. slope / cliff          replaces the painted layers on steep faces
//     7. snow                   settles on top of ground AND cliff alike
//     8. detail overlay         breaks up tiling at close range
//     9. macro variation        large-scale drift over everything
//    10. wetness                darkens and polishes whatever is underneath
//    11. occlusion              layer AO, cavity, baked AO map
//    12. PBR remap              global smoothness/metallic authoring
//    13. distance fade          detail normals settle toward flat
//    14. colour grade           final artistic control
//
//  The sequence is not arbitrary. Each rule below was chosen because the
//  alternative produces a specific artefact:
//
//  - Mud before slope, because mud is ground behaviour. Running it after would
//    smear mud up vertical cliff faces, where in the world it would have run off.
//  - Slope before snow, because snow lies on top of exposed rock as readily as
//    on grass. Reversed, every cliff would punch a snow-free hole through an
//    otherwise white mountainside.
//  - Macro variation after both, so the same large-scale colour drift crosses
//    cliff, ground and snow together. Applied earlier, the cliffs would be
//    visibly exempt from it and read as pasted on.
//  - Wetness last of the surface features, because rain falls on whatever is
//    already there. Wet snow, wet rock and wet mud are all correct outcomes;
//    dry snow over a wet cliff is not.
// -----------------------------------------------------------------------------

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "UC_Underwater.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 uvSplat01                : TEXCOORD0;
    float4 uvSplat23                : TEXCOORD1;
    float4 uvControlAndLM           : TEXCOORD2;   // xy = control uv, zw = lightmap
    float3 positionWS               : TEXCOORD3;
    half3  normalWS                 : TEXCOORD4;
    half4  tangentWS                : TEXCOORD5;   // w = sign
    half4  fogFactorAndVertexLight  : TEXCOORD6;

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD7;
#endif

    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// -----------------------------------------------------------------------------
//  Helpers
// -----------------------------------------------------------------------------

/// Cheap value-noise hash. Used only for macro variation and roughness drift,
/// where the eye reads the low-frequency change and never the exact pattern, so
/// a full gradient noise would cost ALU for a difference nobody can see.
float UCHash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float UCValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    // Smoothstep the interpolant so the result is C1 continuous; linear
    // interpolation here produces visible grid creases at this scale.
    float2 u = f * f * (3.0 - 2.0 * f);

    float a = UCHash21(i);
    float b = UCHash21(i + float2(1, 0));
    float c = UCHash21(i + float2(0, 1));
    float d = UCHash21(i + float2(1, 1));

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

/// Two octaves an irrational ratio apart. The ratio matters: an integer
/// multiple lines the two lattices up and produces a visible grid.
float UCMacroNoise(float2 p)
{
    return UCValueNoise(p) * 0.6 + UCValueNoise(p * 2.317) * 0.4;
}

/// Rebalances four splat weights by per-layer height, so the taller material
/// wins where they overlap instead of the two cross-fading into mud.
/// transition -> 0 makes the boundary a hard cut; -> 1 restores a soft blend.
half4 UCHeightBlend(half4 weights, half4 heights, half transition)
{
    // Guard the transition: at exactly 0 the reciprocal below divides by zero
    // and the whole surface goes NaN, which reads as black on most GPUs.
    half t = max(transition, 0.001h);

    half maxHeight = max(max(heights.x, heights.y), max(heights.z, heights.w));

    // Only materials within `t` of the tallest still contribute. The saturate
    // keeps a layer that is far below from going negative.
    half4 blend = saturate((heights - maxHeight.xxxx + t) / t) * weights;

    half total = blend.x + blend.y + blend.z + blend.w;
    return total > 0.0001h ? blend / total : weights;
}

/// Triplanar projection weights, shared by the cliff and snow samplers so both
/// agree on where each of the three planes takes over.
half3 UCTriplanarWeights(half3 normalWS)
{
    // Power sharpens the transition between the three projections; without it
    // a 45-degree face shows all three blended at once and loses contrast.
    half3 w = pow(abs(normalWS), 4.0h);
    return w / max(w.x + w.y + w.z, 0.0001h);
}

/// Triplanar sample through the shared linear-repeat sampler. Terrain UVs
/// stretch badly on a near-vertical face - that stretching is exactly what
/// makes a cliff read as smeared - so these materials project from world space.
half4 UCTriplanar(TEXTURE2D_PARAM(tex, samp), float3 positionWS, half3 w, float scale)
{
    float3 uvw = positionWS / max(scale, 0.001);

    half4 x = SAMPLE_TEXTURE2D(tex, samp, uvw.zy);
    half4 y = SAMPLE_TEXTURE2D(tex, samp, uvw.xz);
    half4 z = SAMPLE_TEXTURE2D(tex, samp, uvw.xy);

    return x * w.x + y * w.y + z * w.z;
}

/// Rec. 709 luma. Used by the saturation control so desaturating keeps
/// perceived brightness instead of darkening greens and lightening blues.
half UCLuma(half3 c)
{
    return dot(c, half3(0.2126h, 0.7152h, 0.0722h));
}

/// Photoshop's overlay: multiply in the shadows, screen in the highlights, so
/// the blend adds contrast without shifting the midpoint.
half3 UCOverlay(half3 baseColor, half3 blend)
{
    return baseColor < 0.5h ? (2.0h * baseColor * blend)
                            : (1.0h - 2.0h * (1.0h - baseColor) * (1.0h - blend));
}

/// Smooth 0..1 ramp across a band centred on `edge`, `width` wide. Used for
/// every altitude and angle threshold so nothing in this shader steps.
half UCBand(half value, half edge, half width)
{
    half w = max(width, 0.0001h);
    return saturate((value - edge + w) / (2.0h * w));
}

// -----------------------------------------------------------------------------
//  Vertex
// -----------------------------------------------------------------------------
Varyings UCTerrainVert(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 normalOS = input.normalOS;
    UCTerrainInstancing(input.positionOS, normalOS, input.texcoord);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    output.uvControlAndLM.xy = TRANSFORM_TEX(input.texcoord, _Control);
    output.uvSplat01.xy = TRANSFORM_TEX(input.texcoord, _Splat0);
    output.uvSplat01.zw = TRANSFORM_TEX(input.texcoord, _Splat1);
    output.uvSplat23.xy = TRANSFORM_TEX(input.texcoord, _Splat2);
    output.uvSplat23.zw = TRANSFORM_TEX(input.texcoord, _Splat3);

    // The terrain mesh carries no tangents, so one is derived. Terrain UVs run
    // along world X/Z, which makes world +X the tangent direction by
    // construction - this is the same assumption URP's terrain shader makes.
    half3 normalWS  = TransformObjectToWorldNormal(normalOS);
    half3 tangentWS = cross(GetObjectToWorldMatrix()._13_23_33, normalWS);

    output.normalWS  = normalWS;
    output.tangentWS = half4(tangentWS, 1.0h);
    output.positionWS = vertexInput.positionWS;
    output.positionCS = vertexInput.positionCS;

    OUTPUT_LIGHTMAP_UV(input.texcoord, unity_LightmapST, output.uvControlAndLM.zw);

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalWS);
    half fogFactor = 0.0h;
#if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
#endif
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    return output;
}

// -----------------------------------------------------------------------------
//  Fragment
// -----------------------------------------------------------------------------
half4 UCTerrainFrag(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#ifdef _ALPHATEST_ON
    ClipHoles(input.uvControlAndLM.xy);
#endif

    float2 uvControl = input.uvControlAndLM.xy;
    half4 splatControl = SAMPLE_TEXTURE2D(_Control, sampler_Control, uvControl);

    // ---- layer albedo + smoothness ----
    half4 albedo0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, input.uvSplat01.xy);
    half4 albedo1 = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, input.uvSplat01.zw);
    half4 albedo2 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, input.uvSplat23.xy);
    half4 albedo3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, input.uvSplat23.zw);

    // ---- masks: R metallic, G occlusion, B height, A smoothness ----
    // Sampled with the splat samplers - see the sampler-budget note in
    // UC_TerrainLitInput.hlsl. Same texture, same tiling, same filtering.
    half4 mask0 = SAMPLE_TEXTURE2D(_Mask0, sampler_Splat0, input.uvSplat01.xy);
    half4 mask1 = SAMPLE_TEXTURE2D(_Mask1, sampler_Splat1, input.uvSplat01.zw);
    half4 mask2 = SAMPLE_TEXTURE2D(_Mask2, sampler_Splat2, input.uvSplat23.xy);
    half4 mask3 = SAMPLE_TEXTURE2D(_Mask3, sampler_Splat3, input.uvSplat23.zw);

    // A layer with no mask map assigned must not contribute a black mask; fall
    // back to "no metal, full ambient, mid height" so an unmasked layer looks
    // like a plain material rather than a hole.
    mask0 = lerp(half4(0, 1, 0.5h, 0.5h), mask0, _LayerHasMask0);
    mask1 = lerp(half4(0, 1, 0.5h, 0.5h), mask1, _LayerHasMask1);
    mask2 = lerp(half4(0, 1, 0.5h, 0.5h), mask2, _LayerHasMask2);
    mask3 = lerp(half4(0, 1, 0.5h, 0.5h), mask3, _LayerHasMask3);

    // ---- 2. height blend ----
    if (_EnableHeightBlend > 0.5h)
    {
        half4 heights = half4(mask0.b, mask1.b, mask2.b, mask3.b);
        splatControl = UCHeightBlend(splatControl, heights, _HeightTransition);
    }
    else
    {
        // Normalise anyway. A hand-painted control map rarely sums to exactly 1,
        // and letting it drift makes the whole surface subtly brighter or darker
        // than the layer textures actually are.
        half totalW = splatControl.r + splatControl.g + splatControl.b + splatControl.a;
        splatControl = totalW > 0.0001h ? splatControl / totalW : half4(1, 0, 0, 0);
    }

    half3 albedo = albedo0.rgb * _DiffuseRemapScale0.rgb * splatControl.r
                 + albedo1.rgb * _DiffuseRemapScale1.rgb * splatControl.g
                 + albedo2.rgb * _DiffuseRemapScale2.rgb * splatControl.b
                 + albedo3.rgb * _DiffuseRemapScale3.rgb * splatControl.a;

    // Smoothness source 0 means "from the albedo alpha", 1 means "from the mask
    // alpha". Unity exposes this per layer, so it is resolved per layer here.
    half smooth0 = lerp(albedo0.a, mask0.a, _SmoothnessSource0) * _Smoothness0;
    half smooth1 = lerp(albedo1.a, mask1.a, _SmoothnessSource1) * _Smoothness1;
    half smooth2 = lerp(albedo2.a, mask2.a, _SmoothnessSource2) * _Smoothness2;
    half smooth3 = lerp(albedo3.a, mask3.a, _SmoothnessSource3) * _Smoothness3;

    half smoothness = smooth0 * splatControl.r + smooth1 * splatControl.g
                    + smooth2 * splatControl.b + smooth3 * splatControl.a;

    half metallic = mask0.r * _Metallic0 * splatControl.r
                  + mask1.r * _Metallic1 * splatControl.g
                  + mask2.r * _Metallic2 * splatControl.b
                  + mask3.r * _Metallic3 * splatControl.a;

    half occlusion = mask0.g * splatControl.r + mask1.g * splatControl.g
                   + mask2.g * splatControl.b + mask3.g * splatControl.a;

    // Blended mask height, reused by cavity darkening and by mud pooling. Low
    // values mean recessed, which is where both contact shadow and mud collect.
    half cavityHeight = mask0.b * splatControl.r + mask1.b * splatControl.g
                      + mask2.b * splatControl.b + mask3.b * splatControl.a;

    // ---- tangent-space normal ----
    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal0, sampler_Splat0, input.uvSplat01.xy), _NormalScale0) * splatControl.r
                   + UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal1, sampler_Splat1, input.uvSplat01.zw), _NormalScale1) * splatControl.g
                   + UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal2, sampler_Splat2, input.uvSplat23.xy), _NormalScale2) * splatControl.b
                   + UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal3, sampler_Splat3, input.uvSplat23.zw), _NormalScale3) * splatControl.a;
    normalTS = normalize(normalTS + half3(0, 0, 1e-5h));

    half3 geoNormalWS = normalize(input.normalWS);

#ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
    // Instanced per-pixel normals come from the terrain's own normal map rather
    // than from the interpolated vertex normal, which is what keeps instanced
    // terrain from faceting at low tessellation.
    float3 nMap = SAMPLE_TEXTURE2D(_TerrainNormalmapTexture, sampler_TerrainNormalmapTexture,
                                   input.uvControlAndLM.xy).rgb * 2 - 1;
    geoNormalWS = normalize(TransformObjectToWorldNormal(nMap));
#endif

    // Slope, in degrees from flat, from the GEOMETRIC normal. Using the
    // normal-mapped one would make flat ground flicker between grass and cliff
    // wherever the detail bumps happen to cross the angle threshold.
    half slopeCos = saturate(geoNormalWS.y);
    half slopeDeg = degrees(acos(slopeCos));
    float worldY  = input.positionWS.y;
    float viewDist = length(GetCameraPositionWS() - input.positionWS);

    // ---- 3b. distance fade of the detail normal ----
    // Computed here because the slope and snow normals below must be faded by
    // the same factor, or they would keep shimmering after the layers settled.
    half distanceFade = 0.0h;
    if (_EnableDistanceFade > 0.5h)
    {
        distanceFade = saturate((viewDist - _DistanceFadeStart)
                              / max(_DistanceFadeEnd - _DistanceFadeStart, 0.001));
        // Fading the detail normal out at range removes the shimmer that a
        // high-frequency normal map produces once it is smaller than a pixel.
        normalTS = lerp(normalTS, half3(0, 0, 1), distanceFade);
    }

    half3 tangentWS = normalize(input.tangentWS.xyz);
    half3 bitangentWS = normalize(cross(geoNormalWS, tangentWS) * input.tangentWS.w);
    half3x3 tangentToWorld = half3x3(tangentWS, bitangentWS, geoNormalWS);
    half3 normalWS = normalize(TransformTangentToWorld(normalTS, tangentToWorld));

    // ---- 4. gradient tint ----
    // A 256x1 LUT baked by the Color Gradient node, indexed by altitude or by
    // slope. Applied to the painted ground only: cliff, snow and mud below are
    // authored materials in their own right and should not inherit it.
    if (_EnableGradient > 0.5h)
    {
        float gMin = _GradientMin;
        float gMax = _GradientMax;
        float gValue = _GradientMode < 0.5h ? worldY : (float)slopeDeg;
        float gT = saturate((gValue - gMin) / max(gMax - gMin, 0.0001));

        // Clamp sampler, and the LUT is one texel tall, so v is fixed at centre.
        half3 gCol = SAMPLE_TEXTURE2D(_GradientTex, sampler_linear_clamp, float2(gT, 0.5)).rgb;

        half3 graded = _GradientBlendMode < 0.5h ? (albedo * gCol * 2.0h)
                     : (_GradientBlendMode < 1.5h ? UCOverlay(albedo, gCol) : gCol);
        albedo = lerp(albedo, graded, _GradientStrength);
    }

    // ---- 5. mud ----
    // Mud is ground behaviour: it needs somewhere flat enough to stay put and a
    // recess for water to have carried it into. Both conditions, then a plain
    // top-down projection - mud lies on horizontal ground, so the Y plane of a
    // triplanar would carry all the weight anyway and the other two would be
    // two fetches spent on nothing.
    if (_EnableMud > 0.5h)
    {
        half mudFlat  = 1.0h - saturate(slopeDeg / max(_MudSlopeLimit, 0.001h));
        half mudPool  = 1.0h - UCBand(cavityHeight, _MudThreshold, 0.15h);
        half mudMask  = saturate(mudFlat * mudPool * _MudBlend);

        half3 mudTex = SAMPLE_TEXTURE2D(_MudTex, sampler_linear_repeat,
                                        input.positionWS.xz / max(_MudScale, 0.001)).rgb;

        albedo     = lerp(albedo, mudTex * _MudColor.rgb, mudMask);
        smoothness = lerp(smoothness, _MudSmoothness, mudMask);
        metallic   = lerp(metallic, 0.0h, mudMask);
    }

    // ---- 6. slope / cliff ----
#ifdef _UC_SLOPEBLEND_ON
    if (_EnableSlopeBlend > 0.5h)
    {
        half slopeStart = min(_SlopeAngleStart, _SlopeAngleEnd);
        half slopeEnd   = max(_SlopeAngleStart, _SlopeAngleEnd);
        half slopeMask  = saturate((slopeDeg - slopeStart) / max(slopeEnd - slopeStart, 0.001h));
        slopeMask = smoothstep(0.0h, 1.0h, slopeMask);

        half3 triW = UCTriplanarWeights(geoNormalWS);

        half4 slopeAlbedo = UCTriplanar(TEXTURE2D_ARGS(_SlopeTex, sampler_linear_repeat),
                                        input.positionWS, triW, _SlopeTriplanarScale);
        half4 slopeNrmTex = UCTriplanar(TEXTURE2D_ARGS(_SlopeNormal, sampler_linear_repeat),
                                        input.positionWS, triW, _SlopeTriplanarScale);

        half3 slopeNormalTS = UnpackNormalScale(slopeNrmTex,
                                  _SlopeNormalStrength * (1.0h - distanceFade));
        half3 slopeNormalWS = normalize(TransformTangentToWorld(slopeNormalTS, tangentToWorld));

        albedo     = lerp(albedo, slopeAlbedo.rgb * _SlopeColor.rgb, slopeMask);
        normalWS   = normalize(lerp(normalWS, slopeNormalWS, slopeMask));
        smoothness = lerp(smoothness, _SlopeSmoothness, slopeMask);
        metallic   = lerp(metallic, _SlopeMetallic, slopeMask);
        occlusion  = lerp(occlusion, 1.0h, slopeMask);
    }
#endif

    // ---- 7. snow ----
    // Two independent conditions, multiplied: high enough, and flat enough to
    // hold. The altitude term is a soft band rather than a hard line, because a
    // real snow line is a temperature gradient and reads as one - a hard cut
    // draws a contour ring around the mountain.
    half snowMask = 0.0h;
#ifdef _UC_SNOW_ON
    if (_EnableSnow > 0.5h)
    {
        // Altitude band in float, not half. A snow line at 2000 m is past the
        // point where half has 1-metre steps, and the band would quantise into
        // visible terraces on the mountainside.
        float snowFall = max(_SnowFalloff, 0.0001);
        half snowAlt = (half)saturate((worldY - _SnowAltitude + snowFall) / (2.0 * snowFall));
        half snowSlope = 1.0h - UCBand(slopeDeg, _SnowSlopeLimit, max(_SnowSlopeFalloff, 0.001h));
        snowMask = saturate(snowAlt * snowSlope * _SnowCoverage);

        half3 triW = UCTriplanarWeights(geoNormalWS);

        half4 snowAlbedo = UCTriplanar(TEXTURE2D_ARGS(_SnowTex, sampler_linear_repeat),
                                       input.positionWS, triW, _SnowTriplanarScale);
        half4 snowNrmTex = UCTriplanar(TEXTURE2D_ARGS(_SnowNormal, sampler_linear_repeat),
                                       input.positionWS, triW, _SnowTriplanarScale);

        half3 snowNormalTS = UnpackNormalScale(snowNrmTex,
                                 _SnowNormalStrength * (1.0h - distanceFade));
        half3 snowNormalWS = normalize(TransformTangentToWorld(snowNormalTS, tangentToWorld));

        albedo     = lerp(albedo, snowAlbedo.rgb * _SnowColor.rgb, snowMask);
        normalWS   = normalize(lerp(normalWS, snowNormalWS, snowMask));
        smoothness = lerp(smoothness, _SnowSmoothness, snowMask);
        metallic   = lerp(metallic, 0.0h, snowMask);
        // Snow fills crevices. Leaving the underlying AO in place is what makes
        // rendered snow read as a white sticker over rock instead of as depth.
        occlusion  = lerp(occlusion, 1.0h, snowMask);
    }
#endif

    // ---- 8. detail overlay ----
    // A second, much finer texture over the result, faded out with distance.
    // Its whole job is to survive being looked at from a metre away, and past
    // the fade range it is sub-pixel noise, so it is spent there rather than
    // aliased.
    if (_EnableDetail > 0.5h)
    {
        half detailFade = 1.0h - saturate((viewDist - _DetailFadeStart)
                                        / max(_DetailFadeEnd - _DetailFadeStart, 0.001));
        if (detailFade > 0.001h)
        {
            half3 detail = SAMPLE_TEXTURE2D(_DetailTex, sampler_linear_repeat,
                               input.positionWS.xz / max(_DetailScale, 0.001)).rgb;

            half3 blended = _DetailBlendMode < 0.5h ? UCOverlay(albedo, detail)
                          : (_DetailBlendMode < 1.5h ? albedo * detail * 2.0h
                                                     : albedo + (detail - 0.5h));
            albedo = lerp(albedo, blended, _DetailStrength * detailFade);
        }
    }

    // ---- 9. macro variation ----
    if (_EnableMacroVariation > 0.5h)
    {
        float2 macroUV = input.positionWS.xz / max(_MacroVariationScale, 0.001);
        half m = (half)UCMacroNoise(macroUV);

        // Centred on 0.5 so the tint pushes both ways rather than only darkening.
        half amount = (m - 0.5h) * 2.0h * _MacroVariationAmount;
        albedo = lerp(albedo, albedo * _MacroVariationColor.rgb, saturate(amount));
        albedo *= 1.0h + amount * 0.15h;
    }

    // ---- 10. wetness ----
    // Physically this is a thin water film. Light entering it is partly trapped
    // by total internal reflection, so less diffuse escapes - the surface reads
    // darker - while the flat air/water boundary reflects sharply, so specular
    // reads stronger and tighter. Darkening albedo and raising smoothness
    // together is the standard approximation of both effects at once.
    if (_EnableWetness > 0.5h)
    {
        half wet = saturate(_WetnessAmount);

        // Puddles need a basin: below the water level, and in a recess. Both
        // terms, so a low-lying ridge crest stays dry.
        half puddleDepth = saturate((_PuddleLevel - (float)worldY) / max(_PuddleFalloff, 0.001));
        half puddle = puddleDepth * (1.0h - UCBand(cavityHeight, _PuddleThreshold, 0.1h));
        puddle *= 1.0h - saturate(slopeDeg / 12.0h);   // water does not pool on a slope
        wet = saturate(max(wet, puddle));

        // Snow that is already lying does not turn into a puddle.
        wet *= 1.0h - snowMask;

        albedo    *= lerp(1.0h, 1.0h - _WetDarkening, wet);
        smoothness = lerp(smoothness, max(smoothness, _WetSmoothness), wet);
        smoothness = saturate(smoothness + _WetSpecBoost * wet * puddle);
        occlusion  = lerp(occlusion, 1.0h, wet * 0.5h);
    }

    // ---- 11. occlusion ----
    // Strength lerps toward 1 (unoccluded) rather than scaling toward 0, so
    // turning it down brightens the crevices instead of flattening the surface.
    occlusion = lerp(1.0h, occlusion, _OcclusionStrength);

    // Cavity uses the blended mask height as a proxy for concavity.
    occlusion *= lerp(1.0h, saturate(cavityHeight + 0.5h), _CavityStrength);

    // Baked AO from the Occlusion Map node - a raymarched, terrain-wide term
    // the per-layer mask cannot express, because it depends on the landform
    // rather than on the material.
    if (_EnableAOMap > 0.5h)
    {
        half bakedAO = SAMPLE_TEXTURE2D(_AOMap, sampler_linear_clamp, uvControl).r;
        occlusion *= lerp(1.0h, bakedAO, _AOMapStrength);
    }
    occlusion = saturate(occlusion);

    // ---- 12. global PBR remap ----
    // Remap before variation so the artist's min/max still bound the result.
    smoothness = lerp(_SmoothnessRemapMin, _SmoothnessRemapMax, saturate(smoothness));

    if (_RoughnessVariationAmount > 0.001h)
    {
        half rv = (half)UCMacroNoise(input.positionWS.xz
                                     / max(_RoughnessVariationScale, 0.001)) - 0.5h;
        smoothness = saturate(smoothness + rv * 2.0h * _RoughnessVariationAmount);
    }
    metallic = saturate(metallic * _MetallicScale);

    // ---- 13. emission ----
    half3 emission = half3(0, 0, 0);
    if (_EnableEmission > 0.5h)
    {
        half3 emissionTex = SAMPLE_TEXTURE2D(_EmissionMap, sampler_linear_repeat, uvControl).rgb;
        emission = emissionTex * _EmissionColor.rgb * _EmissionIntensity;
    }

    // ---- 14. colour grade ----
    albedo = lerp(UCLuma(albedo).xxx, albedo, _Saturation);
    // Contrast pivots on mid grey, so raising it does not also brighten.
    albedo = lerp(half3(0.5h, 0.5h, 0.5h), albedo, _Contrast);
    albedo *= _Brightness * _TintColor.rgb;
    albedo = max(albedo, 0.0h);

    // ---- lighting ----
    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
    inputData.bakedGI = SAMPLE_GI(input.uvControlAndLM.zw, SampleSHPixel(half3(0,0,0), normalWS), normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.uvControlAndLM.zw);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = albedo;
    surfaceData.metallic = saturate(metallic);
    surfaceData.specular = half3(0, 0, 0);
    surfaceData.smoothness = saturate(smoothness);
    surfaceData.normalTS = normalTS;
    surfaceData.occlusion = occlusion;
    surfaceData.emission = emission;
    surfaceData.alpha = 1.0h;
    surfaceData.clearCoatMask = 0.0h;
    surfaceData.clearCoatSmoothness = 0.0h;

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    // After fog, not before: fog is atmosphere between the camera and the ground,
    // and when the camera is under water there is no atmosphere on that path. The
    // medium is the last thing the light passes through, so it is the last thing
    // applied - the seabed seen from below must be water-coloured, not haze-
    // coloured with water added.
    color.rgb = UCApplyUnderwater(color.rgb, inputData.positionWS);

    color.a = 1.0h;
    return color;
}

#endif // UC_TERRAIN_LIT_PASSES_INCLUDED
