// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RiverCourseSolver.cs
//  Purpose: Step 4 of the river rebuild - the longitudinal profile. Turns a drawn
//           course (its nodes carrying the ground height under them) into a bed
//           elevation that only ever descends from head to mouth. This is the
//           fix for the fought "river climbs uphill" bug, done at the root: an
//           uphill step is not representable in the output, so no later pass has
//           to detect and undo one.
//
//  THE MATH (grounded in RiverBuilder, Pasternack-Lab; adapted, not copied)
//  ------------------------------------------------------------------------
//  G-A1  Sinuosity-corrected slope.  A meander lengthens the path, so the true
//        along-channel slope is the valley slope scaled down by sinuosity:
//            S_channel = S_valley / sinuosity,   sinuosity = channelArc / valleyLength
//        A straightened reach is therefore steeper, a meandering one gentler -
//        the physically real relationship, not a tuned feel.
//  G-A2  Monotonic thalweg by cumulative descent.  Walking head -> mouth, each
//        node's bed is the LOWER of (a) the ground minus the channel depth, and
//        (b) the previous node's bed minus a guaranteed drop (slopeFloor * ds).
//        Because (b) is always strictly below the previous bed, the result can
//        only descend: where the ground rises into a hill, the bed holds the
//        descending line and cuts through it; where the ground falls, the bed
//        follows it down. An uphill bed step cannot be produced.
//
//  No invented grades: the slope floor is the reach's own valley slope (or a
//  gentle real-river minimum so a dead-flat reach still drains). Width/depth
//  derivation from discharge (Leopold) lives with step 5, where the along-course
//  discharge is actually accumulated; this file owns only the vertical profile.
//
//  Pure managed C#: arrays and Mathf, no Unity scene access, so it runs on a
//  worker and is exercised headless by the self test.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>The solved vertical profile of a river course: a bed that only descends.</summary>
    public struct SolvedRiverBed
    {
        /// <summary>Bed (thalweg) elevation in world metres, per course node. Monotonically non-increasing.</summary>
        public float[] bedElevationM;

        /// <summary>Cumulative plan (XZ) arc length in metres, per course node.</summary>
        public float[] arcM;

        /// <summary>True when the mouth's ground sits at or above the head's - the course was drawn uphill
        /// overall. The bed is still forced to descend; step 5 surfaces this to the author as a warning.</summary>
        public bool drawnUphill;

        /// <summary>Channel arc length divided by straight head-to-mouth distance (>= 1).</summary>
        public float sinuosity;

        /// <summary>The along-channel slope used, in metres per metre (S_valley / sinuosity, floored).</summary>
        public float channelSlope;

        /// <summary>True once the solve produced a usable 2+ node profile.</summary>
        public bool valid;
    }

    /// <summary>
    /// Solves a river course's longitudinal (vertical) profile: a monotonically
    /// descending bed, from the drawn nodes and the ground height under each.
    /// </summary>
    public static class RiverCourseSolver
    {
        // A gentle lowland river still falls on the order of a few tenths of a metre
        // per kilometre. This floor keeps the bed descending across a dead-flat reach
        // so water never ponds in a level channel, without asserting a grade steeper
        // than a real river would cut. Metres per metre.
        public const float AbsoluteMinSlope = 0.0002f;   // ~0.2 m per km

        /// <summary>
        /// Builds the descending bed profile. <paramref name="courseWorld"/> are the
        /// course nodes in world space, each Y holding the GROUND height under that
        /// node (the caller samples terrain first). <paramref name="depthM"/> is the
        /// per-node channel depth (0 or null entries fall back to
        /// <paramref name="defaultDepthM"/>). Returns an invalid result for a course
        /// of fewer than two nodes.
        /// </summary>
        public static SolvedRiverBed SolveBedProfile(IList<Vector3> courseWorld,
                                                     IList<float> depthM,
                                                     float defaultDepthM)
        {
            SolvedRiverBed r = new SolvedRiverBed();
            if (courseWorld == null || courseWorld.Count < 2) return r;

            int n = courseWorld.Count;
            float[] arc = new float[n];
            float[] bed = new float[n];

            // Plan (XZ) arc length: slope is drop over distance-across-the-ground, so
            // the vertical component must not inflate the run.
            arc[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                float dx = courseWorld[i].x - courseWorld[i - 1].x;
                float dz = courseWorld[i].z - courseWorld[i - 1].z;
                arc[i] = arc[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
            }

            float channelArc = arc[n - 1];

            float vdx = courseWorld[n - 1].x - courseWorld[0].x;
            float vdz = courseWorld[n - 1].z - courseWorld[0].z;
            float valleyLength = Mathf.Sqrt(vdx * vdx + vdz * vdz);

            float sinuosity = valleyLength > 1e-3f ? channelArc / valleyLength : 1f;
            if (sinuosity < 1f) sinuosity = 1f;   // numerical floor; a path cannot be shorter than its chord

            float headGround = courseWorld[0].y;
            float mouthGround = courseWorld[n - 1].y;
            float totalDrop = headGround - mouthGround;

            r.drawnUphill = totalDrop <= 0f;

            // G-A1: valley slope reduced by sinuosity, then floored so even a flat or
            // uphill-drawn reach still drains.
            float sValley = (valleyLength > 1e-3f && totalDrop > 0f) ? totalDrop / valleyLength : 0f;
            float sChannel = sValley / sinuosity;
            float slopeFloor = Mathf.Max(sChannel, AbsoluteMinSlope);

            // G-A2: cumulative descent. bed[i] is the lower of (ground - depth) and
            // (previous bed - guaranteed drop), so it can only ever go down.
            bed[0] = headGround - DepthAt(depthM, 0, defaultDepthM);
            for (int i = 1; i < n; i++)
            {
                float ds = arc[i] - arc[i - 1];
                float cap = courseWorld[i].y - DepthAt(depthM, i, defaultDepthM);
                float descend = bed[i - 1] - slopeFloor * ds;
                bed[i] = Mathf.Min(cap, descend);
            }

            r.bedElevationM = bed;
            r.arcM = arc;
            r.sinuosity = sinuosity;
            r.channelSlope = slopeFloor;
            r.valid = true;
            return r;
        }

        /// <summary>
        /// Signed per-node curvature of the course in plan (XZ), 1/metre-ish. Positive =
        /// the course turns LEFT of the flow direction at that node, negative = right,
        /// zero on a straight reach. This is what G-X1 keys the asymmetric cross-section
        /// on: the pool deepens on the OUTER bank of a bend and the inner bank shallows
        /// into a point-bar. Endpoints inherit their neighbour so the ends are not flat
        /// by accident.
        /// </summary>
        public static float[] SignedCurvature(IList<Vector3> courseWorld)
        {
            int n = courseWorld != null ? courseWorld.Count : 0;
            float[] cur = new float[n];
            if (n < 3) return cur;

            for (int i = 1; i < n - 1; i++)
            {
                Vector3 a = courseWorld[i - 1], b = courseWorld[i], c = courseWorld[i + 1];
                Vector2 t0 = new Vector2(b.x - a.x, b.z - a.z);
                Vector2 t1 = new Vector2(c.x - b.x, c.z - b.z);
                float l0 = t0.magnitude, l1 = t1.magnitude;
                if (l0 < 1e-4f || l1 < 1e-4f) continue;
                t0 /= l0; t1 /= l1;

                float cross = t0.x * t1.y - t0.y * t1.x;          // + = left turn (CCW in XZ)
                float dot = Mathf.Clamp(t0.x * t1.x + t0.y * t1.y, -1f, 1f);
                float angle = Mathf.Acos(dot);                    // unsigned turn magnitude
                float meanLen = 0.5f * (l0 + l1);
                cur[i] = (cross >= 0f ? 1f : -1f) * angle / Mathf.Max(1e-3f, meanLen);
            }

            cur[0] = cur[1];
            cur[n - 1] = cur[n - 2];
            return cur;
        }

        private static float DepthAt(IList<float> depthM, int i, float fallback)
        {
            float d = (depthM != null && i < depthM.Count) ? depthM[i] : 0f;
            return d > 0f ? d : Mathf.Max(0f, fallback);
        }

        // --- G-C1 / Guarantee C: hydraulic geometry (shape follows flow) --------------
        //
        //  Leopold & Maddock (1953): at-a-station and downstream, bankfull WIDTH grows
        //  as about the square root of discharge, w = a*Q^0.5; DEPTH grows more slowly,
        //  d proportional to w^0.8 (the Flow Depth Exponent the manual documents). The
        //  width is clamped to the real-world band (a stream floors at ~4 m, a trunk tops
        //  out ~80 m) that HydrologyGenerator.WidthAreaExponent's own band uses. Shields
        //  gives the grain-stability floor: a channel on a gentle slope must be at least
        //  this deep for its bed grains to sit still, h = tau*c*(s-1)*d50 / S.

        /// <summary>Leopold-Maddock bankfull-width coefficient, w = a*Q^0.5 (SI, metres & m^3/s).</summary>
        public const float WidthDischargeCoeff = 2.3f;
        public const float WidthDischargeExponent = 0.5f;

        /// <summary>Depth grows as width^0.8 (the documented Flow Depth Exponent); the
        /// coefficient seats a 10 m-wide channel at ~1 m deep (an alluvial w/d ~10).</summary>
        public const float DepthWidthExponent = 0.8f;
        public const float DepthWidthCoeff = 0.158f;     // 1 / 10^0.8

        // FIX 2026-10-19: the floor was 4 m, so the SMALLEST water in the world was already a
        // 4 m-wide channel and a headwater could not be a stream. The band now spans the whole
        // hierarchy: a trickle of a stream at the source up to a big river at the mouth.
        public const float MinChannelWidthM = 1.5f;
        public const float MaxChannelWidthM = 120f;

        /// <summary>The river size hierarchy, smallest to largest, by bankfull width.</summary>
        public enum RiverSizeClass { Stream = 0, SmallRiver = 1, River = 2, BigRiver = 3 }

        /// <summary>Upper bankfull width of each class, in metres: Stream under 4, Small river under
        /// 12, River under 40, anything wider is a Big river.</summary>
        public const float StreamMaxWidthM = 4f;
        public const float SmallRiverMaxWidthM = 12f;
        public const float RiverMaxWidthM = 40f;

        public static RiverSizeClass ClassifyWidth(float bankfullWidthM)
        {
            if (bankfullWidthM < StreamMaxWidthM) return RiverSizeClass.Stream;
            if (bankfullWidthM < SmallRiverMaxWidthM) return RiverSizeClass.SmallRiver;
            if (bankfullWidthM < RiverMaxWidthM) return RiverSizeClass.River;
            return RiverSizeClass.BigRiver;
        }

        /// <summary>Width of a freshly-sprung headwater stream, metres. Never zero.</summary>
        public const float HeadwaterWidthM = 0.3f;

        /// <summary>
        /// A natural channel for a river whose width and depth were not typed and which does not sit
        /// on a procedural trench: it STARTS as a stream (never zero width or depth) and GROWS
        /// downstream toward <paramref name="mouthWidthM"/>, by the same width-to-depth law the
        /// hydraulic geometry uses. <paramref name="t01"/> is the position along the course, 0 at the
        /// source and 1 at the mouth. Width never decreases downstream.
        /// </summary>
        public static void GrowthSection(float t01, float mouthWidthM, out float halfWidthM, out float depthM)
        {
            float t = Mathf.Clamp01(t01);
            float mouth = Mathf.Clamp(mouthWidthM, HeadwaterWidthM * 1.5f, MaxChannelWidthM);
            // Eased so the stream stays a stream for a while, then widens as tributaries would add flow.
            float g = Mathf.SmoothStep(0f, 1f, Mathf.Pow(t, 0.8f));
            float w = Mathf.Lerp(HeadwaterWidthM, mouth, g);
            halfWidthM = 0.5f * w;
            depthM = Mathf.Max(0.2f, DepthWidthCoeff * Mathf.Pow(w, DepthWidthExponent));
        }

        // Shields grain-stability floor constants.
        public const float ShieldsCritical = 0.047f;     // dimensionless critical shear for sand/gravel
        public const float SedimentSpecificGravity = 2.65f;
        public const float DefaultGrainD50M = 0.03f;      // coarse gravel bed, metres

        /// <summary>
        /// Channel half-width and depth from discharge and slope, by Leopold-Maddock with
        /// a Shields grain-stability depth floor. Q in m^3/s, slope in m/m. Returns a
        /// width clamped to the real-world band and a depth that is the larger of the
        /// Leopold regime depth and the (slope-capped) Shields floor.
        /// </summary>
        public static void HydraulicGeometry(float dischargeM3PerS, float slope,
                                             out float halfWidthM, out float depthM)
        {
            float q = Mathf.Max(0.01f, dischargeM3PerS);
            float w = WidthDischargeCoeff * Mathf.Pow(q, WidthDischargeExponent);
            w = Mathf.Clamp(w, MinChannelWidthM, MaxChannelWidthM);

            float dLeopold = DepthWidthCoeff * Mathf.Pow(w, DepthWidthExponent);

            // Shields floor, with the slope floored so a near-flat reach does not drive
            // the grain-stability depth to infinity; capped so it is a floor, not a runaway.
            float s = Mathf.Max(1e-4f, slope);
            float dShields = ShieldsCritical * (SedimentSpecificGravity - 1f) * DefaultGrainD50M / s;
            dShields = Mathf.Min(dShields, dLeopold * 2f);

            halfWidthM = 0.5f * w;
            depthM = Mathf.Max(dLeopold, dShields);
        }

        /// <summary>Fraction of the course length over which the mouth blends to a joined body.</summary>
        public const float EstuaryFraction = 0.15f;

        /// <summary>
        /// Step 7, the mouth blend: where the river joins a lake or ocean, the bed must
        /// not sit ABOVE the body's water surface (that was the §38 "raised bed at the
        /// mouth" seam). Over the last <see cref="EstuaryFraction"/> of the course the
        /// bed is pulled DOWN toward <paramref name="targetSurfaceY"/> by a Min-blend:
        /// each node's bed becomes the lower of what it was and the eased target, so the
        /// mouth meets the still water flush and the bed is never raised (descent and
        /// the deepening-only contract are both preserved). Mutates and returns bed.
        /// </summary>
        public static float[] BlendMouthToSurface(float[] bed, float[] arc,
                                                  float targetSurfaceY, float estuaryFraction)
        {
            if (bed == null || arc == null || bed.Length != arc.Length || bed.Length < 2) return bed;

            float mouthArc = arc[arc.Length - 1];
            float frac = Mathf.Clamp01(estuaryFraction);
            float blendStart = mouthArc * (1f - frac);
            float span = mouthArc - blendStart;
            if (span <= 1e-4f) return bed;

            for (int i = 0; i < bed.Length; i++)
            {
                if (arc[i] < blendStart) continue;
                float t = Mathf.Clamp01((arc[i] - blendStart) / span);
                float eased = Mathf.Lerp(bed[i], targetSurfaceY, t);
                // Min only: pull the bed down to meet the water, never raise it.
                bed[i] = Mathf.Min(bed[i], eased);
            }
            return bed;
        }
    }
}
