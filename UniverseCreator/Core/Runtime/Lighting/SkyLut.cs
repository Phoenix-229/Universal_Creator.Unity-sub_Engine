// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    SkyLut.cs
//  Purpose: The contract and the refresh policy for the precomputed sky table -
//           a tiny 32x16 texture the skybox samples instead of evaluating the
//           Perez model per pixel. This file owns two things a headless run can
//           check: how big the table is, and WHEN it is worth rebuilding. The
//           table's pixels are the Perez sky (SkyModel); allocating and filling
//           the GPU texture, and sampling it from the skybox, are the render
//           stage's job - here is only the shape and the cadence.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The precomputed-sky table's fixed dimensions and the thresholds that decide
    /// when a fresh bake is worth its cost. The sky changes slowly - the sun creeps,
    /// haze drifts - so re-evaluating the Perez model every pixel every frame spends
    /// a great deal to show almost the same sky. RGB stores radiance divided by a
    /// texture-wide power of two; alpha stores its exponent so bright horizons stay
    /// finite in half floats and interpolate linearly before the shared reader decodes.
    /// Each camera also retains one 512-Color CPU bake buffer (8192 bytes).
    /// Baking it into 32x16 texels and
    /// rebuilding only on a meaningful change turns that into a few small bakes an
    /// hour, which is the whole reason the table exists.
    /// </summary>
    public static class SkyLut
    {
        // 32 columns of azimuth-relative-to-sun, 16 rows of elevation. Four half-
        // floats per texel is 32 * 16 * 8 = 4096 bytes - small enough that its cost
        // is the bake, never the memory.
        public const int Width = 32;
        public const int Height = 16;
        public const int BytesPerTexel = 8;   // RGBAHalf
        public const int ByteSize = Width * Height * BytesPerTexel;

        // A rebuild is worth it only past one of these. The sun's elevation moves
        // fastest near the horizon at a handful of degrees a minute, so a few
        // degrees between bakes keeps the count to a few an hour there and to almost
        // none near noon; turbidity and cloud shift slowly and event-like, so their
        // thresholds catch a changing sky without chasing noise.
        public const float RebuildElevationDeltaDeg = 3f;
        public const float RebuildTurbidityDelta = 0.25f;
        public const float RebuildCloudDelta = 0.1f;

        /// <summary>
        /// Whether the table should be rebuilt, given what it was last baked at and
        /// where the sky is now. True as soon as any one of sun elevation, turbidity
        /// or cloud cover has moved past its threshold - the table is stale in that
        /// one respect and the whole 4 KB is cheap enough to redo rather than patch.
        /// </summary>
        public static bool ShouldRebuild(
            float lastElevationDeg, float currentElevationDeg,
            float lastTurbidity, float currentTurbidity,
            float lastCloudCover01, float currentCloudCover01)
        {
            if (Mathf.Abs(currentElevationDeg - lastElevationDeg) >= RebuildElevationDeltaDeg) return true;
            if (Mathf.Abs(currentTurbidity - lastTurbidity) >= RebuildTurbidityDelta) return true;
            if (Mathf.Abs(currentCloudCover01 - lastCloudCover01) >= RebuildCloudDelta) return true;
            return false;
        }
    }
}
