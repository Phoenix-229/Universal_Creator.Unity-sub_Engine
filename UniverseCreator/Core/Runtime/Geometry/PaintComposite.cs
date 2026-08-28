// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PaintComposite.cs
//  Purpose: The composite order of a chunk build (§ 9.8 G-50): the § 9.7 COMPUTED weights
//           first, then the author's strokes for that tile applied OVER them in recorded
//           order. An author who paints nothing gets a fully varied, site-appropriate world
//           (the computed base shows through untouched); an author who paints gets their
//           intent laid on top of it. Authored data is still data: the composite is a
//           deterministic function of the base weights and the ordered stroke list, so
//           rebuilding a chunk twice with the same list yields bit-identical splat weights
//           (C-04).
//
//  A PARTITION-PRESERVING PAINT BLEND
//  ----------------------------------
//  Each stroke raises its target layer toward full coverage by its local weight and scales
//  the other layers down by the complement:
//
//       target' = target + w·(1 − target)      others' = other·(1 − w)
//
//  which keeps the weights summing to one at every step - no separate renormalise, no drift
//  - so the output is always a valid splat partition however many strokes overlap. Order
//  matters (paint A then B is not B then A), which is exactly why the stroke list is applied
//  in RECORDED order and the determinism is pinned against that order.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Weight arrays in, a weight array out; no Terrain, no texture. The self-test pins the
//  partition of unity through overlapping strokes, the paint-over-computed layering, the
//  order sensitivity, and the twice-rebuilt bit-identical result. Writing the weights into
//  the alphamap/splat cache is the scene layer above.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>One evaluated paint contribution at a texel: which layer the stroke targets and its local weight
    /// (already the product of stroke strength and world-space falloff from <see cref="PaintStrokeMath"/>).</summary>
    public readonly struct PaintOp
    {
        public readonly int TargetLayer;
        public readonly float Weight;
        public PaintOp(int targetLayer, float weight) { TargetLayer = targetLayer; Weight = weight; }
    }

    /// <summary>
    /// Pure composite of a texel's splat weights: the § 9.7 computed base with paint strokes applied over it in
    /// recorded order, each blend preserving the partition of unity. Deterministic given the base and the op list
    /// (C-04). Device-free.
    /// </summary>
    public static class PaintComposite
    {
        /// <summary>
        /// Apply one paint op in place: raise the target layer toward full coverage by <paramref name="strokeWeight01"/>
        /// and scale the others by its complement, so the weights still sum to one. Clamps the weight to [0,1];
        /// no-ops on a bad array or out-of-range target.
        /// </summary>
        public static void ApplyStroke(float[] weights, int targetLayer, float strokeWeight01)
        {
            if (weights == null || targetLayer < 0 || targetLayer >= weights.Length) return;
            float w = strokeWeight01 < 0f ? 0f : (strokeWeight01 > 1f ? 1f : strokeWeight01);
            if (w <= 0f) return;

            for (int i = 0; i < weights.Length; i++)
                weights[i] = (i == targetLayer) ? weights[i] + w * (1f - weights[i]) : weights[i] * (1f - w);
        }

        /// <summary>
        /// Composite a texel: copy the computed <paramref name="baseWeights"/> into <paramref name="into"/>, then
        /// apply each op in recorded order. With no ops the result equals the computed base (a varied unpainted
        /// world). No-ops on mismatched arrays.
        /// </summary>
        public static void Composite(float[] baseWeights, IReadOnlyList<PaintOp> ops, float[] into)
        {
            if (baseWeights == null || into == null || into.Length < baseWeights.Length) return;
            for (int i = 0; i < baseWeights.Length; i++) into[i] = baseWeights[i];
            if (ops == null) return;
            for (int k = 0; k < ops.Count; k++) ApplyStroke(into, ops[k].TargetLayer, ops[k].Weight);
        }
    }
}
