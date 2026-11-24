// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    TerrainAlbedo.cs
//  Purpose: The colour the ground bounces, resolved from what is actually on it rather
//           than a single constant. The base is the splat weights times each terrain
//           layer's own average colour, so sand bounces warm, grass green and rock grey;
//           then the two things the world stacks on top of the ground override it in
//           order - snow covers the ground and lifts its albedo enormously, and a canopy's
//           leaves sit above the snow and pull the bounce dark and green. This closes the
//           loop between what grows or falls and how bright the shade beneath it reads.
//           Pure: it takes the weights, the layer colours, a snow fraction and a canopy
//           cover and returns one albedo, so the probe filler and the grid can call it
//           off the main thread.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Resolves the ground's bounce albedo at a point from the splat map and the two
    /// conditions layered above it. <see cref="FromSplat"/> is the terrain's own colour;
    /// <see cref="WithSnow"/> and <see cref="WithCanopy"/> apply the overrides in world
    /// order (snow on the ground, canopy above the snow); <see cref="Resolve"/> chains all
    /// three. The layer colours belong to the caller - the terrain system owns the splat
    /// layers, so passing them in keeps this decoupled from it.
    /// </summary>
    public static class TerrainAlbedo
    {
        /// <summary>
        /// The base albedo: each layer's average colour weighted by its splat weight,
        /// normalised by the total weight so it is a true convex blend even when the
        /// weights do not sum to one. Returns mid-grey if there is nothing to blend.
        /// </summary>
        public static Vector3 FromSplat(float[] weights, Vector3[] layerColors)
        {
            if (weights == null || layerColors == null) return new Vector3(0.3f, 0.3f, 0.3f);
            int n = Mathf.Min(weights.Length, layerColors.Length);
            Vector3 sum = Vector3.zero;
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                float w = Mathf.Max(0f, weights[i]);
                sum += layerColors[i] * w;
                total += w;
            }
            if (total <= 1e-6f) return new Vector3(0.3f, 0.3f, 0.3f);
            return sum / total;
        }

        /// <summary>
        /// Snow over the ground: the albedo moves toward fresh snow's own high, faintly
        /// blue value by the snow fraction. A full cover is almost entirely snow, which is
        /// why a snowfall lifts the ambient so much more than a colour tint would.
        /// </summary>
        public static Vector3 WithSnow(Vector3 baseAlbedo, float snow01)
        {
            return Vector3.Lerp(baseAlbedo, LightingConstants.SnowAlbedoLinear, Mathf.Clamp01(snow01));
        }

        /// <summary>
        /// Canopy above the ground: the leaves the sky sees pull the bounce toward a dark
        /// green by the cover, capped by the influence so even a closed wood keeps a little
        /// of the ground under it. This is what darkens and greens the shade of a forest.
        /// </summary>
        public static Vector3 WithCanopy(Vector3 baseAlbedo, float canopyCover01)
        {
            float t = Mathf.Clamp01(canopyCover01) * LightingConstants.CanopyAlbedoInfluence01;
            return Vector3.Lerp(baseAlbedo, LightingConstants.CanopyBounceAlbedoLinear, t);
        }

        /// <summary>
        /// The full ground albedo: the splat base, then snow, then canopy - applied in the
        /// order the world layers them, so a snowy wood is green leaves over white ground.
        /// The result is held below one (S-69) so a white-authored terrain layer cannot make
        /// the bounce gain energy; every natural surface here is already under that ceiling.
        /// </summary>
        public static Vector3 Resolve(float[] weights, Vector3[] layerColors, float snow01, float canopyCover01)
        {
            Vector3 a = FromSplat(weights, layerColors);
            a = WithSnow(a, snow01);
            a = WithCanopy(a, canopyCover01);
            return EnergyConservation.ClampAlbedo(a);
        }
    }
}
