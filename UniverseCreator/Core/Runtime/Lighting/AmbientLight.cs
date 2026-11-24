// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    AmbientLight.cs
//  Purpose: The scene's ambient fill, taken from the sky itself rather than an
//           authored colour. The sky model's hemispherical integral is nine SH
//           coefficients; this packs them into URP's ambient probe so the diffuse
//           fill is always the colour of the sky overhead - blue at midday, warm at
//           sunset, and, as cloud thickens, greyed and dimmed toward a flat overcast
//           dome. One source, four behaviours, none of them a gradient asset.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.Rendering;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Builds and applies the sky's own ambient light as a URP L2 probe. The clear-sky
    /// coefficients integrate <see cref="SkyModel.SkyRadiance"/> through Unity's diffuse basis; cloud cover then greys
    /// the flat term toward its own luminance, flattens the directional terms toward an
    /// even dome, and dims the whole probe to the overcast fraction of a clear day - so
    /// an overcast ambient is measurably duller and greyer with nothing authored.
    /// </summary>
    public static class AmbientLight
    {
        /// <summary>
        /// The sky's cosine-weighted diffuse fill as a Unity <see cref="SphericalHarmonicsL2"/>, before exposure.
        /// </summary>
        /// <param name="sunDir">Unit world direction to the sun, with +Y up.</param>
        /// <param name="turbidity">Finite atmospheric turbidity, at least one.</param>
        /// <param name="cloudCover01">Combined cloud cover, clamped to 0..1; overcast greys, dims and flattens the fill.</param>
        /// <returns>Linear diffuse radiance per unit albedo; zero lower-hemisphere input. Rebuild only at the existing sky bake cadence.</returns>
        public static SphericalHarmonicsL2 BuildAmbientProbe(
            Vector3 sunDir, float turbidity, float cloudCover01)
        {
            // UC midday repair: raw radiance SH is not a cosine-convolved Unity ambient probe.
            // Unity's directional-probe kernel peaks at 2, whereas the normalized L2 cosine kernel peaks at
            // 1/4 + 1/2 + 5/16 = 17/16. Convert that light API scale to radiance integration (17/32).
            var probe = new SphericalHarmonicsL2();
            const int thetaSamples = 8, phiSamples = 16;
            float dTheta = Mathf.PI * 0.5f / thetaSamples, dPhi = Mathf.PI * 2 / phiSamples;
            for (int t = 0; t < thetaSamples; t++)
            {
                float theta = (t + 0.5f) * dTheta, sine = Mathf.Sin(theta), cosine = Mathf.Cos(theta);
                float diffuseWeight = sine * dTheta * dPhi / Mathf.PI * (17f / 32f);
                for (int p = 0; p < phiSamples; p++)
                {
                    float phi = (p + 0.5f) * dPhi;
                    var direction = new Vector3(sine * Mathf.Cos(phi), cosine, sine * Mathf.Sin(phi));
                    probe.AddDirectionalLight(direction, SkyModel.SkyRadiance(direction, sunDir, turbidity), diffuseWeight);
                }
            }
            float cover = Mathf.Clamp01(cloudCover01);

            // Overcast dims the whole dome to the same fraction of clear sky the direct
            // illuminance falls to, so ambient and key drop together under cloud.
            float overcastFrac = LightingConstants.OvercastDayLux / LightingConstants.NoonClearSkyLux;
            float dim = Mathf.Lerp(1f, overcastFrac, cover);

            // Cloud greys the flat term toward its own luminance and flattens the
            // directional terms toward zero, an even sky with no bright side.
            float lum = 0.2126f * probe[0, 0] + 0.7152f * probe[1, 0] + 0.0722f * probe[2, 0];
            Color dc = new Color(
                Mathf.Lerp(probe[0, 0], lum, cover),
                Mathf.Lerp(probe[1, 0], lum, cover),
                Mathf.Lerp(probe[2, 0], lum, cover));
            float directional = (1f - cover);

            probe[0, 0] = dc.r * dim; probe[1, 0] = dc.g * dim; probe[2, 0] = dc.b * dim;
            for (int i = 1; i < 9; i++)
            {
                probe[0, i] *= directional * dim;
                probe[1, i] *= directional * dim;
                probe[2, i] *= directional * dim;
            }
            return probe;
        }

        /// <summary>
        /// The sky's ambient with the ground's own indirect bounce folded in. The base
        /// probe is the sky overhead; the ground then reflects the downwelling flat fill
        /// back up, per channel, so a bright surface adds light and a dark one adds almost
        /// none. For a Lambertian surface the reflected radiance is the albedo times the
        /// incident radiance, so the added fill is simply <c>albedo * skyFlat</c> - no
        /// tuning constant - which is why a snowfield (near-white) roughly doubles the
        /// ambient floor and a wet road barely lifts it. Added to the flat term only: the
        /// bounce is the diffuse indirect floor, not a second bright side.
        /// </summary>
        public static SphericalHarmonicsL2 BuildAmbientProbe(
            Vector3 sunDir, float turbidity, float cloudCover01, Color groundAlbedo)
        {
            SphericalHarmonicsL2 probe = BuildAmbientProbe(sunDir, turbidity, cloudCover01);

            // Lambertian reflection of the sky's own flat fill, per channel: the ground
            // hands back the fraction of the downwelling light its albedo does not absorb.
            probe[0, 0] += Mathf.Clamp01(groundAlbedo.r) * probe[0, 0];
            probe[1, 0] += Mathf.Clamp01(groundAlbedo.g) * probe[1, 0];
            probe[2, 0] += Mathf.Clamp01(groundAlbedo.b) * probe[2, 0];
            return probe;
        }

        /// <summary>
        /// Drive the scene's ambient from the sky. Sets the ambient mode to custom and
        /// writes the built probe; the lighting controller calls this whenever the sun
        /// or the sky's air changes enough to matter.
        /// </summary>
        public static void Apply(Vector3 sunDir, float turbidity, float cloudCover01)
        {
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = BuildAmbientProbe(sunDir, turbidity, cloudCover01);
        }

        /// <summary>
        /// Drive the scene's ambient from the sky, with the ground's indirect bounce
        /// folded in from <paramref name="groundAlbedo"/> - the surface reflectance the
        /// controller reads under the point (terrain plus snow).
        /// </summary>
        public static void Apply(Vector3 sunDir, float turbidity, float cloudCover01, Color groundAlbedo)
        {
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe =
                BuildAmbientProbe(sunDir, turbidity, cloudCover01, groundAlbedo);
        }
    }
}
