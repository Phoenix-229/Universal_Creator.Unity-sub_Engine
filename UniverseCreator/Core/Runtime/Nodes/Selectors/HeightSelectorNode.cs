// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HeightSelectorNode.cs
//  Purpose: Marks pixels in a height range. Outputs a mask where 1 means the
//           source fell between the specified bounds.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>Which space the height bounds on a selector are expressed in.</summary>
    public enum HeightUnits
    {
        /// <summary>Normalised 0-1 map space, matching the raw field values.</summary>
        Map = 0,
        /// <summary>World metres, divided down by the world's height range.</summary>
        World = 1
    }

    [System.Serializable]
    public class HeightSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Height"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Select by height range."; } }

        [Tooltip("Map = normalised 0-1. World = metres, converted using the world height.")]
        public HeightUnits units = HeightUnits.Map;

        public float minHeight = 0.3f;
        public float maxHeight = 0.7f;
        public float falloff;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            // The field itself is always normalised, so World bounds are divided
            // down into map space rather than the field being scaled up - that
            // keeps this one multiply out of the per-pixel loop.
            float toMap = 1f;
            if (units == HeightUnits.World && tile != null && tile.worldHeight > 0.0001f)
                toMap = 1f / tile.worldHeight;

            float lo = Mathf.Min(minHeight, maxHeight) * toMap;
            float hi = Mathf.Max(minHeight, maxHeight) * toMap;
            float edge = Mathf.Clamp01(falloff * toMap);

            for (int z = 0; z < src.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * src.rect.size.x;
                for (int x = 0; x < src.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float h = src.arr[i];

                    float v = InterpolationMath.SelectRange(h, lo, lo + edge, hi - edge, hi);
                    mask.arr[i] = InterpolationMath.Clamp01(v);
                }
            }
        }
    }
}
