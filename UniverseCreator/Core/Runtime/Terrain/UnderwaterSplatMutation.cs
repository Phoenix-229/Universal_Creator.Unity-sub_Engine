// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World - Refinement #7 (W2)
//
//  File:    UnderwaterSplatMutation.cs
//  Purpose: The safe arithmetic of dressing a SUBMERGED terrain bed - the sea,
//           lake and river floor - into a terrain splat map from the derived
//           hydrology field (W1), and taking it back off again. Where a cell sits
//           under water it is painted from up to three bed materials the buyer
//           assigns - a shallow wave-washed SAND, a quiet deep SILT/mud, and a
//           scoured ROCK - blended by how deep the water stands over it and how
//           fast the water moves across it. The dry ground layers are scaled down
//           so the per-cell weights still sum to one, and the pre-bed map is kept
//           so that lifting the bed restores the author's terrain bit-for-bit.
//
//  WHY THREE MATERIALS AND NOT ONE
//  -------------------------------
//  SnowSplatMutation and ShorelineSplatMutation each drive ONE layer, because snow
//  and a wet berm are each one thing. A submerged bed is not: a real seabed grades
//  from sand in the wave-worked shallows, through silt where the water is deep and
//  still, to bare rock wherever the current is strong enough to sweep the fines
//  away (a river thalweg, a wave-cut shelf). One layer cannot say that, so this
//  mutation allocates the wet cover across three, from the two fields W1 already
//  derived per site: submersion DEPTH and flow SPEED. The split is the domain part;
//  the renormalise that follows is the same general "write N band layers, rescale
//  the rest to keep the sum at one" operation the two peers do for one layer, only
//  generalised to a set. It is a copy of a general op, not a second copy of any
//  domain rule.
//
//  WHY A CACHED BASE, NOT AN INVERSE (unchanged from the peers)
//  -----------------------------------------------------------
//  Renormalising a weight vector is one-way lossy, so lifting the bed does not
//  invert the blend; it copies the cached pre-bed map straight back, exact by
//  construction. See SnowSplatMutation for the full argument.
//
//  Pure arithmetic over plain arrays. Reading and writing the actual TerrainData,
//  sampling the HydroField for depth and flow, caching the base map, the layer
//  guard and the per-body batching all live on the driver that calls it - never
//  here, so this stays safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Tunables for how a submerged bed grades between its three materials. Metres
    /// and metres/second, so an author reasons in real units. These are physical
    /// thresholds, NOT layer indices - which terrain layer is sand, silt or rock is
    /// the buyer's to assign on the driver and is passed to <see cref="WriteBed"/>
    /// separately, so the math here owns no asset reference.
    /// </summary>
    [System.Serializable]
    public struct UnderwaterBedProfile
    {
        /// <summary>
        /// Depth over which the bed material fades in from the waterline. The bed is
        /// absent exactly at the shore (depth 0) and full below this, so it grades
        /// into the wet berm ShorelineSplatMutation lays on the dry side rather than
        /// meeting it along a hard line.
        /// </summary>
        public float edgeBandMetres;

        /// <summary>Depth at which the sand-to-silt transition begins. Above it (shallower) the calm bed is sand.</summary>
        public float shallowDepthMetres;

        /// <summary>Depth at which the sand-to-silt transition completes. Below it (deeper) the calm bed is silt.</summary>
        public float deepDepthMetres;

        /// <summary>Flow speed below which fines settle: the bed reads as silt/mud, not rock.</summary>
        public float depositSpeedMetresPerSecond;

        /// <summary>Flow speed above which the current sweeps fines away and the bed reads as scoured rock.</summary>
        public float scourSpeedMetresPerSecond;

        /// <summary>
        /// A sensible starting profile: bed fades in over the first half-metre of
        /// depth; sand through ~2 m, all silt past ~8 m in still water; the bed turns
        /// to rock as flow climbs from 0.2 to 1.0 m/s (about the span over which a
        /// stream stops depositing sand and starts moving gravel).
        /// </summary>
        public static UnderwaterBedProfile Default
        {
            get
            {
                UnderwaterBedProfile p;
                p.edgeBandMetres = 0.5f;
                p.shallowDepthMetres = 2f;
                p.deepDepthMetres = 8f;
                p.depositSpeedMetresPerSecond = 0.2f;
                p.scourSpeedMetresPerSecond = 1.0f;
                return p;
            }
        }
    }

    /// <summary>
    /// Blends a submerged bed into a terrain alphamap and restores it, keeping every
    /// cell's weights summed to one. Every method is a pure function of its
    /// arguments. See the file header for why it parallels the two peer mutations
    /// and why it drives a SET of layers rather than one.
    /// </summary>
    public static class UnderwaterSplatMutation
    {
        /// <summary>
        /// Whether a configured band layer index addresses a real layer. A disabled
        /// material carries index -1 (the buyer left that slot empty), which is a
        /// legitimate "do not paint this material" and NOT an error - the caller
        /// simply skips it, and the author's terrain shows through underwater until
        /// the slot is filled.
        /// </summary>
        public static bool IsEnabled(int layerIndex, int layerCount)
        {
            return layerIndex >= 0 && layerIndex < layerCount;
        }

        /// <summary>
        /// The three bed-material cover fractions at one submerged cell, from the two
        /// fields W1 derives: how deep the water stands over the ground here, and how
        /// fast it moves. They sum to the total wet cover (which is itself below one
        /// right at the shoreline and one below the edge band), so the remainder is
        /// left for the author's dry terrain and nothing is invented.
        ///
        /// ROCK first: wherever the flow is strong it scours the bed to rock
        /// regardless of depth, so energy claims its share off the top. What calm
        /// water is left then splits by depth - SAND in the worked shallows, SILT
        /// where it is deep and still - because that is the order a real bed sorts by
        /// grain size once the current is not moving it.
        /// </summary>
        /// <param name="depthMetres">Submersion depth (water surface above ground). Zero or less at/above the waterline.</param>
        /// <param name="flowSpeedMetresPerSecond">Water speed across the bed. Zero for a still lake or the open sea floor.</param>
        public static void BedCovers(float depthMetres, float flowSpeedMetresPerSecond,
                                     in UnderwaterBedProfile p,
                                     out float sand, out float silt, out float rock)
        {
            float wet = Smooth01(depthMetres, 0f, Mathf.Max(1e-3f, p.edgeBandMetres));
            if (wet <= 0f) { sand = 0f; silt = 0f; rock = 0f; return; }

            float energy = Smooth01(flowSpeedMetresPerSecond,
                                    p.depositSpeedMetresPerSecond,
                                    Mathf.Max(p.depositSpeedMetresPerSecond + 1e-3f, p.scourSpeedMetresPerSecond));

            rock = wet * energy;

            float calm = wet * (1f - energy);
            float deepShare = Smooth01(depthMetres,
                                       p.shallowDepthMetres,
                                       Mathf.Max(p.shallowDepthMetres + 1e-3f, p.deepDepthMetres));

            silt = calm * deepShare;
            sand = calm * (1f - deepShare);
        }

        /// <summary>
        /// Blends one cell's weights from its pre-bed base: each enabled band layer
        /// takes its cover fraction and the remaining (non-band) layers share what is
        /// left in the proportion they had, so the result still sums to one. If the
        /// band covers sum past one they are scaled to fit and nothing is left for the
        /// ground; a disabled band layer (index &lt; 0 or duplicated) is skipped and
        /// its cover simply does not paint. General over the set - the peers are the
        /// one-layer case of this.
        /// </summary>
        public static void BlendCell(float[] weights, float[] baseWeights,
                                     int[] bandLayers, float[] bandCovers)
        {
            int layers = weights.Length;

            // Raw sum of the enabled band covers, so we know whether to rescale.
            float rawSum = 0f;
            for (int b = 0; b < bandLayers.Length; b++)
            {
                int L = bandLayers[b];
                if (L < 0 || L >= layers) continue;
                rawSum += Mathf.Clamp01(bandCovers[b]);
            }

            float scale = rawSum > 1f ? 1f / rawSum : 1f;
            float bandTotal = Mathf.Min(rawSum, 1f);
            float remaining = 1f - bandTotal;

            // Zero the band layers, then accumulate (so duplicate indices add rather
            // than the last silently winning), and mark them out of the ground share.
            for (int b = 0; b < bandLayers.Length; b++)
            {
                int L = bandLayers[b];
                if (L < 0 || L >= layers) continue;
                weights[L] = 0f;
            }
            for (int b = 0; b < bandLayers.Length; b++)
            {
                int L = bandLayers[b];
                if (L < 0 || L >= layers) continue;
                weights[L] += Mathf.Clamp01(bandCovers[b]) * scale;
            }

            float othersSum = 0f;
            int othersCount = 0;
            for (int l = 0; l < layers; l++)
            {
                if (IsBand(bandLayers, l, layers)) continue;
                othersSum += Mathf.Max(0f, baseWeights[l]);
                othersCount++;
            }

            for (int l = 0; l < layers; l++)
            {
                if (IsBand(bandLayers, l, layers)) continue;
                weights[l] = othersSum > 1e-6f
                    ? Mathf.Max(0f, baseWeights[l]) / othersSum * remaining
                    : remaining / Mathf.Max(1, othersCount);
            }
        }

        /// <summary>
        /// Blends the bed across a whole alphamap block from its cached base, one
        /// cover value per material per cell. <paramref name="covers"/> is one 2D map
        /// per entry in <paramref name="bandLayers"/>, same [y,x] shape as a slice of
        /// the alphamap. A no-op when nothing is enabled, so a world whose bed slots
        /// are all empty is left exactly as the author painted it.
        /// </summary>
        public static void WriteBed(float[,,] alpha, float[,,] baseAlpha,
                                    int[] bandLayers, float[][,] covers)
        {
            int h = alpha.GetLength(0);
            int w = alpha.GetLength(1);
            int layers = alpha.GetLength(2);

            if (bandLayers == null || covers == null) return;
            if (bandLayers.Length != covers.Length) return;

            // Precompute band membership once - the inner cell loop must not allocate.
            bool[] isBand = new bool[layers];
            bool any = false;
            for (int b = 0; b < bandLayers.Length; b++)
            {
                int L = bandLayers[b];
                if (L >= 0 && L < layers) { isBand[L] = true; any = true; }
            }
            if (!any) return;

            int othersCount = 0;
            for (int l = 0; l < layers; l++) if (!isBand[l]) othersCount++;

            float[] cover = new float[bandLayers.Length];

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float rawSum = 0f;
                for (int b = 0; b < bandLayers.Length; b++)
                {
                    int L = bandLayers[b];
                    if (L < 0 || L >= layers) { cover[b] = 0f; continue; }
                    float c = covers[b] != null ? Mathf.Clamp01(covers[b][y, x]) : 0f;
                    cover[b] = c;
                    rawSum += c;
                }

                float scale = rawSum > 1f ? 1f / rawSum : 1f;
                float remaining = 1f - Mathf.Min(rawSum, 1f);

                for (int l = 0; l < layers; l++) if (isBand[l]) alpha[y, x, l] = 0f;
                for (int b = 0; b < bandLayers.Length; b++)
                {
                    int L = bandLayers[b];
                    if (L < 0 || L >= layers) continue;
                    alpha[y, x, L] += cover[b] * scale;
                }

                float othersSum = 0f;
                for (int l = 0; l < layers; l++)
                    if (!isBand[l]) othersSum += Mathf.Max(0f, baseAlpha[y, x, l]);

                for (int l = 0; l < layers; l++)
                {
                    if (isBand[l]) continue;
                    alpha[y, x, l] = othersSum > 1e-6f
                        ? Mathf.Max(0f, baseAlpha[y, x, l]) / othersSum * remaining
                        : remaining / Mathf.Max(1, othersCount);
                }
            }
        }

        /// <summary>
        /// Copies the cached pre-bed map straight back over the live one, so a lifted
        /// bed is bit-for-bit the terrain the author painted. Exact by construction
        /// because it is a copy, not an inverse of the blend.
        /// </summary>
        public static void Restore(float[,,] alpha, float[,,] baseAlpha)
        {
            if (alpha.Length != baseAlpha.Length) return;
            System.Array.Copy(baseAlpha, alpha, alpha.Length);
        }

        // ---------------------------------------------------------------------

        /// <summary>SmoothStep of v mapped across [a,b] to 0..1, hand-normalised
        /// first because Unity's Mathf.SmoothStep takes a 0..1 parameter and is NOT
        /// the HLSL smoothstep that remaps across [a,b]. Clamps a degenerate a&gt;=b.</summary>
        private static float Smooth01(float v, float a, float b)
        {
            if (b <= a) return v >= b ? 1f : 0f;
            float t = Mathf.Clamp01((v - a) / (b - a));
            return Mathf.SmoothStep(0f, 1f, t);
        }

        /// <summary>Whether layer l is one of the enabled band layers.</summary>
        private static bool IsBand(int[] bandLayers, int l, int layerCount)
        {
            for (int b = 0; b < bandLayers.Length; b++)
            {
                int L = bandLayers[b];
                if (L >= 0 && L < layerCount && L == l) return true;
            }
            return false;
        }
    }
}
