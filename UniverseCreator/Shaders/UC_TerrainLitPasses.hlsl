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
#include "UC_Lighting.hlsl"
#include "UC_SurfaceReflectance.hlsl"   // the ray-free wet-sky reflection twin, shared with props and the mobile path

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
    half3  vertexSH                 : TEXCOORD8;   // UC repair 29: vertex/mixed SH must reach SAMPLE_GI intact.

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

    // UC-T07-06: an absent tall material must not suppress materials that actually cover this pixel.
    weights = max(weights, 0.0h);
    half coverage = dot(weights, half4(1, 1, 1, 1));
    weights = coverage > 0.0001h ? weights / coverage : half4(1, 0, 0, 0);
    half maxHeight = -65504.0h;
    if (weights.x > 0) maxHeight = max(maxHeight, heights.x);
    if (weights.y > 0) maxHeight = max(maxHeight, heights.y);
    if (weights.z > 0) maxHeight = max(maxHeight, heights.z);
    if (weights.w > 0) maxHeight = max(maxHeight, heights.w);

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
    half3 w = pow(abs(normalWS), clamp(_UCTriplanarSharpness, 1.0h, 8.0h)); // UC-T07-11: authored sharpness reaches the real projection weights.
    return w / max(w.x + w.y + w.z, 0.0001h);
}

/// Triplanar sample through the shared linear-repeat sampler. Terrain UVs
/// stretch badly on a near-vertical face - that stretching is exactly what
/// makes a cliff read as smeared - so these materials project from world space.
half4 UCTriplanar(TEXTURE2D_PARAM(tex, samp), float3 positionWS, half3 w, half3 geometricNormal, float scale)
{
    float3 uvw = positionWS / max(scale, 0.001);
    half3 axisSign = half3(geometricNormal.x < 0 ? -1 : 1, geometricNormal.y < 0 ? -1 : 1, geometricNormal.z < 0 ? -1 : 1);

    // UC-T07-11: right-handed projection bases shared by colour and decoded normals, including negative world axes.
    half4 x = SAMPLE_TEXTURE2D(tex, samp, float2(-axisSign.x * uvw.z, uvw.y));
    half4 y = SAMPLE_TEXTURE2D(tex, samp, float2(uvw.x, -axisSign.y * uvw.z));
    half4 z = SAMPLE_TEXTURE2D(tex, samp, float2(axisSign.z * uvw.x, uvw.y));

    return x * w.x + y * w.y + z * w.z;
}

// UC-T07-11: shortest-arc rotation maps each projection's normal to the terrain's geometric normal.
// The chosen axis has a nonnegative dot product with geometry, so this never encounters the antiparallel singularity.
half3 UCProjectNormal(half3 detail, half3 axis, half3 geometricNormal)
{
    half3 v = cross(axis, geometricNormal);
    half3 first = cross(v, detail);
    return detail + first + cross(v, first) / max(1.0h + dot(axis, geometricNormal), 0.0001h);
}

half3 UCTriplanarNormalWS(TEXTURE2D_PARAM(tex, samp), float3 positionWS, half3 w, half3 geometricNormal, float scale, half strength)
{
    float3 uvw = positionWS / max(scale, 0.001);
    half3 s = half3(geometricNormal.x < 0 ? -1 : 1, geometricNormal.y < 0 ? -1 : 1, geometricNormal.z < 0 ? -1 : 1);
    half3 nx = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, float2(-s.x * uvw.z, uvw.y)), strength);
    half3 ny = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, float2(uvw.x, -s.y * uvw.z)), strength);
    half3 nz = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, float2(s.z * uvw.x, uvw.y)), strength);
    half3 x = UCProjectNormal(half3(s.x * nx.z, nx.y, -s.x * nx.x), half3(s.x, 0, 0), geometricNormal);
    half3 y = UCProjectNormal(half3(ny.x, s.y * ny.z, -s.y * ny.y), half3(0, s.y, 0), geometricNormal);
    half3 z = UCProjectNormal(half3(s.z * nz.x, nz.y, s.z * nz.z), half3(0, 0, s.z), geometricNormal);
    return normalize(x * w.x + y * w.y + z * w.z);
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
    output.vertexSH  = SampleSHVertex(normalWS);
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
    UCClipSurface(input.uvControlAndLM.xy); // UC-T07-06: same clipping in forward/shadow/depth.

#ifdef _ALPHATEST_ON
    ClipHoles(input.uvControlAndLM.xy);
#endif

    float2 uvControl = input.uvControlAndLM.xy;
    half4 splatControl = SAMPLE_TEXTURE2D(_Control, sampler_Control, UCControlUV(uvControl)); // UC-T07-11: continuous control mapping agrees with endpoint-lattice Apply and clipping.

    // ---- 1b. tiling break-up: noise-perturbed UV domain warp ----
    // A repeating albedo/normal tile reads as a visible grid once the camera is
    // far enough that one tile spans only a few pixels and many repeats sit side
    // by side. The film/landscape fix is not a costlier sampler but to WANDER the
    // sample coordinate with a low-frequency noise field, so the seams drift apart
    // instead of lining up. Two value-noise taps, applied once to the shared UV so
    // all twelve layer samples (albedo/mask/normal x4) stay mutually registered.
    // Gated to fade IN with distance: near ground keeps crisp detail (the detail
    // overlay, item 8, already breaks up close-range tiling), the macro warp only
    // grows where the repeat is actually visible.
    if (_EnableTileBreakup > 0.5h)
    {
        float  bkDist  = length(GetCameraPositionWS() - input.positionWS);
        float2 warpUV  = input.positionWS.xz / max(_TileBreakupScale, 0.001);
        // Centre both noise channels on 0 so the warp pushes both ways, not just +.
        float2 warp    = float2(UCValueNoise(warpUV), UCValueNoise(warpUV + 31.7)) - 0.5;
        // 0 up to the start distance, ramping to full over the same distance again,
        // so one property sets both onset and how quickly it reaches strength.
        float  bkFade  = saturate((bkDist - _TileBreakupDistance)
                                 / max(_TileBreakupDistance, 1.0));
        float2 bkOff   = warp * _TileBreakupStrength * bkFade;
        input.uvSplat01.xy += bkOff;
        input.uvSplat01.zw += bkOff;
        input.uvSplat23.xy += bkOff;
        input.uvSplat23.zw += bkOff;
    }

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
    // UC-T07-06: opacity alpha never becomes smoothness; absent masks preserve the authored scalar.
    half smooth0 = lerp(albedo0.a, lerp(1.0h, mask0.a, _LayerHasMask0), max(_SmoothnessSource0, _UC_LayerAlphaOpacity.x)) * _Smoothness0;
    half smooth1 = lerp(albedo1.a, lerp(1.0h, mask1.a, _LayerHasMask1), max(_SmoothnessSource1, _UC_LayerAlphaOpacity.y)) * _Smoothness1;
    half smooth2 = lerp(albedo2.a, lerp(1.0h, mask2.a, _LayerHasMask2), max(_SmoothnessSource2, _UC_LayerAlphaOpacity.z)) * _Smoothness2;
    half smooth3 = lerp(albedo3.a, lerp(1.0h, mask3.a, _LayerHasMask3), max(_SmoothnessSource3, _UC_LayerAlphaOpacity.w)) * _Smoothness3;

    half smoothness = smooth0 * splatControl.r + smooth1 * splatControl.g
                    + smooth2 * splatControl.b + smooth3 * splatControl.a;

    half metallic = lerp(1.0h, mask0.r, _LayerHasMask0) * _Metallic0 * splatControl.r
                  + lerp(1.0h, mask1.r, _LayerHasMask1) * _Metallic1 * splatControl.g
                  + lerp(1.0h, mask2.r, _LayerHasMask2) * _Metallic2 * splatControl.b
                  + lerp(1.0h, mask3.r, _LayerHasMask3) * _Metallic3 * splatControl.a;

    half occlusion = mask0.g * _UC_LayerOcclusion.x * splatControl.r + mask1.g * _UC_LayerOcclusion.y * splatControl.g
                   + mask2.g * _UC_LayerOcclusion.z * splatControl.b + mask3.g * _UC_LayerOcclusion.w * splatControl.a;

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
#ifdef _UC_GRADIENT_ON
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
#endif

    // ---- 4b. beach / shoreline sand ----
    // A real shoreline is a berm: a wedge of sand that starts at the waterline
    // and thins out over a berm height, laid only where the ground is gentle
    // enough for waves to have graded it. Steep rock meeting the sea stays rock.
    // Sand below the swash line is wet - darker and markedly more specular - so
    // its smoothness lifts toward the water, which is what reads as a wet beach.
#ifdef _UC_BEACH_ON
    if (_EnableBeach > 0.5h)
    {
        float beachTop   = _SeaLevel + max(_BeachHeight, 0.0001);
        half  beachAlt   = (half)saturate((beachTop - worldY) / max(_BeachFalloff, 0.0001));
        half  beachFlat  = 1.0h - saturate(slopeDeg / max(_BeachSlopeLimit, 0.001h));
        half  aboveWater = worldY >= _SeaLevel ? 1.0h : 0.0h;
        half  beachMask  = saturate(beachAlt * beachFlat * aboveWater);

        half3 beachTex = SAMPLE_TEXTURE2D(_BeachTex, sampler_linear_repeat,
                                          input.positionWS.xz / max(_BeachScale, 0.001)).rgb;
        half  wet = 1.0h - (half)saturate((worldY - _SeaLevel) / max(_BeachHeight, 0.0001));

        albedo     = lerp(albedo, beachTex * _BeachColor.rgb, beachMask);
        albedo     = lerp(albedo, albedo * 0.72h, beachMask * wet);   // wet sand darkens
        smoothness = lerp(smoothness, _BeachSmoothness * (0.5h + 0.5h * wet), beachMask);
        metallic   = lerp(metallic, 0.0h, beachMask);
    }
#endif

    // ---- 5. mud ----
    // Mud is ground behaviour: it needs somewhere flat enough to stay put and a
    // recess for water to have carried it into. Both conditions, then a plain
    // top-down projection - mud lies on horizontal ground, so the Y plane of a
    // triplanar would carry all the weight anyway and the other two would be
    // two fetches spent on nothing.
#ifdef _UC_MUD_ON
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
#endif

    // ---- 6. slope / cliff ----
#ifdef _UC_SLOPEBLEND_ON
    if (_EnableSlopeBlend > 0.5h)
    {
        half slopeStart = min(_SlopeAngleStart, _SlopeAngleEnd);
        half slopeEnd   = max(_SlopeAngleStart, _SlopeAngleEnd);
        half slopeMask  = saturate((slopeDeg - slopeStart) / max(slopeEnd - slopeStart, 0.001h));
        slopeMask = smoothstep(0.0h, 1.0h, slopeMask);

        // Altitude-aware rock (real-world): a steep slope LOW on a hill keeps
        // soil and clinging vegetation - it is not bare rock just because it is
        // steep. Bare rock/scree belongs to the cold, high, near-nival zone just
        // below the snow line. altBoost ramps 0->1 from _CliffRockAltitude over
        // _CliffRockAltitudeRange (defaulted to meet the snow line), so exposed
        // rock climbs toward the ice. Low down, _CliffLowAltGrass of the cliff is
        // held back as grass, broken into patches by the macro noise so it reads
        // as vegetation clinging to rock rather than a uniform half-transparent
        // wash; up high the grass fraction vanishes and the cliff goes all rock.
        float cliffRange = max(_CliffRockAltitudeRange, 0.0001);
        half  altBoost   = (half)saturate((worldY - _CliffRockAltitude) / cliffRange);
        half  rockNoise  = (half)UCMacroNoise(input.positionWS.xz * 0.08);
        half  grassKeep  = _CliffLowAltGrass * (1.0h - altBoost) * rockNoise;
        slopeMask = saturate(slopeMask - grassKeep);

        half3 triW = UCTriplanarWeights(geoNormalWS);

        half4 slopeAlbedo = 1.0h;
        half3 slopeNormalWS = geoNormalWS;
        if (_UCTextureQuality < 1.5h)
            slopeAlbedo = UCTriplanar(TEXTURE2D_ARGS(_SlopeTex, sampler_linear_repeat), input.positionWS, triW, geoNormalWS, _SlopeTriplanarScale);
        if (_UCTextureQuality < 0.5h && distanceFade < 0.999h)
            slopeNormalWS = UCTriplanarNormalWS(TEXTURE2D_ARGS(_SlopeNormal, sampler_linear_repeat), input.positionWS, triW, geoNormalWS,
                _SlopeTriplanarScale, _SlopeNormalStrength * (1.0h - distanceFade));

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
        if (_UCSurfaceSnow.y > 0.5h) snowMask = saturate(_UCSurfaceSnow.x * snowSlope * _SnowCoverage); // UC-T08-09: sampled snow replaces the climate-only supply when opted in.

        half3 triW = UCTriplanarWeights(geoNormalWS);

        half4 snowAlbedo = 1.0h;
        half3 snowNormalWS = geoNormalWS;
        if (_UCTextureQuality < 1.5h)
            snowAlbedo = UCTriplanar(TEXTURE2D_ARGS(_SnowTex, sampler_linear_repeat), input.positionWS, triW, geoNormalWS, _SnowTriplanarScale);
        if (_UCTextureQuality < 0.5h && distanceFade < 0.999h)
            snowNormalWS = UCTriplanarNormalWS(TEXTURE2D_ARGS(_SnowNormal, sampler_linear_repeat), input.positionWS, triW, geoNormalWS,
                _SnowTriplanarScale, _SnowNormalStrength * (1.0h - distanceFade));

        albedo     = lerp(albedo, snowAlbedo.rgb * _SnowColor.rgb, snowMask);
        normalWS   = normalize(lerp(normalWS, snowNormalWS, snowMask));
        smoothness = lerp(smoothness, _SnowSmoothness, snowMask);
        metallic   = lerp(metallic, 0.0h, snowMask);
        // Snow fills crevices, so by default it lifts the ground's ambient occlusion
        // away (occlusion -> 1) - that is what makes deep snow read as filled-in depth
        // instead of a white sticker over rock. _SnowOcclusion (0..1) dials how much of
        // that underlying AO the snow keeps instead: 0 = deep fresh snow, filled and
        // bright (the old fixed behaviour); 1 = thin, wind-scoured snow that still reads
        // the rough surface beneath it.
        half snowOcc = lerp(1.0h, occlusion, _SnowOcclusion);
        occlusion  = lerp(occlusion, snowOcc, snowMask);
    }
#endif

    // ---- 8. detail overlay ----
    // A second, much finer texture over the result, faded out with distance.
    // Its whole job is to survive being looked at from a metre away, and past
    // the fade range it is sub-pixel noise, so it is spent there rather than
    // aliased.
#ifdef _UC_DETAIL_ON
    if (_EnableDetail > 0.5h && _UCTextureQuality < 0.5h) // UC-T07-11: cheaper tiers skip microdetail fetches while retaining macro geography and PBR coverage.
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
#endif

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
    // Held at function scope so the wet-sky reflection below reads the same film
    // and the same puddle basin, never a second copy that could disagree.
    half surfaceWet = 0.0h;
    half surfacePuddle = 0.0h;
    if (_EnableWetness > 0.5h)
    {
        // The authored _WetnessAmount is a per-material floor; the live weather wetness
        // arrives on the _UCWetParams global from the one CPU uploader. The wetter of
        // the two wins, so a material can start damp and the sky can only add to it.
        half wet = saturate(max(_WetnessAmount, _UCWetParams.x));

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
        surfaceWet = wet;
        surfacePuddle = puddle;
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
#ifdef _UC_AOMAP_ON
    if (_EnableAOMap > 0.5h)
    {
        half bakedAO = SAMPLE_TEXTURE2D(_AOMap, sampler_linear_clamp, uvControl).r;
        occlusion *= lerp(1.0h, bakedAO, _AOMapStrength);
    }
#endif
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

    // Distance roughening: far ground trends rougher so sub-pixel specular stops
    // sparkling once a surface is smaller than a pixel's footprint (the standard
    // specular-aliasing suppression, done per-pixel). Reuses the SAME distanceFade
    // the detail normal already fades by, so near ground is untouched and the
    // onset lines up exactly with the normal fade - it is a no-op unless Distance
    // Fade is enabled AND this amount is raised.
    if (_DistanceRoughen > 0.001h)
        smoothness = lerp(smoothness, smoothness * (1.0h - _DistanceRoughen), distanceFade);

    metallic = saturate(metallic * _MetallicScale);

#if defined(UC_REFLECTION_MASK)
    // UC midday repair: RG stores final material roughness and local wet/puddle coverage, never normal-buffer alpha.
    return half4(saturate(1.0h - smoothness), saturate(max(surfaceWet, surfacePuddle)), 0.0h, 1.0h);
#endif

    // ---- 13. emission ----
    half3 emission = _UC_LayerEmission0.rgb * splatControl.r + _UC_LayerEmission1.rgb * splatControl.g
                   + _UC_LayerEmission2.rgb * splatControl.b + _UC_LayerEmission3.rgb * splatControl.a; // UC-T07-06: coverage-weighted reusable layer emission.
#ifdef _UC_EMISSION_ON
    if (_EnableEmission > 0.5h)
    {
        half3 emissionTex = SAMPLE_TEXTURE2D(_EmissionMap, sampler_linear_repeat, uvControl).rgb;
        emission += emissionTex * _EmissionColor.rgb * _EmissionIntensity;
    }
#endif

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
    inputData.bakedGI = SAMPLE_GI(input.uvControlAndLM.zw, input.vertexSH, normalWS);
    // The ground is the reference every scattered thing is matched against: read the shared
    // grid here so terrain, vegetation and water all draw their ambient from one source and
    // agree at any shared point. No grid bound yet, and the engine ambient stands unchanged.
    inputData.bakedGI = UCGridAmbientOr(inputData.bakedGI, input.positionWS, normalWS);
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

    // S-81: the ground gets the same ray-free wet-sky/grid reflection every other surface
    // does, reading the film and puddle basin resolved in section 10 - so a rain-slicked
    // road and the wet rock beside it reflect one sky, not two. Fresnel x wetness weights it
    // to nothing when dry (no double-count of URP's probe specular) and the tier gate leaves
    // it inert until S-82 turns it on. Before the media below: a reflection is surface
    // radiance, not something the fog or the water tints on the way to the eye.
    color.rgb += UCEnvReflectionContribution(input.positionWS, normalWS,
        inputData.viewDirectionWS, 1.0h - saturate(surfaceData.smoothness),
        surfaceWet, surfacePuddle, surfaceData.occlusion, normalWS);

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
