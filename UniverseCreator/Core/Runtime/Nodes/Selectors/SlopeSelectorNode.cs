// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SlopeSelectorNode.cs
//  Purpose: Marks pixels by slope angle. Vertical faces produce 1, flat ground 0.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class SlopeSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Slope"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Select by slope angle (degrees)."; } }

        public float minAngle;
        public float maxAngle = 45f;
        public float falloff = 5f;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            Matrix slopes = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return;

            float lo = Mathf.Min(minAngle, maxAngle);
            float hi = Mathf.Max(minAngle, maxAngle);
            float edge = Mathf.Max(0.01f, falloff);

            for (int i = 0; i < slopes.arr.Length; i++)
            {
                float s = slopes.arr[i];
                float v = InterpolationMath.SelectRange(s, lo, lo + edge, hi - edge, hi);
                mask.arr[i] = InterpolationMath.Clamp01(v);
            }
        }
    }
}
