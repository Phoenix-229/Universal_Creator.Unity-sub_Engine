// -----------------------------------------------------------------------------
//  Universe Creator - shared wind sampling.
//
//  One definition of "what is the wind doing at this world position", included by
//  every shader that responds to wind. Kept in one file deliberately: when grass
//  and water each carried their own copy of the gust maths they drifted apart,
//  and a gust would visibly cross a lake at a different speed than it crossed the
//  meadow beside it.
//
//  THE THREE VEGETATION CLASSES DO NOT MOVE ALIKE, and that difference is the
//  whole point of this file. A blade of grass is a thin cantilever with almost no
//  mass, so it lies right over in a breeze and recovers instantly. A shrub is a
//  bundle of woody stems: the base is rigid, the crown gives, and it flutters at
//  the leaf scale rather than swinging as a whole. A tree trunk does not visibly
//  move at all below gale force - only the canopy shifts, and only a little.
//  Feeding all three the same curve is exactly what makes procedural vegetation
//  read as fake, so each class has its own response, its own height profile and
//  its own flutter rate. See UCWindVegetation below.
//
//  Every uniform here is a global set by UCWindManager. None are per-material, so
//  a scene without a wind manager reads zeroes and renders perfectly still.
// -----------------------------------------------------------------------------

#ifndef UC_WIND_INCLUDED
#define UC_WIND_INCLUDED

// x, z = unit direction the wind blows towards. y, w unused.
float4 _UCWindDirection;

// x = normalised strength 0..1 (speed / 25 m/s)
// y = gust cell size in metres
// z = gust travel speed in metres per second
// w = wind time in seconds
float4 _UCWindParams;

// x = current gust multiplier, y = gustiness. z, w reserved.
float4 _UCWindGust;

// Per-class response, so the classes can be balanced against each other from one
// place instead of from every material.
//   x = grass, y = shrub, z = tree canopy, w = water
float4 _UCWindResponse;

// ---- interaction ------------------------------------------------------------
//
//  Up to eight movers can part vegetation - the player plus whatever else is
//  close enough to matter. This is a shader-side push and not a physics
//  response: grass carries no collider by design, and a shrub only needs to look
//  squeezed as someone passes, not to be simulated.
//
//  xyz = world position, w = radius in metres.
#define UC_MAX_INTERACTORS 8
float4 _UCInteractors[UC_MAX_INTERACTORS];

// How hard each mover pushes. Separate from the radius above because they are
// separate things: radius is how wide the parting is, this is how far the plants
// inside it move. A heavy vehicle and a cat can share a radius.
float _UCInteractStrength[UC_MAX_INTERACTORS];

// x = live interactor count. y, z, w reserved.
float4 _UCInteractParams;

// Baked sway maps from the Wind Sway Parameter Bake node.
// R = amplitude (shelter and exposure), G = phase.
TEXTURE2D(_UCSwayMap);
SAMPLER(sampler_UCSwayMap);

// xy = world XZ of the map's minimum corner, zw = world extent in metres.
float4 _UCSwayWindow;

// 0 when no map is bound, so the branch below costs one scalar compare.
float _UCSwayMapEnabled;

#define UC_TWO_PI 6.28318530718

// Vegetation classes. Kept as integers rather than as shader keywords because a
// keyword per class would triple the variant count for one scalar compare.
#define UC_CLASS_GRASS 0
#define UC_CLASS_SHRUB 1
#define UC_CLASS_TREE  2

/// Amplitude and phase at a world position. Returns (1, 0) with no baked map,
/// which is a uniform wind everywhere - correct, just not varied.
float2 UCSampleSway(float3 positionWS)
{
    if (_UCSwayMapEnabled < 0.5) return float2(1.0, 0.0);

    float2 uv = (positionWS.xz - _UCSwayWindow.xy) / max(float2(1e-4, 1e-4), _UCSwayWindow.zw);

    // Outside the baked area falls back to full strength rather than to zero: a
    // plant just past the map edge should not stand frozen next to a moving one.
    if (any(uv < 0.0) || any(uv > 1.0)) return float2(1.0, 0.0);

    return SAMPLE_TEXTURE2D_LOD(_UCSwayMap, sampler_UCSwayMap, uv, 0).rg;
}

/// Per-class constants, returned rather than branched on at each use site.
///   x = response multiplier from _UCWindResponse
///   y = strength exponent - how fast the class saturates with wind speed
///   z = flutter rate in Hz
///   w = flutter weight
float4 UCWindVegetation(int vegClass)
{
    // Grass: near-linear in wind speed, fast light flutter. It is already at
    // full lean in a moderate breeze, which is what grass actually does.
    if (vegClass == UC_CLASS_GRASS)
        return float4(_UCWindResponse.x, 1.0, 4.3, 1.0);

    // Shrub: woody, so it needs more wind before it gives. The exponent above 1
    // is what holds it still in a breeze that already has the grass laid over.
    // Slower flutter than grass because a leaf cluster has more mass than a blade.
    if (vegClass == UC_CLASS_SHRUB)
        return float4(_UCWindResponse.y, 1.6, 2.6, 0.7);

    // Tree canopy: barely responds. The high exponent means a light wind moves
    // it almost not at all and a gale still only nudges it - which is the point.
    // Slow, heavy flutter; a canopy does not shiver.
    return float4(_UCWindResponse.z, 2.2, 1.1, 0.45);
}

/// Signed bend factor at a world position, for one vegetation class.
/// Roughly 0..1 after response scaling.
float UCWindBendClass(float3 positionWS, int vegClass, float flutterScale)
{
    float4 veg = UCWindVegetation(vegClass);

    float2 dir      = _UCWindDirection.xz;
    float  gustSize = max(1.0, _UCWindParams.y);
    float  travel   = _UCWindParams.z;
    float  t        = _UCWindParams.w;

    float2 sway  = UCSampleSway(positionWS);
    float  amp   = sway.x;
    float  phase = sway.y;

    // Distance along the wind axis. Subtracting travel * t makes the wave move
    // downwind, so a gust sweeps across the field instead of pulsing in place.
    float along = dot(positionWS.xz, dir) - travel * t;
    float gust  = sin(along * (UC_TWO_PI / gustSize) + phase * UC_TWO_PI);

    // Class-rate flutter so a shrub beside a clump of grass is visibly a
    // different plant, not the same motion at a different amplitude.
    float flutter = sin(t * veg.z + phase * 12.0
                        + positionWS.x * 0.7 + positionWS.z * 0.53);

    // The exponent is what separates the classes at low wind. pow on a saturated
    // base is safe, and the strength is already 0..1.
    float strength = pow(saturate(_UCWindParams.x), veg.y);

    // Biased positive: wind pushes downwind and eases, it does not swing evenly
    // through vertical the way a pendulum does.
    float bend = (0.62 + 0.38 * gust)
               * (1.0 + veg.w * flutterScale * 0.25 * flutter);

    return strength * amp * bend * veg.x;
}

/// Backwards-compatible entry point: grass behaviour, no class argument.
float UCWindBend(float3 positionWS, float flutterScale)
{
    return UCWindBendClass(positionWS, UC_CLASS_GRASS, flutterScale);
}

/// How the bend distributes up the plant. Grass is a cantilever and bends all
/// the way down; a shrub holds at the base and gives at the crown; a tree holds
/// almost to the canopy. This curve is as important as the amplitude - a shrub
/// bending from its roots looks like a large blade of grass no matter how small
/// you make the number.
float UCWindHeightProfile(float heightFrac, int vegClass)
{
    if (vegClass == UC_CLASS_GRASS)
        return heightFrac * heightFrac;

    if (vegClass == UC_CLASS_SHRUB)
    {
        // Nothing below 25% of the height moves at all, then it ramps quickly.
        float h = saturate((heightFrac - 0.25) / 0.75);
        return h * h;
    }

    // Tree: the lower 55% is trunk and does not move. What is left is canopy.
    float c = saturate((heightFrac - 0.55) / 0.45);
    return c * c;
}

/// Sideways push from anything walking through the plant. Zero when nothing is
/// near, which is the common case, so the loop exits on the first compare.
float3 UCWindPush(float3 positionWS, float heightFrac, float squeeze)
{
    float3 push = float3(0, 0, 0);

    int count = (int)_UCInteractParams.x;
    if (count <= 0 || squeeze <= 0.0) return push;

    [loop]
    for (int i = 0; i < UC_MAX_INTERACTORS; i++)
    {
        if (i >= count) break;

        float4 it = _UCInteractors[i];
        float r = max(0.01, it.w);

        float2 d = positionWS.xz - float2(it.x, it.z);

        float dist = length(d);
        if (dist >= r) continue;

        // Vertical reach: something at ankle height should not part a plant
        // three metres above it.
        float dy = abs(positionWS.y - it.y);
        if (dy > r) continue;

        // Squared falloff so the parting is tight around the body rather than a
        // soft dome the size of the radius.
        float fall = 1.0 - dist / r;
        fall = fall * fall * (1.0 - dy / r);

        float2 away = dist > 1e-4 ? d / dist : float2(1, 0);

        float amount = fall * r * 0.5 * squeeze * _UCInteractStrength[i];

        push.x += away.x * amount;
        push.z += away.y * amount;
    }

    // Only the part of the plant that can bend gets pushed.
    return push * heightFrac;
}

/// World-space displacement for a plant.
///   pivotWS     - the root position, so a whole plant shares one gust
///   heightFrac  - 0 at the root, 1 at the tip
///   response    - per-material stiffness on top of the class response
///   vegClass    - UC_CLASS_GRASS / SHRUB / TREE
float3 UCWindDisplacementClass(float3 pivotWS, float heightFrac, float response,
                               float flutterScale, int vegClass)
{
    float bend = UCWindBendClass(pivotWS, vegClass, flutterScale) * response;

    float profile = UCWindHeightProfile(heightFrac, vegClass);
    float lean = bend * profile;

    float2 dir = _UCWindDirection.xz;

    // Leaning shortens the plant vertically. Without this the tip stretches
    // beyond its own length and grass visibly grows as it bends.
    float drop = lean * lean * 0.22;

    return float3(dir.x * lean, -drop, dir.y * lean);
}

/// Grass-class displacement, for callers that predate the class argument.
float3 UCWindDisplacement(float3 pivotWS, float heightFrac,
                          float response, float flutterScale)
{
    return UCWindDisplacementClass(pivotWS, heightFrac, response,
                                   flutterScale, UC_CLASS_GRASS);
}

/// Wind strength for surface effects such as water ripples, 0..1.
/// Water reads the gust crest rather than the bend, so a lake gets its own
/// pattern instead of a scaled copy of the meadow beside it.
float UCWindSurfaceStrength(float3 positionWS)
{
    float2 sway  = UCSampleSway(positionWS);
    float  t     = _UCWindParams.w;
    float  travel = _UCWindParams.z;
    float2 dir   = _UCWindDirection.xz;

    // Wider cells than the vegetation gust: wind patterns on water read as
    // large drifting patches ("cat's paws"), not as blade-scale ripple.
    float cell = max(1.0, _UCWindParams.y * 2.3);
    float along = dot(positionWS.xz, dir) - travel * t;

    float c1 = sin(along * (UC_TWO_PI / cell));
    float c2 = sin(along * (UC_TWO_PI / cell) * 0.37 + 1.7);
    float g  = 0.5 + 0.5 * saturate(_UCWindGust.y) * (0.6 * c1 + 0.4 * c2);

    return saturate(_UCWindParams.x * sway.x * _UCWindResponse.w * (0.55 + 0.75 * g));
}

#endif // UC_WIND_INCLUDED
