// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UC_WaterPasses.hlsl
//  Purpose: Vertex and fragment for the water surface, plus the depth-only pair.
//
//  THE VERTEX STREAM THIS SHADER EXPECTS
//  -------------------------------------
//  The water meshes UCWaterBody generates carry more than position:
//
//    COLOR.a   water weight. 1 in open water, falling to 0 at the shoreline. It
//              scales the wave displacement so the mesh edge stays welded to the
//              bank, and it multiplies the final alpha so the polygon boundary
//              is never visible as a line.
//    COLOR.rg  flow direction in world XZ, packed 0..1 around 0.5. Rivers write
//              their downstream direction here; an ocean plane leaves it at 0.5,
//              which unpacks to zero and falls back to the material's uniform
//              flow.
//    COLOR.b   flow speed, 0..1, scaled by _FlowStrength. Rapids run faster than
//              a slow reach, and that difference is the main thing that reads as
//              "this is a river" rather than "this is a blue ribbon".
//
//  A Unity primitive plane has no vertex colours, which decode to (0,0,0,0) -
//  weight zero, invisible. UCWaterBody always writes colours; the shader treats a
//  fully black colour as "no data" and substitutes weight 1 so that a hand-made
//  mesh dropped onto the material still renders.
// -----------------------------------------------------------------------------

#ifndef UC_WATER_PASSES_INCLUDED
#define UC_WATER_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
#include "UC_Underwater.hlsl"

struct UCWaterAttributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float4 color        : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct UCWaterVaryings
{
    float4 positionCS   : SV_POSITION;
    float3 positionWS   : TEXCOORD0;
    float3 normalWS     : TEXCOORD1;
    float4 tangentWS    : TEXCOORD2;
    float4 screenPos    : TEXCOORD3;
    float4 flowAndMask  : TEXCOORD4;   // xy dir, z speed, w weight
    half   fogCoord     : TEXCOORD5;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// Unpacks the vertex stream described in the file header into xy=direction,
// z=speed, w=weight.
float4 UCDecodeWaterVertex(float4 vertexColor)
{
    float4 r;

    // An all-zero colour is a mesh that carries no water data - a Unity plane, a
    // user's own mesh. Treat it as fully wet with the material's uniform flow.
    float hasData = step(0.0001, dot(vertexColor, 1.0));

    float2 dir = vertexColor.rg * 2.0 - 1.0;
    float dirLen = length(dir);

    r.xy = dirLen > 0.001 ? dir / dirLen : normalize(_FlowSpeed.xy + float2(1e-5, 0));
    r.z  = dirLen > 0.001 ? vertexColor.b * _FlowStrength : length(_FlowSpeed.xy);
    r.w  = lerp(1.0, vertexColor.a, hasData);

    // Uniform flow wins when vertex flow is switched off, so an ocean built by
    // the same builder still drifts the way the material says it should.
    if (_UseVertexFlow < 0.5)
    {
        float speed = length(_FlowSpeed.xy);
        r.xy = speed > 1e-5 ? _FlowSpeed.xy / speed : float2(1, 0);
        r.z = speed;
    }

    return r;
}

UCWaterVaryings UCWaterVert(UCWaterAttributes input)
{
    UCWaterVaryings output = (UCWaterVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vp = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs vn = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    float4 flow = UCDecodeWaterVertex(input.color);

    float3 positionWS = vp.positionWS;
    positionWS.y += UCWaveOffset(positionWS.xz, _Time.y, flow.w);

    output.positionWS = positionWS;
    output.positionCS = TransformWorldToHClip(positionWS);
    output.normalWS = vn.normalWS;
    output.tangentWS = float4(vn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    output.screenPos = ComputeScreenPos(output.positionCS);
    output.flowAndMask = flow;
    output.fogCoord = ComputeFogFactor(output.positionCS.z);

    return output;
}

// FRONT_FACE_TYPE / IS_FRONT_VFACE rather than a raw VFACE or SV_IsFrontFace:
// the semantic and its sign differ between D3D, GL and Metal, and the macro is
// the platform-portable spelling URP itself uses.
half4 UCWaterFrag(UCWaterVaryings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float2 screenUV = input.screenPos.xy / max(1e-5, input.screenPos.w);
    float3 flow = input.flowAndMask.xyz;
    float weight = saturate(input.flowAndMask.w);

    // ---- 1. how much water the ray passes through ----
    //
    // Both depths are linearised to eye space before subtracting. Subtracting raw
    // depth buffer values instead gives a difference that is correct at the near
    // plane and wrong everywhere else, because the buffer is not linear.
    float rawSceneDepth = SampleSceneDepth(screenUV);
    float sceneEye = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
    float surfaceEye = LinearEyeDepth(input.positionCS.z, _ZBufferParams);

    float waterMetres = max(0.0, sceneEye - surfaceEye);

    // A scene depth at the far plane means nothing opaque was drawn there - sky,
    // or the depth texture is switched off on the URP asset. Treat it as deep
    // rather than as zero, or every such pixel reads as a shoreline and the whole
    // ocean turns to foam.
    #if UNITY_REVERSED_Z
        if (rawSceneDepth <= 1e-7) waterMetres = _DepthFadeMetres * 4.0;
    #else
        if (rawSceneDepth >= 1.0 - 1e-7) waterMetres = _DepthFadeMetres * 4.0;
    #endif

    float depthT = saturate(waterMetres / max(0.05, _DepthFadeMetres));

    // ---- 2. waves ----
    half3 normalTS = UCSampleWaves(input.positionWS.xz, _Time.y, flow);

    // Interactive ripples bend the same normal. Added in tangent space before
    // normalisation rather than as a second lighting term, so a ripple crossing a
    // wave crest bends the light once, the way one surface with two slopes on it
    // does - two separate speculars is the tell that gives away a decal.
    float4 ripple = UCSampleRipple(input.positionWS.xz);
    normalTS.xy += ripple.xy * _RippleStrength;

    // Wind chop bends the same normal again. This is the term that does most of
    // the visible work: the height offset is centimetres and barely reads at
    // distance, but breaking up the specular is what turns a mirror into water.
    float windStrength = UCWindSurfaceStrength(input.positionWS);
    normalTS.xy += UCWindChopSlope(input.positionWS.xz, _Time.y, windStrength);

    normalTS = SafeNormalize(normalTS);

    // Seen from below, the geometric normal points away from the eye. Left as-is,
    // every lighting term reads as backfacing: the underside goes black, the
    // fresnel inverts, and the specular lands on the wrong side of the wave. One
    // sign flip puts the whole frame back on the side the camera is on.
    float faceSign = IS_FRONT_VFACE(facing, 1.0, -1.0);
    float3 geomNormalWS = input.normalWS * faceSign;

    float sgn = input.tangentWS.w;
    float3 bitangent = sgn * cross(geomNormalWS, input.tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent, geomNormalWS);
    half3 normalWS = SafeNormalize(mul(normalTS, tangentToWorld));

    half3 viewDirWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);

    bool seenFromBelow = faceSign < 0.0;

    // ---- 3. refraction ----
    //
    // The offset is scaled by depth: a pixel with ten centimetres of water under
    // it must not be displaced, because the sample would land on ground the water
    // does not cover and drag dry pixels into the surface. This is the standard
    // fix for the coloured halo along a shoreline.
    float2 refractUV = screenUV + normalWS.xz * _Refraction * depthT;

    // And rejected outright if it sampled something in front of the water, which
    // is what happens when a rock or a boat stands proud of the surface.
    float refractedEye = LinearEyeDepth(SampleSceneDepth(refractUV), _ZBufferParams);
    if (refractedEye < surfaceEye) refractUV = screenUV;

    half3 behind = SampleSceneColor(refractUV);

    // ---- 4. absorption ----
    //
    // Beer-Lambert through the shallow-to-deep ramp. exp rather than a lerp:
    // absorption is exponential in path length, and a linear ramp makes deep water
    // reach its final colour and then stop changing, which is what makes flat-
    // looking water read as painted on.
    half3 waterTint = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthT);
    half absorb = 1.0h - exp(-waterMetres / max(0.05, _DepthFadeMetres));

    half3 throughWater = lerp(behind, waterTint, saturate(absorb * _Transparency));

    // ---- 5. caustics ----
    //
    // Applied to what is seen through the water, not on top of the surface:
    // caustics are light focused onto the bed, so they belong behind the surface
    // tint. Two counter-scrolling samples multiplied, which is the cheap stand-in
    // for the moving cusp pattern real caustics form.
    if (_CausticsEnabled > 0.5)
    {
        float ctile = max(0.1, _CausticsTileMetres);
        float2 cuv = input.positionWS.xz / ctile;
        float ct = _Time.y * _CausticsSpeed;

        half c0 = SAMPLE_TEXTURE2D(_CausticsTexture, sampler_CausticsTexture,
                                   cuv + float2(ct, ct * 0.7)).r;
        half c1 = SAMPLE_TEXTURE2D(_CausticsTexture, sampler_CausticsTexture,
                                   cuv * 1.31 - float2(ct * 0.8, ct)).r;

        half caustic = c0 * c1 * _CausticsIntensity;
        caustic *= 1.0h - saturate(waterMetres / max(0.1, _CausticsDepthFade));

        throughWater += _CausticsColor.rgb * caustic;
    }

    // ---- 6. fresnel ----
    //
    // Water is nearly a mirror at grazing angles and nearly clear looking straight
    // down. Schlick with F0 = 0.02, the measured value for an air-water interface.
    half nDotV = saturate(dot(normalWS, viewDirWS));
    half fresnel = 0.02h + 0.98h * pow(1.0h - nDotV, _FresnelPower);

    // ---- 7. foam ----
    half foam = 0.0h;
    if (_FoamEnabled > 0.5)
    {
        // Shoreline term: thin water means the surface is close to the ground,
        // which is where a wave breaks.
        half edge = 1.0h - saturate(waterMetres / max(0.01, _FoamEdgeWidth));
        edge = edge * edge;

        // Flow term: fast water is white water. Without this a river reads as a
        // still ribbon with foam only at the bank, which is backwards - the
        // rapids are the part that should be white.
        half rapid = saturate((flow.z - 0.35h) * 1.6h) * _FlowFoam;

        float ftile = max(0.1, _FoamTileMetres);
        float2 fuv = input.positionWS.xz / ftile;
        float ft = _Time.y * _FoamScrollSpeed;

        half f0 = SAMPLE_TEXTURE2D(_FoamTexture, sampler_FoamTexture,
                                   fuv + flow.xy * ft).r;
        half f1 = SAMPLE_TEXTURE2D(_FoamTexture, sampler_FoamTexture,
                                   fuv * 0.73 - flow.xy * ft * 0.6).r;

        // 0.6/0.4, summing to one. It read 0.6/0.6, which let two bright texels
        // reach 1.2 and clipped the foam to flat white a fifth of the way before
        // the intensity the user set - so turning _FoamIntensity up stopped doing
        // anything long before it looked like it should.
        foam = saturate(max(edge, rapid) * (f0 * 0.6h + f1 * 0.4h) * _FoamIntensity);
    }

    // Dynamic foam from the simulation - splash rings, boat wakes, the white patch
    // under a waterfall. Combined with max rather than added: foam saturates, and
    // adding it makes a splash inside surf brighter than white.
    //
    // Outside the _FoamEnabled branch on purpose. A lake with no ambient foam still
    // goes white where something hits it, and forcing the user to switch on shore
    // foam to get splash foam couples two unrelated controls.
    if (_RippleFoam > 0.0001)
        foam = max(foam, saturate(ripple.w * _RippleFoam));

    // Wind foam: white streaks on the water where the wind is strong enough to
    // tear the surface. Foam is torn off the crests of the chop, so the streak
    // pattern is built on the SAME axis and the same travel direction as the chop
    // in UCWindChopHeight - along the wind, moving downwind. It used to use a
    // hardcoded world direction, so turning the wind rotated the chop and left the
    // foam streaks pointing the way they always had.
    //
    // Combined with max like the splash foam, for the same reason: foam saturates
    // and adding two white sources together just makes a brighter white that
    // clips. Outside the _FoamEnabled branch for the same reason too - a lake with
    // no ambient foam still gets whitecaps in a gale.
    if (_WindFoam > 0.0001)
    {
        float crest = saturate(sin(dot(input.positionWS.xz, _UCWindDirection.xz) * 9.0
                                   - _Time.y * 5.0));
        foam = max(foam, windStrength * windStrength * crest * _WindFoam * 0.8);
    }

    // ---- 8. shade ----
    //
    // Albedo is what already passed through the water. Feeding it as albedo rather
    // than adding lighting on top is what lets a single opaque PBR call handle a
    // transparent surface: the diffuse term is the transmitted light, the specular
    // term is the reflection off the surface.
    half3 albedo = lerp(throughWater, _FoamColor.rgb, foam);

    // ---- 8b. the underside ----
    //
    // Looking UP at water from inside it is not the same picture as looking down at
    // it, and shading it as though it were is why so many games' underwater looks
    // wrong in a way that is hard to name. Two things change:
    //
    //  * Snell's window. Light from the whole sky refracts into a cone of about 49
    //    degrees around straight up, so overhead there is a bright circle showing
    //    the world above, and outside it the surface is a total internal mirror
    //    reflecting the dark water back at you.
    //  * There is no shoreline reading. The depth term above measures water between
    //    the surface and the ground behind it, which from below is measuring the
    //    wrong side and produces foam over the middle of a lake.
    if (seenFromBelow)
    {
        // 1 straight up, falling off towards the horizon. The cone edge is where
        // total internal reflection takes over; smoothstepped over a few degrees
        // because a hard ring is the one thing real water does not have.
        half upness = saturate(dot(viewDirWS, half3(0, 1, 0)));
        half window = smoothstep(0.55h, 0.78h, upness);

        // Outside the window: the mirror. Deep colour rather than a probe sample,
        // because what it is reflecting is the water the camera is already in.
        half3 mirrored = _DeepColor.rgb * 0.75h;

        // Inside it: the refracted world above, which is what already got sampled
        // as `behind` - through the surface is up, from down here.
        albedo = lerp(mirrored, behind, window);

        // Foam still belongs, but only what is actually on the surface film - the
        // shoreline and depth-driven terms measured the wrong side.
        foam = saturate(foam * 0.35h);
        albedo = lerp(albedo, _FoamColor.rgb, foam);
    }

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = viewDirWS;

#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogCoord);
    inputData.bakedGI = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1, 1, 1, 1);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = albedo;
    surfaceData.metallic = 0.0h;
    surfaceData.specular = _SpecularTint.rgb;
    // Foam is froth, not a mirror. Leaving it at water smoothness makes breaking
    // surf glossier than the flat water beside it.
    surfaceData.smoothness = lerp(saturate(_Smoothness), 0.25h, foam);
    surfaceData.normalTS = normalTS;
    surfaceData.occlusion = 1.0h;
    surfaceData.emission = half3(0, 0, 0);
    surfaceData.alpha = 1.0h;
    surfaceData.clearCoatMask = 0.0h;
    surfaceData.clearCoatSmoothness = 0.0h;

    half4 color = UniversalFragmentPBR(inputData, surfaceData);

    // ---- 9. opacity ----
    //
    // The colour above already contains the refracted background, so alpha is not
    // "how much water is there" - it is how much of this shader's own result
    // should replace the framebuffer. Fresnel raises it at grazing angles so the
    // far side of a lake becomes a reflection rather than fading out, and foam is
    // always opaque.
    half alpha = saturate(lerp(_ShallowColor.a, _DeepColor.a, depthT));
    alpha = saturate(max(alpha, fresnel));
    alpha = max(alpha, foam);

    // Shoreline soften, twice over: by measured depth, which handles a plane
    // cutting a sloped bank, and by the mesh's own water weight, which handles a
    // river ribbon whose edge is a polygon boundary rather than a depth event.
    if (_EdgeSoftenMetres > 0.001)
        alpha *= saturate(waterMetres / _EdgeSoftenMetres);

    alpha *= weight;

    // From below, the two softening terms above are meaningless - both measure
    // water depth behind the surface, which from this side is behind the CAMERA.
    // Left in, they fade the underside out over open water and the sky shows
    // through the middle of the lake. The film itself is nearly opaque looking up
    // through it, so it is driven by the window instead.
    if (seenFromBelow)
    {
        half upness = saturate(dot(viewDirWS, half3(0, 1, 0)));
        alpha = saturate(lerp(0.95h, 0.55h, smoothstep(0.55h, 0.78h, upness)));
        alpha = max(alpha, foam);
        alpha *= weight;
    }

    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    // The surface is at the far end of a ray through the medium like anything else,
    // so it is absorbed like anything else. This is what keeps the far side of a
    // lake ceiling from staying crisp while the seabed under it has faded out.
    color.rgb = UCApplyUnderwater(color.rgb, input.positionWS);

    color.a = saturate(alpha);
    return color;
}

// -----------------------------------------------------------------------------
//  Depth only
// -----------------------------------------------------------------------------

struct UCWaterDepthAttributes
{
    float4 positionOS : POSITION;
    float4 color      : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct UCWaterDepthVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

UCWaterDepthVaryings UCWaterDepthVert(UCWaterDepthAttributes input)
{
    UCWaterDepthVaryings output = (UCWaterDepthVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

    // Displaced by exactly what the forward pass displaces by. A depth pass that
    // used the flat plane would z-fight the shaded surface by up to _WaveHeight.
    float weight = dot(input.color, 1.0) > 0.0001 ? input.color.a : 1.0;
    positionWS.y += UCWaveOffset(positionWS.xz, _Time.y, weight);

    output.positionCS = TransformWorldToHClip(positionWS);
    return output;
}

half4 UCWaterDepthFrag(UCWaterDepthVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    return 0;
}

#endif // UC_WATER_PASSES_INCLUDED
