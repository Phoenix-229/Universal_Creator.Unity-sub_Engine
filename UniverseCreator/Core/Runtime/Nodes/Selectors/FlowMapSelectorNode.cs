// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    FlowMapSelectorNode.cs
//  Purpose: Marks drainage channels by running the height-ordering pass from the
//           hydraulic solver and extracting the flow accumulation. High-accumulation
//           pixels mark the centre of valleys where water converges.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class FlowMapSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Flow Map"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Select drainage channels by flow accumulation."; } }

        public float intensity = 1f;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            // Run one hydraulic pass with zero erosion to harvest the flow field.
            Erosion.HydraulicSettings settings = new Erosion.HydraulicSettings
            {
                iterations = 1,
                durability = 1f,
                erosionAmount = 0f,
                sedimentAmount = 0f,
                fluidityIterations = 1,
                relax = 0f
            };

            Matrix flow = mask.Blank();
            Erosion.Hydraulic(src.Copy(), settings, null, flow, null,
                              stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);

            if (stop != null && stop.StopRequested) return;

            // Normalise and scale.
            float max = 0f;
            for (int i = 0; i < flow.arr.Length; i++)
            {
                if (flow.arr[i] > max) max = flow.arr[i];
            }

            float invMax = max > 0f ? 1f / max : 0f;
            float scale = Mathf.Max(0f, intensity);

            for (int i = 0; i < flow.arr.Length; i++)
            {
                mask.arr[i] = InterpolationMath.Clamp01(flow.arr[i] * invMax * scale);
            }
        }
    }
}
