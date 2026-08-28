// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    EmissiveContributors.cs
//  Purpose: Let the world's own bright things light their surroundings through the irradiance
//           grid. Lava, a campfire, a lit window, a magic effect, an author-placed point or
//           spot light - each is an emitter that should warm the ground and the nearby trunks,
//           not only the surfaces its own direct light happens to reach. Every emitter falls
//           off with the inverse square of distance, as real radiant power does. A probe cannot
//           afford to gather from every emitter in the world, so it keeps only the strongest
//           few, chosen by a screen-independent priority - radiance over squared distance - so
//           the surviving set is deterministic and identical every frame and every run. Remove
//           the campfire and its bleed is gone the next refresh, because it was never anything
//           but a live sum over the emitters that still exist.
//
//           Pure and deterministic: the caller supplies the emitter list and the per-probe cap
//           (§ tier: 8 desktop, 4 mobile); this owns only the priority, the cap and the sum.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>One emitter that can bleed onto the grid: its world position, the radiance it
    /// emits (linear colour times intensity), an optional range beyond which it does not reach,
    /// and a stable id that breaks ties so the kept set never depends on array order.</summary>
    public struct EmissiveEmitter
    {
        public Vector3 position;
        public Vector3 radiance;    // linear colour * intensity
        public float rangeM;        // <= 0 means unbounded
        public int stableId;        // deterministic tiebreaker (author/instance order)

        public EmissiveEmitter(Vector3 position, Vector3 radiance, float rangeM, int stableId)
        {
            this.position = position;
            this.radiance = radiance;
            this.rangeM = rangeM;
            this.stableId = stableId;
        }
    }

    /// <summary>
    /// Emissive and point/spot contributors to a probe (S-74). Inverse-square falloff, a hard
    /// per-probe cap, and a deterministic priority selection so the kept set is screen- and
    /// order-independent. All static, allocation-free (a fixed 16-slot stack buffer), thread-safe.
    /// </summary>
    public static class EmissiveContributors
    {
        // The cap can never exceed this; the selection buffers are sized to it on the stack.
        private const int MaxCap = 16;

        /// <summary>The priority that decides which emitters a probe keeps: emitted luminance over
        /// squared distance, the screen-independent measure of how much this emitter matters here.
        /// Zero when the probe is outside the emitter's range, so an out-of-range emitter never
        /// displaces one that actually reaches.</summary>
        public static float Priority(Vector3 probePos, in EmissiveEmitter e)
        {
            float distSq = (e.position - probePos).sqrMagnitude;
            if (e.rangeM > 0f && distSq > e.rangeM * e.rangeM) return 0f;

            float lum = Luminance(e.radiance);
            if (lum <= 0f) return 0f;

            float floorSq = LightingConstants.IrradianceEmissiveMinDistanceM
                          * LightingConstants.IrradianceEmissiveMinDistanceM;
            return lum / Mathf.Max(floorSq, distSq);
        }

        /// <summary>The radiance one emitter delivers to the probe: its emitted radiance with the
        /// inverse-square falloff, held flat inside the one-metre floor so a coincident emitter
        /// cannot divide by zero. Zero beyond the emitter's range.</summary>
        public static Vector3 Contribution(Vector3 probePos, in EmissiveEmitter e)
        {
            float distSq = (e.position - probePos).sqrMagnitude;
            if (e.rangeM > 0f && distSq > e.rangeM * e.rangeM) return Vector3.zero;

            float floorSq = LightingConstants.IrradianceEmissiveMinDistanceM
                          * LightingConstants.IrradianceEmissiveMinDistanceM;
            float falloff = 1f / Mathf.Max(floorSq, distSq);
            return e.radiance * falloff;
        }

        /// <summary>
        /// The total emissive radiance reaching a probe: the sum of the inverse-square
        /// contributions of the strongest <paramref name="cap"/> emitters by priority. Beyond the
        /// cap the world may hold any number of emitters; only the few that matter most here are
        /// summed, and the selection is deterministic (priority, then stable id) so the result is
        /// bit-identical every frame and every run.
        /// </summary>
        public static Vector3 Accumulate(Vector3 probePos, EmissiveEmitter[] emitters, int count, int cap)
        {
            if (emitters == null || cap <= 0) return Vector3.zero;
            count = Mathf.Min(count, emitters.Length);
            if (cap > MaxCap) cap = MaxCap;

            // A top-k buffer sorted descending by (priority, then lower stable id wins ties).
            System.Span<float> bestPri = stackalloc float[MaxCap];
            System.Span<int> bestId = stackalloc int[MaxCap];
            System.Span<int> bestIdx = stackalloc int[MaxCap];
            int filled = 0;

            for (int i = 0; i < count; i++)
            {
                float pri = Priority(probePos, emitters[i]);
                if (pri <= 0f) continue;
                int id = emitters[i].stableId;

                // Reject early if the buffer is full and this cannot beat the weakest kept entry.
                if (filled == cap && !Beats(pri, id, bestPri[cap - 1], bestId[cap - 1])) continue;

                // Find the insertion slot, then shift the weaker tail down one.
                int pos = filled < cap ? filled : cap - 1;
                while (pos > 0 && Beats(pri, id, bestPri[pos - 1], bestId[pos - 1]))
                {
                    bestPri[pos] = bestPri[pos - 1];
                    bestId[pos] = bestId[pos - 1];
                    bestIdx[pos] = bestIdx[pos - 1];
                    pos--;
                }
                bestPri[pos] = pri;
                bestId[pos] = id;
                bestIdx[pos] = i;
                if (filled < cap) filled++;
            }

            Vector3 sum = Vector3.zero;
            for (int k = 0; k < filled; k++)
            {
                sum += Contribution(probePos, emitters[bestIdx[k]]);
            }
            return sum;
        }

        // A beats B when it is stronger, or equally strong but authored earlier (lower id). The
        // tiebreak is what makes two runs with the same emitters keep the same set.
        private static bool Beats(float priA, int idA, float priB, int idB)
        {
            if (priA > priB) return true;
            if (priA < priB) return false;
            return idA < idB;
        }

        // Rec. 709 luminance of a linear radiance, the perceptual weight for the priority.
        private static float Luminance(Vector3 c)
        {
            return 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
        }
    }
}
