// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    TerrainShapeGuard.cs
//  Purpose: DEF-2. The terrain's own limit on how much ground an external system
//           may ask it to flatten.
//
//  WHY THE TERRAIN NEEDS A GUARD AT ALL
//  ------------------------------------
//  A road legitimately owns the ground it sits on: the carriageway, its shoulders,
//  and a short pull of dirt past the shoulder so the cut does not end in a step.
//  That is metres. What it must never own is the landscape.
//
//  The author hit the failure that makes this necessary. Road & City Master ships
//  the upstream default `matchHeightsDistance = 50f` ("the width of height
//  modification in meters", Road.cs:189), and its terraform buffer is sized from
//  `heightDistance * 1.65f` (Terraforming.cs:241) - so a single road reaches
//  roughly 165 m of terrain. On a large open landscape that is invisible. On a
//  4 km world with an island in it, one road flattens the world, and the author's
//  words were "it is destroying our work".
//
//  The road's number is the road's business, and it lives in a different product.
//  What UC can do - and the author asked for exactly this, "add a guard to the
//  terrain to prevent that since they have a contract" - is refuse to carry out an
//  unreasonable request. A contract with no limits is not a contract.
//
//  IT CLAMPS AND REPORTS; IT DOES NOT DROP
//  ---------------------------------------
//  A dropped request would make a road float over unshaped ground, which is a
//  worse and much more confusing failure than a narrower cut. So an over-wide
//  corridor is narrowed to the limit and the author is told which source asked for
//  what, by name. Silence here would turn a loud bug into a quiet one.
//
//  WHAT IT CANNOT DO
//  -----------------
//  This guards the REQUEST path - a plugin asking UC to shape ground through
//  TerrainShapeRequest. It cannot guard a system that writes TerrainData directly
//  behind UC's back, because there is no call to intercept. That case is covered
//  from the other side: DEF-1 stops a direct write being captured as an author
//  sculpt, so a regeneration restores the graph's own ground. Both halves are
//  needed and neither replaces the other.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Integration;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Clamps an external terrain-shaping request to limits the world is willing to
    /// honour. Pure and allocation-free apart from the report string, and safe to
    /// call on the main thread once per bake.
    /// </summary>
    public static class TerrainShapeGuard
    {
        /// <summary>
        /// Widest half-width of FLATTENED ground a corridor may claim, in metres.
        /// 24 m each side covers a six-lane carriageway with shoulders; anything
        /// past it is not a road surface, it is landscaping.
        /// </summary>
        public const float DefaultMaxFlattenHalfWidthM = 24f;

        /// <summary>
        /// How far past the flattened edge the blend-out may reach, in metres. This
        /// is the "pull of dirt past the shoulder" that stops a cut ending in a
        /// step. The author put the real figure at about 2 m; 10 m is deliberately
        /// generous so a legitimately soft verge is never clamped, while still being
        /// an order of magnitude below the 165 m that caused the damage.
        /// </summary>
        public const float DefaultMaxBlendBeyondFlattenM = 10f;

        /// <summary>
        /// Narrows every corridor sample in <paramref name="request"/> to the given
        /// limits. Returns how many samples were clamped, and writes a one-line
        /// report naming the source and the worst figure it asked for - null when
        /// nothing needed clamping, so a well-behaved plugin produces no log at all.
        /// </summary>
        public static int ClampCorridor(TerrainShapeRequest request,
                                        float maxFlattenHalfWidthM,
                                        float maxBlendBeyondFlattenM,
                                        out string report)
        {
            report = null;

            if (request == null || request.corridor == null) return 0;

            // A zero or negative limit would flatten nothing and strand every road on
            // unshaped ground, so the limits themselves are floored. A guard that can
            // be configured into breaking the thing it protects is not a guard.
            float maxFlatten = Mathf.Max(0.5f, maxFlattenHalfWidthM);
            float maxBlend = Mathf.Max(0f, maxBlendBeyondFlattenM);

            int clamped = 0;
            float worstFlatten = 0f;
            float worstOuter = 0f;

            for (int i = 0; i < request.corridor.Length; i++)
            {
                TerrainCorridorSample s = request.corridor[i];

                float flatten = s.flattenedHalfWidthMetres;
                float outer = s.blendOuterHalfWidthMetres;

                if (flatten > worstFlatten) worstFlatten = flatten;
                if (outer > worstOuter) worstOuter = outer;

                bool touched = false;

                if (flatten > maxFlatten)
                {
                    flatten = maxFlatten;
                    touched = true;
                }

                // The blend is measured from the ROAD CENTRE, like the flattened
                // half-width it sits outside, so its ceiling moves with the clamped
                // flatten rather than being an independent number. Clamping them
                // separately could leave a blend inside its own flattened band, which
                // inverts the profile and cuts a step where the blend should smooth.
                float maxOuter = flatten + maxBlend;
                if (outer > maxOuter)
                {
                    outer = maxOuter;
                    touched = true;
                }

                // An outer band narrower than the flattened one is malformed whatever
                // the limits are: there is no room left to blend in. Pulled out to the
                // flattened edge so the profile stays monotonic.
                if (outer < flatten)
                {
                    outer = flatten;
                    touched = true;
                }

                if (!touched) continue;

                s.flattenedHalfWidthMetres = flatten;
                s.blendOuterHalfWidthMetres = outer;
                request.corridor[i] = s;        // struct: write it back or nothing changed
                clamped++;
            }

            if (clamped == 0) return 0;

            string who = !string.IsNullOrEmpty(request.sourceLabel)
                       ? request.sourceLabel
                       : (!string.IsNullOrEmpty(request.sourceId) ? request.sourceId : "an unnamed plugin");

            report =
                "Universe Creator: narrowed " + clamped + " of " + request.corridor.Length +
                " corridor samples from \"" + who + "\". It asked to flatten up to " +
                worstFlatten.ToString("0.#") + " m each side and blend out to " +
                worstOuter.ToString("0.#") + " m; this world allows " +
                maxFlatten.ToString("0.#") + " m flattened plus " + maxBlend.ToString("0.#") +
                " m of blend. A road owns the ground it sits on, not the landscape - raise " +
                "the limits on the Universe Creator world if you really want a wider cut.";

            return clamped;
        }
    }
}
