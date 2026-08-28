// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SkyVisibility.cs
//  Purpose: Stop the sky from leaking into places it cannot reach. A naive probe grid
//           hands every point the full open-sky ambient, so the floor of a gully, the
//           foot of a cliff and a pit read as bright as an open field - the single most
//           recognisable failure of dynamic GI, and the reason baked lighting still looks
//           better than the naive dynamic kind. This scans the compass around a probe with
//           the S-70 heightfield trace and, per azimuth, finds the horizon: the highest
//           elevation angle the terrain rises to along that bearing. The sky above that
//           horizon is open; below it is walled off. It returns two numbers per probe: how
//           much of the cosine-weighted sky actually reaches the point (a scalar 0..1), and
//           the bent normal - the average direction the unoccluded sky comes from - so
//           ambient is shaded along where the light is rather than straight up. A deeper
//           enclosure raises the horizon on every side, so less sky reaches the point and
//           the normal bends harder toward the opening, with no probe volume authored by
//           hand. The horizon angle is continuous, so a narrow shaft and a wide one differ
//           smoothly even when both admit only a sliver of sky near the zenith - the coarse
//           per-direction hit test could never separate those, because all the sky they let
//           in sits above the steepest sample it owns.
//
//           Pure and deterministic: the compass bearings and the march schedule are fixed,
//           so the same probe over the same terrain resolves to the same visibility on two
//           runs and two machines (C-06).
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>One probe's sky exposure: the fraction of the cosine-weighted upper hemisphere
    /// that is open, and the average unoccluded direction to shade ambient along.</summary>
    public struct SkyVisibilitySample
    {
        public float visibility01;
        public Vector3 bentNormal;
    }

    /// <summary>
    /// Cosine-weighted sky visibility and bent normal from the heightfield trace (S-71).
    /// <see cref="AzimuthDirection"/> is the fixed compass ring; <see cref="Sample"/> marches
    /// each bearing for the terrain horizon and reduces the sky above those horizons to a
    /// visibility scalar and a bent normal.
    /// </summary>
    public static class SkyVisibility
    {
        private const float TwoPi = 6.2831853071795862f;
        private const float HalfPi = 1.5707963267948966f;

        /// <summary>
        /// The <paramref name="i"/>-th of <paramref name="count"/> compass bearings as a unit
        /// horizontal (XZ) direction: an even ring around the probe with no random seed, so the
        /// azimuth sweep is deterministic and symmetric (opposite bearings cancel over open
        /// ground, leaving the bent normal at the zenith). Returns +Z for a degenerate count.
        /// </summary>
        public static Vector3 AzimuthDirection(int i, int count)
        {
            if (count <= 0) return new Vector3(0f, 0f, 1f);
            float a = TwoPi * i / count;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        /// <summary>
        /// Sample the sky reaching <paramref name="probePos"/> across <paramref name="directions"/>
        /// compass bearings, each marched against the terrain with the S-70 schedule out to
        /// <paramref name="maxDistance"/>. Along each bearing the highest terrain elevation angle is
        /// the horizon; the cosine-weighted fraction of sky above a horizon at elevation a is
        /// cos^2(a) (one at the flat horizon, zero at the zenith wall), and the visibility is the
        /// mean of that over the ring. The bent normal is the sum of each bearing's visible-arc
        /// centre, weighted by its open fraction and normalised - so it leans up and toward the
        /// side that stays open. A fully open sky returns visibility 1 and a bent normal at the
        /// zenith; a fully enclosed point returns 0 and falls back to straight up. Null sampler
        /// means open sky.
        /// </summary>
        public static SkyVisibilitySample Sample(Vector3 probePos, int directions, float maxDistance,
                                                  Func<float, float, float> heightAt)
        {
            SkyVisibilitySample result;
            int n = Mathf.Clamp(directions, 1, LightingConstants.HeightfieldTraceSteps * 4);

            // No terrain sampler, or nowhere to march: the whole sky is open.
            if (heightAt == null || maxDistance <= 0f)
            {
                result.visibility01 = 1f;
                result.bentNormal = Vector3.up;
                return result;
            }

            int steps = LightingConstants.HeightfieldTraceSteps;
            float visSum = 0f;
            Vector3 bentAccum = Vector3.zero;

            for (int i = 0; i < n; i++)
            {
                Vector3 az = AzimuthDirection(i, n);

                // March the horizontal bearing on the shared growing-stride schedule, tracking the
                // largest rise-over-run: the tangent of the terrain horizon along this azimuth. A
                // point at or below the probe never raises the horizon (it is not sky-blocking).
                float tanHorizon = 0f;
                float t = 0f;
                for (int s = 0; s < steps; s++)
                {
                    t += HeightfieldTrace.StepLength(s);
                    if (t > maxDistance) t = maxDistance;
                    float rise = heightAt(probePos.x + az.x * t, probePos.z + az.z * t) - probePos.y;
                    if (rise > 0f) tanHorizon = Mathf.Max(tanHorizon, rise / t);
                    if (t >= maxDistance) break;
                }

                float horizon = Mathf.Atan(tanHorizon);        // horizon elevation, 0..90 degrees
                float cosH = Mathf.Cos(horizon);
                float open = cosH * cosH;                       // cosine-weighted open-sky fraction
                visSum += open;

                // The visible sky along this bearing spans [horizon, zenith]; its centre elevation
                // is their midpoint. Weighting that direction by the open fraction and summing over
                // the ring builds a bent normal that leans up and toward the open side.
                float midElev = 0.5f * (horizon + HalfPi);
                Vector3 arc = az * Mathf.Cos(midElev) + Vector3.up * Mathf.Sin(midElev);
                bentAccum += arc * open;
            }

            result.visibility01 = Mathf.Clamp01(visSum / n);
            result.bentNormal = bentAccum.sqrMagnitude > 1e-10f ? bentAccum.normalized : Vector3.up;
            return result;
        }
    }
}
