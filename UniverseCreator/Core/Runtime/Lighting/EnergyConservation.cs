// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    EnergyConservation.cs
//  Purpose: Make "every bounce removes energy" a rule the code enforces, not an
//           arithmetic hope. A diffuse surface can only send back a fraction of what
//           lands on it, so the bounce albedo is clamped below one here at the single
//           point every ground albedo passes through - which keeps the multi-bounce
//           series base / (1 - albedo) finite instead of diverging. The furnace ceiling
//           is that same series summed to infinity: the analytic brightness a closed box
//           of one albedo converges toward but, being a partial sum of a series with
//           ratio below one, never reaches. The self-test fills a box against this
//           ceiling, because a lighting model that gains energy reads as a tonemapper
//           bug and gets chased there for a day.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The energy-conservation guards for the irradiance path (S-69, C-16). Pure and
    /// allocation-free: <see cref="ClampAlbedo"/> is the one enforcement point for the
    /// below-one reflectance rule; <see cref="FurnaceCeiling"/> is the analytic
    /// steady-state a closed box converges toward; <see cref="Gains"/> is the assertion
    /// primitive - true when reflected light exceeds the incident light on any channel,
    /// which must never happen.
    /// </summary>
    public static class EnergyConservation
    {
        /// <summary>
        /// Hold a bounce albedo below one, per channel. Every ground albedo is passed
        /// through this before it reflects, so an authored terrain-layer colour of one (a
        /// pure-white splat) cannot make the feedback series diverge. Negative input is
        /// floored at zero - a surface cannot reflect a negative amount.
        /// </summary>
        public static Vector3 ClampAlbedo(Vector3 albedo)
        {
            float max = LightingConstants.IrradianceMaxDiffuseAlbedo;
            return new Vector3(
                Mathf.Clamp(albedo.x, 0f, max),
                Mathf.Clamp(albedo.y, 0f, max),
                Mathf.Clamp(albedo.z, 0f, max));
        }

        /// <summary>
        /// The furnace-test ceiling: the brightness a closed box of uniform
        /// <paramref name="albedo"/> lit by <paramref name="source"/> converges toward as
        /// bounces accumulate - the geometric sum source * (1 + a + a^2 + ...) = source *
        /// 1 / (1 - a), per channel. The albedo is clamped first so the denominator is at
        /// least 0.05 and the ceiling is finite; a real multi-bounce accumulation is a
        /// partial sum of this series and must stay strictly below it.
        /// </summary>
        public static Vector3 FurnaceCeiling(Vector3 source, Vector3 albedo)
        {
            Vector3 a = ClampAlbedo(albedo);
            return new Vector3(
                source.x / (1f - a.x),
                source.y / (1f - a.y),
                source.z / (1f - a.z));
        }

        /// <summary>
        /// True when the reflected radiance exceeds the incident radiance on any channel by
        /// more than a floating-point epsilon - the condition an energy-conserving bounce
        /// must never meet. Used by the self-test to fail loudly rather than let a gain
        /// leak into the grid and surface later as a tonemapper puzzle.
        /// </summary>
        public static bool Gains(Vector3 reflected, Vector3 incident)
        {
            const float eps = 1e-5f;
            return reflected.x > incident.x + eps
                || reflected.y > incident.y + eps
                || reflected.z > incident.z + eps;
        }
    }
}
