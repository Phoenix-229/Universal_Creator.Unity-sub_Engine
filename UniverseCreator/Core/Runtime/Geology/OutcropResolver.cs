// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    OutcropResolver.cs
//  Purpose: The § 9.4 answer to WHICH unit is exposed at a point. LithologyProfile
//           says what the rock is and in what order; GeologicStructure says where each
//           bed top sits; this reads both against the terrain surface and returns the
//           one bed the ground is actually made of here. That is exactly how an outcrop
//           pattern arises in the real world - the intersection of dipping, folded beds
//           with topography - so a valley cut through a tilted stack exposes older rock
//           in its floor and younger rock on its shoulders without anyone painting it.
//
//  A SOFT CONTACT, NOT A STAIR
//  ---------------------------
//  A bed boundary in the field is a contact a few metres thick, not a knife edge. So a
//  sample carries not just the exposed unit but a blend weight toward the unit above,
//  ramped across a contact band, so the downstream splat and relief cross the boundary
//  as a gradient rather than a one-texel step. Like everything in § 9.4 the answer is a
//  pure function of absolute world position, the surface height and the seed - it reads
//  no cell index - so two chunks that meet at an edge resolve the identical unit there.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Geology
{
    /// <summary>
    /// The outcrop answer at one point: which unit is exposed, how deep into it the surface
    /// has cut, and how strongly to blend toward the unit above so the contact reads soft.
    /// A value type - it is produced per sample and never stored, so it allocates nothing.
    /// </summary>
    public struct LithoSample
    {
        /// <summary>Index of the exposed unit in the oldest-first column; -1 for an empty profile.</summary>
        public int unitIndex;

        /// <summary>The exposed unit itself, or null for an empty profile. Convenience for callers.</summary>
        public LithoUnit unit;

        /// <summary>Metres the surface sits below the exposed unit's top - 0 at the top, growing downward.</summary>
        public float depthIntoUnitM;

        /// <summary>
        /// 0..1 weight toward the unit ABOVE the exposed one, ramped across the contact band near
        /// the top of the exposed unit. 0 well inside the unit, approaching 1 at the upper contact.
        /// Always 0 when the youngest unit is exposed (there is nothing above it).
        /// </summary>
        public float blendWeightToAbove;

        /// <summary>Index of the unit blended toward, or -1 when the exposed unit is the youngest.</summary>
        public int aboveIndex;

        /// <summary>The empty answer for a profile with no beds.</summary>
        public static LithoSample None =>
            new LithoSample { unitIndex = -1, unit = null, depthIntoUnitM = 0f,
                              blendWeightToAbove = 0f, aboveIndex = -1 };
    }

    /// <summary>
    /// Resolves the exposed unit at a point by intersecting the structural bed tops with the
    /// terrain surface. Pure and static: it reads only the profile, the structure, the world
    /// position, the surface height and the seed, so its answer is identical wherever the same
    /// point is sampled - which is what keeps an outcrop coherent across a shared cell edge.
    /// </summary>
    public static class OutcropResolver
    {
        /// <summary>
        /// Which unit outcrops at (x,z) given the terrain surface height there. Walks the column
        /// from the youngest bed down and takes the unit whose elevation band contains the surface;
        /// a surface at or above the whole section exposes the youngest bed, and a surface below the
        /// entire column exposes the basement. <paramref name="contactBlendM"/> is the thickness of
        /// the soft contact band at the top of a unit.
        /// </summary>
        public static LithoSample Resolve(LithologyProfile profile, float x, float z,
                                          float surfaceY, int seed, float contactBlendM)
        {
            if (profile == null || profile.UnitCount == 0) return LithoSample.None;

            GeologicStructure s = profile.structure;
            int n = profile.UnitCount;
            int top = n - 1;

            // Top of the whole section: at or above it, the youngest bed is what shows.
            float topYoungest = s.BedTopElevationM(profile, top, x, z, seed);
            int exposed;
            if (surfaceY >= topYoungest)
            {
                exposed = top;
            }
            else
            {
                // Walk down: the exposed unit is the first whose BASE (its lower neighbour's top)
                // sits at or below the surface, i.e. the band [base, top] straddles surfaceY.
                exposed = 0; // basement fallback: below the whole column
                for (int i = top; i >= 1; i--)
                {
                    float baseI = s.BedTopElevationM(profile, i - 1, x, z, seed); // base of i = top of i-1
                    if (surfaceY >= baseI) { exposed = i; break; }
                }
            }

            float topExposed = s.BedTopElevationM(profile, exposed, x, z, seed);
            float depth = Mathf.Max(0f, topExposed - surfaceY);

            // Blend toward the unit above across the contact band near this unit's top.
            float blend = 0f;
            int aboveIndex = -1;
            if (exposed < top)
            {
                aboveIndex = exposed + 1;
                float band = Mathf.Max(0.001f, contactBlendM);
                blend = Mathf.Clamp01((surfaceY - (topExposed - band)) / band);
            }

            return new LithoSample
            {
                unitIndex          = exposed,
                unit               = profile.UnitAt(exposed),
                depthIntoUnitM     = depth,
                blendWeightToAbove = blend,
                aboveIndex         = aboveIndex
            };
        }
    }
}
