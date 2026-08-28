// -----------------------------------------------------------------------------
//  Universe Creator - the ONE home for temporal reprojection, the neighbourhood-variance clamp
//  and the disocclusion reject (S-80). Every accumulating screen-space term we own - SSR, SSGI
//  and contact shadows - steadies itself across frames the SAME way, so there is one place the
//  ghosting rules live rather than a divergent copy in each shader.
//
//  MOTION VECTORS, NOT JUST THE CAMERA. The primary reprojection reads URP's motion texture,
//  which carries the per-pixel screen displacement for BOTH camera and object motion - so the
//  history of a moving car follows the car and does not smear a trail behind it. Where no motion
//  vector is present (a pipeline built without the pass), the caller's previous-frame view-
//  projection reprojects the static world point instead: correct for camera motion, and exactly
//  the fallback SSR/SSGI used before this shared home replaced their inline copies.
//
//  THE CLAMP. A reprojected history sample is only trusted inside the current frame's local
//  neighbourhood: the mean +/- one standard deviation of an NxN trace box. History outside that
//  box is a stale value from a surface that has since changed, so it is clamped back in - the
//  variance clamp that turns accumulation into stability without turning it into a smear.
// -----------------------------------------------------------------------------

#ifndef UC_TEMPORAL_REPROJECT_INCLUDED
#define UC_TEMPORAL_REPROJECT_INCLUDED

// URP produces this as a global when a pass requests ScriptableRenderPassInput.Motion; the value
// stored is the forward screen displacement (this frame -> last frame) in UV space, so last
// frame's coordinate for a pixel is uv - motion. Declared here (as URP's own motion blur does)
// because our fullscreen passes do not otherwise pull it in.
TEXTURE2D_X(_MotionVectorTexture);

// Last frame's UV from the motion texture (camera AND object motion). Returns false off-screen.
bool UCReprojectByMotion(float2 uv, out float2 prevUV)
{
    float2 mv = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_LinearClamp, uv).xy;
    prevUV = uv - mv;
    return all(prevUV >= 0.0) && all(prevUV <= 1.0);
}

// Last frame's UV from a world point through last frame's view-projection (the static / camera-
// only fallback for a pipeline without motion vectors). Returns false behind the eye or off-screen.
bool UCReprojectByViewProj(float3 worldPos, float4x4 prevViewProj, out float2 prevUV)
{
    float4 pc = mul(prevViewProj, float4(worldPos, 1.0));
    if (pc.w <= 0.0) { prevUV = 0.0; return false; }
    prevUV = pc.xy / pc.w * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    prevUV.y = 1.0 - prevUV.y;
#endif
    return all(prevUV >= 0.0) && all(prevUV <= 1.0);
}

// Disocclusion reject: the history is trusted only if the surface it recorded sits at the same
// eye depth as this pixel. A surface newly revealed behind a moving edge fails this and is dropped
// to the current frame - the reject that stops history bleeding across a depth discontinuity.
bool UCHistoryDepthValid(float historyEye, float currentEye)
{
    float tol = 0.1 * currentEye + 0.05;
    return abs(historyEye - currentEye) < tol;
}

// Neighbourhood variance-clamp box from the running first and second moments of an NxN trace box.
// The reprojected history is clamped into [mean - sigma, mean + sigma] before it is blended.
void UCVarianceBox(float m1, float m2, int count, out float lo, out float hi)
{
    float inv   = 1.0 / max(1, count);
    float mean  = m1 * inv;
    float sigma = sqrt(max(0.0, m2 * inv - mean * mean));
    lo = mean - sigma; hi = mean + sigma;
}

void UCVarianceBox(float3 m1, float3 m2, int count, out float3 lo, out float3 hi)
{
    float inv    = 1.0 / max(1, count);
    float3 mean  = m1 * inv;
    float3 sigma = sqrt(max(0.0, m2 * inv - mean * mean));
    lo = mean - sigma; hi = mean + sigma;
}

#endif // UC_TEMPORAL_REPROJECT_INCLUDED
