// -----------------------------------------------------------------------------
//  Universe Creator - the reflection fallback ORDER (S-78), in one shared place.
//
//  A screen-space reflection ray that leaves the screen, finds nothing, or exhausts its budget
//  must NOT return black - a black off-screen reflection reads as a hole and is the classic
//  screen-space artefact. It falls back in a fixed order, coarsest world knowledge last:
//
//    1  height-field trace   the SAME ray the CPU marches for probe fill (HeightfieldTrace),
//                            against the terrain. A hit returns that ground's lit radiance from
//                            the irradiance grid - a ridge reflected in a puddle is the ridge.
//    2  irradiance grid      a downward or short ray that clears no terrain takes the grid's
//                            radiance along the reflect direction - never black, always lit.
//    3  analytic sky         an upward ray that clears the terrain takes the sky LUT along it,
//                            the same radiance the dome shows there (UC_Sky).
//
//  This is structurally what Lumen does with its radiance cache; ours is coarser but, like it,
//  never returns a hole. Requires UC_Lighting.hlsl (the grid) already included by the caller.
// -----------------------------------------------------------------------------

#ifndef UC_REFLECTION_FALLBACK_INCLUDED
#define UC_REFLECTION_FALLBACK_INCLUDED

#include "HeightfieldTrace.hlsl"
#include "UC_Sky.hlsl"

// The reflected radiance for a ray that the screen-space march could not resolve. worldPos is
// the reflecting surface, reflectDir the reflected view vector. The height-field reach comes
// from the shared trace schedule (_UCHeightfieldTraceParams.w), so there is no second reach here.
float3 UCReflectionFallbackOrdered(float3 worldPos, float3 reflectDir)
{
    float3 dir = normalize(reflectDir);
    float reach = _UCHeightfieldTraceParams.w;

    // 1 - height-field trace against the terrain. A hit means the reflected ray strikes ground:
    // return that ground's up-facing lit radiance from the grid (the "terrain" of terrain-and-sky).
    float hitDist;
    float occluded = UCHeightfieldOccluded(worldPos, dir, reach, hitDist);
    if (occluded > 0.5)
    {
        float3 hitPos = worldPos + dir * hitDist;
        return UCSampleIrradiance(hitPos, float3(0.0, 1.0, 0.0));
    }

    // 3 - an upward ray that cleared the terrain sees open sky: the sky LUT along the ray.
    if (dir.y > 0.0)
        return UCSampleSkyRadiance(dir);

    // 2 - a downward/short ray that hit nothing within reach: the grid along the reflect
    // direction, the lit environment radiance - coarse, but never a black hole.
    return UCSampleIrradiance(worldPos, dir);
}

#endif // UC_REFLECTION_FALLBACK_INCLUDED
