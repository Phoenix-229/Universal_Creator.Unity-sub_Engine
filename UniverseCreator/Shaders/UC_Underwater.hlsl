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

    float metres = length(ray);

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

    // Scaled by _UCUnderwaterOn rather than branched on it, so a camera crossing
    // the surface transitions over a few centimetres instead of switching in one
    // frame. A hard switch is visible as a flash on every dive.
    return lerp(color, wet, saturate(_UCUnderwaterOn));
}

#endif // UC_UNDERWATER_INCLUDED
