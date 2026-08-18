// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    CompositeFilterNode.cs
//  Purpose: Bake the whole predicate stack into a mask, once, instead of
//           letting a scatter pass re-evaluate the same bands per dart. Height,
//           slope, aspect and an optional wired mask combine into a 0-1 field
//           that downstream scatter nodes consume as density.
//
//  THE FEATHER, NOT THE SCISSORS
//  -----------------------------
//  Each band is a RangeFilter with a falloff width: full weight inside the band,
//  zero past the falloff, and a linear ramp across it. All mode multiplies the
//  weights (a pixel outside any one band is out), Any mode takes the maximum
//  over the enabled bands (the union of several habitats, e.g. "low meadow OR
//  high ridge"). A boolean predicate would give the machine-cut contour the
//  filters were written to avoid, so the mask carries fractions - and the
//  scatter solver honours them as acceptance probabilities.
//
//  WHY IT REUSES SCATTERFILTER
//  ---------------------------
//  The scatter nodes own their own ScatterFilter instance; this node owns
//  another, binds it to the same fields, and evaluates one weight per pixel
//  instead of per dart. Sharing the struct means a mask baked here and the same
//  bands set on a scatter node cannot drift apart - there is exactly one
//  Evaluate implementation, and both paths feed it the same inputs.
//
//  Managed C# over float[]. Safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class CompositeFilterNode : SelectorNode
    {
        public override string NodeName { get { return "Composite Filter"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Height, slope, aspect and mask bands combined into one density mask."; } }

        public override Port[] GetInputs() { return SharedPorts.HeightMaskIn; }

        /// <summary>Normalised height band, 0-1 map space.</summary>
        public RangeFilter height = RangeFilter.Off;

        /// <summary>Slope band in degrees, 0 flat to 90 vertical.</summary>
        public RangeFilter slope = RangeFilter.Off;

        /// <summary>Compass aspect band in degrees, 0-360 clockwise from north.</summary>
        public RangeFilter aspect = RangeFilter.Off;

        /// <summary>Band on the wired mask input. Null input passes at full weight.</summary>
        public RangeFilter mask = RangeFilter.Off;

        /// <summary>All = every enabled band (multiply). Any = at least one (max).</summary>
        public FilterCombine combine = FilterCombine.All;

        protected override void Select(Matrix src, Matrix maskOut, TileData tile, StopToken stop)
        {
            ScatterFilter filter = new ScatterFilter();
            filter.height = height;
            filter.slope = slope;
            filter.aspect = aspect;
            filter.mask = mask;
            filter.combine = combine;

            // The wired mask input is optional; pull it through the tile's
            // evaluator the same way GetInputMatrix does for port 1. A missing
            // connection leaves the mask filter passing at full weight.
            Matrix wired = tile != null ? GetInputMatrix(tile, 1, stop) : null;
            filter.Bind(src, wired);

            // The slope and aspect matrices Bind derives are built from the same
            // rect as src, so EvaluatePixel's shared index reaches all of them.
            for (int i = 0; i < maskOut.arr.Length; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) return;
                maskOut.arr[i] = filter.EvaluatePixel(i);
            }
        }
    }
}
