// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ProbeFill.cs
//  Purpose: Fill one ambient-cube probe from three physical terms - the sky above it, the
//           lit ground beneath it, and the light bouncing in from its surroundings - each
//           masked by what the terrain around the probe can actually see. The horizon is
//           estimated from a handful of height samples reaching a short way out, which is
//           enough to know whether a probe stands in an open field, at the foot of a
//           cliff, or in the floor of a valley - the difference between plausible ambient
//           occlusion and wrong, at no screen-space cost. Pure: it takes the light and a
//           height/albedo lookup and returns the six faces, so it is the same on any thread.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Builds an <see cref="IrradianceProbe"/> from the sky, ground and bounce terms. Each
    /// face is the sky it can see (up-weighted, masked by the open-sky estimate), the
    /// ground beneath it (down-weighted), and a share of the surrounding bounce. Sun and
    /// sky reach the probe only as far as the terrain around it lets them, so a sheltered
    /// probe is correctly dim and, out of the warm sun, correctly cool.
    /// </summary>
    public static class ProbeFill
    {
        // The eight compass directions the horizon is sampled along, as unit XZ steps.
        private static readonly Vector2[] Compass = BuildCompass();

        private static Vector2[] BuildCompass()
        {
            int n = Mathf.Max(1, LightingConstants.IrradianceHorizonSamples);
            var dirs = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = (i / (float)n) * Mathf.PI * 2f;
                dirs[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
            return dirs;
        }

        /// <summary>
        /// The open-sky fraction above a probe in 0..1: 1 in the open, falling as terrain
        /// around the probe rises above it. Each compass direction contributes the largest
        /// elevation angle the terrain reaches within the horizon reach; the mask is one
        /// minus the mean of those angles over a right angle.
        /// </summary>
        public static float SkyVisibility01(Vector3 probePos, Func<float, float, float> terrainHeightAt)
        {
            if (terrainHeightAt == null) return 1f;
            float reach = LightingConstants.IrradianceHorizonReachM;
            float occ = 0f;
            for (int i = 0; i < Compass.Length; i++)
            {
                float maxAngle = 0f;
                for (float d = reach * 0.25f; d <= reach; d += reach * 0.25f)
                {
                    float hx = probePos.x + Compass[i].x * d;
                    float hz = probePos.z + Compass[i].y * d;
                    float rise = terrainHeightAt(hx, hz) - probePos.y;
                    if (rise > 0f) maxAngle = Mathf.Max(maxAngle, Mathf.Atan2(rise, d));
                }
                occ += Mathf.Clamp01(maxAngle / (Mathf.PI * 0.5f));
            }
            return Mathf.Clamp01(1f - occ / Compass.Length);
        }

        /// <summary>
        /// Whether the sun reaches the probe, 0..1: march toward the sun and block if the
        /// terrain rises above the sun ray at any step. A probe at the foot of a cliff on
        /// the shaded side gets no direct sun, so its ground and bounce go cold.
        /// </summary>
        public static float SunVisibility01(Vector3 probePos, Func<float, float, float> terrainHeightAt, Vector3 sunDir)
        {
            if (terrainHeightAt == null || sunDir.y <= 0f) return sunDir.y <= 0f ? 0f : 1f;
            float reach = LightingConstants.IrradianceHorizonReachM;
            Vector2 stepXZ = new Vector2(sunDir.x, sunDir.z);
            float horiz = stepXZ.magnitude;
            if (horiz < 1e-5f) return 1f;                 // sun overhead: nothing blocks it
            stepXZ /= horiz;
            float slope = sunDir.y / horiz;               // rise per metre of horizontal travel toward the sun
            for (float d = reach * 0.25f; d <= reach; d += reach * 0.25f)
            {
                float hx = probePos.x + stepXZ.x * d;
                float hz = probePos.y * 0f + probePos.z + stepXZ.y * d;
                float rayY = probePos.y + slope * d;
                if (terrainHeightAt(hx, hz) > rayY) return 0f;
            }
            return 1f;
        }

        /// <summary>
        /// The radiance leaving the ground under a probe: its albedo times the light
        /// falling on it (the sky fill always, plus the sun scaled by its angle and by
        /// whether the sun actually reaches here). This is what the downward faces see.
        /// </summary>
        public static Vector3 GroundRadiance(Vector3 albedo, Vector3 skyRadiance, float skyVisibility,
                                             Vector3 sunColor, float sunIlluminance, Vector3 sunDir, float sunVisibility)
        {
            float ndotl = Mathf.Max(0f, sunDir.y);        // the ground faces up
            Vector3 incoming = skyRadiance * skyVisibility + sunColor * (sunIlluminance * ndotl * sunVisibility);
            return Vector3.Scale(albedo, incoming);
        }

        /// <summary>
        /// The bounce term: the mean over the compass samples of each neighbour's albedo
        /// times the direct light it receives, so red soil around a probe reddens it and a
        /// shaded neighbour adds nothing. Scaled by the shared bounce strength.
        /// </summary>
        public static Vector3 BounceRadiance(Vector3 probePos, Func<float, float, float> terrainHeightAt,
                                             Func<float, float, Vector3> albedoAt, Vector3 sunColor,
                                             float sunIlluminance, Vector3 sunDir)
        {
            if (albedoAt == null) return Vector3.zero;
            float reach = LightingConstants.IrradianceHorizonReachM;
            float ndotl = Mathf.Max(0f, sunDir.y);
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < Compass.Length; i++)
            {
                float sx = probePos.x + Compass[i].x * reach;
                float sz = probePos.z + Compass[i].y * reach;
                Vector3 sampleProbe = new Vector3(sx, probePos.y, sz);
                float sunVis = SunVisibility01(sampleProbe, terrainHeightAt, sunDir);
                Vector3 direct = sunColor * (sunIlluminance * ndotl * sunVis);
                sum += Vector3.Scale(albedoAt(sx, sz), direct);
            }
            return sum / Compass.Length * LightingConstants.IrradianceBounceStrength;
        }

        /// <summary>
        /// Assemble the six faces: each is the sky it faces (up-weighted, masked by open
        /// sky), the ground it faces (down-weighted), and an even share of the bounce. The
        /// up face is sky-dominated, the down face ground-dominated, the sides a blend.
        /// </summary>
        public static IrradianceProbe Fill(Vector3 skyRadiance, float skyVisibility,
                                           Vector3 groundRadiance, Vector3 bounceRadiance)
        {
            Vector3 sky = skyRadiance * Mathf.Clamp01(skyVisibility);
            return new IrradianceProbe
            {
                posY = Face(Vector3.up, sky, groundRadiance, bounceRadiance),
                negY = Face(Vector3.down, sky, groundRadiance, bounceRadiance),
                posX = Face(Vector3.right, sky, groundRadiance, bounceRadiance),
                negX = Face(Vector3.left, sky, groundRadiance, bounceRadiance),
                posZ = Face(Vector3.forward, sky, groundRadiance, bounceRadiance),
                negZ = Face(Vector3.back, sky, groundRadiance, bounceRadiance),
            };
        }

        private static Vector3 Face(Vector3 dir, Vector3 sky, Vector3 ground, Vector3 bounce)
        {
            float skyW = Mathf.Clamp01(0.5f + 0.5f * dir.y);   // 1 up, 0.5 horizon, 0 down
            float groundW = 1f - skyW;
            return sky * skyW + ground * groundW + bounce;
        }
    }
}
