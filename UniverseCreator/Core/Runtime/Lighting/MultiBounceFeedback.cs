// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MultiBounceFeedback.cs
//  Purpose: Turn the single-bounce probe grid into an effectively-infinite-bounce one
//           for the price of one extra sample. The grid is already refreshed a few
//           probes at a time; the only change here is what a neighbouring surface is
//           allowed to reflect. In the single-bounce term a surface reflects only the
//           direct light it receives; here it also reflects the ambient it already
//           held - the previous refresh's grid value at that surface. So the second
//           bounce arrives on the next refresh, the third on the one after, and the
//           series settles because every physical albedo is below one. A wall that
//           reddened the floor now lets the floor redden it back, a shade deeper each
//           refresh, until it stops. This is the same feedback a radiance cache uses;
//           ours costs one grid read per neighbour sample and stores nothing new.
//
//  The clamp is a backstop, not the normal path. With physical outdoor albedos the
//  series settles far below the ceiling and never trips it - the tint just deepens and
//  stops on its own. The clamp exists for the two ways the loop can run away: an
//  authored albedo above one, or a fully enclosed bright space, either of which would
//  otherwise gain energy every refresh. Both are unphysical, so the clamp logs once
//  when it engages to name the cause rather than hide it.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The temporal-feedback half of the irradiance grid (S-67). One term - the light a
    /// neighbouring surface re-emits from the ambient it already carried - plus a
    /// convergence clamp. Pure and deterministic: it reads the previous refresh's grid
    /// through the supplied lookups and returns radiance, so the same inputs give the
    /// same output on any thread and any machine. The driver composes this with
    /// <see cref="ProbeFill.BounceRadiance"/> (the direct, first bounce) and feeds the
    /// clamped sum into <see cref="ProbeFill.Fill"/>.
    /// </summary>
    public static class MultiBounceFeedback
    {
        // The same compass geometry ProbeFill's single bounce samples, so the extra
        // read lands on the identical neighbours - one extra grid sample per probe-fill
        // sample, never a second, differently-placed set that could disagree with it.
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
        /// The feedback radiance for a probe: the mean over the compass neighbours of
        /// each neighbour's albedo times the ambient it held on the previous refresh,
        /// scaled by the shared bounce strength. This is bounces two and up; bounce one
        /// is <see cref="ProbeFill.BounceRadiance"/>. <paramref name="previousAmbientAt"/>
        /// returns the last refresh's grid value at a world XZ, so this class needs no
        /// reference to the grid and stays pure. Null lookups yield zero, which is
        /// exactly "feedback disabled".
        /// </summary>
        public static Vector3 FeedbackRadiance(Vector3 probePos,
                                              Func<float, float, Vector3> previousAmbientAt,
                                              Func<float, float, Vector3> albedoAt,
                                              float bounceStrength)
        {
            if (previousAmbientAt == null || albedoAt == null) return Vector3.zero;
            float reach = LightingConstants.IrradianceHorizonReachM;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < Compass.Length; i++)
            {
                float sx = probePos.x + Compass[i].x * reach;
                float sz = probePos.z + Compass[i].y * reach;
                Vector3 ambient = previousAmbientAt(sx, sz);
                sum += Vector3.Scale(albedoAt(sx, sz), ambient);
            }
            return sum / Compass.Length * bounceStrength;
        }

        /// <summary>
        /// The accumulated indirect for a probe, capped for convergence: the first bounce
        /// plus the feedback, clamped so the total luminance never exceeds
        /// <paramref name="clampScale"/> times the first bounce's luminance. Clamping on
        /// luminance and scaling the whole colour keeps the hue, so a red bleed stays red
        /// when capped - it does not desaturate. The cap is only meaningful once the first
        /// bounce carries real light (a small floor guards the divide and stops a probe in
        /// near-total shade from having its legitimate faint feedback scaled to nothing);
        /// with a physical albedo the total settles well under the cap and
        /// <paramref name="engaged"/> stays false.
        /// </summary>
        public static Vector3 ClampAccumulated(Vector3 singleBounce, Vector3 feedback,
                                               float clampScale, out bool engaged)
        {
            engaged = false;
            Vector3 total = singleBounce + feedback;
            if (clampScale <= 0f) return total;

            float singleLum = Luminance(singleBounce);
            if (singleLum <= FirstBounceLuminanceFloor) return total;

            float totalLum = Luminance(total);
            float ceiling = clampScale * singleLum;
            if (totalLum > ceiling && totalLum > 1e-6f)
            {
                engaged = true;
                return total * (ceiling / totalLum);
            }
            return total;
        }

        /// <summary>
        /// Report that the convergence clamp engaged, once per session. It means an
        /// albedo above one or a fully enclosed bright space is being capped, which is a
        /// content problem, so it is named rather than silently corrected every refresh.
        /// The driver calls this when <see cref="ClampAccumulated"/> returns engaged.
        /// </summary>
        public static void ReportClampEngaged()
        {
            if (_clampLogged) return;
            _clampLogged = true;
            Debug.LogWarning("[UniverseCreator] Multi-bounce indirect clamp engaged: an authored " +
                             "albedo above 1, or a fully enclosed bright space, is being capped to " +
                             "keep the irradiance grid convergent. Check the material albedos there.");
        }

        /// <summary>Re-arm the one-shot clamp log. For a fresh session or a self-test that
        /// deliberately drives the clamp and must not leave the guard tripped.</summary>
        public static void ResetClampLog() { _clampLogged = false; }

        /// <summary>Rec. 709 luminance, the perceptual weight used for the convergence cap.</summary>
        public static float Luminance(Vector3 c)
        {
            return 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
        }

        // Below this first-bounce luminance the ratio cap is not applied: the divide would
        // be meaningless and a shaded probe's small, legitimate feedback would be crushed.
        private const float FirstBounceLuminanceFloor = 1e-4f;

        private static bool _clampLogged;
    }
}
