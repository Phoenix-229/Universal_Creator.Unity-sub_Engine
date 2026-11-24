// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UC_WaterInput.hlsl
//  Purpose: The water material's constant buffer, its samplers, and the two
//           functions both passes need.
//
//  WHY A SHARED INCLUDE AND NOT A CBUFFER PER PASS
//  -----------------------------------------------
//  SRP batching requires every pass of a shader to declare an identical
//  UnityPerMaterial block - same members, same order. Two hand-written copies
//  drift the moment one property is added to one of them, and the symptom is not
//  a compile error: the material silently drops out of the SRP batcher and the
//  frame time goes up with nothing in the log to say why. One copy in one file
//  cannot drift.
//
//  The vertex displacement lives here for the same reason. The depth pass has to
//  move its vertices by exactly what the forward pass moves them by, or the two
//  disagree by up to _WaveHeight and the surface z-fights against its own depth.
// -----------------------------------------------------------------------------

#ifndef UC_WATER_INPUT_INCLUDED
#define UC_WATER_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Wind. Water is the third consumer of the shared wind field, after grass and
// shrubs, and it reads the same globals so a gust crosses a lake at the speed it
// crossed the meadow. Included here rather than in the .shader so both passes get
// it from one place - the depth pass displaces vertices by the wind chop too, and
// if it did not, the surface would z-fight against its own depth.
#include "UC_Wind.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _ShallowColor;
    float4 _DeepColor;
    float  _DepthFadeMetres;
    float  _Transparency;
    float  _Smoothness;
    float  _FresnelPower;
    float4 _SpecularTint;

    float  _WaveTileMetres;
    float  _NormalScale;
    float4 _FlowSpeed;
    float  _SecondaryScale;
    float  _Refraction;
    float  _WaterScreenSpaceEffects; // UC-T04-08: capped by quality without mutating authored materials.
    float  _UseVertexFlow;
    float  _FlowStrength;

    float  _FoamEnabled;
    float4 _FoamColor;
    float  _FoamIntensity;
    float  _FoamEdgeWidth;
    float  _FoamScrollSpeed;
    float  _FoamTileMetres;
    float  _FlowFoam;

    // RIV-4 W1. Every one of these MUST be in UnityPerMaterial: a material float
    // declared in Properties but missing from this buffer silently drops the object
    // out of SRP batching instead of erroring, which is a performance bug nobody
    // sees until they profile.
    float  _ShoreWashEnabled;
    float  _ShoreWashPeriod;
    float  _ShoreWashReach;
    float  _ShoreWashSharpness;
    float  _ShoreWashStrength;
    float  _ShoreWashTravel;

    float  _CausticsEnabled;
    float4 _CausticsColor;
    float  _CausticsIntensity;
    float  _CausticsTileMetres;
    float  _CausticsSpeed;
    float  _CausticsDepthFade;

    float  _WaveHeight;
    float  _WaveLength;
    float  _WaveSpeed;

    // Spectral richness and weather, all scalar so the SRP-batched block stays
    // array-free (see the header): _WaveOctaves fades wave trains in and out for a
    // lake-vs-ocean look, _WaveStormAmp is the weather layer's swell multiplier, and
    // _WaveFlowAdvect drifts the whole swell downstream on a river.
    float  _WaveOctaves;
    float  _WaveStormAmp;
    float  _WaveFlowAdvect;

    float  _EdgeSoftenMetres;

    float  _RippleStrength;
    float  _RippleFoam;

    float  _WindRipple;
    float  _WindChopLength;
    float  _WindFoam;

    float  _UCIce;
CBUFFER_END

TEXTURE2D(_WaveNormalMap);      SAMPLER(sampler_WaveNormalMap);
TEXTURE2D(_FoamTexture);        SAMPLER(sampler_FoamTexture);
TEXTURE2D(_CausticsTexture);    SAMPLER(sampler_CausticsTexture);

// -----------------------------------------------------------------------------
//  Interactive ripple simulation
// -----------------------------------------------------------------------------
//
//  Set globally by UCWaterRippleSim, NOT per material - deliberately outside the
//  UnityPerMaterial block. A global inside that block breaks SRP batching for
//  every water material in the scene, and the simulation is one field shared by
//  all of them anyway.
//
//  When no simulation exists in the scene, _UCRippleEnabled is 0 and every sample
//  below is skipped. Water then falls back to the ambient wave texture and looks
//  exactly as it did before this system existed, which is what has to happen on a
//  device where the sim is switched off for budget.

TEXTURE2D(_UCRippleTex);        SAMPLER(sampler_UCRippleTex);
TEXTURE2D(_UCRippleFoamTex);    SAMPLER(sampler_UCRippleFoamTex);

float4 _UCRippleWindow;     // xy world origin XZ, z window size in metres, w 1/size
float  _UCRippleEnabled;

// World XZ to simulation UV. Outside the window this lands outside 0..1, which the
// samplers clamp - so the caller must fade by the same test rather than relying on
// the clamp, or the window's edge texel smears across the whole ocean.
float2 UCRippleUV(float2 worldXZ)
{
    return (worldXZ - _UCRippleWindow.xy) * _UCRippleWindow.w;
}

// 1 inside the simulated window, falling to 0 at its edge. Squared falloff over
// the outer eighth: a linear fade is still visible as a soft square, and the eye
// finds a straight line in open water immediately.
float UCRippleWeight(float2 uv)
{
    float2 e = min(uv, 1.0 - uv);
    float t = saturate(min(e.x, e.y) * 8.0);
    return t * t;
}

// Packed simulation result: xy surface slope, z height in metres, w foam.
float4 UCSampleRipple(float2 worldXZ)
{
    if (_UCRippleEnabled < 0.5) return float4(0, 0, 0, 0);

    float2 uv = UCRippleUV(worldXZ);
    float w = UCRippleWeight(uv);
    if (w <= 0.0) return float4(0, 0, 0, 0);

    float4 s = SAMPLE_TEXTURE2D_LOD(_UCRippleTex, sampler_UCRippleTex, uv, 0);
    float foam = SAMPLE_TEXTURE2D_LOD(_UCRippleFoamTex, sampler_UCRippleFoamTex, uv, 0).r;

    return float4(s.xy * w, s.z * w, foam * w);
}

// -----------------------------------------------------------------------------
//  Wind chop
// -----------------------------------------------------------------------------
//
//  Wind on water is not the same effect as wind on grass, and it is not a splash
//  either, so it is a third term rather than a scale on one of the other two.
//
//  What it has to reproduce is the thing everyone recognises without being able to
//  name: still water is a mirror, and the moment a breeze touches it the mirror
//  breaks into a fine texture that travels with the wind. It is short-wavelength
//  and it is anisotropic - the crests line up ACROSS the wind, not along it - and
//  it comes and goes in patches as gusts pass. A lake in a gale is not a lake with
//  taller swell; it is a lake covered in chop with foam torn off the crests.
//
//  Three practical consequences of that, all of which are in the code below:
//
//    1. Wavelength is short and fixed by _WindChopLength, independent of
//       _WaveLength. Ambient swell is metres to tens of metres; chop is tens of
//       centimetres. Scaling the swell up would give a bigger lake, not a windier
//       one.
//    2. The pattern is elongated ACROSS the wind, so its crests are long lines
//       running across the wind and its wavelength is measured along the wind -
//       the direction it travels. An isotropic ripple reads as rain, not wind.
//       (The note here used to say "stretched along the wind direction", which is
//       the opposite arrangement and gives crests parallel to the wind.)
//    3. Strength comes from UCWindSurfaceStrength, which already carries the gust
//       crests and the sheltered-hollow sway map - so a bay in the lee of a ridge
//       stays glassy while open water chops up, for free.

// Chop height in metres at a world position. Cheap on purpose: two sine octaves,
// no texture fetch, because this runs per vertex on a water mesh that can be
// dense and the target is a midrange phone.
//
// WHICH AXIS CARRIES THE SHORT WAVELENGTH
// ---------------------------------------
// The fast term runs along the WIND, not across it. A sine whose phase varies
// along an axis has its crest lines perpendicular to that axis, so putting the
// short wavelength on the wind direction is what makes the crests run across the
// wind - which is what wind waves do, and what note 2 above asks for. It was on
// the across-wind axis, which lined the crests up parallel to the wind and gave
// the corrugated-sheet look the note says to avoid.
//
// The time term is SUBTRACTED on that axis so the train travels downwind. Added,
// it marches into the wind, which reads as wrong even to someone not looking for
// it.
float UCWindChopHeight(float2 worldXZ, float t, float strength)
{
    if (_WindRipple <= 0.0001 || strength <= 0.001) return 0.0;

    // Unit in XZ by contract - see _UCWindDirection in UC_Wind.hlsl - so the
    // wavelength below is in real metres.
    float2 dir = _UCWindDirection.xz;
    float2 across = float2(-dir.y, dir.x);

    float len = max(0.15, _WindChopLength);
    float k = 6.28318530718 / len;

    float a = dot(worldXZ, dir)    * k        - t * 3.1;
    float b = dot(worldXZ, across) * k * 0.42 + t * 1.7;

    // 0.6/0.4 rather than equal weights: the downwind train is the one the eye
    // reads as wind, the across-wind term only stops it looking like a set of
    // parallel ridges.
    float h = sin(a) * 0.6 + sin(b) * 0.4;

    // Amplitude scales with the square of strength. Chop appears suddenly at a
    // threshold rather than growing linearly from nothing, which is why a lake
    // looks either glassy or textured and rarely in between.
    return h * _WindRipple * 0.045 * strength * strength;
}

// Tangent-space slope of that same chop. Derived analytically from the height
// above rather than sampled with finite differences: the wavelength is smaller
// than the vertex spacing on a distant tile, so differencing the mesh would
// alias badly while the derivative stays correct at any density.
//
// The two axes and the two phases MUST match UCWindChopHeight exactly. A slope
// taken from a different pattern than the displacement lights crests that are not
// where the mesh put them, and the surface reads as though the specular were
// sliding across it.
float2 UCWindChopSlope(float2 worldXZ, float t, float strength)
{
    if (_WindRipple <= 0.0001 || strength <= 0.001) return float2(0, 0);

    float2 dir = _UCWindDirection.xz;
    float2 across = float2(-dir.y, dir.x);

    float len = max(0.15, _WindChopLength);
    float k = 6.28318530718 / len;

    float a = dot(worldXZ, dir)    * k        - t * 3.1;
    float b = dot(worldXZ, across) * k * 0.42 + t * 1.7;

    float amp = _WindRipple * strength * strength;

    // d/dp of sin(dot(p, axis) * k + phase) is cos(...) * k * axis. The time term's
    // sign does not enter the spatial derivative, so subtracting it above changes
    // where the crest is, not how steep it is. The 0.35 folds in the height scale
    // and keeps the slope in a range the normal blend expects.
    float2 s = dir * (cos(a) * 0.6) + across * (cos(b) * 0.4 * 0.42);
    return s * amp * 0.35;
}

// -----------------------------------------------------------------------------
//  Vertex displacement
// -----------------------------------------------------------------------------

// This is the VERTICAL term of the Gerstner wave. The horizontal companion lives in
// UCAmbientWaveHorizontal below: it used to be omitted entirely because, unfaded, the
// sideways push crawls a clipped shoreline in and out of its bank - that companion now
// adds it back with a hard per-vertex water-weight fade so the shore stays put while
// open water gets the crest-sharpening. This vertical term is the one mirrored in C#
// (UCWaterSurface.WaveOffset) for buoyancy; keep the two in lock-step.
//
// Up to four octaves at incommensurate directions. Two beat visibly at their sum
// frequency; three do not resolve inside the distance the camera can see; the
// fourth broadens an open ocean's spectrum. How many are ACTIVE is set per body
// by _WaveOctaves (1..4): a pond runs one train, a lake two, an open ocean four,
// and the count fades an octave in fractionally so switching tier blends rather
// than pops. An octave whose gate is zero costs its sine and nothing else - there
// is no shader variant and no array, so the SRP-batched CBUFFER is untouched.
//
// _WaveStormAmp is the weather layer's storm multiplier: 1 on a calm day (the
// surface is then exactly what _WaveHeight authored), rising into a gale. It scales
// the swell as a whole rather than any one octave, because a storm raises the sea,
// it does not retune it.
//
// The amplitude is scaled by the vertex's own water weight (vertex alpha), so a
// river ribbon one metre from its bank is not lifted clear of the ground it is
// supposed to be sitting in.
// MIRRORED IN C# by UCWaterSurface.WaveOffset, which is what buoyancy samples.
// Same octaves, same directions, same weights, same frequency ratios, same octave
// gate and same storm scale. Change one and you must change the other, or a boat
// floats above the crest it is supposed to be sitting on and the bug is invisible
// in a still frame.
float UCAmbientWaveOffset(float2 worldXZ, float t, float weight)
{
    if (_WaveHeight <= 0.0001) return 0.0;

    float k = 6.28318530718 / max(0.5, _WaveLength);

    // Fractional octave gates. Octave i is fully on once the count passes i+1 and
    // fades in over the count before it, so _WaveOctaves = 1.5 is one full train and
    // half of a second - a continuous dial, not four discrete steps that click.
    float o0 = saturate(_WaveOctaves);
    float o1 = saturate(_WaveOctaves - 1.0);
    float o2 = saturate(_WaveOctaves - 2.0);
    float o3 = saturate(_WaveOctaves - 3.0);

    float a = sin((worldXZ.x *  0.86 + worldXZ.y *  0.51) * k        + t * _WaveSpeed);
    float b = sin((worldXZ.x * -0.39 + worldXZ.y *  0.92) * k * 1.71 + t * _WaveSpeed * 1.43);
    float c = sin((worldXZ.x *  0.62 + worldXZ.y * -0.78) * k * 2.63 + t * _WaveSpeed * 0.79);
    float d = sin((worldXZ.x * -0.81 + worldXZ.y * -0.34) * k * 3.55 + t * _WaveSpeed * 1.17);

    float h = a * 0.55 * o0 + b * 0.30 * o1 + c * 0.15 * o2 + d * 0.07 * o3;

    return h * _WaveHeight * _WaveStormAmp * weight;
}

// Horizontal (Gerstner) companion to UCAmbientWaveOffset. The vertical term above moves
// a vertex up and down; the full Gerstner wave ALSO pulls it along the surface, AGAINST
// the propagation direction by Q*A*cos(phase), which is what bunches crests sharp and
// hollows troughs wide - the single thing that separates a believable swell from a field
// of rolling sine humps.
//
// WHY THIS IS BACK (noted supersession of the "trimmed to vertical" decision above):
// the horizontal term was dropped because, UNFADED, it drags the mesh sideways at the
// shoreline where the surface is already clipped against the ground, so a lake edge
// crawls in and out of its bank. The fix is not to omit it but to fade it HARD by the
// vertex's own water weight: `weight*weight` means the clipped shore (weight -> 0) gets
// essentially no horizontal push while open water (weight = 1) gets the full effect.
// That removes the original failure mode rather than the whole feature.
//
// Shader-only on purpose. UCWaterSurface.WaveOffset (buoyancy) stays vertical-only for
// the reason stated in its header: a floating hull integrates horizontal orbital motion
// away to nothing, so mirroring it would cost a per-frame CPU solve to move a boat by
// less than its own skin. Heights still agree at the authored sample position; only
// where that height is drawn shifts, by centimetres.
//
// Octave directions, phases and gates are IDENTICAL to UCAmbientWaveOffset so the two
// belong to the same wave - a horizontal pattern taken from different phases would smear
// the crests sideways off the humps the vertical term built.
float2 UCAmbientWaveHorizontal(float2 worldXZ, float t, float weight)
{
    if (_WaveHeight <= 0.0001) return float2(0.0, 0.0);

    float k = 6.28318530718 / max(0.5, _WaveLength);

    float o0 = saturate(_WaveOctaves);
    float o1 = saturate(_WaveOctaves - 1.0);
    float o2 = saturate(_WaveOctaves - 2.0);
    float o3 = saturate(_WaveOctaves - 3.0);

    float pa = (worldXZ.x *  0.86 + worldXZ.y *  0.51) * k        + t * _WaveSpeed;
    float pb = (worldXZ.x * -0.39 + worldXZ.y *  0.92) * k * 1.71 + t * _WaveSpeed * 1.43;
    float pc = (worldXZ.x *  0.62 + worldXZ.y * -0.78) * k * 2.63 + t * _WaveSpeed * 0.79;
    float pd = (worldXZ.x * -0.81 + worldXZ.y * -0.34) * k * 3.55 + t * _WaveSpeed * 1.17;

    // Pull each octave back along its OWN propagation direction, weighted exactly as the
    // vertical term weights that octave's amplitude.
    float2 g =
        float2( 0.86,  0.51) * (cos(pa) * 0.55 * o0) +
        float2(-0.39,  0.92) * (cos(pb) * 0.30 * o1) +
        float2( 0.62, -0.78) * (cos(pc) * 0.15 * o2) +
        float2(-0.81, -0.34) * (cos(pd) * 0.07 * o3);

    float storm = _WaveStormAmp > 0.0 ? _WaveStormAmp : 1.0;

    // UC-T04-09: an excursion clamp alone does not bound a summed wave's Jacobian.
    // Bound the sum of each frequency/amplitude/direction outer-product norm.
    // This is a constant-weight/flow bound; shore gradients still need mesh acceptance.
    float spectral = 0.55 * o0 * dot(float2(0.86, 0.51), float2(0.86, 0.51))
        + 0.30 * 1.71 * o1 * dot(float2(-0.39, 0.92), float2(-0.39, 0.92))
        + 0.15 * 2.63 * o2 * dot(float2(0.62, -0.78), float2(0.62, -0.78))
        + 0.07 * 3.55 * o3 * dot(float2(-0.81, -0.34), float2(-0.81, -0.34));
    float amplitude = min(_WaveHeight * storm * 0.5, 0.35 / max(0.0001, k * spectral));
    return -g * (amplitude * weight * weight);
}

// Ambient swell plus whatever the interactive simulation has going on at this
// point. The two ADD rather than one replacing the other: a splash in a choppy
// sea rides on the chop, it does not flatten it.
//
// `flow` is xy = surface direction (unit), z = speed in m/s - the same value the
// fragment shades with. It drives the river-only swell advection below and nothing
// on a lake or ocean, where _WaveFlowAdvect is 0.
//
// Physics only mirrors the ambient term. The simulation's contribution, the wind
// chop, AND the flow advection are all left out of buoyancy on purpose - a ripple
// or a chop is centimetres of displacement over centimetres of wavelength, far
// below what a floating body integrates over, and the advection only slides the
// same swell along the current by less than a hull's length per second. Reading any
// of them back would cost a pipeline stall per frame to move an object by less than
// its own skin depth. Ambient swell is what physics needs and physics has it.
float UCWaveOffset(float2 worldXZ, float t, float3 flow, float weight)
{
    // River swell rides the current: advect the whole ambient pattern downstream so
    // its crests travel with the water instead of standing still in the world. Gated
    // by _WaveFlowAdvect, which is 0 on still water - so an ocean or a lake feeds the
    // ambient term the raw world position and stays a line-for-line match for the C#
    // buoyancy mirror. GPU-only, for the reason in this function's header.
    float2 sampleXZ = worldXZ - flow.xy * flow.z * t * _WaveFlowAdvect;
    float h = UCAmbientWaveOffset(sampleXZ, t, weight);
    float micro = 0.0; // UC-T04-09: GPU-only interactive/wind height has an explicit 5 cm query error ceiling.

    if (_RippleStrength > 0.0001)
        micro += UCSampleRipple(worldXZ).z * _RippleStrength;

    // Wind chop rides on top of both, for the same reason the splash rides on the
    // swell: they are different physical processes happening to the same surface,
    // not three settings for one process.
    //
    // Deliberately NOT mirrored in UCWaterSurface.WaveOffset, which is what
    // buoyancy reads. Chop is a few centimetres over a wavelength shorter than a
    // boat is wide, so a hull integrates it away to nothing - mirroring it would
    // cost a per-frame CPU evaluation to move a floating object by less than its
    // own draught. Ambient swell is what physics needs and physics already has it.
    if (_WindRipple > 0.0001)
    {
        float strength = UCWindSurfaceStrength(float3(worldXZ.x, 0.0, worldXZ.y));
        micro += UCWindChopHeight(worldXZ, t, strength);
    }

    return h + clamp(micro, -0.05, 0.05) * weight;
}

// Horizontal displacement wrapper, sibling to UCWaveOffset. Uses the SAME flow-advected
// sample position so, on a river, the horizontal push rides the current exactly as the
// vertical swell does; on still water _WaveFlowAdvect is 0 and this is the raw world XZ.
// Only the ambient Gerstner term is horizontal - ripples, wind chop and the river advect
// contribute no net sideways pull worth a vertex's cost, same judgement as the mirror.
float2 UCWaveHorizontal(float2 worldXZ, float t, float3 flow, float weight)
{
    float2 sampleXZ = worldXZ - flow.xy * flow.z * t * _WaveFlowAdvect;
    return UCAmbientWaveHorizontal(sampleXZ, t, weight);
}

// UC-T04-09: forward/depth share ordering and physical local-depth limits.
// Zero-depth/zero-weight boundaries retain their actual bank contact.
float3 UCDisplacedWaterPosition(float3 positionWS, float t, float4 flow, float depth)
{
    float limit = max(0.0, depth) * 0.25;
    float2 horizontal = UCWaveHorizontal(positionWS.xz, t, flow.xyz, flow.w);
    float magnitude = length(horizontal);
    if (magnitude > limit && magnitude > 0.0) horizontal *= limit / magnitude;
    positionWS.y += clamp(UCWaveOffset(positionWS.xz, t, flow.xyz, flow.w), -limit, limit);
    positionWS.xz += horizontal;
    return positionWS;
}

// -----------------------------------------------------------------------------
//  Waves
// -----------------------------------------------------------------------------

// Two scrolling samples of one normal map, combined by the whiteout blend rather
// than by averaging. Averaging two normals flattens both; whiteout keeps the
// steeper of the two slopes on each axis, which is what overlapping wave trains
// actually do.
//
// flow.xy is the surface current direction in world XZ, flow.z its speed. For a
// lake or an ocean this is the uniform _FlowSpeed; for a river the mesh carries
// a per-vertex value in its vertex colour, so the water visibly runs downhill
// and turns with the channel instead of drifting one way across the whole world.
half3 UCSampleWaves(float2 worldXZ, float t, float3 flow)
{
    float tile = max(0.5, _WaveTileMetres);

    float2 drift = flow.xy * flow.z * t;

    // UC-T04-08: a texture feature at a fixed UV moves downstream only when
    // current advection is subtracted. Both detail scales travel with the river.
    float2 uv0 = (worldXZ - drift) / tile;
    float2 uv1 = (worldXZ - drift * 0.63) / (tile * max(0.05, _SecondaryScale));

    half3 n0 = UnpackNormalScale(
        SAMPLE_TEXTURE2D(_WaveNormalMap, sampler_WaveNormalMap, uv0), _NormalScale);
    half3 n1 = UnpackNormalScale(
        SAMPLE_TEXTURE2D(_WaveNormalMap, sampler_WaveNormalMap, uv1), _NormalScale);

    half3 blended = half3(n0.xy + n1.xy, n0.z * n1.z);
    return SafeNormalize(blended);
}

#endif // UC_WATER_INPUT_INCLUDED
