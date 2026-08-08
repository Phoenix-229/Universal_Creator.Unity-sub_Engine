// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ParallaxNode.cs
//  Purpose: Offset mapping approximation. Displaces the sample point along the
//           view direction scaled by height, creating a relief illusion. Used
//           in texture generation rather than runtime shaders, so "view" is a
//           parameter rather than computed per camera.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class ParallaxNode : ModifierNode
    {
        public override string NodeName { get { return "Parallax"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Offset mapping illusion. Displaces samples by height."; } }

        public Vector2 viewDirection = new Vector2(0.5f, 0.5f);
        public float strength = 0.1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            if (strength < 0.001f) return;

            Vector2 dir = viewDirection.normalized;
            if (dir.sqrMagnitude < 0.01f) return;

            float[] original = new float[m.arr.Length];
            System.Array.Copy(m.arr, original, m.arr.Length);

            int w = m.rect.size.x;
            int h = m.rect.size.z;

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = rowStart + x;
                    float heightHere = original[i];

                    // Displace sample point opposite to view direction, scaled by height.
                    float sx = x - dir.x * heightHere * strength * w;
                    float sz = z - dir.y * heightHere * strength * h;

                    int ix = Mathf.FloorToInt(sx);
                    int iz = Mathf.FloorToInt(sz);

                    if (ix < 0 || ix >= w - 1 || iz < 0 || iz >= h - 1)
                    {
                        m.arr[i] = heightHere;
                        continue;
                    }

                    float tx = sx - ix;
                    float tz = sz - iz;

                    int i0 = iz * w + ix;
                    int i1 = i0 + 1;
                    int i2 = i0 + w;
                    int i3 = i2 + 1;

                    float a = original[i0];
                    float b = original[i1];
                    float c = original[i2];
                    float d = original[i3];

                    float top = a + (b - a) * tx;
                    float bot = c + (d - c) * tx;
                    m.arr[i] = top + (bot - top) * tz;
                }
            }
        }
    }
}
