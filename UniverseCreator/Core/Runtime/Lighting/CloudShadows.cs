// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    CloudShadows.cs
//  Purpose: The low and mid cloud sheets projected onto the world as a moving cookie on
//           the sun, pure. Broken cloud drops patches of light and shade that crawl
//           across a valley; this is the CPU side of that - how strong the projected
//           contrast is (there is contrast to project only when the deck is a mix of
//           cloud and gap, so it dies at both clear sky and solid overcast) and how far
//           the cookie has scrolled, from the same wind vector as the sheet so the
//           shadow and its cloud move together at one speed and direction.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The projected cloud-shadow terms, pure. The strength is the variance of a
    /// cloud/gap field - maximal at half cover, zero at clear (no cloud) and at overcast
    /// (no gaps) - so the moving patches appear only for broken cloud, where the
    /// two-term dimming already handles the uniform cases. The scroll is the sheet's own
    /// wind drift in the cookie's scale, so patch and cloud never separate.
    /// </summary>
    public static class CloudShadows
    {
        /// <summary>
        /// Projected shadow contrast in 0..1: the normalised variance of a two-state
        /// cloud/gap field, 4 * c * (1 - c). Zero at a clear sky (nothing to cast a
        /// patch) and at solid overcast (no gaps, no contrast), full at half cover.
        /// </summary>
        public static float ShadowContrast01(float combinedCover01)
        {
            float c = Mathf.Clamp01(combinedCover01);
            return 4f * c * (1f - c);
        }

        /// <summary>
        /// The cookie scroll in UV: the world drift the wind has carried (wind times
        /// elapsed time) expressed in the cookie's ground scale. Uses the same wind as
        /// the sheet, so the shadow moves at the cloud's speed and direction; only the
        /// scale differs (the shadow is read at a nearer ground tile).
        /// </summary>
        public static Vector2 CookieScrollUv(Vector2 windMPerS, float elapsedSeconds)
        {
            return windMPerS * elapsedSeconds * LightingConstants.CloudShadowScale;
        }

        /// <summary>
        /// The world drift in metres a cookie scroll represents: the inverse of the
        /// cookie's scale. Recovering the same metres from the cookie scroll and from the
        /// sheet's advection is what proves the shadow and the cloud share one wind.
        /// </summary>
        public static Vector2 WorldDriftM(Vector2 cookieScrollUv)
        {
            return cookieScrollUv / LightingConstants.CloudShadowScale;
        }

        /// <summary>
        /// Whether a region's bounce probes must be refreshed because its cloud cover has
        /// changed enough to move the shadow: true once the cover has drifted by the
        /// hysteresis band from the value the probes were last built at. Refreshing the
        /// bounce with the direct light is what stops a passing shadow dimming the direct
        /// term while the bounce stays bright - the fake-shadow the task warns of. The
        /// band keeps per-frame cover noise from thrashing the probe budget.
        /// </summary>
        public static bool ShouldRefreshProbes(float lastRefreshCover01, float currentCover01)
        {
            return Mathf.Abs(currentCover01 - lastRefreshCover01)
                   >= LightingConstants.CloudShadowProbeRefreshCoverDelta;
        }
    }
}
