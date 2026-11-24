// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RiverProfile.cs
//  Purpose: The per-control-point data a rebuilt river carries ALONGSIDE its
//           course geometry: how wide the channel is, how deep, how much water
//           it carries, and how fast. The course itself is an ordinary Polyline
//           (world-space points); this is the hydraulic payload sampled along it.
//
//  WHY A SEPARATE CLASS AND NOT MORE CHANNELS ON Polyline
//  ------------------------------------------------------
//  Polyline carries exactly one scalar `weights` channel, and every point-count-
//  changing operation in SplineOperations (Resample, TessellateCatmullRom,
//  Subdivide, Simplify, ...) hand-carries that one channel through a Lerp. Rivers
//  need five. Threading five more channels through all ~10 of those methods would
//  bloat a library that shorelines, ridge crests and road centrelines also use,
//  and risk breaking them for a river-only need. So the river payload lives here,
//  keyed by index to the river's control course, and is sampled by arc length
//  through the SAME arc table (Polyline.BuildArcTable) the rest of the toolkit
//  uses - no geometry logic is duplicated.
//
//  THE INHERIT CONVENTION (matches UCWaterBody.TrimNodeSeries)
//  -----------------------------------------------------------
//  Each channel list is either empty (that channel is unauthored - the solver in
//  step 4 derives it entirely) or exactly as long as the course. A value of 0 or
//  less at a node means "not set here, inherit the last authored value" - the
//  identical rule UCWaterBody already uses for a lake's per-ring node depths, so
//  adding a course point means "inherit" rather than "suddenly 0 metres wide".
//  Width, depth, discharge and velocity are all strictly positive for a real
//  river, so 0 is free to mean "unset" without losing a legal value.
//
//  Real-world units throughout. No random numbers: an unauthored channel is not
//  guessed here, it is left for the step-4 solver to derive from drainage
//  (discharge), Leopold hydraulic geometry (width/depth) and Manning (velocity).
//
//  Plain managed C#: Lists and floats, no NativeArray and no Unity API beyond
//  Mathf, so a baked profile is safe to sample from a background Task - the same
//  promise Polyline and HydroField make.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// One hydraulic reading along a river course: the channel's half-width and
    /// depth in metres, its discharge and velocity. A value &lt;= 0 means the
    /// channel was unauthored at this point and the solver should derive it.
    /// </summary>
    public struct RiverSample
    {
        /// <summary>Half the channel top-width in metres (UE's RiverWidth is also a half-width).</summary>
        public float halfWidthM;

        /// <summary>Channel depth in metres, waterline to thalweg.</summary>
        public float depthM;

        /// <summary>Discharge Q in cubic metres per second. &lt;= 0 means "derive from drainage".</summary>
        public float dischargeM3PerS;

        /// <summary>Mean flow velocity in metres per second. &lt;= 0 means "derive from Manning".</summary>
        public float velocityMps;
    }

    /// <summary>
    /// Authored per-control-point river payload, serialized on the river body and
    /// edited by the step-3 authoring UX. Pure storage plus the <see cref="Bake"/>
    /// that turns it into an immutable, arc-length sampler for the solver and carve.
    /// </summary>
    [System.Serializable]
    public class RiverProfile
    {
        // Each list is empty (channel unauthored - derive it) or exactly course
        // length. 0 or less at a node = "inherit the previous authored value".
        [Tooltip("Half channel top-width in metres, per course node. 0 inherits the previous node.")]
        public List<float> halfWidthM = new List<float>();

        [Tooltip("Channel depth in metres, per course node. 0 inherits the previous node.")]
        public List<float> depthM = new List<float>();

        [Tooltip("Discharge Q (m3/s) per course node. 0 lets the solver derive it from drainage.")]
        public List<float> dischargeM3PerS = new List<float>();

        [Tooltip("Mean velocity (m/s) per course node. 0 lets the solver derive it from Manning.")]
        public List<float> velocityMps = new List<float>();

        /// <summary>
        /// Keeps every channel the same length as the course, exactly as
        /// UCWaterBody.TrimNodeSeries does for lake ring depths: trailing nodes are
        /// dropped and new nodes are added as 0 ("inherit") rather than a hard value.
        /// A channel that is empty stays empty - unauthored means "derive", and
        /// padding it with zeros here would wrongly promote it to "authored, all
        /// inherit from nothing".
        /// </summary>
        public void Trim(Polyline course)
        {
            if (course == null) return;
            int want = course.Count;

            TrimOne(halfWidthM, want);
            TrimOne(depthM, want);
            TrimOne(dischargeM3PerS, want);
            TrimOne(velocityMps, want);
        }

        private static void TrimOne(List<float> series, int want)
        {
            if (series == null || series.Count == 0) return;   // empty = unauthored, leave it
            while (series.Count > want) series.RemoveAt(series.Count - 1);
            while (series.Count < want) series.Add(0f);
        }

        /// <summary>
        /// Freezes the authored channels against a course into an immutable sampler.
        /// Forward-fills each authored channel once (so sampling is a cheap lerp, not
        /// an O(n) scan per query) and precomputes the course arc table. The course
        /// is copied defensively so a later edit cannot mutate a baked profile a
        /// worker is reading. Returns null when the course cannot carry a river.
        /// </summary>
        public BakedRiverProfile Bake(Polyline course)
        {
            if (course == null || course.Count < 2) return null;

            int n = course.Count;
            float[] arc = course.BuildArcTable();

            return new BakedRiverProfile(
                arc,
                ForwardFill(halfWidthM, n),
                ForwardFill(depthM, n),
                ForwardFill(dischargeM3PerS, n),
                ForwardFill(velocityMps, n));
        }

        /// <summary>
        /// Expands an authored channel to course length with the inherit rule: a
        /// node at 0 or less takes the previous authored value; leading unset nodes
        /// take the first authored value. Returns null when the channel is empty or
        /// has no authored (&gt; 0) node at all - the caller then derives it rather
        /// than treating a column of zeros as real data.
        /// </summary>
        internal static float[] ForwardFill(List<float> channel, int count)
        {
            if (channel == null || channel.Count == 0 || count <= 0) return null;

            // First authored value seeds the leading run so an unset head inherits
            // forward from the first real node instead of reading 0.
            float seed = 0f;
            bool hasAny = false;
            for (int i = 0; i < channel.Count && i < count; i++)
            {
                if (channel[i] > 0f) { seed = channel[i]; hasAny = true; break; }
            }
            if (!hasAny) return null;   // nothing authored - leave it for the solver

            float[] filled = new float[count];
            float last = seed;
            for (int i = 0; i < count; i++)
            {
                float v = i < channel.Count ? channel[i] : 0f;
                if (v > 0f) last = v;
                filled[i] = last;
            }
            return filled;
        }
    }

    /// <summary>
    /// Immutable, forward-filled river payload with a precomputed arc table, built
    /// by <see cref="RiverProfile.Bake"/>. Any channel may be null, meaning it was
    /// unauthored and the sampled value comes back as 0 ("derive"). Safe to sample
    /// from many worker threads at once: nothing here is ever written after
    /// construction.
    /// </summary>
    public sealed class BakedRiverProfile
    {
        private readonly float[] arc;         // cumulative distance per course node
        private readonly float[] halfWidth;   // may be null (derive)
        private readonly float[] depth;       // may be null (derive)
        private readonly float[] discharge;   // may be null (derive)
        private readonly float[] velocity;    // may be null (derive)

        internal BakedRiverProfile(float[] arc, float[] halfWidth, float[] depth,
                                   float[] discharge, float[] velocity)
        {
            this.arc = arc;
            this.halfWidth = halfWidth;
            this.depth = depth;
            this.discharge = discharge;
            this.velocity = velocity;
        }

        /// <summary>Course length in metres (the last arc entry), 0 if degenerate.</summary>
        public float TotalLengthM { get { return (arc != null && arc.Length > 0) ? arc[arc.Length - 1] : 0f; } }

        public bool HasWidth     { get { return halfWidth != null; } }
        public bool HasDepth     { get { return depth != null; } }
        public bool HasDischarge { get { return discharge != null; } }
        public bool HasVelocity  { get { return velocity != null; } }

        /// <summary>
        /// Reads every authored channel at a distance <paramref name="arcS"/> along
        /// the course, in metres, clamped to the ends. An unauthored channel reads
        /// back 0 so the solver can tell "derive this" from a real authored value.
        /// </summary>
        public RiverSample SampleAtArc(float arcS)
        {
            RiverSample s = new RiverSample();
            if (arc == null || arc.Length < 2) return s;

            float total = arc[arc.Length - 1];
            float d = Mathf.Clamp(arcS, 0f, total);

            // Segment [i, i+1] whose arc span contains d. Binary search the arc table,
            // which is monotonically increasing by construction.
            int lo = 0, hi = arc.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (arc[mid] <= d) lo = mid; else hi = mid;
            }

            float span = arc[hi] - arc[lo];
            float t = span > 1e-6f ? (d - arc[lo]) / span : 0f;

            s.halfWidthM      = Lerp(halfWidth, lo, hi, t);
            s.depthM          = Lerp(depth, lo, hi, t);
            s.dischargeM3PerS = Lerp(discharge, lo, hi, t);
            s.velocityMps     = Lerp(velocity, lo, hi, t);
            return s;
        }

        // A null channel reads 0 ("derive"); indices are already clamped by the
        // arc search, so no bounds guard is needed beyond the null check.
        private static float Lerp(float[] ch, int lo, int hi, float t)
        {
            if (ch == null) return 0f;
            return Mathf.Lerp(ch[lo], ch[hi], t);
        }
    }
}
