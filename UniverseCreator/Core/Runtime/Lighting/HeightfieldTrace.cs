// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    HeightfieldTrace.cs
//  Purpose: The one ray-march every visibility question in the lighting path asks. A ray
//           starts at a world point, steps along its direction against the terrain height,
//           and reports the first place the ground rises in front of it. The stride grows
//           with distance - fine near the origin where a near occluder decides the answer,
//           coarse far away where a whole ridge is one sample - so a fixed step count sees
//           kilometres for the cost of dozens of samples, not thousands. This single
//           function answers probe-to-surface visibility (S-73), sky visibility and the
//           bent normal (S-71), long-range contact shadow past the cascades, and the
//           off-screen half of reflections (S-78): one primitive, four features, and no
//           acceleration structure to build or keep in sync with the terrain.
//
//           Its GPU twin is HeightfieldTrace.hlsl, which steps the identical schedule from
//           the same three numbers (near stride, growth, step count) so a ray cast on the
//           CPU for a probe and the same ray cast in a screen-space pass land on the same
//           ridge - a mismatch here reads as ambient that shifts when a screen-space
//           feature is toggled, which is exactly the bug the shared schedule prevents.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Distance-stepped terrain occlusion, pure and allocation-free. <see cref="StepLength"/>
    /// and <see cref="DistanceAfter"/> expose the schedule so a caller (and the self-test) can
    /// reason about the stride at any step; <see cref="Occluded"/> marches a ray and reports
    /// the first intersection with the height field. The height sampler is a caller-supplied
    /// world-XZ lookup, so this stays decoupled from how the terrain stores its heights and
    /// runs on any thread.
    /// </summary>
    public static class HeightfieldTrace
    {
        /// <summary>The stride taken on step <paramref name="i"/> (0-based): the near stride
        /// grown geometrically, so step 0 is the finest and each is a fixed factor longer.</summary>
        public static float StepLength(int i)
        {
            return LightingConstants.HeightfieldTraceNearStepM
                 * Mathf.Pow(LightingConstants.HeightfieldTraceStepGrowth, Mathf.Max(0, i));
        }

        /// <summary>The distance from the origin reached after <paramref name="steps"/> strides:
        /// the closed-form geometric sum of <see cref="StepLength"/>, so a caller can size a
        /// trace's reach without walking the loop.</summary>
        public static float DistanceAfter(int steps)
        {
            if (steps <= 0) return 0f;
            float g = LightingConstants.HeightfieldTraceStepGrowth;
            return LightingConstants.HeightfieldTraceNearStepM * (Mathf.Pow(g, steps) - 1f) / (g - 1f);
        }

        /// <summary>
        /// The schedule packed for the GPU: x = near stride, y = growth, z = step count,
        /// w = reach. The lighting manager publishes exactly this into
        /// <c>_UCHeightfieldTraceParams</c>, and the HLSL twin steps from it, so the two paths
        /// march one schedule with no second copy of the numbers to fall out of step.
        /// </summary>
        public static Vector4 TraceParams()
        {
            int steps = LightingConstants.HeightfieldTraceSteps;
            return new Vector4(
                LightingConstants.HeightfieldTraceNearStepM,
                LightingConstants.HeightfieldTraceStepGrowth,
                steps,
                DistanceAfter(steps));
        }

        /// <summary>
        /// March a ray from <paramref name="origin"/> along <paramref name="dir"/> against the
        /// terrain height, at most <paramref name="maxDistance"/> and <paramref name="maxSteps"/>
        /// strides, and report whether the ground rises above the ray before it clears that reach.
        /// The stride grows each step; the test at each sample is "is the ray below the terrain
        /// here" and the loop early-outs on the first hit. Returns false (open sky) when the ray
        /// climbs away and nothing intercepts it. <paramref name="hitDistance"/> is the distance
        /// at the intercepting sample, or the reach when there is no hit.
        /// </summary>
        public static bool Occluded(Vector3 origin, Vector3 dir, float maxDistance, int maxSteps,
                                    Func<float, float, float> heightAt, out float hitDistance)
        {
            hitDistance = 0f;
            if (heightAt == null || maxDistance <= 0f) return false;

            // A ray with no horizontal travel samples one column forever; a straight-up ray is
            // never occluded by a height field, so treat a near-vertical ray as open sky.
            Vector3 d = dir;
            float horiz = new Vector2(d.x, d.z).magnitude;
            if (d.sqrMagnitude < 1e-12f || horiz < 1e-6f) { hitDistance = maxDistance; return false; }
            d.Normalize();

            int steps = Mathf.Min(Mathf.Max(1, maxSteps), LightingConstants.HeightfieldTraceSteps);
            float t = 0f;
            for (int i = 0; i < steps; i++)
            {
                t += StepLength(i);
                if (t > maxDistance) { t = maxDistance; }
                Vector3 p = origin + d * t;
                float ground = heightAt(p.x, p.z);
                if (p.y <= ground)
                {
                    hitDistance = t;
                    return true;   // first intersection - the ground is in front of the ray here
                }
                if (t >= maxDistance) break;   // reached the tier's cap with the ray still in the air
            }
            hitDistance = Mathf.Min(t, maxDistance);
            return false;
        }

        /// <summary>Convenience overload using the module's default step count and the reach that
        /// schedule gives, for a probe-fill trace that does not carry its own tier distance.</summary>
        public static bool Occluded(Vector3 origin, Vector3 dir, float maxDistance,
                                    Func<float, float, float> heightAt, out float hitDistance)
        {
            return Occluded(origin, dir, maxDistance, LightingConstants.HeightfieldTraceSteps, heightAt, out hitDistance);
        }
    }
}
