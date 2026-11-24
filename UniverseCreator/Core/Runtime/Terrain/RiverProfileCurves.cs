// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverProfileCurves.cs   (RIV-3)
//  Purpose: Authored along-course profiles for a river: width, depth and velocity
//           as curves from headwater (t=0) to mouth (t=1), evaluated by the R4
//           per-knot rule  value = Base * curve(t) + Min.
//
//  WHY A BAKED LUT AND NOT AnimationCurve.Evaluate
//  -----------------------------------------------
//  THIS IS THE WHOLE REASON THIS TYPE EXISTS RATHER THAN THREE RAW AnimationCurve
//  FIELDS. `AnimationCurve.Evaluate` is NOT thread-safe - it mutates an internal
//  evaluation cache - and the river/terrain nodes are explicitly built to run on a
//  worker Task ("Safe on a worker Task", see RiverFillNode). Calling Evaluate from
//  that worker is an intermittent crash or a silently wrong value, which is the worst
//  class of bug: it passes every test on the main thread.
//
//  So the curves are sampled ONCE on the main thread into plain float[] tables by
//  `Bake`, and the worker reads only those tables through `TryEvaluate`. The LUT is
//  also what makes the result deterministic: two bakes of the same curve give the
//  same table, where Evaluate's cache makes it order-dependent.
//
//  INERT BY DEFAULT, AND NEVER WRONG WHEN UNBAKED
//  ----------------------------------------------
//  `enabled` is false on a fresh instance and the curves are flat at 1, so turning it
//  on without touching the curves reproduces a constant profile rather than jumping
//  to a new shape. `TryEvaluate` returns FALSE when disabled or not yet baked, and
//  every caller is required to keep its existing solved width/depth in that case. An
//  unbaked profile therefore degrades to today's behaviour instead of to zero width.
//
//  WHAT THIS DOES NOT DO
//  ---------------------
//  It does not compute physical depth. That already ships: Shields grain-stability
//  depth is inside `RiverCourseSolver.HydraulicGeometry`, which takes the larger of
//  the Leopold regime depth and the Shields floor. This type is the AUTHORED
//  alternative to that, not a second copy of it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>One station's authored profile values, in world units.</summary>
    public struct RiverProfileSample
    {
        /// <summary>Channel full width in metres (not half-width).</summary>
        public float widthM;

        /// <summary>Channel depth below the bank in metres.</summary>
        public float depthM;

        /// <summary>Along-channel flow speed, metres per second.</summary>
        public float velocityMPerS;
    }

    /// <summary>
    /// Authored width/depth/velocity curves along a river course, sampled to worker-safe
    /// lookup tables. Call <see cref="Bake"/> on the MAIN thread, then
    /// <see cref="TryEvaluate"/> from anywhere.
    /// </summary>
    [System.Serializable]
    public class RiverProfileCurves
    {
        /// <summary>Default LUT resolution: 64 stations is finer than any course is knotted.</summary>
        public const int DefaultSamples = 64;

        /// <summary>
        /// Opt-in. False means every caller keeps the width/depth it already solved, so
        /// adding this to a river changes nothing until the author asks for it.
        /// </summary>
        public bool enabled = false;

        // Flat-at-1 defaults: with Base and Min below, an enabled-but-unedited profile is a
        // CONSTANT river, not a surprise shape. The default has to be the off position.
        public AnimationCurve widthCurve = Flat();
        public AnimationCurve depthCurve = Flat();
        public AnimationCurve velocityCurve = Flat();

        /// <summary>Width scale in metres: width = widthBaseM * curve(t) + widthMinM.</summary>
        public float widthBaseM = 8f;

        /// <summary>Width floor in metres, added after the curve (the R4 Base*curve+Min rule).</summary>
        public float widthMinM = 4f;

        public float depthBaseM = 1.2f;
        public float depthMinM = 0.3f;

        public float velocityBaseMPerS = 1.0f;
        public float velocityMinMPerS = 0.1f;

        // Baked tables. NonSerialized: a LUT is derived data, and persisting it would let a
        // stale table outlive an edited curve.
        [System.NonSerialized] private float[] widthLut;
        [System.NonSerialized] private float[] depthLut;
        [System.NonSerialized] private float[] velocityLut;

        /// <summary>True once <see cref="Bake"/> has produced usable tables.</summary>
        public bool IsBaked
        {
            get
            {
                return widthLut != null && depthLut != null && velocityLut != null
                       && widthLut.Length >= 2;
            }
        }

        private static AnimationCurve Flat()
        {
            return new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));
        }

        /// <summary>
        /// Samples the three curves into lookup tables. MAIN THREAD ONLY - this is the one
        /// method that touches AnimationCurve. Safe to call repeatedly; the last call wins.
        /// </summary>
        public void Bake(int samples)
        {
            samples = Mathf.Clamp(samples, 2, 512);
            widthLut = SampleCurve(widthCurve, samples);
            depthLut = SampleCurve(depthCurve, samples);
            velocityLut = SampleCurve(velocityCurve, samples);
        }

        /// <summary>Bakes at the default resolution. MAIN THREAD ONLY.</summary>
        public void Bake() { Bake(DefaultSamples); }

        /// <summary>Drops the tables, so a stale LUT can never be read after a curve edit.</summary>
        public void Invalidate() { widthLut = null; depthLut = null; velocityLut = null; }

        private static float[] SampleCurve(AnimationCurve c, int samples)
        {
            float[] lut = new float[samples];
            float last = samples - 1;
            for (int i = 0; i < samples; i++)
            {
                float t = i / last;
                lut[i] = c != null ? c.Evaluate(t) : 1f;   // a missing curve is a flat 1, not a 0
            }
            return lut;
        }

        /// <summary>
        /// The authored profile at normalised along-course position <paramref name="t"/>
        /// (0 = headwater, 1 = mouth). Worker-safe: reads only the baked tables.
        /// Returns FALSE when disabled or unbaked, and the caller MUST then keep the
        /// width/depth it already had.
        /// </summary>
        public bool TryEvaluate(float t, out RiverProfileSample sample)
        {
            sample = new RiverProfileSample();
            if (!enabled || !IsBaked) return false;

            float u = Mathf.Clamp01(t);

            float w = widthBaseM * Lookup(widthLut, u) + widthMinM;
            float d = depthBaseM * Lookup(depthLut, u) + depthMinM;
            float v = velocityBaseMPerS * Lookup(velocityLut, u) + velocityMinMPerS;

            // Clamped to the band RiverCourseSolver already documents, so an authored curve
            // cannot ask for a river narrower or wider than the hydraulic path allows -
            // reusing its constants rather than inventing a second set.
            sample.widthM = Mathf.Clamp(w, RiverCourseSolver.MinChannelWidthM,
                                           RiverCourseSolver.MaxChannelWidthM);
            sample.depthM = Mathf.Max(0f, d);
            sample.velocityMPerS = Mathf.Max(0f, v);
            return true;
        }

        /// <summary>Linear interpolation into a baked table at u in 0..1.</summary>
        private static float Lookup(float[] lut, float u)
        {
            int n = lut.Length;
            float f = u * (n - 1);
            int i0 = (int)f;
            if (i0 >= n - 1) return lut[n - 1];
            float frac = f - i0;
            return lut[i0] + (lut[i0 + 1] - lut[i0]) * frac;
        }
    }
}
