// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    ScatterFilter.cs
//  Purpose: Predicate exclusion for placement. Decides whether a world position
//           is allowed to carry an instance, and with what weight, from height
//           band, slope band, and mask value.
//
//  WHY WEIGHTS AND NOT BOOLEANS
//  ----------------------------
//  A boolean predicate gives a hard edge: trees stop dead along a contour line
//  at exactly 30 degrees of slope, which reads as a machine-cut band on a hillside.
//  Every filter here returns a 0-1 weight with an optional falloff width, so the
//  field thins out across the last few degrees instead of stopping. The solver
//  treats a fractional weight as an acceptance probability, so the thinning is
//  achieved by dropping points rather than by shrinking them.
//
//  WHY THE SLOPE MATRIX IS BUILT ONCE
//  ----------------------------------
//  Slope needs a 3x3 Sobel neighbourhood, so computing it per candidate would
//  re-read eight neighbours for every dart thrown - and a dense grass pass
//  throws hundreds of thousands. Bind() derives the whole slope field once
//  through MatrixOperations.SlopeAngles and the per-candidate test becomes a
//  bilinear sample.
//
//  Managed C# over Matrix/float[]. Safe to run on a worker Task: no Unity API
//  beyond Mathf and the Matrix container itself.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>How several active filters combine into one weight.</summary>
    public enum FilterCombine
    {
        /// <summary>Every filter must pass. Weights multiply. The default.</summary>
        All = 0,

        /// <summary>Any one filter passing is enough. Weights take the maximum.</summary>
        Any = 1
    }

    /// <summary>
    /// One "value must fall inside this band" test, with a soft edge.
    /// </summary>
    [Serializable]
    public struct RangeFilter
    {
        /// <summary>When false the filter is skipped entirely and contributes 1.</summary>
        public bool enabled;

        /// <summary>Lower bound of the accepted band, in the filtered value's own unit.</summary>
        public float min;

        /// <summary>Upper bound of the accepted band.</summary>
        public float max;

        /// <summary>
        /// Width of the fade outside each bound, same unit as min/max. 0 gives a
        /// hard cut; a slope filter of 0-30 degrees with a falloff of 8 thins
        /// from full density at 30 to nothing at 38.
        /// </summary>
        public float falloff;

        /// <summary>Swaps pass for fail, so the band becomes an exclusion zone.</summary>
        public bool invert;

        /// <summary>A disabled filter. Contributes a weight of 1 to everything.</summary>
        public static RangeFilter Off
        {
            get
            {
                RangeFilter f;
                f.enabled = false;
                f.min = 0f;
                f.max = 1f;
                f.falloff = 0f;
                f.invert = false;
                return f;
            }
        }

        /// <summary>An enabled band between min and max.</summary>
        public static RangeFilter Band(float min, float max, float falloff = 0f)
        {
            RangeFilter f;
            f.enabled = true;
            f.min = min;
            f.max = max;
            f.falloff = falloff;
            f.invert = false;
            return f;
        }

        /// <summary>
        /// Weight for one value: 1 fully inside the band, 0 fully outside, and a
        /// linear ramp across the falloff width at either edge.
        /// </summary>
        public float Evaluate(float value)
        {
            if (!enabled) return 1f;

            float w;

            if (falloff <= 1e-6f)
            {
                w = (value >= min && value <= max) ? 1f : 0f;
            }
            else
            {
                // Ramping in from below min and out past max separately, then
                // taking the smaller, handles a band narrower than its own
                // falloff without the two ramps cancelling to a flat pass.
                float rampIn = Mathf.InverseLerp(min - falloff, min, value);
                float rampOut = 1f - Mathf.InverseLerp(max, max + falloff, value);
                w = Mathf.Clamp01(Mathf.Min(rampIn, rampOut));
            }

            return invert ? 1f - w : w;
        }
    }
    /// <summary>
    /// A bound set of placement predicates. Build it, call <see cref="Bind"/>
    /// once per tile, then evaluate per candidate.
    /// </summary>
    public class ScatterFilter
    {
        /// <summary>Normalised height band, 0-1. Compared against the height matrix directly.</summary>
        public RangeFilter height = RangeFilter.Off;

        /// <summary>Slope band in degrees, 0 flat to 90 vertical.</summary>
        public RangeFilter slope = RangeFilter.Off;

        /// <summary>
        /// Mask value band, 0-1. Usually left as a Band(0.5, 1) style threshold,
        /// but the full range form lets a selector carve a mid-value ribbon.
        /// </summary>
        public RangeFilter mask = RangeFilter.Off;

        /// <summary>Compass aspect band in degrees, 0-360 clockwise from north.</summary>
        public RangeFilter aspect = RangeFilter.Off;

        /// <summary>How the active filters combine.</summary>
        public FilterCombine combine = FilterCombine.All;

        /// <summary>
        /// When true a mask of 0.6 thins placement to 60% rather than passing at
        /// full density because it cleared the band. This is what makes a biome
        /// blend feather into its neighbour instead of tiling up to a seam.
        /// </summary>
        public bool maskModulatesDensity = true;

        // Bound per tile. Null means the corresponding filter cannot run and is
        // treated as passing, which keeps an unwired mask input from silently
        // rejecting the whole field.
        private Matrix boundHeight;
        private Matrix boundMask;
        private Matrix boundSlope;
        private Matrix boundAspect;

        /// <summary>True once Bind has supplied at least a height matrix.</summary>
        public bool IsBound { get { return boundHeight != null; } }

        /// <summary>
        /// Binds the tile's fields and derives whatever the enabled filters need.
        /// Slope and aspect matrices are only built when their filter is on, so
        /// the common height-and-mask case does not pay for a Sobel pass.
        /// </summary>
        /// <param name="heightMatrix">Tile heights, normalised 0-1. Required.</param>
        /// <param name="maskMatrix">Optional mask. Null means "no mask wired".</param>
        public void Bind(Matrix heightMatrix, Matrix maskMatrix)
        {
            boundHeight = heightMatrix;
            boundMask = maskMatrix;
            boundSlope = null;
            boundAspect = null;

            if (heightMatrix == null) return;

            if (slope.enabled) boundSlope = MatrixOperations.SlopeAngles(heightMatrix);
            if (aspect.enabled) boundAspect = MatrixOperations.AspectAngles(heightMatrix);
        }

        /// <summary>
        /// Releases the derived matrices. A scatter node holding a filter across
        /// many tiles should call this between them so a full-resolution slope
        /// field is not pinned for the lifetime of the node.
        /// </summary>
        public void Unbind()
        {
            boundHeight = null;
            boundMask = null;
            boundSlope = null;
            boundAspect = null;
        }

        /// <summary>
        /// Acceptance weight at a world XZ position: 0 rejects, 1 accepts, in
        /// between is a probability the solver honours by dropping points.
        /// </summary>
        /// <remarks>
        /// This is the delegate body behind <c>PoissonDisc.Solve</c>'s accept
        /// parameter. It runs once per dart thrown, so it stays branch-light and
        /// allocation-free.
        /// </remarks>
        public float EvaluateWorld(float worldX, float worldZ)
        {
            if (boundHeight == null) return 1f;

            // One coordinate conversion serves every matrix: they share the tile's
            // rect and world placement, so the lattice position is the same.
            Vector2 p = boundHeight.WorldToPixel(worldX, worldZ);

            float hv = boundHeight.GetInterpolated(p.x, p.y);
            float mv = boundMask != null ? boundMask.GetInterpolated(p.x, p.y) : 1f;
            float sv = boundSlope != null ? boundSlope.GetInterpolated(p.x, p.y) : 0f;
            float av = boundAspect != null ? boundAspect.GetInterpolated(p.x, p.y) : 0f;

            return CombineWeights(hv, mv, sv, av);
        }

        /// <summary>
        /// The same weight as <see cref="EvaluateWorld"/>, addressed by flat array
        /// index instead of world position. Every bound field shares the tile's
        /// rect, so one index reaches all of them - which skips the world-to-pixel
        /// round trip and four bilinear samples for a caller that is already
        /// walking the lattice.
        /// </summary>
        public float EvaluatePixel(int index)
        {
            if (boundHeight == null || index < 0 || index >= boundHeight.arr.Length) return 1f;

            float hv = boundHeight.arr[index];
            float mv = SampleAligned(boundMask, index, 1f);
            float sv = SampleAligned(boundSlope, index, 0f);
            float av = SampleAligned(boundAspect, index, 0f);

            return CombineWeights(hv, mv, sv, av);
        }

        // A field bound at a different resolution to the height would alias
        // silently through a shared index, so a size mismatch falls back to the
        // neutral value rather than reading the wrong pixel.
        private static float SampleAligned(Matrix m, int index, float fallback)
        {
            if (m == null || index >= m.arr.Length) return fallback;
            return m.arr[index];
        }

        // Shared by both evaluation paths, so a mask built by CompositeFilter and
        // the same bands applied inside a scatter node cannot drift apart.
        private float CombineWeights(float hv, float mv, float slopeDeg, float aspectDeg)
        {
            float wHeight = height.Evaluate(hv);
            float wMask = mask.Evaluate(mv);

            float wSlope = 1f;
            if (boundSlope != null) wSlope = slope.Evaluate(slopeDeg);

            float wAspect = 1f;
            if (boundAspect != null) wAspect = aspect.Evaluate(aspectDeg);

            float w;

            if (combine == FilterCombine.Any)
            {
                // Only the enabled filters vote. Without this guard a disabled
                // filter's constant 1 would win the maximum and pass everything.
                w = 0f;
                bool anyEnabled = false;

                if (height.enabled) { w = Mathf.Max(w, wHeight); anyEnabled = true; }
                if (mask.enabled) { w = Mathf.Max(w, wMask); anyEnabled = true; }
                if (slope.enabled && boundSlope != null) { w = Mathf.Max(w, wSlope); anyEnabled = true; }
                if (aspect.enabled && boundAspect != null) { w = Mathf.Max(w, wAspect); anyEnabled = true; }

                if (!anyEnabled) w = 1f;
            }
            else
            {
                w = wHeight * wMask * wSlope * wAspect;
            }

            // Density modulation is applied after combining, and only when the
            // mask filter is not already doing the same job through its band.
            if (maskModulatesDensity && boundMask != null && !mask.enabled) w *= Mathf.Clamp01(mv);

            return Mathf.Clamp01(w);
        }

        /// <summary>
        /// Fills in the height, slope, and mask a candidate landed on. The solver
        /// only records position and weight; a scatter node needs the sampled
        /// values to place and orient the instance.
        /// </summary>
        public void Resolve(ref ScatterCandidate candidate)
        {
            if (boundHeight == null) return;

            Vector2 p = boundHeight.WorldToPixel(candidate.worldX, candidate.worldZ);

            candidate.height = boundHeight.GetInterpolated(p.x, p.y);
            candidate.maskValue = boundMask != null ? boundMask.GetInterpolated(p.x, p.y) : 1f;

            // Slope is wanted for the instance transform even when no slope
            // filter is enabled, so it is derived on demand here rather than
            // left at zero.
            if (boundSlope != null)
            {
                candidate.slope = boundSlope.GetInterpolated(p.x, p.y);
            }
            else
            {
                candidate.slope = SampleSlopeAt(boundHeight, p.x, p.y);
            }
        }

        /// <summary>
        /// Applies <see cref="Resolve"/> across a whole candidate list in place.
        /// </summary>
        public void ResolveAll(List<ScatterCandidate> candidates, Func<bool> isCancelled = null)
        {
            if (candidates == null || boundHeight == null) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (isCancelled != null && (i & 255) == 0 && isCancelled()) return;

                ScatterCandidate c = candidates[i];
                Resolve(ref c);
                candidates[i] = c;
            }
        }

        /// <summary>
        /// Single-point slope in degrees by central difference, for when the full
        /// slope field was never built. Cheaper than a Sobel pass for a handful
        /// of lookups, noisier for a whole field - which is why the bound path
        /// still uses Sobel.
        /// </summary>
        public static float SampleSlopeAt(Matrix heights, float px, float pz)
        {
            if (heights == null) return 0f;

            Vector2 ps = heights.PixelSize;
            if (ps.x <= 1e-9f || ps.y <= 1e-9f) return 0f;

            float hl = heights.GetInterpolated(px - 1f, pz);
            float hr = heights.GetInterpolated(px + 1f, pz);
            float hd = heights.GetInterpolated(px, pz - 1f);
            float hu = heights.GetInterpolated(px, pz + 1f);

            // Two pixels of run per difference, and the rise is normalised height
            // scaled back into metres.
            float dx = (hr - hl) * heights.worldHeight / (2f * ps.x);
            float dz = (hu - hd) * heights.worldHeight / (2f * ps.y);

            const float RadToDeg = 57.29577951308232f;
            return Mathf.Atan((float)System.Math.Sqrt(dx * dx + dz * dz)) * RadToDeg;
        }

        /// <summary>
        /// Convenience for the common scatter setup: a normalised height band, a
        /// slope band in degrees, and a mask threshold.
        /// </summary>
        public static ScatterFilter FromBands(
            float minHeight, float maxHeight,
            float minSlope, float maxSlope,
            float slopeFalloff = 5f)
        {
            ScatterFilter f = new ScatterFilter();

            // A band covering the full domain is left off rather than enabled and
            // always passing - it saves the Sobel pass for the slope case.
            if (minHeight > 0f || maxHeight < 1f)
                f.height = RangeFilter.Band(minHeight, maxHeight, 0.02f);

            if (minSlope > 0f || maxSlope < 90f)
                f.slope = RangeFilter.Band(minSlope, maxSlope, slopeFalloff);

            return f;
        }
    }
}
