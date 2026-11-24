// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ProbeValidity.cs
//  Purpose: Stop the light leak. An irradiance probe buried in rock must not light a point
//           standing in the open, and a probe out in the open must not light the inside of an
//           overhang or the far side of a cliff. Most engines fake this with depth-based
//           validity heuristics and probe-volume authoring; because we own the terrain height
//           field (S-70) we can test it exactly - trace the straight segment from the shading
//           point to each contributing probe against the height field, drop any probe whose
//           ray is blocked by terrain, and renormalise the surviving weights so the point keeps
//           its full ambient energy instead of going dark just because a cliff took half its
//           probes. This is the leak fix, and owning the height field is why it is affordable.
//
//           Pure and deterministic: the caller supplies the probe positions, the per-probe
//           weights and the height sampler; this owns only the visibility test and the
//           weight bookkeeping.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Exact probe validity against the terrain height field (S-73). Traces the segment from a
    /// shading point to a probe with the shared height-field primitive (S-70) and drops probes
    /// the terrain hides, then renormalises the survivors to preserve total ambient energy.
    /// All static, allocation-free, thread-safe.
    /// </summary>
    public static class ProbeValidity
    {
        /// <summary>True when the straight line from the shading point to the probe is clear of
        /// terrain - the probe can legitimately light this point. False when the height field
        /// rises across the segment (the probe is behind a cliff, in the rock, or over an
        /// overhang lip), so the probe must be dropped. A probe at or below the shading point's
        /// own position is treated as clear: the segment has no length to occlude.</summary>
        public static bool IsVisible(Vector3 shadingPoint, Vector3 probePos, Func<float, float, float> heightAt)
        {
            if (heightAt == null) return true;

            Vector3 toProbe = probePos - shadingPoint;
            float dist = toProbe.magnitude;
            if (dist <= 1e-4f) return true;           // coincident: nothing between them to block

            Vector3 dir = toProbe / dist;
            // Trace up to just short of the probe so the probe's own cell cannot self-occlude.
            float reach = dist - 1e-3f;
            if (reach <= 0f) return true;

            return !HeightfieldTrace.Occluded(shadingPoint, dir, reach, heightAt, out _);
        }

        /// <summary>
        /// Drop the blocked probes and rescale the survivors so their weights sum to the same
        /// total they had before - the point keeps its full ambient energy, it just gathers it
        /// from the probes that can actually see it. Writes the validity result into
        /// <paramref name="valid"/> and the rescaled weights back into <paramref name="weights"/>,
        /// both in place. Returns the number of probes that survived. If every probe is blocked
        /// the weights are left at zero and the caller falls back (sky, or the nearest probe).
        /// </summary>
        public static int FilterAndRenormalize(Vector3 shadingPoint, Vector3[] probePositions,
                                               float[] weights, bool[] valid, int count,
                                               Func<float, float, float> heightAt)
        {
            if (probePositions == null || weights == null || valid == null) return 0;
            count = Mathf.Min(count, Mathf.Min(probePositions.Length, Mathf.Min(weights.Length, valid.Length)));

            float originalSum = 0f;
            float retainedSum = 0f;
            int survivors = 0;

            for (int i = 0; i < count; i++)
            {
                float w = Mathf.Max(0f, weights[i]);
                originalSum += w;

                bool visible = IsVisible(shadingPoint, probePositions[i], heightAt);
                valid[i] = visible;
                if (visible)
                {
                    retainedSum += w;
                    survivors++;
                }
            }

            if (retainedSum <= 1e-8f)
            {
                // Every contributor is blocked: zero them all and let the caller fall back.
                for (int i = 0; i < count; i++) weights[i] = 0f;
                return 0;
            }

            float rescale = originalSum / retainedSum;      // preserve the point's total ambient energy
            for (int i = 0; i < count; i++)
            {
                weights[i] = valid[i] ? Mathf.Max(0f, weights[i]) * rescale : 0f;
            }
            return survivors;
        }
    }
}
