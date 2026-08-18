// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    WaterBedMath.cs
//  Purpose: The real-world shape of the ground under water. Stateless functions,
//           pure managed C#, no allocation per sample.
//
//  WHY A RIVER, A LAKE AND AN OCEAN ARE NOT THE SAME CURVE
//  ------------------------------------------------------
//  They are cut by different processes, so they have different cross-sections and
//  a single "depth" number cannot describe any of them:
//
//    RIVER   - cut by its own flow. The channel is close to a parabola, because
//              the water erodes fastest where it moves fastest, which is midstream.
//              A river is measured by WIDTH and DEPTH, and the ratio of the two is
//              what makes it read as a mountain stream or a lowland river.
//
//    LAKE    - a filled basin. The floor is a shallow dish with a distinct SHELF
//              near the shore where wave action planes the bed flat, then a steeper
//              drop to the deep. That shelf is why you can wade out of a lake for
//              some distance and then suddenly cannot.
//
//    OCEAN   - the same shelf, much longer and much flatter. A real continental
//              shelf runs at roughly 0.1 degrees for tens of kilometres, breaks at
//              the shelf edge, then falls away on the continental slope at 3-6
//              degrees. Compressed to a game world, that is a long flat wade, a
//              defined break, and then deep water.
//
//    POOL    - built, not eroded. Flat floor, straight walls. Whatever shape the
//              designer says, with no natural law to obey.
//
//  All four are expressed as one function: given the normalised distance from the
//  shore (0 at the waterline, 1 at the deepest point), return the fraction of full
//  depth. That keeps the carve code shape-agnostic.
//
//  RUGGEDNESS
//  ----------
//  A real bed is not smooth. Ruggedness is value noise summed over octaves, keyed
//  on absolute world position so it is identical from either side of a chunk seam
//  and identical on every rebuild. It is scaled DOWN toward the shoreline, because
//  a boulder field that breaks the waterline turns a lake edge into a dashed line
//  of islands.
//
//  NO DOTS, NO BURST, NO NATIVEARRAY. Flat managed math.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// How the bed falls away from the shoreline. Each is a different real process.
    /// </summary>
    public enum BedProfile
    {
        /// <summary>Parabolic channel cut by flow. Rivers and streams.</summary>
        RiverChannel = 0,

        /// <summary>Wave-cut shelf then a dish. Lakes.</summary>
        LakeBasin = 1,

        /// <summary>Long flat shelf, a break, then the slope. Oceans and seas.</summary>
        ContinentalShelf = 2,

        /// <summary>Flat floor, steep walls. Built pools.</summary>
        FlatFloor = 3,

        /// <summary>Straight linear ramp. Predictable, for when the others fight
        /// the look the designer wants.</summary>
        Linear = 4
    }

    /// <summary>
    /// Stateless bed-shape and roughness math. Every function is a pure function of
    /// its arguments, so two chunks sampling the same world position get the same
    /// answer regardless of build order.
    /// </summary>
    public static class WaterBedMath
    {
        // =====================================================================
        //  CROSS-SECTION
        // =====================================================================

        /// <summary>
        /// Fraction of full depth at a normalised distance from the shore.
        /// t = 0 is the waterline, t = 1 is the deepest point. Returns 0..1.
        ///
        /// shelfFraction is how much of the run from shore to deep is taken up by
        /// the flat shelf, and only the shelf profiles read it.
        /// </summary>
        public static float DepthFraction(BedProfile profile, float t, float shelfFraction, float bankExponent)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;

            switch (profile)
            {
                case BedProfile.RiverChannel:
                    {
                        // A flow-cut channel is close to parabolic. Expressed from the
                        // bank inward: at the bank (t=0) depth is 0, at midstream (t=1)
                        // it is full. Exponent 2 is the natural parabola, higher is a
                        // flat-bedded channel with steep banks, 1 is a shallow V.
                        float e = Mathf.Clamp(bankExponent, 1f, 6f);
                        return 1f - Mathf.Pow(1f - t, e);
                    }

                case BedProfile.LakeBasin:
                    {
                        // Wave-planed shelf near the shore, then a dish. The shelf is
                        // not perfectly flat - it dips gently - or the transition to
                        // the dish reads as a hard step on the bed.
                        float s = Mathf.Clamp(shelfFraction, 0.02f, 0.9f);

                        if (t < s)
                        {
                            // Shallow, gently deepening wade. Reaches ~18% of full
                            // depth by the shelf break, which is about right for a
                            // natural lake margin.
                            float k = t / s;
                            return k * k * 0.18f;
                        }

                        // Past the break, smoothstep into the deep. Smoothstep rather
                        // than linear so the bed has no crease at either end.
                        float u = (t - s) / (1f - s);
                        return 0.18f + Mathf.SmoothStep(0f, 1f, u) * 0.82f;
                    }

                case BedProfile.ContinentalShelf:
                    {
                        // The real thing: a very long, very flat shelf, a defined
                        // break, then the continental slope. The shelf only reaches a
                        // few percent of full ocean depth however far it runs - that
                        // disproportion IS the shape.
                        float s = Mathf.Clamp(shelfFraction, 0.05f, 0.95f);

                        if (t < s)
                        {
                            // Near-linear, very shallow gradient. Slight curve so it
                            // is not a perfect plane.
                            float k = t / s;
                            return Mathf.Pow(k, 1.3f) * 0.08f;
                        }

                        // The slope. Steep at the break, easing into the abyssal
                        // plain, which is why this is a power curve and not a line.
                        float u = (t - s) / (1f - s);
                        return 0.08f + Mathf.Pow(u, 0.65f) * 0.92f;
                    }

                case BedProfile.FlatFloor:
                    {
                        // Built. Walls rise over a short fraction of the run and the
                        // rest is a flat floor at full depth.
                        const float wall = 0.12f;
                        if (t >= wall) return 1f;
                        return Mathf.SmoothStep(0f, 1f, t / wall);
                    }

                default:
                    return t;
            }
        }

        /// <summary>
        /// The natural profile for a body kind. Used so a newly created body starts
        /// physically plausible rather than flat, and so the inspector can say what
        /// "default" means.
        /// </summary>
        public static BedProfile DefaultProfileFor(Nodes.WaterBodyKind kind)
        {
            switch (kind)
            {
                case Nodes.WaterBodyKind.Ocean: return BedProfile.ContinentalShelf;
                case Nodes.WaterBodyKind.River: return BedProfile.RiverChannel;
                case Nodes.WaterBodyKind.Pool:  return BedProfile.FlatFloor;
                default:                        return BedProfile.LakeBasin;
            }
        }

        // =====================================================================
        //  RIVER HYDRAULICS
        // =====================================================================

        /// <summary>
        /// Manning's equation. Mean velocity in m/s for a channel of hydraulic
        /// radius R (m) on slope S (m/m) with roughness n.
        ///
        ///     v = (1/n) * R^(2/3) * S^(1/2)
        ///
        /// This is the standard open-channel formula, and it is why a steep rocky
        /// stream and a wide slow river with the same depth move at different speeds.
        /// </summary>
        public static float ManningVelocity(float hydraulicRadius, float slope, float roughness)
        {
            if (hydraulicRadius <= 0f || slope <= 0f) return 0f;

            float n = Mathf.Max(0.005f, roughness);
            return (1f / n) * Mathf.Pow(hydraulicRadius, 0.6666667f) * Mathf.Sqrt(slope);
        }

        /// <summary>
        /// Hydraulic radius of a parabolic channel: cross-sectional area over wetted
        /// perimeter. For a wide shallow channel this tends toward the mean depth,
        /// which is why using raw depth is a decent approximation for big rivers and
        /// a bad one for narrow ones.
        /// </summary>
        public static float HydraulicRadius(float widthMetres, float depthMetres)
        {
            float w = Mathf.Max(0.05f, widthMetres);
            float d = Mathf.Max(0.01f, depthMetres);

            // Parabolic channel: A = (2/3)wd. Wetted perimeter approximated by the
            // standard series form, accurate to well under a percent for w/d > 1.
            float area = 0.6666667f * w * d;
            float x = (4f * d) / w;
            float perimeter = w * (1f + 0.6666667f * x * x - 0.2f * x * x * x * x);

            return area / Mathf.Max(0.01f, perimeter);
        }

        /// <summary>
        /// Froude number. Below 1 the flow is tranquil, above 1 it is shooting and
        /// stands up into a hydraulic jump - which is where white water comes from.
        /// </summary>
        public static float Froude(float velocity, float depthMetres)
        {
            float d = Mathf.Max(0.05f, depthMetres);
            return velocity / Mathf.Sqrt(9.81f * d);
        }

        /// <summary>
        /// Regime width for a given discharge, from the Lacey/regime relation
        /// W ~ 4.8 * sqrt(Q). A designer who knows the river should carry a certain
        /// flow gets a plausible width instead of guessing.
        /// </summary>
        public static float RegimeWidth(float dischargeCumecs)
        {
            return 4.8f * Mathf.Sqrt(Mathf.Max(0.01f, dischargeCumecs));
        }

        // =====================================================================
        //  RUGGEDNESS
        // =====================================================================

        /// <summary>
        /// Fractal value noise in world space, in the range -1..1. Deterministic:
        /// the same world position and seed always return the same number, on every
        /// machine and in any build order. That is what lets two chunks carve the
        /// same riverbed and agree exactly along the seam between them.
        /// </summary>
        public static float Ruggedness(float worldX, float worldZ, float featureMetres,
                                       int octaves, float persistence, int seed)
        {
            float frequency = 1f / Mathf.Max(0.5f, featureMetres);
            float amplitude = 1f;
            float sum = 0f;
            float norm = 0f;

            int n = Mathf.Clamp(octaves, 1, 8);
            float p = Mathf.Clamp(persistence, 0.05f, 0.95f);

            for (int o = 0; o < n; o++)
            {
                sum += ValueNoise(worldX * frequency, worldZ * frequency, seed + o * 9127) * amplitude;
                norm += amplitude;

                amplitude *= p;

                // Irrational-ish step rather than exactly 2, so octaves do not line
                // their grids up and produce visible axis-aligned banding.
                frequency *= 2.037f;
            }

            return norm > 1e-6f ? sum / norm : 0f;
        }

        /// <summary>
        /// Smooth value noise on an integer lattice, -1..1. Not Perlin: value noise
        /// is cheaper, has no gradient tables to seed, and at these amplitudes the
        /// difference is invisible under water.
        /// </summary>
        private static float ValueNoise(float x, float z, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int zi = Mathf.FloorToInt(z);

            float xf = x - xi;
            float zf = z - zi;

            // Quintic fade. Cubic leaves second-derivative discontinuities at the
            // lattice lines, which on a riverbed shows as a faint square grid.
            float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
            float v = zf * zf * zf * (zf * (zf * 6f - 15f) + 10f);

            float a = Hash(xi, zi, seed);
            float b = Hash(xi + 1, zi, seed);
            float c = Hash(xi, zi + 1, seed);
            float d = Hash(xi + 1, zi + 1, seed);

            float ab = a + (b - a) * u;
            float cd = c + (d - c) * u;

            return (ab + (cd - ab) * v) * 2f - 1f;
        }

        /// <summary>Integer hash to 0..1. Deterministic across platforms because it
        /// is pure integer math in unchecked 32-bit, with no float rounding.</summary>
        private static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + z * 668265263 + seed * 1442695040;
                h = (h ^ (h >> 13)) * 1274126177;
                h = h ^ (h >> 16);
                return (h & 0x7FFFFFFF) / 2147483647f;
            }
        }

        // =====================================================================
        //  FALLOFF
        // =====================================================================

        /// <summary>
        /// Blend weight for the outer edge of a carve, 1 inside and 0 past the
        /// feather distance. Smoothstep rather than linear: a linear falloff leaves
        /// a visible crease where the carve stops, because the surface gradient
        /// changes discontinuously there even though the height does not.
        /// </summary>
        public static float EdgeWeight(float distanceMetres, float startMetres, float featherMetres)
        {
            if (featherMetres <= 1e-4f) return distanceMetres <= startMetres ? 1f : 0f;

            float t = (distanceMetres - startMetres) / featherMetres;
            if (t <= 0f) return 1f;
            if (t >= 1f) return 0f;

            return 1f - Mathf.SmoothStep(0f, 1f, t);
        }
    }
}
