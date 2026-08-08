// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    LevelsNode.cs
//  Purpose: Photoshop-style levels adjustment. Input black/white points compress
//           the value range before gamma is applied, then the result is remapped
//           to the output black/white range.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class LevelsNode : ModifierNode
    {
        public override string NodeName { get { return "Levels"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Black point, white point, gamma, and output range."; } }

        public float inputBlack = 0f;
        public float inputWhite = 1f;
        public float gamma = 1f;
        public float outputBlack = 0f;
        public float outputWhite = 1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float inLo = Mathf.Clamp01(inputBlack);
            float inHi = Mathf.Clamp01(inputWhite);
            float g = Mathf.Max(0.01f, gamma);
            float outLo = Mathf.Clamp01(outputBlack);
            float outHi = Mathf.Clamp01(outputWhite);

            float inRange = Mathf.Max(0.0001f, inHi - inLo);
            float outRange = outHi - outLo;

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float v = m.arr[i];

                    // Compress input range.
                    v = InterpolationMath.Clamp01((v - inLo) / inRange);

                    // Apply gamma.
                    v = Mathf.Pow(v, 1f / g);

                    // Remap to output range.
                    m.arr[i] = outLo + v * outRange;
                }
            }
        }
    }
}
