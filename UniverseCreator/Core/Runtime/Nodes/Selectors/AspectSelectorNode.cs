// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    AspectSelectorNode.cs
//  Purpose: Marks pixels by compass facing. North-facing slopes produce 1 when
//           centered on 0°, south-facing slopes produce 1 when centered on 180°.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class AspectSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Aspect"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Select by compass direction the slope faces."; } }

        /// <summary>Compass bearing in degrees. 0 = north, 90 = east, etc.</summary>
        public float direction;

        /// <summary>Angular width of the selection cone.</summary>
        public float spread = 90f;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            Matrix aspects = MatrixOperations.AspectAngles(src);
            if (stop != null && stop.StopRequested) return;

            float center = direction % 360f;
            if (center < 0f) center += 360f;
            float halfSpread = Mathf.Max(0.01f, spread) * 0.5f;

            for (int i = 0; i < aspects.arr.Length; i++)
            {
                float a = aspects.arr[i];
                float delta = Mathf.Abs(Mathf.DeltaAngle(a, center));
                float v = 1f - Mathf.Clamp01(delta / halfSpread);
                mask.arr[i] = v;
            }
        }
    }
}
