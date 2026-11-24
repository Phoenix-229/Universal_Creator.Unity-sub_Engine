// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UC_Underwater.hlsl
//  Purpose: What the world looks like when the camera is inside water.
//
//  WHY THIS EXISTS
//  ---------------
//  Water rendered only as a surface reads as a sheet of blue glass: you dive
//  through it and the world on the other side looks exactly like the world above
//  it. That is the loudest possible tell that the water is not a volume, and no
//  amount of work on the surface shader fixes it, because the surface is not
//  where the effect lives - the medium is.
//
//  Real water is a participating medium. Every ray that reaches the eye has
//  travelled through metres of it and been attenuated on the way, wavelength by
//  wavelength: red goes first, then green, and blue survives longest. That is why
//  the deep is blue-green, why a red wetsuit turns grey at fifteen metres, and why
//  you cannot see the far wall of a lake at all. One exponential per pixel over
//  the distance the ray travelled reproduces all three, because all three are the
//  same effect measured at different distances.
//
//  WHY IT IS A GLOBAL AND NOT A MATERIAL PROPERTY
//  ----------------------------------------------
//  It is a property of where the CAMERA is, not of what is being drawn. The
//  terrain, the water surface, the grass and every rock share one answer, and one
//  global set once per frame keeps them consistent. Putting it in a material block
//  would also break SRP batching for every material that read it - the same reason
//  the ripple simulation lives outside UnityPerMaterial.
//
//  WHY NOT URP'S OWN FOG
//  ---------------------
//  Two reasons. URP fog is a scene setting, so writing to it underwater means
//  restoring it on the way out and losing the user's own fog while submerged - and
//  a world with weather driving its fog would fight over the same field. And fog
//  is applied per shader AFTER lighting with no depth of its own, so it cannot
//  darken with depth beneath the surface, which is most of what selling "you are
//  ten metres down" consists of.
//
//  COST
//  ----
//  Skipped entirely by a branch on _UCUnderwaterOn when the camera is dry, which
//  is nearly always. Submerged it is one exp3, one saturate and two lerps per
//  pixel, no texture reads. That is inside the mobile budget this project holds.
// -----------------------------------------------------------------------------

#ifndef UC_UNDERWATER_INCLUDED
#define UC_UNDERWATER_INCLUDED

// Set once per frame by UCUnderwaterView. All zero when the camera is dry, and
// the branch below then costs one compare per pixel.
float  _UCUnderwaterOn;         // 1 submerged, 0 dry. Also fades on the way through.
float4 _UCUnderwaterColor;      // rgb medium colour, a unused
float4 _UCUnderwaterParams;     // x visibility metres, y 1/visibility, z surfaceY, w depth fade metres

// High-tier extras. Each is 0 on the low and mid tiers, so the whole caustics or
// god-ray block below is one compare per pixel there and nothing more - the reason
// they are scalars and not shader keywords is that a keyword variant would double
// the water/terrain/detail shader permutation count for an effect two tiers never
// run. Beer-Lambert extinction above is unconditional and runs on every tier; only
// these two are gated, which is exactly what "degrade, never drop" asks for - the
// deep still darkens with depth on a low-end phone, it simply does it without the
// dancing light and the shafts.
float  _UCUnderwaterCaustics;      // caustic brightness, 0 disables (low/mid)
float  _UCUnderwaterGodRays;       // shaft brightness, 0 disables (low/mid)
float4 _UCUnderwaterCausticParams; // x tile(m), y scroll speed, z/w reserved
float4 _UCUnderwaterSun;           // xyz world-space direction TO the sun, w unused

// How much of the medium a ray has accumulated over `metres`, 0 to 1.
//
// Per channel rather than one scalar. A single grey factor fades the world towards
// one colour uniformly, which reads as haze; water removes red about four times
// faster than blue, and doing it per channel is the entire difference between
// "foggy" and "underwater". The multipliers are the shape of clear water's
// absorption curve, normalised so the authored visibility distance means what it
// says on the green channel.
half3 UCUnderwaterAbsorption(float metres)
{
    float inv = _UCUnderwaterParams.y;
    float3 rate = float3(2.6, 1.0, 0.55) * inv;
    return 1.0h - half3(exp(-metres * rate.x),
                        exp(-metres * rate.y),
                        exp(-metres * rate.z));
}

// Analytic caustics - no texture, so there is nothing for the buyer to author and
// nothing to load. Two ridged, drifting sine layers crossing at an angle give the
// wandering polygonal cells real caustics form: the ridge 1-|sin| sharpens the
// pattern into bright lines, the product of two layers leaves sparse bright cells
// with dark water between, and squaring tightens the highlights. Projected on world
// XZ so the pattern sits in the world and slides over the ground as the light moves,
// not smeared across the screen with the camera.
half UCUnderwaterCaustic(float2 worldXZ, float t)
{
    float tile = max(0.5, _UCUnderwaterCausticParams.x);
    float2 p = worldXZ / tile;
    float s = _UCUnderwaterCausticParams.y * t;

    float2 a = float2(p.x + s,           p.y + s * 0.7);
    float2 b = float2(p.x * 1.3 - s * 0.6, p.y * 0.7 + s * 0.9);

    float r1 = 1.0 - abs(sin(a.x) * cos(a.y));
    float r2 = 1.0 - abs(sin(b.x + b.y) * cos(b.x - b.y));
    float c = r1 * r2;
    return (half)saturate(c * c);
}

// Analytic god-rays. Real shafts are sunlight scattered by the medium along the view
// ray; the eye sees them where it looks toward the sun through clear water. The
// alignment term is a tight cone around the sun direction; a moving band breaks the
// glow into separate shafts; and the build term grows with the wet distance so the
// shafts accumulate through the water instead of sitting on the near surface. No
// texture, a handful of ops, and it reads as volumetric light without a volume.
half UCUnderwaterGodRays(float3 rayDir, float metres, float t)
{
    float align = saturate(dot(rayDir, _UCUnderwaterSun.xyz));
    align = pow(align, 6.0);
    float bands = 0.5 + 0.5 * sin(rayDir.x * 18.0 + rayDir.z * 14.0 + t * 0.6);
    float build = saturate(metres * _UCUnderwaterParams.y * 0.5);
    return (half)(align * bands * build);
}

// Applies the medium to a shaded colour.
//
//   color      what the surface shaded to
//   positionWS where that surface is, for the ray length
//
// The ray length is measured from the camera rather than taken from the depth
// buffer so this works in every pass, including transparent ones that never wrote
// depth.
half3 UCApplyUnderwater(half3 color, float3 positionWS)
{
    if (_UCUnderwaterOn < 0.001) return color;

    float surfaceY = _UCUnderwaterParams.z;

    float3 eye = GetCameraPositionWS();
    float3 ray = positionWS - eye;

    // The full eye-to-pixel length and its direction, kept before the wet-fraction
    // clamp below shortens `metres` - the god-ray cone needs the true look direction,
    // not the trimmed one.
    float rawLen = length(ray);
    float3 rayDir = ray / max(1e-4, rawLen);
    float metres = rawLen;

    // ONLY THE WET PART OF THE RAY ABSORBS.
    //
    // Water absorbs along the distance travelled THROUGH WATER, and a camera just
    // under the surface sees plenty that is not: the far bank, the hillside behind
    // it, anything above the waterline. Charging the whole eye-to-pixel distance to
    // the medium tinted all of it, so wading in ankle-deep turned the entire
    // landscape green while the shallow water at your feet stayed clear - the
    // absorption was strongest exactly where there was least water.
    //
    // Against a plane surface the wet fraction is exact rather than estimated:
    // similar triangles on the ray's vertical component give the parameter at which
    // it crosses the waterline, and the segment before that is the part in water.
    // Pixels below the surface keep the full length; the guard on ray.y is for a
    // ray running flat along the surface, which crosses nowhere.
    if (positionWS.y > surfaceY && ray.y > 1e-4)
        metres *= saturate((surfaceY - eye.y) / ray.y);

    half3 absorb = UCUnderwaterAbsorption(metres);

    // The medium darkens with depth, because the light reaching it has already been
    // absorbed on its way DOWN as well as on its way to the eye. Without this the
    // deep end of a lake is the same brightness as the shallows and the whole body
    // reads as one flat tint.
    float depthBelow = max(0.0, surfaceY - positionWS.y);
    float fade = _UCUnderwaterParams.w;
    half lightLeft = (half)exp(-depthBelow / max(1.0, fade));
    half3 medium = _UCUnderwaterColor.rgb * lerp(0.25h, 1.0h, lightLeft);

    half3 wet = lerp(color, medium, absorb);

    // Caustics: the dancing light the surface focuses onto whatever is below it. Cast
    // from above, so they ride the light that survived the trip down (lightLeft) and
    // fade out in the deep exactly as the medium does. Zero on the low and mid tiers,
    // where this block is skipped by the compare.
    if (_UCUnderwaterCaustics > 0.001)
    {
        half caust = UCUnderwaterCaustic(positionWS.xz, _Time.y);
        wet += _UCUnderwaterColor.rgb * caust * (half)_UCUnderwaterCaustics * lightLeft;
    }

    // God-rays: sunlight scattered toward the eye along the wet part of the ray.
    // Added to the medium rather than to the surface, because a shaft is light in the
    // water itself, seen against whatever happens to be behind it. High tier only.
    if (_UCUnderwaterGodRays > 0.001)
    {
        half shaft = UCUnderwaterGodRays(rayDir, metres, _Time.y);
        wet += _UCUnderwaterColor.rgb * shaft * (half)_UCUnderwaterGodRays;
    }

    // Scaled by _UCUnderwaterOn rather than branched on it, so a camera crossing
    // the surface transitions over a few centimetres instead of switching in one
    // frame. A hard switch is visible as a flash on every dive.
    return lerp(color, wet, saturate(_UCUnderwaterOn));
}

#endif // UC_UNDERWATER_INCLUDED
