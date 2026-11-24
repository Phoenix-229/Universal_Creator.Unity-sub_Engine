// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    SlopeToDensityNode.cs
//  Purpose: N4 of the river/vegetation suite. Turns the ground's STEEPNESS into a
//           continuous density channel, 0..1 - the knissen "Normal-to-Density /
//           Slope Filter -> Threshold" idea, written fresh. A bank, a bed or a
//           hillside then carries cover in proportion to how well its slope suits a
//           thing, instead of a hard pass/fail band that stops dead along a contour.
//
//  WHY THIS AND NOT JUST THE SCATTER SLOPE BAND
//  --------------------------------------------
//  ScatterNodeBase already rejects by a slope band, but that decision is private to
//  one scatter node. Exposing slope-as-density as its own node means the SAME channel
//  can drive several scatter nodes, feather a texture weight, or be combined with a
//  water or biome mask before anything is placed - the composability an Unreal PCG
//  graph gets from a Normal-to-Density node feeding a density attribute.
//
//  Slope is read from MatrixOperations.SlopeAngles (one Sobel pass), and the slope ->
//  weight curve is RangeFilter - the exact same band maths, GradedBand included, that
//  the scatter solver uses - so a slope of 30 degrees means the same thing to a
//  density here as it does to a rejection there. One curve, not two that drift.
//
//  Deterministic. Managed C# over Matrix/float[]. Safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Maps terrain slope to a 0..1 density channel through a (optionally graded) band.
    /// Flat-favouring by default (dense on gentle ground, thinning as it steepens);
    /// invert to favour steep ground, e.g. scree on a cliff.
    /// </summary>
    [System.Serializable]
    public class SlopeToDensityNode : Generator
    {
        public override string NodeName { get { return "Slope To Density"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }

        public override string Tooltip
        {
            get
            {
                return "Converts ground steepness into a 0..1 density channel for scatter " +
                       "or texture. Dense on gentle slope, thinning as it steepens; invert " +
                       "for steep-favouring cover like scree.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Density", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Lower slope bound of the accepted band, in degrees.</summary>
        public float minSlopeDeg = 0f;

        /// <summary>Upper slope bound of the accepted band, in degrees.</summary>
        public float maxSlopeDeg = 25f;

        /// <summary>Linear fade width past each bound, in degrees. Ignored when graded.</summary>
        public float falloffDeg = 8f;

        /// <summary>
        /// Grade the band as a continuous bell peaking at the band CENTRE instead of a
        /// flat plateau. Off by default so the default is flat-favouring (full from 0 to
        /// the slope limit, fading past it); turn it on when cover wants a mid-slope
        /// OPTIMUM rather than "flatter is always better" - a bank that likes a gentle
        /// incline, say, not dead-flat ground.
        /// </summary>
        public bool graded = false;

        /// <summary>Flip the band into an exclusion, so STEEP ground scores high.</summary>
        public bool invert = false;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix slope = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();
            int n = dst.arr.Length;

            RangeFilter band = BuildBand(minSlopeDeg, maxSlopeDeg, falloffDeg, graded, invert);

            for (int i = 0; i < n && i < slope.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return dst;
                dst.arr[i] = Mathf.Clamp01(band.Evaluate(slope.arr[i]));
            }

            return dst;
        }

        /// <summary>
        /// The slope band this node evaluates, as a pure builder so the mapping can be
        /// self-tested without a Sobel pass or the graph: a flat site should score high
        /// under a low, flat-favouring band and a steep one should score low, and invert
        /// must swap the two. A graded band is a bell; otherwise a plateau with a linear
        /// edge. This reuses RangeFilter so the curve is the scatter solver's own.
        /// </summary>
        internal static RangeFilter BuildBand(float minDeg, float maxDeg, float falloff,
                                              bool graded, bool invert)
        {
            RangeFilter band = graded
                ? RangeFilter.GradedBand(minDeg, maxDeg)
                : RangeFilter.Band(minDeg, maxDeg, Mathf.Max(0f, falloff));
            band.invert = invert;
            return band;
        }
    }
}
