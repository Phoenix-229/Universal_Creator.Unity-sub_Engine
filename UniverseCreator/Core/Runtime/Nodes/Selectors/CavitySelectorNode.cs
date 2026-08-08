// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CavitySelectorNode.cs
//  Purpose: Marks cracks and recesses using the two-pass chamfer distance
//           transform in MatrixOperations.Cavity. Dark pockets where multiple
//           faces converge produce 1.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class CavitySelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Cavity"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Select cracks and recesses. Distance-transform cavity."; } }

        public float intensity = 1f;
        public bool normalise = true;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            Matrix cavity = MatrixOperations.Cavity(src, normalise);
            if (stop != null && stop.StopRequested) return;

            float scale = Mathf.Max(0f, intensity);

            for (int i = 0; i < cavity.arr.Length; i++)
            {
                mask.arr[i] = InterpolationMath.Clamp01(cavity.arr[i] * scale);
            }
        }
    }
}
