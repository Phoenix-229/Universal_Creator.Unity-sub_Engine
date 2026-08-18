// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    ShorelineConformer.cs
//  Purpose: The ONE place that decides how a water surface meets the ground it
//           sits in, and how far below that ground it is allowed to reach. Every
//           water body goes through it - ocean, lake, pool, river, and whether it
//           was hand-placed, spawned by the hydrology math, or built by the node
//           graph.
//
//  WHY IT IS A SHARED HELPER AND NOT A GRAPH NODE
//  ----------------------------------------------
//  A node can only act on bodies whose author remembered to wire it, and the bug
//  it exists to prevent - a water plane floating over its own bank - is not
//  optional. So it is called directly from the two places that build water
//  geometry (UCWaterBody's level-body grid and its river ribbon), which between
//  them cover every creation path. There is deliberately no second copy of this
//  arithmetic anywhere: if a body has a shoreline, it came through here.
//
//  THE TWO THINGS IT DOES
//  ----------------------
//  1. THE SKIRT. The outermost wet ring of a water mesh is dropped a little BELOW
//     the ground it meets, then blended back to the true surface over the first
//     metre of depth. That overlap is what survives float drift, terrain LOD
//     swaps and height noise - all three of which move the ground by centimetres
//     at the exact place a hard edge would show as a gap you can see under.
//
//     It is a few tens of centimetres, not a volume. The visible softening of the
//     waterline is the shader's job (depth fade against the scene depth texture,
//     driven by _DepthFadeMetres / _EdgeSoftenMetres / _FoamEdgeWidth, set from
//     the WaterProduct by UCWaterMaterials.ApplyProductRuntime). Geometry closes
//     the gap; the shader makes the closure invisible. Neither alone is enough.
//
//  2. THE DEPTH CAP. Depth is clamped to an EXPLICIT ceiling before any geometry
//     is built from it. An uncapped depth is how water ends up as a solid slab
//     reaching hundreds of metres down to whatever the deepest sample happened to
//     be: invisible, and paid for in fill rate and vertex bandwidth on every
//     frame. The target device is a 6 GB phone, so this is a budget, not a
//     tidiness preference.
//
//  Managed C# over float[]. No Unity API beyond Mathf; safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Seats every water body's shore edge into the ground and bounds how deep its
    /// volume may reach. See the file header for why this is a shared helper rather
    /// than a node.
    /// </summary>
    public static class ShorelineConformer
    {
        /// <summary>
        /// How far the dry shore edge is pushed below the ground, in metres.
        ///
        /// Chosen to be larger than anything that can move the ground under a
        /// finished mesh - LOD height quantisation, heightmap round-off, the
        /// centimetre-scale noise a detail pass leaves - and small enough that the
        /// buried lip is never thick enough to read as a step where the water is
        /// genuinely shallow.
        /// </summary>
        public const float SkirtDropMetres = 0.5f;

        /// <summary>
        /// The EXPLICIT depth cap of spec 4.3: no water volume extends further than
        /// this below its own surface, however deep the bed sampled underneath it.
        ///
        /// 100 m is past the point where anything is legible through water - the
        /// deep colour has fully absorbed by a few tens of metres - so nothing below
        /// it could be seen even if it were built. Everything deeper is
        /// consequently cost with no image attached, which is exactly the case the
        /// mobile budget cannot afford.
        /// </summary>
        public const float MaxSkirtDepthMetres = 100f;

        /// <summary>
        /// Where a shore vertex's surface belongs: below the ground at the
        /// waterline, easing back to the true water level over the first metre of
        /// depth so there is no step where the skirt ends.
        /// </summary>
        /// <param name="surfaceY">The body's water level at this vertex, in world Y.</param>
        /// <param name="depth">Water depth here, in metres. Zero or less is dry.</param>
        /// <param name="maxDrop">How far the dry edge is buried, in metres.</param>
        public static float CalculateSkirtHeight(float surfaceY, float depth,
                                                 float maxDrop = SkirtDropMetres)
        {
            if (depth <= 0f) return surfaceY - maxDrop;
            if (depth > 1f) return surfaceY;

            // Linear over the first metre rather than smoothstepped: the shader's
            // depth fade is already easing the same band, and easing twice makes the
            // waterline read as two overlapping edges instead of one.
            return surfaceY - Mathf.Lerp(maxDrop, 0f, depth);
        }

        /// <summary>Clamps a sampled depth to the explicit cap. See MaxSkirtDepthMetres.</summary>
        public static float CapDepth(float rawDepth, float maxDepth = MaxSkirtDepthMetres)
        {
            return Mathf.Min(rawDepth, maxDepth);
        }

        /// <summary>
        /// Conforms a whole grid of surface heights and depths in place - the level
        /// bodies' entry point. Rivers call the two methods above per vertex instead,
        /// because a ribbon's surface follows its own gradient and is not one level.
        /// </summary>
        public static void ConformGrid(float[] surf, float[] depth, int length,
                                       float maxDrop = SkirtDropMetres,
                                       float maxDepth = MaxSkirtDepthMetres)
        {
            for (int i = 0; i < length; i++)
            {
                surf[i] = CalculateSkirtHeight(surf[i], depth[i], maxDrop);
                depth[i] = CapDepth(depth[i], maxDepth);
            }
        }
    }
}
