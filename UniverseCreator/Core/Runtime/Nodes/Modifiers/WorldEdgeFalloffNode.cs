// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    WorldEdgeFalloffNode.cs
//  Purpose: Flattens the outer band of the world down to sea level so the
//           continent ends at 0 altitude, like a real coastline, instead of a
//           cliff of hills sliced off at the world boundary.
//
//  THE 500m RULE
//  -------------
//  Everything within `falloffMeters` of the world edge is blended toward
//  `edgeHeight`. The blend is measured in WORLD METRES against the full world
//  bounds (TileData.fullWorldPos/fullWorldSize), not against the chunk, so a chunk
//  sitting in the middle of the continent is untouched and only the true border
//  chunks fall away. That also means the band stays 500m wide no matter how large
//  the world grows or how it is sliced - the maths is world-size relative.
//
//  The curve is smoothstep, so hills near the border ease down gently rather than
//  snapping flat at the band edge.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class WorldEdgeFalloffNode : ModifierNode
    {
        public override string NodeName { get { return "World Edge Falloff"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip
        {
            get { return "Fall to sea level within N metres of the world edge, like a real continent."; }
        }

        [Tooltip("Width of the falloff band in metres, measured inward from the world edge.")]
        public float falloffMeters = 500f;

        [Tooltip("Height the very edge settles to. 0 is sea level.")]
        public float edgeHeight = 0f;

        [Tooltip("Shapes the descent. 1 is a plain smoothstep; higher keeps land " +
                 "high for longer then drops more steeply near the shore.")]
        public float curvePower = 1f;

        [Tooltip("Apply the falloff on the X axis (east and west edges).")]
        public bool falloffX = true;

        [Tooltip("Apply the falloff on the Z axis (north and south edges).")]
        public bool falloffZ = true;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float band = Mathf.Max(0.0001f, falloffMeters);
            float power = Mathf.Max(0.0001f, curvePower);

            // World bounds of the whole continent. TileData defaults these to the
            // chunk's own bounds, so this still behaves correctly for a single
            // whole-world tile with no chunking.
            float worldMinX = tile.fullWorldPos.x;
            float worldMinZ = tile.fullWorldPos.z;
            float worldMaxX = worldMinX + tile.fullWorldSize.x;
            float worldMaxZ = worldMinZ + tile.fullWorldSize.z;

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                // Distance to the nearer of the two Z edges.
                float dz = falloffZ ? Mathf.Min(wz - worldMinZ, worldMaxZ - wz) : float.MaxValue;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;
                    float dx = falloffX ? Mathf.Min(wx - worldMinX, worldMaxX - wx) : float.MaxValue;

                    // Nearest world edge in any direction. Corners fall off on both
                    // axes at once, which is what makes them round rather than square.
                    float edgeDistance = Mathf.Min(dx, dz);

                    // Outside the band: untouched interior. Saves the curve maths on
                    // the overwhelming majority of a large world's pixels.
                    if (edgeDistance >= band) continue;

                    float t = Mathf.Clamp01(edgeDistance / band);
                    float shaped = Mathf.SmoothStep(0f, 1f, t);
                    if (!Mathf.Approximately(power, 1f)) shaped = Mathf.Pow(shaped, power);

                    int i = rowStart + x;
                    m.arr[i] = Mathf.Lerp(edgeHeight, m.arr[i], shaped);
                }
            }
        }
    }
}
