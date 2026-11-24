// -----------------------------------------------------------------------------
//  Universe Creator - shared heightfield ray-march (GPU twin of HeightfieldTrace.cs).
//
//  The screen-space passes (contact shadow, SSGI's off-screen fallback, reflections)
//  march the SAME ray the CPU marches for probe fill, stepping the identical schedule
//  so a ridge that occludes a probe also occludes the pixel in front of it. The three
//  numbers that define the schedule - near stride, growth, step count - are NOT literals
//  here; they arrive in _UCHeightfieldTraceParams, published by the lighting manager from
//  LightingConstants, so there is one schedule and no second copy to drift. A scene with
//  no manager reads a zero param and the trace reports open sky, which is the safe default.
// -----------------------------------------------------------------------------

#ifndef UC_HEIGHTFIELD_TRACE_INCLUDED
#define UC_HEIGHTFIELD_TRACE_INCLUDED

// x = near stride (m), y = growth per step, z = step count, w = schedule reach (m).
// The mirror of HeightfieldTrace's near step / growth / steps / DistanceAfter(steps).
float4 _UCHeightfieldTraceParams;

// Terrain height field, one channel, sampled in world XZ. xy = world origin of texel (0,0),
// z = metres per texel, w = height scale that maps the sampled 0..1 to world metres. The
// same source the CPU height sampler reads, published by the manager.
TEXTURE2D(_UCTerrainHeight);   SAMPLER(sampler_UCTerrainHeight);
float4 _UCTerrainHeightPlacement;   // xy origin, z metres/texel, w height scale
float2 _UCTerrainHeightBaseSize;    // x = base world Y, y = world span (m) the texture covers

// Terrain height at a world XZ point - the GPU twin of the CPU heightAt callback.
float UCSampleTerrainHeight(float2 worldXZ)
{
    float2 uv = (worldXZ - _UCTerrainHeightPlacement.xy) / max(_UCTerrainHeightBaseSize.y, 1e-3);
    float h = SAMPLE_TEXTURE2D_LOD(_UCTerrainHeight, sampler_UCTerrainHeight, uv, 0).r;
    return _UCTerrainHeightBaseSize.x + h * _UCTerrainHeightPlacement.w;
}

// The stride on step i - the near stride grown geometrically, identical to StepLength(i).
float UCHeightfieldStepLength(int i)
{
    return _UCHeightfieldTraceParams.x * pow(_UCHeightfieldTraceParams.y, max(0, i));
}

// March a ray against the height field and return 1 if the ground rises above it before it
// clears the reach, 0 for open sky. The loop is the exact CPU loop: grow the stride each step,
// clamp to maxDistance, sample the terrain at the stepped world XZ, early-out on the first
// sample where the ray is at or below the ground. outHitDistance carries the intercept range.
float UCHeightfieldOccluded(float3 origin, float3 dir, float maxDistance, out float outHitDistance)
{
    outHitDistance = 0.0;
    if (maxDistance <= 0.0) return 0.0;

    float horiz = length(dir.xz);
    if (dot(dir, dir) < 1e-12 || horiz < 1e-6) { outHitDistance = maxDistance; return 0.0; }
    float3 d = normalize(dir);

    int steps = (int)min(_UCHeightfieldTraceParams.z, 256.0);
    float t = 0.0;
    [loop] for (int i = 0; i < steps; i++)
    {
        t += UCHeightfieldStepLength(i);
        if (t > maxDistance) t = maxDistance;
        float3 p = origin + d * t;
        float ground = UCSampleTerrainHeight(p.xz);
        if (p.y <= ground) { outHitDistance = t; return 1.0; }
        if (t >= maxDistance) break;
    }
    outHitDistance = min(t, maxDistance);
    return 0.0;
}

#endif // UC_HEIGHTFIELD_TRACE_INCLUDED
