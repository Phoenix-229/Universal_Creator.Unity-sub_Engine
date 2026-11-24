// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    FoliageColorBleed.cs
//  Purpose: Make the shade under a tree the right colour. A forest floor is green in
//           summer because sunlight reaches it two ways through the canopy and both carry
//           the leaf's colour: light transmitted THROUGH the leaves (a real green leaf
//           passes ~5-10% of the visible, strongly biased green while chlorophyll holds)
//           and light the canopy reflects down onto everything beneath it (~10-15%, again
//           green). Beer-Lambert on the leaf area index decides how much light the canopy
//           intercepts versus how much falls straight through the gaps unchanged - so a
//           denser canopy is darker and more saturated, and sunflecks stay white. The whole
//           effect reads the season's leaf colour and its leaf-off fraction, so the same
//           spot tints green in July, amber in October and goes bright and colourless once
//           the leaves are down - a seasonal correctness a screen-space GI cannot have,
//           because it does not know it is autumn.
//
//           Pure and deterministic: the caller supplies the LAI (§ 6), the leaf-off fraction
//           (§ 7 phenology) and the seasonal leaf colour (§ 5); this owns only the optics.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The canopy colour-bleed optics (S-72). Beer-Lambert interception on the leaf area
    /// index, split into the light that falls through gaps unchanged and the light that
    /// passes through leaves tinted; plus the canopy as a coloured secondary source. Leaf
    /// transmittance and reflectance are the season's leaf colour scaled to physical
    /// magnitudes and held energy-conserving. All static, allocation-free, thread-safe.
    /// </summary>
    public static class FoliageColorBleed
    {
        /// <summary>The leaf area actually present after phenology strips leaves: the raw LAI
        /// scaled by one minus the leaf-off fraction, so a bare winter canopy has none.</summary>
        public static float EffectiveLai(float lai, float leafOffFraction01)
        {
            return Mathf.Max(0f, lai) * (1f - Mathf.Clamp01(leafOffFraction01));
        }

        /// <summary>The unscattered gap fraction: the share of a vertical beam that finds a
        /// hole and reaches the floor unchanged, exp(-k * effective LAI). One when there are no
        /// leaves, falling toward zero under a dense canopy - the Beer-Lambert law.</summary>
        public static float GapFraction(float effectiveLai)
        {
            return Mathf.Exp(-LightingConstants.CanopyExtinctionK * Mathf.Max(0f, effectiveLai));
        }

        /// <summary>Leaf transmittance: the season's leaf colour scaled to the visible-band
        /// magnitude, so the through-leaf light is green in summer and amber in autumn.</summary>
        public static Vector3 LeafTransmittance(Vector3 seasonalLeafColor)
        {
            return ClampColor(seasonalLeafColor) * LightingConstants.LeafTransmittanceMag;
        }

        /// <summary>Leaf reflectance: the canopy as a coloured secondary source, the season's
        /// leaf colour at the reflectance magnitude.</summary>
        public static Vector3 LeafReflectance(Vector3 seasonalLeafColor)
        {
            return ClampColor(seasonalLeafColor) * LightingConstants.LeafReflectanceMag;
        }

        /// <summary>
        /// The downwelling radiance reaching the floor under the canopy: the gap light, which
        /// passes through holes unchanged and stays the colour of the sky and sun, plus the
        /// intercepted light that passed through leaves and took their tint. This is what turns
        /// the ground and a face beneath a summer canopy green and an October canopy amber; in
        /// leaf-off the interception is zero, so the floor gets the full, colourless incident
        /// light and reads brighter. Never exceeds the incident light on any channel.
        /// </summary>
        public static Vector3 FloorRadiance(Vector3 incident, float lai, float leafOffFraction01,
                                            Vector3 seasonalLeafColor)
        {
            float effLai = EffectiveLai(lai, leafOffFraction01);
            float gap = GapFraction(effLai);
            float intercepted = 1f - gap;

            Vector3 throughGap = incident * gap;                                    // white sunflecks
            Vector3 throughLeaf = Vector3.Scale(incident, LeafTransmittance(seasonalLeafColor)) * intercepted;
            return throughGap + throughLeaf;
        }

        /// <summary>
        /// The canopy as a coloured secondary source for everything beneath it: the intercepted
        /// light returned at the leaf reflectance, the season's colour. Kept separate from
        /// <see cref="FloorRadiance"/> so the two paths are not double counted - this is the
        /// bounce onto nearby trunks and understory, not the light that reached the floor.
        /// </summary>
        public static Vector3 ReflectedSource(Vector3 incident, float lai, float leafOffFraction01,
                                              Vector3 seasonalLeafColor)
        {
            float effLai = EffectiveLai(lai, leafOffFraction01);
            float intercepted = 1f - GapFraction(effLai);
            return Vector3.Scale(incident, LeafReflectance(seasonalLeafColor)) * intercepted;
        }

        // A leaf colour is a reflectance/transmittance ratio, so hold it in 0..1 per channel
        // before scaling - an over-one authored colour cannot make a leaf a source of energy.
        private static Vector3 ClampColor(Vector3 c)
        {
            return new Vector3(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z));
        }
    }
}
