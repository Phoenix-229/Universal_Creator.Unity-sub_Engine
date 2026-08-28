#ifndef UC_TERRAIN_LIT_INPUT_INCLUDED
#define UC_TERRAIN_LIT_INPUT_INCLUDED

// -----------------------------------------------------------------------------
//  Universe Creator - UC_TerrainLitInput.hlsl
//  Uniform declarations for the terrain uber-shader.
//
//  The _Terrain CBUFFER below mirrors URP's own layout deliberately. Unity's
//  TerrainLayer system writes these uniforms by name from the layer's inspector
//  fields - tiling, normal scale, mask remap, smoothness source. Reordering is
//  safe; renaming is not, because the engine looks them up by string.
//
//  The UC block at the end is ours. It sits in its own CBUFFER so that adding a
//  control here can never shift the offsets the terrain engine relies on.
//
//  SAMPLER BUDGET - why the shared samplers below exist
//  ----------------------------------------------------
//  A D3D11 shader stage gets 16 sampler slots. Not 16 textures - 16 samplers.
//  Declared naively this shader wants 24: control, four splats, four normals,
//  four masks, cliff colour and normal, snow colour and normal, mud, detail,
//  gradient LUT, ambient occlusion, emission, holes and the terrain normal map.
//  That does not compile, and the failure arrives as an opaque register error
//  rather than as anything naming the cause.
//
//  Two things bring it back under the limit:
//
//  1. Layers share their splat's sampler. _Normal0 and _Mask0 are sampled with
//     sampler_Splat0, because a layer's three maps are the same texture at the
//     same tiling and there is no case where they should filter or wrap
//     differently. URP's own TerrainLit does exactly this.
//  2. Everything the texture graph adds samples through one of two inline
//     sampler states. `sampler_linear_repeat` and `sampler_linear_clamp` are
//     names Unity recognises and resolves to a single shared sampler each,
//     rather than one per texture.
//
//  Result: 5 dedicated (control + four splats) + 2 shared + 2 conditional
//  (holes, terrain normal map) = 9 worst case.
// -----------------------------------------------------------------------------

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    half4  _BaseColor;
    half   _Cutoff;
    float4 _Splat0_TexelSize, _Splat1_TexelSize, _Splat2_TexelSize, _Splat3_TexelSize;
CBUFFER_END

// Terrain is never transparent, so the lighting code's surface branch is fixed.
#define _Surface 0.0

CBUFFER_START(_Terrain)
    half _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
    half _Metallic0, _Metallic1, _Metallic2, _Metallic3;
    half _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
    half4 _DiffuseRemapScale0, _DiffuseRemapScale1, _DiffuseRemapScale2, _DiffuseRemapScale3;
    half4 _MaskMapRemapOffset0, _MaskMapRemapOffset1, _MaskMapRemapOffset2, _MaskMapRemapOffset3;
    half4 _MaskMapRemapScale0, _MaskMapRemapScale1, _MaskMapRemapScale2, _MaskMapRemapScale3;

    float4 _Control_ST;
    float4 _Control_TexelSize;
    half _DiffuseHasAlpha0, _DiffuseHasAlpha1, _DiffuseHasAlpha2, _DiffuseHasAlpha3;
    half _LayerHasMask0, _LayerHasMask1, _LayerHasMask2, _LayerHasMask3;
    half _SmoothnessSource0, _SmoothnessSource1, _SmoothnessSource2, _SmoothnessSource3;
    half4 _Splat0_ST, _Splat1_ST, _Splat2_ST, _Splat3_ST;
    half _HeightTransition;
    half _NumLayersCount;
    float _TerrainBasemapDistance;

#ifdef UNITY_INSTANCING_ENABLED
    float4 _TerrainHeightmapRecipSize;   // (1/w, 1/h, 1/(w-1), 1/(h-1))
#endif
    float4 _TerrainHeightmapScale;       // (scale.x, scale.y/kMaxHeight, scale.z, 0)
CBUFFER_END

// -----------------------------------------------------------------------------
//  UC control surface. Written by the texture graph's Shader Output node.
//
//  Every _Enable* here is a float the fragment shader branches on, NOT a shader
//  keyword. That is a deliberate trade and worth stating, because the reflex is
//  to reach for a keyword every time.
//
//  Keywords multiply. Ten independent toggles is 1024 variants on top of URP's
//  own lighting matrix, which turns a player build into an overnight job and
//  a shader cache into gigabytes. A uniform branch costs one scalar compare per
//  fragment and - because the condition comes from a constant buffer, so every
//  lane in every wave takes the same side - the untaken half is skipped
//  wholesale. There is no divergence to pay for.
//
//  What a uniform branch does cost is registers: the compiler allocates for the
//  worst path whether or not it runs. So the rule applied here is that features
//  costing only arithmetic branch on a uniform, and features costing several
//  extra texture fetches get a real keyword. That is why exactly two keywords
//  survive - _UC_SLOPEBLEND_ON and _UC_SNOW_ON, six fetches each - and
//  everything else is a float.
// -----------------------------------------------------------------------------
CBUFFER_START(UCTerrain)
    // -- blending --------------------------------------------------------
    half   _EnableHeightBlend;
    half   _OcclusionStrength;
    half   _CavityStrength;

    // -- macro variation -------------------------------------------------
    half   _EnableMacroVariation;
    float  _MacroVariationScale;
    half   _MacroVariationAmount;
    half4  _MacroVariationColor;

    // -- slope / cliff (keyword _UC_SLOPEBLEND_ON) -----------------------
    half   _EnableSlopeBlend;
    half   _SlopeAngleStart;
    half   _SlopeAngleEnd;
    half   _SlopeSmoothness;
    half   _SlopeMetallic;
    half   _SlopeNormalStrength;
    half4  _SlopeColor;
    float  _SlopeTriplanarScale;

    // -- snow (keyword _UC_SNOW_ON) --------------------------------------
    half   _EnableSnow;
    half4  _SnowColor;
    float  _SnowAltitude;
    float  _SnowFalloff;
    half   _SnowSlopeLimit;
    half   _SnowSlopeFalloff;
    half   _SnowCoverage;
    half   _SnowSmoothness;
    half   _SnowNormalStrength;
    float  _SnowTriplanarScale;

    // -- wetness ---------------------------------------------------------
    half   _EnableWetness;
    half   _WetnessAmount;
    half   _WetDarkening;
    half   _WetSmoothness;
    half   _WetSpecBoost;
    half   _PuddleThreshold;
    float  _PuddleLevel;
    float  _PuddleFalloff;

    // -- mud / dirt ------------------------------------------------------
    half   _EnableMud;
    half4  _MudColor;
    float  _MudScale;
    half   _MudThreshold;
    half   _MudBlend;
    half   _MudSmoothness;
    half   _MudSlopeLimit;

    // -- detail overlay --------------------------------------------------
    half   _EnableDetail;
    float  _DetailScale;
    half   _DetailStrength;
    half   _DetailBlendMode;      // 0 overlay, 1 multiply, 2 add
    float  _DetailFadeStart;
    float  _DetailFadeEnd;

    // -- gradient LUT ----------------------------------------------------
    half   _EnableGradient;
    half   _GradientMode;         // 0 altitude, 1 slope
    half   _GradientBlendMode;    // 0 multiply, 1 overlay, 2 replace
    float  _GradientMin;
    float  _GradientMax;
    half   _GradientStrength;

    // -- baked ambient occlusion ----------------------------------------
    half   _EnableAOMap;
    half   _AOMapStrength;

    // -- emission --------------------------------------------------------
    half   _EnableEmission;
    half4  _EmissionColor;
    half   _EmissionIntensity;

    // -- global PBR remap ------------------------------------------------
    half   _SmoothnessRemapMin;
    half   _SmoothnessRemapMax;
    half   _MetallicScale;
    half   _RoughnessVariationScale;
    half   _RoughnessVariationAmount;

    // -- distance fade ---------------------------------------------------
    half   _EnableDistanceFade;
    float  _DistanceFadeStart;
    float  _DistanceFadeEnd;

    // -- colour grade ----------------------------------------------------
    half   _Saturation;
    half   _Brightness;
    half   _Contrast;
    half4  _TintColor;
CBUFFER_END

// The world's live wetness, published by the single WetnessCoupling uploader on the
// CPU (x = wetness). A global, not a material property, so the whole world wets from
// one number; the per-material _WetnessAmount above still acts as an authored floor.
float4 _UCWetParams;

// -----------------------------------------------------------------------------
//  Textures
// -----------------------------------------------------------------------------

// Inline sampler states. Unity resolves these names to shared sampler objects,
// so every graph-authored texture below costs zero additional sampler slots.
SAMPLER(sampler_linear_repeat);
SAMPLER(sampler_linear_clamp);

TEXTURE2D(_Control);    SAMPLER(sampler_Control);

// Splats own the only per-layer samplers. Normals and masks below reuse them.
TEXTURE2D(_Splat0);     SAMPLER(sampler_Splat0);
TEXTURE2D(_Splat1);     SAMPLER(sampler_Splat1);
TEXTURE2D(_Splat2);     SAMPLER(sampler_Splat2);
TEXTURE2D(_Splat3);     SAMPLER(sampler_Splat3);

TEXTURE2D(_Normal0);
TEXTURE2D(_Normal1);
TEXTURE2D(_Normal2);
TEXTURE2D(_Normal3);

TEXTURE2D(_Mask0);
TEXTURE2D(_Mask1);
TEXTURE2D(_Mask2);
TEXTURE2D(_Mask3);

// Graph-authored maps. All sampled through the shared states above.
TEXTURE2D(_SlopeTex);
TEXTURE2D(_SlopeNormal);
TEXTURE2D(_SnowTex);
TEXTURE2D(_SnowNormal);
TEXTURE2D(_MudTex);
TEXTURE2D(_DetailTex);
TEXTURE2D(_GradientTex);
TEXTURE2D(_AOMap);

// _EmissionMap is NOT declared here. URP's SurfaceInput.hlsl, included above,
// already declares it along with _BaseMap and _BumpMap - redeclaring it is a
// hard redefinition error, and the compiler reports it against this line rather
// than against the include that got there first. It is still sampled through
// sampler_linear_repeat below; a texture's declaration and the sampler state
// used to read it are independent, so inheriting theirs costs us nothing.

#ifdef UNITY_INSTANCING_ENABLED
    TEXTURE2D(_TerrainHeightmapTexture);
    TEXTURE2D(_TerrainNormalmapTexture);
    SAMPLER(sampler_TerrainNormalmapTexture);
#endif

UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)  // (xBase, yBase, skipScale, ~)
UNITY_INSTANCING_BUFFER_END(Terrain)

#if defined(UNITY_INSTANCING_ENABLED) && defined(_TERRAIN_INSTANCED_PERPIXEL_NORMAL)
    #define ENABLE_TERRAIN_PERPIXEL_NORMAL
#endif

#ifdef _ALPHATEST_ON
    TEXTURE2D(_TerrainHolesTexture);
    SAMPLER(sampler_TerrainHolesTexture);

    // A hole is authored as 0 in the holes texture. Clipping on a small epsilon
    // rather than on == 0 keeps bilinear filtering from leaving a one-pixel
    // fringe of surviving geometry around every hole edge.
    void ClipHoles(float2 uv)
    {
        float hole = SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r;
        clip(hole == 0.0f ? -1 : 1);
    }
#endif

// -----------------------------------------------------------------------------
//  Terrain instancing. Unity draws instanced terrain as one flat patch mesh and
//  expects the vertex shader to displace it by sampling the heightmap, so this
//  is not optional decoration - without it every instanced patch draws flat at
//  the origin.
// -----------------------------------------------------------------------------
void UCTerrainInstancing(inout float4 positionOS, inout float3 normal, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);

    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;
    float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));

    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y  = height * _TerrainHeightmapScale.y;

    #ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
        normal = float3(0, 1, 0);
    #else
        normal = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rgb * 2 - 1;
    #endif
    uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

void UCTerrainInstancing(inout float4 positionOS, inout float3 normal)
{
    float2 uv = float2(0, 0);
    UCTerrainInstancing(positionOS, normal, uv);
}

void UCTerrainInstancing(inout float4 positionOS)
{
    float3 normal = float3(0, 0, 0);
    UCTerrainInstancing(positionOS, normal);
}

#endif // UC_TERRAIN_LIT_INPUT_INCLUDED
